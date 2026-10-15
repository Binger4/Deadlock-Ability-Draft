using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AbilityDraft.Contracts;
using abilitydraft.Models;
using abilitydraft.Services.InGame;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static class GameNodeChecks
{
    public static async Task Run(string temporary)
    {
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(temporary, "nodes") };
        Directory.CreateDirectory(environment.ContentRootPath);
        var localOptions = Options.Create(new MatchWorkerOptions { Enabled = false });
        using var local = new MatchWorkerCoordinator(localOptions, new Host(), NullLogger<MatchWorkerCoordinator>.Instance);
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var configuration = Options.Create(new GameNodeOptions { RemoteNodes = [new RemoteGameNodeOptions {
            Id = "test-remote", Name = "Test remote", Region = "Test", Secret = secret, PublicGameHost = "127.0.0.1", MaxWorkers = 1 }] });
        GameNodeCoordinator NewCoordinator() => new(local, localOptions, Options.Create(new MainGameNodeOptions { Name = "Main server" }),
            configuration, Options.Create(new InGameOptions { Enabled = true }), environment, NullLogger<GameNodeCoordinator>.Instance);
        using var coordinator = NewCoordinator();
        Check(coordinator.Authenticate("test-remote", "Bearer " + secret), "Dedicated node authentication accepts its own credential");
        Check(!coordinator.Authenticate("main", "Bearer " + secret) && !coordinator.Authenticate("test-remote", "Bearer wrong"), "Wrong node and token are rejected");
        Check(coordinator.Nodes().Single(n => n.Id == "test-remote").Status == "Offline", "Configured nodes remain visible while offline");
        var registration = new GameNodeRegistration(1, "test-version", "127.0.0.1", 27069, 1, null);
        var session = coordinator.Connect("test-remote", registration);
        coordinator.Heartbeat("test-remote", session, new(registration, []), DateTime.UtcNow);
        Check(coordinator.Nodes().Single(n => n.Id == "test-remote").Available, "Reconciled heartbeat makes node available");
        var result = new DraftResult(1, "test-result", "TEST", DateTime.UtcNow, DateTime.UtcNow,
            [new("one", "76561198000000001", "Player", "HiddenKing", "hero_inferno", 1, null, false,
                [new(1, "a", false), new(2, "b", false), new(3, "c", false), new(4, "d", true)])]) { GameNodeId = "test-remote" };
        var catalog = new DraftResourceCatalog(1, [new("hero_inferno", 1, ["a", "b", "c", "d"])]);
        foreach (var (port, capacity, valid) in new[] { (65535, 1, true), (65535, 2, false), (int.MaxValue, 2, false) })
        {
            using var boundary = new MatchWorkerCoordinator(Options.Create(new MatchWorkerOptions { Enabled = true,
                FirstPort = port, MaxWorkers = capacity, StateDirectory = Path.Combine(temporary, "port-check-" + port + "-" + capacity) }),
                new Host(), NullLogger<MatchWorkerCoordinator>.Instance);
            if (valid) Check(boundary.Request(result, catalog).State == "Starting", "Single-worker pool can use port 65535");
            else Expect(() => boundary.Request(result, catalog), "Worker pool rejects out-of-range ports without integer overflow");
        }
        var winners = new ConcurrentBag<MatchView>();
        Parallel.For(0, 24, i => { try { winners.Add(coordinator.Request(result with { ResultId = "race-" + i }, catalog)); } catch (InvalidOperationException) { } });
        Check(winners.Count == 1, "Concurrent reservations cannot overbook the final slot");
        var winner = winners.Single();
        Check(coordinator.Request(result with { ResultId = winner.ResultId }, catalog).MatchId == winner.MatchId, "Duplicate request preserves assignment ID");
        Check(coordinator.Nodes().Single(n => n.Id == "test-remote") is { Status: "Full", FreeSlots: 0 }, "Pending starts count toward capacity");
        Expect(() => coordinator.RequireAvailable("test-remote"), "Server validates creation against current capacity");
        var reply = coordinator.Heartbeat("test-remote", session, new(registration, []), DateTime.UtcNow);
        Check(reply.StartMatches.Single().MatchId == winner.MatchId, "Typed StartMatch is replayed until observed");
        var report = new GameNodeMatchStatus(winner.MatchId, "Ready", 27069, true, 0, []);
        coordinator.Heartbeat("test-remote", session, new(registration, [report]), DateTime.UtcNow);
        Check(coordinator.Get(winner.ResultId)?.Address == "127.0.0.1:27069", "Ready address points directly to the selected node");
        var spectator = new DraftSpectator("spectator", "76561198000000002", "Observer");
        coordinator.AdmitSpectator(winner.ResultId, spectator);
        Check(coordinator.Get(winner.ResultId, spectator.SteamId64)?.Address is null, "Remote spectator handoff waits for admission acknowledgement");
        var admission = coordinator.Heartbeat("test-remote", session, new(registration, [report]), DateTime.UtcNow).StartMatches.Single().AdmissionRevision;
        report = report with { AdmissionRevision = admission };
        coordinator.Heartbeat("test-remote", session, new(registration, [report]), DateTime.UtcNow);
        Check(coordinator.Get(winner.ResultId, spectator.SteamId64)?.Address is not null, "Acknowledged spectator can connect directly");
        coordinator.Heartbeat("test-remote", session, new(registration, [report]), DateTime.UtcNow.AddMinutes(-1));
        Check(coordinator.Nodes().Single(n => n.Id == "test-remote").Status == "Offline" && coordinator.Get(winner.ResultId)?.Address is null,
            "Heartbeat timeout marks node offline and hides handoff while retaining capacity");
        using var restarted = NewCoordinator();
        Check(restarted.Nodes().Single(n => n.Id == "test-remote").ActiveMatches == 1, "MAIN restart restores durable reservations");
        var reconnect = restarted.Connect("test-remote", registration);
        restarted.Heartbeat("test-remote", reconnect, new(registration, [report]), DateTime.UtcNow);
        Check(restarted.Get(winner.ResultId)?.MatchId == winner.MatchId, "Reconnect reconciles existing match without a new ID");
        var unknown = new GameNodeMatchStatus(Guid.NewGuid().ToString("N"), "Playing", 27069, true, 1, []);
        Check(restarted.Heartbeat("test-remote", reconnect, new(registration, [report, unknown]), DateTime.UtcNow).StopMatches.Contains(unknown.MatchId),
            "Unknown reported matches are explicitly stopped");
        var completion = restarted.Heartbeat("test-remote", reconnect, new(registration, [report with { State = "Completed", ProcessActive = false }]), DateTime.UtcNow);
        Check(completion.ForgetMatches.Contains(winner.MatchId) && restarted.Nodes().Single(n => n.Id == "test-remote").Available,
            "Only confirmed process exit releases capacity and removes ephemeral payload");
        var journal = File.ReadAllText(Path.Combine(environment.ContentRootPath, "App_Data/game-nodes/assignments.json"));
        Check(!journal.Contains("76561198000000001"), "Finished assignment journal drops match personal data");
        var other = restarted.Request(result, catalog);
        restarted.Heartbeat("test-remote", reconnect, new(registration, [new(other.MatchId, "Starting", 27069, true, 0, [])]), DateTime.UtcNow);
        restarted.Heartbeat("test-remote", reconnect, new(registration, []), DateTime.UtcNow);
        Check(restarted.Get(result.ResultId)?.State == "Failed", "Missing previously observed match fails safely without replay");
        Expect(() => restarted.Connect("test-remote", registration with { ProtocolVersion = 99 }), "Protocol mismatch is rejected");
        Check(restarted.AdminNodes().Single(n => n.Node.Id == "test-remote") is { Node.Status: "Error", Error: not null }, "Protocol incompatibility is visible to admin");
        Check(!JsonSerializer.Serialize(restarted.AdminNodes()).Contains(secret) && !JsonSerializer.Serialize(restarted.Nodes()).Contains(secret),
            "Public and admin DTOs never contain credentials");
        Check(new DraftRoom().GameNodeId == "main" && JsonSerializer.Deserialize<DraftRoom>("{}")!.GameNodeId == "main", "Legacy rooms default to MAIN");
        Check(JsonSerializer.Deserialize<DraftRoom>(JsonSerializer.Serialize(new DraftRoom { GameNodeId = "test-remote" }))!.GameNodeId == "test-remote", "Room affinity survives serialization");
        Check(!abilitydraft.Services.CustomDraftPresetService.Export(new DraftRoomConfig { DraftMode = DraftMode.Custom }).Contains("GameNode", StringComparison.OrdinalIgnoreCase), "Server affinity is not a draft preset property");
        SelectionChecks();
        WorkerRecoveryChecks(temporary, result, catalog);
        await SocketChecks(restarted, registration, secret, environment.ContentRootPath, result, catalog);
    }
    private static void SelectionChecks()
    {
        GameNodeView Node(string id, string status) => new(id, id, "", false, status, status == "Full" ? 1 : 0, 1, null);
        GameNodeView[] nodes = [Node("full", "Full"), Node("offline", "Offline"), Node("good", "Online"), Node("other", "Online")];
        var pings = new Dictionary<string, double?> { ["full"] = 1, ["offline"] = 2, ["good"] = 30, ["other"] = 100 };
        Check(GameNodeSelection.Choose(nodes, pings, null, false) == "good", "Automatic selection chooses lowest available player ping");
        Check(GameNodeSelection.Choose(nodes, pings, "other", true) == "other", "Manual selection survives better ping results");
        Check(GameNodeSelection.Choose(nodes, pings, "full", true) == "good", "Unavailable manual selection falls back safely");
        pings["good"] = null;
        Check(GameNodeSelection.Choose(nodes, pings, "good", false) == "other", "Reachable server wins over missing latency");
        Check(GameNodeSelection.Choose(nodes.Take(2), pings, null, false) is null, "All busy nodes yield no selection");
        Check(GameNodeSelection.PingClass(69) == "ping-good" && GameNodeSelection.PingClass(100) == "ping-medium" &&
            GameNodeSelection.PingClass(151) == "ping-bad" && GameNodeSelection.PingClass(501) == "ping-terrible" && GameNodeSelection.PingClass(null) == "ping-unknown", "Ping colors cover requested boundaries and unknown state");
    }
    private static void WorkerRecoveryChecks(string temporary, DraftResult result, DraftResourceCatalog catalog)
    {
        var options = Options.Create(new MatchWorkerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "recover-workers") });
        var host = new Host();
        using var first = new MatchWorkerCoordinator(options, host, NullLogger<MatchWorkerCoordinator>.Instance);
        first.Request(result, catalog, false);
        first.Request(result, catalog, false);
        Check(host.Starts == 1, "Repeated StartMatch delivery launches one native worker");
        using var recovered = new MatchWorkerCoordinator(options, host, NullLogger<MatchWorkerCoordinator>.Instance);
        Check(recovered.Get(result.ResultId)?.State == "Starting" && recovered.NodeReports().Single().ProcessActive, "Agent restart recovers process identity from journal");
        recovered.Request(result, catalog, false);
        Check(host.Starts == 1, "Recovered match never launches another native process");
        File.WriteAllText(Path.Combine(host.Directory!, "local-abandoned.json"), JsonSerializer.Serialize(new[] { result.Players[0].SteamId64 }));
        recovered.Poll(DateTime.UtcNow);
        Check(recovered.Get(result.ResultId)?.State == "Completed", "Local plugin abandonment stops an empty match without MAIN credentials");
        recovered.Stop(result.ResultId);
        recovered.Request(result, catalog, false);
        Check(host.Starts == 1 && !File.Exists(Path.Combine(host.Directory!, "result.json")), "Completed StartMatch stays idempotent and private state is removed");
    }
    private static async Task SocketChecks(GameNodeCoordinator coordinator, GameNodeRegistration registration, string secret, string root,
        DraftResult result, DraftResourceCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = root });
        builder.Logging.ClearProviders(); builder.Services.AddSingleton(coordinator);
        builder.Services.AddRateLimiter(o => o.AddConcurrencyLimiter("ingame", p => { p.PermitLimit = 16; p.QueueLimit = 0; }));
        await using var app = builder.Build();
        app.UseWebSockets(); app.UseRateLimiter(); app.MapGameNodes(); app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var address = new Uri(app.Urls.Single().Replace("http:", "ws:") + "/api/game-nodes/v1/connect");
        using (var unauthorized = new ClientWebSocket())
        {
            try { await unauthorized.ConnectAsync(address, timeout.Token); throw new Exception("Unauthenticated socket accepted"); }
            catch (WebSocketException) { Check(true, "WebSocket handshake denies missing credentials"); }
        }
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Game-Node-Id", "test-remote"); socket.Options.SetRequestHeader("Authorization", "Bearer " + secret);
        await socket.ConnectAsync(address, timeout.Token);
        await GameNodeProtocol.Send(socket, new GameNodeHeartbeat(registration, []), timeout.Token);
        var commands = await GameNodeProtocol.Receive<GameNodeCommands>(socket, timeout.Token);
        Check(commands.Error is null && commands.ProtocolVersion == 1, "Authenticated persistent WebSocket registers and acknowledges heartbeat");
        socket.Abort();
        var agentPath = Environment.GetEnvironmentVariable("GAME_NODE_TEST_DLL");
        if (agentPath is { Length: > 0 })
        {
            var info = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(agentPath))!, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.GetFullPath(agentPath));
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GameNode", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
            info.Environment["GameNode__Id"] = "test-remote"; info.Environment["GameNode__Name"] = "Test remote";
            info.Environment["GameNode__CoordinatorUrl"] = app.Urls.Single().TrimEnd('/') + "/";
            info.Environment["GameNode__NodeSecret"] = secret;
            info.Environment["GameNode__StateDirectory"] = Path.Combine(root, "standalone-agent");
            info.Environment["GameNode__PublicGameHost"] = "127.0.0.1";
            info.Environment["GameNode__FirstGamePort"] = "27069";
            info.Environment["GameNode__MaxWorkers"] = "1";
            info.Environment["GameNode__Simulation"] = "true";
            info.Environment["GameNode__ProbeListenUrl"] = "http://127.0.0.1:0";
            using var process = System.Diagnostics.Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await Until(() => coordinator.Nodes().Single(n => n.Id == "test-remote").Available);
                var assigned = coordinator.Request(result with { ResultId = "standalone-smoke" }, catalog);
                await Until(() => coordinator.Get(assigned.ResultId)?.State == "Ready");
                Check(coordinator.Get(assigned.ResultId)?.Address == "127.0.0.1:27069", "Standalone simulated agent receives StartMatch and reports direct readiness over WebSocket");
                var state = Path.Combine(root, "standalone-agent/matches");
                Check(Directory.GetDirectories(state).Length == 1, "Repeated coordinator deliveries create exactly one standalone worker");
                coordinator.StopMatch(assigned.ResultId);
                await Until(() => coordinator.Get(assigned.ResultId)?.State == "Completed");
                await Until(() => !Directory.GetFiles(state, "result.json", SearchOption.AllDirectories).Any());
                Check(coordinator.Nodes().Single(n => n.Id == "test-remote").Available, "Standalone StopMatch cleans ephemeral data and releases capacity");
            }
            finally
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                var output = await stdout + await stderr;
                Check(!output.Contains(secret), "Standalone agent logs never reveal its credential");
            }
        }
        await app.StopAsync();
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Standalone Game Node integration timed out.");
            await Task.Delay(200);
        }
    }
    private sealed class Host : IMatchWorkerHost, IRecoverableMatchWorkerHost
    {
        public int Starts; public string? Directory; private Process? process;
        public IMatchWorkerProcess Start(MatchWorkerLaunch launch) { Starts++; Directory = launch.Directory; return process = new(); }
        public IMatchWorkerProcess? Recover(int pid, DateTime time) => process;
    }
    private sealed class Process : IMatchWorkerProcess
    {
        public int? ProcessId => 12345;
        public DateTime? StartedUtc { get; } = DateTime.UtcNow;
        public bool HasExited { get; private set; }
        public void Stop() => HasExited = true;
        public void Dispose() => Stop();
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Expect(Action action, string message) { try { action(); } catch (InvalidOperationException) { Check(true, message); return; } throw new Exception(message); }
}
