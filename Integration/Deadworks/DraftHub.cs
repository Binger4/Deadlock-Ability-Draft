using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft.Deadworks;

public sealed partial class AbilityDraftPlugin
{
    private void ExitToMenu(Session session)
    {
        session.ExitRequested = true;
        if (Players.FromSlot(session.Slot) is { } player)
            UI.Panel(PanelId).Set(player.Recipients, "disconnect", "1");
    }

    private void TickHostLease()
    {
        // Prevent the native waiting deadline from disconnecting players in draft hubs.
        if (IsDraftHub && GameRules.IsValid && GameRules.GameStateEndTime != float.MaxValue)
            GameRules.SetGameStateEndTime(float.MaxValue);
        if (string.IsNullOrWhiteSpace(hostDirectory) || hostReporting || DateTime.UtcNow < nextHostReport) return;
        hostReporting = true;
        nextHostReport = DateTime.UtcNow.AddSeconds(3);
        var ready = GameRules.IsValid && GameRules.GameState == EGameState.WaitingForPlayersToJoin;
        _ = Task.Run(() =>
        {
            var ownerAlive = false;
            try
            {
                if (ready) File.WriteAllText(Path.Combine(hostDirectory, "status.json"), "{\"ready\":true}");
                ownerAlive = DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(hostDirectory, "owner.heartbeat")) < TimeSpan.FromSeconds(45);
            }
            catch (Exception ex) { Log("Draft server heartbeat failed: " + ex.GetType().Name); }
            completions.Enqueue(() =>
            {
                hostReporting = false;
                if (!ownerAlive) { Log("Website heartbeat expired; stopping lobby server"); Server.ExecuteCommand("quit"); }
            });
        });
    }
}
