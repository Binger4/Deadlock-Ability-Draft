using AbilityDraft.Runtime;
using DeadworksManaged.Api;

namespace AbilityDraft.Deadworks;

internal static class NativeGameTime
{
    public static float Interval => ServerSimulationClock.Interval(GlobalVars.IntervalPerTick);
    public static float Now => ServerSimulationClock.Time(GlobalVars.CurTime, GlobalVars.TickCount, GlobalVars.IntervalPerTick);

    public static float MatchClock
    {
        get
        {
            if (!GameRules.IsValid || !GlobalVars.IsValid) return 0;
            var now = Now;
            if (GameRules.GamePaused && now > GameRules.PauseStartTick * Interval)
                return (GameRules.PauseStartTick - GameRules.TotalPausedTicks) * Interval - GameRules.GameStartTime;
            return now - GameRules.TotalPausedTicks * Interval - GameRules.GameStartTime;
        }
    }
}
