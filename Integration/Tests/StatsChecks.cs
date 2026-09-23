using System.Text.Json;
using abilitydraft.Models;
using abilitydraft.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static class StatsChecks
{
    public static void Run(string temporary)
    {
        var root = Path.Combine(temporary, "stats-history");
        Directory.CreateDirectory(Path.Combine(root, "Data/Stats"));
        var path = Path.Combine(root, "Data/Stats/completed-drafts.json");
        var now = DateTime.UtcNow;
        // Deliberately use the old schema, without a source field.
        var legacy = Enumerable.Range(0, 5005).Select(i => new { hostName = "Host", draftCode = "OLD" + i,
            completedUtc = now.AddMinutes(-i), playerCount = 12 }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(legacy));
        var stats = new DraftStatsService(new Environment(root), Options.Create(new DraftStatsOptions()), NullLogger<DraftStatsService>.Instance);
        var first = stats.GetCompletedDraftPage();
        Check(first is { Total: 5005, Pages: 51, Page: 1 } && first.Items.Count == 100 && first.Items.All(p => p.Source == "web"),
            "Legacy history defaults to web and sends only 100 of 5005 drafts to the panel");
        var second = stats.GetCompletedDraftPage(2);
        Check(second.Items.Count == 100 && !second.Items.Select(r => r.DraftCode).Intersect(first.Items.Select(r => r.DraftCode)).Any(),
            "Completed history pages do not repeat entries");
        Check(stats.GetCompletedDraftPage(int.MaxValue) is { Page: 51, Items.Count: 5 } && stats.GetCompletedDraftPage(-9).Page == 1,
            "Completed pagination clamps invalid pages and keeps the last partial page");
        stats.RecordCompletedDraft(new DraftRoom { Code = "PUBLIC", InGameManaged = true, IsPublicQueue = true });
        var updated = stats.GetCompletedDraftPage();
        Check(updated.Total == 5006 && updated.Items[0] is { Source: "public", HostName: "none" },
            "New public statistics invalidate the cache and persist source with no host");
        stats.RecordCompletedDraft(new DraftRoom { Code = "CUSTOM", InGameManaged = true });
        var reloaded = new DraftStatsService(new Environment(root), Options.Create(new DraftStatsOptions()), NullLogger<DraftStatsService>.Instance).GetCompletedDraftPage();
        Check(reloaded.Total == 5007 && reloaded.Items[0].Source == "custom" && reloaded.Items.Skip(2).All(r => r.Source == "web"),
            "Source flags survive reload without losing legacy history");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "StatsTests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
