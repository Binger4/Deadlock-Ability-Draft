using abilitydraft.Services.InGame;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

static class DraftServerChecks
{
    public static void Run(string temporary)
    {
        foreach (var invalid in new[] {
            new DraftServerOptions { Enabled = true },
            new DraftServerOptions { Enabled = true, StateDirectory = "relative/drafting" },
            new DraftServerOptions { Enabled = true, StateDirectory = temporary, CustomPort = 0 },
            new DraftServerOptions { Enabled = true, StateDirectory = temporary, PublicPort = 65536 },
            new DraftServerOptions { Enabled = true, StateDirectory = temporary, PublicPort = 27067 }
        })
        {
            var rejectedHost = new Host();
            using var rejected = new DraftServerSupervisor(Options.Create(invalid), Options.Create(new InGameOptions { Enabled = true }), rejectedHost, NullLogger<DraftServerSupervisor>.Instance);
            try { rejected.Poll(DateTime.UtcNow); throw new Exception("Invalid configuration was accepted."); }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("InGame:DraftServers:") && rejectedHost.Launched.Count == 0,
                    "Invalid drafting configuration identifies the setting before launching a process");
            }
        }
        var config = new DraftServerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "drafting") };
        var host = new Host();
        using var supervisor = new DraftServerSupervisor(Options.Create(config), Options.Create(new InGameOptions { Enabled = true }), host, NullLogger<DraftServerSupervisor>.Instance);
        var now = DateTime.UtcNow;
        supervisor.Poll(now);
        Check(host.Launched.Select(x => x.Role).Order().SequenceEqual(new[] { "custom", "public" }) && host.Launched.Select(x => x.Port).Distinct().Count() == 2,
            "Website starts separate owned public and custom drafting servers");
        supervisor.Poll(now.AddSeconds(1));
        Check(host.Launched.Count == 2, "Supervisor does not launch duplicate live drafting servers");
        host.Processes[0].Stop();
        supervisor.Poll(now.AddSeconds(2));
        supervisor.Poll(now.AddSeconds(8));
        Check(host.Launched.Count == 3 && host.Launched[2].Role == "custom" && !host.Processes[1].HasExited,
            "Closing a drafting server restarts only that server while the website stays on");
        var status = Path.Combine(host.Launched[1].Directory, "status.json");
        File.WriteAllText(status, "{}"); File.SetLastWriteTimeUtc(status, now.AddSeconds(9));
        supervisor.Poll(now.AddSeconds(9));
        Check(supervisor.Status().Single(s => s.Role == "public").Ready, "Drafting readiness requires a fresh plugin heartbeat");
        supervisor.StopAsync(default).GetAwaiter().GetResult();
        Check(host.Processes.All(p => p.HasExited), "Website shutdown stops its owned drafting servers");
        var incompatibleHost = new Host();
        using var incompatible = new DraftServerSupervisor(Options.Create(WithState(Path.Combine(temporary, "incompatible"))),
            Options.Create(new InGameOptions { Enabled = true }), incompatibleHost, NullLogger<DraftServerSupervisor>.Instance);
        incompatible.Poll(now);
        File.WriteAllText(Path.Combine(incompatibleHost.Launched[0].Directory, "server.log"),
            "[bootstrap] Deadlock build 99999 is not supported by this version of Deadworks");
        incompatibleHost.Processes[0].Stop();
        incompatible.Poll(now.AddSeconds(2)); incompatible.Poll(now.AddSeconds(8));
        Check(incompatibleHost.Launched.Count == 2, "An incompatible Deadworks build does not enter a five-second restart loop");
        incompatible.Poll(now.AddSeconds(63));
        Check(incompatibleHost.Launched.Count == 3, "An incompatible build is retried after the maintenance backoff");

        var pluginHost = new Host();
        using var pluginFailure = new DraftServerSupervisor(Options.Create(WithState(Path.Combine(temporary, "plugin-failure"))),
            Options.Create(new InGameOptions { Enabled = true }), pluginHost, NullLogger<DraftServerSupervisor>.Instance);
        pluginFailure.Poll(now);
        File.WriteAllText(Path.Combine(pluginHost.Launched[0].Directory, "server.log"),
            "[PluginLoader] Failed to load AbilityDraft.Deadworks.dll: Unknown Deadlock server build: update the gameplay compatibility bridge");
        pluginFailure.Poll(now.AddSeconds(2)); pluginFailure.Poll(now.AddSeconds(8));
        Check(pluginHost.Processes[0].HasExited && pluginHost.Launched.Count == 2,
            "A failed gameplay plugin is stopped immediately and receives maintenance backoff instead of a five-second restart loop");
        pluginFailure.Poll(now.AddSeconds(63));
        pluginFailure.Poll(now.AddSeconds(65));
        Check(pluginHost.Launched.Count == 3 && !pluginHost.Processes[2].HasExited,
            "A previous process's stale plugin error cannot stop the replacement server");

        static DraftServerOptions WithState(string state) => new() { Enabled = true, StateDirectory = state };
    }
    public static async Task RunStartupLifetime(string temporary)
    {
        var host = new Host();
        using var supervisor = new DraftServerSupervisor(
            Options.Create(new DraftServerOptions { Enabled = true, StateDirectory = Path.Combine(temporary, "startup-drafting") }),
            Options.Create(new InGameOptions { Enabled = true }), host, NullLogger<DraftServerSupervisor>.Instance, new Lifetime());
        await supervisor.StartAsync(default);
        await Task.Delay(50);
        Check(host.Launched.Count == 0, "Drafting processes wait until the HTTP host has started successfully");
        Check(supervisor.Status() is { Length: 2 } pending && pending.All(s => !s.Running && !s.Ready),
            "The initial HTTP hosting status reports enabled lobbies as pending before the first supervision tick");
        await supervisor.StopAsync(default);
        Check(supervisor.ExecuteTask is { IsCompletedSuccessfully: true }, "Cancelling draft supervision during startup exits without a background-service exception");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private sealed class Host : IDraftServerHost
    {
        public List<DraftServerLaunch> Launched = [];
        public List<Process> Processes = [];
        public IMatchWorkerProcess Start(DraftServerLaunch launch) { Launched.Add(launch); var p = new Process(); Processes.Add(p); return p; }
    }
    private sealed class Process : IMatchWorkerProcess
    {
        public bool HasExited { get; private set; }
        public void Stop() => HasExited = true;
        public void Dispose() => Stop();
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
