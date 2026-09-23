namespace abilitydraft.Services.InGame;

public sealed class InGamePresenceService(InGameRoomAdapter adapter, MatchWorkerCoordinator workers, ILogger<InGamePresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { adapter.ExpireConnections(DateTime.UtcNow); adapter.ReconcileMatches(workers.States(), DateTime.UtcNow); }
            catch (Exception ex) { logger.LogError(ex, "In-game presence cleanup failed"); }
        }
    }
}
