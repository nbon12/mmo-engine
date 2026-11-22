namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// What <see cref="ZeroAllocation.Measure"/> or <see cref="ZeroAllocation.MeasureWindows"/> counted: the
/// bytes of each window of the first measurement and, when exactly one of them was not 0, of each window of
/// the rerun. It also says how many measurements ran, so a test can check the calls it made, and anything it
/// counts per window, against the windows that really ran.
/// </summary>
internal sealed class ZeroAllocationMeasurement
{
    public ZeroAllocationMeasurement(long[] first, long[] rerun)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(rerun);
        First = first;
        Rerun = rerun;
    }

    /// <summary>The bytes each of the first measurement's <see cref="ZeroAllocation.Windows"/> windows counted.</summary>
    public long[] First { get; }

    /// <summary>The bytes each window of the rerun counted; empty when there was no rerun.</summary>
    public long[] Rerun { get; }

    /// <summary>The measurements that ran: 1, or 2 when the rerun ran.</summary>
    public int Measurements => Rerun.Length == 0 ? 1 : ZeroAllocation.MaxMeasurements;

    /// <summary>The counted windows that ran across the measurements: 10, or 20 with the rerun.</summary>
    public int CountedWindows => Measurements * ZeroAllocation.Windows;

    /// <summary>
    /// The counted calls <see cref="ZeroAllocation.Measure"/> made: <see cref="ZeroAllocation.CallsPerWindow"/>
    /// per counted window, so 10,000, or 20,000 with the rerun.
    /// </summary>
    public int CountedCalls => CountedWindows * ZeroAllocation.CallsPerWindow;

    /// <summary>
    /// Every call <see cref="ZeroAllocation.Measure"/> made: the <see cref="ZeroAllocation.WarmUpCalls"/>
    /// warm-up calls and the counted calls, so 10,002, or 20,002 with the rerun.
    /// </summary>
    public int Calls => ZeroAllocation.WarmUpCalls + CountedCalls;
}
