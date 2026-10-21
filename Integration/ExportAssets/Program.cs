using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

// Builds and publishing must work without the developer's game or generated dist folder.
// The API is a compile-time reference only; Deadworks supplies it on the game host.
const string apiHash = "8953525BA9C18EEA39F3A2F3C82172D8E149A2F1087F035D276C3091A1DA7597";
const string releaseUrl = "https://github.com/Deadworks-net/deadworks/releases/download/v0.6.2/deadworks-v0.6.2.zip";
const string localisationCommit = "37f9aebdc62766aa13748f73309b9b8120549833";

try
{
    if (args.Length != 2) throw new ArgumentException("Usage: AbilityDraft.ExportAssets <repository-root> <configuration>");
    var root = Path.GetFullPath(args[0]);
    var configuration = args[1];
    var cache = Path.Combine(root, "Integration/local/export-assets");
    Directory.CreateDirectory(cache);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AbilityDraft-ExportAssets/1.0");

    var api = Path.Combine(root, "Tools/DeadworksRelease/v0.6.2/game/bin/win64/managed/DeadworksManaged.Api.dll");
    if (!ValidApi(api))
    {
        api = Path.Combine(cache, "sdk/DeadworksManaged.Api.dll");
        if (!ValidApi(api))
        {
            Console.WriteLine("Preparing the official Deadworks 0.6.2 compile-time reference...");
            var archivePath = Path.Combine(cache, "deadworks-v0.6.2.zip");
            await Download(releaseUrl, archivePath);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var entry = archive.Entries.Single(e => e.FullName.Replace('\\', '/').EndsWith("/managed/DeadworksManaged.Api.dll", StringComparison.Ordinal));
                Directory.CreateDirectory(Path.GetDirectoryName(api)!);
                entry.ExtractToFile(api, overwrite: true);
            }
            if (!ValidApi(api)) throw new InvalidDataException("The downloaded Deadworks API does not match the pinned official SHA-256.");
            File.Delete(archivePath);
        }
    }

    var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    {
        WorkingDirectory = root,
        UseShellExecute = false
    };
    foreach (var argument in new[] { "build", "Integration/Gameplay/AbilityDraft.Gameplay.csproj", "--configuration", configuration,
        "--nologo", "--verbosity", "quiet", "-m:1", "-p:DeadworksManagedDir=" + Path.GetDirectoryName(api) })
        start.ArgumentList.Add(argument);
    using var build = Process.Start(start) ?? throw new IOException("Could not start the gameplay companion build.");
    await build.WaitForExitAsync();
    if (build.ExitCode != 0) throw new InvalidOperationException("Building the gameplay export companion failed.");
    var companion = Path.Combine(root, "Integration/dist/gameplay/AbilityDraft.Gameplay.dll");
    Directory.CreateDirectory(Path.GetDirectoryName(companion)!);
    File.Copy(Path.Combine(root, "Integration/Gameplay/bin", configuration, "net10.0/AbilityDraft.Gameplay.dll"), companion, overwrite: true);

    var localisation = Path.Combine(root, "Integration/dist/localization/resource/localization/citadel_heroes");
    Directory.CreateDirectory(localisation);
    // GameTracking distributes English. It also supplies the game's fallback tokens
    // for languages without aliases. Keep any translated templates built locally.
    {
        const string name = "citadel_heroes_english.txt";
        var destination = Path.Combine(localisation, name);
        if (!File.Exists(destination) || !File.ReadAllText(destination).Contains("\"ability_nurse_healing_needles\"", StringComparison.Ordinal))
        {
            Console.WriteLine("Preparing hero localization for exports...");
            await Download($"https://raw.githubusercontent.com/SteamTracking/GameTracking-Deadlock/{localisationCommit}/game/citadel/resource/localization/citadel_heroes/{name}", destination);
        }
        var tokens = File.ReadAllText(destination);
        if (!tokens.Contains("\"ability_artist_homingbrush\"", StringComparison.Ordinal) ||
            !tokens.Contains("\"ability_nurse_healing_needles\"", StringComparison.Ordinal))
            throw new InvalidDataException($"Missing Living Ink or Healing Needles tokens in {destination}. Delete this cached template and build again.");
    }
    Console.WriteLine("Gameplay export companion and localization templates are ready.");

    async Task Download(string url, string destination)
    {
        // An interrupted download must never become a reusable cached asset.
        var temporary = destination + ".tmp";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temporary)) await response.Content.CopyToAsync(output);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Preparing website export assets failed: " + ex.Message);
    return 1;
}
return 0;

static bool ValidApi(string path) => File.Exists(path) &&
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == apiHash;
