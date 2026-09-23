using System.Collections.Concurrent;
using System.Text.Json;
using AbilityDraft.Contracts;
using AbilityDraft.Runtime;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin : DeadworksPluginBase
{
    public override string Name => "Ability Draft";
    private const string PanelId = "abilityDraft";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource stop = new();
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
    private readonly ConcurrentQueue<Action> completions = new();
    private readonly Dictionary<string, Session> sessions = new();
    private readonly DeadworksRuntime game = new();
    private DraftBackendClient backend = null!;
    private RuntimeDraftApplier applier = null!;
    private IPreparationArea preparationArea = null!;
    private bool allowRuntime;
    private bool useWebsite;
    private bool holdForDraft;
    private bool localMatchTest;
    private bool matchReleased;
    private DraftResult? runtimeResult;
    private string? preparedResult;
    private string? requestedStartHost;
    private DraftResourceCatalog? resourceCatalog;
    private string exportDirectory = null!;
    private string serverRole = "custom";
    private string? hostDirectory;
    private Uri websiteOrigin = null!;
    private DateTime nextHostReport;
    private bool hostReporting;
    private bool IsDraftHub => workerResult is null && separateMatches;
    private sealed class Session(int slot)
    {
        public int Slot = slot;
        public bool Busy;
        public DateTime NextPoll;
        public RoomView? View;
        public string? LastState;
        public bool Queued;
        public string? PendingEntry;
        public DateTime EntryFallback = DateTime.UtcNow.AddSeconds(3);
        public bool EntryReceived;
        public DateTime UiDeadline = DateTime.UtcNow.AddSeconds(15);
        public bool UiWarningShown;
        public bool UiReady;
        public string? RuntimeStatus;
        public bool ExitRequested;
        public string? AutoTransferMatch;
        public MatchView? Match;
        public string? LastMatchRoster;
    }

    public override void OnLoad(bool isReload)
    {
        LoadMatchWorker();
        var uri = new Uri(Environment.GetEnvironmentVariable("ABILITYDRAFT_BACKEND_URL") ?? "http://127.0.0.1:5050/");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidOperationException("Use HTTPS for a remote draft backend.");
        if (!uri.AbsoluteUri.EndsWith('/')) throw new InvalidOperationException("Backend URL must end with /.");
        var key = Environment.GetEnvironmentVariable("ABILITYDRAFT_SERVER_KEY") ?? "";
        if (workerResult is null && key.Length < 32) throw new InvalidOperationException("Set ABILITYDRAFT_SERVER_KEY (at least 32 characters).");
        backend = new(http, uri, key);
        websiteOrigin = WebsiteNavigation.Origin(Environment.GetEnvironmentVariable("ABILITYDRAFT_WEBSITE_URL") ?? "http://localhost:5050/");
        allowRuntime = Environment.GetEnvironmentVariable("ABILITYDRAFT_ENABLE_RUNTIME") == "1";
        useWebsite = Environment.GetEnvironmentVariable("ABILITYDRAFT_USE_WEBSITE") == "1";
        holdForDraft = Environment.GetEnvironmentVariable("ABILITYDRAFT_HOLD_PREGAME") == "1";
        localMatchTest = Environment.GetEnvironmentVariable("ABILITYDRAFT_LOCAL_MATCH_TEST") == "1";
        separateMatches = Environment.GetEnvironmentVariable("ABILITYDRAFT_SEPARATE_MATCHES") == "1" && workerResult is null;
        serverRole = Environment.GetEnvironmentVariable("ABILITYDRAFT_SERVER_ROLE") == "public" ? "public" : "custom";
        hostDirectory = Environment.GetEnvironmentVariable("ABILITYDRAFT_HOST_DIRECTORY");
        if (allowRuntime)
        {
            var path = Environment.GetEnvironmentVariable("ABILITYDRAFT_RESOURCE_CATALOG")
                ?? throw new InvalidOperationException("Fetch the backend resource catalogue before starting a runtime server.");
            resourceCatalog = JsonSerializer.Deserialize<DraftResourceCatalog>(File.ReadAllText(path), Json);
            if (resourceCatalog is not { ProtocolVersion: 1, Heroes.Length: > 0 })
                throw new InvalidOperationException("Invalid backend resource catalogue.");
        }
        exportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("ABILITYDRAFT_EXPORT_DIRECTORY") ?? "abilitydraft-exports");
        preparationArea = new NativePreparationArea(game, Log);
        applier = new(game, new DraftPreparationGate(PlayersPrepared, ApplicationFailed), Log);
        UI.Panel(PanelId).On("command", OnCommand);
        UI.AddonStatusChanged += AddonStatus;
        foreach (var player in Players.GetAll()) Connect(player);
        Log("Plugin loaded; runtime test application " + (allowRuntime ? "enabled" : "disabled"));
    }

    public override void OnClientFullConnect(ClientFullConnectEvent args)
    {
        if (args.Controller is { } player) Connect(player);
    }
    private void Connect(CCitadelPlayerController player)
    {
        if (player.PlayerSteamId == 0) return;
        var steam = player.PlayerSteamId.ToString();
        var session = new Session(player.Slot);
        sessions[steam] = session;
        UI.Panel(PanelId).LoadXml(player.Recipients, useWebsite
            ? "file://{resources}/layout/ability_draft_site.xml"
            : "file://{resources}/layout/ability_draft.xml");
        UI.Panel(PanelId).Set(player.Recipients, "serverRole", workerResult is null ? serverRole : "match");
        UI.Panel(PanelId).Set(player.Recipients, "draftHub", IsDraftHub ? "1" : "0");
        if (workerResult is not null)
        {
            UI.Panel(PanelId).Set(player.Recipients, "screen", "match");
            UI.Panel(PanelId).Set(player.Recipients, "inRoom", "1");
            UI.Panel(PanelId).Set(player.Recipients, "roomCode", workerResult.RoomCode);
            UI.Panel(PanelId).Set(player.Recipients, "matchStarted", matchReleased ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "canAbandon", "1");
            return;
        }
        if (useWebsite)
        {
            UI.Panel(PanelId).Set(player.Recipients, "screen", "connecting");
            UI.Panel(PanelId).Set(player.Recipients, "status", "Connecting…");
            UI.Panel(PanelId).Set(player.Recipients, "runtimeEnabled", allowRuntime ? "1" : "0");
            Log("Website compatibility screen requested at slot " + player.Slot);
            return;
        }
        UI.Panel(PanelId).Set(player.Recipients, "runtimeEnabled", allowRuntime ? "1" : "0");
        UI.Panel(PanelId).Set(player.Recipients, "status", "");
        Log("In-game client connection at slot " + player.Slot);
        Run(steam, session, async () => Deliver(steam, session, await backend.State(steam, stop.Token)));
    }

    [Command("ad_entry", ConsoleOnly = true, Hidden = true)]
    public void MenuEntry(CCitadelPlayerController caller, string kind)
    {
        var steam = caller.PlayerSteamId.ToString();
        if (!useWebsite || !sessions.TryGetValue(steam, out var session)) return;
        if (kind is not ("public" or "custom")) return;
        kind = serverRole; // Dedicated public/custom server controls admission, not the menu alias.
        Log($"Menu entry {kind} received from connected slot {caller.Slot}");
        session.EntryReceived = true;
        session.PendingEntry = kind == "public" ? "public" : "create";
    }

    private void OpenEntry(string steam, Session session, string page)
    {
        var name = Players.FromSlot(session.Slot)!.PlayerName;
        Run(steam, session, async () =>
        {
            var reply = await backend.State(steam, stop.Token);
            if (reply.State is null && page == "public")
                return QueueDelivery(steam, session, await backend.Queue(steam, new("queueJoin", Name: name), stop.Token));
            var website = reply.State is null
                ? await backend.WebsiteEntry(steam, page, name, stop.Token)
                : await backend.Website(steam, stop.Token);
            return () =>
            {
                Deliver(steam, session, reply)();
                NavigateParticipant(session, website.Path);
                if (reply.State is null) Status(steam, "");
            };
        });
    }

    private void OnCommand(UIEvent e)
    {
        var steam = e.Caller.PlayerSteamId.ToString(); // Authenticated transport caller, never a JSON identity.
        if (!sessions.TryGetValue(steam, out var session)) return;
        if (session.Busy) { Status(steam, "Please wait…"); return; }
        try
        {
            var command = PanoramaCommandCodec.Decode(e.ArgAt(0));
            if (command.Operation == "leaveServer" && IsDraftHub)
            {
                Run(steam, session, async () =>
                {
                    await backend.Disconnect(steam, stop.Token);
                    return () => ExitToMenu(session);
                });
                return;
            }
            if (command.Operation == "abandon")
            {
                Run(steam, session, async () =>
                {
                    await backend.Abandon(steam, workerResult?.ResultId, stop.Token);
                    return () => ExitToMenu(session);
                });
                return;
            }
            if (workerResult is not null) return;
            if (command.Operation == "resumeMatch")
            {
                if (session.Match is { State: "Ready" or "Playing", Address: not null } match)
                    TransferToMatch(session, match);
                return;
            }
            if (command.Operation == "playMatch")
            {
                if (separateMatches) { RequestSeparateMatch(steam, session); return; }
                if (!localMatchTest || !holdForDraft || !allowRuntime || matchReleased || requestedStartHost is not null || applier.IsBusy ||
                    session.View is not { Status: "Completed" } view ||
                    !view.Players.Any(p => p.Id == view.SelfId && p.Host && p.Team != "Spectator"))
                    throw new InvalidOperationException("Only the completed room host can start this local match.");
                var expectedRoom = view.Code;
                Run(steam, session, async () =>
                {
                    if (view.RuntimeResultId is null) await backend.Command(steam, new("finalize"), stop.Token);
                    var result = await backend.Result(steam, stop.Token);
                    var latest = await backend.State(steam, stop.Token);
                    return () =>
                    {
                        if (result.RoomCode != expectedRoom || latest.State?.Code != expectedRoom)
                            throw new InvalidOperationException("Room changed; retry match start.");
                        VerifyConnectedRoster(result);
                        if (runtimeResult is not null && runtimeResult.ResultId != result.ResultId)
                            throw new InvalidOperationException("This test map is reserved for another draft. Restart the server.");
                        Deliver(steam, session, latest)();
                        runtimeResult = result;
                        requestedStartHost = steam;
                        session.RuntimeStatus = "Preparing match…";
                        Status(steam, session.RuntimeStatus);
                        Log("Preparing local match from finalized website result " + result.ResultId);
                        try { applier.Apply(result); }
                        catch { requestedStartHost = null; throw; }
                    };
                });
            }
            else if (command.Operation == "startMatch")
            {
                StartPreparedMatch(steam, session);
            }
            else if (command.Operation is "openCreate" or "openJoin")
            {
                if (serverRole == "public") throw new InvalidOperationException("Custom lobbies use the Custom Lobby entry in Play.");
                OpenEntry(steam, session, command.Operation == "openCreate" ? "create" : "join");
            }
            else if (command.Operation is "queueJoin" or "queueLeave")
            {
                if (serverRole != "public") throw new InvalidOperationException("Use Public Queue in Play to find a match.");
                // Display name comes from the connected controller; Steam identity is already authenticated above.
                command = command with { Name = e.Caller.PlayerName };
                var queueCommand = command;
                Run(steam, session, async () =>
                {
                    var reply = await backend.Queue(steam, queueCommand, stop.Token);
                    return () =>
                    {
                        if (queueCommand.Operation == "queueLeave" && reply.Status != "Matched") ExitToMenu(session);
                        else QueueDelivery(steam, session, reply)();
                    };
                });
            }
            else if (command.Operation == "export")
            {
                if (session.View?.Players.Any(p => p.Id == session.View.SelfId && p.Host && p.Team != "Spectator") != true)
                    throw new InvalidOperationException("Only the playing room host can generate the VPK.");
                UI.Panel(PanelId).Set(e.Caller.Recipients, "exportStatus", "Generating with the website packer. This may take several minutes…");
                Run(steam, session, async () =>
                {
                    var archive = await backend.GenerateArchive(steam, stop.Token);
                    Directory.CreateDirectory(exportDirectory);
                    var path = Path.Combine(exportDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + archive.FileName);
                    await File.WriteAllBytesAsync(path, archive.Bytes, stop.Token);
                    Log("Draft archive saved: " + path);
                    return () => UI.Panel(PanelId).Set(e.Caller.Recipients, "exportStatus", archive.Format == "vpk"
                        ? "VPK saved on the host computer. Share it with everyone, close Deadlock, install it, then restart."
                        : "ZIP saved on the host computer. VPK compilation did not succeed; check the website packing log.");
                });
            }
            else if (command.Operation is "applySelf" or "applyAll")
            {
                if (!allowRuntime) throw new InvalidOperationException("Runtime testing is disabled on this server.");
                if (command.Operation == "applyAll" && session.View?.Players.Any(p => p.Id == session.View.SelfId && p.Host) != true)
                    throw new InvalidOperationException("Only the room host can prepare all players.");
                var expectedRoom = session.View?.Code;
                Run(steam, session, async () =>
                {
                    var result = await backend.Result(steam, stop.Token);
                    return () =>
                    {
                        if (result.RoomCode != expectedRoom) throw new InvalidOperationException("Room changed; retry application.");
                        if (matchReleased || (runtimeResult is not null && runtimeResult.ResultId != result.ResultId))
                            throw new InvalidOperationException("This test map is already reserved for another result. Restart the test server.");
                        Log("Final draft retrieval " + result.ResultId);
                        runtimeResult = result;
                        session.RuntimeStatus = "Preparing hero…";
                        Status(steam, session.RuntimeStatus);
                        applier.Apply(result, command.Operation == "applySelf" ? steam : null);
                    };
                });
            }
            else Run(steam, session, async () => Deliver(steam, session, await backend.Command(steam, command, stop.Token)));
        }
        catch (Exception ex) { Status(steam, ex.Message); Log("Command rejected: " + ex.Message); }
    }

    // Async continuations only enqueue immutable results. UI/native calls happen in OnGameFrame.
    private void Run(string steam, Session session, Func<Task<Action>> work)
    {
        session.Busy = true;
        _ = Task.Run(async () =>
        {
            Action action;
            try { action = await work(); }
            catch (Exception ex)
            {
                var message = ex is InvalidOperationException ? ex.Message : "Backend connection failed; try reconnecting.";
                action = () =>
                {
                    Status(steam, message);
                    Log("Backend request failed: " + message);
                    if (useWebsite && session.View is null && !session.Queued)
                        UI.Panel(PanelId).Set(Players.FromSlot(session.Slot)!.Recipients, "screen", "entry");
                };
            }
            completions.Enqueue(() =>
            {
                if (stop.IsCancellationRequested || sessions.GetValueOrDefault(steam) != session ||
                    Players.FromSlot(session.Slot)?.PlayerSteamId.ToString() != steam) return;
                session.Busy = false;
                session.NextPoll = DateTime.UtcNow.AddSeconds(1);
                try { action(); }
                catch (Exception ex) { Status(steam, ex.Message); Log("Deadworks application failed: " + ex.Message); }
            });
        });
    }

    private Action Deliver(string steam, Session session, CommandReply reply) => () =>
    {
        var hadRoom = session.View is not null;
        session.View = reply.State;
        var player = Players.FromSlot(session.Slot)!;
        if (useWebsite)
        {
            if (reply.ExternalLink is { } link)
            {
                UI.Panel(PanelId).Set(player.Recipients, "externalUrl", WebsiteNavigation.External(websiteOrigin, link.Url));
                UI.Panel(PanelId).Set(player.Recipients, "externalLinkId", link.Id);
            }
            if (reply.State is null && hadRoom)
            {
                session.RuntimeStatus = null; session.Match = null; session.AutoTransferMatch = null;
                UI.Panel(PanelId).Set(player.Recipients, "websiteUrl", "");
                UI.Panel(PanelId).Set(player.Recipients, "screen", "entry");
                UI.Panel(PanelId).Set(player.Recipients, "canResumeMatch", "0");
                Status(steam, "Match ended.");
                if (serverRole == "public") { ExitToMenu(session); return; }
            }
            var self = reply.State?.Players.SingleOrDefault(p => p.Id == reply.State.SelfId);
            var launchDelay = reply.State?.MatchReadyUtc is { } readyUtc ? Math.Max(0, (int)Math.Ceiling((readyUtc - reply.State.ServerUtc).TotalSeconds)) : 0;
            UI.Panel(PanelId).Set(player.Recipients, "playCooldown", launchDelay);
            UI.Panel(PanelId).Set(player.Recipients, "canFinalize", self?.Host == true && reply.State?.Status == "Completed" && reply.State.RuntimeResultId is null ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "canApply", self is { Team: not "Spectator" } && reply.State?.RuntimeResultId is not null ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "inRoom", reply.State is null ? "0" : "1");
            UI.Panel(PanelId).Set(player.Recipients, "roomCode", reply.State?.Code ?? "");
            UI.Panel(PanelId).Set(player.Recipients, "canAbandon", reply.State is null ? "0" : "1");
            var prepared = reply.State?.RuntimeResultId is { } id && preparedResult == id;
            UI.Panel(PanelId).Set(player.Recipients, "loadoutPrepared", prepared ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "canStartMatch", prepared && localMatchTest && self?.Host == true && !matchReleased ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "localMatchTest", localMatchTest || separateMatches ? "1" : "0");
            UI.Panel(PanelId).Set(player.Recipients, "canPlayMatch", (separateMatches || localMatchTest && allowRuntime && holdForDraft) &&
                !matchReleased && requestedStartHost is null && !applier.IsBusy && self is { Team: not "Spectator" } && (self.Host || reply.State?.IsPublicQueue == true) &&
                reply.State?.Status == "Completed" ? "1" : "0");
            // Keep entry-form status stable; a newly created room is discovered by the next poll.
            if (reply.State is not null || reply.Error is not null)
                Status(steam, reply.Error ?? session.RuntimeStatus ?? "");
            return;
        }
        // Avoid streaming unchanged card lists each second over the bounded Deadworks UI channel.
        var state = JsonSerializer.Serialize(reply.State is null ? null : reply.State with { ServerUtc = default }, Json).Replace("^", "\\u005E");
        if (state != session.LastState)
        {
            UI.Panel(PanelId).Set(player.Recipients, "room", state);
            session.LastState = state;
            Log("Draft state sync " + (reply.State?.Code ?? "left room"));
        }
        var seconds = reply.State is { TimerPhase: not "None" } view
            ? Math.Max(0, Math.Ceiling((view.TimerEndsUtc - view.ServerUtc).TotalSeconds)) : 0;
        UI.Panel(PanelId).Set(player.Recipients, "seconds", seconds);
        Status(steam, reply.Error ?? "");
    };

    private Action QueueDelivery(string steam, Session session, QueueReply reply) => () =>
    {
        session.Queued = reply.Status == "Waiting";
        var player = Players.FromSlot(session.Slot)!;
        UI.Panel(PanelId).Set(player.Recipients, "queued", session.Queued ? "1" : "0");
        UI.Panel(PanelId).Set(player.Recipients, "queueCount", $"{reply.Waiting} / {reply.Required}");
        UI.Panel(PanelId).Set(player.Recipients, "screen", session.Queued ? "queue" : reply.Status == "Matched" ? "website" : "entry");
        Status(steam, session.Queued ? $"Queue position: {reply.Position}"
            : reply.Status == "Matched" ? "Match found. Ready up." : "Search cancelled.");
        if (reply.WebsitePath is { } path)
        {
            NavigateParticipant(session, path);
            Run(steam, session, async () => Deliver(steam, session, await backend.State(steam, stop.Token)));
        }
    };

    private void NavigateParticipant(Session session, string path)
    {
        // Relative path is produced by the trusted backend for this participant, never broadcast.
        var url = WebsiteNavigation.Participant(websiteOrigin, path);
        if (Players.FromSlot(session.Slot) is { } player)
        {
            UI.Panel(PanelId).Set(player.Recipients, "websiteUrl", url);
            UI.Panel(PanelId).Set(player.Recipients, "screen", "website");
        }
    }

    public override void OnGameFrame(bool simulating, bool firstTick, bool lastTick)
    {
        HoldTrooperWaves();
        AuditPatronIntro();
        AuditLanes();
        while (completions.TryDequeue(out var action))
        {
            try { action(); } catch (Exception ex) { Log("Application failed: " + ex.Message); }
        }
        game.TickHeroAssignments();
        applier.Tick(DateTime.UtcNow);
        if (useWebsite)
            foreach (var session in sessions.Values)
                if (!session.UiWarningShown && DateTime.UtcNow >= session.UiDeadline && !session.UiReady)
                {
                    session.UiWarningShown = true;
                    Log($"Client UI not ready at slot {session.Slot}: {UI.Addon(session.Slot, PanelId)}; check mounted Ability Draft VPK/bridge");
                    Players.FromSlot(session.Slot)?.HudAnnounce("Ability Draft UI did not load",
                        "Restart Deadlock after updating the Ability Draft mod.");
                }
        if (workerResult is not null) { TickMatchWorker(); return; }
        TickHostLease();
        if (requestedStartHost is { } host && runtimeResult is { } result && preparedResult == result.ResultId)
        {
            requestedStartHost = null;
            try
            {
                if (!sessions.TryGetValue(host, out var hostSession)) throw new InvalidOperationException("The host disconnected before match start.");
                StartPreparedMatch(host, hostSession);
            }
            catch (Exception ex) { ApplicationFailed(result.ResultId, ex.Message); }
        }
        foreach (var (steam, session) in sessions)
            if (!session.Busy && !session.ExitRequested && DateTime.UtcNow >= session.NextPoll)
            {
                // Do not put a client with an absent/broken UI into the public queue.
                if (useWebsite && !session.UiReady) continue;
                if (useWebsite && !session.EntryReceived && DateTime.UtcNow >= session.EntryFallback)
                {
                    session.EntryReceived = true;
                    session.PendingEntry = serverRole == "public" ? "public" : "create";
                }
                if (session.PendingEntry is { } entry)
                {
                    session.PendingEntry = null;
                    OpenEntry(steam, session, entry);
                }
                else if (session.Queued)
                    Run(steam, session, async () => QueueDelivery(steam, session, await backend.Queue(steam, null, stop.Token)));
                else if (session.View is not null || (useWebsite && session.EntryReceived))
                    Run(steam, session, async () =>
                    {
                        var reply = await backend.State(steam, stop.Token);
                        var match = separateMatches && reply.State?.RuntimeResultId is not null
                            ? await backend.Match(steam, stop.Token) : null;
                        return () => { Deliver(steam, session, reply)(); if (match is not null) DeliverMatch(steam, session, match); };
                    });
            }
    }
    public override void OnClientDisconnect(ClientDisconnectedEvent args)
    {
        foreach (var steam in sessions.Where(s => s.Value.Slot == args.Slot).Select(s => s.Key).ToArray())
        {
            sessions.Remove(steam);
            game.Forget(steam);
            applier.PlayerDisconnected(steam);
            if (!matchReleased && preparedResult is { } resultId && !IsMatchSpectator(steam))
                ApplicationFailed(resultId, "A prepared player disconnected. Reconnect and prepare their hero again.");
            if (workerResult is null) _ = Disconnect(steam);
        }
    }
    public override void OnPawnHeroInitialized(CCitadelPlayerPawn pawn)
    {
        BotHeroInitialized(pawn);
        game.HeroInitialized(pawn);
    }
    private async Task Disconnect(string steam)
    {
        try { await backend.Disconnect(steam, stop.Token); } catch (Exception ex) { Log("Backend disconnect failed: " + ex.GetType().Name); }
    }
    public override void OnStartupServer()
    {
        game.Clear(); lastLanes.Clear(); lastIntroLanes.Clear(); observerControllers.Clear(); nextLaneAudit = 0;
        preparationArea.Release();
        applier = new(game, new DraftPreparationGate(PlayersPrepared, ApplicationFailed), Log);
        runtimeResult = null; preparedResult = null; requestedStartHost = null; matchReleased = false;
        sessions.Clear(); // New connections rehydrate website sessions; old async completions cannot affect them.
        if (IsDraftHub || workerResult is not null) ConfigureNativeMatchFlow();
        if (IsDraftHub)
        {
            // This server hosts the website, not a native match roster. Mark the engine's
            // synthetic roster ready; OnGameStateChanging owns the permanent hub barrier.
            GameRules.SetWaitingForPlayersRoster(1, 1);
            var hibernate = ConVar.Find("sv_hibernate_when_empty")
                ?? throw new InvalidOperationException("This game build lacks the empty-server tick setting.");
            hibernate.SetInt(0);
            Log("Draft-only " + serverRole + " server stays in WaitingForPlayersToJoin; no pregame countdown");
        }
        if (workerResult is not null)
        {
            runtimeResult = workerResult; botsRequested = false; createdBots.Clear(); initializedBots.Clear(); workerPreparing = false; workerLastCount = -1; preparation.Reset();
            // An empty dedicated server otherwise stops ticking before our readiness heartbeat.
            var hibernate = ConVar.Find("sv_hibernate_when_empty")
                ?? throw new InvalidOperationException("This game build lacks the empty-server tick setting.");
            hibernate.SetInt(0);
            Log("Isolated worker empty-server hibernation disabled");
            GameRules.SetWaitingForPlayersRoster(0, (uint)workerResult.Players.Count(p => !p.IsBot && !workerAbandoned.Contains(p.SteamId64!)));
        }
    }

    public override void OnPrecacheResources()
    {
        game.ClearResourceCoverage();
        if (resourceCatalog is null) return;
        var count = 0;
        foreach (var hero in resourceCatalog.Heroes)
        {
            if (!HeroTypeExtensions.TryParse(hero.Key, out var identity) || (int)identity != hero.Id ||
                identity.GetHeroData() is not { IsValid: true })
            {
                Log("Resource precache skipped missing hero " + hero.Key);
                continue;
            }
            Precache.AddHero(identity);
            game.RegisterResourceCoverage(hero);
            count++;
        }
        Log($"Precache registered {count} website heroes and their ability resources before map load");
    }

    // Draft hubs remain in the waiting state; only match workers enter gameplay.
    public override bool OnGameStateChanging(EGameState currentState, EGameState newState)
    {
        if (IsDraftHub) return newState is not (EGameState.HeroSelection or EGameState.PreGameWait or EGameState.GameInProgress);
        // The engine must know the drafted heroes before it builds their match intro.
        if (workerResult is not null && preparedResult != workerResult.ResultId &&
            newState is EGameState.MatchIntro or EGameState.WaitForMapToLoad or EGameState.PreGameWait or EGameState.GameInProgress) return false;
        return !holdForDraft || matchReleased || newState != EGameState.GameInProgress;
    }

    public override void OnGameStateChanged(EGameState newState)
    {
        Log("Game state " + newState + (holdForDraft && !matchReleased ? " (draft hold active)" : ""));
        if (workerResult is not null && newState == EGameState.MatchIntro)
            foreach (var session in sessions.Values)
                if (Players.FromSlot(session.Slot) is { } player) UI.Panel(PanelId).Set(player.Recipients, "nativeIntro", "1");
        if (IsDraftHub && newState == EGameState.WaitingForPlayersToJoin)
            GameRules.SetGameStateEndTime(float.MaxValue);
        if (newState == EGameState.PreGameWait && holdForDraft && !matchReleased)
            GameRules.SetGameStateEndTime(-1); // No countdown until the website loadout is prepared.
        if (workerResult is not null && newState is EGameState.PostGame or EGameState.PostGamePlayOfTheGame or EGameState.Abandoned or EGameState.End)
            workerState = "Completed";
    }

    private void PlayersPrepared(string resultId)
    {
        preparedResult = resultId;
        Log("All drafted loadouts verified for " + resultId);
        foreach (var (steam, session) in sessions)
        {
            if (session.View?.RuntimeResultId != resultId) continue;
            session.RuntimeStatus = requestedStartHost is null ? "Ready to start." : "Starting match…";
            Status(steam, session.RuntimeStatus);
            UI.Panel(PanelId).Set(Players.FromSlot(session.Slot)!.Recipients, "loadoutPrepared", "1");
            if (localMatchTest && session.View.Players.Any(p => p.Id == session.View.SelfId && p.Host))
                UI.Panel(PanelId).Set(Players.FromSlot(session.Slot)!.Recipients, "canStartMatch", "1");
        }
    }

    private void ApplicationFailed(string resultId, string reason)
    {
        preparationArea.Release();
        if (workerResult is not null) workerState = "Failed";
        preparation.Reset();
        if (GameRules.IsValid && GameRules.GameState == EGameState.PreGameWait) GameRules.SetGameStateEndTime(-1);
        preparedResult = null;
        requestedStartHost = null;
        Log("Match preparation blocked for " + resultId + ": " + reason);
        foreach (var (steam, session) in sessions.Where(s => workerResult?.ResultId == resultId || s.Value.View?.RuntimeResultId == resultId))
        {
            session.RuntimeStatus = "Loadout preparation failed: " + reason;
            Status(steam, session.RuntimeStatus);
            UI.Panel(PanelId).Set(Players.FromSlot(session.Slot)!.Recipients, "canStartMatch", "0");
        }
    }

    private void StartPreparedMatch(string steam, Session session)
    {
        if (!localMatchTest || !allowRuntime || !holdForDraft || matchReleased || runtimeResult is null ||
            preparedResult != runtimeResult.ResultId || session.View?.RuntimeResultId != preparedResult ||
            !session.View.Players.Any(p => p.Id == session.View.SelfId && p.Host))
            throw new InvalidOperationException("Only the lobby host can start the match.");
        VerifyConnectedRoster(runtimeResult);
        if (!GameRules.IsValid || GameRules.GameState != EGameState.PreGameWait)
            throw new InvalidOperationException("The server is not ready. Return to the lobby and retry.");
        try { applier.VerifyPrepared(runtimeResult); }
        catch (Exception ex) { ApplicationFailed(runtimeResult.ResultId, ex.Message); throw; }
        ReleasePreparedMatch();
    }

    private void ReleasePreparedMatch()
    {
        preparationArea.Release();
        matchReleased = true;
        Log("Releasing local pregame hold for " + preparedResult);
        GameRules.SetGameStartTime(GlobalVars.CurTime);
        GameRules.ChangeGameState(EGameState.GameInProgress);
        if (GameRules.GameState != EGameState.GameInProgress)
        {
            matchReleased = false;
            throw new InvalidOperationException("The native game did not enter gameplay.");
        }
        // Grant the first unlock only after native gameplay starts.
        foreach (var player in Players.GetAll())
            if (player.GetHeroPawn() is { } pawn)
            {
                pawn.Level = 1;
                pawn.SetCurrency(ECurrencyType.EAbilityUnlocks, 1);
                pawn.SetCurrency(ECurrencyType.EAbilityPoints, 0);
                foreach (var ability in pawn.AbilityComponent.Abilities.Where(a => a.IsSignature)) ability.CanBeUpgraded = true;
                Log($"Gameplay progression slot {player.Slot}: level={pawn.Level}, unlocks={pawn.GetCurrency(ECurrencyType.EAbilityUnlocks)}, upgrades={pawn.GetCurrency(ECurrencyType.EAbilityPoints)}");
            }
        BeginPatronIntro();
        foreach (var member in sessions.Values)
        {
            member.RuntimeStatus = "Match in progress";
            UI.Panel(PanelId).Set(Players.FromSlot(member.Slot)!.Recipients, "matchStarted", "1");
            UI.Panel(PanelId).Set(Players.FromSlot(member.Slot)!.Recipients, "canStartMatch", "0");
            UI.Panel(PanelId).Set(Players.FromSlot(member.Slot)!.Recipients, "canPlayMatch", "0");
        }
        Log("Match started from verified website result " + preparedResult);
    }

    private void VerifyConnectedRoster(DraftResult result)
    {
        var roster = result.Players.Where(p => !p.IsBot).Select(p => p.SteamId64).ToHashSet();
        var connected = Players.GetAll().Where(p => p.PlayerSteamId != 0 && !result.IsSpectator(p.PlayerSteamId.ToString()))
            .Select(p => p.PlayerSteamId.ToString()).ToHashSet();
        if (roster.Contains(null) || !roster.SetEquals(connected) || result.Players.Any(p => !game.IsConnected(RuntimePlayerId.For(p))))
            throw new InvalidOperationException("Every connected player must belong to this draft. Use a separate match server for other rooms.");
    }

    private sealed class DraftPreparationGate(Action<string> prepared, Action<string, string> failed) : IMatchStartGate
    {
        public void PlayersPrepared(string resultId) => prepared(resultId);
        public void ApplicationFailed(string resultId, string reason) => failed(resultId, reason);
    }
    public override void OnUnload()
    {
        stop.Cancel();
        UI.AddonStatusChanged -= AddonStatus;
        UI.Panel(PanelId).On("command", _ => { });
        UI.Panel(PanelId).DestroyLayout(RecipientFilter.All);
        game.Clear();
        http.Dispose();
    }
    private void AddonStatus(int slot, string panel, AddonState state)
    {
        if (panel != PanelId) return;
        // The pinned bootstrap can report Ready from script init before its outer
        // loadxml call reports Loaded. Do not regress an acknowledged ready script.
        foreach (var session in sessions.Values.Where(s => s.Slot == slot))
        {
            if (state == AddonState.Ready) session.UiReady = true;
            else if (state == AddonState.Failed) session.UiReady = false;
        }
        Log($"Panorama addon slot {slot}: {state}");
    }
    private void Status(string steam, string message)
    {
        if (sessions.TryGetValue(steam, out var session) && Players.FromSlot(session.Slot) is { } player)
            UI.Panel(PanelId).Set(player.Recipients, "status", message.Replace('^', ' '));
    }
    private static void Log(string message) => Console.WriteLine("[AbilityDraft] " + message);
}
