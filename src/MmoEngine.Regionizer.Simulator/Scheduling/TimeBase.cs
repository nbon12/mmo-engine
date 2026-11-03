using MmoEngine.Regionizer.Simulator.Config;

namespace MmoEngine.Regionizer.Simulator.Scheduling;

/// <summary>
/// The continent time base of one run (research D7): an integer count of base steps at
/// <see cref="BaseStepHz"/>. A region at <see cref="TickHz"/> is due every <see cref="TickPeriodSteps"/>
/// steps and, while degraded, every <see cref="DegradedPeriodSteps"/>. Continent tick <c>t</c> covers
/// steps <c>t·p</c> to <c>t·p + p − 1</c> for <c>p = TickPeriodSteps</c>. Instances are immutable, so
/// runs on parallel threads each hold their own.
/// </summary>
/// <remarks>
/// The static members describe the default rates (60, 30 and 20 Hz) for code that has no run's time
/// base yet. A run's own rates come from <see cref="From"/>.
/// </remarks>
public sealed class TimeBase
{
    /// <summary>The default base step rate (<c>scheduling.baseStepHz</c>).</summary>
    public const int DefaultBaseStepHz = 60;

    /// <summary>The default region tick rate (<c>scheduling.tickHz</c>).</summary>
    public const int DefaultTickHz = 30;

    /// <summary>The default degraded tick rate (<c>scheduling.degradedTickHz</c>).</summary>
    public const int DefaultDegradedTickHz = 20;

    /// <summary>Base steps per second at the default rates.</summary>
    public const int StepsPerSecond = DefaultBaseStepHz;

    /// <summary>Base steps per continent tick at the default rates.</summary>
    public const int StepsPerTick = DefaultBaseStepHz / DefaultTickHz;

    /// <summary>Base steps per degraded tick at the default rates.</summary>
    public const int StepsPerSlowTick = DefaultBaseStepHz / DefaultDegradedTickHz;

    /// <summary>Continent ticks per second at the default rates.</summary>
    public const int TicksPerSecond = DefaultTickHz;

    /// <param name="baseStepHz">Base steps per second.</param>
    /// <param name="tickHz">Region ticks per second; must divide <paramref name="baseStepHz"/>.</param>
    /// <param name="degradedTickHz">Degraded ticks per second; must divide <paramref name="baseStepHz"/> and be lower than <paramref name="tickHz"/>.</param>
    /// <exception cref="ArgumentException">The rates break those rules.</exception>
    public TimeBase(int baseStepHz, int tickHz, int degradedTickHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(baseStepHz, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tickHz, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(degradedTickHz, 1);
        if (baseStepHz % tickHz != 0 || baseStepHz % degradedTickHz != 0)
        {
            throw new ArgumentException($"The base step rate {baseStepHz} Hz must be a multiple of {tickHz} Hz and {degradedTickHz} Hz.", nameof(baseStepHz));
        }

        if (degradedTickHz >= tickHz)
        {
            throw new ArgumentException($"The degraded tick rate {degradedTickHz} Hz must be lower than the tick rate {tickHz} Hz.", nameof(degradedTickHz));
        }

        BaseStepHz = baseStepHz;
        TickHz = tickHz;
        DegradedTickHz = degradedTickHz;
        TickPeriodSteps = baseStepHz / tickHz;
        DegradedPeriodSteps = baseStepHz / degradedTickHz;
    }

    /// <summary>The default rates: 60 Hz base steps, 30 Hz ticks, 20 Hz degraded ticks.</summary>
    public static TimeBase Default { get; } = new(DefaultBaseStepHz, DefaultTickHz, DefaultDegradedTickHz);

    public int BaseStepHz { get; }

    public int TickHz { get; }

    public int DegradedTickHz { get; }

    /// <summary>Base steps between a region's ticks at <see cref="TickHz"/>.</summary>
    public int TickPeriodSteps { get; }

    /// <summary>Base steps between a degraded region's ticks.</summary>
    public int DegradedPeriodSteps { get; }

    /// <summary>The time base of a validated <c>scheduling</c> section.</summary>
    public static TimeBase From(SchedulingConfig scheduling)
    {
        ArgumentNullException.ThrowIfNull(scheduling);
        return new TimeBase(scheduling.BaseStepHz, scheduling.TickHz, scheduling.DegradedTickHz);
    }

    /// <summary>Seconds as base steps at the default rate, rounded half away from zero.</summary>
    public static long SecondsToSteps(double seconds) => Default.ToSteps(seconds);

    /// <summary>Base steps at the default rate as seconds.</summary>
    public static double StepsToSeconds(long steps) => Default.ToSeconds(steps);

    /// <summary>The continent tick a base step belongs to, at the default rates.</summary>
    public static long TickOf(long step) => Default.TickAt(step);

    /// <summary>The first base step of a continent tick, at the default rates.</summary>
    public static long FirstStepOf(long tick) => Default.FirstStepOfTick(tick);

    /// <summary>True on the last base step of a continent tick, at the default rates.</summary>
    public static bool EndsTick(long step) => Default.IsLastStepOfTick(step);

    /// <summary>Seconds as base steps, rounded half away from zero.</summary>
    public long ToSteps(double seconds) => (long)Math.Round(seconds * BaseStepHz, MidpointRounding.AwayFromZero);

    /// <summary>Base steps as seconds.</summary>
    public double ToSeconds(long steps) => steps / (double)BaseStepHz;

    /// <summary>Continent ticks as seconds.</summary>
    public double TicksToSeconds(long ticks) => ticks / (double)TickHz;

    /// <summary>Continent ticks as base steps: the run length, and a timer delay in absolute mode (ticks × baseStepHz / tickHz).</summary>
    public long TicksToSteps(long ticks) => ticks * TickPeriodSteps;

    /// <summary>The continent tick a base step belongs to.</summary>
    public long TickAt(long step) => step / TickPeriodSteps;

    /// <summary>The first base step of a continent tick.</summary>
    public long FirstStepOfTick(long tick) => tick * TickPeriodSteps;

    /// <summary>True on the last base step of a continent tick, where its metrics row is recorded.</summary>
    public bool IsLastStepOfTick(long step) => step % TickPeriodSteps == TickPeriodSteps - 1;

    public override string ToString() => $"{BaseStepHz} Hz base, {TickHz} Hz ticks, {DegradedTickHz} Hz degraded";
}
