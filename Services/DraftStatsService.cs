using System.Text;
using System.Text.Json;
using abilitydraft.Models;
using Microsoft.Extensions.Options;

namespace abilitydraft.Services;

public sealed class DraftStatsService(
    IWebHostEnvironment environment,
    IOptions<DraftStatsOptions> options,
    ILogger<DraftStatsService> logger)
{
    private readonly object _lock = new();

    public bool SaveCompletedHistoryEnabled => options.Value.SaveCompletedDraftHistory != false;

    public IReadOnlyList<CompletedDraftStatsRecord> GetCompletedDrafts()
    {
        lock (_lock)
        {
            return LoadCompletedDrafts()
                .OrderByDescending(record => record.CompletedUtc)
                .ToList();
        }
    }

    public void RecordCompletedDraft(DraftRoom room)
    {
        if (!SaveCompletedHistoryEnabled)
        {
            return;
        }

        var record = new CompletedDraftStatsRecord(
            HostName(room),
            room.Code,
            DraftTurnService.ActiveSlots(room).Count(),
            DateTime.UtcNow);

        lock (_lock)
        {
            var records = LoadCompletedDrafts().ToList();
            records.Add(record);
            SaveCompletedDrafts(records);
        }
    }

    public bool ClearCompletedDrafts()
    {
        lock (_lock)
        {
            try
            {
                SaveCompletedDrafts([]);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clear completed draft statistics history.");
                return false;
            }
        }
    }

    private List<CompletedDraftStatsRecord> LoadCompletedDrafts()
    {
        var path = CompletedDraftsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            SaveCompletedDrafts([]);
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CompletedDraftStatsRecord>>(File.ReadAllText(path, Encoding.UTF8), JsonOptions()) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Completed draft statistics file is missing, corrupted, or unreadable. Returning empty history.");
            return [];
        }
    }

    private void SaveCompletedDrafts(IReadOnlyList<CompletedDraftStatsRecord> records)
    {
        var path = CompletedDraftsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOptions()), Encoding.UTF8);
    }

    private string CompletedDraftsPath() =>
        Path.Combine(environment.ContentRootPath, "Data", "Stats", "completed-drafts.json");

    private static string HostName(DraftRoom room) =>
        room.Clients.FirstOrDefault(client => client.IsHost)?.DisplayName
        ?? room.Players.FirstOrDefault(player => player.IsHost)?.DisplayName
        ?? "Unknown";

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
