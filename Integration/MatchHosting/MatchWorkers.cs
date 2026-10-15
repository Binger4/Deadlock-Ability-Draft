using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;
using AbilityDraft.Contracts;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services.InGame;

public sealed class MatchWorkerOptions
{
    public string ServerKey { get; set; } = "";
    public string BackendUrl { get; set; } = "http://127.0.0.1:5050/";
    public string WebsiteUrl { get; set; } = "http://localhost:5050/";
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
    int? ProcessId => null;
    DateTime? StartedUtc => null;
    bool HasExited { get; }
    void Stop();
}
// Only this boundary starts native processes. Tests substitute it; draft rules are never involved.
public interface IMatchWorkerHost { IMatchWorkerProcess Start(MatchWorkerLaunch launch); }
public interface IRecoverableMatchWorkerHost { IMatchWorkerProcess? Recover(int pid, DateTime startedUtc); }

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
    private bool recovered;
    private DateTime quarantineUntil;
    private void Quarantine()
    {
        quarantineUntil = DateTime.UtcNow.AddSeconds(90);
        var path = Path.Combine(options.Value.StateDirectory, "quarantine.until");
        File.WriteAllText(path + ".tmp", quarantineUntil.ToString("O"));
        File.Move(path + ".tmp", path, true);
    }
    private sealed record Recovery(string Id, string ResultId, int Port, DateTime Created, string State,
        int? Pid, DateTime? StartedUtc);
    private void SaveWorker(Worker w)
    {
        var file = Path.Combine(w.Directory, "worker.json");
        using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new Recovery(w.Id, w.Result.ResultId, w.Port,
                w.Created, w.State, w.Process?.ProcessId, w.Process?.StartedUtc), Json);
            stream.Flush(true);
        }
        File.Move(file + ".tmp", file, true);
    }
    private void Recover()
    {
        if (recovered) return;
        recovered = true;
        if (!options.Value.Enabled || !Path.IsPathFullyQualified(options.Value.StateDirectory) || !Directory.Exists(options.Value.StateDirectory)) return;
        var quarantine = Path.Combine(options.Value.StateDirectory, "quarantine.until");
        if (File.Exists(quarantine)) quarantineUntil = DateTime.Parse(File.ReadAllText(quarantine), null, System.Globalization.DateTimeStyles.RoundtripKind);
        foreach (var directory in Directory.GetDirectories(options.Value.StateDirectory))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            var file = Path.Combine(directory, "worker.json");
            if (!File.Exists(file)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(file), Json)!;
                var terminal = GameNodeProtocol.Terminal(saved.State);
                var result = terminal && saved.Pid is null ? new DraftResult(1, saved.ResultId, "", saved.Created, saved.Created, []) :
                    JsonSerializer.Deserialize<DraftResult>(File.ReadAllText(Path.Combine(directory, "result.json")), Json)!;
                var w = new Worker(saved.Id, directory, saved.Port, result, saved.Created) { State = saved.State };
                if (saved.Pid is { } pid && saved.StartedUtc is { } time && host is IRecoverableMatchWorkerHost recoverable)
                    w.Process = recoverable.Recover(pid, time);
                if (w.Process is null && (!terminal || saved.Pid is not null))
                {
                    // A crash between process launch and journal commit must never re-use its port immediately.
                    Quarantine();
                    if (!terminal) { w.State = "Failed"; w.Message = "Match hosting restarted; create a new match after recovery."; }
                }
                if (w.Process is not null)
                {
                    File.WriteAllText(Path.Combine(directory, "owner.heartbeat"), DateTime.UtcNow.ToString("O"));
                    w.Ended = terminal ? DateTime.UtcNow : null;
                    w.Ready = w.State is "Ready" or "Playing" ? DateTime.UtcNow : null;
                    var admissions = Path.Combine(directory, "spectators.json");
                    if (File.Exists(admissions)) foreach (var spectator in JsonSerializer.Deserialize<DraftSpectator[]>(File.ReadAllText(admissions), Json) ?? [])
                        w.Spectators[spectator.SteamId64] = spectator;
                    var abandoned = Path.Combine(directory, "abandoned.json");
                    if (File.Exists(abandoned)) w.Abandoned.UnionWith(JsonSerializer.Deserialize<string[]>(File.ReadAllText(abandoned), Json) ?? []);
                }
                workers[result.ResultId] = w;
                if (w.Process is null)
                {
                    SaveWorker(w);
                    if (DateTime.UtcNow >= quarantineUntil) CleanupState(w);
                    // If process recovery failed, leave its files intact until the owner watchdog expires.
                }
            }
            catch (Exception ex)
            {
                Quarantine();
                logger.LogError(ex, "Could not recover worker journal {Directory}; port pool quarantined", directory);
                // Do not skip an unreadable journal and risk launching a duplicate.
                recovered = false;
                throw new InvalidOperationException("Match recovery journal is unreadable. Restore it before enabling workers.", ex);
            }
        }
    }
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
        public bool StateCleaned;
    }
    public MatchView Request(DraftResult result, DraftResourceCatalog catalog, bool retryFailed = true)
    {
        lock (gate)
        {
            Recover();
            if (!options.Value.Enabled) throw new InvalidOperationException("Separate match servers are disabled.");
            if (workers.TryGetValue(result.ResultId, out var existing))
            {
                if (!retryFailed || existing.State != "Failed" || existing.Process is not null) return View(existing);
            }
            var config = options.Value;
            if (DateTime.UtcNow < quarantineUntil) throw new InvalidOperationException("Match ports are recovering from a restart. Retry in 90 seconds.");
            if (existing is not null)
            {
                // Retire the old attempt's journal before a replacement; directory enumeration on
                // restart must never let an older failed attempt shadow the new live worker.
                File.Delete(Path.Combine(existing.Directory, "worker.json"));
                workers.Remove(result.ResultId);
            }
            if (config.MaxWorkers is < 1 or > 16 || config.FirstPort is < 1024 or > 65535 || config.FirstPort + config.MaxWorkers - 1 > 65535 ||
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
            if (revoked.TryGetValue(result.ResultId, out var allDeparted) && result.Players.Where(p => !p.IsBot).All(p => allDeparted.Contains(p.SteamId64!)))
                throw new InvalidOperationException("All players abandoned this result. Create a new draft.");
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
            SaveWorker(worker); // Record intent before launching a native process.
            try { worker.Process = host.Start(new(directory, port, result, catalog)); SaveWorker(worker); }
            catch (Exception ex)
            {
                worker.State = "Failed"; worker.Message = "Match server could not start. Check server logs.";
                worker.Ended = DateTime.UtcNow;
                logger.LogError(ex, "Match worker {Match} failed to launch", id);
                worker.Process?.Dispose(); worker.Process = null; SaveWorker(worker); CleanupState(worker);
            }
            logger.LogInformation("Match worker {Match} allocated port {Port} for result {Result}", id, port, result.ResultId);
            return View(worker);
        }
    }
    public MatchView? Get(string resultId) { lock (gate) { Recover(); return workers.TryGetValue(resultId, out var worker) ? View(worker) : null; } }
    public GameNodeMatchStatus[] NodeReports()
    {
        lock (gate)
        {
            Recover();
            return workers.Values.Select(w => new GameNodeMatchStatus(w.Result.ResultId, w.State, w.Port,
                w.Process is not null, w.Connected, w.Abandoned.ToArray())).ToArray();
        }
    }
    public void Stop(string resultId)
    {
        lock (gate) { Recover(); if (workers.TryGetValue(resultId, out var w) && w.Process is not null)
            Finish(w, "Completed", "Stopped by the Game Node Coordinator.", DateTime.UtcNow); }
    }
    public void Forget(string resultId)
    {
        lock (gate)
        {
            Recover();
            if (!workers.TryGetValue(resultId, out var w) || w.Process is not null || DateTime.UtcNow < quarantineUntil) return;
            CleanupState(w);
            // Directory was created by this coordinator and contains only its own files.
            foreach (var name in new[] { "worker.json", "ended.json", "owner.heartbeat" }) File.Delete(Path.Combine(w.Directory, name));
            workers.Remove(resultId); revoked.Remove(resultId);
        }
    }
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
            var localAbandoned = Path.Combine(worker.Directory, "local-abandoned.json");
            if (File.Exists(localAbandoned))
            {
                var identities = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(localAbandoned), Json) ?? [];
                if (identities.Remove(spectator.SteamId64))
                {
                    File.WriteAllText(localAbandoned + ".tmp", JsonSerializer.Serialize(identities, Json));
                    File.Move(localAbandoned + ".tmp", localAbandoned, true);
                }
            }
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
        lock (gate) { Recover(); return workers.ToDictionary(p => p.Key, p => p.Value.State); }
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
    public string[] GetAbandoned(string resultId)
    {
        lock (gate) return revoked.TryGetValue(resultId, out var identities) ? identities.ToArray() : [];
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
            Recover();
            if (now >= quarantineUntil)
                foreach (var ended in workers.Values.Where(w => w.Process is null && GameNodeProtocol.Terminal(w.State))) CleanupState(ended);
            foreach (var worker in workers.Values.Where(w => w.Process is not null))
            {
                try
                {
                    var localAbandon = Path.Combine(worker.Directory, "local-abandoned.json");
                    if (File.Exists(localAbandon) && new FileInfo(localAbandon).Length < 16384)
                        foreach (var steam in JsonSerializer.Deserialize<string[]>(File.ReadAllText(localAbandon), Json) ?? [])
                            if ((worker.Result.IsPlayer(steam) || worker.Spectators.ContainsKey(steam)) && !worker.Abandoned.Contains(steam))
                                Abandon(worker.Result.ResultId, steam);
                    if (worker.Process is null) continue;
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
                            if (worker.State != report.State) logger.LogInformation("Match {Match} is {State}", worker.Result.ResultId, report.State);
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
                    {
                        File.WriteAllText(Path.Combine(worker.Directory, "owner.heartbeat"), now.ToString("O"));
                        SaveWorker(worker);
                    }
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
        SaveWorker(worker);
        CleanupState(worker);
        logger.LogInformation("Match worker {Match} stopped: {State}", worker.Id, state);
    }
    private static void CleanupState(Worker worker)
    {
        if (worker.StateCleaned) return;
        foreach (var name in new[] { "result.json", "catalog.json", "spectators.json", "abandoned.json", "local-abandoned.json", "status.json" })
            File.Delete(Path.Combine(worker.Directory, name));
        worker.StateCleaned = true;
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
