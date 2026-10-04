using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AbilityDraft.Contracts;
using abilitydraft.Models;

namespace abilitydraft.Services.InGame;

// All mutations delegate to the same service used by Blazor. This class owns only transport,
// verified identity binding, privacy projection and the immutable runtime handoff.
public sealed partial class InGameRoomAdapter(DraftRoomService rooms, ServerDeadlockDataService data,
    ILogger<InGameRoomAdapter> logger, Microsoft.Extensions.Options.IOptions<InGameOptions> options,
    abilitydraft.Services.ProjectLinksService projectLinks)
{
    private readonly object gate = new();
    private readonly byte[] opaqueKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, string> memberships = new();
    private readonly ConcurrentDictionary<string, DateTime> heartbeats = new();
    private readonly Dictionary<string, (string Code, string Player, DateTime Expires)> links = new();
    private readonly ConcurrentDictionary<string, DraftResult> results = new();

    private string Opaque(string code, string value) => Convert.ToHexString(
        HMACSHA256.HashData(opaqueKey, Encoding.UTF8.GetBytes(code + ":" + value)))[..24];

    // Called from an existing authenticated-by-session Blazor circuit, never from the public API.
    public string CreateLinkCode(string code, string playerId)
    {
        lock (gate)
        {
            var room = rooms.GetRoom(code) ?? throw new InvalidOperationException("Room not found.");
            lock (room)
            {
                var client = room.Clients.Single(c => c.PlayerId == playerId);
                if (client.SteamId64 is not null || room.RuntimeResultId is not null)
                    throw new InvalidOperationException("Participant is already linked or finalized.");
                foreach (var expired in links.Where(p => p.Value.Expires < DateTime.UtcNow ||
                             (p.Value.Code == room.Code && p.Value.Player == playerId)).Select(p => p.Key).ToArray())
                    links.Remove(expired);
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                links[token] = (room.Code, playerId, DateTime.UtcNow.AddMinutes(5));
                return token;
            }
        }
    }

    public CommandReply Execute(string steam, RoomCommand command)
    {
        lock (gate)
        {
            ExpirePublicQueue(DateTime.UtcNow);
            if (publicQueue.ContainsKey(steam))
                throw new InvalidOperationException("Cancel the public queue before using a custom room.");
            if (command.Operation == "link")
            {
                if (!links.TryGetValue(command.Key ?? "", out var link) || link.Expires < DateTime.UtcNow)
                    throw new InvalidOperationException("Link code is invalid or expired.");
                EnsureNoMembership(steam);
                var room = rooms.GetRoom(link.Code) ?? throw new InvalidOperationException("Room not found.");
                lock (room)
                {
                    var client = room.Clients.SingleOrDefault(c => c.PlayerId == link.Player)
                        ?? throw new InvalidOperationException("Participant has left the room.");
                    Bind(room, client, steam, fromBrowser: true);
                    links.Remove(command.Key!);
                }
            }
            else if (command.Operation is "create" or "join" or "spectate")
            {
                // A Steam identity resumes its exact participant, never the legacy nickname reconnect flow.
                if (HasMembership(steam) && memberships.TryGetValue(steam, out var existing))
                {
                    if (command.Operation == "create" || !string.Equals(existing, command.RoomCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Leave the current room first.");
                }
                else
                {
                    var resumeRoom = rooms.GetRoom(command.RoomCode ?? "");
                    if (command.Operation != "create" && resumeRoom is not null)
                    {
                        lock (resumeRoom)
                        {
                            var resumeClient = resumeRoom.Clients.SingleOrDefault(c => c.SteamId64 == steam);
                            if (resumeClient is not null)
                            {
                                memberships[steam] = resumeRoom.Code;
                                heartbeats[steam] = DateTime.UtcNow;
                                rooms.MarkPlayerConnected(resumeRoom.Code, resumeClient.PlayerId, inGame: true);
                                return new(Project(resumeRoom, resumeClient));
                            }
                        }
                    }
                    if (!data.Current.IsLoaded) throw new InvalidOperationException("Draft data is not available.");
                    var team = command.Operation == "spectate" ? DeadlockTeam.Spectator : ParseTeam(command.Team);
                    JoinRoomResult joined;
                    if (command.Operation == "create")
                    {
                        if (!Enum.TryParse<DraftMode>(command.Mode ?? "FreePick", out var mode) || !Enum.IsDefined(mode))
                            throw new InvalidOperationException("Invalid draft mode.");
                        joined = rooms.CreateRoom("In-game Ability Draft", CleanName(command.Name), team,
                            new DraftRoomConfig { DraftMode = mode });
                    }
                    else
                    {
                        var target = rooms.GetRoom(command.RoomCode ?? "") ?? throw new InvalidOperationException("Room not found.");
                        lock (target)
                        {
                            if (target.IsCompleted && team != DeadlockTeam.Spectator)
                                throw new InvalidOperationException("Only drafted players can return to a playing slot.");
                            if (target.Status != DraftRoomStatus.Lobby && team != DeadlockTeam.Spectator)
                                throw new InvalidOperationException("Use a website link code to attach an existing participant.");
                            joined = rooms.JoinRoom(target.Code, CleanName(command.Name), team);
                        }
                    }
                    var room = rooms.GetRoom(joined.RoomCode)!;
                    if (command.Operation == "create") room.InGameManaged = true;
                    lock (room) Bind(room, room.Clients.Single(c => c.PlayerId == joined.PlayerId), steam);
                    logger.LogInformation("In-game room join {Room} ({Operation})", room.Code, command.Operation);
                }
            }
            else
            {
                var (room, client) = Member(steam);
                lock (room)
                {
                    EnsureStillMember(room, client);
                    switch (command.Operation)
                    {
                        case "ready": rooms.SetReady(room.Code, client.PlayerId, command.Ready); break;
                        case "team": rooms.ChangeTeam(room.Code, client.PlayerId, ParseTeam(command.Team)); break;
                        case "start": rooms.StartDraft(room.Code, client.PlayerId); break;
                        case "pick": rooms.Pick(room.Code, client.PlayerId, ResolveCard(room, command.Key)); break;
                        case "chat":
                            var composer = command.Key is null ? null : NativeChat(steam);
                            if (command.Key is not null && (composer?.Id != command.Key || composer.RoomCode != room.Code))
                                throw new InvalidOperationException("Chat entry expired. Open Message again.");
                            var chatSession = composer is null ? null : nativeChats[steam];
                            if (chatSession is not null && command.ChatSequence != 0)
                            {
                                // A lost acknowledgement can be retried without posting twice.
                                if (command.ChatSequence == chatSession.LastSequence && command.Text == chatSession.LastText) break;
                                if (command.ChatSequence != chatSession.LastSequence + 1)
                                    throw new InvalidOperationException("Chat message is out of order. Open Message again.");
                            }
                            var scope = client.Team == DeadlockTeam.Spectator ? DraftChatScope.Spectators :
                                (composer?.Scope ?? command.Scope) == "All" ? DraftChatScope.All : DraftChatScope.Allies;
                            rooms.SendChatMessage(room.Code, client.PlayerId, scope, command.Text ?? "");
                            if (chatSession is not null)
                            {
                                if (command.ChatSequence == 0) nativeChats.Remove(steam); // Older clients use a single submission.
                                else nativeChats[steam] = chatSession with { LastSequence = command.ChatSequence, LastText = command.Text, Expires = DateTime.UtcNow.AddMinutes(5) };
                            }
                            break;
                        case "recommend": case "want":
                            rooms.SendQuickChat(room.Code, client.PlayerId, ResolveCard(room, command.Key),
                                command.Operation == "want" ? DraftQuickChatAction.WantThis : DraftQuickChatAction.Recommend); break;
                        case "reorder": rooms.ReorderRegularAbility(room.Code, client.PlayerId, command.Slot, command.From, command.To); break;
                        case "finalize": Finalize(room, client); break;
                        case "leave":
                            rooms.MarkPlayerDisconnected(room.Code, client.PlayerId, inGame: true);
                            memberships.TryRemove(steam, out _);
                            heartbeats.TryRemove(steam, out _);
                            return new(null);
                        case "reconnect": rooms.MarkPlayerConnected(room.Code, client.PlayerId, inGame: true); break;
                        default: throw new InvalidOperationException("Unknown operation.");
                    }
                }
            }
        }
        return new(State(steam));
    }

    public RoomView? CurrentState(string steam)
    {
        lock (gate) return HasMembership(steam) ? State(steam) : null;
    }

    public RoomView State(string steam)
    {
        var (room, client) = Member(steam);
        lock (room)
        {
            EnsureStillMember(room, client);
            heartbeats[steam] = DateTime.UtcNow;
            rooms.MarkPlayerConnected(room.Code, client.PlayerId, inGame: true);
            logger.LogDebug("In-game draft state sync {Room} turn {Turn}", room.Code, room.CurrentTurnIndex);
            return Project(room, client);
        }
    }

    public void Disconnect(string steam)
    {
        lock (gate) publicQueue.Remove(steam);
        try { var (room, client) = Member(steam); rooms.MarkPlayerDisconnected(room.Code, client.PlayerId, inGame: true); }
        catch (InvalidOperationException) { }
    }

    public DraftResult Result(string steam, bool logRetrieval = true)
    {
        var (room, client) = Member(steam);
        lock (room)
        {
            EnsureStillMember(room, client);
            if (client.Team == DeadlockTeam.Spectator || room.RuntimeResultId is null || !results.TryGetValue(room.RuntimeResultId, out var result))
                throw new InvalidOperationException("No finalized runtime result is available for this participant.");
            if (logRetrieval) logger.LogInformation("Final draft retrieval {Room} result {Result}", room.Code, result.ResultId);
            return result;
        }
    }

    public DraftResult MatchResult(string steam, bool requireHost)
    {
        lock (gate)
        {
            var (room, client) = Member(steam);
            lock (room)
            {
                EnsureStillMember(room, client);
                if (requireHost) EnsureCanLaunch(room, client);
                if (!requireHost && client.Team == DeadlockTeam.Spectator)
                {
                    if (room.RuntimeResultId is not null && results.TryGetValue(room.RuntimeResultId, out var result))
                        return result; // Used internally to authorize the address; never returned by /result.
                    throw new InvalidOperationException("The match is not ready yet.");
                }
                return Result(steam, logRetrieval: requireHost);
            }
        }
    }

    public DraftArchive GenerateArchive(string steam)
    {
        var (room, client) = Member(steam);
        lock (room)
        {
            EnsureStillMember(room, client);
            if (!client.IsHost || client.Team == DeadlockTeam.Spectator)
                throw new InvalidOperationException("Only the playing room host can generate the draft VPK.");
            logger.LogInformation("In-game VPK generation requested for {Room}", room.Code);
            // GenerateFiles holds the room lock during packing.
            rooms.GenerateZip(room.Code, client.PlayerId);
            heartbeats[steam] = DateTime.UtcNow;
            var vpk = room.GeneratedVpk;
            var bytes = vpk ?? room.GeneratedZip ?? throw new InvalidOperationException("Draft archive generation failed.");
            var format = vpk is null ? "zip" : "vpk";
            logger.LogInformation("In-game archive ready for {Room}: {Format}, {Bytes} bytes", room.Code, format, bytes.Length);
            // Do not send compiler paths/logs or server-side filenames to clients.
            return new($"ability-draft-{room.Code}.{format}", format, bytes);
        }
    }

    private (DraftRoom Room, DraftClientSession Client) Member(string steam)
    {
        // Plugin restart can resume the same backend membership by verified Steam ID.
        if (!memberships.TryGetValue(steam, out var code)) throw new InvalidOperationException("Join or create a room first.");
        var room = rooms.GetRoom(code) ?? throw new InvalidOperationException("Room expired.");
        lock (room)
            return (room, room.Clients.SingleOrDefault(c => c.SteamId64 == steam)
                ?? throw new InvalidOperationException("Participant left or was removed from this room."));
    }

    private void EnsureNoMembership(string steam)
    {
        if (HasMembership(steam))
            throw new InvalidOperationException("Leave the current room first.");
    }

    private bool HasMembership(string steam)
    {
        if (!memberships.TryGetValue(steam, out var code) || rooms.GetRoom(code) is not { } room) return false;
        lock (room) return room.Clients.Any(c => c.SteamId64 == steam);
    }

    private static void EnsureStillMember(DraftRoom room, DraftClientSession client)
    {
        if (!room.Clients.Contains(client)) throw new InvalidOperationException("Participant was removed from this room.");
    }

    public void ExpireConnections(DateTime now)
    {
        lock (gate)
        {
            ExpirePublicQueue(now);
            foreach (var (steam, seen) in heartbeats.ToArray())
            {
                if (!HasMembership(steam))
                {
                    heartbeats.TryRemove(steam, out _);
                    memberships.TryRemove(steam, out _);
                    continue;
                }
                var room = rooms.GetRoom(memberships.GetValueOrDefault(steam) ?? "");
                if (room is null) continue;
                lock (room)
                {
                    var client = room.Clients.SingleOrDefault(c => c.SteamId64 == steam);
                    if (client is null) continue;
                    if (heartbeats.GetValueOrDefault(steam) > now.AddSeconds(-30)) continue;
                    rooms.MarkPlayerDisconnected(room.Code, client.PlayerId, inGame: true);
                    heartbeats.TryRemove(steam, out _);
                    logger.LogInformation("In-game connection lease expired in {Room}", room.Code);
                }
            }
            foreach (var id in results.Where(p => rooms.GetRoom(p.Value.RoomCode) is null).Select(p => p.Key).ToArray())
                results.TryRemove(id, out _);
            foreach (var token in links.Where(p => p.Value.Expires < now).Select(p => p.Key).ToArray()) links.Remove(token);
            foreach (var token in browserEntries.Where(p => p.Value.Expires < now).Select(p => p.Key).ToArray()) browserEntries.Remove(token);
        }
    }

    private void Bind(DraftRoom room, DraftClientSession client, string steam, bool fromBrowser = false)
    {
        if ((room.RuntimeResultId is not null && client.Team != DeadlockTeam.Spectator) || client.SteamId64 is not null || room.Clients.Any(c => c.SteamId64 == steam))
            throw new InvalidOperationException("Participant cannot be linked.");
        client.SteamId64 = steam;
        if (!fromBrowser) client.IsBrowserConnected = false;
        memberships[steam] = room.Code;
        heartbeats[steam] = DateTime.UtcNow;
        rooms.MarkPlayerConnected(room.Code, client.PlayerId, inGame: true);
        logger.LogInformation("Player identity mapping established in {Room} for participant {Participant}", room.Code, Opaque(room.Code, client.PlayerId));
    }

    private string ResolveCard(DraftRoom room, string? id) => room.DraftHeroPoolKeys.Concat(room.DraftAbilityPoolKeys)
        .FirstOrDefault(key => Opaque(room.Code, key) == id) ?? throw new InvalidOperationException("Unknown draft card.");

    private RoomView Project(DraftRoom room, DraftClientSession viewer)
    {
        var blind = room.Config.DraftMode == DraftMode.Custom && room.Config.BlindDraft && !room.IsCompleted;
        var definitions = room.DeadlockData.Abilities.ToDictionary(a => a.Key);
        bool Hidden(string key) => blind && definitions.TryGetValue(key, out var a) && a.PickKind == DraftPickKind.RegularAbility;
        string VisibleKey(string key) => Hidden(key) ? "?" : key;
        Participant Player(DraftClientSession c)
        {
            var slot = room.Players.FirstOrDefault(p => p.PlayerId == c.PlayerId);
            return new(Opaque(room.Code, c.PlayerId), c.DisplayName, c.Team.ToString(), c.IsHost && !room.IsPublicQueue,
                c.IsReady, c.IsConnected, slot?.SlotNumber, slot?.HeroKey,
                slot is null ? [] : OrderedKeys(room, slot).Select(VisibleKey).ToArray(),
                slot is not null && room.RuntimeResultId is null && rooms.CanPlayerReorderSlot(room, viewer.PlayerId, slot));
        }
        var players = room.Clients.Select(Player).Concat(room.Players.Where(p => p.IsBot).Select(p =>
            new Participant("bot-" + p.SlotNumber, p.NameOrFallback, p.Team.ToString(), false, true, true,
                p.SlotNumber, p.HeroKey, OrderedKeys(room, p).Select(VisibleKey).ToArray(), false))).ToArray();
        var heroes = room.DeadlockData.Heroes.Where(h => room.DraftHeroPoolKeys.Contains(h.Key))
            .Select(h => new Card(Opaque(room.Code, h.Key), h.DisplayName, "Hero", h.Key, h.Key,
                room.PickedHeroKeys.Contains(h.Key), rooms.CanPlayerPickKey(room, viewer.PlayerId, h.Key), false)).ToArray();
        var abilities = room.DraftAbilityPoolKeys.Where(definitions.ContainsKey).Select(k => definitions[k])
            .Select(a => new Card(Opaque(room.Code, a.Key), Hidden(a.Key) ? "Unknown ability" : a.DisplayName,
                a.PickKind.ToString(), Hidden(a.Key) ? null : a.Key, Hidden(a.Key) ? null : a.SourceHeroKey,
                room.PickedAbilityKeys.Contains(a.Key), rooms.CanPlayerPickKey(room, viewer.PlayerId, a.Key), Hidden(a.Key)))
            .OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
        // Match browser scopes; never include raw room errors, paths, ZIP/VPK bytes, IDs or Steam IDs.
        var chat = room.Config.DisableChat ? [] : room.ChatMessages.Where(m =>
                viewer.Team == DeadlockTeam.Spectator || m.Scope == DraftChatScope.All ||
                (m.Scope == DraftChatScope.Allies && m.SenderTeam == viewer.Team) ||
                (room.IsCompleted && m.Scope == DraftChatScope.Spectators && m.SentUtc > room.CompletedUtc))
            .TakeLast(80).Select(m => new ChatView(m.Id, Opaque(room.Code, m.SenderPlayerId), m.SenderName,
                m.SenderTeam.ToString(), m.Scope.ToString(), viewer.Team == DeadlockTeam.Spectator ? Censor(m.Text) : m.Text)).ToArray();
        return new(1, room.Code, room.Name, room.Status.ToString(), room.Config.DraftMode.ToString(),
            Opaque(room.Code, viewer.PlayerId), DateTime.UtcNow, room.TimerEndsUtc, room.TimerPhase.ToString(), room.CurrentTurnIndex,
            players, heroes, abilities, room.TurnOrder.Select(t => new TurnView(t.SlotNumber, t.PickKind.ToString(), t.RoundNumber)).ToArray(),
            room.PickHistory.Select(p => new PickView(p.SlotNumber, p.PickKind.ToString(), Opaque(room.Code, p.PickedKey))).ToArray(),
            chat, room.Config.DisableChat, blind, room.Config.FlexibleUltimateSlots, room.RuntimeResultId,
            room.IsPublicQueue, room.IsPublicQueue ? room.CompletedUtc?.AddSeconds(15) : null);
    }

    private void Finalize(DraftRoom room, DraftClientSession client)
    {
        EnsureCanLaunch(room, client);
        if (!room.IsCompleted) throw new InvalidOperationException("Complete the draft first.");
        if (room.RuntimeResultId is not null) return;
        var players = DraftTurnService.ActiveSlots(room)
            .Where(p => !room.InGameManaged || p.IsBot || room.Clients.Any(c => c.PlayerId == p.PlayerId)).Select(p =>
        {
            var hero = room.DeadlockData.Heroes.SingleOrDefault(h => h.Key == p.HeroKey)
                ?? throw new InvalidOperationException("Missing hero in final draft.");
            var keys = OrderedKeys(room, p);
            if (keys.Length != 4 || keys.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Runtime integration requires four filled ability slots.");
            var slots = keys.Select((key, i) =>
            {
                var ability = room.DeadlockData.Abilities.SingleOrDefault(a => a.Key == key)
                    ?? throw new InvalidOperationException("Missing ability in final draft.");
                return new DraftAbilitySlot(i + 1, key, ability.PickKind == DraftPickKind.UltimateAbility);
            }).ToArray();
            // VPK can clone/retype abilities. Runtime API does not promise equivalent semantics.
            if (slots.Select(s => s.AbilityKey).Distinct().Count() != 4 || slots.Any(s => s.IsUltimate != (s.Slot == 4)))
                throw new InvalidOperationException("This loadout requires ability cloning or type conversion; use the existing VPK workflow.");
            var identity = room.Clients.SingleOrDefault(c => c.PlayerId == p.PlayerId)?.SteamId64;
            return new DraftParticipantResult(Opaque(room.Code, p.PlayerId ?? "bot-" + p.SlotNumber), identity,
                p.NameOrFallback, p.Team.ToString(), hero.Key, hero.Id, p.Loadout.Weapon, p.IsBot, slots);
        }).ToArray();
        var id = Guid.NewGuid().ToString("N");
        results[id] = new(1, id, room.Code, room.CompletedUtc ?? DateTime.UtcNow, DateTime.UtcNow, players)
        {
            Spectators = room.Clients.Where(c => c.Team == DeadlockTeam.Spectator && c.SteamId64 is not null)
                .Select(c => new DraftSpectator(Opaque(room.Code, c.PlayerId), c.SteamId64!, c.DisplayName)).ToArray(),
            Source = room.Source, DraftMode = room.Config.DraftMode.ToString(), BotsEnabled = room.Config.AllowEmptySlotsAsBots,
            HostName = room.IsPublicQueue ? "none" : client.DisplayName
        };
        rooms.FinalizeRuntimeResult(room.Code, room.IsPublicQueue ? room.Clients.First(c => c.IsHost).PlayerId : client.PlayerId, id);
        logger.LogInformation("Finalized runtime draft {Room} result {Result}", room.Code, id);
    }

    private static void EnsureCanLaunch(DraftRoom room, DraftClientSession client)
    {
        if (client.Team == DeadlockTeam.Spectator || (!room.IsPublicQueue && !client.IsHost))
            throw new InvalidOperationException("Only a public-match player or the custom lobby host can start the match.");
        if (!room.IsCompleted) throw new InvalidOperationException("Complete the draft first.");
        if (room.IsPublicQueue && (room.CompletedUtc is null || DateTime.UtcNow < room.CompletedUtc.Value.AddSeconds(15)))
            throw new InvalidOperationException("The public match is ready 15 seconds after the draft ends.");
    }

    public DraftSpectator? MatchSpectator(string steam)
    {
        lock (gate)
        {
            var (room, client) = Member(steam);
            lock (room) return client.Team == DeadlockTeam.Spectator ? new(Opaque(room.Code, client.PlayerId), steam, client.DisplayName) : null;
        }
    }

    private static string[] OrderedKeys(DraftRoom room, DraftPlayerSlot player) => room.Config.FlexibleUltimateSlots
        ? player.Loadout.RegularAbilities.ToArray()
        : player.Loadout.RegularAbilities.Concat([player.Loadout.Ultimate ?? ""]).ToArray();
    private static string CleanName(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 80
        ? throw new InvalidOperationException("Enter a name of 1 to 80 characters.") : name.Trim();
    private static DeadlockTeam ParseTeam(string? team) => team switch
    {
        null or "HiddenKing" => DeadlockTeam.HiddenKing,
        "Archmother" => DeadlockTeam.Archmother,
        _ => throw new InvalidOperationException("Invalid team.")
    };
    private static string Censor(string text)
    {
        var index = text.IndexOf("[A:1:", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? text : text[..index] + "[server address hidden]";
    }
}
