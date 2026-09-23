using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbilityDraft.Contracts;
using AbilityDraft.Runtime;
using abilitydraft.Models;
using abilitydraft.Services;
using abilitydraft.Services.InGame;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Exact encodeURIComponent payload from Panorama; no quotes or console separators on the wire.
var wire = "%7B%22operation%22%3A%22chat%22%2C%22text%22%3A%22hello%20%7C%20%5C%22quoted%5C%22%3B%20%D0%BF%D1%80%D0%B8%D0%B2%D0%B5%D1%82%22%7D";
var decoded = PanoramaCommandCodec.Decode(wire);
Check(decoded.Operation == "chat" && decoded.Text == "hello | \"quoted\"; привет", "Panorama console transport preserves JSON, separators and Unicode");
Expect(() => PanoramaCommandCodec.Decode("{\"operation\":\"queueJoin\"}"), "Unencoded JSON is rejected instead of passing through console tokenization");
Expect(() => PanoramaCommandCodec.Decode("%7Bbad%7D"), "Malformed encoded commands produce a controlled rejection");
Expect(() => PanoramaCommandCodec.Decode(new string('a', 6145)), "Oversized Panorama commands are rejected");
var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
var temporary = Path.Combine(Path.GetTempPath(), "abilitydraft-tests-" + Guid.NewGuid().ToString("N"));
Check(WebsiteNavigation.Participant(WebsiteNavigation.Origin("https://draft.example.org/"), "room/ROOM?player=test").StartsWith("https://draft.example.org/room/"), "Remote website navigation uses the configured HTTPS origin");
Expect(() => WebsiteNavigation.Origin("http://draft.example.org/"), "Remote website navigation rejects unencrypted origins");
Expect(() => WebsiteNavigation.Origin("https://user:password@draft.example.org/"), "Website origin cannot contain credentials");
Expect(() => WebsiteNavigation.Participant(WebsiteNavigation.Origin("https://draft.example.org/"), "//other.example.org/room"), "Website navigation cannot redirect to another host");
MatchWorkerChecks.Run(temporary);
DraftServerChecks.Run(temporary);
PresetChecks.Run();
StatsChecks.Run(temporary);
ProjectLinksChecks.Run(temporary);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, Args = [] });
builder.Logging.ClearProviders();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["InGame:Enabled"] = "true", ["InGame:ServerKey"] = new string('x', 48),
    ["InGame:PublicQueueEnabled"] = "false",
    ["InGame:DraftServers:Enabled"] = "false",
    ["InGame:MatchWorkers:Enabled"] = "false"
});
builder.Services.Configure<DeadlockDataOptions>(o => { o.GameDataPath = Path.Combine(root, "Data/Deadlock"); o.IconsPath = Path.Combine(temporary, "icons"); o.OutputPath = Path.Combine(temporary, "output"); });
var testPacking = args.Contains("--pack");
builder.Services.Configure<DeadPackerOptions>(o =>
{
    o.Enabled = testPacking;
    o.ExecutablePath = Path.Combine(root, "Tools/DeadPacker/DeadPacker.exe");
    o.ResourceCompilerPath = Path.Combine(root, "Tools/Reduced_CSDK_12/game/bin_cs2/win64/resourcecompiler.exe");
    o.GameRootPath = Path.Combine(root, "Tools/Reduced_CSDK_12/game");
    o.OutputVpkPath = Path.Combine(temporary, "packed/draft.vpk");
});
builder.Services.Configure<DraftStatsOptions>(o => o.SaveCompletedDraftHistory = false);
builder.Services.Configure<DraftTimingOptions>(o => { o.PreparationSeconds = 30; o.PickSeconds = 3600; });
builder.Services.AddSingleton<LocalisationDiscoveryService>();
builder.Services.AddSingleton<LocalisationParser>();
builder.Services.AddSingleton<DeadlockFileParser>();
builder.Services.AddSingleton<ServerDeadlockDataService>();
builder.Services.AddSingleton<DraftPoolGenerator>();
builder.Services.AddSingleton<DraftTurnService>();
builder.Services.AddSingleton<AbilityAssignmentService>();
builder.Services.AddSingleton<ModFileGenerator>();
builder.Services.AddSingleton<ZipExportService>();
builder.Services.AddSingleton<DeadPackerService>();
builder.Services.AddSingleton<DraftStatsService>();
builder.Services.AddSingleton<DraftRoomService>();
builder.Services.AddInGameIntegration(builder.Configuration);
// Never read or overwrite the developer's Admin settings while exercising links.
var projectLinks = ProjectLinksChecks.Create(temporary);
builder.Services.AddSingleton(projectLinks);
await using var app = builder.Build();
app.UseRateLimiter(); app.MapInGameIntegration();
app.Urls.Add("http://127.0.0.1:0");
var snapshot = app.Services.GetRequiredService<ServerDeadlockDataService>().Reload();
Check(snapshot.IsLoaded, "Local Deadlock data loads");
await app.StartAsync();
using var http = new HttpClient();
var backend = new DraftBackendClient(http, new Uri(app.Urls.Single()), new string('x', 48));
var rooms = app.Services.GetRequiredService<DraftRoomService>();
var adapter = app.Services.GetRequiredService<InGameRoomAdapter>();
LifecycleChecks.Run(adapter, rooms);
const string host = "76561198000000001", other = "76561198000000002", spectator = "76561198000000003";
using (var catalogRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(app.Urls.Single()), "/api/ingame/v1/catalog")))
{
    catalogRequest.Headers.Authorization = new("Bearer", new string('x', 48));
    using var catalogResponse = await http.SendAsync(catalogRequest);
    var catalog = await catalogResponse.Content.ReadFromJsonAsync<DraftResourceCatalog>();
    Check(catalogResponse.IsSuccessStatusCode && catalog is { ProtocolVersion: 1 } &&
        catalog.Heroes.Select(h => h.Key).Order().SequenceEqual(snapshot.Data!.Heroes.Select(h => h.Key).Order()),
        "Trusted server fetches the full website resource catalogue before any player connects");
}
using (var catalogResponse = await http.GetAsync(new Uri(new Uri(app.Urls.Single()), "/api/ingame/v1/catalog")))
    Check(catalogResponse.StatusCode == HttpStatusCode.Unauthorized, "Resource catalogue still requires server authentication");
using (var stateRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(app.Urls.Single()), "/api/ingame/v1/state")))
{
    stateRequest.Headers.Authorization = new("Bearer", new string('x', 48));
    using var stateResponse = await http.SendAsync(stateRequest);
    Check(stateResponse.StatusCode == HttpStatusCode.BadRequest, "Player operations still require a verified Steam identity");
}
var ct = CancellationToken.None;
using (var unauthorized = await http.GetAsync(app.Urls.Single() + "/api/ingame/v1/state"))
    Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "API denies unauthenticated access");
Check((await backend.State(host, ct)).State is null, "A connected player without a room gets an empty state for the website entry");
var footerEntry = adapter.CreateWebsiteEntry(host, "create", "Host");
var footerToken = footerEntry.Path.Split("gameEntry=")[1];
adapter.OpenWebsiteLink(footerToken, 2);
var footerReply = await backend.State(host, ct);
Check(footerReply.ExternalLink?.Url == "https://discord.gg/SxQjYeA7aW", "Original website footer routes its game click to the authenticated player's external browser handoff");
Check((await backend.State(other, ct)).ExternalLink is null, "Footer handoff cannot open a browser for another player");
Expect(() => adapter.OpenWebsiteLink("invalid", 0), "Footer handoff requires the private game-entry capability");
Expect(() => adapter.OpenWebsiteLink(footerToken, 3), "Footer handoff rejects an unconfigured support link");
projectLinks.Save(null, null, [new("Support", "https://support.example.org/project")]);
adapter.OpenWebsiteLink(footerToken, 3);
var supportPath = (await backend.State(host, ct)).ExternalLink!.Url;
Check(supportPath == "project-links/3", "Configured footer links use a website route without embedding destinations in the mod");
var supportUrl = WebsiteNavigation.External(WebsiteNavigation.Origin("https://draft.example.org/"), supportPath);
Check(supportUrl == "https://draft.example.org/project-links/3", "Plugin accepts the configured support-link handoff");
using (var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
{
    using var redirect = await noRedirect.GetAsync(app.Urls.Single() + "/" + supportPath);
    Check(redirect.StatusCode == HttpStatusCode.Redirect && redirect.Headers.Location?.AbsoluteUri == "https://support.example.org/project" && redirect.Headers.CacheControl?.NoStore == true,
        "External browser receives the Admin-configured URL through an uncached redirect");
    projectLinks.Save(null, null, []);
    using var removed = await noRedirect.GetAsync(app.Urls.Single() + "/" + supportPath);
    Check(removed.StatusCode == HttpStatusCode.NotFound, "Removed support links stop redirecting immediately");
}
Expect(() => WebsiteNavigation.External(WebsiteNavigation.Origin("https://draft.example.org/"), "project-links/3?url=https://other.example.org"), "Support handoff cannot supply a redirect destination");
var transferService = app.Services.GetRequiredService<PresetTransferService>();
var exportJson = CustomDraftPresetService.Export(new() { DraftMode = DraftMode.Custom });
var presetToken = transferService.Create(exportJson);
adapter.OpenPresetLink("presets/" + presetToken, gameEntry: footerToken);
Check((await backend.State(host, ct)).ExternalLink?.Url == "presets/" + presetToken, "Preset browser request is delivered only to its game identity");
Expect(() => adapter.OpenPresetLink("presets/" + presetToken, gameEntry: "bad"), "Preset browser handoff requires the game form capability");
using (var download = await http.GetAsync(app.Urls.Single() + "/presets/" + presetToken + "/download"))
    Check(download.IsSuccessStatusCode && download.Content.Headers.ContentDisposition?.DispositionType == "attachment" &&
        await download.Content.ReadAsStringAsync() == exportJson && download.Headers.CacheControl?.NoStore == true,
        "Browser preset endpoint downloads an uncached JSON attachment");
using (var missing = await http.GetAsync(app.Urls.Single() + "/presets/invalid/download"))
    Check(missing.StatusCode == HttpStatusCode.NotFound, "Preset endpoint hides unknown capabilities");
var created = (await backend.Command(host, new("create", Name: "Host"), ct)).State!;
var room = rooms.GetRoom(created.Code)!;
Check(room.Clients.Count == 1 && room.Clients[0].SteamId64 == host, "HTTP creates a real domain room with Steam binding");
Check(!JsonSerializer.Serialize(created).Contains(room.Clients[0].PlayerId), "Public state excludes control-bearing browser IDs");
var browser = rooms.JoinRoom(room.Code, "Browser player", DeadlockTeam.Archmother);
var link = adapter.CreateLinkCode(room.Code, browser.PlayerId);
await backend.Command(other, new("link", Key: link), ct);
await RejectAsync(() => backend.Command(spectator, new("link", Key: link), ct), "Link tokens are single use");
Check(room.Clients.Single(c => c.PlayerId == browser.PlayerId).SteamId64 == other, "Existing browser participant links without duplication");
await backend.Command(spectator, new("spectate", room.Code, "Watcher"), ct);
await backend.Command(host, new("chat", Text: "private ally message", Scope: "Allies"), ct);
var otherView = (await backend.State(other, ct)).State!;
Check(!otherView.Chat.Any(m => m.Text == "private ally message"), "Opponent cannot receive ally chat");
await backend.Command(host, new("chat", Text: "connect [A:1:12345:9]", Scope: "All"), ct);
Check(!(await backend.State(spectator, ct)).State!.Chat.Any(m => m.Text.Contains("12345")), "Spectator address is censored server-side");
await RejectAsync(() => backend.Command(other, new("start"), ct), "Non-host start is rejected by domain validation");
await backend.Command(host, new("ready", Ready: true), ct);
await backend.Command(other, new("ready", Ready: true), ct);
await backend.Command(host, new("start"), ct);
lock (room) { room.TimerPhase = DraftTimerPhase.Picking; room.TimerEndsUtc = DateTime.UtcNow.AddHours(1); }
await RejectAsync(() => backend.Command(spectator, new("pick", Key: created.SelfId), ct), "Invalid spectator pick is rejected");
await backend.Disconnect(other, ct);
Check(room.Clients.Single(c => c.PlayerId == browser.PlayerId).IsConnected, "Game disconnect preserves active browser presence");
rooms.MarkPlayerDisconnected(room.Code, browser.PlayerId);
Check(!room.Clients.Single(c => c.PlayerId == browser.PlayerId).IsConnected, "Both transports disconnected marks participant offline");
Expect(() => rooms.JoinRoom(room.Code, "Browser player", DeadlockTeam.Archmother), "Legacy nickname reconnect cannot claim a Steam-bound participant");
await backend.Command(other, new("reconnect"), ct);
Check(room.Clients.Single(c => c.PlayerId == browser.PlayerId).IsConnected, "Steam reconnect preserves participant identity");
var iterations = 0;
while (!room.IsCompleted && iterations++ < 30)
{
    var turnPlayer = room.Players.Single(p => p.SlotNumber == room.CurrentTurn!.SlotNumber);
    var steam = room.Clients.Single(c => c.PlayerId == turnPlayer.PlayerId).SteamId64!;
    var view = (await backend.State(steam, ct)).State!;
    var card = view.Heroes.Concat(view.Abilities).First(c => c.CanPick);
    await backend.Command(steam, new("pick", Key: card.Id), ct);
}
Check(room.IsCompleted, "HTTP picks complete the shared website draft");
var hostSlot = room.Players.Single(p => p.PlayerId == room.Clients.Single(c => c.SteamId64 == host).PlayerId);
await backend.Command(host, new("reorder", Slot: hostSlot.SlotNumber, From: 0, To: 2), ct);
var ordered = hostSlot.Loadout.RegularAbilities.ToArray();
await RejectAsync(() => backend.Result(host, ct), "Results unavailable before explicit finalization");
await backend.Command(host, new("finalize"), ct);
var result = await backend.Result(host, ct);
await RejectAsync(() => backend.RequestMatch(other, ct), "Only the completed room host can allocate a worker");
Check((await backend.Match(spectator, ct)).State == "Idle", "A pre-draft spectator can query their match handoff");
Check(result.IsSpectator(spectator) && !result.IsPlayer(spectator), "Finalized roster separates observers from drafted players");
await RejectAsync(() => backend.RequestMatch(spectator, ct), "Spectators cannot allocate a match");
await backend.Command(spectator, new("leave"), ct);
var spectatorEntry = adapter.CreateWebsiteEntry(spectator, "join", "Watcher");
adapter.JoinFromWebsite(spectatorEntry.Path.Split("gameEntry=")[1], room.Code, DeadlockTeam.Spectator);
Check((await backend.Match(spectator, ct)).ResultId == result.ResultId && room.Clients.Count(c => c.SteamId64 == spectator) == 1,
    "Spectator returns by website code after finalization without a duplicate participant");
var clientCount = room.Clients.Count;
await backend.Command("76561198000000099", new("spectate", room.Code, "Late viewer"), ct);
Check(room.Clients.Count == clientCount + 1 && (await backend.Match("76561198000000099", ct)).ResultId == result.ResultId,
    "New spectators can join by code after draft completion and request the match");
await RejectAsync(() => backend.Result("76561198000000099", ct), "Late spectator admission does not expose the private result");
Check((await backend.Match(host, ct)) is { State: "Idle", Address: null }, "A finalized draft has no invented server address before allocation");
await RejectAsync(() => backend.RequestMatch(host, ct), "HTTP match allocation remains disabled by default");
Check(result.Players.Single(p => p.SteamId64 == host).Slots.Take(3).Select(s => s.AbilityKey).SequenceEqual(ordered), "Runtime result preserves final slot order");
await backend.Command(host, new("finalize"), ct);
Check((await backend.Result(host, ct)).ResultId == result.ResultId, "Finalization is idempotent");
await RejectAsync(() => backend.Command(host, new("reorder", Slot: hostSlot.SlotNumber, From: 0, To: 1), ct), "Finalized runtime slot order cannot change");
await RejectAsync(() => backend.Result(spectator, ct), "Spectators cannot retrieve Steam identity results");
var files = new ModFileGenerator().Generate(room);
var zip = new ZipExportService().CreateZip(files);
Check(zip.Length > 0 && files.Count == 2, "Existing VData and ZIP generation still works after runtime finalization");
await RejectAsync(() => backend.GenerateArchive(spectator, ct), "Spectator cannot generate or download the draft archive");
await RejectAsync(() => backend.GenerateArchive(other, ct), "Non-host cannot trigger compilation");
var archive = await backend.GenerateArchive(host, ct);
if (testPacking)
{
    Console.WriteLine("Native packing log: " + room.PackingLogPath);
    Check(archive.Format == "vpk" && archive.Bytes.Length > 0, "Existing native compiler produces a VPK through the in-game HTTP endpoint");
}
else Check(archive.Format == "zip" && archive.Bytes.Length > 0 && room.GeneratedZip!.SequenceEqual(archive.Bytes), "In-game export returns the existing website archive (ZIP fallback with compiler disabled)");

const string blindSteam = "76561198000000004";
var blindView = (await backend.Command(blindSteam, new("create", Name: "Blind", Mode: "Custom"), ct)).State!;
var blindRoom = rooms.GetRoom(blindView.Code)!;
rooms.UpdateRoomConfig(blindRoom.Code, blindRoom.Clients[0].PlayerId, new DraftRoomConfig { DraftMode = DraftMode.Custom, BlindDraft = true });
await backend.Command(blindSteam, new("start"), ct);
var blind = (await backend.State(blindSteam, ct)).State!;
Check(blind.Abilities.Where(a => a.Kind == "RegularAbility").All(a => a.Hidden && a.IconKey == null && a.SourceHeroKey == null), "Blind cards omit identities and source icons");
var blindJson = JsonSerializer.Serialize(blind);
var leaked = blindRoom.DeadlockData.Abilities.Where(a => blindRoom.DraftAbilityPoolKeys.Contains(a.Key) && a.PickKind == DraftPickKind.RegularAbility)
    .Where(a => blindJson.Contains(JsonSerializer.Serialize(a.Key))).Select(a => a.Key).ToArray();
Check(leaked.Length == 0, "Blind snapshot contains no underlying regular ability keys" + (leaked.Length == 0 ? "" : ": " + string.Join(",", leaked)));

adapter.ExpireConnections(DateTime.UtcNow.AddSeconds(40));
Check(!room.Clients.Single(c => c.SteamId64 == host).IsConnected, "Expired game connection lease marks participant offline");
await backend.State(host, ct);
Check(room.Clients.Single(c => c.SteamId64 == host).IsConnected, "Polling recovers the same Steam participant after a connection outage");

var target = result.Players.Single(p => p.SteamId64 == host);
var fake = new FakeRuntime(); var gate = new FakeGate(); var applier = new RuntimeDraftApplier(fake, gate, _ => { });
var single = result with { Players = [target] };
applier.Apply(single); applier.Tick(DateTime.UtcNow);
Check(fake.Adds == 0, "Runtime waits for hero initialization");
fake.Ready = true; applier.Tick(DateTime.UtcNow);
Check(fake.Abilities.Select(a => a.Key).SequenceEqual(target.Slots.Select(s => s.AbilityKey)) && gate.Prepared == 1, "Team/hero/four-slot orchestration verifies readback before preparing match");
Check(fake.Abilities.All(a => a.UpgradeBits == 0) && fake.Progress == new RuntimeProgression(1, 0, 0), "Preparation starts level 1 with four locked slots and no spendable points");
applier.Apply(single); applier.Tick(DateTime.UtcNow);
Check(fake.Begins == 1 && fake.Adds == 4, "Duplicate delivery does not reset or reapply loadout");
applier.VerifyPrepared(single);
fake.Abilities[0] = fake.Abilities[0] with { UpgradeBits = 1 }; fake.Progress = new(1, 0, 0);
Expect(() => applier.VerifyPrepared(single), "Learning an ability during preparation prevents premature gameplay");
fake.Abilities[0] = fake.Abilities[0] with { UpgradeBits = 0 };
fake.Progress = new(1, 1, 0);
Expect(() => applier.VerifyPrepared(single), "Extra starting ability unlocks prevent gameplay");
fake.Progress = new(1, 0, 0);
fake.Abilities[0] = fake.Abilities[0] with { Key = "unexpected-reset-ability" };
Expect(() => applier.VerifyPrepared(single), "Match start rejects a loadout changed after preparation");
fake.Abilities[0] = fake.Abilities[0] with { Key = target.Slots[0].AbilityKey };
applier.PlayerDisconnected(host);
Expect(() => applier.VerifyPrepared(single), "Disconnect invalidates prior match preparation");
applier.Apply(single); applier.Tick(DateTime.UtcNow);
applier.VerifyPrepared(single);
Check(fake.Begins == 2 && gate.Prepared == 2, "Reconnected player can prepare the same result again");
var botTarget = target with { ParticipantId = "bot-seat", SteamId64 = null, IsBot = true };
var botRuntime = new FakeRuntime { Ready = true }; var botGate = new FakeGate();
var botApplier = new RuntimeDraftApplier(botRuntime, botGate, _ => { });
var botResult = single with { Players = [botTarget] };
botApplier.Apply(botResult); botApplier.Tick(DateTime.UtcNow); botApplier.VerifyPrepared(botResult);
Check(botGate.Prepared == 1 && botRuntime.Adds == 4 && RuntimePlayerId.For(botTarget) == "bot:bot-seat",
    "Bot participant applies the same four-slot pipeline without a fake Steam identity");
Expect(() => RuntimePlayerId.For(botTarget with { SteamId64 = host }), "Bot cannot impersonate a Steam participant");
var resetRuntime = new FakeRuntime { Ready = true }; var resetApplier = new RuntimeDraftApplier(resetRuntime, new FakeGate(), _ => { });
resetApplier.Apply(single); resetApplier.Tick(DateTime.UtcNow);
resetRuntime.Abilities = Enumerable.Range(1, 4).Select(i => new RuntimeAbility(i, "original" + i, 1)).ToList();
resetRuntime.Progress = new(1, 1, 0);
resetApplier.RestoreAfterNativePregame(single); resetApplier.VerifyPrepared(single);
Check(resetRuntime.Abilities.Select(a => a.Key).SequenceEqual(target.Slots.OrderBy(s => s.Slot).Select(s => s.AbilityKey)) &&
    resetRuntime.Begins == 1 && resetRuntime.Progress == new RuntimeProgression(1, 0, 0),
    "Native pregame reset restores drafted slots and locks without reselecting the hero or changing team");
var addsBefore = resetRuntime.Adds;
resetApplier.RestoreAfterNativePregame(single);
Check(resetRuntime.Adds == addsBefore, "Unchanged pregame signatures are not removed and re-added");
resetRuntime.Ready = false;
Expect(() => resetApplier.RestoreAfterNativePregame(single), "Pregame repair cannot overwrite a changed hero or team");
var failing = new FakeRuntime { Ready = true, FailKey = target.Slots[1].AbilityKey };
var failedGate = new FakeGate(); var failedApplier = new RuntimeDraftApplier(failing, failedGate, _ => { });
failedApplier.Apply(single); failedApplier.Tick(DateTime.UtcNow);
Check(failedGate.Failed == 1 && failedGate.Prepared == 0 && failing.Abilities.All(a => a.Key.StartsWith("original")), "Ability failure restores signatures and prevents match preparation");
var timeoutGame = new FakeRuntime(); var timeoutGate = new FakeGate(); var timeoutApplier = new RuntimeDraftApplier(timeoutGame, timeoutGate, _ => { });
timeoutApplier.Apply(single); timeoutApplier.Tick(DateTime.UtcNow.AddMinutes(1));
Check(timeoutGate.Failed == 1 && timeoutGame.Adds == 0, "Hero timeout never replaces old hero abilities");
Expect(() => new RuntimeDraftApplier(new FakeRuntime(), new FakeGate(), _ => { }).Apply(single with
    { Players = [target with { Slots = [target.Slots[0] with { Slot = 0 }, .. target.Slots.Skip(1)] }] }), "Invalid runtime slots are rejected before mutation");
Expect(() => new RuntimeDraftApplier(new FakeRuntime(), new FakeGate(), _ => { }).Apply(single with
    { Players = [target with { SteamId64 = null }] }), "Unlinked identity never falls back to nickname");
var uncovered = new FakeRuntime { MissingAbility = target.Slots[0].AbilityKey };
Expect(() => new RuntimeDraftApplier(uncovered, new FakeGate(), _ => { }).Apply(single), "Unprecached donor ability is rejected before preparing heroes");
Check(uncovered.Begins == 0, "Missing resource coverage leaves the native player unchanged");
var preparation = new MatchPreparation();
Check(AbilityProgress.FromBits(0) == new AbilityProgress(false, 0, 0) && AbilityProgress.FromBits(1) == new AbilityProgress(true, 0, 1),
    "Ability levels distinguish locked from learned without a free upgrade");
Check(new[] { 3, 7, 15, 31 }.Select(b => AbilityProgress.FromBits(b).UpgradeTier).SequenceEqual(new[] { 1, 2, 3, 4 }) &&
    AbilityProgress.FromBits(0x101).UpgradeTier == 0, "Live ability tiers count native upgrade stars without counting unrelated flags");
Check(!preparation.CanStart(100, true), "Unprepared match cannot start");
preparation.Begin(100); preparation.Begin(120);
Check(preparation.Remaining(120) == 10 && !preparation.CanStart(129.99f, true), "All players receive exactly 30 seconds; repeated ticks do not restart preparation");
Check(!preparation.CanStart(130, false) && preparation.CanStart(130, true), "Preparation deadline never bypasses connected-roster validation");
preparation.Reset(); preparation.Begin(140);
Check(!preparation.CanStart(169, true) && preparation.CanStart(170, true), "A reset preparation receives a fresh full window");
var inGameOptions = app.Services.GetRequiredService<IOptions<InGameOptions>>().Value;
const string entryHost = "76561198000000021", entryFriend = "76561198000000022";
var entry = await backend.WebsiteEntry(entryHost, "create", "Game host", ct);
var entryToken = entry.Path.Split("gameEntry=")[1];
Check(entry.Path.StartsWith("create?gameEntry=") && !entry.Path.Contains(entryHost) && adapter.WebsiteEntryName(entryToken) == "Game host",
    "Game entry goes directly to the original create form with a private capability, not a claimed Steam ID");
var replacement = await backend.WebsiteEntry(entryHost, "create", "Game host", ct);
Expect(() => adapter.WebsiteEntryName(entryToken), "Replacing an entry invalidates the previous capability");
entryToken = replacement.Path.Split("gameEntry=")[1];
var websiteCreated = adapter.CreateFromWebsite(entryToken, "Original custom form", DeadlockTeam.Archmother,
    new DraftRoomConfig { DraftMode = DraftMode.Custom, MaxPlayers = 3, PickSeconds = 60 });
var entryRoom = rooms.GetRoom(websiteCreated.RoomCode)!;
Check(entryRoom.Config.DraftMode == DraftMode.Custom && entryRoom.Config.MaxPlayers == 3 && entryRoom.Config.PickSeconds == 60 &&
    entryRoom.Clients.Single().SteamId64 == entryHost && entryRoom.Clients.Single().Team == DeadlockTeam.Archmother,
    "Original custom settings create one domain room and automatically bind the verified game identity");
Expect(() => adapter.CreateFromWebsite(entryToken, "Replay", DeadlockTeam.HiddenKing, new()), "Website entry capability cannot create another room after consumption");
Check((await backend.State(entryHost, ct)).State!.Code == entryRoom.Code, "Game polling discovers the room created by the original website form");
Check((await backend.WebsiteEntry(entryHost, "create", "Game host", ct)).Path.StartsWith("room/" + entryRoom.Code + "/"),
    "Reconnecting resumes the private existing participant instead of opening a website homepage");
var legacyFriend = rooms.JoinRoom(entryRoom.Code, "Game friend", DeadlockTeam.HiddenKing);
var joinEntry = await backend.WebsiteEntry(entryFriend, "join", "Game friend", ct);
var joinToken = joinEntry.Path.Split("gameEntry=")[1];
Expect(() => adapter.JoinFromWebsite(joinToken, "BADCODE", DeadlockTeam.HiddenKing), "Website join preserves the domain rejection for unknown rooms");
var joinedEntry = adapter.JoinFromWebsite(joinToken, entryRoom.Code, DeadlockTeam.HiddenKing);
Check(entryRoom.Clients.Single(c => c.PlayerId == legacyFriend.PlayerId).SteamId64 is null &&
    entryRoom.Clients.Single(c => c.SteamId64 == entryFriend).DisplayName == "Game friend #2" && joinedEntry.Path.StartsWith("room/"),
    "Game join cannot hijack an unlinked legacy participant with the same nickname");
Expect(() => adapter.JoinFromWebsite(joinToken, entryRoom.Code, DeadlockTeam.HiddenKing), "Website join capabilities are single-use too");
await RejectAsync(() => backend.WebsiteEntry("76561198000000023", "https://example.com", "Bad route", ct), "Website entry only permits create and join routes");
const string queueOne = "76561198000000011", queueTwo = "76561198000000012", queueThree = "76561198000000013";
await RejectAsync(() => backend.Queue(queueOne, new("queueJoin", Name: "Queue player"), ct), "Public queue is opt-in");
inGameOptions.PublicQueueEnabled = true;
inGameOptions.PublicMatchSize = 2;
var queued = await backend.Queue(queueOne, new("queueJoin", Name: "Queue player"), ct);
Check(queued.Status == "Waiting" && queued.Waiting == 1 && queued.WebsitePath is null, "Public queue waits without inventing a draft room");
await RejectAsync(() => backend.WebsiteEntry(queueOne, "create", "Queued player", ct), "A queued player cannot enter a competing website custom lobby");
Check((await backend.Queue(queueOne, new("queueJoin", Name: "Changed name"), ct)).Waiting == 1, "Repeated joins do not duplicate a queued Steam identity");
await RejectAsync(() => backend.Command(queueOne, new("create", Name: "Duplicate membership"), ct), "Queued players cannot simultaneously create a custom room");
await RejectAsync(() => backend.Queue(host, new("queueJoin", Name: "Existing member"), ct), "Existing room participants cannot join public matchmaking");
Check((await backend.Queue(queueOne, new("queueLeave"), ct)).Status == "Idle", "Waiting players can cancel");
await backend.Queue(queueOne, new("queueJoin", Name: "Queue player"), ct);
adapter.ExpireConnections(DateTime.UtcNow.AddSeconds(40));
Check((await backend.Queue(queueOne, null, ct)).Status == "Idle", "Expired queue leases cannot produce ghost participants");
await backend.Queue(queueOne, new("queueJoin", Name: "Same name"), ct);
var matched = await backend.Queue(queueTwo, new("queueJoin", Name: "Same name"), ct);
var publicRoom = rooms.GetRoom(matched.RoomCode!)!;
Check(matched.Status == "Matched" && publicRoom.Clients.Count == 2 && publicRoom.Status == DraftRoomStatus.Drafting && publicRoom.IsPublicQueue,
    "A full public queue starts the existing website draft automatically");
Check(publicRoom.Clients.Select(c => c.SteamId64).ToHashSet().SetEquals([queueOne, queueTwo]) &&
    publicRoom.Clients.Select(c => c.Team).Distinct().Count() == 2, "Matched players have stable Steam mappings and balanced teams");
var firstMatch = await backend.Queue(queueOne, null, ct);
Check(firstMatch.RoomCode == matched.RoomCode && firstMatch.WebsitePath != matched.WebsitePath &&
    matched.WebsitePath!.Contains(publicRoom.Clients.Single(c => c.SteamId64 == queueTwo).PlayerId),
    "Each matched player gets only their private existing website participant URL");
Check((await backend.Queue(queueTwo, new("queueLeave"), ct)).Status == "Matched" && publicRoom.Clients.Count == 2,
    "A late queue cancellation cannot dismantle an already formed lobby");
await backend.Queue(queueThree, new("queueJoin", Name: "Next match"), ct);
await backend.Disconnect(queueThree, ct);
Check((await backend.Queue(queueThree, null, ct)).Status == "Idle", "Disconnect cancels a pending public queue entry");
Check((await backend.State(queueTwo, ct)).State!.Players.All(p => !p.Host), "Public participants have no visible host role");
Check(rooms.GetActiveDraftStats().Single(s => s.DraftCode == publicRoom.Code) is { Source: "public", HostName: "none" },
    "Active statistics identify public drafts without a host");
lock (publicRoom) { publicRoom.TimerPhase = DraftTimerPhase.Picking; publicRoom.TimerEndsUtc = DateTime.UtcNow.AddHours(1); }
for (var i = 0; !publicRoom.IsCompleted && i < 30; i++)
{
    var turnPlayer = publicRoom.Players.Single(p => p.SlotNumber == publicRoom.CurrentTurn!.SlotNumber);
    var identity = publicRoom.Clients.Single(c => c.PlayerId == turnPlayer.PlayerId).SteamId64!;
    var view = (await backend.State(identity, ct)).State!;
    await backend.Command(identity, new("pick", Key: view.Heroes.Concat(view.Abilities).First(c => c.CanPick).Id), ct);
}
Check(publicRoom.IsCompleted, "Public queue completes through ordinary server-side pick validation");
Check((await backend.State(queueTwo, ct)).State!.MatchReadyUtc == publicRoom.CompletedUtc!.Value.AddSeconds(15),
    "Every public player sees the same server deadline for PLAY DRAFT");
await RejectAsync(() => backend.Command(queueTwo, new("finalize"), ct), "Public launch cooldown is enforced before freezing the result");
publicRoom.CompletedUtc = DateTime.UtcNow.AddSeconds(-16);
await backend.Command(queueTwo, new("finalize"), ct);
Check(adapter.MatchResult(queueTwo, true) is { Source: "public", HostName: "none" } &&
    adapter.MatchResult(queueOne, true).ResultId == adapter.MatchResult(queueTwo, true).ResultId,
    "Any public player can start the same finalized match after cooldown");
inGameOptions.Enabled = false;
using (var disabled = await http.GetAsync(app.Urls.Single() + "/api/ingame/v1/state"))
    Check(disabled.StatusCode == HttpStatusCode.NotFound, "Disabled integration exposes no endpoint behavior");
await app.StopAsync();
Console.WriteLine("All integration checks passed (real HTTP/domain plus fake game boundary; no Deadlock runtime claim).");

static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
static void Expect(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
static async Task RejectAsync(Func<Task> action, string name) { try { await action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
sealed class FakeGate : IMatchStartGate
{
    public int Prepared, Failed;
    public void PlayersPrepared(string id) => Prepared++;
    public void ApplicationFailed(string id, string reason) => Failed++;
}
sealed class FakeRuntime : IGameDraftRuntime
{
    public bool Ready; public int Adds, Begins; public string? FailKey, MissingAbility;
    public List<RuntimeAbility> Abilities = Enumerable.Range(1, 4).Select(i => new RuntimeAbility(i, "original" + i, 1)).ToList();
    public bool IsConnected(string steam) => true;
    public bool SupportsHero(string key, int id) => true;
    public bool SupportsAbility(string key) => key != MissingAbility;
    public void BeginHero(string steam, string team, string hero) => Begins++;
    public bool HeroReady(string steam, string hero, string team) => Ready;
    public RuntimeAbility[] SignatureAbilities(string steam) => Abilities.ToArray();
    public bool RemoveAbility(string steam, string key) => Abilities.RemoveAll(a => a.Key == key) > 0;
    public bool AddAbility(string steam, RuntimeAbility ability) { Adds++; if (ability.Key == FailKey) return false; Abilities.Add(ability); return true; }
    public RuntimeProgression Progress = new(6, 4, 5);
    public RuntimeProgression Progression(string steam) => Progress;
    public void SetStartingProgression(string steam) => Progress = new(1, 0, 0);
}
