using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AbilityDraft.Contracts;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services.InGame;

public sealed class MainGameNodeOptions
{
    public string Name { get; set; } = "Local server";
    public string Region { get; set; } = "Local";
    public string PublicProbeUrl { get; set; } = "/game-nodes/ping";
}
public sealed class RemoteGameNodeOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public string Secret { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string PublicGameHost { get; set; } = "";
    public string? PublicProbeUrl { get; set; }
    public int FirstGamePort { get; set; } = 27069;
    public int MaxWorkers { get; set; } = 4;
}
public sealed class GameNodeOptions
{
    public string StateDirectory { get; set; } = "App_Data/game-nodes";
    public int HeartbeatTimeoutSeconds { get; set; } = 30;
    public RemoteGameNodeOptions[] RemoteNodes { get; set; } = [];
}

// One lock owns remote assignments, session fencing and capacity. No room locks are acquired here.
public sealed class GameNodeCoordinator : BackgroundService
{
    private readonly object gate = new();
    private readonly MatchWorkerCoordinator local;
    private readonly MatchWorkerOptions localOptions;
    private readonly MainGameNodeOptions main;
    private readonly GameNodeOptions options;
    private readonly bool integrationEnabled;
    private readonly string journal;
    private readonly DateTime started = DateTime.UtcNow;
    private readonly Dictionary<string, Remote> remotes;
    private readonly Dictionary<string, Assignment> assignments;
    private readonly Queue<(string ResultId, string Steam)> abandonments = new();
    private Dictionary<string, string[]> localAbandonments = new();
    private readonly ILogger<GameNodeCoordinator> logger;
    public event Action? Changed;
    private sealed class Remote(RemoteGameNodeOptions config)
    {
        public readonly RemoteGameNodeOptions Config = config;
        public string? Session, Error;
        public string Version = "";
        public DateTime? Since, Heartbeat;
        public GameNodeMatchStatus[] Reports = [];
    }
    public sealed class Assignment
    {
        public string MatchId { get; set; } = "";
        public string ResultId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public string State { get; set; } = "Starting";
        public int Port { get; set; }
        public bool Reserved { get; set; } = true;
        public DateTime Created { get; set; }
        public StartGameNodeMatch? Payload { get; set; }
        public string[] Abandoned { get; set; } = [];
        public bool StopRequested { get; set; }
        public bool Observed { get; set; }
        public int Connected { get; set; }
        public int AcknowledgedAdmissionRevision { get; set; } = -1;
    }
    public GameNodeCoordinator(MatchWorkerCoordinator local, IOptions<MatchWorkerOptions> localOptions,
        IOptions<MainGameNodeOptions> main, IOptions<GameNodeOptions> options, IOptions<InGameOptions> integration,
        IWebHostEnvironment environment, ILogger<GameNodeCoordinator> logger, IOptions<abilitydraft.Models.AdminAuthOptions>? admin = null)
    {
        this.local = local; this.localOptions = localOptions.Value; this.main = main.Value;
        this.options = options.Value; this.logger = logger; integrationEnabled = integration.Value.Enabled;
        if (this.options.HeartbeatTimeoutSeconds is < 15 or > 120) throw new InvalidOperationException("Game Node heartbeat timeout must be 15–120 seconds.");
        if (this.localOptions.Enabled && string.IsNullOrWhiteSpace(this.main.Name)) throw new InvalidOperationException("Configure InGame:GameNode:Name.");
        remotes = new(StringComparer.Ordinal);
        foreach (var config in this.options.RemoteNodes)
        {
            if (config.Id is "main" or "" || config.Id.Length > 64 || !config.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
                string.IsNullOrWhiteSpace(config.Name) || config.Name.Length > 100 || config.Region.Length > 80 || !GameNodeProtocol.ValidHost(config.PublicGameHost) ||
                config.MaxWorkers is < 1 or > 16 || config.FirstGamePort is < 1024 or > 65535 || config.FirstGamePort + config.MaxWorkers - 1 > 65535 ||
                !ValidProbe(config.PublicProbeUrl) || (config.Enabled && (config.Secret.Length < 32 || config.Secret == integration.Value.ServerKey || config.Secret == admin?.Value.Password)))
                throw new InvalidOperationException($"Invalid Game Node configuration for '{config.Id}'. Use a unique secret of at least 32 characters.");
            if (!remotes.TryAdd(config.Id, new(config))) throw new InvalidOperationException("Duplicate Game Node ID.");
        }
        if (remotes.Values.Where(r => r.Config.Enabled).GroupBy(r => r.Config.Secret).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Each Game Node needs a unique secret.");
        var directory = Path.GetFullPath(this.options.StateDirectory, environment.ContentRootPath);
        var publicRoot = Path.GetFullPath(string.IsNullOrEmpty(environment.WebRootPath) ? Path.Combine(environment.ContentRootPath, "wwwroot") : environment.WebRootPath);
        if (directory.StartsWith(publicRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || directory == publicRoot)
            throw new InvalidOperationException("Game Node state must be outside the web root.");
        journal = Path.Combine(directory, "assignments.json");
        assignments = File.Exists(journal) ? JsonSerializer.Deserialize<Dictionary<string, Assignment>>(File.ReadAllText(journal), GameNodeProtocol.Json)
            ?? throw new InvalidDataException("Empty Game Node assignment journal.") : new();
    }
    public bool Enabled => integrationEnabled && (localOptions.Enabled || remotes.Count > 0);
    public static string NormalizeNodeId(string? id) => string.IsNullOrWhiteSpace(id) ? "main" : id;
    private static bool ValidProbe(string? url) => string.IsNullOrWhiteSpace(url) ||
        (Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
         (uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)));
    public bool Authenticate(string id, string authorization)
    {
        lock (gate) return integrationEnabled && remotes.TryGetValue(id, out var remote) && remote.Config.Enabled &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)),
                SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + remote.Config.Secret)));
    }
    private bool Online(Remote r, DateTime now) => r.Config.Enabled && r.Session is not null && r.Error is null &&
        r.Heartbeat is { } time && now - time <= TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds);
    private int Active(Remote r) => assignments.Values.Where(a => a.NodeId == r.Config.Id && a.Reserved).Select(a => a.MatchId)
        .Union(r.Reports.Where(m => m.ProcessActive).Select(m => m.MatchId)).Count();
    public GameNodeView[] Nodes()
    {
        lock (gate)
        {
            var count = local.NodeReports().Count(m => m.ProcessActive);
            var localOnline = integrationEnabled && localOptions.Enabled;
            return new[] { new GameNodeView("main", main.Name + " (MAIN)", main.Region, true,
                !localOnline ? "Offline" : count >= localOptions.MaxWorkers ? "Full" : "Online", count,
                localOptions.Enabled ? localOptions.MaxWorkers : 0, main.PublicProbeUrl) }
                .Concat(remotes.Values.Select(r => new GameNodeView(r.Config.Id, r.Config.Name, r.Config.Region, false,
                    r.Error is not null ? "Error" : !Online(r, DateTime.UtcNow) ? "Offline" : Active(r) >= r.Config.MaxWorkers ? "Full" : "Online",
                    Active(r), r.Config.MaxWorkers, r.Config.PublicProbeUrl))).ToArray();
        }
    }
    public GameNodeAdminView[] AdminNodes()
    {
        lock (gate) return Nodes().Select(n => n.IsMain ? new GameNodeAdminView(n, localOptions.PublicHost,
            typeof(GameNodeCoordinator).Assembly.GetName().Version?.ToString() ?? "", started, DateTime.UtcNow, null,
            local.NodeReports().Where(r => r.ProcessActive).Select(r => r.MatchId).ToArray()) :
            new GameNodeAdminView(n, remotes[n.Id].Config.PublicGameHost, remotes[n.Id].Version,
                remotes[n.Id].Since, remotes[n.Id].Heartbeat, remotes[n.Id].Error,
                assignments.Values.Where(a => a.NodeId == n.Id && a.Reserved).Select(a => a.MatchId).ToArray())).ToArray();
    }
    public string DisplayName(string? id) => Nodes().FirstOrDefault(n => n.Id == NormalizeNodeId(id))?.Name ?? NormalizeNodeId(id);
    public void RequireAvailable(string? id)
    {
        if (!Enabled) return; // Manual ZIP/VPK installations retain their existing room workflow.
        if (Nodes().FirstOrDefault(n => n.Id == NormalizeNodeId(id)) is not { Available: true })
            throw new InvalidOperationException("The selected game server is full or offline. Choose an available server before creating the room.");
    }
    public MatchView Request(DraftResult result, DraftResourceCatalog catalog)
    {
        var id = NormalizeNodeId(result.GameNodeId);
        if (id == "main")
        {
            if (!integrationEnabled) throw new InvalidOperationException("In-game hosting is disabled.");
            return local.Request(result, catalog);
        }
        lock (gate)
        {
            if (assignments.TryGetValue(result.ResultId, out var existing))
            {
                if (existing.Reserved || existing.State != "Failed") return View(existing);
                if (existing.NodeId != id) throw new InvalidOperationException("A room cannot change its Game Node.");
            }
            RuntimePlayerId.Validate(result);
            if (!integrationEnabled || !remotes.TryGetValue(id, out var remote) || !Online(remote, DateTime.UtcNow))
                throw new InvalidOperationException("This room's game server is offline. Wait for it to reconnect, then press PLAY DRAFT again.");
            if (Active(remote) >= remote.Config.MaxWorkers)
                throw new InvalidOperationException("This room's game server is full. Wait for a match to finish, then press PLAY DRAFT again.");
            var matchId = Guid.NewGuid().ToString("N");
            var abandoned = existing?.Abandoned ?? local.GetAbandoned(result.ResultId);
            if (result.Players.Where(p => !p.IsBot).All(p => abandoned.Contains(p.SteamId64!)))
                throw new InvalidOperationException("All players abandoned this result. Create a new draft.");
            var payload = new StartGameNodeMatch(matchId, result, catalog, result.Spectators, abandoned);
            // Check envelope budget before accepting a reservation.
            if (JsonSerializer.SerializeToUtf8Bytes(payload, GameNodeProtocol.Json).Length > 200_000)
                throw new InvalidOperationException("Match assignment exceeds the Game Node protocol limit.");
            var assignment = new Assignment { MatchId = matchId, NodeId = id, ResultId = result.ResultId,
                Created = DateTime.UtcNow, Payload = payload, Abandoned = abandoned };
            assignments[result.ResultId] = assignment;
            Save();
            logger.LogInformation("Reserved match {Match} on Game Node {Node}", matchId, id);
            return View(assignment);
        }
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(assignments, GameNodeProtocol.Json);
        using (var file = new FileStream(journal + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(journal + ".tmp", journal, true);
    }
    public string Connect(string id, GameNodeRegistration registration)
    {
        lock (gate)
        {
            var remote = remotes[id];
            remote.Error = null;
            if (registration.ProtocolVersion != GameNodeProtocol.Version)
                remote.Error = $"Incompatible protocol {registration.ProtocolVersion}; expected {GameNodeProtocol.Version}.";
            else if (registration.PublicHost != remote.Config.PublicGameHost || registration.MaxWorkers != remote.Config.MaxWorkers ||
                     registration.FirstPort != remote.Config.FirstGamePort || registration.PublicProbeUrl != remote.Config.PublicProbeUrl ||
                     string.IsNullOrWhiteSpace(registration.Version) || registration.Version.Length > 80)
                remote.Error = "Game Node registration does not match MAIN's authorized host, ports, capacity or probe URL.";
            if (remote.Error is not null) { remote.Session = null; throw new InvalidOperationException(remote.Error); }
            remote.Session = Guid.NewGuid().ToString("N"); remote.Version = registration.Version;
            remote.Since = DateTime.UtcNow; remote.Heartbeat = null; // Available only after reconciliation.
            return remote.Session;
        }
    }
    public GameNodeCommands Heartbeat(string id, string session, GameNodeHeartbeat heartbeat, DateTime now)
    {
        lock (gate)
        {
            var remote = remotes[id];
            if (remote.Session != session) throw new InvalidOperationException("Game Node connection superseded.");
            if (heartbeat.Registration.ProtocolVersion != GameNodeProtocol.Version || heartbeat.Matches is null || heartbeat.Matches.Length > 256 ||
                heartbeat.Matches.Any(m => m is null || !Guid.TryParseExact(m.MatchId, "N", out _) || m.Abandoned is null || m.Abandoned.Length > 80 ||
                    m.State is not ("Starting" or "Ready" or "Playing" or "Completed" or "Failed") || m.Connected is < 0 or > 64 ||
                    (m.ProcessActive && (m.Port < remote.Config.FirstGamePort || m.Port >= remote.Config.FirstGamePort + remote.Config.MaxWorkers))) ||
                heartbeat.Matches.Select(m => m.MatchId).Distinct().Count() != heartbeat.Matches.Length)
                throw new InvalidOperationException("Invalid Game Node heartbeat.");
            var reports = heartbeat.Matches.ToDictionary(m => m.MatchId);
            var stop = new List<string>(); var forget = new List<string>(); var start = new List<StartGameNodeMatch>();
            foreach (var assignment in assignments.Values.Where(a => a.NodeId == id))
            {
                if (reports.TryGetValue(assignment.MatchId, out var report))
                {
                    if (!assignment.Reserved)
                    {
                        if (report.ProcessActive) stop.Add(report.MatchId); else forget.Add(report.MatchId);
                        continue;
                    }
                    if (!GameNodeProtocol.Terminal(report.State) && !report.ProcessActive)
                        throw new InvalidOperationException("A live match must own a worker process.");
                    assignment.Observed = true; assignment.State = report.State; assignment.Port = report.Port; assignment.Connected = report.Connected;
                    if (assignment.Payload is { } payload && report.AdmissionRevision >= payload.AdmissionRevision)
                    {
                        assignment.AcknowledgedAdmissionRevision = payload.AdmissionRevision;
                        var authorized = report.Abandoned.Where(s => payload.Result.IsPlayer(s) || payload.Spectators.Any(p => p.SteamId64 == s));
                        foreach (var steam in authorized.Except(assignment.Abandoned)) abandonments.Enqueue((assignment.ResultId, steam));
                        assignment.Abandoned = assignment.Abandoned.Union(authorized).ToArray();
                    }
                    if (GameNodeProtocol.Terminal(report.State) && !report.ProcessActive)
                    { assignment.Reserved = false; assignment.Payload = null; if (report.State == "Completed") assignment.Abandoned = []; forget.Add(report.MatchId); }
                }
                else if (assignment.Reserved && assignment.Observed)
                {
                    // The agent lost a previously acknowledged worker. Do not replay its old StartMatch.
                    assignment.State = "Failed"; assignment.Reserved = false; assignment.Payload = null;
                }
                if (!assignment.Reserved) continue;
                if (assignment.StopRequested) stop.Add(assignment.MatchId);
                else if (assignment.Payload is { } payload) start.Add(payload with { Abandoned = assignment.Abandoned });
            }
            var owned = assignments.Values.Where(a => a.NodeId == id).Select(a => a.MatchId).ToHashSet();
            foreach (var unknown in heartbeat.Matches.Where(m => !owned.Contains(m.MatchId)))
            { if (unknown.ProcessActive) stop.Add(unknown.MatchId); else forget.Add(unknown.MatchId); }
            remote.Reports = heartbeat.Matches; remote.Heartbeat = now;
            Save(); // Ack only after the authoritative state is durable.
            return new(GameNodeProtocol.Version, null, start.ToArray(), stop.Distinct().ToArray(), forget.Distinct().ToArray());
        }
    }
    public void Disconnect(string id, string session)
    {
        lock (gate) if (remotes[id].Session == session) remotes[id].Session = null;
    }
    public MatchView? Get(string resultId, string? steam = null)
    {
        lock (gate)
        {
            if (!assignments.TryGetValue(resultId, out var a)) return local.Get(resultId);
            var view = View(a);
            if (steam is not null && a.Payload is { } payload && !payload.Result.IsPlayer(steam) &&
                a.AcknowledgedAdmissionRevision < payload.AdmissionRevision)
                return view with { Address = null, Message = "Waiting for the Game Node to admit this spectator…" };
            return view;
        }
    }
    private MatchView View(Assignment a)
    {
        var online = remotes.TryGetValue(a.NodeId, out var r) && Online(r, DateTime.UtcNow);
        var message = a.State switch
        {
            "Completed" => "Match complete. Create a new draft to play again.",
            "Failed" => "The remote match server stopped. The host can retry PLAY DRAFT once the node is available.",
            _ when !online => "Game server connection lost. Waiting for it to reconnect; your reservation is retained.",
            "Ready" => "Match server ready.", "Playing" => "Match in progress.", _ => "Starting the selected game server…"
        };
        return new(a.MatchId, a.ResultId, a.State, online && a.State is "Ready" or "Playing" ? r!.Config.PublicGameHost + ":" + a.Port : null, message);
    }
    public IReadOnlyDictionary<string, string> States()
    {
        lock (gate) return local.States().Concat(assignments.Select(p => new KeyValuePair<string, string>(p.Key, p.Value.State))).ToDictionary(p => p.Key, p => p.Value);
    }
    public void AdmitSpectator(string resultId, DraftSpectator spectator)
    {
        lock (gate)
        {
            if (!assignments.TryGetValue(resultId, out var a)) { local.AdmitSpectator(resultId, spectator); return; }
            if (!a.Reserved || a.Payload is not { } payload) return;
            if (payload.Result.IsPlayer(spectator.SteamId64)) throw new InvalidOperationException("A drafted player cannot become a spectator.");
            if (payload.Spectators.Contains(spectator) && !a.Abandoned.Contains(spectator.SteamId64)) return;
            var spectators = payload.Spectators.Where(s => s.SteamId64 != spectator.SteamId64).Append(spectator).ToArray();
            if (spectators.Length > 64) throw new InvalidOperationException("The spectator list is full.");
            RuntimePlayerId.Validate(payload.Result with { Spectators = spectators });
            a.Payload = payload with { Spectators = spectators, AdmissionRevision = payload.AdmissionRevision + 1 };
            a.Abandoned = a.Abandoned.Where(s => s != spectator.SteamId64).ToArray(); Save();
        }
    }
    public void Abandon(string resultId, string steam)
    {
        lock (gate)
        {
            if (!assignments.TryGetValue(resultId, out var a)) { local.Abandon(resultId, steam); return; }
            if (!a.Reserved || a.Payload is not { } payload) return;
            if (!payload.Result.IsPlayer(steam) && !payload.Spectators.Any(s => s.SteamId64 == steam)) throw new InvalidOperationException("Not a match participant.");
            a.Abandoned = a.Abandoned.Append(steam).Distinct().ToArray(); Save();
        }
    }
    public (string ResultId, string Steam)[] DrainAbandonments()
    {
        lock (gate)
        {
            var reports = local.NodeReports();
            foreach (var report in reports)
                foreach (var steam in report.Abandoned.Except(localAbandonments.GetValueOrDefault(report.MatchId) ?? []))
                    abandonments.Enqueue((report.MatchId, steam));
            localAbandonments = reports.ToDictionary(r => r.MatchId, r => r.Abandoned);
            var items = abandonments.ToArray(); abandonments.Clear(); return items;
        }
    }
    public void StopMatch(string resultId)
    {
        lock (gate)
        {
            if (assignments.TryGetValue(resultId, out var a)) { a.StopRequested = true; Save(); }
            else local.Stop(resultId);
        }
    }
    public IReadOnlyList<ActiveMatchStatsRecord> GetActiveMatchStats()
    {
        lock (gate) return local.GetActiveMatchStats().Concat(assignments.Values.Where(a => a.Reserved && a.Payload is not null).Select(a =>
        {
            var result = a.Payload!.Result; var names = a.Payload.Catalog.Names ?? new();
            return new ActiveMatchStatsRecord(a.MatchId, result.RoomCode, result.Source, result.HostName, result.DraftMode,
                result.BotsEnabled, a.State, a.Created, a.Connected, result.Players.Count(p => !p.IsBot),
                result.Players.Select(p => new MatchPlayerStats(p.DisplayName, p.Team, names.GetValueOrDefault(p.HeroKey) ?? p.HeroKey,
                    p.Slots.OrderBy(s => s.Slot).Select(s => names.GetValueOrDefault(s.AbilityKey) ?? s.AbilityKey).ToArray(), p.IsBot,
                    a.Abandoned.Contains(p.SteamId64))).ToArray(), a.Payload.Spectators.Select(s => s.DisplayName).ToArray());
        })).ToArray();
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            lock (gate)
            {
                // Retain only opaque replay tombstones beyond the website's ordinary room lifetime.
                var changed = false;
                foreach (var assignment in assignments.Values.Where(a => !a.Reserved && a.Created < DateTime.UtcNow.AddDays(-2) && a.Abandoned.Length > 0))
                { assignment.Abandoned = []; changed = true; }
                if (changed) Save();
            }
            // Blazor's existing SignalR circuit delivers updates; no browser capacity polling.
            foreach (var handler in Changed?.GetInvocationList() ?? [])
                try { ((Action)handler)(); } catch (Exception ex) { logger.LogDebug(ex, "Game Node UI subscriber disconnected"); }
        }
    }
}
