using System.Net;
using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using abilitydraft.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static class GameDataUpdateChecks
{
    public static async Task Run(string root, string temporary, DeadlockFileParser parser)
    {
        var directory = Path.Combine(temporary, "data-update");
        Directory.CreateDirectory(directory);
        var heroes = File.ReadAllBytes(Path.Combine(root, "Data/Deadlock/heroes.vdata"));
        var abilities = File.ReadAllBytes(Path.Combine(root, "Data/Deadlock/abilities.vdata"));
        var heroesPath = Path.Combine(directory, "heroes.vdata");
        var abilitiesPath = Path.Combine(directory, "abilities.vdata");
        File.WriteAllBytes(heroesPath, heroes); File.WriteAllBytes(abilitiesPath, abilities);
        var environment = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory }).Environment;
        var config = Options.Create(new DeadlockDataOptions { GameDataPath = directory, IconsPath = Path.Combine(directory, "icons"), OutputPath = Path.Combine(directory, "output") });
        var data = new ServerDeadlockDataService(config, environment, parser);
        var network = new FakeDownloads(heroes, abilities);
        using var updater = new DeadlockVDataUpdateService(network, config, environment, data, parser, NullLogger<DeadlockVDataUpdateService>.Instance);
        var updated = await updater.VerifyNowAsync();
        Check(updated.Updated && data.Current.IsLoaded, "Game-data update validates and loads a complete matching pair: " + updater.GetStatus().LastError);
        Check(network.Urls.Count == 3 && network.Urls.Skip(1).All(u => u.Contains("/" + network.Commit + "/") && !u.Contains("/master/")),
            "Hero and ability downloads use the same immutable commit");
        network.Commit = new string('b', 40); network.InvalidHeroes = true;
        var failed = await updater.VerifyNowAsync();
        Check(!failed.Updated && File.ReadAllBytes(heroesPath).SequenceEqual(heroes) && File.ReadAllBytes(abilitiesPath).SequenceEqual(abilities) && data.Current.IsLoaded,
            "Malformed downloads preserve both installed files and the active data snapshot");
        network.InvalidHeroes = false; network.VersionTag = true;
        // Fail the second installation after the first replacement has succeeded.
        using (var locked = new FileStream(heroesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var blocked = await updater.VerifyNowAsync();
            Check(!blocked.Updated && File.ReadAllBytes(abilitiesPath).SequenceEqual(abilities),
                "A failed second-file replacement rolls back the first file");
        }
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "state.json")));
        Check(state.RootElement.GetProperty("commitHash").GetString() == new string('a', 40),
            "Failed updates cannot mark the candidate commit as installed");

        var logs = new UpdateLogs();
        using var offline = new DeadlockVDataUpdateService(network, config, environment, data, parser, logs);
        var loaded = data.Current;
        network.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "Host not found.");
        var unavailable = await offline.VerifyNowAsync();
        Check(!unavailable.Updated && ReferenceEquals(loaded, data.Current) &&
            File.ReadAllBytes(heroesPath).SequenceEqual(heroes) && File.ReadAllBytes(abilitiesPath).SequenceEqual(abilities),
            "GitHub DNS failure preserves both local files and the loaded game data");
        Check(logs.Entries is [{ Level: LogLevel.Warning, Exception: null }] &&
            logs.Entries[0].Message.Contains("DNS") && logs.Entries[0].Message.Contains("using existing local game data") &&
            offline.GetStatus().LastError is not null,
            "An offline update reports one concise warning and keeps diagnostics in update status");
        network.Failure = null; network.Commit = new string('c', 40); network.VersionTag = false;
        var recovered = await offline.VerifyNowAsync();
        Check(recovered.Updated && offline.GetStatus().LastError is null,
            "Updates recover after GitHub connectivity returns and clear the old error");

        var timeoutLogs = new UpdateLogs();
        using var timed = new DeadlockVDataUpdateService(network, config, environment, data, parser, timeoutLogs);
        network.Failure = new TaskCanceledException("Request timed out.", new TimeoutException());
        await timed.StartAsync(CancellationToken.None);
        await timeoutLogs.Warning.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (timed.GetStatus().IsRunning) await Task.Delay(10, deadline.Token);
        Check(timed.ExecuteTask is { IsCompleted: false } && timeoutLogs.Entries is [{ Level: LogLevel.Warning, Exception: null }] &&
            timeoutLogs.Entries[0].Message.Contains("timed out"),
            "An HTTP timeout leaves the scheduled updater running and reports a single warning");
        network.Failure = null; network.Commit = new string('d', 40);
        Check((await timed.VerifyNowAsync()).Updated, "The updater accepts another check after a timeout");
        await timed.StopAsync(CancellationToken.None);

        logs.Entries.Clear();
        using (var shutdown = new CancellationTokenSource())
        {
            network.BeforeResponse = shutdown.Cancel;
            var cancelled = false;
            try { await offline.VerifyNowAsync(shutdown.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            finally { network.BeforeResponse = null; }
            Check(cancelled && logs.Entries.Count == 0 && offline.GetStatus().LastError is null,
                "Caller cancellation exits without recording an outage or logging an error");
        }

        var emptyOptions = Options.Create(new DeadlockDataOptions { GameDataPath = Path.Combine(directory, "missing") });
        var empty = new ServerDeadlockDataService(emptyOptions, environment, parser);
        network.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "Host not found.");
        using var missing = new DeadlockVDataUpdateService(network, emptyOptions, environment, empty, parser, logs);
        await missing.VerifyNowAsync();
        Check(logs.Entries is [{ Level: LogLevel.Error, Exception: null }] &&
            missing.GetStatus().Status.Contains("no local game data is loaded"),
            "Network failure without usable local data remains an explicit error");
    }

    sealed class FakeDownloads(byte[] heroes, byte[] abilities) : HttpMessageHandler, IHttpClientFactory
    {
        public string Commit = new('a', 40);
        public bool InvalidHeroes, VersionTag;
        public Exception? Failure;
        public Action? BeforeResponse;
        public readonly List<string> Urls = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BeforeResponse?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            var url = request.RequestUri!.AbsoluteUri; Urls.Add(url);
            var bytes = url.Contains("api.github.com") ? Encoding.UTF8.GetBytes("{\"sha\":\"" + Commit + "\"}") :
                url.EndsWith("heroes.vdata") ? InvalidHeroes ? "{ broken"u8.ToArray() : heroes :
                VersionTag ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(abilities) + "\n// candidate version\n") : abilities;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    sealed class UpdateLogs : ILogger<DeadlockVDataUpdateService>
    {
        public readonly List<(LogLevel Level, Exception? Exception, string Message)> Entries = [];
        public readonly TaskCompletionSource Warning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            Entries.Add((level, exception, formatter(state, exception)));
            if (level == LogLevel.Warning) Warning.TrySetResult();
        }
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
