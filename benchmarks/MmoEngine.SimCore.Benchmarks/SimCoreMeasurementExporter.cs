using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// Writes the measurement report's <c>server</c> object,
/// <c>&lt;results directory&gt;/simcore-server-benchmarks.json</c>
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced, step 1,
/// and the <c>server</c> table of #fields): <c>tool</c>, <c>label</c>, <c>runnerClass</c> and one
/// <c>benchmarks[]</c> entry per benchmark that has statistics and an allocation measurement, in step order
/// (movement, weapon, projectile, hitboxBuild, stateHash), then in each class's <c>Case</c> <c>[Params]</c>
/// order. Each benchmark's step is its single <c>[BenchmarkCategory]</c> and its case its <c>Case</c>
/// parameter. A report without statistics (a failed benchmark) or without an allocation measurement is
/// left out (<see cref="TryMeasurements"/>), and <see cref="Program"/> exits 1 naming it, by the same rule,
/// because <c>simcore measure validate-report</c> only notices a missing movement, weapon or projectile
/// step. The file is written to a temp file in the same directory and then renamed over the export, so a
/// reader never sees half of it.
/// BenchmarkDotNet logs an exporter's exception and goes on, so <see cref="WrittenPath"/> records whether
/// the export was written, for the exit code of <see cref="Program"/>.
/// </summary>
public sealed class SimCoreMeasurementExporter : IExporter
{
    /// <summary>The export's file name in the summary's results directory.</summary>
    public const string FileName = "simcore-server-benchmarks.json";

    /// <summary>The name of the parameter that holds each benchmark's case.</summary>
    public const string CaseParameter = "Case";

    /// <summary>The steps, in the order the export lists them.</summary>
    private static readonly string[] StepOrder = ["movement", "weapon", "projectile", "hitboxBuild", "stateHash"];

    private readonly string _runnerClass;

    /// <summary>An exporter that labels its benchmarks with <paramref name="runnerClass"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="runnerClass"/> is null.</exception>
    public SimCoreMeasurementExporter(string runnerClass)
    {
        ArgumentNullException.ThrowIfNull(runnerClass);
        _runnerClass = runnerClass;
    }

    /// <summary>The <c>tool</c> value: <c>BenchmarkDotNet &lt;major.minor.patch&gt;</c>, from the BenchmarkDotNet assembly's informational version without its <c>+&lt;commit&gt;</c> suffix.</summary>
    public static string Tool
    {
        get
        {
            var assembly = typeof(BenchmarkRunner).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString(3)
                ?? "unknown";
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            return "BenchmarkDotNet " + (plus < 0 ? version : version[..plus]);
        }
    }

    /// <inheritdoc />
    public string Name => nameof(SimCoreMeasurementExporter);

    /// <summary>
    /// The path the last <see cref="ExportToFiles"/> call wrote, or null when it threw or there has been
    /// none (the run selected no benchmark, or a critical validation error stopped it before the exporters).
    /// </summary>
    public string? WrittenPath { get; private set; }

    /// <summary>Writes nothing to the log: the export is a file, and the console table already shows the numbers.</summary>
    public void ExportToLog(Summary summary, ILogger logger)
    {
    }

    /// <summary>Writes the <c>server</c> object and returns its path.</summary>
    /// <exception cref="InvalidOperationException">A benchmark has no single category, no <c>Case</c> parameter, or a step or case outside the export's order; the message names it.</exception>
    public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
    {
        ArgumentNullException.ThrowIfNull(summary);
        WrittenPath = null;
        var entries = Entries(summary);
        Directory.CreateDirectory(summary.ResultsDirectoryPath);
        var path = Path.Combine(summary.ResultsDirectoryPath, FileName);
        var temp = Path.Combine(summary.ResultsDirectoryPath, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                Write(writer, entries);
            }

            File.Move(temp, path, overwrite: true);
            WrittenPath = path;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        return [path];
    }

    /// <summary>
    /// The one rule for which reports the export lists, which <see cref="Program"/> also applies to its exit
    /// code: true, with the report's <paramref name="statistics"/> and <paramref name="allocatedBytesPerOp"/>,
    /// when it has both; else false, with why it is left out: <c>has no statistics: it failed</c> when
    /// <see cref="BenchmarkReport.ResultStatistics"/> is null, or <c>has no allocation measurement</c> when
    /// <c>GcStats.GetBytesAllocatedPerOperation</c> is null, because <c>allocatedBytesPerOp</c> is a required
    /// integer.
    /// </summary>
    internal static bool TryMeasurements(
        BenchmarkReport report,
        [NotNullWhen(true)] out Statistics? statistics,
        out long allocatedBytesPerOp,
        [NotNullWhen(false)] out string? leftOutReason)
    {
        ArgumentNullException.ThrowIfNull(report);
        statistics = report.ResultStatistics;
        allocatedBytesPerOp = 0;
        if (statistics is null)
        {
            leftOutReason = "has no statistics: it failed";
            return false;
        }

        if (report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) is not { } allocated)
        {
            statistics = null;
            leftOutReason = "has no allocation measurement";
            return false;
        }

        allocatedBytesPerOp = allocated;
        leftOutReason = null;
        return true;
    }

    /// <summary>The export's entries in its order; the reports <see cref="TryMeasurements"/> leaves out are skipped.</summary>
    private static List<Entry> Entries(Summary summary)
    {
        var entries = new List<Entry>();
        foreach (var report in summary.Reports)
        {
            var benchmark = report.BenchmarkCase;
            var (step, stepIndex) = StepOf(benchmark);
            var (caseName, caseIndex) = CaseOf(benchmark);
            if (!TryMeasurements(report, out var statistics, out var allocatedBytesPerOp, out _))
            {
                continue;
            }

            entries.Add(new Entry(
                stepIndex,
                caseIndex,
                step,
                caseName,
                statistics.Mean,
                statistics.Percentiles.Percentile(99),
                statistics.N,
                allocatedBytesPerOp));
        }

        // A stable sort, so several jobs' entries of one case keep the summary's order.
        return [.. entries.OrderBy(static entry => entry.StepIndex).ThenBy(static entry => entry.CaseIndex)];
    }

    private static (string Step, int Index) StepOf(BenchmarkCase benchmark)
    {
        var categories = benchmark.Descriptor.Categories;
        if (categories.Length != 1)
        {
            throw new InvalidOperationException(
                $"The benchmark {benchmark.DisplayInfo} has {categories.Length} categories; it needs exactly one, its step.");
        }

        var index = Array.IndexOf(StepOrder, categories[0]);
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"The benchmark {benchmark.DisplayInfo} has the category '{categories[0]}'; it must be one of {string.Join(", ", StepOrder)}.");
        }

        return (categories[0], index);
    }

    private static (string Case, int Index) CaseOf(BenchmarkCase benchmark)
    {
        foreach (var parameter in benchmark.Parameters.Items)
        {
            if (parameter.Name == CaseParameter && parameter.Value is string caseName)
            {
                var index = Array.IndexOf(parameter.Definition.Values, caseName);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"The benchmark {benchmark.DisplayInfo} has the case '{caseName}', which its [Params] do not list.");
                }

                return (caseName, index);
            }
        }

        throw new InvalidOperationException($"The benchmark {benchmark.DisplayInfo} has no string {CaseParameter} parameter.");
    }

    private void Write(Utf8JsonWriter writer, List<Entry> entries)
    {
        writer.WriteStartObject();
        writer.WriteString("tool", Tool);
        writer.WriteString("label", "measurement");
        writer.WriteString("runnerClass", _runnerClass);
        writer.WriteStartArray("benchmarks");
        foreach (var entry in entries)
        {
            writer.WriteStartObject();
            writer.WriteString("step", entry.Step);
            writer.WriteString("case", entry.Case);
            writer.WriteNumber("meanNs", entry.MeanNs);
            writer.WriteNumber("p99Ns", entry.P99Ns);
            writer.WriteNumber("iterations", entry.Iterations);
            writer.WriteNumber("allocatedBytesPerOp", entry.AllocatedBytesPerOp);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private readonly record struct Entry(
        int StepIndex,
        int CaseIndex,
        string Step,
        string Case,
        double MeanNs,
        double P99Ns,
        int Iterations,
        long AllocatedBytesPerOp);
}
