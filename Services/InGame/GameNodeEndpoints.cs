using System.Net.WebSockets;
using AbilityDraft.Contracts;

namespace abilitydraft.Services.InGame;

public static class GameNodeEndpoints
{
    public static void MapGameNodes(this WebApplication app)
    {
        app.MapGet("/game-nodes/ping", (HttpContext http) =>
        { http.Response.Headers.CacheControl = "no-store"; return Results.NoContent(); });
        app.MapGet("/api/game-nodes/v1/connect", async (HttpContext http, GameNodeCoordinator coordinator, ILogger<GameNodeCoordinator> logger) =>
        {
            if ((!http.Request.IsHttps && !(app.Environment.IsDevelopment() && http.Connection.RemoteIpAddress is { } ip && System.Net.IPAddress.IsLoopback(ip))) || !http.WebSockets.IsWebSocketRequest)
            { http.Response.StatusCode = 400; return; }
            var id = http.Request.Headers["X-Game-Node-Id"].ToString();
            if (!coordinator.Authenticate(id, http.Request.Headers.Authorization.ToString()))
            { http.Response.StatusCode = 401; return; }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            string? session = null;
            try
            {
                while (!http.RequestAborted.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    var heartbeat = await GameNodeProtocol.Receive<GameNodeHeartbeat>(socket, timeout.Token);
                    if (heartbeat.Registration is null) throw new InvalidOperationException("Missing Game Node registration.");
                    session ??= coordinator.Connect(id, heartbeat.Registration);
                    var reply = coordinator.Heartbeat(id, session, heartbeat, DateTime.UtcNow);
                    await GameNodeProtocol.Send(socket, reply, timeout.Token);
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Game Node {Node} rejected: {Reason}", id, ex.Message);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await GameNodeProtocol.Send(socket, new GameNodeCommands(GameNodeProtocol.Version, ex.Message, [], [], []), timeout.Token); }
                catch (Exception) { }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
            { logger.LogInformation("Game Node {Node} disconnected ({Type})", id, ex.GetType().Name); }
            finally { if (session is not null) coordinator.Disconnect(id, session); socket.Abort(); }
        }).RequireRateLimiting("ingame");
    }
}
