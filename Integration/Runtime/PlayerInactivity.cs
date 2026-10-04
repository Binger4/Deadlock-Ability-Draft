namespace AbilityDraft.Runtime;

// Uses the unpaused match clock, never position/velocity or network packet count.
public sealed class PlayerInactivity
{
    public const double KickAfterSeconds = 300;
    private readonly Dictionary<int, double> lastActivity = new();
    public void Observe(int slot, double now, bool active)
    {
        if (!lastActivity.ContainsKey(slot) || active) lastActivity[slot] = now;
    }
    public int[] Expired(double now) => lastActivity.Where(p => now - p.Value >= KickAfterSeconds).Select(p => p.Key).ToArray();
    public void Remove(int slot) => lastActivity.Remove(slot);
    public void Clear() => lastActivity.Clear();
}

public sealed class EmptyMatchLifetime
{
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromMinutes(2);
    public DateTime? EmptySinceUtc { get; private set; }
    public bool HadPlayers { get; private set; }
    public bool HasExpired(DateTime now) => EmptySinceUtc is { } since && now - since >= ReconnectGrace;
    public void Observe(DateTime now, int connectedPlayers)
    {
        // A late arrival cannot revive an expired match before the next tick.
        if (HasExpired(now)) return;
        if (connectedPlayers > 0) { HadPlayers = true; EmptySinceUtc = null; }
        else if (HadPlayers) EmptySinceUtc ??= now;
    }
}
