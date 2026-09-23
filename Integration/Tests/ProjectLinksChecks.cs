using abilitydraft.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

static class ProjectLinksChecks
{
    public static ProjectLinksService Create(string temporary) => new(new Environment(Path.Combine(temporary, "links")), NullLogger<ProjectLinksService>.Instance);

    public static void Run(string temporary)
    {
        var root = Path.Combine(temporary, "download-settings");
        var path = Path.Combine(root, "links", "Data", "project-links.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"GameBananaUrl":"https://example.org/mod","SupportLinks":[{"Label":"Support","Url":"https://example.org/support"}]}""");
        var service = Create(root);
        Check(service.ModDownloadFileName == "pak01_dir.vpk", "Settings without a VPK number use a padded download filename");
        service.SaveModPakNumber(7);
        var reloaded = Create(root);
        Check(reloaded.ModDownloadFileName == "pak07_dir.vpk" && reloaded.GameBananaUrl == "https://example.org/mod" &&
            reloaded.GetSettings().SupportLinks.Single().Label == "Support", "Download filename persists without changing public links");
        reloaded.Save("https://example.org/new-mod", "Support Project", reloaded.GetSettings().SupportLinks);
        Check(Create(root).ModDownloadFileName == "pak07_dir.vpk", "Saving public links preserves the download number");
        foreach (var invalid in new[] { -1, 0, 1000 })
        {
            var rejected = false;
            try { reloaded.SaveModPakNumber(invalid); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && Create(root).ModDownloadFileName == "pak07_dir.vpk", "Invalid download numbers leave saved settings intact");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "LinksTests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
