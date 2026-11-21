using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The server benchmarks' entry point
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced, step 1):
/// <c>dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'</c>, with
/// <c>MMO_RUNNER_CLASS</c> set.
/// </summary>
public static class Program
{
    /// <summary>The line written when the runner class is missing.</summary>
    public const string NoRunnerClassLine = "MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable";

    /// <summary>Runs the benchmarks with the runner class from <c>MMO_RUNNER_CLASS</c>.</summary>
    public static int Main(string[] args) =>
        Run(args, Environment.GetEnvironmentVariable("MMO_RUNNER_CLASS"), Console.Error);

    /// <summary>
    /// Runs the benchmarks <paramref name="args"/> select with <see cref="SimCoreBenchmarkConfig.Create"/>
    /// for <paramref name="runnerClass"/> and the default job.
    /// </summary>
    /// <returns>
    /// 2, after writing <see cref="NoRunnerClassLine"/> to <paramref name="stderr"/>, when
    /// <paramref name="runnerClass"/> is null or empty, without running a benchmark; 1 when a summary has a
    /// critical validation error or a report without statistics (a failed benchmark); else 0.
    /// </returns>
    public static int Run(string[] args, string? runnerClass, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);
        if (string.IsNullOrEmpty(runnerClass))
        {
            stderr.WriteLine(NoRunnerClassLine);
            return 2;
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
            .Run(args, SimCoreBenchmarkConfig.Create(runnerClass, Job.Default));
        return AllSucceeded(summaries) ? 0 : 1;
    }

    private static bool AllSucceeded(IEnumerable<Summary> summaries)
    {
        foreach (var summary in summaries)
        {
            if (summary.HasCriticalValidationErrors)
            {
                return false;
            }

            foreach (var report in summary.Reports)
            {
                if (report.ResultStatistics is null)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
