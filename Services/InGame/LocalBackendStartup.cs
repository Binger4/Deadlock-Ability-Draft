using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Configuration;

namespace abilitydraft.Services.InGame;

// Local development can be launched from either the shortcut or dotnet run.
// Keep the lease for the process lifetime, before hosted services start.
public sealed class LocalBackendStartup : IDisposable
{
    private FileStream? lease;
    public bool ShouldStart => Message is null;
    public string? Message { get; private init; }
    public int ExitCode { get; private init; }

    public static async Task<LocalBackendStartup> AcquireAsync(string contentRoot, IConfiguration configuration, bool isDevelopment)
    {
        var options = configuration.GetSection("InGame:DraftServers").Get<DraftServerOptions>();
        if (!isDevelopment || !configuration.GetValue<bool>("InGame:Enabled") || options?.Enabled != true ||
            !Uri.TryCreate(options.BackendUrl, UriKind.Absolute, out var backend) || !backend.IsLoopback ||
            backend.Scheme != Uri.UriSchemeHttp ||
            !(configuration["urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Any(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback &&
                endpoint.Scheme == backend.Scheme && endpoint.Port == backend.Port))
            return new();

        var website = options.WebsiteUrl;
        var directory = Path.Combine(contentRoot, "App_Data");
        Directory.CreateDirectory(directory);
        FileStream lease;
        try
        {
            lease = File.Open(Path.Combine(directory, $"local-backend-{backend.Port}.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
        {
            return new() { Message = $"Ability Draft is already starting or running. Use {website}" };
        }

        var result = new LocalBackendStartup { lease = lease };
        var starting = false;
        try
        {
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == backend.Port))
            {
                starting = true;
                return result;
            }

            // Verify the existing service with the private key rather than
            // treating an unrelated website on the same port as this backend.
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", configuration["InGame:ServerKey"]);
            try
            {
                var status = await http.GetFromJsonAsync<DraftServerStatus[]>(new Uri(backend, "api/ingame/v1/hosting"));
                if (status is not null && status.Any(s => s.Role == "custom" && s.Port == options.CustomPort) &&
                    status.Any(s => s.Role == "public" && s.Port == options.PublicPort))
                    return new() { Message = $"Ability Draft is already running at {website} Reusing the existing backend and drafting servers." };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }

            return new()
            {
                ExitCode = 1,
                Message = $"Local API port {backend.Port} is already in use by another service or a differently configured backend. Stop that service or change the local API address before starting Ability Draft."
            };
        }
        finally
        {
            // A successful first launch owns the lease until Program exits.
            // Every early return above disposes it instead.
            if (!starting) result.Dispose();
        }
    }

    public void Dispose()
    {
        lease?.Dispose();
        lease = null;
    }
}
