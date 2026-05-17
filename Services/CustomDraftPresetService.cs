using System.Text.Json;
using System.Text.Json.Serialization;
using abilitydraft.Models;

namespace abilitydraft.Services;

public static class CustomDraftPresetService
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static string Export(DraftRoomConfig config)
    {
        var preset = new CustomDraftPreset
        {
            Version = CurrentVersion,
            Mode = DraftMode.Custom.ToString(),
            Settings = new CustomDraftPresetSettings
            {
                BaseDraftType = config.CustomBaseDraftMode,
                FullRandom = config.FullRandom,
                BlindDraft = config.BlindDraft,
                HeroPoolSize = config.HeroPoolSize,
                MaxPlayers = config.MaxPlayers,
                PickOrder = config.PickOrder,
                AutoPickBehavior = config.AutoPickBehavior,
                TeamBalance = config.TeamBalance,
                PreparationTimeSeconds = config.PreparationSeconds,
                PickTimerSeconds = config.PickSeconds,
                AllowDuplicateHeroes = config.AllowDuplicateHeroes,
                AllowDuplicateAbilities = config.AllowDuplicateAbilities,
                FlexibleUltimateSlots = config.FlexibleUltimateSlots,
                AllowHostOverride = config.AllowHostOverridePicks,
                DisableChat = config.DisableChat,
                AllowEmptySlotsAsBots = config.AllowEmptySlotsAsBots
            },
            Bans = new CustomDraftPresetBans
            {
                Heroes = Sorted(config.CustomBans.BannedHeroes),
                UnbannedHeroes = Sorted(config.CustomBans.UnbannedHeroes),
                Abilities = Sorted(config.CustomBans.BannedAbilities),
                UnbannedAbilities = Sorted(config.CustomBans.UnbannedAbilities)
            },
            CreatedAtUtc = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(preset, JsonOptions);
    }

    public static CustomDraftPresetImportResult Import(
        string json,
        ParsedDeadlockData data,
        DeadlockBanList globalBans,
        DraftTimingOptions timing)
    {
        CustomDraftPreset? preset;
        try
        {
            preset = JsonSerializer.Deserialize<CustomDraftPreset>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid preset file.");
        }

        if (preset is null)
        {
            throw new InvalidDataException("Invalid preset file.");
        }

        if (preset.Version <= 0)
        {
            throw new InvalidDataException("Preset version is missing.");
        }

        if (preset.Version != CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported preset version {preset.Version}.");
        }

        if (!string.Equals(preset.Mode, DraftMode.Custom.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Preset is not a Custom draft preset.");
        }

        if (preset.Settings is null)
        {
            throw new InvalidDataException("Preset settings are missing.");
        }

        var warnings = new List<string>();
        var bans = FilterBans(preset.Bans, data, warnings);
        var maxHeroes = Math.Max(1, data.Heroes.Count);
        var defaultPreparation = PositiveOrDefault(timing.PreparationSeconds, 30);
        var defaultPick = PositiveOrDefault(timing.PickSeconds, 10);
        var heroPoolSize = ClampPositive(preset.Settings.HeroPoolSize, 12, 1, maxHeroes, "Hero pool size", warnings);
        var maxPlayers = ClampPositive(preset.Settings.MaxPlayers, 12, 1, heroPoolSize, "Max players", warnings);
        var preparationSeconds = PositiveSetting(preset.Settings.PreparationTimeSeconds, defaultPreparation, "Preparation time", warnings);
        var pickSeconds = PositiveSetting(preset.Settings.PickTimerSeconds, defaultPick, "Pick timer", warnings);
        var config = new DraftRoomConfig
        {
            DraftMode = DraftMode.Custom,
            CustomBaseDraftMode = NormalizeBaseDraftType(preset.Settings.BaseDraftType),
            FullRandom = preset.Settings.FullRandom ?? false,
            BlindDraft = preset.Settings.BlindDraft ?? false,
            HeroPoolSize = heroPoolSize,
            MaxPlayers = maxPlayers,
            PickOrder = preset.Settings.PickOrder ?? DraftPickOrder.AlternatingTeamsSnake,
            AutoPickBehavior = preset.Settings.AutoPickBehavior ?? DraftAutoPickBehavior.RandomValidPick,
            TeamBalance = preset.Settings.TeamBalance ?? DraftTeamBalance.AllowUnevenTeams,
            PreparationSeconds = preparationSeconds,
            PickSeconds = pickSeconds,
            AllowDuplicateHeroes = preset.Settings.AllowDuplicateHeroes ?? false,
            AllowDuplicateAbilities = preset.Settings.AllowDuplicateAbilities ?? false,
            FlexibleUltimateSlots = preset.Settings.FlexibleUltimateSlots ?? false,
            AllowHostOverridePicks = preset.Settings.AllowHostOverride ?? false,
            DisableChat = preset.Settings.DisableChat ?? false,
            AllowEmptySlotsAsBots = preset.Settings.AllowEmptySlotsAsBots ?? false,
            RequiredHeroCount = 1,
            RequiredAbilitySlots = 4,
            UltimatePicksPerPlayer = preset.Settings.FlexibleUltimateSlots == true ? 0 : 1
        };
        config.RegularAbilityPicksPerPlayer = config.FlexibleUltimateSlots
            ? config.RequiredAbilitySlots
            : Math.Max(0, config.RequiredAbilitySlots - config.UltimatePicksPerPlayer);
        ReplaceBanList(config.CustomBans, bans);

        var availableNonBannedHeroes = data.Heroes.Count(hero => !DraftPoolGenerator.IsHeroBanned(hero.Key, globalBans, config.CustomBans));
        if (availableNonBannedHeroes > 0 && config.HeroPoolSize > availableNonBannedHeroes)
        {
            config.HeroPoolSize = availableNonBannedHeroes;
            warnings.Add($"Hero pool size was clamped to {availableNonBannedHeroes} available non-banned heroes.");
        }

        if (config.MaxPlayers > config.HeroPoolSize)
        {
            config.MaxPlayers = config.HeroPoolSize;
            warnings.Add("Max players was clamped to the hero pool size.");
        }

        return new CustomDraftPresetImportResult(config, warnings);
    }

    private static DeadlockBanList FilterBans(CustomDraftPresetBans? source, ParsedDeadlockData data, List<string> warnings)
    {
        var bans = new DeadlockBanList();
        if (source is null)
        {
            return bans;
        }

        var foundMissing = false;
        var heroKeys = data.Heroes.Select(hero => hero.Key).ToHashSet(StringComparer.Ordinal);
        var abilityKeys = data.Abilities.Select(ability => ability.Key).ToHashSet(StringComparer.Ordinal);
        AddExisting(source.Heroes, heroKeys, bans.BannedHeroes, ref foundMissing);
        AddExisting(source.UnbannedHeroes, heroKeys, bans.UnbannedHeroes, ref foundMissing);
        AddExisting(source.Abilities, abilityKeys, bans.BannedAbilities, ref foundMissing);

        foreach (var key in CleanKeys(source.UnbannedAbilities))
        {
            var ability = data.Abilities.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (ability is null)
            {
                foundMissing = true;
                continue;
            }

            if (DraftPoolGenerator.IsAbilityAutoBanned(ability, data))
            {
                warnings.Add($"Skipped unban override for '{key}' because its source hero is unknown.");
                continue;
            }

            bans.UnbannedAbilities.Add(key);
        }

        if (foundMissing)
        {
            warnings.Add("Some preset entries were not found in current game data.");
        }

        return bans;
    }

    private static void AddExisting(IEnumerable<string>? keys, HashSet<string> validKeys, HashSet<string> target, ref bool foundMissing)
    {
        foreach (var key in CleanKeys(keys))
        {
            if (validKeys.Contains(key))
            {
                target.Add(key);
            }
            else
            {
                foundMissing = true;
            }
        }
    }

    private static IEnumerable<string> CleanKeys(IEnumerable<string>? keys) =>
        keys?.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()).Distinct(StringComparer.Ordinal) ?? [];

    private static List<string> Sorted(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value)).OrderBy(value => value, StringComparer.Ordinal).ToList();

    private static DraftMode NormalizeBaseDraftType(DraftMode? mode) =>
        mode is DraftMode.Classic or DraftMode.RandomHero ? mode.Value : DraftMode.FreePick;

    private static int ClampPositive(int? value, int fallback, int min, int max, string label, List<string> warnings)
    {
        var actual = value.GetValueOrDefault(fallback);
        if (actual < min)
        {
            warnings.Add($"{label} was raised to {min}.");
            return min;
        }

        if (actual > max)
        {
            warnings.Add($"{label} was clamped to {max}.");
            return max;
        }

        return actual;
    }

    private static int PositiveSetting(int? value, int fallback, string label, List<string> warnings)
    {
        if (value.GetValueOrDefault(fallback) > 0)
        {
            return value.GetValueOrDefault(fallback);
        }

        warnings.Add($"{label} was reset to the configured default.");
        return fallback;
    }

    private static int PositiveOrDefault(params int[] values)
    {
        foreach (var value in values)
        {
            if (value > 0)
            {
                return value;
            }
        }

        return 1;
    }

    private static void ReplaceBanList(DeadlockBanList target, DeadlockBanList source)
    {
        target.BannedHeroes.UnionWith(source.BannedHeroes);
        target.UnbannedHeroes.UnionWith(source.UnbannedHeroes);
        target.BannedAbilities.UnionWith(source.BannedAbilities);
        target.UnbannedAbilities.UnionWith(source.UnbannedAbilities);
    }
}

public sealed class CustomDraftPreset
{
    public int Version { get; set; }
    public string Mode { get; set; } = string.Empty;
    public CustomDraftPresetSettings? Settings { get; set; }
    public CustomDraftPresetBans? Bans { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CustomDraftPresetSettings
{
    public DraftMode? BaseDraftType { get; set; }
    public bool? FullRandom { get; set; }
    public bool? BlindDraft { get; set; }
    public int? HeroPoolSize { get; set; }
    public int? MaxPlayers { get; set; }
    public DraftPickOrder? PickOrder { get; set; }
    public DraftAutoPickBehavior? AutoPickBehavior { get; set; }
    public DraftTeamBalance? TeamBalance { get; set; }
    public int? PreparationTimeSeconds { get; set; }
    public int? PickTimerSeconds { get; set; }
    public bool? AllowDuplicateHeroes { get; set; }
    public bool? AllowDuplicateAbilities { get; set; }
    public bool? FlexibleUltimateSlots { get; set; }
    public bool? AllowHostOverride { get; set; }
    public bool? DisableChat { get; set; }
    public bool? AllowEmptySlotsAsBots { get; set; }
}

public sealed class CustomDraftPresetBans
{
    public List<string> Heroes { get; set; } = [];
    public List<string> UnbannedHeroes { get; set; } = [];
    public List<string> Abilities { get; set; } = [];
    public List<string> UnbannedAbilities { get; set; } = [];
}

public sealed record CustomDraftPresetImportResult(DraftRoomConfig Config, IReadOnlyList<string> Warnings);
