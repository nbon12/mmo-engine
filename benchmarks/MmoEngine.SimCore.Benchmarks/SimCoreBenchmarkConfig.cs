using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The configuration of the server benchmarks
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced, step 1).
/// The benchmark project's <c>Main</c> runs with it, and <c>ServerBenchmarkExportTests</c>' dry run calls
/// the same method.
/// </summary>
public static class SimCoreBenchmarkConfig
{
    /// <summary>
    /// <see cref="DefaultConfig.Instance"/> with exactly <paramref name="job"/>, the
    /// <see cref="SimCoreMeasurementExporter"/> for <paramref name="runnerClass"/>, the
    /// <see cref="P99Column"/>, <see cref="MemoryDiagnoser.Default"/> and
    /// <see cref="ConfigOptions.JoinSummary"/>, which joins every benchmark class into one summary and so
    /// one export file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="runnerClass"/> or <paramref name="job"/> is null.</exception>
    public static IConfig Create(string runnerClass, Job job)
    {
        ArgumentNullException.ThrowIfNull(runnerClass);
        ArgumentNullException.ThrowIfNull(job);
        return ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddExporter(new SimCoreMeasurementExporter(runnerClass))
            .AddColumn(P99Column.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);
    }
}
