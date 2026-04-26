using Microsoft.AspNetCore.SignalR;

namespace abilitydraft.Services;

public sealed class DraftRoomHub : Hub
{
    public Task JoinRoomGroup(string roomCode) =>
        Groups.AddToGroupAsync(Context.ConnectionId, roomCode.Trim().ToUpperInvariant());

    public Task LeaveRoomGroup(string roomCode) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, roomCode.Trim().ToUpperInvariant());
}
