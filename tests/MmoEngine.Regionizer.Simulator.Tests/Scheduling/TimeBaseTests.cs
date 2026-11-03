using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Tests.Scheduling;

/// <summary>The run's time base built from its tick rates (FR-018, research D7).</summary>
public sealed class TimeBaseTests
{
    [Theory]
    [InlineData(60, 30, 20, 2, 3)]
    [InlineData(120, 30, 20, 4, 6)]
    [InlineData(60, 60, 20, 1, 3)]
    [InlineData(90, 30, 10, 3, 9)]
    public void PeriodsAreTheBaseRateOverEachTickRate(int baseStepHz, int tickHz, int degradedTickHz, int period, int degradedPeriod)
    {
        var time = new TimeBase(baseStepHz, tickHz, degradedTickHz);

        Assert.Equal((baseStepHz, tickHz, degradedTickHz), (time.BaseStepHz, time.TickHz, time.DegradedTickHz));
        Assert.Equal(period, time.TickPeriodSteps);
        Assert.Equal(degradedPeriod, time.DegradedPeriodSteps);
        Assert.Equal($"{baseStepHz} Hz base, {tickHz} Hz ticks, {degradedTickHz} Hz degraded", time.ToString());
    }

    [Theory]
    [InlineData(120, 30, 20, 0L, 0L, true, false)]
    [InlineData(120, 30, 20, 3L, 0L, false, true)]
    [InlineData(120, 30, 20, 4L, 1L, true, false)]
    [InlineData(120, 30, 20, 6L, 1L, false, false)]
    [InlineData(60, 60, 20, 5L, 5L, true, true)]
    [InlineData(90, 30, 10, 8L, 2L, false, true)]
    public void StepsBelongToContinentTicksOfTheConfiguredPeriod(int baseStepHz, int tickHz, int degradedTickHz, long step, long tick, bool first, bool last)
    {
        var time = new TimeBase(baseStepHz, tickHz, degradedTickHz);

        Assert.Equal(tick, time.TickAt(step));
        Assert.Equal(first, time.FirstStepOfTick(tick) == step);
        Assert.Equal(last, time.IsLastStepOfTick(step));
    }

    [Theory]
    [InlineData(60, 2.0, 120L)]
    [InlineData(120, 2.0, 240L)]
    [InlineData(120, 0.0125, 2L)]
    [InlineData(120, 0.00625, 1L)]
    [InlineData(30, 0.05, 2L)]
    public void SecondsBecomeStepsRoundedHalfAwayFromZero(int baseStepHz, double seconds, long steps)
    {
        var time = new TimeBase(baseStepHz, baseStepHz / 2, baseStepHz / 3);

        Assert.Equal(steps, time.ToSteps(seconds));
        Assert.Equal(steps / (double)baseStepHz, time.ToSeconds(steps));
    }

    /// <summary>A delay in ticks is ticks × baseStepHz / tickHz base steps (data-model.md, Timer); durations likewise.</summary>
    [Theory]
    [InlineData(60, 30, 20, 90L, 180L, 3.0)]
    [InlineData(120, 30, 20, 90L, 360L, 3.0)]
    [InlineData(60, 60, 20, 90L, 90L, 1.5)]
    [InlineData(90, 30, 10, 7L, 21L, 7 / 30.0)]
    public void TicksBecomeStepsAndSecondsAtTheTickRate(int baseStepHz, int tickHz, int degradedTickHz, long ticks, long steps, double seconds)
    {
        var time = new TimeBase(baseStepHz, tickHz, degradedTickHz);

        Assert.Equal(steps, time.TicksToSteps(ticks));
        Assert.Equal(seconds, time.TicksToSeconds(ticks));
    }

    [Theory]
    [InlineData(0, 30, 20)]
    [InlineData(60, 0, 20)]
    [InlineData(60, 30, 0)]
    public void RatesBelowOneHertzAreRejected(int baseStepHz, int tickHz, int degradedTickHz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeBase(baseStepHz, tickHz, degradedTickHz));
    }

    [Theory]
    [InlineData(50, 30, 20, "baseStepHz")]
    [InlineData(60, 25, 20, "baseStepHz")]
    [InlineData(60, 30, 7, "baseStepHz")]
    [InlineData(60, 30, 30, "degradedTickHz")]
    [InlineData(60, 20, 30, "degradedTickHz")]
    public void RatesTheBaseRateIsNotAMultipleOfOrADegradedRateNotBelowTheTickRateAreRejected(int baseStepHz, int tickHz, int degradedTickHz, string parameter)
    {
        var ex = Assert.Throws<ArgumentException>(() => new TimeBase(baseStepHz, tickHz, degradedTickHz));

        Assert.Equal(parameter, ex.ParamName);
    }

    [Fact]
    public void TheTimeBaseOfAConfigurationUsesItsSchedulingRates()
    {
        var time = TimeBase.From(new SchedulingConfig { BaseStepHz = 120, TickHz = 40, DegradedTickHz = 20 });

        Assert.Equal((120, 40, 20, 3, 6), (time.BaseStepHz, time.TickHz, time.DegradedTickHz, time.TickPeriodSteps, time.DegradedPeriodSteps));
        Assert.Throws<ArgumentNullException>(() => TimeBase.From(null!));
    }

    /// <summary>The static helpers, kept for code that has no run's time base, are the default rates.</summary>
    [Fact]
    public void TheStaticHelpersAreTheDefaultSixtyThirtyAndTwentyHertzRates()
    {
        var defaults = new SchedulingConfig();

        Assert.Equal((60, 30, 20), (defaults.BaseStepHz, defaults.TickHz, defaults.DegradedTickHz));
        Assert.Equal((60, 30, 20), (TimeBase.Default.BaseStepHz, TimeBase.Default.TickHz, TimeBase.Default.DegradedTickHz));
        Assert.Equal((60, 2, 3, 30), (TimeBase.StepsPerSecond, TimeBase.StepsPerTick, TimeBase.StepsPerSlowTick, TimeBase.TicksPerSecond));
        Assert.Equal(TimeBase.Default.ToSteps(1.5), TimeBase.SecondsToSteps(1.5));
        Assert.Equal(TimeBase.Default.ToSeconds(45), TimeBase.StepsToSeconds(45));
        Assert.Equal(TimeBase.Default.TickAt(45), TimeBase.TickOf(45));
        Assert.Equal(TimeBase.Default.FirstStepOfTick(7), TimeBase.FirstStepOf(7));
        Assert.Equal(TimeBase.Default.IsLastStepOfTick(45), TimeBase.EndsTick(45));
    }
}
