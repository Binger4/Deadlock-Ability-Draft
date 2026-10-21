using DeadworksManaged.Api;

namespace AbilityDraft.Gameplay;

// Companion for browser-generated VPKs. The full Ability Draft server embeds
// this bridge itself; do not install the companion alongside that server plugin.
public sealed class GameplayPlugin : DeadworksPluginBase
{
    public override string Name => "Ability Draft gameplay compatibility";
    private NativeDennyCompatibility? denny;
    private readonly MapResourceGate resources = new();
    public override void OnLoad(bool isReload) => denny = NativeDennyCompatibility.Install();
    public override void OnUnload() => denny?.Dispose();
    public override void OnPrecacheResources() { CompatibilityResources.Precache(); resources.Precached(); }
    public override void OnStartupServer() => resources.StartMap(Server.MapName, MapResourceGate.ReloadInitialMap);
    public override void OnAbilityAttempt(AbilityAttemptEvent args) => DraftedAlternateFire.Guard(args);
}
