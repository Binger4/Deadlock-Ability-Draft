using abilitydraft.Services.InGame;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
}
