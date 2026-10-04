using AbilityDraft.Contracts;
using abilitydraft.Models;

namespace abilitydraft.Services.InGame;

public sealed partial class InGameRoomAdapter
{
    private sealed record NativeChatSession(NativeChatRequest Request, DateTime Expires, int LastSequence = 0, string? LastText = null);
    private readonly Dictionary<string, NativeChatSession> nativeChats = new();

    // Called by the participant's Blazor circuit. Only the game server for that
    // verified Steam identity receives the request through its existing poll.
    public void OpenNativeChat(string code, string playerId, DraftChatScope scope) => SetNativeChat(code, playerId, scope, open: true);

    public void UpdateNativeChatScope(string code, string playerId, DraftChatScope scope) => SetNativeChat(code, playerId, scope, open: false);

    private void SetNativeChat(string code, string playerId, DraftChatScope scope, bool open)
    {
        lock (gate)
        {
            var room = rooms.GetRoom(code) ?? throw new InvalidOperationException("Room not found.");
            lock (room)
            {
                var client = room.Clients.SingleOrDefault(c => c.PlayerId == playerId);
                if (!options.Value.Enabled || client?.SteamId64 is not { } steam ||
                    memberships.GetValueOrDefault(steam) != room.Code || room.Config.DisableChat)
                    throw new InvalidOperationException("In-game chat is unavailable.");
                var target = client.Team == DeadlockTeam.Spectator ? DraftChatScope.Spectators :
                    scope == DraftChatScope.All ? DraftChatScope.All : DraftChatScope.Allies;
                foreach (var expired in nativeChats.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToArray())
                    nativeChats.Remove(expired);
                if (open)
                    nativeChats[steam] = new(new(Guid.NewGuid().ToString("N"), room.Code, target.ToString()), DateTime.UtcNow.AddMinutes(5));
                else if (nativeChats.TryGetValue(steam, out var pending) && pending.Request.RoomCode == room.Code)
                    nativeChats[steam] = pending with { Request = pending.Request with { Scope = target.ToString(), Revision = pending.Request.Revision + 1 }, Expires = DateTime.UtcNow.AddMinutes(5) };
            }
        }
    }

    public NativeChatRequest? NativeChat(string steam)
    {
        lock (gate)
        {
            if (!nativeChats.TryGetValue(steam, out var pending) || pending.Expires < DateTime.UtcNow ||
                memberships.GetValueOrDefault(steam) != pending.Request.RoomCode) return null;
            var room = rooms.GetRoom(pending.Request.RoomCode);
            if (room is null) return null;
            lock (room)
            {
                if (room.Config.DisableChat) return null;
                nativeChats[steam] = pending with { Expires = DateTime.UtcNow.AddMinutes(5) };
                return pending.Request;
            }
        }
    }
}
