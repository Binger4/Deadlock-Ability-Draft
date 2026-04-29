using abilitydraft.Components;
using abilitydraft.Models;
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
builder.Services.AddHttpClient();
builder.Services.Configure<DeadlockDataOptions>(builder.Configuration.GetSection("DeadlockData"));
builder.Services.Configure<DeadPackerOptions>(builder.Configuration.GetSection("DeadPacker"));
builder.Services.Configure<AdminAuthOptions>(builder.Configuration.GetSection("AdminAuth"));
builder.Services.Configure<CacheCleanupOptions>(builder.Configuration.GetSection("CacheCleanup"));
builder.Services.Configure<GeneratedFilesOptions>(builder.Configuration.GetSection("GeneratedFiles"));
builder.Services.Configure<DraftTimingOptions>(builder.Configuration.GetSection("DraftTiming"));
builder.Services.AddSingleton<abilitydraft.Services.LocalisationDiscoveryService>();
builder.Services.AddSingleton<abilitydraft.Services.LocalisationParser>();
builder.Services.AddSingleton<abilitydraft.Services.DeadlockFileParser>();
builder.Services.AddSingleton<abilitydraft.Services.DraftPoolGenerator>();
builder.Services.AddSingleton<abilitydraft.Services.DraftTurnService>();
builder.Services.AddSingleton<abilitydraft.Services.AbilityAssignmentService>();
builder.Services.AddSingleton<abilitydraft.Services.ModFileGenerator>();
builder.Services.AddSingleton<abilitydraft.Services.ZipExportService>();
builder.Services.AddSingleton<abilitydraft.Services.DeadPackerService>();
builder.Services.AddSingleton<abilitydraft.Services.DraftRoomService>();
builder.Services.AddSingleton<abilitydraft.Services.ServerDeadlockDataService>();
builder.Services.AddHostedService<abilitydraft.Services.DeadlockVDataUpdateService>();
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

app.MapStaticAssets();
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

static string SafeReturnUrl(string? returnUrl)
{
    if (!string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl.StartsWith("/", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal))
    {
        return returnUrl;
    }

    return "/admin";
}

sealed record RoomPresencePayload(string RoomCode, string PlayerId);
