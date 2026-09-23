using System.Security.Cryptography;
using AbilityDraft.Contracts;
using abilitydraft.Models;

namespace abilitydraft.Services.InGame;

public sealed partial class InGameRoomAdapter
{
    private sealed record BrowserEntry(string Steam, string Name, DateTime Expires);
    private readonly Dictionary<string, BrowserEntry> browserEntries = new();
    private readonly Dictionary<string, (ExternalWebsiteLink Link, DateTime Expires)> externalLinks = new();
    public void OpenWebsiteLink(string token, int link)
    {
        lock (gate)
        {
            var entry = BrowserAdmission(token);
            var links = projectLinks.FooterLinks();
            if (link < 0 || link >= links.Count) throw new InvalidOperationException("Unknown project link.");
            foreach (var expired in externalLinks.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray()) externalLinks.Remove(expired);
            externalLinks[entry.Steam] = (new(Guid.NewGuid().ToString("N"), link < 3 ? links[link].Url : $"project-links/{link}"), DateTime.UtcNow.AddSeconds(15));
        }
    }
    public ExternalWebsiteLink? WebsiteLink(string steam)
    {
        lock (gate) return externalLinks.TryGetValue(steam, out var link) && link.Expires > DateTime.UtcNow ? link.Link : null;
    }

    public void OpenPresetLink(string path, string? gameEntry = null, string? code = null, string? playerId = null)
    {
        if (!path.StartsWith("presets/", StringComparison.Ordinal) || path.Length != 72 || !path[8..].All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Invalid preset link.");
        lock (gate)
        {
            string steam;
            if (gameEntry is not null) steam = BrowserAdmission(gameEntry).Steam;
            else
            {
                var room = rooms.GetRoom(code ?? "") ?? throw new InvalidOperationException("Room not found.");
                lock (room)
                {
                    var client = room.Clients.SingleOrDefault(c => c.PlayerId == playerId);
                    if (!options.Value.Enabled || room.Status != DraftRoomStatus.Lobby || client is not { IsHost: true, SteamId64: not null })
                        throw new InvalidOperationException("Only the lobby host can transfer presets.");
                    steam = client.SteamId64;
                }
            }
            externalLinks[steam] = (new(Guid.NewGuid().ToString("N"), path), DateTime.UtcNow.AddSeconds(30));
        }
    }

    // Minted only by the authenticated game-server endpoint. The VPK never supplies a Steam ID.
    // A private, short-lived capability lets the existing Blazor forms bind their new participant.
    public WebsiteEntry CreateWebsiteEntry(string steam, string page, string? name)
    {
        lock (gate)
        {
            if (!options.Value.Enabled) throw new InvalidOperationException("In-game integration is disabled.");
            if (page is not ("create" or "join")) throw new InvalidOperationException("Invalid website entry.");
            if (HasMembership(steam)) return Website(steam);
            EnsureNotQueued(steam);
            foreach (var key in browserEntries.Where(p => p.Value.Expires < DateTime.UtcNow || p.Value.Steam == steam).Select(p => p.Key).ToArray())
                browserEntries.Remove(key);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            browserEntries[token] = new(steam, CleanName(name), DateTime.UtcNow.AddMinutes(15));
            return new($"{page}?gameEntry={token}");
        }
    }

    public string WebsiteEntryName(string token)
    {
        lock (gate) return BrowserAdmission(token).Name;
    }

    public JoinRoomResult CreateFromWebsite(string token, string roomName, DeadlockTeam team, DraftRoomConfig config)
    {
        lock (gate)
        {
            var entry = BrowserAdmission(token);
            EnsureNoMembership(entry.Steam);
            EnsureNotQueued(entry.Steam);
            var joined = rooms.CreateRoom(roomName, entry.Name, team, config);
            var room = rooms.GetRoom(joined.RoomCode)!;
            room.InGameManaged = true;
            lock (room) Bind(room, room.Clients.Single(c => c.PlayerId == joined.PlayerId), entry.Steam, fromBrowser: true);
            browserEntries.Remove(token);
            logger.LogInformation("In-game website created and linked room {Room}", room.Code);
            return joined;
        }
    }

    public WebsiteEntry JoinFromWebsite(string token, string code, DeadlockTeam team)
    {
        lock (gate)
        {
            var entry = BrowserAdmission(token);
            var name = entry.Name;
            // Never let a Steam-authenticated join take over an unrelated legacy nickname session.
            var room = rooms.GetRoom(code);
            if (room is not null)
            {
                lock (room)
                {
                    var suffix = 1;
                    while (room.Clients.Any(c => c.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        name = entry.Name + " #" + ++suffix;
                }
            }
            Execute(entry.Steam, new(team == DeadlockTeam.Spectator ? "spectate" : "join", code, name, team.ToString()));
            var (joinedRoom, client) = Member(entry.Steam);
            lock (joinedRoom) rooms.MarkPlayerConnected(joinedRoom.Code, client.PlayerId);
            browserEntries.Remove(token);
            return Website(entry.Steam);
        }
    }

    private BrowserEntry BrowserAdmission(string token)
    {
        if (!options.Value.Enabled || !browserEntries.TryGetValue(token, out var entry) || entry.Expires < DateTime.UtcNow)
            throw new InvalidOperationException("Game entry expired. Use Create lobby or Join lobby in the game toolbar again.");
        return entry;
    }

    private void EnsureNotQueued(string steam)
    {
        ExpirePublicQueue(DateTime.UtcNow);
        if (publicQueue.ContainsKey(steam)) throw new InvalidOperationException("Cancel the public queue before using a custom room.");
    }
}
