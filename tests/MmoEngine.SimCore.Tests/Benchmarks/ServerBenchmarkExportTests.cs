using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BenchmarkDotNet.Validators;
using MmoEngine.SimCore.Benchmarks;
using MmoEngine.SimCore.Cli.Measure;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Benchmarks;

/// <summary>
/// US7.1, FR-030: the server build's step benchmarks report the mean and the p99 cost of one movement,
/// one weapon and one projectile step, labelled as measurements with the runner class
/// (contracts/measurement-report.md#how-the-report-is-produced, step 1, and the <c>server</c> table of
/// #fields). The dry run runs the movement, weapon and projectile benchmarks in process with
/// <see cref="BenchmarkRunner"/>, through the configuration the benchmark project's <c>Main</c> uses
/// (<see cref="SimCoreBenchmarkConfig.Create"/>), and checks the <c>server</c> object that
/// <see cref="SimCoreMeasurementExporter"/> exports. The full run on the runner class is the
/// <c>measure</c> job's (Phase 12).
/// </summary>
[Collection(BenchmarkDryRunCollection.Name)]
public sealed class ServerBenchmarkExportTests
{
    private const string RunnerClass = "test-runner";

    private const string ExportFile = "simcore-server-benchmarks.json";

    private const string Tool = "BenchmarkDotNet 0.15.8";

    private const string NoRunnerClassLine = "MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable";

    /// <summary>
    /// The entries the dry run's three benchmark classes export, in the exporter's order: step order
    /// movement, weapon, projectile, then each class's <c>Case</c> <c>[Params]</c> order.
    /// </summary>
    private static readonly (string Step, string Case)[] DryRunEntries =
    [
        ("movement", "flat-run"),
        ("movement", "wall-slide"),
        ("movement", "corner-wedge"),
        ("weapon", "rifle-hold"),
        ("projectile", "launcher-flight"),
    ];

    /// <summary>
    /// US7.1. Given a unique artifacts directory, When <c>BenchmarkRunner.Run</c> runs
    /// <c>MovementBenchmarks</c>, <c>WeaponBenchmarks</c> and <c>ProjectileBenchmarks</c> with
    /// <c>SimCoreBenchmarkConfig.Create("test-runner", Job.Dry.WithToolchain(InProcessEmitToolchain.Instance))</c>,
    /// the artifacts path set to that directory and the optimizations validator off, Then the run gives
    /// exactly one summary (the summaries are joined) without a critical validation error, and
    /// <c>&lt;dir&gt;/results/simcore-server-benchmarks.json</c> is the <c>server</c> object: tool
    /// <c>BenchmarkDotNet 0.15.8</c>, label <c>measurement</c>, runner class <c>test-runner</c>, exactly the
    /// five movement, weapon and projectile entries in order, each with <c>meanNs</c> &gt; 0,
    /// <c>p99Ns</c> &gt; 0, an integer <c>iterations</c> ≥ 1 and an integer <c>allocatedBytesPerOp</c> ≥ 0,
    /// and <c>MeasurementReportValidator.ValidateServerObject</c> finds no problem in it (check 5 and every
    /// <c>server</c> field rule).
    /// </summary>
    [Fact]
    public void DryRunExportsAValidServerObject()
    {
        using var artifacts = TempDirectories.Create("benchmark-dry-run");
        var config = SimCoreBenchmarkConfig.Create(RunnerClass, Job.Dry.WithToolchain(InProcessEmitToolchain.Instance))
            .WithArtifactsPath(artifacts.Path)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        Type[] benchmarks = [typeof(MovementBenchmarks), typeof(WeaponBenchmarks), typeof(ProjectileBenchmarks)];

        var summaries = BenchmarkRunner.Run(benchmarks, config);

        var summary = Assert.Single(summaries);
        Assert.False(
            summary.HasCriticalValidationErrors,
            "The dry run has critical validation errors: " + string.Join("; ", summary.ValidationErrors.Select(error => error.Message)));

        var exportPath = artifacts.Combine("results", ExportFile);
        Assert.True(File.Exists(exportPath), $"The dry run exported no {ExportFile}: {exportPath} does not exist");
        using var document = JsonDocument.Parse(File.ReadAllText(exportPath));
        var server = document.RootElement;
        Assert.Equal(JsonValueKind.Object, server.ValueKind);
        Assert.Equal(Tool, StringMember(server, "tool"));
        Assert.Equal("measurement", StringMember(server, "label"));
        Assert.Equal(RunnerClass, StringMember(server, "runnerClass"));

        var entries = Member(server, "benchmarks", "$");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        var exported = new List<(string Step, string Case)>();
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var path = $"benchmarks[{index}]";
            exported.Add((StringMember(entry, "step", path), StringMember(entry, "case", path)));
            var meanNs = NumberMember(entry, "meanNs", path);
            var p99Ns = NumberMember(entry, "p99Ns", path);
            var iterations = IntegerMember(entry, "iterations", path);
            var allocatedBytesPerOp = IntegerMember(entry, "allocatedBytesPerOp", path);
            Assert.True(meanNs > 0, $"{path}.meanNs is {meanNs}; it must be greater than 0");
            Assert.True(p99Ns > 0, $"{path}.p99Ns is {p99Ns}; it must be greater than 0");
            Assert.True(iterations >= 1, $"{path}.iterations is {iterations}; it must be at least 1");
            Assert.True(allocatedBytesPerOp >= 0, $"{path}.allocatedBytesPerOp is {allocatedBytesPerOp}; it must be at least 0");
            index++;
        }

        Assert.Equal(DryRunEntries, exported);
        Assert.Empty(MeasurementReportValidator.ValidateServerObject(server, RunnerClass));
    }

    /// <summary>
    /// <c>SimCoreBenchmarkConfig.Create("x", Job.Dry)</c> carries exactly one
    /// <see cref="SimCoreMeasurementExporter"/>, a column whose <c>Id</c> and <c>ColumnName</c> are both
    /// <c>P99</c>, <see cref="MemoryDiagnoser.Default"/> among its diagnosers, <see cref="ConfigOptions.JoinSummary"/>
    /// in its options (so every benchmark class lands in one export file), and exactly the one job passed.
    /// </summary>
    [Fact]
    public void ConfigCarriesTheExporterP99ColumnMemoryDiagnoserAndJoinedSummary()
    {
        var job = Job.Dry;

        var config = SimCoreBenchmarkConfig.Create("x", job);

        Assert.Single(config.GetExporters().OfType<SimCoreMeasurementExporter>());
        Assert.Contains(ColumnsOf(config), column => column.Id == "P99" && column.ColumnName == "P99");
        Assert.Contains(config.GetDiagnosers(), diagnoser => ReferenceEquals(diagnoser, MemoryDiagnoser.Default));
        Assert.True(
            (config.Options & ConfigOptions.JoinSummary) == ConfigOptions.JoinSummary,
            $"The configuration's options are {config.Options}; ConfigOptions.JoinSummary must be set");
        Assert.Same(job, Assert.Single(config.GetJobs()));
    }

    /// <summary>
    /// A benchmark run without its runner class is not usable (contracts/measurement-report.md#how-the-report-is-produced,
    /// step 1): <c>Program.Run([], runnerClass, stderr)</c> with the runner class null or empty returns 2
    /// before any benchmark runs, and its stderr holds exactly the line
    /// <c>MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable</c>. The runner
    /// class is passed in, so the test never sets a process environment variable.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MainExitsTwoWithoutARunnerClass(string? runnerClass)
    {
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var exit = MmoEngine.SimCore.Benchmarks.Program.Run([], runnerClass, stderr);

        Assert.Equal(2, exit);
        Assert.Equal(NoRunnerClassLine + Environment.NewLine, stderr.ToString());
    }

    /// <summary>
    /// The columns <paramref name="config"/>'s column providers give for an empty summary, which has no
    /// benchmark, report or validation error.
    /// </summary>
    private static List<IColumn> ColumnsOf(IConfig config)
    {
        var empty = new Summary(
            "columns",
            ImmutableArray<BenchmarkReport>.Empty,
            HostEnvironmentInfo.GetCurrent(),
            string.Empty,
            string.Empty,
            TimeSpan.Zero,
            CultureInfo.InvariantCulture,
            ImmutableArray<ValidationError>.Empty,
            ImmutableArray<IColumnHidingRule>.Empty);
        return config.GetColumnProviders().SelectMany(provider => provider.GetColumns(empty)).ToList();
    }

    private static JsonElement Member(JsonElement element, string name, string path)
    {
        Assert.True(
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out _),
            $"{path} has no member {name}: {element.GetRawText()}");
        return element.GetProperty(name);
    }

    private static string StringMember(JsonElement element, string name, string path = "$")
    {
        var value = Member(element, name, path);
        Assert.True(value.ValueKind == JsonValueKind.String, $"{path}.{name} is {value.GetRawText()}; it must be a string");
        return value.GetString() ?? string.Empty;
    }

    private static double NumberMember(JsonElement element, string name, string path)
    {
        var value = Member(element, name, path);
        Assert.True(value.ValueKind == JsonValueKind.Number, $"{path}.{name} is {value.GetRawText()}; it must be a number");
        return value.GetDouble();
    }

    private static long IntegerMember(JsonElement element, string name, string path)
    {
        var value = Member(element, name, path);
        Assert.True(
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            $"{path}.{name} is {value.GetRawText()}; it must be an integer");
        return value.GetInt64();
    }
}
