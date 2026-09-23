using DeadworksManaged.Api;
using System.Text.Json;
using AbilityDraft.Contracts;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private readonly HashSet<uint> observerControllers = new();
    private Dictionary<string, DraftSpectator> workerSpectators = new();
    private bool IsMatchSpectator(string steam) => workerSpectators.ContainsKey(steam) || workerResult?.IsSpectator(steam) == true;

    private HashSet<string> RefreshSpectatorAdmission()
    {
        var file = new FileInfo(Path.Combine(workerDirectory!, "spectators.json"));
        if (file.Exists)
        {
            if (file.Length > 64 * 1024) throw new InvalidOperationException("Spectator roster is too large.");
            var spectators = JsonSerializer.Deserialize<DraftSpectator[]>(File.ReadAllText(file.FullName), Json)
                ?? throw new InvalidOperationException("Missing spectator roster.");
            RuntimePlayerId.Validate(workerResult! with { Spectators = spectators });
            workerSpectators = spectators.ToDictionary(s => s.SteamId64);
        }
        else workerSpectators = workerResult!.Spectators.ToDictionary(s => s.SteamId64);
        var revoked = Path.Combine(workerDirectory!, "abandoned.json");
        return File.Exists(revoked) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(revoked), Json) ?? [] : [];
    }

    // Use the SDK's native observer pawn, never a hero with spectator-like movement.
    // Run on the game frame after full connect, outside the hero initialization callback.
    private void PrepareSpectators()
    {
        if (workerResult is null || !GameRules.IsValid) return;
        foreach (var player in Players.GetAll().Where(p => IsMatchSpectator(p.PlayerSteamId.ToString()) && sessions.ContainsKey(p.PlayerSteamId.ToString())))
        {
            if (workerAbandoned.Contains(player.PlayerSteamId.ToString())) continue;
            if (observerControllers.Contains(player.EntityHandle) && player.TeamNum == 1 && player.Pawn is not null && player.GetHeroPawn() is null) continue;
            player.ChangeTeam(1, keepHero: false);
            player.MakeObserver();
            player.Pawn?.SetObserverMode(ObserverMode_t.Roaming);
            observerControllers.Add(player.EntityHandle);
            Log($"Draft spectator admitted as native observer in slot {player.Slot}");
        }
    }
}
