using System.Text.Json;

namespace abilitydraft.Services;

public sealed record ProjectLink(string Label, string Url);

public sealed record ProjectLinksSettings
{
    public int ModPakNumber { get; init; } = 1;
    public string GameBananaUrl { get; init; } = string.Empty;
    public string SupportHeading { get; init; } = "Support Project";
    public List<ProjectLink> SupportLinks { get; init; } = [];
}

/// <summary>Stores public links and mod download settings outside the repository.</summary>
public sealed class ProjectLinksService(IWebHostEnvironment environment, ILogger<ProjectLinksService> logger)
{
    private readonly object sync = new();
    private readonly string path = Path.Combine(environment.ContentRootPath, "Data", "project-links.json");
    private ProjectLinksSettings settings = new();
    private bool loaded;

    public event Action? Changed;

    public ProjectLinksSettings GetSettings()
    {
        lock (sync)
        {
            EnsureLoaded();
            return settings with { SupportLinks = settings.SupportLinks?.ToList() ?? [] };
        }
    }

    public IReadOnlyList<ProjectLink> FooterLinks()
    {
        var current = GetSettings();
        return new[]
        {
            new ProjectLink("GitHub", "https://github.com/Binger4"),
            new ProjectLink("GitHub", "https://github.com/Binger4/Deadlock-Ability-Draft"),
            new ProjectLink("Discord", "https://discord.gg/SxQjYeA7aW")
        }.Concat((current.SupportLinks ?? []).Where(link => IsSafeUrl(link.Url))).ToArray();
    }

    public void Save(string? gameBananaUrl, string? heading, IEnumerable<ProjectLink> links)
    {
        var next = new ProjectLinksSettings
        {
            GameBananaUrl = NormalizeUrl(gameBananaUrl),
            SupportHeading = string.IsNullOrWhiteSpace(heading) ? "Support Project" : heading.Trim(),
            SupportLinks = links
                .Select(link => new ProjectLink((link.Label ?? string.Empty).Trim(), NormalizeUrl(link.Url)))
                .Where(link => !string.IsNullOrWhiteSpace(link.Label) && IsSafeUrl(link.Url))
                .Take(8)
                .ToList()
        };

        lock (sync)
        {
            EnsureLoaded();
            Persist(next with { ModPakNumber = settings.ModPakNumber });
        }
        Changed?.Invoke();
    }

    public void SaveModPakNumber(int number)
    {
        if (number is < 1 or > 999)
            throw new InvalidOperationException("Enter a VPK number between 1 and 999.");

        lock (sync)
        {
            EnsureLoaded();
            Persist(settings with { ModPakNumber = number });
        }
        Changed?.Invoke();
    }

    public string ModDownloadFileName => $"pak{GetSettings().ModPakNumber.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)}_dir.vpk";

    public string? GameBananaUrl => GetSettings().GameBananaUrl is { Length: > 0 } value ? value : null;

    private void Persist(ProjectLinksSettings next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
        settings = next;
    }

    private void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<ProjectLinksSettings>(File.ReadAllText(path)) ?? new();
            if (settings.ModPakNumber is < 1 or > 999)
                settings = settings with { ModPakNumber = 1 };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load project links; optional links are disabled.");
            settings = new();
        }
    }

    private static string NormalizeUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" ? uri.ToString() : string.Empty;

    private static bool IsSafeUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}
