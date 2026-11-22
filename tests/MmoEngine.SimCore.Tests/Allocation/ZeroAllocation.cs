namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// The measurement every zero-allocation test of this feature uses (FR-032, US7.3, SC-005): warm the call
/// up twice, then count <see cref="GC.GetAllocatedBytesForCurrentThread"/> across ten separate windows of
/// 1,000 calls, and require every window to be exactly 0 bytes.
/// </summary>
/// <remarks>
/// <para>
/// There is no byte tolerance, and one allowance only. When exactly one window of the first measurement is
/// not 0 bytes, the whole measurement runs once more, all ten windows, on the same state and with no new
/// warm-up, and every window of that rerun must be exactly 0 bytes. Two or more non-zero windows in the
/// first measurement fail at once. The failure message names the row and the call and lists every
/// window's bytes of both measurements.
/// </para>
/// <para>
/// The allowance is for a one-off stray count. One counted window of 1,000 calls failed once in five
/// full-suite runs during the Phase 2 review, with 4,952 bytes for a <c>SimWorld.Step</c> that allocated
/// 0 in every rerun. A count like that lands in one window and does not come back. An allocation the call
/// makes does: a per-call allocation shows in every window, and one that recurs every 10,000 calls or
/// fewer shows at least once in any 10,000 consecutive calls, so it shows in two windows of the first
/// measurement or again in the rerun. What the rule gives up is a single allocation made once, after the
/// warm-up, which reads as stray; the world's buffers are sized at construction, so no step has such a
/// path.
/// </para>
/// <para>
/// The rule before this one required only the smallest of three windows to be 0 bytes, and a
/// <c>MovementStep.Step</c> that allocated 64 bytes on every 9,000th call passed every allocation test
/// under it (Phase 10 spec-test review, US7-AS3).
/// </para>
/// </remarks>
internal static class ZeroAllocation
{
    /// <summary>The calls run before any count, so first-call work is not counted.</summary>
    public const int WarmUpCalls = 2;

    /// <summary>The calls in each counted window.</summary>
    public const int CallsPerWindow = 1_000;

    /// <summary>The counted windows of one measurement.</summary>
    public const int Windows = 10;

    /// <summary>The counted calls of one measurement: <see cref="Windows"/> windows of <see cref="CallsPerWindow"/>.</summary>
    public const int CallsPerMeasurement = Windows * CallsPerWindow;

    /// <summary>The measurements that can run: the first, and the one rerun a single non-zero window allows.</summary>
    public const int MaxMeasurements = 2;

    /// <summary>The counted windows across both measurements, when the rerun runs.</summary>
    public const int MaxCountedWindows = MaxMeasurements * Windows;

    /// <summary>
    /// Calls <paramref name="call"/> <see cref="WarmUpCalls"/> times with indices 0 and 1, then measures with
    /// <see cref="MeasureWindows"/>, each window being <see cref="CallsPerWindow"/> calls with indices 0 to 999
    /// and counting the bytes allocated on this thread across them. The delegate is created by the caller
    /// before the first count, and invoking it allocates nothing, so every byte counted is the call's own.
    /// </summary>
    public static ZeroAllocationMeasurement Measure(Action<int> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        for (var i = 0; i < WarmUpCalls; i++)
        {
            call(i);
        }

        return MeasureWindows(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < CallsPerWindow; i++)
            {
                call(i);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        });
    }

    /// <summary>
    /// Runs <paramref name="window"/> <see cref="Windows"/> times as the first measurement, then, only when
    /// exactly one of those windows counted bytes, <see cref="Windows"/> times more as the rerun, with no
    /// warm-up in between. Each run of <paramref name="window"/> counts one window and returns its bytes.
    /// It is for callers that warm up and count their own windows, pausing the count for work that is not
    /// measured; <see cref="Measure"/> is built on it.
    /// </summary>
    public static ZeroAllocationMeasurement MeasureWindows(Func<long> window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var first = Run(window);
        var rerun = NonZeroWindows(first) == 1 ? Run(window) : [];
        return new ZeroAllocationMeasurement(first, rerun);
    }

    /// <summary>
    /// Asserts that every window allocated exactly 0 bytes: every window of the first measurement, or, when
    /// exactly one of them was not 0, every window of the rerun. The message starts with
    /// <paramref name="what"/>, which names the row and the call, and lists every window's bytes of both
    /// measurements. <paramref name="window"/> describes one window, <c>1000 calls</c> when it is null.
    /// </summary>
    public static void AssertEveryWindowAllocatedNothing(ZeroAllocationMeasurement measurement, string what, string? window = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        var nonZero = NonZeroWindows(measurement.First);
        var passed = nonZero == 0
            || (nonZero == 1 && measurement.Rerun.Length == Windows && NonZeroWindows(measurement.Rerun) == 0);
        if (passed)
        {
            return;
        }

        var rerun = measurement.Rerun.Length == 0
            ? $"; it had {nonZero} non-zero windows, so there was no rerun"
            : $", then [{string.Join(", ", measurement.Rerun)}] bytes in the rerun of all {measurement.Rerun.Length} windows";
        Assert.Fail(
            $"{what} allocated [{string.Join(", ", measurement.First)}] bytes in {measurement.First.Length} windows of {window ?? $"{CallsPerWindow} calls"}{rerun}; "
            + "every window must be 0 bytes, and only a first measurement with exactly one non-zero window runs once more, every window of which must then be 0");
    }

    private static long[] Run(Func<long> window)
    {
        var counts = new long[Windows];
        for (var w = 0; w < counts.Length; w++)
        {
            counts[w] = window();
        }

        return counts;
    }

    private static int NonZeroWindows(long[] counts)
    {
        var nonZero = 0;
        foreach (var count in counts)
        {
            if (count != 0)
            {
                nonZero++;
            }
        }

        return nonZero;
    }
}
