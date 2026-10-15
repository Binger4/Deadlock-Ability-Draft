using AbilityDraft.Contracts;
namespace abilitydraft.Services.InGame;

public static class GameNodeSelection
{
    public static string? Choose(IEnumerable<GameNodeView> nodes, IReadOnlyDictionary<string, double?> pings, string? selected, bool manual)
    {
        var available = nodes.Where(n => n.Available).ToArray();
        if (manual && available.Any(n => n.Id == selected)) return selected;
        return available.OrderBy(n => pings.TryGetValue(n.Id, out var ms) && ms is >= 0 && double.IsFinite(ms.Value) ? ms.Value : double.MaxValue)
            .ThenBy(n => n.Id == selected ? 0 : 1).FirstOrDefault()?.Id;
    }
    public static string PingClass(double? ms) => ms is null ? "ping-unknown" : ms < 70 ? "ping-good" : ms <= 150 ? "ping-medium" : ms <= 500 ? "ping-bad" : "ping-terrible";
}
