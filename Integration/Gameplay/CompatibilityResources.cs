using System.Text.Json;
using DeadworksManaged.Api;

namespace AbilityDraft.Gameplay;

public static class CompatibilityResources
{
    private static readonly Lazy<string[]> Resources = new(() =>
    {
        using var stream = typeof(CompatibilityResources).Assembly.GetManifestResourceStream("AbilityDraft.Gameplay.compatibility-resources.json")
            ?? throw new InvalidOperationException("Missing Ability Draft gameplay resource manifest. Rebuild the gameplay helper.");
        return JsonSerializer.Deserialize<string[]>(stream) ?? throw new InvalidOperationException("Invalid gameplay resource manifest.");
    });
    public static IReadOnlyList<string> Paths => Resources.Value;
    public static void Precache()
    {
        // Hero precaching alone misses donor preview/projectile effects and
        // dependent abilities. Loading them during a cast can block the server on
        // CResourceSystem::BlockingLoadResourceByNameIntoJustInTimeManifest.
        foreach (var path in Paths) DeadworksManaged.Api.Precache.AddResource(path);
        Console.WriteLine($"[AbilityDraft] Explicit gameplay precache registered {Paths.Count} ability and dependent resources.");
    }
}
