using System.Text.Json;
using AbilityDraft.Contracts;
using AbilityDraft.Runtime;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private bool separateMatches, workerPreparing, workerReporting;
    private DraftResult? workerResult;
    private string? workerDirectory;
    private string workerState = "Starting";
    private int workerLastCount = -1;
    private DateTime nextWorkerReport, workerStarted = DateTime.UtcNow;
    private HashSet<string> workerAbandoned = new();
    private readonly MatchPreparation preparation = new();

    private void LoadMatchWorker()
    {
        workerDirectory = Environment.GetEnvironmentVariable("ABILITYDRAFT_MATCH_DIRECTORY");
        if (string.IsNullOrWhiteSpace(workerDirectory)) { workerDirectory = null; return; }
        if (!Path.IsPathFullyQualified(workerDirectory)) throw new InvalidOperationException("Match directory must be absolute.");
        workerResult = JsonSerializer.Deserialize<DraftResult>(File.ReadAllText(Path.Combine(workerDirectory, "result.json")), Json);
        if (workerResult is null) throw new InvalidOperationException("Missing reserved website result.");
        RuntimePlayerId.Validate(workerResult);
        Log("Isolated match worker loaded website result " + workerResult.ResultId);
        var revoked = Path.Combine(workerDirectory, "abandoned.json");
        if (File.Exists(revoked)) workerAbandoned = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(revoked), Json) ?? [];
    }

    public override bool OnClientConnect(ClientConnectEvent args)
    {
        if (workerResult is null) return true;
        // Read admission before accepting a late observer, avoiding an address/polling race.
        HashSet<string> abandoned;
        try { abandoned = RefreshSpectatorAdmission(); }
        catch (Exception ex) { Log("Match admission read failed: " + ex.GetType().Name); return false; }
        var steam = args.SteamId.ToString();
        return !abandoned.Contains(steam) && (workerResult.IsPlayer(steam) || IsMatchSpectator(steam));
    }

    private void RequestSeparateMatch(string steam, Session session)
    {
        if (session.View is not { Status: "Completed" } view ||
            !view.Players.Any(p => p.Id == view.SelfId && (p.Host || view.IsPublicQueue) && p.Team != "Spectator"))
            throw new InvalidOperationException("Complete the draft before starting the match.");
        Run(steam, session, async () =>
        {
            if (view.RuntimeResultId is null) await backend.Command(steam, new("finalize"), stop.Token);
            var match = await backend.RequestMatch(steam, stop.Token);
            var reply = await backend.State(steam, stop.Token);
            return () => { Deliver(steam, session, reply)(); DeliverMatch(steam, session, match); };
        });
    }

    private void DeliverMatch(string steam, Session session, MatchView match)
    {
        if (match.ResultId != session.View?.RuntimeResultId) return;
        session.Match = match;
        if (match.State == "Starting") session.AutoTransferMatch = match.MatchId;
        session.RuntimeStatus = match.Message;
        Status(steam, match.Message);
        var recipients = Players.FromSlot(session.Slot)!.Recipients;
        UI.Panel(PanelId).Set(recipients, "spectating", session.View.Players.Any(p => p.Id == session.View.SelfId && p.Team == "Spectator") ? "1" : "0");
        UI.Panel(PanelId).Set(recipients, "canPlayMatch", match.State is "Idle" or "Failed" && session.View.Players.Any(p => p.Id == session.View.SelfId && (p.Host || session.View.IsPublicQueue) && p.Team != "Spectator") ? "1" : "0");
        UI.Panel(PanelId).Set(recipients, "canResumeMatch", match.Address is not null && match.State is "Ready" or "Playing" ? "1" : "0");
        if (session.AutoTransferMatch == match.MatchId) TransferToMatch(session, match);
    }

    private void TransferToMatch(Session session, MatchView match)
    {
        var recipients = Players.FromSlot(session.Slot)!.Recipients;
        if (match.Address is not null && match.State is "Ready" or "Playing")
        {
            var parts = match.Address.Split(':');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[0].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
                !int.TryParse(parts[1], out var port) || port is < 1024 or > 65535)
                throw new InvalidOperationException("Invalid match server address.");
            UI.Panel(PanelId).Set(recipients, "matchAddress", match.Address);
            UI.Panel(PanelId).Set(recipients, "transferMatch", match.MatchId);
        }
    }

    private void TickMatchWorker()
    {
        var reserved = workerResult!;
        var result = reserved with { Players = reserved.Players.Where(p => p.IsBot || !workerAbandoned.Contains(p.SteamId64!)).ToArray(), Spectators = workerSpectators.Values.ToArray() };
        if (GameRules.IsValid && workerState == "Starting") workerState = "Ready";
        PrepareSpectators();
        var count = Players.GetAll().Count(p => result.IsPlayer(p.PlayerSteamId.ToString()));
        var required = result.Players.Count(p => !p.IsBot);
        if (workerLastCount != count)
        {
            workerLastCount = count;
            if (!matchReleased) GameRules.SetWaitingForPlayersRoster((uint)count, (uint)required);
            foreach (var session in sessions.Values)
            {
                var recipients = Players.FromSlot(session.Slot)!.Recipients;
                UI.Panel(PanelId).Set(recipients, "workerHint", $"{count}/{required} drafted players connected");
            }
        }
        if (!workerPreparing && workerState == "Ready" && result.Players.Length > 0 && count == required &&
            GameRules.GameState is EGameState.WaitingForPlayersToJoin or EGameState.HeroSelection)
        {
            try
            {
                EnsureNativeLobbySlots();
                if (PrepareBots(result))
                {
                    workerPreparing = true;
                    VerifyConnectedRoster(result); applier.Apply(result);
                }
            }
            catch (Exception ex) { ApplicationFailed(result.ResultId, ex.Message); }
        }
        if (workerPreparing && !matchReleased && preparedResult == result.ResultId && workerState == "Ready" && GameRules.GameState == EGameState.PreGameWait)
        {
            try
            {
                VerifyConnectedRoster(result);
                if (GameRules.GameState != EGameState.PreGameWait) throw new InvalidOperationException("Match left the pregame barrier.");
                if (preparation.EndsAt is null)
                {
                    applier.RestoreAfterNativePregame(result);
                    LogPregameTroopers();
                    foreach (var member in result.Players)
                        preparationArea.Enter(RuntimePlayerId.For(member), member.Team == "HiddenKing" ? 2 : 3);
                    preparation.Begin(GlobalVars.CurTime);
                    GameRules.SetGameStateEndTime(preparation.EndsAt!.Value);
                    Log("All drafted players connected; starting 30-second native pregame preparation for " + result.ResultId);
                    foreach (var session in sessions.Values)
                        if (Players.FromSlot(session.Slot) is { } player)
                            UI.Panel(PanelId).Set(player.Recipients, "preparationStarted", "1");
                }
                applier.VerifyPrepared(result);
                preparationArea.Tick();
                if (preparation.CanStart(GlobalVars.CurTime, rosterReady: true))
                {
                    ReleasePreparedMatch(); workerState = "Playing";
                }
            }
            catch (Exception ex) { ApplicationFailed(result.ResultId, ex.Message); }
        }
        PublishMatchRoster();
        if (!workerReporting && DateTime.UtcNow >= nextWorkerReport)
        {
            workerReporting = true;
            nextWorkerReport = DateTime.UtcNow.AddSeconds(3);
            var report = new MatchWorkerReport(reserved.ResultId, workerState, count, reserved.Players.Count(p => !p.IsBot), DateTime.UtcNow);
            _ = Task.Run(() =>
            {
                var ownerAlive = false;
                var abandoned = new HashSet<string>();
                try
                {
                    var revoked = Path.Combine(workerDirectory!, "abandoned.json");
                    if (File.Exists(revoked)) abandoned = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(revoked), Json) ?? [];
                    var path = Path.Combine(workerDirectory!, "status.json");
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(report, Json));
                    File.Move(path + ".tmp", path, overwrite: true);
                    var owner = File.GetLastWriteTimeUtc(Path.Combine(workerDirectory!, "owner.heartbeat"));
                    ownerAlive = DateTime.UtcNow - owner < TimeSpan.FromSeconds(45);
                }
                catch (Exception ex) { Log("Match worker report failed: " + ex.GetType().Name); }
                completions.Enqueue(() =>
                {
                    workerReporting = false;
                    // A spectator may have rejoined since the background heartbeat began.
                    try { abandoned = RefreshSpectatorAdmission(); }
                    catch (Exception ex) { Log("Admission refresh failed: " + ex.GetType().Name); }
                    if (!workerAbandoned.SetEquals(abandoned))
                    {
                        workerLastCount = -1; // Refresh the waiting roster even if player count did not change.
                        if (workerPreparing && !matchReleased && abandoned.Except(workerAbandoned).Any(reserved.IsPlayer))
                            ApplicationFailed(reserved.ResultId, "A player abandoned during hero preparation. The remaining host can retry the match.");
                    }
                    workerAbandoned = abandoned;
                    foreach (var (steam, session) in sessions)
                        if (workerAbandoned.Contains(steam)) ExitToMenu(session);
                    if (!ownerAlive || DateTime.UtcNow - workerStarted > TimeSpan.FromHours(2))
                    {
                        Log("Website heartbeat expired or match lifetime exceeded; stopping match server.");
                        Server.ExecuteCommand("quit");
                    }
                });
            });
        }
    }
}
