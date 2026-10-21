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
    DeadlockFileParser parser,
    ILogger<DeadlockVDataUpdateService> logger) : BackgroundService
{
    private const string AbilitiesUrl = "https://raw.githubusercontent.com/SteamTracking/GameTracking-Deadlock/master/game/citadel/pak01_dir/scripts/abilities.vdata";
    private const string HeroesUrl = "https://raw.githubusercontent.com/SteamTracking/GameTracking-Deadlock/master/game/citadel/pak01_dir/scripts/heroes.vdata";
    private const string CommitUrl = "https://api.github.com/repos/SteamTracking/GameTracking-Deadlock/commits/master";

    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly object _stateLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutomaticUpdatesEnabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScheduledCheckAsync(stoppingToken);
                await Task.Delay(UpdateInterval(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deadlock vdata update loop failed. The next scheduled check will still run.");
            }
        }
    }

    public DeadlockVDataUpdateStatus GetStatus()
    {
        var state = LoadState();
        return new DeadlockVDataUpdateStatus(
            _updateLock.CurrentCount == 0,
            UpdateIntervalMinutes(),
            state.LastCheckedUtc,
            state.LastUpdatedUtc,
            state.LastReloadedUtc,
            state.LastCheckedCommitHash ?? state.CommitHash,
            StatusText(state),
            state.LastError,
            state.LastErrorUtc);
    }

    public Task<DeadlockVDataCheckResult> VerifyNowAsync(CancellationToken cancellationToken = default) =>
        CheckAndUpdateAsync("manual", cancellationToken);

    private async Task RunScheduledCheckAsync(CancellationToken cancellationToken)
    {
        var result = await CheckAndUpdateAsync("scheduled", cancellationToken);
        if (result.AlreadyRunning)
        {
            logger.LogInformation("Skipped scheduled vdata check because another update is already running.");
        }
    }

    private async Task<DeadlockVDataCheckResult> CheckAndUpdateAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!await _updateLock.WaitAsync(0, cancellationToken))
        {
            return new DeadlockVDataCheckResult(true, false, "Vdata update is already in progress.");
        }

        try
        {
            return await CheckAndUpdateCoreAsync(trigger, cancellationToken);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<DeadlockVDataCheckResult> CheckAndUpdateCoreAsync(string trigger, CancellationToken cancellationToken)
    {
        var dataPath = Resolve(options.Value.GameDataPath);
        Directory.CreateDirectory(dataPath);

        var abilitiesPath = Path.Combine(dataPath, "abilities.vdata");
        var heroesPath = Path.Combine(dataPath, "heroes.vdata");
        var state = LoadState();
        var checkedUtc = DateTime.UtcNow;

        try
        {
            var latestCommit = await FetchLatestCommitAsync(cancellationToken);
            var missingFiles = !File.Exists(abilitiesPath) || !File.Exists(heroesPath);
            var needsUpdate = missingFiles || !string.Equals(state.CommitHash, latestCommit, StringComparison.Ordinal);

            state.LastCheckedUtc = checkedUtc;
            state.LastCheckedCommitHash = latestCommit;
            state.LastError = null;
            state.LastErrorUtc = null;

            if (!needsUpdate)
            {
                state.LastResult = "files are up to date";
                state.LastMessage = $"vdata files are up to date, last check at {checkedUtc:O}";
                SaveState(state);
                logger.LogInformation("{Message}", state.LastMessage);
                return new DeadlockVDataCheckResult(false, false, state.LastMessage);
            }

            // Pin both downloads to the commit we checked. A moving branch can
            // otherwise mix hero bindings from one build with another's abilities.
            var abilitiesBytes = await DownloadBytesAsync(AbilitiesUrl.Replace("/master/", $"/{latestCommit}/", StringComparison.Ordinal), cancellationToken);
            var heroesBytes = await DownloadBytesAsync(HeroesUrl.Replace("/master/", $"/{latestCommit}/", StringComparison.Ordinal), cancellationToken);
            var candidate = parser.Parse(new UploadedDeadlockFiles(Encoding.UTF8.GetString(heroesBytes),
                Encoding.UTF8.GetString(abilitiesBytes), new Dictionary<string, string>(),
                new Dictionary<string, string>(), new Dictionary<string, string>(), []));
            if (candidate.Heroes.Count < 12 || candidate.Abilities.Count == 0 ||
                DeadlockFileParser.GetString(candidate.HeroesDocument.Root, "generic_data_type") != "CitadelHeroData_t" ||
                DeadlockFileParser.GetString(candidate.AbilitiesDocument.Root, "generic_data_type") != "CitadelAbilityVData" ||
                // Valve also ships unfinished/prototype heroes with unresolved
                // bindings. Require enough complete loadouts for 6v6 instead.
                candidate.Heroes.Count(h => new[] { h.WeaponAbility, h.Ability1, h.Ability2, h.Ability3, h.Ultimate }
                    .All(candidate.AbilitiesDocument.Root.ContainsKey)) < 12)
                throw new InvalidDataException("Downloaded game data is incomplete or has unresolved hero abilities; existing files were kept.");

            ReplaceFilesSafely([(abilitiesPath, abilitiesBytes), (heroesPath, heroesBytes)]);

            var updatedUtc = DateTime.UtcNow;
            state.CommitHash = latestCommit;
            state.LastUpdatedUtc = updatedUtc;
            state.LastResult = missingFiles ? "files downloaded" : "files updated";
            var reloadMessage = ReloadServerData(state);
            state.LastMessage = missingFiles
                ? $"vdata files downloaded for the first time at {updatedUtc:O}; {reloadMessage}"
                : $"vdata files successfully updated at {updatedUtc:O}; {reloadMessage}";
            SaveState(state);

            logger.LogInformation("{Message}", state.LastMessage);
            return new DeadlockVDataCheckResult(false, true, state.LastMessage);
        }
        // HttpClient timeouts also throw OperationCanceledException. Only the
        // caller's cancellation should stop the hosted update loop.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var errorUtc = DateTime.UtcNow;
            state.LastCheckedUtc = checkedUtc;
            state.LastErrorUtc = errorUtc;
            state.LastError = $"{errorUtc:O}: {ex.Message}";
            var networkFailure = ex is HttpRequestException or OperationCanceledException;
            var hasLocalData = dataService.Current.IsLoaded;
            state.LastResult = networkFailure
                ? hasLocalData ? "GitHub update check unavailable; using existing local game data"
                    : "GitHub update check unavailable; no local game data is loaded"
                : $"GitHub check failed during {trigger}; local files were kept";
            state.LastMessage = state.LastResult;
            SaveState(state);
            if (networkFailure)
            {
                var reason = ex switch
                {
                    OperationCanceledException => "request timed out",
                    HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "GitHub DNS lookup failed",
                    HttpRequestException { StatusCode: { } status } => $"GitHub returned HTTP {(int)status}",
                    _ => "GitHub connection failed"
                };
                var retry = options.Value.AutomaticUpdatesEnabled
                    ? $"Next scheduled check in {UpdateIntervalMinutes()} minutes."
                    : "Use Verify now to retry.";
                logger.Log(hasLocalData ? LogLevel.Warning : LogLevel.Error,
                    "{Message} ({Reason}). {Retry}", state.LastMessage, reason, retry);
                logger.LogDebug(ex, "Deadlock vdata update network diagnostics.");
            }
            else
            {
                logger.LogError(ex, "Deadlock vdata update check failed at {Timestamp}. Existing local files were kept.", errorUtc);
            }
            return new DeadlockVDataCheckResult(false, false, state.LastMessage);
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
            state.LastErrorUtc = state.LastReloadedUtc;
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
        if (sha is not { Length: 40 } || !sha.All(char.IsAsciiHexDigit))
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
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeadlockAbilityDraft", "1.0"));
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private DeadlockVDataState LoadState()
    {
        lock (_stateLock)
        {
            var path = StatePath();
            if (!File.Exists(path))
            {
                return new DeadlockVDataState();
            }

            try
            {
                return JsonSerializer.Deserialize<DeadlockVDataState>(File.ReadAllText(path, Encoding.UTF8), JsonOptions()) ?? new DeadlockVDataState();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Deadlock vdata state file is missing, corrupted, or unreadable. Returning empty status.");
                return new DeadlockVDataState
                {
                    LastError = $"{DateTime.UtcNow:O}: state file could not be read: {ex.Message}",
                    LastErrorUtc = DateTime.UtcNow,
                    LastResult = "state file could not be read"
                };
            }
        }
    }

    private void SaveState(DeadlockVDataState state)
    {
        lock (_stateLock)
        {
            var path = StatePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions()), Encoding.UTF8);
        }
    }

    private string StatePath() =>
        Path.Combine(Resolve(options.Value.GameDataPath), "state.json");

    private static void ReplaceFilesSafely((string Path, byte[] Bytes)[] files)
    {
        var staged = files.Select(f => (f.Path, f.Bytes, Temporary: $"{f.Path}.{Guid.NewGuid():N}.tmp",
            Previous: File.Exists(f.Path) ? File.ReadAllBytes(f.Path) : null)).ToArray();
        var installed = 0;
        try
        {
            foreach (var file in staged) File.WriteAllBytes(file.Temporary, file.Bytes);
            foreach (var file in staged)
            { File.Move(file.Temporary, file.Path, overwrite: true); installed++; }
        }
        catch
        {
            // A failure installing the second file must not leave a mixed pair.
            foreach (var file in staged.Take(installed))
            {
                if (file.Previous is null) File.Delete(file.Path);
                else
                {
                    File.WriteAllBytes(file.Temporary, file.Previous);
                    File.Move(file.Temporary, file.Path, overwrite: true);
                }
            }
            throw;
        }
        finally { foreach (var file in staged) if (File.Exists(file.Temporary)) File.Delete(file.Temporary); }
    }

    private TimeSpan UpdateInterval() =>
        TimeSpan.FromMinutes(UpdateIntervalMinutes());

    private int UpdateIntervalMinutes() =>
        options.Value.UpdateIntervalMinutes > 0 ? options.Value.UpdateIntervalMinutes : 60;

    private string Resolve(string path) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));

    private static string StatusText(DeadlockVDataState state)
    {
        if (!string.IsNullOrWhiteSpace(state.LastResult))
        {
            return state.LastResult;
        }

        return state.LastCheckedUtc is null ? "not checked yet" : "last check completed";
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class DeadlockVDataState
    {
        public string? CommitHash { get; set; }
        public string? LastCheckedCommitHash { get; set; }
        public DateTime? LastCheckedUtc { get; set; }
        public DateTime? LastUpdatedUtc { get; set; }
        public DateTime? LastReloadedUtc { get; set; }
        public string? LastResult { get; set; }
        public string? LastMessage { get; set; }
        public string? LastError { get; set; }
        public DateTime? LastErrorUtc { get; set; }
    }
}
