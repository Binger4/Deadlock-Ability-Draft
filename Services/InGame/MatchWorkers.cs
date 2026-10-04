using System.Diagnostics;
using System.Text.Json;
using AbilityDraft.Contracts;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services.InGame;

public sealed class MatchWorkerOptions
{
    public bool Enabled { get; set; }
    public string GameRoot { get; set; } = "";
    public string StateDirectory { get; set; } = "";
    public string PublicHost { get; set; } = "127.0.0.1";
    public int FirstPort { get; set; } = 27068;
    public int MaxWorkers { get; set; } = 1;
}

public sealed record MatchWorkerLaunch(string Directory, int Port, DraftResult Result, DraftResourceCatalog Catalog);
public sealed record MatchPlayerStats(string Name, string Team, string Hero, string[] Abilities, bool Bot, bool Abandoned);
public sealed record ActiveMatchStatsRecord(string Id, string Code, string Source, string Host, string Mode, bool Bots,
    string State, DateTime StartedUtc, int Connected, int Required, MatchPlayerStats[] Players, string[] Spectators);
public interface IMatchWorkerProcess : IDisposable
{
    bool HasExited { get; }
    void Stop();
}
// Only this boundary starts native processes. Tests substitute it; draft rules are never involved.
public interface IMatchWorkerHost { IMatchWorkerProcess Start(MatchWorkerLaunch launch); }

public sealed class WindowsMatchWorkerHost(IOptions<MatchWorkerOptions> options, IOptions<InGameOptions> integration,
    IOptions<DraftServerOptions> drafting) : IMatchWorkerHost
{
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
        foreach (var arg in new[] { "-dedicated", "-dev", "-insecure", "-allow_no_lobby_connect", "-nomaster",
                     "+hostport", launch.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "+map", "dl_midtown" })
            info.ArgumentList.Add(arg);
        // No command line, map, executable or path comes from a player request.
        info.Environment["ABILITYDRAFT_MATCH_DIRECTORY"] = launch.Directory;
        info.Environment["ABILITYDRAFT_HOST_DIRECTORY"] = "";
        info.Environment["ABILITYDRAFT_SERVER_KEY"] = integration.Value.ServerKey;
        info.Environment["ABILITYDRAFT_BACKEND_URL"] = drafting.Value.BackendUrl;
        info.Environment["ABILITYDRAFT_WEBSITE_URL"] = drafting.Value.WebsiteUrl;
        info.Environment["ABILITYDRAFT_ENABLE_RUNTIME"] = "1";
        info.Environment["ABILITYDRAFT_USE_WEBSITE"] = "1";
        info.Environment["ABILITYDRAFT_HOLD_PREGAME"] = "1";
        info.Environment["ABILITYDRAFT_LOCAL_MATCH_TEST"] = "0";
        info.Environment["ABILITYDRAFT_RESOURCE_CATALOG"] = Path.Combine(launch.Directory, "catalog.json");
        var process = Process.Start(info) ?? throw new InvalidOperationException("Match worker did not start.");
        return new OwnedProcess(process, launch.Directory);
    }

    internal sealed class OwnedProcess : IMatchWorkerProcess
    {
        private readonly Process process;
        private readonly Task stdout, stderr;
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

// One immutable website result -> one owned server process. Opt-in and deliberately capacity-limited.
public sealed class MatchWorkerCoordinator(IOptions<MatchWorkerOptions> options, IMatchWorkerHost host,
    ILogger<MatchWorkerCoordinator> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly Dictionary<string, Worker> workers = new();
    // A finalized player can abandon before allocation or while a failed worker is stopped.
    // Keep that revocation through retries without changing the frozen draft result.
    private readonly Dictionary<string, HashSet<string>> revoked = new();
    private sealed class Worker(string id, string directory, int port, DraftResult result, DateTime now)
    {
        public readonly string Id = id, Directory = directory;
        public readonly int Port = port;
        public readonly DraftResult Result = result;
        public readonly DateTime Created = now;
        public DateTime? Ready, Ended, EmptySince, AllConnectedSince;
        public string State = "Starting", Message = "Starting a fresh match server…";
        public IMatchWorkerProcess? Process;
        public readonly HashSet<string> Abandoned = new();
        public readonly Dictionary<string, DraftSpectator> Spectators = result.Spectators.ToDictionary(s => s.SteamId64);
        public Dictionary<string, string> Names = new();
        public int Connected;
        public bool HadPlayers;
    }
    public MatchView Request(DraftResult result, DraftResourceCatalog catalog)
    {
        lock (gate)
        {
            if (!options.Value.Enabled) throw new InvalidOperationException("Separate match servers are disabled.");
            if (workers.TryGetValue(result.ResultId, out var existing))
            {
                if (existing.State != "Failed" || existing.Process is not null) return View(existing);
                workers.Remove(result.ResultId); // Explicit host retry after the owned process is stopped.
            }
            var config = options.Value;
            if (config.MaxWorkers is < 1 or > 16 || config.FirstPort is < 1024 or > 65519 ||
                Uri.CheckHostName(config.PublicHost) is not (UriHostNameType.Dns or UriHostNameType.IPv4) ||
                config.PublicHost.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) ||
                !Path.IsPathFullyQualified(config.StateDirectory))
                throw new InvalidOperationException("Invalid match worker host, port pool or state directory.");
            RuntimePlayerId.Validate(result);
            var used = workers.Values.Where(w => w.Process is not null).Select(w => w.Port).ToHashSet();
            var port = Enumerable.Range(config.FirstPort, config.MaxWorkers).FirstOrDefault(p => !used.Contains(p));
            if (port == 0) throw new InvalidOperationException("All match servers are busy. Retry when a match finishes.");
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Path.GetFullPath(config.StateDirectory), id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result, Json));
            File.WriteAllText(Path.Combine(directory, "catalog.json"), JsonSerializer.Serialize(catalog, Json));
            File.WriteAllText(Path.Combine(directory, "owner.heartbeat"), DateTime.UtcNow.ToString("O"));
            var worker = new Worker(id, directory, port, result, DateTime.UtcNow);
            worker.Names = catalog.Names ?? new();
            if (revoked.TryGetValue(result.ResultId, out var departed)) worker.Abandoned.UnionWith(departed);
            if (result.Players.Where(p => !p.IsBot).All(p => worker.Abandoned.Contains(p.SteamId64!)))
                throw new InvalidOperationException("All players abandoned this result. Create a new draft.");
            WriteAbandoned(worker);
            WriteSpectators(worker);
            workers.Add(result.ResultId, worker); // Reserve before starting; duplicate requests cannot launch twice.
            try { worker.Process = host.Start(new(directory, port, result, catalog)); }
            catch (Exception ex)
            {
                worker.State = "Failed"; worker.Message = "Match server could not start. Check server logs.";
                worker.Ended = DateTime.UtcNow;
                logger.LogError(ex, "Match worker {Match} failed to launch", id);
            }
            logger.LogInformation("Match worker {Match} allocated for result {Result}", id, result.ResultId);
            return View(worker);
        }
    }
    public MatchView? Get(string resultId) { lock (gate) return workers.TryGetValue(resultId, out var worker) ? View(worker) : null; }
    public IReadOnlyList<ActiveMatchStatsRecord> GetActiveMatchStats()
    {
        lock (gate) return workers.Values.Where(w => w.Process is not null && w.State is "Starting" or "Ready" or "Playing")
            .OrderByDescending(w => w.Created).Select(w => new ActiveMatchStatsRecord(w.Id, w.Result.RoomCode, w.Result.Source,
                w.Result.Source == "public" ? "none" : w.Result.HostName, w.Result.DraftMode, w.Result.BotsEnabled,
                w.State, w.Created, w.Connected, w.Result.Players.Count(p => !p.IsBot && !w.Abandoned.Contains(p.SteamId64!)),
                w.Result.Players.Select(p => new MatchPlayerStats(p.DisplayName, p.Team, w.Names.GetValueOrDefault(p.HeroKey) ?? p.HeroKey,
                    p.Slots.OrderBy(s => s.Slot).Select(s => w.Names.GetValueOrDefault(s.AbilityKey) ?? s.AbilityKey).ToArray(), p.IsBot,
                    !p.IsBot && w.Abandoned.Contains(p.SteamId64!))).ToArray(),
                w.Spectators.Values.Where(s => !w.Abandoned.Contains(s.SteamId64)).Select(s => s.DisplayName).ToArray())).ToArray();
    }
    public void AdmitSpectator(string resultId, DraftSpectator spectator)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(resultId, out var worker) || worker.State is not ("Starting" or "Ready" or "Playing")) return;
            if (worker.Result.IsPlayer(spectator.SteamId64)) throw new InvalidOperationException("A drafted player cannot switch to a spectator seat in this match.");
            if (worker.Spectators.TryGetValue(spectator.SteamId64, out var previous) && previous == spectator && !worker.Abandoned.Contains(spectator.SteamId64)) return;
            var spectators = worker.Spectators.Values.Where(s => s.SteamId64 != spectator.SteamId64).Append(spectator).ToArray();
            RuntimePlayerId.Validate(worker.Result with { Spectators = spectators });
            if (spectators.Length > 64) throw new InvalidOperationException("The spectator list is full.");
            worker.Spectators[spectator.SteamId64] = spectator;
            worker.Abandoned.Remove(spectator.SteamId64);
            revoked.GetValueOrDefault(resultId)?.Remove(spectator.SteamId64);
            WriteSpectators(worker);
            WriteAbandoned(worker);
            logger.LogInformation("Spectator admission updated for match {Match}", worker.Id);
        }
    }
    private static void WriteSpectators(Worker worker)
    {
        var file = Path.Combine(worker.Directory, "spectators.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(worker.Spectators.Values, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }
    public IReadOnlyDictionary<string, string> States()
    {
        lock (gate) return workers.ToDictionary(p => p.Key, p => p.Value.State);
    }
    public void Abandon(string resultId, string steam)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(resultId, out var worker))
            {
                if (!revoked.TryGetValue(resultId, out var identities)) revoked[resultId] = identities = new();
                identities.Add(steam);
                return; // The API has already verified membership in this result.
            }
            if (!worker.Result.IsPlayer(steam) && !worker.Spectators.ContainsKey(steam)) throw new InvalidOperationException("Not a match participant.");
            worker.Abandoned.Add(steam);
            revoked[resultId] = new(worker.Abandoned);
            WriteAbandoned(worker);
            if (worker.Result.Players.Where(p => !p.IsBot).All(p => worker.Abandoned.Contains(p.SteamId64!)))
                Finish(worker, "Completed", "All players abandoned the match.", DateTime.UtcNow);
        }
    }
    private static void WriteAbandoned(Worker worker)
    {
        var file = Path.Combine(worker.Directory, "abandoned.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(worker.Abandoned, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }
    private MatchView View(Worker w) => new(w.Id, w.Result.ResultId, w.State,
        w.State is "Ready" or "Playing" ? options.Value.PublicHost + ":" + w.Port : null, w.Message);

    // Exposed for deterministic watchdog tests. Reads bounded, atomically replaced private files.
    public void Poll(DateTime now)
    {
        lock (gate)
        {
            foreach (var worker in workers.Values.Where(w => w.Process is not null))
            {
                try
                {
                    if (File.Exists(Path.Combine(worker.Directory, "ended.json")))
                    {
                        Finish(worker, "Completed", "Match ended. Create a new draft to play again.", now);
                        continue;
                    }
                    if (worker.Process!.HasExited)
                    {
                        Finish(worker, worker.State == "Completed" ? "Completed" : "Failed",
                            worker.State == "Completed" ? "Match complete." : "Match server stopped unexpectedly.", now);
                        continue;
                    }
                    var file = new FileInfo(Path.Combine(worker.Directory, "status.json"));
                    if (file.Exists && file.Length <= 4096)
                    {
                        var report = JsonSerializer.Deserialize<MatchWorkerReport>(File.ReadAllText(file.FullName), Json);
                        if (report?.ResultId != worker.Result.ResultId || report.Utc > now.AddSeconds(10) ||
                            report.Required != worker.Result.Players.Count(p => !p.IsBot) || report.Connected < 0 || report.Connected > report.Required ||
                            report.State is not ("Starting" or "Ready" or "Playing" or "Completed" or "Failed"))
                            throw new InvalidOperationException("Invalid match worker report.");
                        if (report.State == "Failed") { Finish(worker, "Failed", "Drafted loadout preparation failed. Check match logs.", now); continue; }
                        worker.Connected = report.Connected;
                        worker.HadPlayers |= report.Connected > 0 || report.State == "Playing" || report.EmptySinceUtc is not null;
                        if (report.State == "Completed")
                        {
                            worker.Ended ??= now;
                            worker.State = "Completed"; worker.Message = "Match complete.";
                            if (now - worker.Ended >= TimeSpan.FromSeconds(20)) Finish(worker, "Completed", worker.Message, now);
                            continue;
                        }
                        if (report.State is "Ready" or "Playing")
                        {
                            if (worker.State == "Playing" && report.State == "Ready")
                                throw new InvalidOperationException("Match worker reset unexpectedly.");
                            worker.Ready ??= now;
                            worker.State = report.State;
                            var expected = report.Required - worker.Result.Players.Count(p => !p.IsBot && worker.Abandoned.Contains(p.SteamId64!));
                            if (report.State == "Ready" && expected > 0 && report.Connected == expected)
                                worker.AllConnectedSince ??= now;
                            else worker.AllConnectedSince = null;
                            worker.Message = report.State == "Playing" ? "Match in progress" : $"Match server ready · {report.Connected}/{report.Required} players connected";
                        }
                        if (now - report.Utc > TimeSpan.FromSeconds(30)) { Finish(worker, "Failed", "Match server stopped responding.", now); continue; }
                        // Bots/spectators are not substitutes for a connected drafted player.
                        if (worker.HadPlayers && report.Connected == 0)
                        {
                            if (report.EmptySinceUtc is { } since && since <= report.Utc && since >= worker.Created)
                                worker.EmptySince = since;
                            worker.EmptySince ??= now;
                            if (now - worker.EmptySince >= TimeSpan.FromMinutes(2))
                            {
                                Finish(worker, "Completed", "Nobody rejoined within two minutes. The match has ended.", now);
                                continue;
                            }
                        }
                        else worker.EmptySince = null;
                    }
                    if (worker.Ready is null && now - worker.Created > TimeSpan.FromMinutes(2))
                        Finish(worker, "Failed", "Match server startup timed out.", now);
                    else if (worker.State == "Ready" && !worker.HadPlayers && now - worker.Ready > TimeSpan.FromMinutes(3))
                        Finish(worker, "Failed", "Not all drafted players joined the match in time.", now);
                    else if (worker.State == "Ready" && worker.AllConnectedSince is { } connected && now - connected > TimeSpan.FromSeconds(90))
                        Finish(worker, "Failed", "Match preparation did not complete. Check match logs.", now);
                    else if (now - worker.Created > TimeSpan.FromHours(2))
                        Finish(worker, "Failed", "Match server time limit reached.", now);
                    if (worker.Process is not null)
                        File.WriteAllText(Path.Combine(worker.Directory, "owner.heartbeat"), now.ToString("O"));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Match worker {Match} monitoring failed", worker.Id);
                    Finish(worker, "Failed", "Match server monitoring failed. Check server logs.", now);
                }
            }
        }
    }
    private void Finish(Worker worker, string state, string message, DateTime now)
    {
        if (state == "Completed") File.WriteAllText(Path.Combine(worker.Directory, "ended.json"), message);
        worker.Process?.Dispose(); worker.Process = null;
        worker.State = state; worker.Message = message; worker.Ended ??= now;
        logger.LogInformation("Match worker {Match} stopped: {State}", worker.Id, state);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) Poll(DateTime.UtcNow);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        lock (gate) foreach (var worker in workers.Values.Where(w => w.Process is not null))
            Finish(worker, "Failed", "Match hosting stopped.", DateTime.UtcNow);
    }
}
