using Collector.Engine.Definitions;

namespace Collector.Server.Game;

/// <summary>
/// Decides how far a player's game clock moves between requests. Pure functions, so the rules are easy to test.
/// </summary>
public static class GameClock
{
    /// <summary>
    /// Game time to add for a wall-clock gap since the player's last request, after repaying any time
    /// previously granted ahead of the wall clock.
    /// </summary>
    public static (long AdvanceMs, long AheadMs) CatchUp(ClockDefinition clock, long gapMs, long aheadMs, long sessionTimeoutMs)
    {
        if (clock.Mode == ClockMode.Turn || gapMs <= 0)
            return (0, aheadMs);

        var credit = clock.Offline switch
        {
            OfflinePolicy.Running => gapMs,
            OfflinePolicy.Capped => gapMs <= sessionTimeoutMs ? gapMs : Math.Min(gapMs, Math.Max(sessionTimeoutMs, clock.OfflineCapSeconds * 1000)),
            // Paused: a gap longer than the session timeout means the player was away, so none of it counts.
            _ => gapMs <= sessionTimeoutMs ? gapMs : 0,
        };
        var repaid = Math.Min(credit, aheadMs);
        return (credit - repaid, aheadMs - repaid);
    }

    /// <summary>
    /// When a client's clock is slightly ahead (e.g. it thinks a crop is ready a moment early), grant the difference,
    /// but never let the total granted ahead of the wall clock exceed the tolerance.
    /// </summary>
    public static long Tolerance(ClockDefinition clock, long serverTime, long clientTime, long aheadMs, long toleranceMs)
    {
        if (clock.Mode == ClockMode.Turn || clientTime <= serverTime)
            return 0;
        return Math.Max(0, Math.Min(clientTime - serverTime, toleranceMs - aheadMs));
    }
}
