namespace MmoEngine.Regionizer.Simulator.Scheduling;

/// <summary>
/// The continent time base (research D7): an integer count of 60 Hz base steps. A region at 30 Hz is
/// due every 2 steps and at 20 Hz every 3. Continent tick <c>t</c> covers steps <c>2t</c> and <c>2t + 1</c>.
/// </summary>
public static class TimeBase
{
    public const int StepsPerSecond = 60;

    /// <summary>Base steps per continent tick (30 Hz).</summary>
    public const int StepsPerTick = 2;

    /// <summary>Base steps per tick of a region dropped to 20 Hz.</summary>
    public const int StepsPerSlowTick = 3;

    public const int TicksPerSecond = StepsPerSecond / StepsPerTick;

    /// <summary>Seconds as base steps, rounded half away from zero.</summary>
    public static long SecondsToSteps(double seconds) => (long)Math.Round(seconds * StepsPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Base steps as seconds.</summary>
    public static double StepsToSeconds(long steps) => steps / (double)StepsPerSecond;

    /// <summary>The continent tick a base step belongs to.</summary>
    public static long TickOf(long step) => step / StepsPerTick;

    /// <summary>The first base step of a continent tick.</summary>
    public static long FirstStepOf(long tick) => tick * StepsPerTick;

    /// <summary>True on the last base step of a continent tick, where its metrics row is recorded.</summary>
    public static bool EndsTick(long step) => step % StepsPerTick == StepsPerTick - 1;
}
