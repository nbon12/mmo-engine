using System.Diagnostics;
using MmoEngine.Regionizer.Simulator.Metrics;

namespace MmoEngine.Regionizer.Simulator.Output;

/// <summary>
/// The wall-clock meter for the regionizer's bookkeeping cost (FR-027, research D9). The engine brackets
/// every core call with <see cref="Start"/> and <see cref="Stop"/>, which read
/// <see cref="Stopwatch.GetTimestamp"/>; each interval, less the calibrated cost of a timestamp pair
/// (<see cref="OverheadTicks"/>), adds to the total that <see cref="Take"/> returns once per continent tick.
/// <para>
/// The core calls the simulator's observers synchronously inside these intervals, so whatever they do is
/// measured as bookkeeping. They only queue work there; event output and the timer monitor's shadow work
/// run after the call returns. <see cref="InCall"/> is true only between <see cref="Start"/> and
/// <see cref="Stop"/>, which lets a test check that nothing is written inside a measured call.
/// </para>
/// One meter serves one run on one thread.
/// </summary>
public sealed class BookkeepingMeter
{
    private double _overheadTicks;
    private double _total;

    /// <summary>The cost of a timestamp pair, in Stopwatch ticks, subtracted from each measured call (at least 0).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, infinite or not a number.</exception>
    public double OverheadTicks
    {
        get => _overheadTicks;
        set
        {
            if (!(value >= 0 && double.IsFinite(value)))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The timestamp pair's cost must be a finite number of Stopwatch ticks, at least 0.");
            }

            _overheadTicks = value;
        }
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>: a core call is being measured.</summary>
    public bool InCall { get; private set; }

    /// <summary>Measured calls so far.</summary>
    public long Calls { get; private set; }

    /// <summary>Starts measuring a core call; returns the timestamp to pass to <see cref="Stop"/>.</summary>
    public long Start()
    {
        InCall = true;
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Ends the call started at <paramref name="start"/> and adds its net cost to the total.</summary>
    public void Stop(long start)
    {
        var end = Stopwatch.GetTimestamp();
        InCall = false;
        Calls++;
        _total += TimestampOverhead.Net(end - start, _overheadTicks);
    }

    /// <summary>Returns and resets the Stopwatch ticks measured since the last call, net of the timestamp overhead.</summary>
    public double Take()
    {
        var value = _total;
        _total = 0;
        return value;
    }
}
