using abilitydraft.Services;
using abilitydraft.Services.InGame;
using abilitydraft.Models;

static class NativeChatChecks
{
    public static void Run(InGameRoomAdapter adapter, DraftRoomService rooms)
    {
        const string host = "76561198000000501", stranger = "76561198000000502";
        var view = adapter.Execute(host, new("create", Name: "Chat test")).State!;
        var room = rooms.GetRoom(view.Code)!;
        var player = room.Clients.Single().PlayerId;
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.All);
        Check(adapter.NativeChat(host) is null, "Changing audience alone does not open a native composer");
        adapter.OpenNativeChat(view.Code, player, DraftChatScope.Allies);
        var request = adapter.NativeChat(host)!;
        Check(request.Scope == "Allies" && adapter.NativeChat(stranger) is null, "Native composer is private to the linked participant");
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.All);
        var updated = adapter.NativeChat(host)!;
        Check(updated.Id == request.Id && updated.Scope == "All" && updated.Revision == 1, "Audience switches within the same native entry");
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.Allies);
        Check(adapter.NativeChat(host) is { Scope: "Allies", Revision: 2 }, "Rapid audience toggles still notify the native entry");
        Expect(() => adapter.UpdateNativeChatScope(view.Code, "not-a-member", DraftChatScope.All), "Another participant cannot change the composer audience");
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.All);
        const string text = "Привет — français 中文 한국어 العربية 😀";
        adapter.Execute(host, new("chat", Key: request.Id, Text: text, Scope: "Allies"));
        var message = room.ChatMessages.Last();
        Check(message.Text == text && message.Scope == DraftChatScope.All, "Native text survives Unicode transport and sends to the updated audience");
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.All);
        Check(adapter.NativeChat(host) is null, "Audience changes do not reopen a submitted entry");
        Expect(() => adapter.Execute(host, new("chat", Key: request.Id, Text: text)), "Duplicate native submit is rejected");
        Expect(() => adapter.OpenNativeChat(view.Code, "not-a-member", DraftChatScope.All), "Composer requires the participant capability");
        adapter.OpenNativeChat(view.Code, player, DraftChatScope.Allies);
        var continuous = adapter.NativeChat(host)!;
        adapter.Execute(host, new("chat", Key: continuous.Id, Text: "one", ChatSequence: 1));
        var count = room.ChatMessages.Count;
        adapter.Execute(host, new("chat", Key: continuous.Id, Text: "one", ChatSequence: 1));
        Check(room.ChatMessages.Count == count, "Retry after a lost acknowledgement cannot duplicate a message");
        adapter.UpdateNativeChatScope(view.Code, player, DraftChatScope.All);
        adapter.Execute(host, new("chat", Key: continuous.Id, Text: "two", ChatSequence: 2));
        Check(adapter.NativeChat(host)?.Id == continuous.Id && room.ChatMessages.Count == count + 1 && room.ChatMessages.Last().Scope == DraftChatScope.All,
            "Open composer sends consecutive messages and keeps audience switching");
        Expect(() => adapter.Execute(host, new("chat", Key: continuous.Id, Text: "changed", ChatSequence: 2)), "An accepted message number cannot be reused with different text");
        Expect(() => adapter.Execute(host, new("chat", Key: continuous.Id, Text: "skip", ChatSequence: 4)), "Out-of-order native messages are rejected");
        adapter.OpenNativeChat(view.Code, player, DraftChatScope.All);
        var stale = adapter.NativeChat(host)!;
        adapter.Abandon(host);
        adapter.Execute(host, new("create", Name: "Next room"));
        Check(adapter.NativeChat(host) is null, "Old composer cannot cross into a new room");
        Expect(() => adapter.Execute(host, new("chat", Key: stale.Id, Text: text)), "Old composer cannot send into another room");
        adapter.Abandon(host);
        var disabled = adapter.Execute(host, new("create", Name: "Disabled chat")).State!;
        var disabledRoom = rooms.GetRoom(disabled.Code)!;
        disabledRoom.Config.DisableChat = true;
        Expect(() => adapter.OpenNativeChat(disabled.Code, disabledRoom.Clients.Single().PlayerId, DraftChatScope.All), "Disabled chat cannot open a native composer");
        adapter.Abandon(host);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Expect(Action action, string message) { try { action(); } catch (InvalidOperationException) { Check(true,message); return; } throw new Exception("FAIL: " + message); }
}
