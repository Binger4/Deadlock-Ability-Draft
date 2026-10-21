namespace AbilityDraft.Gameplay;

public static class HealingNeedlesWeapon
{
    public const string Ability = "ability_nurse_healing_needles";
    public const string Weapon = "citadel_weapon_nurse_healing_needles";
    public const string NursePrimary = "citadel_weapon_nurse_set";

    public static string Primary(string weapon)
    {
        foreach (var suffix in new[] { "_abilitydraft_chess", "_abilitydraft_ink" })
            if (weapon.EndsWith(suffix, StringComparison.Ordinal)) return weapon[..^suffix.Length];
        return weapon;
    }

    public static string AbilityFor(string weapon) => Primary(weapon) == NursePrimary ? Ability : Ability + "_abilitydraft_" + Primary(weapon);
    public static string WeaponFor(string weapon) => Primary(weapon) == NursePrimary ? Weapon : Weapon + "_abilitydraft_" + Primary(weapon);
    public static string OriginalKey(string ability) => ability.StartsWith(Ability + "_abilitydraft_", StringComparison.Ordinal) ? Ability : ability;
}
