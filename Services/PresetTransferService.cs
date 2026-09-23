using System.Security.Cryptography;
using System.Text;

namespace abilitydraft.Services;

// A browser may transfer one preset, never control the room or identify another player.
public sealed class PresetTransferService(TimeProvider clock)
{
    public const int MaxBytes = 128 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, Transfer> transfers = new();
    private sealed record Transfer(DateTimeOffset Expires, string? Export, string? Import = null);

    public string Create(string? export = null)
    {
        if (export is not null) CheckSize(export);
        lock (gate)
        {
            foreach (var key in transfers.Where(p => p.Value.Expires <= clock.GetUtcNow()).Select(p => p.Key).ToArray()) transfers.Remove(key);
            if (transfers.Count >= 2048) throw new InvalidOperationException("Too many open preset transfers. Try again shortly.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            transfers[token] = new(clock.GetUtcNow().AddMinutes(10), export);
            return token;
        }
    }

    public bool IsDownload(string token) { lock (gate) return Find(token).Export is not null; }
    public string Download(string token) { lock (gate) return Find(token).Export ?? throw new InvalidOperationException("This link is for loading a preset."); }
    public void Upload(string token, string json)
    {
        CheckSize(json);
        lock (gate)
        {
            var transfer = Find(token);
            if (transfer.Export is not null || transfer.Import is not null) throw new InvalidOperationException("This preset transfer has already been used.");
            // Syntax/schema validation remains in CustomDraftPresetService when the game consumes it.
            transfers[token] = transfer with { Import = json };
        }
    }
    public string? TakeUpload(string token)
    {
        lock (gate)
        {
            var transfer = Find(token);
            if (transfer.Import is null) return null;
            transfers.Remove(token);
            return transfer.Import;
        }
    }
    public void Remove(string? token) { if (token is not null) lock (gate) transfers.Remove(token); }
    private Transfer Find(string token) => transfers.TryGetValue(token, out var transfer) && transfer.Expires > clock.GetUtcNow()
        ? transfer : throw new InvalidOperationException("Preset link expired. Click Generate preset or Load preset in the game again.");
    private static void CheckSize(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidOperationException("Preset files must be smaller than 128 KB.");
    }
}
