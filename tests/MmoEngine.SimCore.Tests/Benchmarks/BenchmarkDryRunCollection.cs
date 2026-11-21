namespace MmoEngine.SimCore.Tests.Benchmarks;

/// <summary>
/// Runs the benchmark dry-run tests one at a time, after the parallel tests (FR-030, US7.1).
/// BenchmarkDotNet's in-process toolchain runs the benchmarks on the test process's own thread, times
/// them, and raises the process and thread priority while it runs: a test sharing the process with
/// parallel tests would have their work land in its timings, and its raised priority would starve them.
/// Every test class that runs BenchmarkDotNet in process is
/// <c>[Collection(BenchmarkDryRunCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BenchmarkDryRunCollection
{
    public const string Name = "Benchmark dry run (in-process BenchmarkDotNet)";
}
