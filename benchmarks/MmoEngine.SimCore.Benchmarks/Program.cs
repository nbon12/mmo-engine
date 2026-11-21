using BenchmarkDotNet.Running;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The benchmark entry point. T247 adds this minimal switcher so the new executable project builds;
/// T252 replaces it with <c>Run(args, runnerClass, stderr)</c>, which refuses a run without
/// <c>MMO_RUNNER_CLASS</c> and runs with <c>SimCoreBenchmarkConfig</c>.
/// </summary>
public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
