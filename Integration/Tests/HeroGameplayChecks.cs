using System.IO.Compression;
using System.Text;
using abilitydraft.Models;
using abilitydraft.Services;

static class HeroGameplayChecks
{
    public static void Run(ParsedDeadlockData data, DeadPackerService? packer = null)
    {
        CheckInkLocalisation();
        CheckReportedShuffleExport(data, packer);
        CheckAllHeroShuffleExports(data);
        var resources = DraftedHeroCompatibility.GameplayResources(data.AbilitiesDocument);
        Check(resources.Contains("particles/abilities/artist/artist_pullbrush.vpcf") &&
            resources.Contains("particles/abilities/airheart/airheart_jetpack_ambient.vpcf") &&
            resources.Contains("models/particle/blend/artist_solidblocker.vmdl"),
            "Gameplay precaching includes Living Ink's dependent drawing effects and Denny jetpack effects");
        var preset = CustomDraftPresetService.Export(new() { DraftMode = DraftMode.Custom, AbilityDraftShuffle = true });
        Check(CustomDraftPresetService.Import(preset, data, new(), new()).Config.AbilityDraftShuffle,
            "Custom preset import restores the Shuffle setting");
        foreach (var piece in DraftedHeroCompatibility.ChessPieces)
        {
            var room = Room(data);
            room.Players[0].Loadout.RegularAbilities[0] = piece;
            var files = new ModFileGenerator().Generate(room);
            var heroes = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/heroes.vdata").Content);
            var abilities = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/abilities.vdata").Content);
            var bindings = (Kv3Object)((Kv3Object)heroes.Root["hero_haze"])["m_mapBoundAbilities"];
            var primary = DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Primary");
            var stockPrimary = DeadlockFileParser.GetString((Kv3Object)((Kv3Object)data.HeroesDocument.Root["hero_haze"])["m_mapBoundAbilities"], "ESlot_Weapon_Primary");
            var pieceData = (Kv3Object)abilities.Root[piece];
            var dependencies = (Kv3Object)pieceData["m_mapDependentAbilities"];
            Check(DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Secondary") == DraftedHeroCompatibility.MovePiece &&
                primary == stockPrimary + DraftedHeroCompatibility.ChessWeaponSuffix &&
                DeadlockFileParser.GetString((Kv3Object)dependencies[DraftedHeroCompatibility.MovePiece], "m_eFlags") == "AssignAsAltFire",
                piece + " independently supplies Solomon controls to a foreign hero");
            Check(DeadlockFileParser.GetString((Kv3Object)abilities.Root[primary], "_class") == DeadlockFileParser.GetString((Kv3Object)abilities.Root[stockPrimary], "_class") &&
                Kv3Writer.Write(new() { Root = (Kv3Object)abilities.Root[primary] }).Contains("m_bCanZoom = false"),
                "The recipient keeps its primary weapon class while secondary fire and zoom yield to piece movement");
            Check(Kv3Writer.Write(new() { Root = (Kv3Object)abilities.Root[stockPrimary] }) == Kv3Writer.Write(new() { Root = (Kv3Object)data.AbilitiesDocument.Root[stockPrimary] }),
                "The original weapon remains unchanged for other players");
        }
        var denny = Room(data);
        denny.Players[0].Loadout.RegularAbilities[0] = "ability_deadpack_flamethrower";
        denny.Players[0].Loadout.RegularAbilities[2] = "ability_deadpack_rocketeer";
        var export = new ModFileGenerator().Generate(denny);
        var companion = export.Single(f => f.Path.EndsWith("AbilityDraft.Gameplay.dll"));
        Check(companion.BinaryContent is { Length: > 1000 } && export.Any(f => f.Path == "HOST-SETUP.txt"),
            "Browser Denny exports include the native host companion and installation instructions");
        using var zip = new ZipArchive(new MemoryStream(new ZipExportService().CreateZip(export)));
        using var dll = zip.GetEntry(companion.Path)!.Open(); using var bytes = new MemoryStream(); dll.CopyTo(bytes);
        Check(bytes.ToArray().SequenceEqual(companion.BinaryContent!), "The generated ZIP preserves the companion DLL as binary data");
        var heroMap = Kv3Parser.Parse(export.Single(f => f.Path == "scripts/heroes.vdata").Content);
        var final = (Kv3Object)((Kv3Object)heroMap.Root["hero_haze"])["m_mapBoundAbilities"];
        Check(DeadlockFileParser.GetString(final, "ESlot_Signature_3") == "ability_deadpack_rocketeer" &&
            DeadlockFileParser.GetString(final, "ESlot_Signature_1") == "ability_deadpack_flamethrower",
            "Denny's fixes preserve the drafted slots instead of forcing the jetpack into slot 2");
        CheckInkTeams(data, packer);
    }

    static void CheckReportedShuffleExport(ParsedDeadlockData data, DeadPackerService? packer)
    {
        var room = new DraftRoom { DeadlockData = data, Config = new() { DraftMode = DraftMode.Custom, AbilityDraftShuffle = true } };
        var rat = room.Players[0]; rat.PlayerId = "reported-host"; rat.HeroKey = "hero_ratking";
        var denny = room.Players[1]; denny.PlayerId = "reported-guest"; denny.HeroKey = "hero_deadpack";
        // These are the original picks, before the two-player shuffle in the report.
        rat.Loadout.RegularAbilities.AddRange(["ability_deadpack_rocketeer", "ability_unicorn_prismaticguard", "ability_ratking_ratarmor"]);
        rat.Loadout.Ultimate = "ability_ratking_standard_bearer";
        denny.Loadout.RegularAbilities.AddRange(["ability_werewolf_unloadgun", "ability_werewolf_netshot", "ability_warden_lock_down"]);
        denny.Loadout.Ultimate = "ability_deadpack_ultimate";
        AbilityDraftShuffle.Apply(room);
        var files = new ModFileGenerator().Generate(room);
        var heroes = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/heroes.vdata").Content);
        var bindings = (Kv3Object)((Kv3Object)heroes.Root["hero_deadpack"])["m_mapBoundAbilities"];
        Check(room.ShuffleAssignments.Count == 2 && DeadlockFileParser.GetString(bindings, "ESlot_Signature_1") == "ability_deadpack_rocketeer",
            "The reported Rat King/Denny Shuffle draft exports Chimera Mk1 in its assigned slot");
        using var zip = new ZipArchive(new MemoryStream(new ZipExportService().CreateZip(files)));
        Check(zip.GetEntry("host/managed/plugins/AbilityDraft.Gameplay.dll") is { Length: > 1000 },
            "The reported Shuffle download includes its required gameplay companion");
        if (packer is not null)
        {
            var packed = packer.TryCreateVpk("REPORTEDSHUFFLE", files);
            Check(packed.Success && packed.VpkBytes is { Length: > 1000 }, "Native compiler packs the exact reported Shuffle loadout: " + packed.LogPath);
        }
    }

    static void CheckAllHeroShuffleExports(ParsedDeadlockData data)
    {
        var heroes = data.Heroes.Where(h => !h.Disabled && !h.HeroLabs &&
            h.DraftableAbilityKeys().All(data.AbilitiesDocument.Root.ContainsKey)).ToArray();
        foreach (var batch in heroes.Chunk(12))
        {
            var room = new DraftRoom { DeadlockData = data, Config = new() { DraftMode = DraftMode.Custom, AbilityDraftShuffle = true } };
            for (var i = 0; i < batch.Length; i++)
            {
                var p = room.Players[i]; var hero = batch[i];
                p.PlayerId = "all-heroes-" + i; p.HeroKey = hero.Key;
                p.Loadout.RegularAbilities.AddRange([hero.Ability1, hero.Ability2, hero.Ability3]);
                p.Loadout.Ultimate = hero.Ultimate;
            }
            AbilityDraftShuffle.Apply(room);
            var files = new ModFileGenerator().Generate(room);
            var exported = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/heroes.vdata").Content);
            foreach (var p in room.Players.Take(batch.Length))
            {
                var bindings = (Kv3Object)((Kv3Object)exported.Root[p.HeroKey!])["m_mapBoundAbilities"];
                for (var slot = 1; slot <= 4; slot++)
                {
                    var original = slot == 4 ? p.Loadout.Ultimate! : p.Loadout.RegularAbilities[slot - 1];
                    var actual = DeadlockFileParser.GetString(bindings, "ESlot_Signature_" + slot);
                    if (actual != original && !(original == DraftedHeroCompatibility.LivingInk && actual.StartsWith(original + "_abilitydraft_", StringComparison.Ordinal)))
                        throw new Exception($"Incorrect shuffled export for {p.HeroKey}, slot {slot}: {actual}");
                }
            }
        }
        Check(true, $"Shuffle exports preserve all four assigned skills across every available hero ({heroes.Length} heroes)");
    }

    static void CheckInkTeams(ParsedDeadlockData data, DeadPackerService? packer)
    {
        var room = new DraftRoom { DeadlockData = data };
        var recipients = data.Heroes.Where(h => !h.Disabled && !h.HeroLabs &&
            h.DraftableAbilityKeys().All(data.AbilitiesDocument.Root.ContainsKey)).Take(12).ToArray();
        for (var i = 0; i < 12; i++)
        {
            var p = room.Players[i]; var h = recipients[i];
            p.PlayerId = "ink-" + i; p.HeroKey = h.Key;
            p.Loadout.RegularAbilities.AddRange(["ability_deadpack_chargeblast", "ability_deadpack_rocketeer", "ability_deadpack_flamethrower"]);
            p.Loadout.RegularAbilities[i % 3] = DraftedHeroCompatibility.LivingInk;
            if (i % 2 == 0) p.Loadout.RegularAbilities[(i + 1) % 3] = DraftedHeroCompatibility.ChessPieces[i % 3];
            p.Loadout.Ultimate = "ability_deadpack_ultimate";
        }
        var files = new ModFileGenerator().Generate(room);
        var heroes = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/heroes.vdata").Content);
        var abilities = Kv3Parser.Parse(files.Single(f => f.Path == "scripts/abilities.vdata").Content);
        foreach (var p in room.Players.Take(12))
        {
            var chess = DraftedHeroCompatibility.HasChessPieces(p.Loadout.PickedAbilityKeys());
            var bindings = (Kv3Object)((Kv3Object)heroes.Root[p.HeroKey!])["m_mapBoundAbilities"];
            var ink = Enumerable.Range(1, 3).Select(slot => DeadlockFileParser.GetString(bindings, "ESlot_Signature_" + slot))
                .Single(key => key.StartsWith(DraftedHeroCompatibility.LivingInk, StringComparison.Ordinal));
            var dependency = (Kv3Object)((Kv3Object)abilities.Root[ink])["m_mapDependentAbilities"];
            var primary = DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Primary");
            var inkSlot = p.Loadout.RegularAbilities.IndexOf(DraftedHeroCompatibility.LivingInk) + 1;
            var childKey = DraftedHeroCompatibility.DrawingForSlot(inkSlot);
            var child = (Kv3Object)abilities.Root[childKey];
            Check(DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Secondary") ==
                (chess ? DraftedHeroCompatibility.MovePiece : childKey) &&
                dependency.ContainsKey(childKey) != chess &&
                DeadlockFileParser.GetString(child, "m_eLeaderAbilitySlot") == "ESlot_Signature_" + inkSlot &&
                primary.EndsWith(chess ? DraftedHeroCompatibility.ChessWeaponSuffix : DraftedHeroCompatibility.InkWeaponSuffix, StringComparison.Ordinal),
                p.HeroKey + ": 6v6 Living Ink draws in its drafted slot, with chess movement taking priority");
        }
        var inkOnly = Room(data);
        inkOnly.Players[0].Loadout.RegularAbilities[2] = DraftedHeroCompatibility.LivingInk;
        var inkExport = new ModFileGenerator().Generate(inkOnly);
        Check(inkExport.Any(f => f.Path.EndsWith("AbilityDraft.Gameplay.dll")),
            "A Living Ink export includes the host fix even without Denny abilities");
        var tokens = new LocalisationParser().ParseTokens([inkExport.Single(f => f.Path == AbilityVariantLocalisation.ResourcePath + "citadel_heroes_english.txt").Content]);
        Check(Enumerable.Range(1, 4).Select(DraftedHeroCompatibility.InkForSlot).Append(DraftedHeroCompatibility.InkWithoutDrawing).All(key =>
            tokens[key] == tokens[DraftedHeroCompatibility.LivingInk] &&
            tokens[key + "/stack"] == tokens[DraftedHeroCompatibility.LivingInk + "/stack"]),
            "Website exports localize Living Ink's title and floating stack labels in every drafted slot and with chess pieces");
        if (packer is not null)
        {
            var packed = packer.TryCreateVpk("INKLOCALIZATION", inkExport);
            Check(packed.Success, "Native compiler packs a Living Ink web export with localization: " + packed.LogPath);
            Check(packed.VpkBytes is { } bytes && bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(DraftedHeroCompatibility.InkForSlot(3) + "/stack")) >= 0,
                "The compiled website VPK retains Living Ink's localized floating stack token");
        }
    }
    static void CheckInkLocalisation()
    {
        const string original = """
            "lang"
            {
                "Language" "russian"
                "Tokens"
                {
                    "ability_artist_homingbrush" "Живые чернила"
                    "ability_artist_homingbrush/stack" "Чернила"
                    "ability_artist_homingbrush_t1_desc" "<span class=\"highlight\">{s:AbilityCooldown}</span>"
                    "ability_artist_pullbrush" "Рисовать"
                    "unrelated_ability" "Unchanged"
                }
            }
            """;
        var keys = new[] { DraftedHeroCompatibility.InkForSlot(1), DraftedHeroCompatibility.InkWithoutDrawing,
            DraftedHeroCompatibility.DrawingForSlot(1), DraftedHeroCompatibility.InkForSlot(4) + "_abilitydraft_ultimate_1" };
        var localized = AbilityVariantLocalisation.AddAliases(original, keys);
        var parser = new LocalisationParser();
        var before = parser.ParseTokens([original]); var after = parser.ParseTokens([localized]);
        Check(before.All(p => after.TryGetValue(p.Key, out var value) && value == p.Value),
            "Localization aliases preserve every stock token, escaped markup and translated text");
        Check(after[keys[0]] == "Живые чернила" && after[keys[0] + "/stack"] == "Чернила" &&
            after[keys[2]] == "Рисовать" && after[keys[3] + "_t1_desc"] == before[DraftedHeroCompatibility.LivingInk + "_t1_desc"],
            "Living Ink variants reuse translated names, modifier labels and upgrade descriptions, including flexible ultimate slots");
        Check(AbilityVariantLocalisation.AddAliases(localized, keys) == localized,
            "Rebuilding localization aliases does not duplicate tokens");
    }
    static DraftRoom Room(ParsedDeadlockData data)
    {
        var room = new DraftRoom { DeadlockData = data };
        var p = room.Players[0]; p.PlayerId = "gameplay-test"; p.HeroKey = "hero_haze";
        var source = data.Heroes.First(h => h.Key == "hero_haze");
        p.Loadout.RegularAbilities.AddRange([source.Ability1, source.Ability2, source.Ability3]); p.Loadout.Ultimate = source.Ultimate;
        return room;
    }
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
}
