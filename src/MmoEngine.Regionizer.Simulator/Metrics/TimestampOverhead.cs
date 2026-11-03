using System.Diagnostics;

namespace MmoEngine.Regionizer.Simulator.Metrics;

/// <summary>
/// The cost of the bookkeeping clock itself (research D9). Every core call is timed with a pair of
/// <see cref="Stopwatch.GetTimestamp"/> calls; the pair's own cost, calibrated once at run start, is
/// subtracted from each measured call so it does not inflate the budget reading.
/// </summary>
public static class TimestampOverhead
{
    /// <summary>
    /// The mean cost, in Stopwatch ticks, of two back-to-back timestamps, over the middle 80% of
    /// <paramref name="samples"/> pairs. Trimming drops pairs a context switch or interrupt hit; the
    /// mean (not the median) keeps the sub-tick part on clocks coarser than the pair's cost.
    /// </summary>
    public static double CalibrateTicks(int samples = 4001)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 10);
        var costs = new long[samples];
        for (var i = 0; i < 256; i++)
        {
            Stopwatch.GetTimestamp();
        }

        for (var i = 0; i < samples; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            costs[i] = Stopwatch.GetTimestamp() - t0;
        }

        Array.Sort(costs);
        var trim = samples / 10;
        var sum = 0L;
        for (var i = trim; i < samples - trim; i++)
        {
            sum += costs[i];
        }

        return sum / (double)(samples - (2 * trim));
    }

    /// <summary>A measured interval less the pair's overhead, never below 0.</summary>
    public static double Net(long elapsedTicks, double overheadTicks) => elapsedTicks > overheadTicks ? elapsedTicks - overheadTicks : 0;

    /// <summary>Stopwatch ticks as nanoseconds.</summary>
    public static double TicksToNanos(double ticks) => ticks * 1_000_000_000.0 / Stopwatch.Frequency;

    /// <summary>Nanoseconds as Stopwatch ticks.</summary>
    public static double NanosToTicks(double nanos) => nanos * Stopwatch.Frequency / 1_000_000_000.0;
}
