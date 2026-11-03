namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>Whether a memo row carries a recommendation.</summary>
public enum MemoRowStatus
{
    /// <summary>A value was read off the curve.</summary>
    Recommended,

    /// <summary>The results were read, but the rule gives no value (a conflict, or nothing in the swept range qualifies).</summary>
    Flagged,

    /// <summary>The results could not be read. The memo does not succeed.</summary>
    Unreadable,
}

/// <summary>One point of a value's curve: x and the metric's mean, min and max over seeds.</summary>
/// <param name="Series">The cell's other swept parameters (empty when the sweep steps x alone).</param>
public sealed record CurvePoint(string Series, string X, double Mean, double Min, double Max, int Seeds);

/// <summary>A metric's mean, min and max over seeds.</summary>
public sealed record CurveStat(double Mean, double Min, double Max);

/// <summary>
/// One point of the constitution reading (research D15): the fused-fight-pairs, region-count and
/// largest-share curves at one swept value, each over seeds.
/// </summary>
public sealed record ReadingPoint(string Series, string X, CurveStat FusedFightPairs, CurveStat RegionCount, CurveStat LargestShare);

/// <summary>One value of the decision memo, with its evidence.</summary>
/// <param name="Recommended">The value labeled as a measurement, or what stands in for it when there is none.</param>
/// <param name="Label">Whether the value is a recommendation or a reading.</param>
/// <param name="LabelText">The label as the memo prints it, naming Principle VI and the open decision when there is one.</param>
/// <param name="Population">The <c>population.total</c> the value is read at, checked against every run's manifest.</param>
/// <param name="Rule">How the value is read (research D15).</param>
/// <param name="Source">The sweeps, metric and x the value is read from.</param>
/// <param name="Reading">The constitution reading next to the curve, or empty when the rule records none.</param>
/// <param name="RegionCountNote">Where region count collapses, for a value with a constitution reading.</param>
/// <param name="MergeCounting">How the merge rate treats absorptions, for a value read from merge rates.</param>
/// <param name="Note">The Principle VI note for a value that bears on an open decision.</param>
/// <param name="Problem">Why the row is flagged or unreadable.</param>
public sealed record MemoRow(
    string Id,
    string Title,
    MemoRowStatus Status,
    string Recommended,
    MemoLabel Label,
    string LabelText,
    int Population,
    string Margin,
    string Rule,
    string Source,
    IReadOnlyList<CurvePoint> Curve,
    IReadOnlyList<ReadingPoint> Reading,
    string? RegionCountNote,
    string? MergeCounting,
    IReadOnlyList<ulong> Seeds,
    IReadOnlyList<string> RunIds,
    string? Note,
    string? Problem);

/// <summary>One row of a measured option's side-by-side table: a metric and its cell under each option.</summary>
public sealed record OptionTableRow(string Metric, IReadOnlyList<string> Cells);

/// <summary>A measured option reported side by side, with no recommendation (research D15).</summary>
/// <param name="Options">The column headings, one per option.</param>
/// <param name="Table">The metrics, then the runs that stopped at a timer fault or an invariant violation.</param>
/// <param name="Problem">Why the option could not be read, or null.</param>
public sealed record MeasuredOptionResult(
    string Id,
    string Title,
    bool Readable,
    int Population,
    string Description,
    string Source,
    IReadOnlyList<string> Options,
    IReadOnlyList<OptionTableRow> Table,
    string? MergeCounting,
    IReadOnlyList<ulong> Seeds,
    IReadOnlyList<string> RunIds,
    string? Problem);

/// <summary>The memo's rows, its measured options and its Markdown.</summary>
public sealed record MemoResult(IReadOnlyList<MemoRow> Rows, IReadOnlyList<MeasuredOptionResult> MeasuredOptions, string Markdown)
{
    /// <summary>True when every value and measured option could be read; the <c>memo</c> command exits 4 otherwise.</summary>
    public bool Success => Rows.All(r => r.Status != MemoRowStatus.Unreadable) && MeasuredOptions.All(o => o.Readable);
}
