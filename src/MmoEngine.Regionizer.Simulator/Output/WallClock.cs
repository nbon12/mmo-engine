using System.Diagnostics;

namespace MmoEngine.Regionizer.Simulator.Output;

/// <summary>
/// The run's wall-clock reads, kept off the simulated path (FR-019, research D7). The banned-API scan
/// (tests/MmoEngine.Regionizer.Tests/Architecture/BannedApiTests.cs) covers every simulator folder except
/// <c>Output/</c> and <c>Analysis/</c>, so <c>Running/</c> reads the wall clock only through this helper
/// and <see cref="BookkeepingMeter"/>: the manifest's start and finish times and the summary's wall-clock
/// seconds. Nothing read here reaches the simulated state or its hash.
/// </summary>
public static class WallClock
{
    /// <summary>The current UTC time, for the manifest's <c>startedUtc</c> and <c>finishedUtc</c>.</summary>
    public static DateTime UtcNow() => DateTime.UtcNow;

    /// <summary>A started stopwatch, for the summary's <c>wallClock.seconds</c>.</summary>
    public static Stopwatch StartTimer() => Stopwatch.StartNew();
}
