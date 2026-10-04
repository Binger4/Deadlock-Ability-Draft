namespace abilitydraft.Services.InGame;

public sealed record WebsiteActivity(string? RoomCode, string? PlayerId, string? GameEntry);

public sealed partial class InGameRoomAdapter
{
    private readonly Dictionary<string, DateTime> websiteActivity = new();
    public void RecordWebsiteActivity(WebsiteActivity activity)
    {
        lock (gate)
        {
            string steam;
            if (activity.GameEntry is not null) steam = BrowserAdmission(activity.GameEntry).Steam;
            else
            {
                var room = rooms.GetRoom(activity.RoomCode ?? "") ?? throw new InvalidOperationException("Room not found.");
                lock (room)
                {
                    var client = room.Clients.SingleOrDefault(c => c.PlayerId == activity.PlayerId);
                    if (!options.Value.Enabled || client?.SteamId64 is not { } identity || memberships.GetValueOrDefault(identity) != room.Code)
                        throw new InvalidOperationException("In-game participant not found.");
                    steam = identity;
                }
            }
            websiteActivity[steam] = DateTime.UtcNow;
        }
    }
    public DateTime? LastWebsiteActivity(string steam)
    {
        lock (gate) return websiteActivity.TryGetValue(steam, out var when) ? when : null;
    }
}
