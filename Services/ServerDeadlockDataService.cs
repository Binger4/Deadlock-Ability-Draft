using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services;

public sealed class ServerDeadlockDataService(
    IOptions<DeadlockDataOptions> options,
    IWebHostEnvironment environment,
    DeadlockFileParser parser)
{
    private readonly object _lock = new();
    private DeadlockDataSnapshot _snapshot = new() { LoadedUtc = DateTime.UtcNow };

    public DeadlockDataSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _snapshot;
            }
        }
    }

    public DeadlockDataSnapshot Reload()
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var config = options.Value;
        var gameDataPath = Resolve(config.GameDataPath);
        var iconsPath = Resolve(config.IconsPath);
        var outputPath = Resolve(config.OutputPath);

        Directory.CreateDirectory(gameDataPath);
        Directory.CreateDirectory(iconsPath);
        Directory.CreateDirectory(Path.Combine(iconsPath, "Abilities"));
        Directory.CreateDirectory(Path.Combine(iconsPath, "Heroes"));
        Directory.CreateDirectory(outputPath);

        var heroesPath = FindFirst(gameDataPath, "heroes.vdata");
        var abilitiesPath = FindFirst(gameDataPath, "abilities.vdata");
        if (heroesPath is null)
        {
            errors.Add($"Missing heroes.vdata under {gameDataPath}.");
        }

        if (abilitiesPath is null)
        {
            errors.Add($"Missing abilities.vdata under {gameDataPath}.");
        }

        var bans = LoadBans(Path.Combine(gameDataPath, "bans.json"), warnings);
        var overrides = LoadSiteLocalisationOverrides(Path.Combine(gameDataPath, "site_localisation_overrides.json"), warnings);
        ParsedDeadlockData? data = null;

        if (errors.Count == 0 && heroesPath is not null && abilitiesPath is not null)
        {
            try
            {
                var localisations = LoadLocalisationFiles(gameDataPath);
                if (localisations.Count == 0)
                {
                    warnings.Add($"No localisation files found under {gameDataPath}. Internal keys will be used as fallback names.");
                }

                var icons = LoadIconFiles(iconsPath, warnings);
                data = parser.Parse(new UploadedDeadlockFiles(
                    File.ReadAllText(heroesPath, Encoding.UTF8),
                    File.ReadAllText(abilitiesPath, Encoding.UTF8),
                    localisations,
                    icons,
                    warnings));
                ApplySiteLocalisationOverrides(data, overrides);
                warnings.AddRange(data.Warnings);
                GenerateAbilityIconList(data, outputPath, warnings);
                GenerateHeroIconList(data, outputPath, warnings);
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to load Deadlock data: {ex.Message}");
            }
        }

        var snapshot = new DeadlockDataSnapshot
        {
            Data = data,
            Bans = bans,
            SiteLocalisationOverrides = overrides,
            LoadedUtc = DateTime.UtcNow,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            Errors = errors
        };

        lock (_lock)
        {
            _snapshot = snapshot;
        }

        return snapshot;
    }

    public string GenerateAbilityIconList()
    {
        var snapshot = Current;
        var outputPath = Resolve(options.Value.OutputPath);
        Directory.CreateDirectory(outputPath);

        if (snapshot.Data is null)
        {
            var path = Path.Combine(outputPath, "all_abilities_icon_names.txt");
            File.WriteAllText(path, "Deadlock data is not loaded. Reload data after adding heroes.vdata and abilities.vdata.", Encoding.UTF8);
            return path;
        }

        return GenerateAbilityIconList(snapshot.Data, outputPath, []);
    }

    public string GenerateHeroIconList()
    {
        var snapshot = Current;
        var outputPath = Resolve(options.Value.OutputPath);
        Directory.CreateDirectory(outputPath);

        if (snapshot.Data is null)
        {
            var path = Path.Combine(outputPath, "all_heroes_icon_names.txt");
            File.WriteAllText(path, "Deadlock data is not loaded. Reload data after adding heroes.vdata and abilities.vdata.", Encoding.UTF8);
            return path;
        }

        return GenerateHeroIconList(snapshot.Data, outputPath, []);
    }

    public DeadlockDataSnapshot SaveBans(IEnumerable<string> bannedHeroes, IEnumerable<string> bannedAbilities, IEnumerable<string>? unbannedAbilities = null)
    {
        var path = Path.Combine(Resolve(options.Value.GameDataPath), "bans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new BansJson
        {
            BannedHeroes = bannedHeroes.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            BannedAbilities = bannedAbilities.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            UnbannedAbilities = (unbannedAbilities ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions()), Encoding.UTF8);
        return Reload();
    }

    public DeadlockDataSnapshot SaveSiteLocalisationOverrides(SiteLocalisationOverrides overrides)
    {
        var path = Path.Combine(Resolve(options.Value.GameDataPath), "site_localisation_overrides.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cleaned = new SiteLocalisationOverrides
        {
            Heroes = overrides.Heroes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal),
            Abilities = overrides.Abilities
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.Ordinal)
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cleaned, JsonOptions()), Encoding.UTF8);
        return Reload();
    }

    private string GenerateAbilityIconList(ParsedDeadlockData data, string outputPath, List<string> warnings)
    {
        var path = Path.Combine(outputPath, "all_abilities_icon_names.txt");
        var builder = new StringBuilder();
        if (!data.HasLocalisation)
        {
            builder.AppendLine("WARNING: localisation was not found. Display names below use fallback/internal names.");
            builder.AppendLine();
        }

        builder.AppendLine("Display Name | Ability Key | Hero Key | Hero Display Name | Slot | Recommended Icon File");
        foreach (var ability in data.Abilities
                     .Where(ability => ability.PickKind is DraftPickKind.RegularAbility or DraftPickKind.UltimateAbility)
                     .OrderBy(ability => ability.SourceHeroName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(ability => SlotOrder(ability.SourceSlot))
                     .ThenBy(ability => ability.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var slot = ability.SourceSlot == "Ult" ? "Ultimate" : ability.SourceSlot;
            builder.AppendLine($"{ability.DisplayName} | {ability.Key} | {ability.SourceHeroKey} | {ability.SourceHeroName} | {slot} | {ability.Key}.png");
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        warnings.Add($"Generated ability icon naming list at {path}.");
        return path;
    }

    private string GenerateHeroIconList(ParsedDeadlockData data, string outputPath, List<string> warnings)
    {
        var path = Path.Combine(outputPath, "all_heroes_icon_names.txt");
        var builder = new StringBuilder();
        if (!data.HasLocalisation)
        {
            builder.AppendLine("WARNING: localisation was not found. Display names below use fallback/internal names.");
            builder.AppendLine();
        }

        builder.AppendLine("Display Name | Hero Key | Recommended Icon File");
        foreach (var hero in data.Heroes.OrderBy(hero => hero.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{hero.DisplayName} | {hero.Key} | {hero.Key}.png");
        }

        File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
        warnings.Add($"Generated hero icon naming list at {path}.");
        return path;
    }

    private static int SlotOrder(string slot) => slot switch
    {
        "1" => 1,
        "2" => 2,
        "3" => 3,
        "Ult" => 4,
        _ => 99
    };

    private static DeadlockBanList LoadBans(string path, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\n  \"bannedHeroes\": [],\n  \"bannedAbilities\": [],\n  \"unbannedAbilities\": []\n}\n", Encoding.UTF8);
            warnings.Add($"Created empty bans file at {path}.");
            return new DeadlockBanList();
        }

        try
        {
            var document = JsonSerializer.Deserialize<BansJson>(File.ReadAllText(path, Encoding.UTF8), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new BansJson();
            return new DeadlockBanList
            {
                BannedHeroes = document.BannedHeroes.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal),
                BannedAbilities = document.BannedAbilities.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal),
                UnbannedAbilities = document.UnbannedAbilities.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal)
            };
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse bans.json at {path}: {ex.Message}");
            return new DeadlockBanList();
        }
    }

    private static SiteLocalisationOverrides LoadSiteLocalisationOverrides(string path, List<string> warnings)
    {
        if (!File.Exists(path))
        {
            return new SiteLocalisationOverrides();
        }

        try
        {
            return JsonSerializer.Deserialize<SiteLocalisationOverrides>(File.ReadAllText(path, Encoding.UTF8), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new SiteLocalisationOverrides();
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not parse site_localisation_overrides.json at {path}: {ex.Message}");
            return new SiteLocalisationOverrides();
        }
    }

    private static void ApplySiteLocalisationOverrides(ParsedDeadlockData data, SiteLocalisationOverrides overrides)
    {
        foreach (var hero in data.Heroes)
        {
            if (overrides.Heroes.TryGetValue(hero.Key, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                hero.DisplayName = displayName.Trim();
            }
        }

        foreach (var ability in data.Abilities)
        {
            if (overrides.Abilities.TryGetValue(ability.Key, out var displayName) && !string.IsNullOrWhiteSpace(displayName))
            {
                ability.DisplayName = displayName.Trim();
            }

            var sourceHero = data.Heroes.FirstOrDefault(hero => hero.Key == ability.SourceHeroKey);
            if (sourceHero is not null)
            {
                ability.SourceHeroName = sourceHero.DisplayName;
            }
        }
    }

    private static Dictionary<string, string> LoadLocalisationFiles(string gameDataPath)
    {
        return Directory.EnumerateFiles(gameDataPath, "*.txt", SearchOption.AllDirectories)
            .Where(path => path.Contains("citadel_heroes", StringComparison.OrdinalIgnoreCase) ||
                           path.Contains("citadel_gc_hero_names", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetFileName(path) ?? path, path => File.ReadAllText(path, Encoding.UTF8), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> LoadIconFiles(string iconsPath, List<string> warnings)
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(iconsPath, "*.*", SearchOption.AllDirectories).Where(IsSupportedImage))
        {
            try
            {
                var key = Path.GetFileNameWithoutExtension(path);
                var bytes = File.ReadAllBytes(path);
                icons[key] = $"data:{ContentType(path)};base64,{Convert.ToBase64String(bytes)}";
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not load icon {path}: {ex.Message}");
            }
        }

        return icons;
    }

    private static string? FindFirst(string root, string fileName)
    {
        return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    private string Resolve(string path)
    {
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }

    private static bool IsSupportedImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string ContentType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    private sealed class BansJson
    {
        public List<string> BannedHeroes { get; set; } = [];
        public List<string> BannedAbilities { get; set; } = [];
        public List<string> UnbannedAbilities { get; set; } = [];
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
