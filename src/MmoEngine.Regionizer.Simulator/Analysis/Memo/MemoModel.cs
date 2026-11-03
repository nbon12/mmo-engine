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

/// <summary>One value of the decision memo, with its evidence.</summary>
/// <param name="Recommended">The value labeled as a measurement, or what stands in for it when there is none.</param>
/// <param name="Rule">How the value is read (research D15).</param>
/// <param name="Source">The sweeps, metric and x the value is read from.</param>
/// <param name="Note">The Principle VI note for a value that bears on an open decision.</param>
/// <param name="Problem">Why the row is flagged or unreadable.</param>
public sealed record MemoRow(
    string Id,
    string Title,
    MemoRowStatus Status,
    string Recommended,
    string Margin,
    string Rule,
    string Source,
    IReadOnlyList<CurvePoint> Curve,
    IReadOnlyList<ulong> Seeds,
    IReadOnlyList<string> RunIds,
    string? Note,
    string? Problem);

/// <summary>The memo's rows and its Markdown.</summary>
public sealed record MemoResult(IReadOnlyList<MemoRow> Rows, string Markdown)
{
    /// <summary>True when every value could be read; the <c>memo</c> command exits 4 otherwise.</summary>
    public bool Success => Rows.All(r => r.Status != MemoRowStatus.Unreadable);
}
