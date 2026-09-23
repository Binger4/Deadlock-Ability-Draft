using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

// The intro resolves lanes by lobby slot. Assign missing slots before hero setup;
// otherwise direct-connect players fall back to yellow after swapping lanes.
public sealed partial class AbilityDraftPlugin
{
    private static readonly SchemaAccessor<byte> LobbyPlayerSlot = new("CCitadelPlayerController"u8, "m_unLobbyPlayerSlot"u8);
    private static readonly SchemaAccessor<sbyte> AssignedLane = new("CCitadelPlayerController"u8, "m_nAssignedLane"u8);
    private static readonly SchemaAccessor<sbyte> OriginalLane = new("CCitadelPlayerController"u8, "m_nOriginalLaneAssignment"u8);
    private static readonly SchemaAccessor<int> AttachedZiplineLane = new("CCitadel_Ability_ZipLine"u8, "m_iAttachedZipLineLane"u8);
    private readonly Dictionary<uint, (sbyte Current, sbyte Original)> lastLanes = new();
    private readonly Dictionary<uint, int> lastIntroLanes = new();
    private float nextLaneAudit;

    private void EnsureNativeLobbySlots()
    {
        if (workerResult is null || matchReleased) return;
        var players = Players.GetAll().Where(p => !IsMatchSpectator(p.PlayerSteamId.ToString())).ToArray();
        var occupied = players.Select(p => LobbyPlayerSlot.Get(p.Handle)).Where(slot => slot != 0).ToHashSet();
        foreach (var player in players)
        {
            if (LobbyPlayerSlot.Get(player.Handle) != 0) continue;
            var slot = Enumerable.Range(1, 24).Select(i => (byte)i).FirstOrDefault(i => !occupied.Contains(i));
            if (slot == 0) throw new InvalidOperationException("No native lobby slot available for the draft roster.");
            LobbyPlayerSlot.Set(player.Handle, slot);
            occupied.Add(slot);
            Log($"Native lobby slot {slot} assigned to connected slot {player.Slot} for lane handoff");
        }
    }

    private void AuditLanes()
    {
        if (workerResult is null || !GameRules.IsValid || GlobalVars.CurTime < nextLaneAudit) return;
        nextLaneAudit = GlobalVars.CurTime + 0.25f;
        foreach (var player in Players.GetAll())
        {
            var lanes = (AssignedLane.Get(player.Handle), OriginalLane.Get(player.Handle));
            if (matchReleased && GameRules.GameClock < 20 && player.GetHeroPawn() is { } pawn)
            {
                var zipline = pawn.AbilityComponent.Abilities.FirstOrDefault(a => a.AbilityName == "citadel_ability_zip_line");
                if (zipline is not null)
                {
                    var attached = AttachedZiplineLane.Get(zipline.Handle);
                    if (lastIntroLanes.GetValueOrDefault(player.EntityHandle, -1) != attached)
                    {
                        lastIntroLanes[player.EntityHandle] = attached;
                        Log($"Intro lane handoff slot {player.Slot}: assigned={lanes.Item1}, zipline={attached}, clock={GameRules.GameClock:F1}");
                    }
                }
            }
            if (lastLanes.TryGetValue(player.EntityHandle, out var previous) && previous == lanes) continue;
            lastLanes[player.EntityHandle] = lanes;
            Log($"Native lane slot {player.Slot}, team {player.TeamNum}: assigned={lanes.Item1}, original={lanes.Item2}, lobby={LobbyPlayerSlot.Get(player.Handle)}, phase={GameRules.GameState}");
        }
    }

    public override HookResult OnClientConCommand(ClientConCommandEvent args)
    {
        if (workerResult is not null && args.Command == "laneswap" && args.Controller is { } player)
            Log($"Native lane swap requested by slot {player.Slot}; assigned={AssignedLane.Get(player.Handle)}; args={string.Join(",", args.Args.Take(3))}");
        return HookResult.Continue;
    }
}
