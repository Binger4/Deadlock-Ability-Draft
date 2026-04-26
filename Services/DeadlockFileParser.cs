using System.Text.RegularExpressions;
using abilitydraft.Models;

namespace abilitydraft.Services;

public sealed partial class DeadlockFileParser(LocalisationDiscoveryService localisationDiscoveryService, LocalisationParser localisationParser)
{
    public ParsedDeadlockData Parse(UploadedDeadlockFiles files)
    {
        var heroesDocument = Kv3Parser.Parse(files.HeroesVData);
        var abilitiesDocument = Kv3Parser.Parse(files.AbilitiesVData);
        abilitiesDocument.Root.Remove("_include");

        var heroNames = localisationParser.ParseTokens(files.LocalisationFiles
            .Where(file => localisationDiscoveryService.IsHeroNameFile(file.Key))
            .Select(file => file.Value));
        var rawAbilityNames = localisationParser.ParseTokens(files.LocalisationFiles
            .Where(file => localisationDiscoveryService.IsAbilityNameFile(file.Key))
            .Select(file => file.Value));

        var heroes = ParseHeroes(heroesDocument, heroNames);
        var abilityUsers = BuildAbilityUsers(heroes);
        var abilityNames = BuildAbilityNames(heroes, abilitiesDocument, rawAbilityNames, abilityUsers);
        var abilities = ParseAbilities(abilitiesDocument, heroes, abilityNames);
        var warnings = files.Warnings.ToList();
        ApplyIcons(files.IconFiles, heroes, abilities);

        if (heroes.Count < 12)
        {
            warnings.Add("Fewer than 12 usable heroes were parsed. A full 12-player draft cannot start from these files.");
        }

        if (rawAbilityNames.Count == 0)
        {
            warnings.Add("Ability localisation was unavailable. Internal ability IDs are used as display names.");
        }

        return new ParsedDeadlockData
        {
            HeroesDocument = heroesDocument,
            AbilitiesDocument = abilitiesDocument,
            Heroes = heroes,
            Abilities = abilities,
            HeroNames = heroNames,
            AbilityNames = abilityNames,
            Warnings = warnings
        };
    }

    private static List<HeroDefinition> ParseHeroes(Kv3Document document, IReadOnlyDictionary<string, string> heroNames)
    {
        var heroes = new List<HeroDefinition>();
        foreach (var (key, value) in document.Root.Pairs)
        {
            if (key == "generic_data_type" || value is not Kv3Object heroObject)
            {
                continue;
            }

            if (!TryGetObject(heroObject, "m_mapBoundAbilities", out var boundAbilities))
            {
                continue;
            }

            var weapon = GetString(boundAbilities, "ESlot_Weapon_Primary");
            var ability1 = GetString(boundAbilities, "ESlot_Signature_1");
            var ability2 = GetString(boundAbilities, "ESlot_Signature_2");
            var ability3 = GetString(boundAbilities, "ESlot_Signature_3");
            var ultimate = GetString(boundAbilities, "ESlot_Signature_4");

            if (new[] { weapon, ability1, ability2, ability3, ultimate }.Any(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            heroes.Add(new HeroDefinition
            {
                Id = (int)GetLong(heroObject, "m_HeroID"),
                Key = key,
                DisplayName = heroNames.GetValueOrDefault($"{key}:n", heroNames.GetValueOrDefault(key, PrettifyKey(key))),
                Disabled = GetBool(heroObject, "m_bDisabled"),
                HeroLabs = GetBool(heroObject, "m_bInDevelopment"),
                WeaponAbility = weapon,
                Ability1 = ability1,
                Ability2 = ability2,
                Ability3 = ability3,
                Ultimate = ultimate
            });
        }

        return heroes.OrderBy(hero => hero.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, List<string>> BuildAbilityUsers(IEnumerable<HeroDefinition> heroes)
    {
        var users = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var hero in heroes)
        {
            AddUser(hero.WeaponAbility, $"{hero.DisplayName} Weapon");
            AddUser(hero.Ability1, $"{hero.DisplayName} 1");
            AddUser(hero.Ability2, $"{hero.DisplayName} 2");
            AddUser(hero.Ability3, $"{hero.DisplayName} 3");
            AddUser(hero.Ultimate, $"{hero.DisplayName} Ult");
        }

        return users;

        void AddUser(string abilityKey, string label)
        {
            if (!users.TryGetValue(abilityKey, out var labels))
            {
                labels = [];
                users[abilityKey] = labels;
            }

            labels.Add(label);
        }
    }

    private static Dictionary<string, string> BuildAbilityNames(
        IReadOnlyCollection<HeroDefinition> heroes,
        Kv3Document abilitiesDocument,
        IReadOnlyDictionary<string, string> rawAbilityNames,
        IReadOnlyDictionary<string, List<string>> abilityUsers)
    {
        var abilityNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var hero in heroes)
        {
            abilityNames[hero.WeaponAbility] = $"{hero.DisplayName} Weapon";
            foreach (var ability in hero.DraftableAbilityKeys())
            {
                if (abilityNames.ContainsKey(ability))
                {
                    continue;
                }

                var baseName = rawAbilityNames.GetValueOrDefault(ability, PrettifyKey(ability));
                var users = abilityUsers.GetValueOrDefault(ability, []);
                abilityNames[ability] = users.Count == 0 ? baseName : $"{baseName} ({string.Join(", ", users)})";
            }
        }

        foreach (var key in abilitiesDocument.Root.Keys)
        {
            if (key == "generic_data_type" || abilityNames.ContainsKey(key) || key.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var localized = rawAbilityNames.GetValueOrDefault(key, PrettifyKey(key));
            if (string.Equals(localized, "Melee", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            abilityNames[key] = $"{localized} (Unknown source)";
        }

        return abilityNames;
    }

    private static List<AbilityDefinition> ParseAbilities(
        Kv3Document abilitiesDocument,
        IReadOnlyCollection<HeroDefinition> heroes,
        IReadOnlyDictionary<string, string> abilityNames)
    {
        var sourceMap = new Dictionary<string, (HeroDefinition Hero, string Slot, DraftPickKind PickKind)>(StringComparer.Ordinal);
        foreach (var hero in heroes)
        {
            sourceMap.TryAdd(hero.WeaponAbility, (hero, "Weapon", DraftPickKind.Weapon));
            sourceMap.TryAdd(hero.Ability1, (hero, "1", DraftPickKind.RegularAbility));
            sourceMap.TryAdd(hero.Ability2, (hero, "2", DraftPickKind.RegularAbility));
            sourceMap.TryAdd(hero.Ability3, (hero, "3", DraftPickKind.RegularAbility));
            sourceMap.TryAdd(hero.Ultimate, (hero, "Ult", DraftPickKind.UltimateAbility));
        }

        var abilities = new List<AbilityDefinition>();
        foreach (var (key, value) in abilitiesDocument.Root.Pairs)
        {
            if (key == "generic_data_type" || value is not Kv3Object abilityObject || key.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sourceMap.TryGetValue(key, out var source);
            var warnings = DetectDependencyWarnings(key, abilityObject, source.Hero?.Key);
            abilities.Add(new AbilityDefinition
            {
                Key = key,
                DisplayName = abilityNames.GetValueOrDefault(key, PrettifyKey(key)),
                SourceHeroKey = source.Hero?.Key ?? string.Empty,
                SourceHeroName = source.Hero?.DisplayName ?? "Unknown",
                SourceSlot = source.Slot ?? "Unknown",
                AbilityType = GetString(abilityObject, "m_eAbilityType", "Unknown"),
                PickKind = source.PickKind == default ? InferPickKind(abilityObject) : source.PickKind,
                Warnings = warnings
            });
        }

        return abilities.OrderBy(ability => ability.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ApplyIcons(
        IReadOnlyDictionary<string, string> iconFiles,
        IEnumerable<HeroDefinition> heroes,
        IEnumerable<AbilityDefinition> abilities)
    {
        if (iconFiles.Count == 0)
        {
            return;
        }

        var normalizedIcons = iconFiles
            .GroupBy(file => NormalizeIconKey(file.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        foreach (var hero in heroes)
        {
            hero.IconDataUrl = FindIcon(normalizedIcons, hero.Key, hero.DisplayName);
        }

        foreach (var ability in abilities)
        {
            ability.IconDataUrl = FindIcon(normalizedIcons, ability.Key, ability.DisplayName);
        }
    }

    private static string? FindIcon(IReadOnlyDictionary<string, string> icons, params string[] names)
    {
        foreach (var name in names)
        {
            if (icons.TryGetValue(NormalizeIconKey(name), out var icon))
            {
                return icon;
            }
        }

        return null;
    }

    private static DraftPickKind InferPickKind(Kv3Object abilityObject)
    {
        var abilityType = GetString(abilityObject, "m_eAbilityType");
        if (abilityType.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
        {
            return DraftPickKind.UltimateAbility;
        }

        if (abilityType.Contains("Weapon", StringComparison.OrdinalIgnoreCase))
        {
            return DraftPickKind.Weapon;
        }

        return DraftPickKind.RegularAbility;
    }

    private static List<string> DetectDependencyWarnings(string abilityKey, Kv3Object abilityObject, string? sourceHeroKey)
    {
        var warnings = new List<string>();
        var flattened = Flatten(abilityObject).ToList();

        if (flattened.Any(value => value.Contains("modifier", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Uses modifier references; verify referenced modifiers are included and valid on the target hero.");
        }

        if (flattened.Any(value => value.Contains("weapon", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Mentions weapon-specific data. Test this ability on non-source heroes.");
        }

        if (flattened.Any(value => value.Contains("upgrade", StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add("Mentions upgrades; verify upgrade dependencies after compilation.");
        }

        if (!string.IsNullOrWhiteSpace(sourceHeroKey) &&
            flattened.Any(value => value.Contains(sourceHeroKey, StringComparison.OrdinalIgnoreCase) && !string.Equals(value, abilityKey, StringComparison.Ordinal)))
        {
            warnings.Add("Contains source-hero references. This may depend on hero-specific data.");
        }

        return warnings;
    }

    private static IEnumerable<string> Flatten(Kv3Value value)
    {
        switch (value)
        {
            case Kv3Scalar scalar when scalar.Value is not null:
                yield return scalar.Value.ToString() ?? string.Empty;
                break;
            case Kv3Array array:
                foreach (var item in array.Items.SelectMany(Flatten))
                {
                    yield return item;
                }

                break;
            case Kv3Object obj:
                foreach (var (key, child) in obj.Pairs)
                {
                    yield return key;
                    foreach (var item in Flatten(child))
                    {
                        yield return item;
                    }
                }

                break;
            case Kv3TypedValue typed:
                yield return typed.TypeName;
                foreach (var item in Flatten(typed.InnerValue))
                {
                    yield return item;
                }

                break;
        }
    }

    private static bool TryGetObject(Kv3Object obj, string key, out Kv3Object child)
    {
        if (obj.TryGetValue(key, out var value) && value is Kv3Object childObject)
        {
            child = childObject;
            return true;
        }

        child = new Kv3Object();
        return false;
    }

    public static string GetString(Kv3Object obj, string key, string fallback = "")
    {
        return obj.TryGetValue(key, out var value) && value is Kv3Scalar scalar
            ? scalar.Value?.ToString() ?? fallback
            : fallback;
    }

    public static long GetLong(Kv3Object obj, string key)
    {
        return obj.TryGetValue(key, out var value) && value is Kv3Scalar scalar
            ? Convert.ToInt64(scalar.Value ?? 0)
            : 0;
    }

    public static bool GetBool(Kv3Object obj, string key)
    {
        return obj.TryGetValue(key, out var value) && value is Kv3Scalar scalar && Convert.ToBoolean(scalar.Value);
    }

    public static string PrettifyKey(string key)
    {
        var clean = key
            .Replace("citadel_ability_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("ability_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("hero_", "", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');

        return CultureRegex().Replace(clean, match => match.Value.ToUpperInvariant());
    }

    public static string NormalizeIconKey(string key)
    {
        var fileName = Path.GetFileNameWithoutExtension(key);
        return Regex.Replace(fileName, @"[^a-zA-Z0-9]+", "", RegexOptions.CultureInvariant).ToLowerInvariant();
    }

    [GeneratedRegex(@"\b[a-z]")]
    private static partial Regex CultureRegex();
}
