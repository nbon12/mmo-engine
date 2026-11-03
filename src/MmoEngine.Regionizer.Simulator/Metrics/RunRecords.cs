namespace MmoEngine.Regionizer.Simulator.Metrics;

/// <summary>The event types of <c>events.csv</c> (contracts/run-output.md).</summary>
public enum RunEventType
{
    /// <summary>A = absorber, B = absorbed. Only merges where both sides held an entity; absorptions are not events.</summary>
    Merge,

    /// <summary>A = parent, B = number of resulting regions.</summary>
    Split,

    /// <summary>A merge joining fight tags. A = absorber, B = absorbed, value = players after.</summary>
    Fusion,

    /// <summary>A = region, value = age in steps. A region absorbed while holding no entity, or one that never held an entity, is left out.</summary>
    RegionDied,

    /// <summary>A = region, value = ticks from the first sampled two-island tick to the split.</summary>
    SplitLatency,

    /// <summary>detail = rule and message. A run stops at its first violation.</summary>
    Violation,

    /// <summary>A = region, detail = timer ID, expected and actual.</summary>
    TimerFault,
}

/// <summary>One row of <c>events.csv</c>.</summary>
public readonly record struct RunEvent(
    long Tick,
    long TimeSteps,
    RunEventType Type,
    long RegionA,
    long? RegionB = null,
    EntityClass? CauseClass = null,
    int? CauseEntity = null,
    double? Value = null,
    string? Detail = null);

/// <summary>One row of <c>ticks.csv</c>: the state after a continent tick (contracts/run-output.md).</summary>
/// <param name="Tick">Continent tick index (at <c>scheduling.tickHz</c>, 30 Hz by default).</param>
/// <param name="TimeSteps">The time base at the tick's end, in base steps (60 Hz by default).</param>
/// <param name="RegionCount">Regions holding at least one player.</param>
/// <param name="LargestRegionPlayers">Players in the largest region.</param>
/// <param name="LargestRegionShare">Its share of all placed players.</param>
/// <param name="Merges">Merges where both sides held an entity, since the previous row.</param>
/// <param name="Splits">Splits since the previous row.</param>
/// <param name="Fusions">Fusions since the previous row.</param>
/// <param name="SectionChurn">Section owner changes since the previous row, except the moves of absorptions.</param>
/// <param name="CrossPairsDeferred">Player pairs in different regions within the deferred range, when sampled.</param>
/// <param name="CrossPairsReadOnly">Player pairs in different regions beyond the deferred range and within the read-only range, when sampled.</param>
/// <param name="BookkeepingMicros">Wall-clock time in regionizer calls during the tick.</param>
/// <param name="SerializedMicros">The cross-region part of it (core's serialized timestamps).</param>
/// <param name="OversizedFusion">A region holding two or more fight tags has more players than the target fight size.</param>
/// <param name="FusedFightPairs">Distinct pairs of fight tags sharing a region after the tick.</param>
/// <param name="Violations">Violations and timer faults found during the tick.</param>
/// <param name="StateHash">The chained state hash after the tick (research D8).</param>
/// <param name="Absorptions">Merges in which a side held no entity, since the previous row (written after <c>merges</c>).</param>
public readonly record struct TickRow(
    long Tick,
    long TimeSteps,
    int RegionCount,
    int LargestRegionPlayers,
    double LargestRegionShare,
    long Merges,
    long Splits,
    long Fusions,
    long SectionChurn,
    long? CrossPairsDeferred,
    long? CrossPairsReadOnly,
    double BookkeepingMicros,
    double SerializedMicros,
    bool OversizedFusion,
    int FusedFightPairs,
    int Violations,
    ulong StateHash,
    long Absorptions = 0);

/// <summary>Nearest-rank percentiles, the maximum and the mean of one metric.</summary>
public readonly record struct MetricSummary(double P50, double P95, double P99, double Max, double Mean, int Count);

/// <summary>Percentiles by the nearest-rank method (contracts/run-output.md).</summary>
public static class Percentiles
{
    /// <summary>
    /// The value of rank ⌈p·n/100⌉ (1-based) in ascending order. <paramref name="sorted"/> must be
    /// ascending and non-empty; <paramref name="percent"/> is in (0, 100].
    /// </summary>
    public static double NearestRank(ReadOnlySpan<double> sorted, int percent)
    {
        if (sorted.IsEmpty)
        {
            throw new ArgumentException("No values.", nameof(sorted));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        var rank = (int)(((long)percent * sorted.Length + 99) / 100);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    /// <summary>p50, p95, p99, max and mean of <paramref name="values"/>; all zero when there are none.</summary>
    public static MetricSummary Summarize(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return default;
        }

        var sorted = values.ToArray();
        Array.Sort(sorted);
        var sum = 0.0;
        foreach (var v in sorted)
        {
            sum += v;
        }

        return new MetricSummary(
            NearestRank(sorted, 50),
            NearestRank(sorted, 95),
            NearestRank(sorted, 99),
            sorted[^1],
            sum / sorted.Length,
            sorted.Length);
    }
}
