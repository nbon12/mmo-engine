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
using BenchmarkProgram = MmoEngine.SimCore.Benchmarks.Program;

namespace MmoEngine.SimCore.Tests.Benchmarks;

/// <summary>
/// US7.1, FR-030: the server build's step benchmarks report the mean and the p99 cost of one movement,
/// one weapon and one projectile step, labelled as measurements with the runner class
/// (contracts/measurement-report.md#how-the-report-is-produced, step 1, and the <c>server</c> table of
/// #fields). The dry run runs the movement, weapon and projectile benchmarks in process with
/// <see cref="BenchmarkRunner"/>, through the configuration the benchmark project's <c>Main</c> uses
/// (<see cref="SimCoreBenchmarkConfig.Create"/>), and checks the <c>server</c> object that
/// <see cref="SimCoreMeasurementExporter"/> exports. The <c>Run…</c> tests run the same configuration
/// through the entry point's <c>Program.Run</c> and <c>BenchmarkSwitcher</c>, as <c>Main</c> does, and pin
/// its exit code: 0 only when this run wrote the export with every benchmark it ran. The full run on the
/// runner class is the <c>measure</c> job's (Phase 12).
/// </summary>
[Collection(BenchmarkDryRunCollection.Name)]
public sealed class ServerBenchmarkExportTests
{
    private const string RunnerClass = "test-runner";

    private const string ExportFile = "simcore-server-benchmarks.json";

    private const string Tool = "BenchmarkDotNet 0.15.8";

    private const string NoRunnerClassLine = "MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable";

    private const string NoBenchmarkLine = "no benchmark ran, so simcore-server-benchmarks.json was not written";

    private const string NotExportedLine = "simcore-server-benchmarks.json was not written; the exporter's error is in the log above";

    /// <summary>The text of an export left over from an earlier run.</summary>
    private const string LeftOverExport = "{\"left\": \"over\"}";

    /// <summary>The measured iterations of the dry run's job: at least two, so the mean and the 99th percentile differ.</summary>
    private const int DryRunIterations = 3;

    /// <summary>
    /// <c>Job.Dry</c> (cold start, one warm-up, one invocation per iteration) with three measured
    /// iterations, run in process. A cold-start job keeps every iteration (its outlier mode is
    /// <c>DontRemove</c>), so the statistics cover exactly <see cref="DryRunIterations"/>.
    /// </summary>
    private static readonly Job DryRunJob = Job.Dry.WithIterationCount(DryRunIterations).WithToolchain(InProcessEmitToolchain.Instance);

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
    /// <c>SimCoreBenchmarkConfig.Create("test-runner", Job.Dry.WithIterationCount(3).WithToolchain(InProcessEmitToolchain.Instance))</c>,
    /// the artifacts path set to that directory and the optimizations validator off, Then the run gives
    /// exactly one summary (the summaries are joined) without a critical validation error, and
    /// <c>&lt;dir&gt;/results/simcore-server-benchmarks.json</c> is the <c>server</c> object: tool
    /// <c>BenchmarkDotNet 0.15.8</c>, label <c>measurement</c>, runner class <c>test-runner</c>, exactly the
    /// five movement, weapon and projectile entries in order, each with <c>meanNs</c> &gt; 0,
    /// <c>p99Ns</c> &gt; 0, an integer <c>iterations</c> ≥ 1 and an integer <c>allocatedBytesPerOp</c> ≥ 0,
    /// and <c>MeasurementReportValidator.ValidateServerObject</c> finds no problem in it (check 5 and every
    /// <c>server</c> field rule). Each entry also carries exactly its own report's numbers, found by the
    /// report's category and <c>Case</c>: <c>meanNs</c> is <c>ResultStatistics.Mean</c>, <c>p99Ns</c> is
    /// <c>ResultStatistics.Percentiles.Percentile(99)</c>, <c>iterations</c> is <c>ResultStatistics.N</c>,
    /// which is the job's 3, and <c>allocatedBytesPerOp</c> is <c>GcStats.GetBytesAllocatedPerOperation</c>.
    /// The job measures three iterations, not <c>Job.Dry</c>'s one, because with one iteration N is 1 and the
    /// 99th percentile is the mean, so a wrong statistic in either field would pass. <c>Utf8JsonWriter</c>
    /// writes a double in its shortest round-trip form, so the numbers compare exactly.
    /// </summary>
    [Fact]
    public void DryRunExportsAValidServerObject()
    {
        using var artifacts = TempDirectories.Create("benchmark-dry-run");
        var config = DryRunConfig(RunnerClass, artifacts.Path);
        Type[] benchmarks = [typeof(MovementBenchmarks), typeof(WeaponBenchmarks), typeof(ProjectileBenchmarks)];

        var summaries = BenchmarkRunner.Run(benchmarks, config);

        var summary = Assert.Single(summaries);
        Assert.False(
            summary.HasCriticalValidationErrors,
            "The dry run has critical validation errors: " + string.Join("; ", summary.ValidationErrors.Select(error => error.Message)));
        var reports = ReportsByEntry(summary);
        Assert.Equal(DryRunEntries.OrderBy(entry => entry), reports.Keys.OrderBy(entry => entry));

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
            var key = (StringMember(entry, "step", path), StringMember(entry, "case", path));
            exported.Add(key);
            var meanNs = NumberMember(entry, "meanNs", path);
            var p99Ns = NumberMember(entry, "p99Ns", path);
            var iterations = IntegerMember(entry, "iterations", path);
            var allocatedBytesPerOp = IntegerMember(entry, "allocatedBytesPerOp", path);
            Assert.True(meanNs > 0, $"{path}.meanNs is {meanNs}; it must be greater than 0");
            Assert.True(p99Ns > 0, $"{path}.p99Ns is {p99Ns}; it must be greater than 0");
            Assert.True(iterations >= 1, $"{path}.iterations is {iterations}; it must be at least 1");
            Assert.True(allocatedBytesPerOp >= 0, $"{path}.allocatedBytesPerOp is {allocatedBytesPerOp}; it must be at least 0");

            Assert.True(reports.TryGetValue(key, out var report), $"{path} is {key}, which no report of the summary has");
            var statistics = report.ResultStatistics;
            Assert.NotNull(statistics);
            Assert.Equal(statistics.Mean, meanNs);
            Assert.Equal(statistics.Percentiles.Percentile(99), p99Ns);
            Assert.Equal(statistics.N, iterations);
            Assert.Equal(DryRunIterations, iterations);
            Assert.Equal(report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase), allocatedBytesPerOp);
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

        var exit = BenchmarkProgram.Run([], runnerClass, stderr);

        Assert.Equal(2, exit);
        Assert.Equal(NoRunnerClassLine + Environment.NewLine, stderr.ToString());
    }

    /// <summary>
    /// The entry point's success (contracts/measurement-report.md#how-the-report-is-produced, step 1):
    /// <c>Program.Run(["--filter", "*MovementBenchmarks*", "*WeaponBenchmarks*", "*ProjectileBenchmarks*"], "test-runner", stderr, createConfig)</c>,
    /// with the configuration <see cref="SimCoreBenchmarkConfig.Create"/> builds for the dry run's job in a
    /// unique artifacts directory, runs the movement, weapon and projectile benchmarks through
    /// <c>BenchmarkSwitcher</c> as <c>Main</c> does, returns 0 with nothing on stderr, and has written
    /// <c>&lt;dir&gt;/results/simcore-server-benchmarks.json</c> with exactly the five entries of the dry run,
    /// in order, and runner class <c>test-runner</c>, which
    /// <c>MeasurementReportValidator.ValidateServerObject</c> finds valid.
    /// </summary>
    [Fact]
    public void RunExitsZeroWhenItExportedEveryBenchmarkItRan()
    {
        using var artifacts = TempDirectories.Create("benchmark-run");
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        string[] args = ["--filter", "*MovementBenchmarks*", "*WeaponBenchmarks*", "*ProjectileBenchmarks*"];

        var exit = BenchmarkProgram.Run(args, RunnerClass, stderr, runner => DryRunConfig(runner, artifacts.Path));

        Assert.True(exit == 0, $"Program.Run exited {exit}, expected 0; stderr: {stderr}");
        Assert.Equal(string.Empty, stderr.ToString());
        var exportPath = artifacts.Combine("results", ExportFile);
        Assert.True(File.Exists(exportPath), $"The run exported no {ExportFile}: {exportPath} does not exist");
        using var document = JsonDocument.Parse(File.ReadAllText(exportPath));
        var server = document.RootElement;
        Assert.Equal(RunnerClass, StringMember(server, "runnerClass"));
        var exported = new List<(string Step, string Case)>();
        var index = 0;
        foreach (var entry in Member(server, "benchmarks", "$").EnumerateArray())
        {
            var path = $"benchmarks[{index}]";
            exported.Add((StringMember(entry, "step", path), StringMember(entry, "case", path)));
            index++;
        }

        Assert.Equal(DryRunEntries, exported);
        Assert.Empty(MeasurementReportValidator.ValidateServerObject(server, RunnerClass));
    }

    /// <summary>
    /// The entry point exits 1 when this run wrote no export, because the <c>measure</c> job reads the file
    /// next (contracts/measurement-report.md#how-the-report-is-produced, step 1). Each row runs
    /// <c>Program.Run</c> with the dry run's configuration in a unique artifacts directory and expects exit
    /// 1 with exactly its line on stderr:
    /// <list type="bullet">
    ///   <item><c>*NothingMatches*</c> selects no benchmark, so BenchmarkDotNet gives no summary. An export
    ///   left over from an earlier run is in the results directory, and it is left as it was: the exit code
    ///   still says nothing was written.</item>
    ///   <item><c>*WeaponBenchmarks*</c> runs, but a directory occupies the export's path, so the exporter's
    ///   rename throws. BenchmarkDotNet logs an exporter's exception and goes on, so only the exporter's
    ///   <see cref="SimCoreMeasurementExporter.WrittenPath"/> shows it.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("*NothingMatches*", false, NoBenchmarkLine)]
    [InlineData("*WeaponBenchmarks*", true, NotExportedLine)]
    public void RunExitsOneWhenItWroteNoExport(string filter, bool directoryAtExportPath, string expectedLine)
    {
        using var artifacts = TempDirectories.Create("benchmark-run");
        var exportPath = artifacts.Combine("results", ExportFile);
        if (directoryAtExportPath)
        {
            Directory.CreateDirectory(exportPath);
        }
        else
        {
            Directory.CreateDirectory(artifacts.Combine("results"));
            File.WriteAllText(exportPath, LeftOverExport);
        }

        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var exit = BenchmarkProgram.Run(["--filter", filter], RunnerClass, stderr, runner => DryRunConfig(runner, artifacts.Path));

        Assert.True(exit == 1, $"Program.Run with --filter {filter} exited {exit}, expected 1; stderr: {stderr}");
        Assert.Equal(expectedLine + Environment.NewLine, stderr.ToString());
        if (directoryAtExportPath)
        {
            Assert.True(Directory.Exists(exportPath), $"{exportPath} is no longer the directory the row put there");
        }
        else
        {
            Assert.Equal(LeftOverExport, File.ReadAllText(exportPath));
        }
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

    /// <summary>
    /// The entry point exits 1 when a report has statistics but no allocation measurement, because the
    /// export leaves such a report out (<c>allocatedBytesPerOp</c> is a required integer), so the file the
    /// run wrote does not hold every benchmark it ran. The configuration is the dry run's without
    /// <c>MemoryDiagnoser</c>, under which <c>GcStats.GetBytesAllocatedPerOperation</c> is null: exit 1, and
    /// stderr is exactly
    /// <c>WeaponBenchmarks.Step [Case=rifle-hold] has no allocation measurement, and simcore-server-benchmarks.json leaves it out</c>.
    /// </summary>
    [Fact]
    public void RunExitsOneWhenAReportHasNoAllocationMeasurement()
    {
        using var artifacts = TempDirectories.Create("benchmark-run");
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var exit = BenchmarkProgram.Run(
            ["--filter", "*WeaponBenchmarks*"],
            RunnerClass,
            stderr,
            runner => ManualConfig.Create(DefaultConfig.Instance)
                .AddJob(DryRunJob)
                .AddExporter(new SimCoreMeasurementExporter(runner))
                .WithOptions(ConfigOptions.JoinSummary | ConfigOptions.DisableOptimizationsValidator)
                .WithArtifactsPath(artifacts.Path));

        Assert.True(exit == 1, $"Program.Run without MemoryDiagnoser exited {exit}, expected 1; stderr: {stderr}");
        Assert.Equal(
            "WeaponBenchmarks.Step [Case=rifle-hold] has no allocation measurement, and simcore-server-benchmarks.json leaves it out" + Environment.NewLine,
            stderr.ToString());
    }

    /// <summary>
    /// The dry run's configuration for <paramref name="runnerClass"/>: <see cref="SimCoreBenchmarkConfig.Create"/>
    /// with <see cref="DryRunJob"/>, the artifacts in <paramref name="artifactsPath"/> and the optimizations
    /// validator off, because the tests run a Debug build.
    /// </summary>
    private static IConfig DryRunConfig(string runnerClass, string artifactsPath) =>
        SimCoreBenchmarkConfig.Create(runnerClass, DryRunJob)
            .WithArtifactsPath(artifactsPath)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);

    /// <summary>
    /// <paramref name="summary"/>'s reports by the export entry each one gives: its single category (the
    /// step) and its <c>Case</c> parameter. Each key is asserted unique, so no entry can match two reports.
    /// </summary>
    private static Dictionary<(string Step, string Case), BenchmarkReport> ReportsByEntry(Summary summary)
    {
        var reports = new Dictionary<(string Step, string Case), BenchmarkReport>();
        foreach (var report in summary.Reports)
        {
            var benchmark = report.BenchmarkCase;
            var step = Assert.Single(benchmark.Descriptor.Categories);
            var caseName = Assert.IsType<string>(Assert.Single(benchmark.Parameters.Items, parameter => parameter.Name == "Case").Value);
            Assert.True(reports.TryAdd((step, caseName), report), $"Two reports of the summary are {step}/{caseName}");
        }

        return reports;
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
