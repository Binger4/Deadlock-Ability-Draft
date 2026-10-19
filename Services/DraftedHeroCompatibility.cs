namespace abilitydraft.Services;

public static class DraftedHeroCompatibility
{
    public const string MovePiece = "ability_chessmaster_movechesspiece";
    public const string ChessWeaponSuffix = "_abilitydraft_chess";
    public const string InkWeaponSuffix = "_abilitydraft_ink";
    public const string LivingInk = "ability_artist_homingbrush";
    public const string DrawInk = "ability_artist_pullbrush";
    public const string InkWithoutDrawing = LivingInk + "_abilitydraft_chess";
    public static readonly string[] ChessPieces = ["ability_chessmaster_throwbishop", "ability_chessmaster_knightbuff", "ability_chessmaster_queen"];

    public static string[] GameplayResources(Kv3Document abilities)
    {
        var resources = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Kv3Value value)
        {
            if (value is Kv3TypedValue typed)
            {
                if (typed.TypeName == "resource_name" && typed.InnerValue is Kv3Scalar { Value: string path } && path.Length > 0)
                    resources.Add(path);
                Walk(typed.InnerValue);
            }
            else if (value is Kv3Object obj)
                foreach (var pair in obj.Pairs) Walk(pair.Value);
            else if (value is Kv3Array array)
                foreach (var item in array.Items) Walk(item);
        }
        void Visit(string key)
        {
            if (!visited.Add(key) || !abilities.Root.TryGetValue(key, out var value) || value is not Kv3Object ability) return;
            Walk(ability);
            if (ability.TryGetValue("m_mapDependentAbilities", out var deps) && deps is Kv3Object dependencies)
                foreach (var pair in dependencies.Pairs) Visit(pair.Key);
            if (ability.TryGetValue("_multibase", out var bases) && bases is Kv3Array parents)
                foreach (var parent in parents.Items.OfType<Kv3Scalar>())
                    if (parent.Value is string name) Visit(name);
        }
        foreach (var key in ChessPieces.Concat(new[] { MovePiece, "ability_chessmaster_forkpiece", LivingInk, DrawInk,
            "ability_deadpack_chargeblast", "ability_deadpack_rocketeer", "ability_deadpack_flamethrower", "ability_deadpack_ultimate",
            "ability_deadpack_primary_weapon_charge", "ability_deadpack_zip_line" })) Visit(key);
        return resources.ToArray();
    }

    public static void PrepareAbilities(Kv3Document abilities)
    {
        foreach (var key in ChessPieces)
        {
            if (!abilities.Root.TryGetValue(key, out var value) || value is not Kv3Object ability) continue;
            var dependencies = ability.TryGetValue("m_mapDependentAbilities", out var deps) && deps is Kv3Object map ? map : new Kv3Object();
            dependencies.Set(MovePiece, new Kv3Object { ["m_eFlags"] = new Kv3Scalar("AssignAsAltFire") });
            ability.Set("m_mapDependentAbilities", dependencies);
        }
        // A separate definition keeps another player's Living Ink drawable.
        // Removing the dependency entirely also prevents upgrades/recreation from
        // assigning PullBrush over this owner's chess controls.
        if (abilities.Root.TryGetValue(LivingInk, out var ink) && ink is Kv3Object source)
        {
            var clone = (Kv3Object)source.Clone();
            if (clone.TryGetValue("m_mapDependentAbilities", out var deps) && deps is Kv3Object dependencies)
                dependencies.Remove(DrawInk);
            abilities.Root.Set(InkWithoutDrawing, clone);
            if (abilities.Root.TryGetValue(DrawInk, out var drawValue) && drawValue is Kv3Object draw)
                for (var slot = 1; slot <= 4; slot++)
                {
                    var childKey = DrawingForSlot(slot);
                    var child = (Kv3Object)draw.Clone();
                    child.Set("m_eLeaderAbilitySlot", new Kv3Scalar("ESlot_Signature_" + slot));
                    abilities.Root.Set(childKey, child);
                    var parent = (Kv3Object)source.Clone();
                    var parentDependencies = (Kv3Object)parent["m_mapDependentAbilities"];
                    var link = parentDependencies[DrawInk].Clone();
                    parentDependencies.Remove(DrawInk); parentDependencies.Set(childKey, link);
                    abilities.Root.Set(InkForSlot(slot), parent);
                }
        }
    }

    public static string InkForSlot(int slot) => LivingInk + "_abilitydraft_slot_" + slot;
    public static string DrawingForSlot(int slot) => DrawInk + "_abilitydraft_slot_" + slot;

    public static bool HasChessPieces(IEnumerable<string> keys) => keys.Any(key => ChessPieces.Any(piece =>
        key == piece || key.StartsWith(piece + "_abilitydraft_", StringComparison.Ordinal)));

    public static string ChessWeapon(Kv3Document abilities, string weaponKey)
        => AlternateFireWeapon(abilities, weaponKey, ChessWeaponSuffix);

    public static string AlternateFireWeapon(Kv3Document abilities, string weaponKey, string suffix)
    {
        var key = weaponKey + suffix;
        if (abilities.Root.ContainsKey(key)) return key;
        if (!abilities.Root.TryGetValue(weaponKey, out var source) || source is not Kv3Object weapon)
            throw new InvalidOperationException("Missing primary weapon for Solomon piece controls: " + weaponKey);
        var clone = (Kv3Object)weapon.Clone();
        DisableSecondaryFire(clone);
        abilities.Root.Set(key, clone);
        return key;
    }

    private static void DisableSecondaryFire(Kv3Value value)
    {
        if (value is Kv3Object obj)
        {
            if (obj.TryGetValue("m_mapWeaponInfos", out var infos) && infos is Kv3Object weapons)
                foreach (var info in weapons.Pairs.Select(p => p.Value).OfType<Kv3Object>()) info.Set("m_bCanZoom", new Kv3Scalar(false));
            if (obj.ContainsKey("m_bCanZoom")) obj.Set("m_bCanZoom", new Kv3Scalar(false));
            if (obj.TryGetValue("m_mapDependentAbilities", out var deps) && deps is Kv3Object map)
                foreach (var pair in map.Pairs.ToArray())
                    if (pair.Value is Kv3Object dependency && DeadlockFileParser.GetString(dependency, "m_eFlags").Contains("AssignAsAltFire", StringComparison.Ordinal))
                        map.Remove(pair.Key);
            foreach (var pair in obj.Pairs.ToArray()) DisableSecondaryFire(pair.Value);
        }
        else if (value is Kv3Array array)
            foreach (var item in array.Items) DisableSecondaryFire(item);
        else if (value is Kv3TypedValue typed) DisableSecondaryFire(typed.InnerValue);
    }

    public static void BindAlternateFire(Kv3Object bindings, IReadOnlyList<string> keys, Kv3Document abilities)
    {
        var chess = HasChessPieces(keys);
        var ink = keys.Any(key => key == LivingInk || key.StartsWith(LivingInk + "_abilitydraft_", StringComparison.Ordinal));
        if (!chess && !ink) return;
        var primary = DeadlockFileParser.GetString(bindings, "ESlot_Weapon_Primary");
        bindings.Set("ESlot_Weapon_Primary", new Kv3Scalar(AlternateFireWeapon(abilities, primary, chess ? ChessWeaponSuffix : InkWeaponSuffix)));
        var inkSlot = keys.ToList().FindIndex(key => key == LivingInk || key.StartsWith(LivingInk + "_abilitydraft_", StringComparison.Ordinal)) + 1;
        bindings.Set("ESlot_Weapon_Secondary", new Kv3Scalar(chess ? MovePiece : DrawingForSlot(inkSlot)));
        // Keep the stock mover's native ownership checks and chess ability map.
    }
}
