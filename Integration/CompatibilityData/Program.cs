using abilitydraft.Services;
using System.Text.Json;

if (args is ["--verify-abilities", var expectedPath, var compiledPath])
{
    var expected = Kv3Parser.Parse(File.ReadAllText(expectedPath));
    GameCatalogValidation.VerifyAbilities(expected, Kv3Parser.Parse(File.ReadAllText(compiledPath)));
    Console.WriteLine($"Verified every compiled ability definition and native class ({expected.Root.Pairs.Count(p => p.Value is Kv3Object)} definitions).");
    return;
}

if (args is ["--localization", var gameRoot, var contentDirectory, var templatesDirectory])
{
    var stock = Path.Combine(gameRoot, "game/citadel", AbilityVariantLocalisation.ResourcePath);
    var prepared = Kv3Parser.Parse(File.ReadAllText(Path.Combine(contentDirectory, "scripts/abilities.vdata")));
    var count = 0;
    foreach (var path in Directory.EnumerateFiles(stock, "citadel_heroes_*.txt"))
    {
        var original = File.ReadAllText(path);
        var localized = AbilityVariantLocalisation.AddAliases(original, prepared.Root.Keys);
        if (localized == original) continue;
        foreach (var (root, text) in new[] { (contentDirectory, localized), (templatesDirectory, original) })
        {
            var target = Path.Combine(root, AbilityVariantLocalisation.ResourcePath, Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, System.Text.Encoding.UTF8);
        }
        count++;
    }
    if (count == 0) throw new InvalidOperationException("No gameplay variant localization was found in the installed game.");
    Console.WriteLine($"Prepared Living Ink and Healing Needles name, modifier and upgrade aliases in {count} localization files.");
    return;
}

if (args is ["--resources", var source, var manifest])
{
    var paths = DraftedHeroCompatibility.GameplayResources(Kv3Parser.Parse(File.ReadAllText(source)));
    File.WriteAllText(manifest, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine($"Prepared {paths.Length} explicit gameplay resources, including dependent abilities.");
    return;
}

if (args.Length != 3) throw new ArgumentException("Usage: <abilities.vdata> <heroes.vdata> <addon content directory>");
var abilities = Kv3Parser.Parse(File.ReadAllText(args[0]));
var heroes = Kv3Parser.Parse(File.ReadAllText(args[1]));
abilities.Root.Remove("_include");
DraftedHeroCompatibility.PrepareAbilities(abilities);
foreach (var hero in heroes.Root.Pairs.Select(p => p.Value).OfType<Kv3Object>())
    if (hero.TryGetValue("m_mapBoundAbilities", out var value) && value is Kv3Object bindings &&
        DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Primary") is { Length: > 0 } weapon && abilities.Root.ContainsKey(weapon))
    {
        DraftedHeroCompatibility.ChessWeapon(abilities, weapon);
        DraftedHeroCompatibility.AlternateFireWeapon(abilities, weapon, DraftedHeroCompatibility.InkWeaponSuffix);
        if (abilities.Root[weapon] is Kv3Object primary && primary.TryGetValue("m_mapWeaponInfos", out var infos) &&
            infos is Kv3Object weaponInfos && weaponInfos.ContainsKey("primary"))
            DraftedHeroCompatibility.HealingNeedlesForWeapon(abilities, weapon);
    }
var directory = Path.Combine(args[2], "scripts"); Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(directory, "abilities.vdata"), Kv3Writer.Write(abilities));
Console.WriteLine("Prepared Solomon controls, secondary-fire variants and recipient-weapon Healing Needles for the runtime addon.");
