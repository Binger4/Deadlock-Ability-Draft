using System.Net;
using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using abilitydraft.Services;
using Microsoft.AspNetCore.Builder;
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
    }

    sealed class FakeDownloads(byte[] heroes, byte[] abilities) : HttpMessageHandler, IHttpClientFactory
    {
        public string Commit = new('a', 40);
        public bool InvalidHeroes, VersionTag;
        public readonly List<string> Urls = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri; Urls.Add(url);
            var bytes = url.Contains("api.github.com") ? Encoding.UTF8.GetBytes("{\"sha\":\"" + Commit + "\"}") :
                url.EndsWith("heroes.vdata") ? InvalidHeroes ? "{ broken"u8.ToArray() : heroes :
                VersionTag ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(abilities) + "\n// candidate version\n") : abilities;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
}
