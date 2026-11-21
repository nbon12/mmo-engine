using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The server benchmarks' entry point
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced, step 1):
/// <c>dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'</c>, with
/// <c>MMO_RUNNER_CLASS</c> set. Its exit code is 0 only when this run wrote
/// <c>simcore-server-benchmarks.json</c> with every benchmark it ran, because the <c>measure</c> job reads
/// that file next.
/// </summary>
public static class Program
{
    /// <summary>The line written when the runner class is missing.</summary>
    public const string NoRunnerClassLine = "MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable";

    /// <summary>
    /// The line written when the run gave no summary: the filter selected no benchmark, or the arguments
    /// asked only for a listing or for help.
    /// </summary>
    public const string NoBenchmarkLine = "no benchmark ran, so simcore-server-benchmarks.json was not written";

    /// <summary>
    /// The line written when benchmarks ran but the export was not written. BenchmarkDotNet catches an
    /// exporter's exception and logs it, so the reason is in the log above the line.
    /// </summary>
    public const string NotExportedLine = "simcore-server-benchmarks.json was not written; the exporter's error is in the log above";

    /// <summary>The line written when a critical validation error stopped the run before any benchmark.</summary>
    public const string ValidationFailedLine = "a critical validation error stopped the run; the errors are in the log above";

    /// <summary>Runs the benchmarks with the runner class from <c>MMO_RUNNER_CLASS</c>.</summary>
    public static int Main(string[] args) =>
        Run(args, Environment.GetEnvironmentVariable("MMO_RUNNER_CLASS"), Console.Error);

    /// <summary>
    /// Runs the benchmarks <paramref name="args"/> select with <see cref="SimCoreBenchmarkConfig.Create"/>
    /// for <paramref name="runnerClass"/> and the default job.
    /// </summary>
    /// <returns>See <see cref="Run(string[], string?, TextWriter, Func{string, IConfig})"/>.</returns>
    public static int Run(string[] args, string? runnerClass, TextWriter stderr) =>
        Run(args, runnerClass, stderr, static runner => SimCoreBenchmarkConfig.Create(runner, Job.Default));

    /// <summary>
    /// Runs the benchmarks <paramref name="args"/> select with the configuration
    /// <paramref name="createConfig"/> builds for <paramref name="runnerClass"/>. <see cref="Main"/> builds it
    /// with <see cref="SimCoreBenchmarkConfig.Create"/> and the default job; the dry-run tests build it the
    /// same way with an in-process job and their own artifacts directory.
    /// </summary>
    /// <returns>
    /// 2, after writing <see cref="NoRunnerClassLine"/> to <paramref name="stderr"/>, when
    /// <paramref name="runnerClass"/> is null or empty, without running a benchmark. 1, after writing one
    /// line naming the reason to <paramref name="stderr"/>, when the run gave no summary
    /// (<see cref="NoBenchmarkLine"/>), a summary has a critical validation error
    /// (<see cref="ValidationFailedLine"/>), a report is one the export leaves out
    /// (<see cref="SimCoreMeasurementExporter.TryMeasurements"/>: no statistics, meaning a failed benchmark, or
    /// no allocation measurement), or the exporter did not write its file in this run
    /// (<see cref="NotExportedLine"/>). Else 0. An export left over from an earlier run never makes the exit
    /// code 0.
    /// </returns>
    /// <exception cref="ArgumentException">The configuration does not hold exactly one <see cref="SimCoreMeasurementExporter"/>.</exception>
    public static int Run(string[] args, string? runnerClass, TextWriter stderr, Func<string, IConfig> createConfig)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(createConfig);
        if (string.IsNullOrEmpty(runnerClass))
        {
            stderr.WriteLine(NoRunnerClassLine);
            return 2;
        }

        var config = createConfig(runnerClass);
        var exporter = ExporterOf(config);
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config).ToList();
        var failure = Failure(summaries, exporter);
        if (failure is null)
        {
            return 0;
        }

        stderr.WriteLine(failure);
        return 1;
    }

    private static SimCoreMeasurementExporter ExporterOf(IConfig config)
    {
        var exporters = config.GetExporters().OfType<SimCoreMeasurementExporter>().ToList();
        if (exporters.Count != 1)
        {
            throw new ArgumentException(
                $"The configuration holds {exporters.Count} SimCoreMeasurementExporters; it needs exactly one, as SimCoreBenchmarkConfig.Create adds.",
                nameof(config));
        }

        return exporters[0];
    }

    /// <summary>The line naming why the run did not export every benchmark it ran, or null when it did.</summary>
    private static string? Failure(List<Summary> summaries, SimCoreMeasurementExporter exporter)
    {
        if (summaries.Count == 0)
        {
            return NoBenchmarkLine;
        }

        foreach (var summary in summaries)
        {
            if (summary.HasCriticalValidationErrors)
            {
                return ValidationFailedLine;
            }

            foreach (var report in summary.Reports)
            {
                // The exporter's own rule, so every report the export leaves out fails the run.
                if (!SimCoreMeasurementExporter.TryMeasurements(report, out _, out _, out var leftOutReason))
                {
                    return $"{NameOf(report.BenchmarkCase)} {leftOutReason}, and {SimCoreMeasurementExporter.FileName} leaves it out";
                }
            }
        }

        return exporter.WrittenPath is null ? NotExportedLine : null;
    }

    /// <summary>The benchmark's method and parameters without its job, for example <c>WeaponBenchmarks.Step [Case=rifle-hold]</c>.</summary>
    private static string NameOf(BenchmarkCase benchmark) => $"{benchmark.Descriptor.DisplayInfo} {benchmark.Parameters.DisplayInfo}";
}
