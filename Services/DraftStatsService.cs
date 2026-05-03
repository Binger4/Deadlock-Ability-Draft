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

        var record = new CompletedDraftStatsRecord
        {
            HostName = HostName(room),
            DraftCode = room.Code,
            PlayerCount = DraftTurnService.ActiveSlots(room).Count(),
            CompletedUtc = DateTime.UtcNow,
            DraftMode = DraftModeLabel(room.Config.DraftMode),
            AllowEmptySlotsAsBots = room.Config.AllowEmptySlotsAsBots,
            Participants = StatsParticipants(room)
        };

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
            return ParseCompletedDrafts(File.ReadAllText(path, Encoding.UTF8));
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
        File.WriteAllText(path, FormatCompletedDrafts(records), Encoding.UTF8);
    }

    private string CompletedDraftsPath() =>
        Path.Combine(environment.ContentRootPath, "Data", "Stats", "completed-drafts.json");

    private static string HostName(DraftRoom room) =>
        room.Clients.FirstOrDefault(client => client.IsHost)?.DisplayName
        ?? room.Players.FirstOrDefault(player => player.IsHost)?.DisplayName
        ?? "Unknown";

    private static List<DraftStatsParticipantRecord> StatsParticipants(DraftRoom room) =>
        DraftTurnService.ActiveSlots(room)
            .Where(player => !player.IsBot)
            .OrderBy(player => player.Team)
            .ThenBy(player => player.TeamIndex())
            .Select(player => new DraftStatsParticipantRecord(player.NameOrFallback, StatsTeamCode(player)))
            .ToList();

    private static string DraftModeLabel(DraftMode mode) => mode switch
    {
        DraftMode.FreePick => "Free Pick",
        DraftMode.Classic => "Classic",
        DraftMode.RandomHero => "Random Hero",
        _ => "Unknown"
    };

    private static string TeamCode(DeadlockTeam team) => team switch
    {
        DeadlockTeam.HiddenKing => "HK",
        DeadlockTeam.Archmother => "AM",
        _ => "Unknown"
    };

    private static string StatsTeamCode(DraftPlayerSlot player) =>
        player.IsHost ? $"{TeamCode(player.Team)}*" : TeamCode(player.Team);

    private static List<CompletedDraftStatsRecord> ParseCompletedDrafts(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var records = new List<CompletedDraftStatsRecord>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            records.Add(new CompletedDraftStatsRecord
            {
                HostName = GetString(element, "hostName"),
                DraftCode = GetString(element, "draftCode"),
                PlayerCount = GetInt(element, "playerCount"),
                DraftMode = GetNullableString(element, "draftMode"),
                AllowEmptySlotsAsBots = GetNullableBool(element, "botsEnabled") ?? GetNullableBool(element, "allowEmptySlotsAsBots"),
                Participants = GetParticipants(element, "players") ?? GetParticipants(element, "participants"),
                CompletedUtc = GetDateTime(element, "completedUtc")
            });
        }

        return records;
    }

    private static string FormatCompletedDrafts(IReadOnlyList<CompletedDraftStatsRecord> records)
    {
        if (records.Count == 0)
        {
            return "[]";
        }

        var builder = new StringBuilder();
        builder.AppendLine("[");
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var lines = new List<string>
            {
                $"    \"hostName\": {Json(record.HostName)}",
                $"    \"draftCode\": {Json(record.DraftCode)}",
                $"    \"playerCount\": {record.PlayerCount}"
            };

            if (!string.IsNullOrWhiteSpace(record.DraftMode))
            {
                lines.Add($"    \"draftMode\": {Json(record.DraftMode)}");
            }

            if (record.AllowEmptySlotsAsBots is not null)
            {
                lines.Add($"    \"botsEnabled\": {Json(record.AllowEmptySlotsAsBots.Value)}");
            }

            if (record.Participants is not null)
            {
                lines.Add($"    \"players\": {Json(record.Participants)}");
            }

            lines.Add($"    \"completedUtc\": {Json(record.CompletedUtc)}");

            builder.AppendLine("  {");
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                builder.Append(lines[lineIndex]);
                builder.AppendLine(lineIndex == lines.Count - 1 ? string.Empty : ",");
            }

            builder.Append("  }");
            builder.AppendLine(i == records.Count - 1 ? string.Empty : ",");
        }

        builder.AppendLine("]");
        return builder.ToString();
    }

    private static List<DraftStatsParticipantRecord>? GetParticipants(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var participants = new List<DraftStatsParticipantRecord>();
        foreach (var participant in property.EnumerateArray())
        {
            if (participant.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            participants.Add(new DraftStatsParticipantRecord(
                GetString(participant, "name"),
                GetString(participant, "team")));
        }

        return participants;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetNullableString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool? GetNullableBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static DateTime GetDateTime(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetDateTime(out var value)
            ? value
            : default;

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
