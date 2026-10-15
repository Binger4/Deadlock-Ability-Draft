namespace abilitydraft.Services.InGame;

public sealed class InGamePresenceService(InGameRoomAdapter adapter, GameNodeCoordinator workers, ILogger<InGamePresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var (result, steam) in workers.DrainAbandonments())
                    try { adapter.Abandon(steam, result); } catch (InvalidOperationException) { }
                adapter.ExpireConnections(DateTime.UtcNow); adapter.ReconcileMatches(workers.States(), DateTime.UtcNow);
            }
            catch (Exception ex) { logger.LogError(ex, "In-game presence cleanup failed"); }
        }
    }
}
