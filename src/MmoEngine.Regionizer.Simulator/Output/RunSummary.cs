using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Output;

/// <summary>
/// Builds <c>summary.json</c> (contracts/run-output.md): p50, p95, p99, max and mean per metric by
/// nearest rank, rates by causing class, totals (absorptions apart from merges), fused fight pairs,
/// the run's tick rates and timer overhead, the bookkeeping budget check and wall-clock time.
/// Everything except the bookkeeping, timer overhead, budget and wall-clock values is deterministic.
/// </summary>
public static class RunSummary
{
    /// <summary>The summary of a run.</summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="completed">True when every configured tick ran.</param>
    /// <param name="ticks">Ticks run.</param>
    /// <param name="failure">The first violation or timer fault, or null.</param>
    /// <param name="metrics">The collector.</param>
    /// <param name="timers">The timer monitor.</param>
    /// <param name="config">The metrics configuration (budget).</param>
    /// <param name="time">The run's time base; rates are per second of it.</param>
    /// <param name="timerOverheadNanos">The calibrated cost of a timestamp pair, subtracted from each timed call (research D9).</param>
    /// <param name="wallClockSeconds">Wall-clock duration of the run.</param>
    public static JsonObject Build(
        string runId,
        bool completed,
        long ticks,
        RunFailure? failure,
        MetricsCollector metrics,
        TimerMonitor timers,
        MetricsConfig config,
        TimeBase time,
        double timerOverheadNanos,
        double wallClockSeconds)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        var seconds = time.TicksToSeconds(ticks);
        var perMetric = new JsonObject();
        foreach (var name in MetricsCollector.SampledMetrics)
        {
            perMetric[name] = ToJson(Percentiles.Summarize(metrics.Samples(name)));
        }

        var mergeRates = new JsonObject();
        var fusionRates = new JsonObject();
        foreach (var cls in Enum.GetValues<EntityClass>())
        {
            mergeRates[cls.ToString()] = Rate(metrics.MergesCausedBy(cls), seconds);
            fusionRates[cls.ToString()] = Rate(metrics.FusionsCausedBy(cls), seconds);
        }

        var bookkeeping = Percentiles.Summarize(metrics.Samples("bookkeepingMicros"));
        return new JsonObject
        {
            ["formatVersion"] = RunFolder.FormatVersion,
            ["runId"] = runId,
            ["completed"] = completed,
            ["ticks"] = ticks,
            ["firstViolation"] = failure is null ? null : ToJson(failure),
            ["metrics"] = perMetric,
            ["rates"] = new JsonObject
            {
                ["mergesPerSecondByClass"] = mergeRates,
                ["splitsPerSecond"] = Rate(metrics.Splits, seconds),
                ["fusionsPerSecondByClass"] = fusionRates,
            },
            ["totals"] = new JsonObject
            {
                ["merges"] = metrics.Merges,
                ["absorptions"] = metrics.Absorptions,
                ["splits"] = metrics.Splits,
                ["fusions"] = metrics.Fusions,
                ["oversizedFusionTicks"] = metrics.OversizedFusionTicks,
                ["timersSet"] = timers.TimersSet,
                ["timersFired"] = timers.TimersFired,
            },
            ["fusedFightPairs"] = ToJson(Percentiles.Summarize(metrics.FusedFightPairSamples)),
            ["scheduling"] = new JsonObject
            {
                ["baseStepHz"] = time.BaseStepHz,
                ["tickHz"] = time.TickHz,
                ["degradedTickHz"] = time.DegradedTickHz,
                ["timerOverheadNanos"] = timerOverheadNanos,
            },
            ["budget"] = new JsonObject
            {
                ["bookkeepingP99Micros"] = bookkeeping.P99,
                ["budgetMicros"] = config.BookkeepingBudgetMicros,
                ["withinBudget"] = bookkeeping.P99 <= config.BookkeepingBudgetMicros,
            },
            ["wallClock"] = new JsonObject { ["seconds"] = wallClockSeconds },
        };
    }

    /// <summary>The failure as written in <c>summary.json</c>.</summary>
    public static JsonObject ToJson(RunFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new JsonObject
        {
            ["kind"] = failure.Kind.ToString(),
            ["rule"] = failure.Rule?.ToString(),
            ["ruleNumber"] = failure.RuleNumber,
            ["tick"] = failure.Tick,
            ["timeSteps"] = failure.TimeSteps,
            ["seed"] = failure.Seed,
            ["detail"] = failure.Detail,
            ["count"] = failure.Count,
        };
    }

    private static JsonObject ToJson(MetricSummary s) => new()
    {
        ["p50"] = s.P50,
        ["p95"] = s.P95,
        ["p99"] = s.P99,
        ["max"] = s.Max,
        ["mean"] = s.Mean,
    };

    private static double Rate(long count, double seconds) => seconds <= 0 ? 0 : count / seconds;
}
