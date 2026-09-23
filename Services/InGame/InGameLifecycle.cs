using abilitydraft.Models;

namespace abilitydraft.Services.InGame;

public sealed partial class InGameRoomAdapter
{
    private readonly Dictionary<string, DateTime> emptyRooms = new();

    // Returns the reserved result, if any, so its worker can revoke this identity too.
    public string? Abandon(string steam, string? expectedResult = null)
    {
        lock (gate)
        {
            publicQueue.Remove(steam);
            if (!HasMembership(steam)) { ForgetMembership(steam); return null; }
            var (room, client) = Member(steam);
            lock (room)
            {
                if (expectedResult is not null && room.RuntimeResultId != expectedResult)
                    throw new InvalidOperationException("This match is no longer your current room.");
                var result = room.RuntimeResultId;
                rooms.AbandonInGameParticipant(room.Code, client.PlayerId);
                ForgetMembership(steam);
                if (room.InGameManaged && room.Clients.Count == 0)
                    CloseManagedRoom(room, "All players left this draft.");
                logger.LogInformation("In-game participant abandoned room {Room}", room.Code);
                return result;
            }
        }
    }

    // Worker state is captured before taking the adapter lock: no coordinator/adapter lock cycle.
    public void ReconcileMatches(IReadOnlyDictionary<string, string> workerStates, DateTime now)
    {
        lock (gate)
        {
            foreach (var code in memberships.Values.Distinct().ToArray())
            {
                var room = rooms.GetRoom(code);
                if (room is null) continue;
                lock (room)
                {
                    if (!room.InGameManaged) continue;
                    var state = room.RuntimeResultId is { } id ? workerStates.GetValueOrDefault(id) : null;
                    if (state == "Completed") { CloseManagedRoom(room, "The match has ended. You can start a new draft."); continue; }
                    // A transfer closes the drafting connection. The reserved worker owns presence then.
                    if (state is "Starting" or "Ready" or "Playing") { emptyRooms.Remove(code); continue; }
                    var connected = room.Clients.Any(c => c.SteamId64 is null ? c.IsConnected : c.IsInGameConnected);
                    if (connected) { emptyRooms.Remove(code); continue; }
                    if (!emptyRooms.TryGetValue(code, out var since)) emptyRooms[code] = now;
                    else if (now - since >= TimeSpan.FromSeconds(15)) CloseManagedRoom(room, "All players left this draft.");
                }
            }
            foreach (var steam in memberships.Keys.Where(s => !HasMembership(s)).ToArray()) ForgetMembership(steam);
        }
    }

    private void CloseManagedRoom(DraftRoom room, string notice)
    {
        rooms.CloseInGameRoom(room.Code, notice);
        foreach (var steam in memberships.Where(p => p.Value == room.Code).Select(p => p.Key).ToArray()) ForgetMembership(steam);
        if (room.RuntimeResultId is { } id) results.TryRemove(id, out _);
        emptyRooms.Remove(room.Code);
    }

    private void ForgetMembership(string steam)
    {
        memberships.TryRemove(steam, out _);
        heartbeats.TryRemove(steam, out _);
        publicAssignments.Remove(steam);
        foreach (var token in browserEntries.Where(p => p.Value.Steam == steam).Select(p => p.Key).ToArray()) browserEntries.Remove(token);
    }
}
