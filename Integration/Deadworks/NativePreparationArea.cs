using System.Numerics;
using AbilityDraft.Runtime;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

// Map-authored spawn points and the engine's friendly-base trigger state.
// The map owns barrier geometry; friendly-base containment is a fallback.
public sealed class NativePreparationArea(DeadworksRuntime game, Action<string> log) : IPreparationArea
{
    private sealed class Hold(Vector3 position, float entered)
    {
        public Vector3 LastSafe = position;
        public float Entered = entered;
        public bool Confirmed, Returned;
    }
    private readonly Dictionary<string, Hold> held = new();
    private float nextCheck;

    public void Enter(string steamId, int team)
    {
        var pawn = game.Find(steamId)?.GetHeroPawn()
            ?? throw new InvalidOperationException("Cannot enter preparation without the drafted pawn.");
        if (held.Count == 0) SetBarriers(true);
        var patron = Entities.ByDesignerName("npc_boss_tier3").FirstOrDefault(e => e.TeamNum == team)
            ?? throw new InvalidOperationException("This map has no team Patron for preparation spawn selection.");
        var spawns = Entities.ByDesignerName("info_team_spawn").Where(e => e.TeamNum == team)
            .OrderBy(e => Vector3.DistanceSquared(e.Position, patron.Position)).ToArray();
        if (spawns.Length == 0) throw new InvalidOperationException("This map has no team spawn points for preparation.");
        var spawn = spawns[held.Count % Math.Min(6, spawns.Length)].Position;
        if (Environment.GetEnvironmentVariable("ABILITYDRAFT_TRACE_MATCH_START") == "1")
            log($"Preparation spawn slot {pawn.Controller?.Slot}: native={pawn.Position}, base={spawn}");
        pawn.Teleport(position: spawn, velocity: Vector3.Zero);
        held[steamId] = new(spawn, GlobalVars.CurTime);
        log($"Preparation base spawn selected for slot {pawn.Controller?.Slot}, team {team}");
    }

    public void Tick()
    {
        if (held.Count == 0 || GlobalVars.CurTime < nextCheck) return;
        nextCheck = GlobalVars.CurTime + 0.1f;
        foreach (var player in Players.GetAll())
        {
            if (!held.TryGetValue(game.Identity(player), out var hold) || player.GetHeroPawn() is not { } pawn) continue;
            if (pawn.ModifierProp?.HasModifierState(EModifierState.InFriendlyBase) == true)
            {
                hold.LastSafe = pawn.Position;
                if (!hold.Confirmed) log($"Preparation base containment verified for slot {player.Slot}");
                hold.Confirmed = true;
                continue;
            }
            if (!hold.Confirmed && GlobalVars.CurTime - hold.Entered > 3)
                throw new InvalidOperationException("Native friendly-base trigger did not confirm the preparation spawn.");
            pawn.Teleport(position: hold.LastSafe, velocity: Vector3.Zero);
            if (hold.Confirmed && !hold.Returned)
            {
                hold.Returned = true;
                log($"Preparation exit blocked for slot {player.Slot}; returned inside own base");
            }
        }
    }

    private static bool IsBarrier(CBaseEntity entity) =>
        entity.Name.EndsWith("amber_spawn_block_brush", StringComparison.Ordinal) ||
        entity.Name.EndsWith("sapphire_spawn_block_brush", StringComparison.Ordinal);
    private void SetBarriers(bool enabled)
    {
        var barriers = Entities.ByDesignerName("func_brush").Where(IsBarrier).ToArray();
        foreach (var barrier in barriers) barrier.AcceptInput(enabled ? "Enable" : "Disable");
        log($"Preparation map barriers {(enabled ? "enabled" : "released")}: {barriers.Length}");
    }
    public void Release()
    {
        if (held.Count != 0) SetBarriers(false);
        held.Clear(); nextCheck = 0;
    }
}
