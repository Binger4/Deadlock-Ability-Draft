using AbilityDraft.Contracts;
using abilitydraft.Models;

namespace abilitydraft.Services.InGame;

public sealed partial class InGameRoomAdapter
{
    private sealed record QueuePlayer(string Name, long Order, DateTime Seen);
    private readonly Dictionary<string, QueuePlayer> publicQueue = new();
    private readonly Dictionary<string, string> publicAssignments = new();
    private long queueOrder;

    // All queue and room admission changes share the adapter's gate. Drafting stays in DraftRoomService.
    public QueueReply Queue(string steam, string operation, string? name = null)
    {
        lock (gate)
        {
            if (!options.Value.PublicQueueEnabled) throw new InvalidOperationException("Public queue is disabled.");
            var size = options.Value.PublicMatchSize;
            if (size is < 2 or > 12 || size % 2 != 0)
                throw new InvalidOperationException("Public match size must be an even number from 2 to 12.");
            ExpirePublicQueue(DateTime.UtcNow);
            if (operation == "queueLeave")
            {
                publicQueue.Remove(steam);
                // Cancelling a wait never removes a participant from a room that has already formed.
                return QueueView(steam, size);
            }
            if (operation is not ("queueJoin" or "queueStatus")) throw new InvalidOperationException("Unknown queue operation.");
            if (HasMembership(steam))
            {
                if (publicAssignments.GetValueOrDefault(steam) == memberships.GetValueOrDefault(steam))
                    return QueueView(steam, size);
                throw new InvalidOperationException("Leave the custom room before joining the public queue.");
            }
            if (operation == "queueJoin" && !publicQueue.ContainsKey(steam))
            {
                if (!data.Current.IsLoaded) throw new InvalidOperationException("Draft data is not available.");
                publicQueue[steam] = new(CleanName(name), ++queueOrder, DateTime.UtcNow);
                logger.LogInformation("Public queue joined; waiting {Count}/{Size}", publicQueue.Count, size);
            }
            if (publicQueue.TryGetValue(steam, out var waiting))
                publicQueue[steam] = waiting with { Seen = DateTime.UtcNow };
            if (publicQueue.Count >= size) FormPublicRoom(size);
            return QueueView(steam, size);
        }
    }

    public WebsiteEntry Website(string steam)
    {
        lock (gate)
        {
            var (room, client) = Member(steam);
            lock (room)
            {
                EnsureStillMember(room, client);
                var page = room.Status == DraftRoomStatus.Lobby ? "lobby" : "draft";
                return new($"room/{Uri.EscapeDataString(room.Code)}/{page}?playerId={Uri.EscapeDataString(client.PlayerId)}&inGame=true");
            }
        }
    }

    private QueueReply QueueView(string steam, int size)
    {
        if (HasMembership(steam) && publicAssignments.GetValueOrDefault(steam) == memberships.GetValueOrDefault(steam))
        {
            heartbeats[steam] = DateTime.UtcNow;
            return new("Matched", publicQueue.Count, size, null, memberships[steam], Website(steam).Path);
        }
        var ordered = publicQueue.OrderBy(p => p.Value.Order).Select(p => p.Key).ToArray();
        var index = Array.IndexOf(ordered, steam);
        return new(index < 0 ? "Idle" : "Waiting", ordered.Length, size, index < 0 ? null : index + 1);
    }

    private void FormPublicRoom(int size)
    {
        var batch = publicQueue.OrderBy(p => p.Value.Order).Take(size).ToArray();
        var first = batch[0];
        var joined = rooms.CreateRoom("Public Ability Draft", first.Value.Name, DeadlockTeam.HiddenKing,
            new DraftRoomConfig { DraftMode = DraftMode.FreePick, MaxPlayers = size });
        var room = rooms.GetRoom(joined.RoomCode)!;
        room.InGameManaged = true;
        room.IsPublicQueue = true;
        lock (room)
        {
            Bind(room, room.Clients.Single(c => c.PlayerId == joined.PlayerId), first.Key);
            for (var i = 1; i < batch.Length; i++)
            {
                var entry = batch[i];
                var displayName = entry.Value.Name;
                var suffix = 1;
                while (room.Clients.Any(c => c.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                    displayName = entry.Value.Name + " #" + ++suffix;
                var player = rooms.JoinRoom(room.Code, displayName,
                    i % 2 == 0 ? DeadlockTeam.HiddenKing : DeadlockTeam.Archmother);
                Bind(room, room.Clients.Single(c => c.PlayerId == player.PlayerId), entry.Key);
            }
            foreach (var entry in batch)
            {
                publicQueue.Remove(entry.Key);
                publicAssignments[entry.Key] = room.Code;
            }
            // A filled public queue starts the existing website draft without appointing a player host.
            foreach (var client in room.Clients) rooms.SetReady(room.Code, client.PlayerId, true);
            rooms.StartDraft(room.Code, joined.PlayerId);
        }
        logger.LogInformation("Public queue formed website room {Room} with {Count} players", room.Code, size);
    }

    private void ExpirePublicQueue(DateTime now)
    {
        foreach (var steam in publicQueue.Where(p => p.Value.Seen < now.AddSeconds(-30)).Select(p => p.Key).ToArray())
            publicQueue.Remove(steam);
        foreach (var steam in publicAssignments.Where(p => !HasMembership(p.Key) || memberships.GetValueOrDefault(p.Key) != p.Value).Select(p => p.Key).ToArray())
            publicAssignments.Remove(steam);
    }
}
