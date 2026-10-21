namespace AbilityDraft.Gameplay;

// Deadworks can initialize managed plugins after the first native map manifest.
// Never admit a match on that map: reload it once with the callbacks registered.
public sealed class MapResourceGate
{
    private bool observedPrecache, reloadRequested;
    public bool Ready { get; private set; }
    public void Precached() => observedPrecache = true;
    public bool StartMap(string map, Action<string> reload)
    {
        Ready = observedPrecache;
        observedPrecache = false;
        if (Ready) { reloadRequested = false; return true; }
        if (reloadRequested)
            throw new InvalidOperationException("The map resource callback did not run after reloading. Update Deadworks before admitting players.");
        if (string.IsNullOrWhiteSpace(map) || map.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '/')))
            throw new InvalidOperationException("Cannot reload a map with an unknown or invalid name.");
        reloadRequested = true;
        Console.WriteLine("[AbilityDraft] Initial map loaded before plugin precaching; reloading " + map + " before server readiness.");
        reload(map);
        return false;
    }

    // changelevel can keep the native server instance without another startup
    // callback. A fresh map command completes the startup/precache handshake.
    public static void ReloadInitialMap(string map) => DeadworksManaged.Api.Server.ExecuteCommand("map " + map);
}
