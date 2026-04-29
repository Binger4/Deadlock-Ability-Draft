using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services;

public sealed class DeadlockVDataUpdateService(
    IHttpClientFactory httpClientFactory,
    IOptions<DeadlockDataOptions> options,
    IWebHostEnvironment environment,
    ServerDeadlockDataService dataService,
    ILogger<DeadlockVDataUpdateService> logger) : BackgroundService
{
    private const string AbilitiesUrl = "https://raw.githubusercontent.com/SteamTracking/GameTracking-Deadlock/master/game/citadel/pak01_dir/scripts/abilities.vdata";
    private const string HeroesUrl = "https://raw.githubusercontent.com/SteamTracking/GameTracking-Deadlock/master/game/citadel/pak01_dir/scripts/heroes.vdata";
    private const string CommitUrl = "https://api.github.com/repos/SteamTracking/GameTracking-Deadlock/commits/master";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckAndUpdateAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.UpdateIntervalMinutes));
            try
            {
                await Task.Delay(interval, stoppingToken);
                await CheckAndUpdateAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckAndUpdateAsync(CancellationToken cancellationToken)
    {
        var dataPath = Resolve(options.Value.GameDataPath);
        Directory.CreateDirectory(dataPath);

        var abilitiesPath = Path.Combine(dataPath, "abilities.vdata");
        var heroesPath = Path.Combine(dataPath, "heroes.vdata");
        var statePath = Path.Combine(dataPath, "state.json");
        var state = LoadState(statePath);
        var checkedUtc = DateTime.UtcNow;

        try
        {
            var latestCommit = await FetchLatestCommitAsync(cancellationToken);
            var missingFiles = !File.Exists(abilitiesPath) || !File.Exists(heroesPath);
            var needsUpdate = missingFiles || !string.Equals(state.CommitHash, latestCommit, StringComparison.Ordinal);

            if (!needsUpdate)
            {
                state.LastCheckedUtc = checkedUtc;
                state.LastMessage = $"vdata files are up to date, last check at {checkedUtc:O}";
                state.LastError = null;
                SaveState(statePath, state);
                logger.LogInformation("{Message}", state.LastMessage);
                return;
            }

            var abilitiesBytes = await DownloadBytesAsync(AbilitiesUrl, cancellationToken);
            var heroesBytes = await DownloadBytesAsync(HeroesUrl, cancellationToken);

            ReplaceFileSafely(abilitiesPath, abilitiesBytes);
            ReplaceFileSafely(heroesPath, heroesBytes);

            var updatedUtc = DateTime.UtcNow;
            state.CommitHash = latestCommit;
            state.LastCheckedUtc = checkedUtc;
            state.LastUpdatedUtc = updatedUtc;
            var reloadMessage = ReloadServerData(state);
            state.LastMessage = missingFiles
                ? $"vdata files downloaded for the first time at {updatedUtc:O}; {reloadMessage}"
                : $"vdata files successfully updated at {updatedUtc:O}; {reloadMessage}";
            SaveState(statePath, state);

            logger.LogInformation("{Message}", state.LastMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.LastCheckedUtc = checkedUtc;
            state.LastError = $"{DateTime.UtcNow:O}: {ex.Message}";
            SaveState(statePath, state);
            logger.LogError(ex, "Deadlock vdata update check failed at {Timestamp}. Existing local files were kept.", DateTime.UtcNow);
        }
    }

    private string ReloadServerData(DeadlockVDataState state)
    {
        try
        {
            var snapshot = dataService.Reload();
            var reloadedUtc = DateTime.UtcNow;
            state.LastReloadedUtc = reloadedUtc;
            state.LastError = snapshot.Errors.Count == 0 ? null : string.Join(" | ", snapshot.Errors);

            if (snapshot.Data is null)
            {
                logger.LogWarning("Deadlock vdata files were updated, but server data reload did not produce parsed data: {Errors}", state.LastError);
                return $"server data reload finished with errors at {reloadedUtc:O}";
            }

            if (snapshot.Errors.Count > 0)
            {
                logger.LogWarning("Deadlock server data reloaded after vdata update with warnings/errors: {Errors}", state.LastError);
            }

            logger.LogInformation(
                "Deadlock server data reloaded after vdata update at {Timestamp}: {HeroCount} heroes, {AbilityCount} abilities.",
                reloadedUtc,
                snapshot.Data.Heroes.Count,
                snapshot.Data.Abilities.Count);
            return $"server data reloaded at {reloadedUtc:O}";
        }
        catch (Exception ex)
        {
            state.LastReloadedUtc = DateTime.UtcNow;
            state.LastError = $"{state.LastReloadedUtc:O}: server data reload failed: {ex.Message}";
            logger.LogError(ex, "Deadlock vdata files were updated, but server data reload failed.");
            return $"server data reload failed at {state.LastReloadedUtc:O}";
        }
    }

    private async Task<string> FetchLatestCommitAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var response = await client.GetAsync(CommitUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement))
        {
            throw new InvalidOperationException("GitHub commit response did not include a sha field.");
        }

        var sha = shaElement.GetString();
        if (string.IsNullOrWhiteSpace(sha))
        {
            throw new InvalidOperationException("GitHub commit sha was empty.");
        }

        return sha;
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        var bytes = await client.GetByteArrayAsync(url, cancellationToken);
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException($"Downloaded file from {url} was empty.");
        }

        return bytes;
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(nameof(DeadlockVDataUpdateService));
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeadlockAbilityDraft", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static DeadlockVDataState LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return new DeadlockVDataState();
        }

        try
        {
            return JsonSerializer.Deserialize<DeadlockVDataState>(File.ReadAllText(path, Encoding.UTF8), JsonOptions()) ?? new DeadlockVDataState();
        }
        catch
        {
            return new DeadlockVDataState();
        }
    }

    private static void SaveState(string path, DeadlockVDataState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions()), Encoding.UTF8);
    }

    private static void ReplaceFileSafely(string path, byte[] bytes)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, bytes);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class DeadlockVDataState
    {
        public string? CommitHash { get; set; }
        public DateTime? LastCheckedUtc { get; set; }
        public DateTime? LastUpdatedUtc { get; set; }
        public DateTime? LastReloadedUtc { get; set; }
        public string? LastMessage { get; set; }
        public string? LastError { get; set; }
    }
}
