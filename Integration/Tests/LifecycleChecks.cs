using abilitydraft.Services;
using abilitydraft.Services.InGame;
using abilitydraft.Models;

static class LifecycleChecks
{
    public static void Run(InGameRoomAdapter adapter, DraftRoomService rooms)
    {
        const string first = "76561198000000401", second = "76561198000000402";
        var one = adapter.Execute(first, new("create", Name: "Lifecycle host", Team: "HiddenKing")).State!;
        adapter.Execute(second, new("join", RoomCode: one.Code, Name: "Lifecycle guest", Team: "Archmother"));
        adapter.Disconnect(first);
        adapter.ReconcileMatches(new Dictionary<string,string>(), DateTime.UtcNow.AddMinutes(1));
        Check(adapter.State(first).SelfId == one.SelfId, "Leaving a game lobby preserves the same participant while another player remains");
        adapter.Abandon(first);
        Check(adapter.CurrentState(first) is null && rooms.GetRoom(one.Code)!.Clients.Single().IsHost,
            "Abandon releases the Steam membership and transfers room hosting to a remaining player");
        var newRoom = adapter.Execute(first, new("create", Name: "Fresh host")).State!;
        Check(newRoom.Code != one.Code, "An abandoning player can immediately create a different draft");
        adapter.Abandon(second);
        Check(rooms.GetRoom(one.Code) is null, "The last abandonment deletes the old integrated room");
        adapter.Disconnect(first);
        var now = DateTime.UtcNow;
        adapter.ReconcileMatches(new Dictionary<string,string>(), now);
        adapter.ReconcileMatches(new Dictionary<string,string>(), now.AddSeconds(16));
        Check(rooms.GetRoom(newRoom.Code) is null && adapter.CurrentState(first) is null,
            "An empty disconnected game draft expires without trapping its Steam membership");
        var playing = adapter.Execute(first, new("create", Name: "Transfer test")).State!;
        var room = rooms.GetRoom(playing.Code)!;
        room.RuntimeResultId = "lifecycle-result";
        adapter.Disconnect(first);
        adapter.ReconcileMatches(new Dictionary<string,string> { [room.RuntimeResultId] = "Playing" }, now.AddMinutes(5));
        Check(rooms.GetRoom(playing.Code) is not null, "Transferring off the drafting server does not delete an active match");
        adapter.State(first);
        adapter.ReconcileMatches(new Dictionary<string,string> { [room.RuntimeResultId] = "Completed" }, now.AddMinutes(6));
        Check(adapter.CurrentState(first) is null && rooms.GetRoom(playing.Code) is null,
            "Completed worker releases even a participant who has already returned to the drafting server");
        var afterMatch = adapter.Execute(first, new("create", Name: "Next game")).State!;
        Expect(() => adapter.Abandon(first, "lifecycle-result"), "A delayed old-match abandon cannot remove a newly created room");
        Check(adapter.State(first).Code == afterMatch.Code, "A stale worker request leaves the new room intact");
        adapter.Abandon(first);
        var browser = rooms.CreateRoom("Browser stays unchanged", "Browser", DeadlockTeam.HiddenKing, new());
        rooms.MarkPlayerDisconnected(browser.RoomCode, browser.PlayerId);
        adapter.ReconcileMatches(new Dictionary<string,string>(), now.AddHours(1));
        Check(rooms.GetRoom(browser.RoomCode) is not null, "Game lifecycle cleanup does not change normal website room retention");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Expect(Action action, string message) { try { action(); } catch (InvalidOperationException) { Check(true,message); return; } throw new Exception("FAIL: " + message); }
}
