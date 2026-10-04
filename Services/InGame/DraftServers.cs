using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services.InGame;

public sealed class DraftServerOptions
{
    public bool Enabled { get; set; }
    public string GameRoot { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string BackendUrl { get; set; } = "http://127.0.0.1:5050/";
    public string WebsiteUrl { get; set; } = "http://localhost:5050/";
    public int CustomPort { get; set; } = 27067;
    public int PublicPort { get; set; } = 27068;
}
public sealed record DraftServerLaunch(string Role, string Directory, int Port);
public sealed record DraftServerStatus(string Role, int Port, bool Running, bool Ready);
public interface IDraftServerHost { IMatchWorkerProcess Start(DraftServerLaunch launch); }

public sealed class WindowsDraftServerHost(IOptions<DraftServerOptions> options, IOptions<InGameOptions> integration) : IDraftServerHost
{
    public IMatchWorkerProcess Start(DraftServerLaunch launch)
    {
        var config = options.Value;
        var executable = Path.Combine(Path.GetFullPath(config.GameRoot), "game/bin/win64/deadworks.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(executable) || integration.Value.ServerKey.Length < 32)
            throw new InvalidOperationException("Configure the installed Deadworks server and private integration key.");
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Any(p => p.Port == launch.Port))
            throw new InvalidOperationException("Drafting port is already in use. Stop the old standalone drafting server.");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "-dedicated", "-dev", "-insecure", "-allow_no_lobby_connect", "-nomaster",
                     "+hostport", launch.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "+map", "dl_midtown" })
            info.ArgumentList.Add(arg);
        info.Environment["ABILITYDRAFT_BACKEND_URL"] = config.BackendUrl;
        info.Environment["ABILITYDRAFT_WEBSITE_URL"] = config.WebsiteUrl;
        info.Environment["ABILITYDRAFT_SERVER_KEY"] = integration.Value.ServerKey;
        info.Environment["ABILITYDRAFT_HOST_DIRECTORY"] = launch.Directory;
        info.Environment["ABILITYDRAFT_SERVER_ROLE"] = launch.Role;
        info.Environment["ABILITYDRAFT_MATCH_DIRECTORY"] = "";
        info.Environment["ABILITYDRAFT_USE_WEBSITE"] = "1";
        info.Environment["ABILITYDRAFT_SEPARATE_MATCHES"] = "1";
        info.Environment["ABILITYDRAFT_HOLD_PREGAME"] = "1";
        info.Environment["ABILITYDRAFT_LOCAL_MATCH_TEST"] = "0";
        info.Environment["ABILITYDRAFT_ENABLE_RUNTIME"] = "0"; // Only match workers manipulate heroes.
        var process = Process.Start(info) ?? throw new InvalidOperationException("Drafting server did not start.");
        return new WindowsMatchWorkerHost.OwnedProcess(process, launch.Directory);
    }
}

// Owned by the website lifetime, never by a player's console or a launcher.
public sealed class DraftServerSupervisor(IOptions<DraftServerOptions> options, IOptions<InGameOptions> integration,
    IDraftServerHost host, ILogger<DraftServerSupervisor> logger) : BackgroundService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> servers = new();
    private sealed class Entry(DraftServerLaunch launch)
    {
        public readonly DraftServerLaunch Launch = launch;
        public IMatchWorkerProcess? Process;
        public DateTime Started, RetryAt;
        public bool Ready;
    }
    public DraftServerStatus[] Status()
    {
        lock (gate) return servers.Values.Select(s => new DraftServerStatus(s.Launch.Role, s.Launch.Port,
            s.Process is { HasExited: false }, s.Ready)).ToArray();
    }
    public void Poll(DateTime now)
    {
        if (!options.Value.Enabled || !integration.Value.Enabled) return;
        lock (gate)
        {
            var config = options.Value;
            if (!Path.IsPathFullyQualified(config.StateDirectory))
                throw new InvalidOperationException("InGame:DraftServers:StateDirectory must be an absolute private directory path.");
            if (config.CustomPort is < 1024 or > 65535)
                throw new InvalidOperationException("InGame:DraftServers:CustomPort must be between 1024 and 65535.");
            if (config.PublicPort is < 1024 or > 65535)
                throw new InvalidOperationException("InGame:DraftServers:PublicPort must be between 1024 and 65535.");
            if (config.CustomPort == config.PublicPort)
                throw new InvalidOperationException("InGame:DraftServers:CustomPort and PublicPort must be different.");
            if (servers.Count == 0)
                foreach (var pair in new[] { ("custom", config.CustomPort), ("public", config.PublicPort) })
                    servers[pair.Item1] = new(new(pair.Item1, Path.Combine(config.StateDirectory, pair.Item1), pair.Item2));
            foreach (var entry in servers.Values)
            {
                var directory = entry.Launch.Directory;
                if (entry.Process is not null)
                {
                    var status = new FileInfo(Path.Combine(directory, "status.json"));
                    entry.Ready = !entry.Process.HasExited && status.Exists && status.LastWriteTimeUtc >= entry.Started && now - status.LastWriteTimeUtc < TimeSpan.FromSeconds(30);
                    if (entry.Process.HasExited || now - entry.Started > TimeSpan.FromMinutes(2) && !entry.Ready)
                    {
                        entry.Process.Dispose(); entry.Process = null; entry.Ready = false;
                        entry.RetryAt = now.AddSeconds(5);
                        logger.LogWarning("Drafting server {Role} stopped; restarting in five seconds", entry.Launch.Role);
                    }
                }
                if (entry.Process is null && now >= entry.RetryAt)
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        File.WriteAllText(Path.Combine(directory, "owner.heartbeat"), now.ToString("O"));
                        entry.Started = now;
                        entry.Process = host.Start(entry.Launch);
                        logger.LogInformation("Website started {Role} drafting server on port {Port}", entry.Launch.Role, entry.Launch.Port);
                    }
                    catch (Exception ex)
                    {
                        entry.RetryAt = now.AddSeconds(15);
                        logger.LogError(ex, "Could not start {Role} drafting server; will retry", entry.Launch.Role);
                    }
                }
                if (entry.Process is not null) File.WriteAllText(Path.Combine(directory, "owner.heartbeat"), now.ToString("O"));
            }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { Poll(DateTime.UtcNow); }
            catch (Exception ex) { logger.LogError(ex, "Draft server supervision failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        lock (gate) foreach (var entry in servers.Values) { entry.Process?.Dispose(); entry.Process = null; entry.Ready = false; }
    }
}
