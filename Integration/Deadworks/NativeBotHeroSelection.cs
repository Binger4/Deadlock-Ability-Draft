using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

// The normal bot roster excludes heroes whose AI difficulty is -1. Ability Draft
// already has an authoritative roster, so opt those bots into hero selection for
// this call only. This does not add AI support for arbitrary cross-hero abilities.
internal static class NativeBotHeroSelection
{
    private static readonly SchemaAccessor<int> Ally = new("CitadelHeroData_t"u8, "m_nAllyBotDifficulty"u8);
    private static readonly SchemaAccessor<int> Enemy = new("CitadelHeroData_t"u8, "m_nEnemyBotDifficulty"u8);

    public static void Select(CCitadelPlayerController player, Heroes hero, Action select)
    {
        if (player.PlayerSteamId != 0) { select(); return; }
        var data = hero.GetHeroData() ?? throw new InvalidOperationException("Missing bot hero data.");
        if (!data.IsValid) throw new InvalidOperationException("Invalid bot hero data.");
        var ally = data.AllyBotDifficulty;
        var enemy = data.EnemyBotDifficulty;
        try
        {
            Ally.Set(data.Pointer, Math.Max(1, ally));
            Enemy.Set(data.Pointer, Math.Max(1, enemy));
            select();
        }
        finally
        {
            Ally.Set(data.Pointer, ally);
            Enemy.Set(data.Pointer, enemy);
        }
    }
}
