using System.Diagnostics;
using Microsoft.Extensions.Options;
namespace abilitydraft.Services.InGame;

public sealed class DeadworksMatchWorkerHost(IOptions<MatchWorkerOptions> options) : IMatchWorkerHost, IRecoverableMatchWorkerHost
{
    public IMatchWorkerProcess? Recover(int pid, DateTime startedUtc)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            var expected = Path.GetFullPath(Path.Combine(options.Value.GameRoot, "game/bin/win64/deadworks.exe"));
            if (process.HasExited || process.StartTime.ToUniversalTime() != startedUtc ||
                !string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
            { process.Dispose(); return null; }
            return new OwnedProcess(process);
        }
        catch (ArgumentException) { return null; }
    }
    public IMatchWorkerProcess Start(MatchWorkerLaunch launch)
    {
        var root = Path.GetFullPath(options.Value.GameRoot);
        var executable = Path.Combine(root, "game/bin/win64/deadworks.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(executable))
            throw new InvalidOperationException("Configure an installed Windows Deadworks server before enabling match workers.");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Check both protocols before launching; the game still owns the final bind.
        using (var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Any, launch.Port))) { }
        var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, launch.Port);
        try { tcp.Start(); } finally { tcp.Stop(); }
        foreach (var arg in new[] { "-dedicated", "-dev", "-insecure", "-allow_no_lobby_connect", "-nomaster",
                     "+hostport", launch.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "+map", "dl_midtown" })
            info.ArgumentList.Add(arg);
        // No command line, map, executable or path comes from a player request.
        info.Environment["ABILITYDRAFT_MATCH_DIRECTORY"] = launch.Directory;
        info.Environment["ABILITYDRAFT_HOST_DIRECTORY"] = "";
        info.Environment["ABILITYDRAFT_SERVER_KEY"] = options.Value.ServerKey;
        info.Environment["ABILITYDRAFT_BACKEND_URL"] = options.Value.BackendUrl;
        info.Environment["ABILITYDRAFT_WEBSITE_URL"] = options.Value.WebsiteUrl;
        info.Environment["ABILITYDRAFT_ENABLE_RUNTIME"] = "1";
        info.Environment["ABILITYDRAFT_USE_WEBSITE"] = "1";
        info.Environment["ABILITYDRAFT_HOLD_PREGAME"] = "1";
        info.Environment["ABILITYDRAFT_LOCAL_MATCH_TEST"] = "0";
        info.Environment["ABILITYDRAFT_RESOURCE_CATALOG"] = Path.Combine(launch.Directory, "catalog.json");
        // A match plugin has no reason to inherit the agent's or website's environment secrets.
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GameNode", StringComparison.OrdinalIgnoreCase) ||
                     k.StartsWith("InGame__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("AdminAuth__", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Match worker did not start.");
        return new OwnedProcess(process, launch.Directory);
    }

    public sealed class OwnedProcess : IMatchWorkerProcess
    {
        private readonly Process process;
        private readonly Task stdout, stderr;
        public OwnedProcess(Process process) { this.process = process; stdout = stderr = Task.CompletedTask; }
        public OwnedProcess(Process process, string directory)
        {
            this.process = process;
            stdout = Copy(process.StandardOutput.BaseStream, Path.Combine(directory, "server.log"));
            stderr = Copy(process.StandardError.BaseStream, Path.Combine(directory, "server.err.log"));
        }
        private static async Task Copy(Stream stream, string path)
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer)) != 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, length));
                await file.FlushAsync(); // Keep startup/gameplay evidence readable while the worker is alive.
            }
        }
        public bool HasExited => process.HasExited;
        public int? ProcessId => process.Id;
        public DateTime? StartedUtc => process.StartTime.ToUniversalTime();
        public void Stop() { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        public void Dispose()
        {
            Stop();
            process.WaitForExit(5000);
            try { Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            process.Dispose();
        }
    }
}

