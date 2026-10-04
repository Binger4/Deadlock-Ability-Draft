using AbilityDraft.Runtime;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private readonly PlayerInactivity inactivity = new();
    private readonly Dictionary<int, ulong> lastInputButtons = new();
    private double nextInactivityCheck;
    private EmptyMatchLifetime matchLifetime = new();

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        if (workerResult is null || !matchReleased || workerState != "Playing" || args.Paused ||
            args.Controller is not { } player || !Players.IsConnected(args.PlayerSlot) ||
            !workerResult.IsPlayer(player.PlayerSteamId.ToString())) return;
        var active = false;
        var buttons = lastInputButtons.GetValueOrDefault(args.PlayerSlot);
        foreach (var command in args.Usercmds)
            active |= NativePlayerInput.HasActivity(command, ref buttons);
        lastInputButtons[args.PlayerSlot] = buttons;
        inactivity.Observe(args.PlayerSlot, GameRules.GameClock, active);
    }

    private void RecordMatchActivity(CCitadelPlayerController player)
    {
        if (sessions.TryGetValue(player.PlayerSteamId.ToString(), out var session)) session.LastActivityUtc = DateTime.UtcNow;
        if (workerResult is not null && matchReleased && workerState == "Playing" && workerResult.IsPlayer(player.PlayerSteamId.ToString()))
            inactivity.Observe(player.Slot, GameRules.GameClock, active: true);
    }

    private DateTime nextLobbyInactivityCheck;
    private void TickLobbyInactivity()
    {
        var now = DateTime.UtcNow;
        if (!IsDraftHub || serverRole != "custom" || now < nextLobbyInactivityCheck) return;
        nextLobbyInactivityCheck = now.AddSeconds(1);
        foreach (var (steam, session) in sessions.ToArray())
        {
            if (now - session.LastActivityUtc < TimeSpan.FromMinutes(5) || session.ExitRequested) continue;
            if (Players.FromSlot(session.Slot) is not { } player || player.PlayerSteamId.ToString() != steam) continue;
            session.ExitRequested = true;
            Log($"Custom lobby AFK kick at slot {session.Slot}: five minutes without website activity; reconnect remains allowed.");
            player.Kick();
        }
    }

    private void TickInactivity()
    {
        if (!matchReleased || workerState != "Playing" || GameRules.GamePaused || GameRules.ServerPaused) return;
        var now = GameRules.GameClock;
        if (now < nextInactivityCheck) return;
        nextInactivityCheck = now + 1;
        var players = Players.GetAll().Where(p => workerResult!.IsPlayer(p.PlayerSteamId.ToString()) &&
            !workerAbandoned.Contains(p.PlayerSteamId.ToString())).ToDictionary(p => p.Slot);
        foreach (var slot in players.Keys) inactivity.Observe(slot, now, active: false);
        foreach (var slot in inactivity.Expired(now))
        {
            inactivity.Remove(slot);
            if (!players.TryGetValue(slot, out var player)) continue;
            Log($"AFK kick at slot {slot}: five minutes without player input; reconnect remains allowed.");
            player.Kick(); // Disconnect only. Never abandon, revoke admission or remove the pawn.
        }
    }

    private int ConnectedMatchPlayers(int exceptSlot = -1) => Players.GetAll().Count(p => p.Slot != exceptSlot &&
        workerResult!.IsPlayer(p.PlayerSteamId.ToString()) && !workerAbandoned.Contains(p.PlayerSteamId.ToString()));
}
