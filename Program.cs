using abilitydraft.Components;
using abilitydraft.Models;
using abilitydraft.Services.InGame;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.AccessDeniedPath = "/admin/login";
        options.Cookie.HttpOnly = true;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddInGameIntegration(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<abilitydraft.Services.SiteAccessService>();
builder.Services.Configure<DeadlockDataOptions>(builder.Configuration.GetSection("DeadlockData"));
builder.Services.Configure<DeadPackerOptions>(builder.Configuration.GetSection("DeadPacker"));
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
builder.Services.Configure<CacheCleanupOptions>(builder.Configuration.GetSection("CacheCleanup"));
builder.Services.Configure<GeneratedFilesOptions>(builder.Configuration.GetSection("GeneratedFiles"));
builder.Services.Configure<DraftTimingOptions>(builder.Configuration.GetSection("DraftTiming"));
builder.Services.Configure<DraftStatsOptions>(builder.Configuration.GetSection("DraftStats"));
builder.Services.AddSingleton<abilitydraft.Services.LocalisationDiscoveryService>();
builder.Services.AddSingleton<abilitydraft.Services.LocalisationParser>();
builder.Services.AddSingleton<abilitydraft.Services.DeadlockFileParser>();
builder.Services.AddSingleton<abilitydraft.Services.DraftPoolGenerator>();
builder.Services.AddSingleton<abilitydraft.Services.DraftTurnService>();
builder.Services.AddSingleton<abilitydraft.Services.AbilityAssignmentService>();
builder.Services.AddSingleton<abilitydraft.Services.ModFileGenerator>();
builder.Services.AddSingleton<abilitydraft.Services.ZipExportService>();
builder.Services.AddSingleton<abilitydraft.Services.DeadPackerService>();
builder.Services.AddSingleton<abilitydraft.Services.DraftStatsService>();
builder.Services.AddSingleton<abilitydraft.Services.DraftRoomService>();
builder.Services.AddSingleton<abilitydraft.Services.ServerDeadlockDataService>();
builder.Services.AddSingleton<abilitydraft.Services.DeadlockVDataUpdateService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<abilitydraft.Services.DeadlockVDataUpdateService>());
builder.Services.AddHostedService<abilitydraft.Services.DraftCacheCleanupService>();

var app = builder.Build();
app.Services.GetRequiredService<abilitydraft.Services.ServerDeadlockDataService>().Reload();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();

var siteAccess = app.Services.GetRequiredService<abilitydraft.Services.SiteAccessService>();
app.Use(async (httpContext, next) =>
{
    if (siteAccess.IsClosed &&
        !siteAccess.CanBypass(httpContext.User) &&
        !IsSiteAccessAllowedPath(httpContext.Request.Path))
    {
        var returnUrl = httpContext.Request.PathBase +
                        httpContext.Request.Path +
                        httpContext.Request.QueryString;
        var location = $"/site-closed?returnUrl={Uri.EscapeDataString(returnUrl)}";
        httpContext.Response.Redirect(location);
        return;
    }

    await next();
});

app.MapInGameIntegration();

app.MapGet("/download/ability-draft-mod", (IWebHostEnvironment environment, IOptions<InGameOptions> inGame, abilitydraft.Services.ProjectLinksService projectLinks) =>
{
    if (!inGame.Value.Enabled)
    {
        return Results.NotFound();
    }

    var candidates = new[]
    {
        Path.Combine(environment.ContentRootPath, "Integration", "dist", "ability_draft_base.vpk"),
        Path.Combine(environment.ContentRootPath, "dist", "ability_draft_base.vpk")
    };
    var source = candidates.FirstOrDefault(File.Exists);
    return source is null
        ? Results.NotFound()
        : Results.File(source, "application/octet-stream", projectLinks.ModDownloadFileName, enableRangeProcessing: true);
});

app.MapStaticAssets();
app.MapGet("/site-access/status", (HttpContext httpContext, abilitydraft.Services.SiteAccessService siteAccess) =>
{
    httpContext.Response.Headers.CacheControl = "no-store, no-cache";
    var isClosed = siteAccess.IsClosed;
    return Results.Ok(new
    {
        isClosed,
        requiresPassword = isClosed && !siteAccess.CanBypass(httpContext.User)
    });
});
app.MapPost("/site-access/submit", async (HttpContext httpContext, abilitydraft.Services.SiteAccessService siteAccess) =>
{
    var form = await httpContext.Request.ReadFormAsync();
    var returnUrl = SafeReturnUrl(form["returnUrl"].ToString(), "/");
    var password = form["password"].ToString();

    if (!siteAccess.IsClosed)
    {
        return Results.Redirect(returnUrl);
    }

    if (!siteAccess.VerifyDeveloperPassword(password, out var accessVersion))
    {
        return Results.Redirect($"/site-closed?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
    }

    var claims = httpContext.User.Claims
        .Where(claim => claim.Type is not abilitydraft.Services.SiteAccessService.AccessClaimType and
                       not abilitydraft.Services.SiteAccessService.AccessVersionClaimType)
        .ToList();
    if (claims.All(claim => claim.Type != ClaimTypes.Name))
    {
        claims.Add(new Claim(ClaimTypes.Name, "Developer"));
    }

    claims.Add(new Claim(abilitydraft.Services.SiteAccessService.AccessClaimType, "true"));
    claims.Add(new Claim(abilitydraft.Services.SiteAccessService.AccessVersionClaimType, accessVersion));
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties
        {
            IsPersistent = false,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        });

    return Results.Redirect(returnUrl);
}).DisableAntiforgery();
app.MapPost("/admin/login-submit", async (HttpContext httpContext, IOptions<AdminAuthOptions> adminOptions) =>
{
    var form = await httpContext.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());
    var configured = adminOptions.Value;

    if (string.Equals(username, configured.Username, StringComparison.Ordinal) &&
        string.Equals(password, configured.Password, StringComparison.Ordinal))
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, "Admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
        return Results.Redirect(returnUrl);
    }

    return Results.Redirect($"/admin/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
}).DisableAntiforgery();
app.MapGet("/admin/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
});
app.MapPost("/room-presence/disconnect", async (HttpContext httpContext, abilitydraft.Services.DraftRoomService rooms) =>
{
    var payload = await JsonSerializer.DeserializeAsync<RoomPresencePayload>(
        httpContext.Request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (!string.IsNullOrWhiteSpace(payload?.RoomCode) && !string.IsNullOrWhiteSpace(payload.PlayerId))
    {
        rooms.MarkPlayerDisconnected(payload.RoomCode, payload.PlayerId);
    }

    return Results.NoContent();
}).DisableAntiforgery();
app.MapHub<abilitydraft.Services.DraftRoomHub>("/draft-room-hub");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string SafeReturnUrl(string? returnUrl, string fallback = "/admin")
{
    if (!string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl.StartsWith("/", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        return returnUrl;
    }

    return fallback;
}

static bool IsSiteAccessAllowedPath(PathString path)
{
    // The private server-key endpoint must still report process health while the
    // public site is closed. Its integration authentication remains mandatory.
    if (path.Equals("/api/ingame/v1/hosting")) return true;
    if (path.StartsWithSegments("/site-closed") ||
        path.StartsWithSegments("/site-access") ||
        path.Equals("/admin/login") ||
        path.Equals("/admin/login-submit") ||
        path.Equals("/admin/logout") ||
        path.StartsWithSegments("/_framework") ||
        path.StartsWithSegments("/lib"))
    {
        return true;
    }

    var value = path.Value;
    return value is not null &&
           (value.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".woff", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase));
}

sealed record RoomPresencePayload(string RoomCode, string PlayerId);
