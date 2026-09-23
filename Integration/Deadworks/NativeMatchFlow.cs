using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private ConVar? trooperSpawning;
    private int originalTrooperSpawning;
    private int pregameTroopers;
    private bool firstWaveLogged;
    private bool trooperWavesReleased;
    private float nextWaveAudit, cinematicAuditAt;
    private const float FirstWaveSeconds = 20;

    private void ConfigureNativeMatchFlow()
    {
        pregameTroopers = 0; firstWaveLogged = false; trooperWavesReleased = false;
        nextWaveAudit = 0; cinematicAuditAt = 0;
        trooperSpawning = ConVar.Find("citadel_trooper_spawn_enabled")
            ?? throw new InvalidOperationException("This game build lacks native trooper spawn control.");
        originalTrooperSpawning = trooperSpawning.GetInt();
        trooperSpawning.SetInt(0);
        Log("Trooper hold requested; readback=" + trooperSpawning.GetString());
        if (workerResult is null) return;
        Log("Native lane override: " + (ConVar.Find("citadel_force_assigned_lane")?.GetString() ?? "unavailable"));
        var introEnabled = ConVar.Find("citadel_match_intro_force_enabled");
        var introStyle = ConVar.Find("citadel_match_intro_style");
        if (introEnabled is null || introStyle is null)
        {
            Log("Native match intro settings unavailable; using the engine's default presentation.");
            return;
        }
        introEnabled.SetInt(1);
        introStyle.SetInt(3); // Installed help: 3 = in-map intro, including the team arrival.
        ConVar.Find("citadel_pregame_use_intro_spawn")?.SetInt(1);
        ConVar.Find("citadel_cinematic_intro_enabled")?.SetInt(1);
        Log("Native match intro requested; style " + introStyle.GetInt());
    }

    private void HoldTrooperWaves()
    {
        if (trooperSpawning is null) return;
        if (matchReleased && GameRules.GameClock >= FirstWaveSeconds)
        {
            if (!trooperWavesReleased) ReleaseTrooperWaves();
            if (workerResult is not null && !firstWaveLogged && GlobalVars.CurTime >= nextWaveAudit)
            {
                nextWaveAudit = GlobalVars.CurTime + 1;
                var alive = Entities.ByDesignerName("npc_trooper").Count(e => e.IsAlive && e.TeamNum is 2 or 3);
                if (alive > 0)
                {
                    firstWaveLogged = true;
                    Log($"First active lane wave: {alive} troopers at match clock {GameRules.GameClock:F1}s");
                }
            }
            return;
        }
        // StartupServer runs before the engine executes its map/server defaults.
        // Reassert the hold after those defaults, without touching the native wave cadence.
        if (trooperSpawning.GetInt() == 0) return;
        Log("Engine settings changed the pregame trooper hold; restoring 0 from " + trooperSpawning.GetString());
        trooperSpawning.SetInt(0);
    }

    private void ReleaseTrooperWaves()
    {
        if (trooperSpawning is null) return;
        trooperWavesReleased = true;
        trooperSpawning.SetInt(originalTrooperSpawning);
        Log($"Native trooper spawning restored to {originalTrooperSpawning} at match clock {GameRules.GameClock:F1}s");
    }

    private void BeginPatronIntro()
    {
        if (workerResult is null) return;
        var alreadyPlaying = Players.GetAll().Any(p => p.GetHeroPawn()?.ModifierProp?.HasModifierState(EModifierState.CinematicIntro) == true);
        if (!alreadyPlaying)
        {
            Server.ExecuteCommand("citadel_test_cinematic_intro");
            Log("Requested native Patron cinematic through the engine intro command");
        }
        cinematicAuditAt = GlobalVars.CurTime + 0.5f;
    }

    private void AuditPatronIntro()
    {
        if (cinematicAuditAt == 0 || GlobalVars.CurTime < cinematicAuditAt) return;
        cinematicAuditAt = 0;
        var count = Players.GetAll().Count(p => p.GetHeroPawn()?.ModifierProp?.HasModifierState(EModifierState.CinematicIntro) == true);
        Log(count > 0 ? $"Native Patron cinematic active for {count} player(s)" : "Native Patron cinematic was not confirmed; this game build needs intro testing.");
    }

    private void LogPregameTroopers()
    {
        var groups = Entities.ByDesignerName("npc_trooper")
            .GroupBy(e => new { e.TeamNum, e.LifeState, e.Health, Origin = e.Position == System.Numerics.Vector3.Zero })
            .Select(g => $"team={g.Key.TeamNum},life={g.Key.LifeState},hp={g.Key.Health},atOrigin={g.Key.Origin},count={g.Count()}");
        Log("Pregame trooper audit; spawning=" + trooperSpawning?.GetString() + "; " + string.Join("; ", groups));
    }

    public override void OnEntitySpawned(EntitySpawnedEvent args)
    {
        if (workerResult is null) return;
        var name = args.Entity.DesignerName;
        if (name != "npc_trooper") return;
        if (!matchReleased)
        {
            pregameTroopers++;
            // Resource warmup can also create NPC entities; do not mistake every allocation
            // for a lane wave or remove engine-owned preload entities.
            if (pregameTroopers <= 3)
                Log($"Pregame trooper entity: {name}; team={args.Entity.TeamNum}; life={args.Entity.LifeState}; hp={args.Entity.Health}; position={args.Entity.Position}; spawning={trooperSpawning?.GetString()}");
        }
    }
}
