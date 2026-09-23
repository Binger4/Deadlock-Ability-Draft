namespace AbilityDraft.Runtime;

// Game-time clock: pausing the simulation cannot consume the lane preparation window.
public sealed class MatchPreparation
{
    public const int DurationSeconds = 30;
    public float? EndsAt { get; private set; }
    public void Begin(float gameTime) => EndsAt ??= gameTime + DurationSeconds;
    public int Remaining(float gameTime) => EndsAt is { } end ? Math.Max(0, (int)Math.Ceiling(end - gameTime)) : DurationSeconds;
    public bool CanStart(float gameTime, bool rosterReady) => rosterReady && EndsAt is { } end && gameTime >= end;
    public void Reset() => EndsAt = null;
}
