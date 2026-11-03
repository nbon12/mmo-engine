using System.Diagnostics;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Metrics;

/// <summary>
/// The bookkeeping clock's own cost (research D9): calibrated at run start, recorded as
/// <c>scheduling.timerOverheadNanos</c>, and subtracted from each timed call, never below 0.
/// </summary>
public sealed class TimestampOverheadTests
{
    [Theory]
    [InlineData(10L, 3.0, 7.0)]
    [InlineData(10L, 2.5, 7.5)]
    [InlineData(3L, 3.0, 0.0)]
    [InlineData(2L, 3.0, 0.0)]
    [InlineData(0L, 0.0, 0.0)]
    public void EachCallIsChargedItsTimeLessTheOverheadNeverBelowZero(long elapsedTicks, double overheadTicks, double charged)
    {
        Assert.Equal(charged, TimestampOverhead.Net(elapsedTicks, overheadTicks));
    }

    [Fact]
    public void CalibrationMeasuresAFiniteNonNegativePairCost()
    {
        var ticks = TimestampOverhead.CalibrateTicks();
        var nanos = TimestampOverhead.TicksToNanos(ticks);

        Assert.True(double.IsFinite(ticks) && ticks >= 0, $"calibrated {ticks} Stopwatch ticks");
        Assert.True(nanos < 1_000_000, $"a timestamp pair should cost well under a millisecond, calibrated {nanos} ns");
        Assert.Throws<ArgumentOutOfRangeException>(() => TimestampOverhead.CalibrateTicks(9));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(41.5)]
    [InlineData(1e9)]
    public void NanosecondsAndStopwatchTicksConvertBothWays(double nanos)
    {
        var ticks = TimestampOverhead.NanosToTicks(nanos);

        Assert.Equal(nanos * Stopwatch.Frequency / 1e9, ticks, 9);
        Assert.Equal(nanos, TimestampOverhead.TicksToNanos(ticks), 6);
    }

    /// <summary>
    /// With an overhead larger than any call, every timed call is charged 0, so every tick's
    /// bookkeeping is exactly 0; with none, the calls are charged in full. The value used is recorded.
    /// </summary>
    [Theory]
    [InlineData(1e12, true)]
    [InlineData(0.0, false)]
    public void TheOverheadIsSubtractedFromEachTimedCallAndRecordedInTheSummary(double overheadNanos, bool allZero)
    {
        const ulong Seed = 6;
        var dir = TestRuns.TempDir();

        var result = SimulationRunner.Run(TestRuns.Load(TestRuns.Small("Baseline", Seed, 20)), dir, new RunOptions { GitSha = "unknown", TimerOverheadNanos = overheadNanos });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        var bookkeeping = TestRuns.CsvColumn(Path.Combine(dir, RunFolder.TicksFile), "bookkeepingMicros").Select(TestRuns.ParseDouble).ToList();
        Assert.Equal(20, bookkeeping.Count);
        Assert.All(bookkeeping, b => Assert.True(b >= 0, $"seed {Seed}: bookkeeping {b} µs is negative"));
        Assert.Equal(allZero, bookkeeping.All(b => b == 0));
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunFolder.SummaryFile)))!;
        Assert.Equal(overheadNanos, summary["scheduling"]!["timerOverheadNanos"]!.GetValue<double>(), 3);
        Assert.Equal(overheadNanos, result.TimerOverheadNanos, 3);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnInvalidOverheadIsRejectedBeforeTheRun(double overheadNanos)
    {
        var dir = TestRuns.TempDir();

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulationRunner.Run(TestRuns.Load(TestRuns.Small("Baseline", 1, 2)), dir, new RunOptions { GitSha = "unknown", TimerOverheadNanos = overheadNanos }));
        Assert.False(Directory.Exists(dir));
    }
}
