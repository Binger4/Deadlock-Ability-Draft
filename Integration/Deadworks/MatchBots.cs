using AbilityDraft.Contracts;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private readonly Queue<uint> createdBots = new();
    private readonly HashSet<uint> initializedBots = new();
    private DateTime botDeadline;
    private bool botsRequested;

    public override void OnClientPutInServer(ClientPutInServerEvent args)
    {
        if (workerResult is not null && args.IsBot && args.Controller is { } controller)
            createdBots.Enqueue(controller.EntityHandle);
    }

    private void BotHeroInitialized(CCitadelPlayerPawn pawn)
    {
        if (workerResult is not null && pawn.Controller is { PlayerSteamId: 0 } controller)
            initializedBots.Add(controller.EntityHandle);
    }

    private bool PrepareBots(DraftResult result)
    {
        var bots = result.Players.Where(p => p.IsBot).ToArray();
        if (bots.Length == 0) return true;
        if (!botsRequested)
        {
            // Native practice fill adds real fake-client controllers up to a team total.
            // The website still chooses every hero, team and ability; the applier replaces
            // the temporary native bot loadouts after these controllers arrive.
            var enabled = ConVar.Find("citadel_spawn_practice_bots")
                ?? throw new InvalidOperationException("Native practice bot fill is unavailable.");
            var count = ConVar.Find("citadel_spawn_practice_bots_count")
                ?? throw new InvalidOperationException("Native bot team size is unavailable.");
            var teams = result.Players.GroupBy(p => p.Team).Select(g => g.Count()).Distinct().ToArray();
            if (teams.Length != 1 || teams[0] is < 1 or > 6)
                throw new InvalidOperationException("Native bot fill requires equally sized draft teams.");
            foreach (var human in result.Players.Where(p => !p.IsBot))
                game.BeginHero(RuntimePlayerId.For(human), human.Team, human.HeroKey);
            game.TickHeroAssignments();
            // Bots initially occupy native practice heroes; the final website roster is
            // still validated independently while those temporary heroes are replaced.
            ConVar.Find("citadel_allow_duplicate_heroes")?.SetInt(1);
            count.SetInt(teams[0]);
            enabled.SetInt(1);
            botsRequested = true;
            botDeadline = DateTime.UtcNow.AddSeconds(25);
            Log($"Requested native bot fill for {bots.Length} drafted seats, {teams[0]} players per team");
        }
        while (createdBots.TryDequeue(out var handle))
        {
            var controller = CBaseEntity.FromHandle<CCitadelPlayerController>(handle);
            if (controller is null) continue;
            var member = bots.FirstOrDefault(p => !game.IsConnected(RuntimePlayerId.For(p)) &&
                (p.Team == "HiddenKing" ? 2 : 3) == controller.TeamNum)
                ?? bots.FirstOrDefault(p => !game.IsConnected(RuntimePlayerId.For(p)));
            if (member is null) throw new InvalidOperationException("Native fill created more bots than the reserved draft.");
            game.BindBot(RuntimePlayerId.For(member), controller);
            Log($"Draft bot {member.ParticipantId} mapped to native slot {controller.Slot}; {member.Team}, {member.HeroKey}");
        }
        // Practice fill loads its initial heroes asynchronously. A second SelectHero
        // during that load can be overwritten by the first completion. Wait for the
        // native initialization event before applying the website's drafted heroes.
        if (bots.All(p => game.Find(RuntimePlayerId.For(p)) is { } controller &&
            initializedBots.Contains(controller.EntityHandle)))
        {
            ConVar.Find("citadel_spawn_practice_bots")!.SetInt(0);
            return true;
        }
        if (DateTime.UtcNow > botDeadline) throw new InvalidOperationException("Native bot fill did not complete the drafted roster.");
        return false;
    }
}
