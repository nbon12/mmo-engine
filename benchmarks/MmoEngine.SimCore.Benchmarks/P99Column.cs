using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The P99 column of the console table: each benchmark's 99th percentile of its measured iterations,
/// <c>report.ResultStatistics.Percentiles.Percentile(99)</c>, shown in the summary's time unit
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced, step 1).
/// It is the same number <see cref="SimCoreMeasurementExporter"/> exports as <c>p99Ns</c>.
/// </summary>
public sealed class P99Column : IColumn
{
    /// <summary>The one instance <see cref="SimCoreBenchmarkConfig.Create"/> adds.</summary>
    public static readonly P99Column Instance = new();

    private const int Percentile = 99;

    private P99Column()
    {
    }

    /// <inheritdoc />
    public string Id => "P99";

    /// <inheritdoc />
    public string ColumnName => "P99";

    /// <inheritdoc />
    public bool AlwaysShow => true;

    /// <inheritdoc />
    public ColumnCategory Category => ColumnCategory.Statistics;

    /// <inheritdoc />
    public int PriorityInCategory => 0;

    /// <inheritdoc />
    public bool IsNumeric => true;

    /// <inheritdoc />
    public UnitType UnitType => UnitType.Time;

    /// <inheritdoc />
    public string Legend => "Percentile 99: 99% of the measured iterations' means per operation are at most this value";

    /// <summary>
    /// True for every summary, including one with no report: a column provider drops an unavailable
    /// column, and a benchmark without statistics shows <c>NA</c> in its own row instead.
    /// </summary>
    public bool IsAvailable(Summary summary) => true;

    /// <inheritdoc />
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    /// <inheritdoc />
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, summary.Style);

    /// <summary>
    /// The benchmark's 99th percentile in <paramref name="style"/>'s time unit, or, when the style has
    /// none, the best unit for the summary's means; <c>NA</c> for a benchmark without statistics.
    /// </summary>
    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        var statistics = summary.HasReport(benchmarkCase) ? summary[benchmarkCase]?.ResultStatistics : null;
        if (statistics is null)
        {
            return "NA";
        }

        var unit = style.TimeUnit ?? BestUnit(summary);
        var value = TimeUnit.Convert(statistics.Percentiles.Percentile(Percentile), TimeUnit.Nanosecond, unit);
        var culture = style.CultureInfo ?? CultureInfo.InvariantCulture;
        var text = value.ToString("N3", culture);
        return style.PrintUnitsInContent ? text + " " + unit.Abbreviation : text;
    }

    /// <inheritdoc />
    public override string ToString() => ColumnName;

    private static TimeUnit BestUnit(Summary summary)
    {
        var means = new List<double>();
        foreach (var report in summary.Reports)
        {
            if (report.ResultStatistics is { } statistics)
            {
                means.Add(statistics.Mean);
            }
        }

        return TimeUnit.GetBestTimeUnit([.. means]);
    }
}
