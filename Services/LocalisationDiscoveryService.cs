namespace abilitydraft.Services;

public sealed class LocalisationDiscoveryService
{
    public bool IsHeroNameFile(string fileName) =>
        fileName.Contains("citadel_gc_hero_names", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("hero_names", StringComparison.OrdinalIgnoreCase);

    public bool IsAbilityNameFile(string fileName) =>
        fileName.Contains("citadel_heroes", StringComparison.OrdinalIgnoreCase);
}
