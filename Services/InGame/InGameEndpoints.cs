using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using AbilityDraft.Contracts;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services.InGame;

public static class InGameEndpoints
{
    private sealed class ServerCatalogOperation;
    public static IServiceCollection AddInGameIntegration(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<InGameOptions>(config.GetSection("InGame"));
        services.AddSingleton<abilitydraft.Services.ProjectLinksService>();
        services.AddSingleton<InGameRoomAdapter>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<PresetTransferService>();
        services.AddHostedService<InGamePresenceService>();
        services.Configure<MatchWorkerOptions>(config.GetSection("InGame:MatchWorkers"));
        services.AddSingleton<IMatchWorkerHost, WindowsMatchWorkerHost>();
        services.AddSingleton<MatchWorkerCoordinator>();
        services.AddHostedService(provider => provider.GetRequiredService<MatchWorkerCoordinator>());
        services.Configure<DraftServerOptions>(config.GetSection("InGame:DraftServers"));
        services.AddSingleton<IDraftServerHost, WindowsDraftServerHost>();
        services.AddSingleton<DraftServerSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<DraftServerSupervisor>());
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("ingame", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromSeconds(10), QueueLimit = 0 }));
        });
        return services;
    }

    public static void MapInGameIntegration(this WebApplication app)
    {
        app.MapPost("/ingame-activity", (WebsiteActivity activity, InGameRoomAdapter adapter) =>
        {
            try { adapter.RecordWebsiteActivity(activity); return Results.NoContent(); }
            catch (InvalidOperationException) { return Results.BadRequest(); }
        }).RequireRateLimiting("ingame");
        app.MapGet("/project-links/{index:int}", (int index, HttpContext http, ProjectLinksService links) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            var footer = links.FooterLinks();
            return index >= 0 && index < footer.Count
                ? Results.Redirect(footer[index].Url) : Results.NotFound();
        }).RequireRateLimiting("ingame");
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.StartsWithSegments("/presets"))
            {
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            await next(http);
        });
        app.MapGet("/presets/{token}/download", (string token, HttpContext http, PresetTransferService transfers) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            try { return Results.File(Encoding.UTF8.GetBytes(transfers.Download(token)), "application/json", "custom-draft-preset.json"); }
            catch (InvalidOperationException) { return Results.NotFound(); }
        }).RequireRateLimiting("ingame");
        var group = app.MapGroup("/api/ingame/v1").RequireRateLimiting("ingame");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var options = http.RequestServices.GetRequiredService<IOptions<InGameOptions>>().Value;
            if (!options.Enabled) return Results.NotFound();
            var supplied = http.Request.Headers.Authorization.ToString();
            if (options.ServerKey.Length < 32 || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
                    SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + options.ServerKey))))
                return Results.Unauthorized();
            // Only a trusted plugin may assert this header, obtained from UIEvent.Caller, not event args.
            var steam = http.Request.Headers["X-Steam-Id"].ToString();
            if (http.GetEndpoint()?.Metadata.GetMetadata<ServerCatalogOperation>() is null &&
                (steam.Length != 17 || !ulong.TryParse(steam, out var value) || value == 0))
                return Results.BadRequest(new CommandReply(null, "A verified Steam ID is required."));
            http.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new CommandReply(null, ex.Message)); }
            catch (Exception ex)
            {
                http.RequestServices.GetRequiredService<ILogger<InGameRoomAdapter>>().LogError(ex, "In-game request failed");
                return Results.Problem("In-game request failed. Check backend logs.");
            }
        });
        group.MapGet("/hosting", (DraftServerSupervisor servers) => servers.Status()).WithMetadata(new ServerCatalogOperation());
        group.MapGet("/catalog", (ServerDeadlockDataService data) =>
        {
            var parsed = data.Current.Data ?? throw new InvalidOperationException("Draft data is not available.");
            return ResourceCatalog(parsed);
        }).WithMetadata(new ServerCatalogOperation());
        group.MapPost("/command", (RoomCommand command, HttpContext http, InGameRoomAdapter adapter) =>
            adapter.Execute(http.Request.Headers["X-Steam-Id"].ToString(), command));
        group.MapGet("/state", (HttpContext http, InGameRoomAdapter adapter) =>
            new CommandReply(adapter.CurrentState(http.Request.Headers["X-Steam-Id"].ToString()),
                ExternalLink: adapter.WebsiteLink(http.Request.Headers["X-Steam-Id"].ToString()),
                NativeChat: adapter.NativeChat(http.Request.Headers["X-Steam-Id"].ToString()),
                LastActivityUtc: adapter.LastWebsiteActivity(http.Request.Headers["X-Steam-Id"].ToString())));
        group.MapGet("/result", (HttpContext http, InGameRoomAdapter adapter) =>
            adapter.Result(http.Request.Headers["X-Steam-Id"].ToString()));
        group.MapPost("/match", (HttpContext http, InGameRoomAdapter adapter, MatchWorkerCoordinator workers, ServerDeadlockDataService data) =>
        {
            var result = adapter.MatchResult(http.Request.Headers["X-Steam-Id"].ToString(), requireHost: true);
            var parsed = data.Current.Data ?? throw new InvalidOperationException("Draft data is not available.");
            var catalog = ResourceCatalog(parsed);
            return workers.Request(result, catalog);
        });
        group.MapGet("/match", (HttpContext http, InGameRoomAdapter adapter, MatchWorkerCoordinator workers) =>
        {
            var steam = http.Request.Headers["X-Steam-Id"].ToString();
            var result = adapter.MatchResult(steam, requireHost: false);
            if (adapter.MatchSpectator(steam) is { } spectator) workers.AdmitSpectator(result.ResultId, spectator);
            return workers.Get(result.ResultId) ?? new MatchView("", result.ResultId, "Idle", null,
                result.Source == "public" ? "Ready to start." : "Waiting for the host to start the match.");
        });
        group.MapGet("/website", (HttpContext http, InGameRoomAdapter adapter) =>
            adapter.Website(http.Request.Headers["X-Steam-Id"].ToString()));
        group.MapPost("/website-entry", (RoomCommand command, HttpContext http, InGameRoomAdapter adapter) =>
            adapter.CreateWebsiteEntry(http.Request.Headers["X-Steam-Id"].ToString(), command.Operation, command.Name));
        group.MapGet("/queue", (HttpContext http, InGameRoomAdapter adapter) =>
            adapter.Queue(http.Request.Headers["X-Steam-Id"].ToString(), "queueStatus"));
        group.MapPost("/queue", (RoomCommand command, HttpContext http, InGameRoomAdapter adapter) =>
            adapter.Queue(http.Request.Headers["X-Steam-Id"].ToString(), command.Operation, command.Name));
        group.MapPost("/export", async (HttpContext http, InGameRoomAdapter adapter) =>
        {
            var steam = http.Request.Headers["X-Steam-Id"].ToString();
            var archive = await Task.Run(() => adapter.GenerateArchive(steam));
            http.Response.Headers["X-AbilityDraft-Format"] = archive.Format;
            return Results.File(archive.Bytes, archive.Format == "vpk" ? "application/octet-stream" : "application/zip", archive.FileName);
        });
        group.MapPost("/disconnect", (HttpContext http, InGameRoomAdapter adapter) =>
        {
            adapter.Disconnect(http.Request.Headers["X-Steam-Id"].ToString());
            return Results.NoContent();
        });
        group.MapPost("/abandon", (RoomCommand command, HttpContext http, InGameRoomAdapter adapter, MatchWorkerCoordinator workers) =>
        {
            var steam = http.Request.Headers["X-Steam-Id"].ToString();
            var result = adapter.Abandon(steam, command.Key);
            if (result is not null) workers.Abandon(result, steam);
            return new CommandReply(null);
        });
    }
    private static DraftResourceCatalog ResourceCatalog(abilitydraft.Models.ParsedDeadlockData parsed) => new(1,
        parsed.Heroes.Select(h => new ResourceHero(h.Key, h.Id,
            h.DraftableAbilityKeys().Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToArray())).ToArray(),
        parsed.Heroes.Select(h => (h.Key, h.DisplayName)).Concat(parsed.Abilities.Select(a => (a.Key, a.DisplayName)))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().DisplayName));
}
