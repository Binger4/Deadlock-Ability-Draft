namespace AbilityDraft.Runtime;

public static class ServerSimulationClock
{
    // Deadlock's dedicated server runs at 64 ticks/second. Deadworks 0.5.6
    // reads obsolete curtime/interval offsets on the current game build.
    public const float DefaultTickInterval = 1f / 64f;

    public static float Interval(float nativeInterval) =>
        float.IsFinite(nativeInterval) && nativeInterval is >= 0.001f and <= 0.1f
            ? nativeInterval : DefaultTickInterval;

    public static float Time(float nativeTime, int tickCount, float nativeInterval)
    {
        var interval = Interval(nativeInterval);
        var tickTime = Math.Max(0, tickCount) * interval;
        return float.IsFinite(nativeTime) && nativeTime >= 0 && Math.Abs(nativeTime - tickTime) <= interval * 2
            ? nativeTime : tickTime;
    }
}
