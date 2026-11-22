using Xunit.Sdk;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// Pins the rule every zero-allocation test measures with (<see cref="ZeroAllocation"/>; US7.3, Phase 10
/// spec-test review of US7-AS3): ten windows of 1,000 calls after two warm-up calls, every window exactly
/// 0 bytes; a first measurement with exactly one non-zero window runs once more, all ten windows, and
/// every window of the rerun must then be 0; two or more non-zero windows fail with no rerun. The
/// <see cref="ZeroAllocation.MeasureWindows"/> rows feed it window counts, so they pin the decision alone;
/// the <see cref="ZeroAllocation.Measure"/> rows allocate on chosen calls and count them for real.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class ZeroAllocationRuleTests
{
    /// <summary>
    /// Rows <c>(name, windows, passes, windowsRun)</c>: the counts the window delegate returns, in order;
    /// whether the measurement passes; and how many windows it runs, 10 without the rerun and 20 with it.
    /// </summary>
    public static TheoryData<string, long[], bool, int> WindowCases => new()
    {
        { "all-zero", Counts(ZeroAllocation.Windows), true, 10 },
        { "one-stray-then-clean", [.. Counts(ZeroAllocation.Windows, (2, 4952)), .. Counts(ZeroAllocation.Windows)], true, 20 },
        { "one-stray-in-the-last-window-then-clean", [.. Counts(ZeroAllocation.Windows, (9, 64)), .. Counts(ZeroAllocation.Windows)], true, 20 },
        { "one-stray-then-one-in-the-rerun", [.. Counts(ZeroAllocation.Windows, (2, 4952)), .. Counts(ZeroAllocation.Windows, (7, 64))], false, 20 },
        { "two-non-zero-windows", Counts(ZeroAllocation.Windows, (0, 64), (9, 64)), false, 10 },
        { "every-window", Counts(ZeroAllocation.Windows, (0, 64), (1, 64), (2, 64), (3, 64), (4, 64), (5, 64), (6, 64), (7, 64), (8, 64), (9, 64)), false, 10 },
        { "one-byte-in-one-window-then-one-byte-again", [.. Counts(ZeroAllocation.Windows, (5, 1)), .. Counts(ZeroAllocation.Windows, (5, 1))], false, 20 },
    };

    /// <summary>
    /// Given a window delegate that returns the row's counts in order, When
    /// <see cref="ZeroAllocation.MeasureWindows"/> runs and its result is asserted, Then exactly the row's
    /// windows ran, the first measurement holds the first ten counts and the rerun the next ten (or none),
    /// and the assertion passes or fails as the row says, a failure naming the row and listing every
    /// window's bytes of both measurements.
    /// </summary>
    [Theory]
    [MemberData(nameof(WindowCases))]
    public void MeasureWindowsAppliesTheRule(string name, long[] windows, bool passes, int windowsRun)
    {
        var next = 0;
        var measurement = ZeroAllocation.MeasureWindows(() => windows[next++]);

        Assert.Equal(windowsRun, next);
        Assert.Equal(windowsRun, measurement.CountedWindows);
        Assert.Equal(windows[..ZeroAllocation.Windows], measurement.First);
        Assert.Equal(windows[ZeroAllocation.Windows..windowsRun], measurement.Rerun);
        Assert.Equal(windowsRun / ZeroAllocation.Windows, measurement.Measurements);

        var failure = Record.Exception(() => ZeroAllocation.AssertEveryWindowAllocatedNothing(measurement, name));
        if (passes)
        {
            Assert.Null(failure);
            return;
        }

        var message = Assert.IsType<FailException>(failure).Message;
        Assert.StartsWith($"{name} allocated [{string.Join(", ", measurement.First)}] bytes in 10 windows of 1000 calls", message, StringComparison.Ordinal);
        if (measurement.Rerun.Length != 0)
        {
            Assert.Contains($"then [{string.Join(", ", measurement.Rerun)}] bytes in the rerun of all 10 windows", message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Given a call that allocates nothing, When <see cref="ZeroAllocation.Measure"/> runs it, Then it is
    /// called first with indices 0 and 1 and then with 0 to 999 in each of ten windows, 10,002 calls (20,002
    /// should a stray count have made it rerun), the measurement reports exactly those calls, and the
    /// assertion passes.
    /// </summary>
    [Fact]
    public void MeasureCallsTheWarmUpThenTenWindows()
    {
        var indices = new int[(2 * ZeroAllocation.CallsPerMeasurement) + ZeroAllocation.WarmUpCalls];
        var calls = 0;

        var measurement = ZeroAllocation.Measure(i => indices[calls++] = i);

        Assert.Equal(10, measurement.First.Length);
        Assert.Equal(measurement.Measurements == 1 ? 10_002 : 20_002, calls);
        Assert.Equal(calls, measurement.Calls);
        Assert.Equal(calls - 2, measurement.CountedCalls);
        Assert.Equal(new[] { 0, 1 }, indices[..2]);
        for (var call = ZeroAllocation.WarmUpCalls; call < calls; call++)
        {
            Assert.Equal((call - ZeroAllocation.WarmUpCalls) % ZeroAllocation.CallsPerWindow, indices[call]);
        }

        ZeroAllocation.AssertEveryWindowAllocatedNothing(measurement, "no allocation");
    }

    /// <summary>
    /// Rows <c>(allocatingCall, passes, calls)</c>: a call that allocates 64 bytes when it is call
    /// <c>allocatingCall</c> of <see cref="ZeroAllocation.Measure"/> (counting from 0, warm-up included),
    /// or on every multiple of <c>-allocatingCall</c> when it is negative, the 64-byte probes of the
    /// review: once, at call 2,502, it is a stray count, rerun and passed; every 9,000th, 5,000th, 2,500th
    /// or 1,500th call fails.
    /// </summary>
    public static TheoryData<int, bool, int> AllocatingCalls => new()
    {
        { 2_502, true, 20_002 },
        { -9_000, false, 20_002 },
        { -5_000, false, 10_002 },
        { -2_500, false, 10_002 },
        { -1_500, false, 10_002 },
    };

    /// <summary>
    /// Given a call that allocates 64 bytes (a 40-byte array kept in a field, so it escapes) on the row's
    /// calls, When <see cref="ZeroAllocation.Measure"/> runs it, Then it makes the row's calls and the
    /// assertion passes or fails as the row says: a single allocation after the warm-up is the stray count
    /// the rerun absorbs, and a periodic one recurs in two windows or in the rerun.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllocatingCalls))]
    public void MeasureFailsARecurringAllocation(int allocatingCall, bool passes, int expectedCalls)
    {
        var keeper = new Keeper();
        var calls = 0;

        var measurement = ZeroAllocation.Measure(_ =>
        {
            if (allocatingCall >= 0 ? calls == allocatingCall : calls % -allocatingCall == 0 && calls != 0)
            {
                keeper.Kept = new byte[40];
            }

            calls++;
        });

        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, measurement.Calls);
        var failure = Record.Exception(() => ZeroAllocation.AssertEveryWindowAllocatedNothing(measurement, $"allocating call {allocatingCall}"));
        if (passes)
        {
            Assert.Null(failure);
            Assert.Equal(1, measurement.First.Count(count => count != 0));
        }
        else
        {
            Assert.IsType<FailException>(failure);
        }
    }

    /// <summary>Ten-or-more window counts of 0, with the given windows set to the given bytes.</summary>
    private static long[] Counts(int length, params (int Window, long Bytes)[] nonZero)
    {
        var counts = new long[length];
        foreach (var (window, bytes) in nonZero)
        {
            counts[window] = bytes;
        }

        return counts;
    }

    /// <summary>Holds the last array a call allocated, so the allocation escapes and is not optimised away.</summary>
    private sealed class Keeper
    {
        public byte[]? Kept { get; set; }
    }
}
