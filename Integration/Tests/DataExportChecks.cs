using abilitydraft.Models;
using abilitydraft.Services;
using abilitydraft.Services.InGame;

static class DataExportChecks
{
    public static void Run(ParsedDeadlockData data, DeadPackerService? packer = null)
    {
        var originalSnapshot = Kv3Writer.Write(data.AbilitiesDocument);
        var document = Kv3Parser.Parse("""
            { /* schema strings must remain strings */
                properties = [ "0", "001", "false", "null", "1.25", "1e3", "tab\there" ]
                number = 12
                flag = false
                empty = null
                resource = resource_name:"123"
            }
            """);
        Check(((Kv3Array)document.Root["properties"]).Items.Cast<Kv3Scalar>().All(s => s.Value is string),
            "KV3 preserves quoted numbers, booleans, null and exponents as strings");
        Check(Same(document.Root, Kv3Parser.Parse(Kv3Writer.Write(document)).Root), "KV3 preserves scalar and resource types through export");
        Reject(() => Kv3Parser.Parse("{} garbage"), "KV3 rejects truncated/extra documents instead of silently exporting a partial file");
        Reject(() => Kv3Parser.Parse("{ /* unfinished"), "KV3 rejects incomplete comments");

        var room = new DraftRoom { DeadlockData = data };
        var heroes = data.Heroes.Where(h => !h.Disabled && !h.HeroLabs &&
            h.DraftableAbilityKeys().All(data.AbilitiesDocument.Root.ContainsKey)).Take(12).ToArray();
        Check(heroes.Length == 12, "Full 6v6 export has twelve valid hero definitions");
        for (var i = 0; i < heroes.Length; i++)
        {
            var player = room.Players[i];
            var donor = heroes[(i + 1) % heroes.Length];
            player.PlayerId = "player-" + i;
            player.HeroKey = heroes[i].Key;
            player.Loadout.RegularAbilities.AddRange([donor.Ability1, donor.Ability2, donor.Ability3]);
            player.Loadout.Ultimate = donor.Ultimate;
        }
        var generated = new ModFileGenerator().Generate(room);
        var exportedHeroes = Kv3Parser.Parse(generated.Single(f => f.Path == "scripts/heroes.vdata").Content);
        var exportedAbilities = Kv3Parser.Parse(generated.Single(f => f.Path == "scripts/abilities.vdata").Content);
        var expectedAbilities = data.AbilitiesDocument.Clone();
        DraftedHeroCompatibility.PrepareAbilities(expectedAbilities);
        var changed = expectedAbilities.Root.Pairs.FirstOrDefault(p => !exportedAbilities.Root.ContainsKey(p.Key) || !Same(p.Value, exportedAbilities.Root[p.Key]));
        Check(changed.Key is null, "6v6 export preserves every stock ability, melee/parry and pickup definition" + (changed.Key is null ? "" : ": " + changed.Key));
        foreach (var key in new[] { "citadel_ability_melee_parry", "ability_item_pickup_effects" })
            Check(data.AbilitiesDocument.Root.ContainsKey(key) && Same(data.AbilitiesDocument.Root[key], exportedAbilities.Root[key]),
                "Export preserves current game definition: " + key);
        var drafted = heroes.Select(h => h.Key).ToHashSet();
        Check(data.HeroesDocument.Root.Pairs.Where(p => !drafted.Contains(p.Key)).All(p => Same(p.Value, exportedHeroes.Root[p.Key])),
            "6v6 export preserves all undrafted heroes and shared hero defaults");
        Check(room.Players.Take(12).All(p =>
        {
            var original = (Kv3Object)data.HeroesDocument.Root[p.HeroKey!];
            var exported = (Kv3Object)exportedHeroes.Root[p.HeroKey!];
            var bindings = (Kv3Object)exported["m_mapBoundAbilities"];
            var nativeWeapon = DeadlockFileParser.GetString((Kv3Object)original["m_mapBoundAbilities"], "ESlot_Weapon_Primary");
            var first = p.Loadout.RegularAbilities[0] == DraftedHeroCompatibility.LivingInk ? DraftedHeroCompatibility.InkForSlot(1) : p.Loadout.RegularAbilities[0];
            return DeadlockFileParser.GetString(bindings, "ESlot_Signature_1") == first &&
                DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Primary") ==
                nativeWeapon + (DraftedHeroCompatibility.HasChessPieces(p.Loadout.PickedAbilityKeys()) ? DraftedHeroCompatibility.ChessWeaponSuffix :
                    p.Loadout.PickedAbilityKeys().Contains(DraftedHeroCompatibility.LivingInk) ? DraftedHeroCompatibility.InkWeaponSuffix : "");
        }), "All twelve loadouts retain their primary weapon, with Solomon control variants only where required");

        // Real ability namespaces can already contain the old alias names.
        var source = heroes[1].Ability1;
        var collisionKey = source + "_abilitydraft_duplicate_1";
        var abilities = data.AbilitiesDocument.Clone();
        abilities.Root.Set(collisionKey, new Kv3Object());
        room.DeadlockData = new ParsedDeadlockData { HeroesDocument = data.HeroesDocument, AbilitiesDocument = abilities };
        room.Config.AllowDuplicateAbilities = true;
        room.Players[0].Loadout.RegularAbilities.Clear();
        room.Players[0].Loadout.RegularAbilities.AddRange([source, source, source]);
        var duplicateFiles = new ModFileGenerator().Generate(room);
        var expectedDuplicates = abilities.Clone();
        DraftedHeroCompatibility.PrepareAbilities(expectedDuplicates);
        var duplicated = Kv3Parser.Parse(duplicateFiles.Single(f => f.Path == "scripts/abilities.vdata").Content);
        var duplicateHeroes = Kv3Parser.Parse(duplicateFiles.Single(f => f.Path == "scripts/heroes.vdata").Content);
        var duplicateBindings = (Kv3Object)((Kv3Object)duplicateHeroes.Root[heroes[0].Key])["m_mapBoundAbilities"];
        var slots = Enumerable.Range(1, 3).Select(i => DeadlockFileParser.GetString(duplicateBindings, "ESlot_Signature_" + i)).ToArray();
        Check(slots.Distinct().Count() == 3 && slots.All(k => Same(expectedDuplicates.Root[source], duplicated.Root[k])),
            "Repeated abilities use distinct complete definitions with their native class and nested modifiers");
        Check(expectedDuplicates.Root.Pairs.All(p => Same(p.Value, duplicated.Root[p.Key])), "Duplicate export cannot overwrite stock or previously generated definitions");
        Check(Kv3Writer.Write(data.AbilitiesDocument) == originalSnapshot, "Generating compatibility variants and duplicates leaves the shared source snapshot unchanged");
        if (packer is not null)
        {
            var packed = packer.TryCreateVpk("EXPORTREGRESSION", duplicateFiles);
            Check(packed.Success, "Native compiler packs a full 6v6 draft with repeated abilities: " + packed.LogPath);
        }
        room.Players[0].Loadout.RegularAbilities[0] = "missing_ability";
        Reject(() => new ModFileGenerator().Generate(room), "Export rejects missing abilities before invoking the compiler");
        Reject(() => InGameRoomAdapter.ValidateRuntimeConfig(new DraftRoomConfig { DraftMode = DraftMode.Custom, FlexibleUltimateSlots = true }),
            "In-game configuration rejects unsupported ultimate conversions before drafting");
        Console.WriteLine("PASS data export checks");
    }

    static bool Same(Kv3Value a, Kv3Value b) => (a, b) switch
    {
        (Kv3Scalar x, Kv3Scalar y) => Equals(x.Value, y.Value),
        (Kv3TypedValue x, Kv3TypedValue y) => x.TypeName == y.TypeName && Same(x.InnerValue, y.InnerValue),
        (Kv3Array x, Kv3Array y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(p => Same(p.First, p.Second)),
        (Kv3Object x, Kv3Object y) => x.Keys.SequenceEqual(y.Keys) && x.Pairs.All(p => Same(p.Value, y[p.Key])),
        _ => false
    };
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or FormatException) { Check(true, name); return; }
        throw new Exception("FAIL " + name);
    }
}
