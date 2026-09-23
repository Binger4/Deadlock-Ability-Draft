using System.Text.Json;
using AbilityDraft.Contracts;
using abilitydraft.Services.InGame;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static class MatchWorkerChecks
{
    public static void Run(string temporary)
    {
        var config = new MatchWorkerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "workers") };
        var host = new FakeHost();
        using var coordinator = new MatchWorkerCoordinator(Options.Create(config), host, NullLogger<MatchWorkerCoordinator>.Instance);
        var result = new DraftResult(1, "result-one", "ROOM", DateTime.UtcNow, DateTime.UtcNow,
            [new("one", "76561198000000001", "Player", "HiddenKing", "hero_inferno", 1, null, false,
                [new(1,"a",false),new(2,"b",false),new(3,"c",false),new(4,"d",true)])]);
        var catalog = new DraftResourceCatalog(1, [new("hero_inferno", 1, ["a","b","c","d"])]);
        var first = coordinator.Request(result, catalog);
        Check(first.State == "Starting" && first.Address is null && host.Launches.Count == 1, "Worker address stays private until map readiness");
        Check(coordinator.Request(result, catalog).MatchId == first.MatchId && host.Launches.Count == 1, "Duplicate match requests launch exactly one worker");
        Expect(() => coordinator.Request(result with { ResultId = "second" }, catalog), "Worker capacity cannot exceed configured process limit");
        Expect(() => coordinator.Request(result with { ResultId = "bot", Players = [result.Players[0] with { IsBot = true }] }, catalog), "Worker refuses an unmappable bot roster");
        var now = DateTime.UtcNow;
        WriteReport(host.Launches[0], new(result.ResultId, "Ready", 0, 1, now));
        coordinator.Poll(now);
        Check(coordinator.Get(result.ResultId) is { State: "Ready", Address: "127.0.0.1:27068" }, "Only a ready reserved worker exposes its match address");
        coordinator.Poll(now.AddSeconds(40));
        Check(coordinator.Get(result.ResultId) is { State: "Failed", Address: null } && host.Processes[0].Stopped, "Stale worker heartbeat stops only the owned process and hides its address");
        var retry = coordinator.Request(result, catalog);
        Check(retry.MatchId != first.MatchId && host.Launches.Count == 2 && host.Launches[1].Port == 27068, "Host can retry a failed result after its port is released");
        WriteReport(host.Launches[1], new(result.ResultId, "Completed", 1, 1, now));
        coordinator.Poll(now);
        Check(coordinator.Get(result.ResultId) is { State: "Completed", Address: null } && !host.Processes[1].Stopped, "Match end hides handoff address and gives the result screen a grace period");
        coordinator.Poll(now.AddSeconds(21));
        Check(host.Processes[1].Stopped, "Finished match worker is shut down after the grace period");
        var second = result with { ResultId = "second" };
        coordinator.Request(second, catalog);
        coordinator.Poll(DateTime.UtcNow.AddMinutes(3));
        Check(coordinator.Get(second.ResultId)?.State == "Failed" && host.Processes[2].Stopped, "Worker that never loads its map times out");
        var third = result with { ResultId = "third" };
        coordinator.Request(third, catalog);
        WriteReport(host.Launches[3], new("wrong-result", "Ready", 0, 1, now));
        coordinator.Poll(now);
        Check(coordinator.Get(third.ResultId)?.State == "Failed" && host.Processes[3].Stopped, "Worker report cannot substitute another website result");
        var empty = result with { ResultId = "empty-match" };
        coordinator.Request(empty, catalog);
        WriteReport(host.Launches[4], new(empty.ResultId, "Playing", 0, 1, now));
        coordinator.Poll(now);
        Check(!host.Processes[4].Stopped, "Empty playing worker allows a reconnect grace period");
        WriteReport(host.Launches[4], new(empty.ResultId, "Playing", 1, 1, now.AddSeconds(100)));
        coordinator.Poll(now.AddSeconds(100));
        WriteReport(host.Launches[4], new(empty.ResultId, "Playing", 0, 1, now.AddSeconds(130)));
        coordinator.Poll(now.AddSeconds(130));
        Check(!host.Processes[4].Stopped, "A returning player resets the empty-match watchdog");
        WriteReport(host.Launches[4], new(empty.ResultId, "Playing", 0, 1, now.AddSeconds(251)));
        coordinator.Poll(now.AddSeconds(251));
        Check(coordinator.Get(empty.ResultId) is { State: "Completed", Address: null } && host.Processes[4].Stopped,
            "An abandoned empty match releases its owned worker and port");
        var exited = result with { ResultId = "completed-exit" };
        coordinator.Request(exited, catalog);
        WriteReport(host.Launches[5], new(exited.ResultId, "Completed", 1, 1, now));
        coordinator.Poll(now);
        host.Processes[5].Stop();
        coordinator.Poll(now.AddSeconds(1));
        Check(coordinator.Get(exited.ResultId)?.State == "Completed", "A completed worker's natural exit does not turn the match into a failure");
        config.PublicHost = "127.0.0.1;quit";
        Expect(() => coordinator.Request(result with { ResultId = "bad-host" }, catalog), "Match address rejects console command injection");
        config.Enabled = false;
        Expect(() => coordinator.Request(result with { ResultId = "disabled" }, catalog), "Match process launch is opt-in");
        CheckBots(temporary, result, catalog);
        CheckConcurrentAndAbandon(temporary, result, catalog);
        CheckLateArrival(temporary, result, catalog);
        CheckSpectators(temporary, result, catalog);
    }
    private static void CheckSpectators(string temporary, DraftResult result, DraftResourceCatalog catalog)
    {
        var config = new MatchWorkerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "spectators") };
        var host = new FakeHost();
        using var coordinator = new MatchWorkerCoordinator(Options.Create(config), host, NullLogger<MatchWorkerCoordinator>.Instance);
        var watching = result with { Spectators = [new("watcher", "76561198000000003")] };
        coordinator.Request(watching, catalog);
        Check(host.Launches[0].Result.IsSpectator("76561198000000003") && !watching.Admits("76561198000000099"),
            "Worker admission includes only the frozen player and spectator roster");
        coordinator.Abandon(watching.ResultId, "76561198000000003");
        var now = DateTime.UtcNow;
        WriteReport(host.Launches[0], new(watching.ResultId, "Ready", 1, 1, now)); coordinator.Poll(now);
        Check(!host.Processes[0].Stopped && coordinator.Get(watching.ResultId)?.State == "Ready", "Spectator departure neither stops the match nor changes required player count");
        WriteReport(host.Launches[0], new(watching.ResultId, "Playing", 1, 1, now.AddSeconds(2))); coordinator.Poll(now.AddSeconds(2));
        Check(coordinator.Get(watching.ResultId)?.State == "Playing", "Playing match keeps its address available after a spectator leaves");
        coordinator.AdmitSpectator(watching.ResultId, new("late", "76561198000000004", "Late spectator"));
        var admitted = JsonSerializer.Deserialize<DraftSpectator[]>(File.ReadAllText(Path.Combine(host.Launches[0].Directory, "spectators.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(admitted.Any(s => s.SteamId64 == "76561198000000004") && host.Launches.Count == 1,
            "A new observer can enter a playing match without restarting it or changing its result");
        var stats = coordinator.GetActiveMatchStats().Single();
        Check(stats.State == "Playing" && stats.Players.Single().Name == "Player" && stats.Connected == 1 && stats.Spectators.Contains("Late spectator"),
            "Admin active match snapshot contains the current status, players and spectator roster");
        Expect(() => coordinator.AdmitSpectator(watching.ResultId, new("bad", watching.Players[0].SteamId64!)),
            "Observer admission cannot overwrite a drafted player's identity");
        coordinator.Abandon(watching.ResultId, watching.Players[0].SteamId64!);
        Check(host.Processes[0].Stopped, "Spectators do not keep a match alive after its last player abandons");
        Check(coordinator.GetActiveMatchStats().Count == 0, "Finished workers leave the active match list");
        Expect(() => RuntimePlayerId.Validate(watching with { Spectators = [new("watcher", watching.Players[0].SteamId64!)] }),
            "One Steam identity cannot be both player and spectator");
    }
    private static void CheckBots(string temporary, DraftResult result, DraftResourceCatalog catalog)
    {
        var config = new MatchWorkerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "bots") };
        var host = new FakeHost();
        using var coordinator = new MatchWorkerCoordinator(Options.Create(config), host, NullLogger<MatchWorkerCoordinator>.Instance);
        var mixed = result with { Players = [result.Players[0], result.Players[0] with { ParticipantId = "bot", SteamId64 = null, IsBot = true }] };
        coordinator.Request(mixed, catalog);
        var now = DateTime.UtcNow;
        WriteReport(host.Launches[0], new(mixed.ResultId, "Playing", 1, 1, now)); coordinator.Poll(now);
        Check(coordinator.Get(mixed.ResultId)?.State == "Playing", "Bots do not count as missing human connections");
        coordinator.Abandon(mixed.ResultId, mixed.Players[0].SteamId64!);
        Check(host.Processes[0].Stopped, "Bots do not keep an abandoned human match alive");
        coordinator.Request(mixed with { ResultId = "bots-empty" }, catalog);
        WriteReport(host.Launches[1], new("bots-empty", "Playing", 0, 1, now)); coordinator.Poll(now);
        WriteReport(host.Launches[1], new("bots-empty", "Playing", 0, 1, now.AddSeconds(16))); coordinator.Poll(now.AddSeconds(16));
        Check(host.Processes[1].Stopped, "Empty-human watchdog reclaims a worker containing bots");
    }
    private static void CheckLateArrival(string temporary, DraftResult result, DraftResourceCatalog catalog)
    {
        var config = new MatchWorkerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "late-arrival") };
        var host = new FakeHost();
        using var coordinator = new MatchWorkerCoordinator(Options.Create(config), host, NullLogger<MatchWorkerCoordinator>.Instance);
        coordinator.Request(result, catalog);
        var now = DateTime.UtcNow;
        void Report(int seconds, int connected, string state = "Ready")
        {
            WriteReport(host.Launches[0], new(result.ResultId, state, connected, 1, now.AddSeconds(seconds)));
            coordinator.Poll(now.AddSeconds(seconds));
        }
        Report(0, 0);
        Report(179, 1);
        Report(211, 1);
        Check(coordinator.Get(result.ResultId)?.State == "Ready" && !host.Processes[0].Stopped,
            "Last arrival gets the full preparation window even near the three-minute join deadline");
        Report(270, 1);
        Check(coordinator.Get(result.ResultId)?.State == "Failed" && host.Processes[0].Stopped,
            "A fully connected worker with stuck preparation still times out");
    }
    private static void CheckConcurrentAndAbandon(string temporary, DraftResult solo, DraftResourceCatalog catalog)
    {
        var config = new MatchWorkerOptions { Enabled = true, MaxWorkers = 2, FirstPort = 27069,
            StateDirectory = Path.Combine(temporary, "concurrent-workers") };
        var host = new FakeHost();
        using var coordinator = new MatchWorkerCoordinator(Options.Create(config), host, NullLogger<MatchWorkerCoordinator>.Instance);
        var first = solo with { ResultId = "group-one", Players = [solo.Players[0], solo.Players[0] with { ParticipantId = "two", SteamId64 = "76561198000000002" }] };
        var second = solo with { ResultId = "group-two", Players = [solo.Players[0] with { ParticipantId = "three", SteamId64 = "76561198000000003" }] };
        coordinator.Request(first, catalog);
        coordinator.Request(second, catalog);
        Check(host.Launches.Select(l => l.Port).SequenceEqual(new[] { 27069, 27070 }) && host.Launches[0].Directory != host.Launches[1].Directory,
            "Two groups receive independent match processes, ports and results");
        coordinator.Abandon(first.ResultId, first.Players[0].SteamId64!);
        Check(host.Processes.All(p => !p.Stopped), "One abandon does not stop the remaining players or another group's match");
        host.Processes[0].Stop();
        coordinator.Poll(DateTime.UtcNow);
        coordinator.Abandon(first.ResultId, first.Players[0].SteamId64!);
        coordinator.Request(first, catalog);
        var denied = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(host.Launches[2].Directory, "abandoned.json")))!;
        Check(denied.SequenceEqual(new[] { first.Players[0].SteamId64 }), "Abandoned identities stay revoked after a failed worker is retried");
        coordinator.Abandon(first.ResultId, first.Players[1].SteamId64!);
        Check(host.Processes[2].Stopped && !host.Processes[1].Stopped && coordinator.Get(first.ResultId)?.State == "Completed",
            "Last abandon immediately stops only that group's worker");
        var beforeLaunch = first with { ResultId = "abandoned-before-allocation" };
        coordinator.Abandon(beforeLaunch.ResultId, beforeLaunch.Players[0].SteamId64!);
        coordinator.Request(beforeLaunch, catalog);
        denied = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(host.Launches[3].Directory, "abandoned.json")))!;
        Check(denied.Contains(first.Players[0].SteamId64!), "Abandon before worker allocation is included in its initial admission list");
        coordinator.Abandon(second.ResultId, second.Players[0].SteamId64!);
        coordinator.Abandon(beforeLaunch.ResultId, beforeLaunch.Players[1].SteamId64!);
    }
    private static void WriteReport(MatchWorkerLaunch launch, MatchWorkerReport report) => File.WriteAllText(
        Path.Combine(launch.Directory, "status.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static void Expect(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    private sealed class FakeHost : IMatchWorkerHost
    {
        public List<MatchWorkerLaunch> Launches = [];
        public List<FakeProcess> Processes = [];
        public IMatchWorkerProcess Start(MatchWorkerLaunch launch)
        {
            Launches.Add(launch); var process = new FakeProcess(); Processes.Add(process); return process;
        }
    }
    private sealed class FakeProcess : IMatchWorkerProcess
    {
        public bool Stopped;
        public bool HasExited => Stopped;
        public void Stop() => Stopped = true;
        public void Dispose() => Stop();
    }
}
