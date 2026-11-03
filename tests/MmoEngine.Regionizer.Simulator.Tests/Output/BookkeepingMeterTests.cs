using System.Diagnostics;
using MmoEngine.Regionizer.Simulator.Output;

namespace MmoEngine.Regionizer.Simulator.Tests.Output;

/// <summary>The bookkeeping meter (FR-027, research D9): it times core calls, less the timestamp pair's cost.</summary>
public sealed class BookkeepingMeterTests
{
    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TheOverheadMustBeAFiniteTickCountOfAtLeastZero(double overhead)
    {
        var meter = new BookkeepingMeter { OverheadTicks = 3 };

        Assert.Throws<ArgumentOutOfRangeException>(() => meter.OverheadTicks = overhead);
        Assert.Equal(3, meter.OverheadTicks);
    }

    [Fact]
    public void AMeasuredCallIsInCallUntilStoppedAndItsCostIsTakenOnce()
    {
        var meter = new BookkeepingMeter();

        Assert.False(meter.InCall);
        var start = meter.Start();
        Assert.True(meter.InCall);
        while (Stopwatch.GetTimestamp() == start)
        {
        }

        meter.Stop(start);

        Assert.False(meter.InCall);
        Assert.Equal(1, meter.Calls);
        Assert.True(meter.Take() > 0);
        Assert.Equal(0, meter.Take());
    }

    [Fact]
    public void TheOverheadIsSubtractedFromEachCallButNeverBelowZero()
    {
        var meter = new BookkeepingMeter { OverheadTicks = 1e15 };

        meter.Stop(meter.Start());
        meter.Stop(meter.Start());

        Assert.Equal(2, meter.Calls);
        Assert.Equal(0, meter.Take());
    }
}
