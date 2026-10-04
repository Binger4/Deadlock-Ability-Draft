using System.Net;
using System.Net.Http.Json;
using abilitydraft.Services.InGame;
using abilitydraft.Services;

static class WebsiteActivityChecks
{
    public static async Task Run(HttpClient http, InGameRoomAdapter adapter, DraftRoomService rooms)
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
        const string steam = "76561198000000601";
        var view = adapter.Execute(steam, new("create", Name: "Activity test")).State!;
        var playerId = rooms.GetRoom(view.Code)!.Clients.Single().PlayerId;
        adapter.State(steam);
        Check(adapter.LastWebsiteActivity(steam) is null, "Server presence polls do not reset website AFK time");
        using var denied = await http.PostAsJsonAsync("/ingame-activity", new WebsiteActivity(view.Code,"not-the-participant",null));
        Check(denied.StatusCode == HttpStatusCode.BadRequest && adapter.LastWebsiteActivity(steam) is null, "Activity requires the participant capability");
        using var allowed = await http.PostAsJsonAsync("/ingame-activity", new WebsiteActivity(view.Code,playerId,null));
        var when = adapter.LastWebsiteActivity(steam);
        Check(allowed.StatusCode == HttpStatusCode.NoContent && when is not null, "Draft website interaction updates only the linked player");
        adapter.State(steam); adapter.NativeChat(steam);
        Check(adapter.LastWebsiteActivity(steam) == when, "Chat/state polling still does not manufacture activity");
        adapter.Abandon(steam);
        Check(adapter.LastWebsiteActivity(steam) is null, "Abandon clears old activity metadata");
        using var stale = await http.PostAsJsonAsync("/ingame-activity", new WebsiteActivity(view.Code,playerId,null));
        Check(stale.StatusCode == HttpStatusCode.BadRequest, "An old page cannot keep a new participant alive");
        var entry = adapter.CreateWebsiteEntry(steam, "create", "Activity test");
        var token = entry.Path.Split("gameEntry=")[1];
        using var entryAllowed = await http.PostAsJsonAsync("/ingame-activity", new WebsiteActivity(null,null,token));
        Check(entryAllowed.StatusCode == HttpStatusCode.NoContent && adapter.LastWebsiteActivity(steam) is not null, "Create/join forms count real activity before a room exists");
    }
}
