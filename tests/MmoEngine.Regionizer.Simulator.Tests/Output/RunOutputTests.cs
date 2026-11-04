using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Output;

/// <summary>The run folder (FR-024, contracts/run-output.md).</summary>
public sealed class RunOutputTests
{
    /// <summary>The ticks.csv header exactly as contracts/run-output.md lists it, absorptions after merges.</summary>
    private const string ContractTicksHeader =
        "tick,timeSteps,regionCount,largestRegionPlayers,largestRegionShare,merges,absorptions,splits,fusions,"
        + "fusedFightPairs,sectionChurn,crossPairsDeferred,crossPairsReadOnly,bookkeepingMicros,serializedMicros,"
        + "oversizedFusion,violations,stateHash";

    private static readonly string[] SummaryMetrics =
    [
        "regionCount", "largestRegionPlayers", "largestRegionShare", "sectionChurn", "crossPairsDeferred",
        "crossPairsReadOnly", "bookkeepingMicros", "serializedMicros", "regionLifetimeSteps", "splitLatencyTicks",
    ];

    private static readonly string[] EventTypes = ["Merge", "Split", "Fusion", "RegionDied", "SplitLatency", "Violation", "TimerFault"];

    /// <summary>
    /// US2.3: Given a completed run, When its output folder is read, Then it holds a manifest (source
    /// revision, configuration hash, scenario, seed, parameters, runtime and hardware-intrinsic
    /// settings), one metrics row per tick, and a summary with p50, p95, p99 and max for each metric.
    /// </summary>
    [Fact]
    public void RunOutputHasManifestTicksEventsAndSummary()
    {
        const ulong Seed = 12;
        const int Ticks = 90;
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", Seed, Ticks));
        var dir = TestRuns.TempDir();

        var result = SimulationRunner.Run(loaded, dir, new RunOptions());

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.Equal(
            [RunFolder.EventsFile, RunFolder.ManifestFile, RunFolder.SummaryFile, RunFolder.TicksFile],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var manifest = ReadJson(dir, RunFolder.ManifestFile);
        Assert.Equal(1, manifest["formatVersion"]!.GetValue<int>());
        Assert.Equal(result.RunId, manifest["runId"]!.GetValue<string>());
        Assert.Equal("simulator", manifest["kind"]!.GetValue<string>());
        Assert.Matches("^([0-9a-f]{40}|unknown)$", manifest["gitSha"]!.GetValue<string>());
        Assert.Equal(ConfigJson.Hash(loaded.CanonicalJson), manifest["configHash"]!.GetValue<string>());
        Assert.Equal("Baseline", manifest["scenario"]!.GetValue<string>());
        Assert.Equal(Seed, manifest["seed"]!.GetValue<ulong>());
        Assert.Equal("Lockstep", manifest["scheduling"]!.GetValue<string>());
        Assert.Equal(loaded.CanonicalJson, ConfigJson.Canonical(manifest["parameters"]));
        var rates = manifest["parameters"]!["scheduling"]!;
        Assert.Equal((60, 30, 20), (rates["baseStepHz"]!.GetValue<int>(), rates["tickHz"]!.GetValue<int>(), rates["degradedTickHz"]!.GetValue<int>()));
        var runtime = manifest["runtime"]!.AsObject();
        Assert.StartsWith(".NET", runtime["framework"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(runtime["os"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(runtime["architecture"]!.GetValue<string>()));
        Assert.True(runtime["processorCount"]!.GetValue<int>() >= 1);
        Assert.False(string.IsNullOrEmpty(runtime["machineName"]!.GetValue<string>()));
        Assert.Contains("DOTNET_EnableAVX2", runtime["hardwareIntrinsics"]!.AsObject().Select(kv => kv.Key));
        Assert.NotNull(runtime["vectorIsHardwareAccelerated"]);
        Assert.Equal(RunManifest.CpuModel(), runtime["cpuModel"]!.GetValue<string>());
        Assert.NotNull(runtime["runnerClass"]);
        Assert.NotNull(manifest["startedUtc"]);
        Assert.NotNull(manifest["finishedUtc"]);
        Assert.Equal(Ticks, manifest["ticks"]!.GetValue<long>());
        Assert.Equal(1, manifest["metricsEveryTicks"]!.GetValue<int>());

        var ticksPath = Path.Combine(dir, RunFolder.TicksFile);
        Assert.Equal(ContractTicksHeader, File.ReadLines(ticksPath).First());
        Assert.Equal(ContractTicksHeader, RunFolder.TicksHeader);
        var rows = TestRuns.CsvRows(ticksPath);
        Assert.Equal(Ticks, rows.Count);
        var deferred = TestRuns.CsvColumn(ticksPath, "crossPairsDeferred");
        for (var t = 0; t < Ticks; t++)
        {
            Assert.Equal(18, rows[t].Length);
            Assert.Equal(t.ToString(CultureInfo.InvariantCulture), rows[t][0]);
            Assert.Equal(((2 * t) + 1).ToString(CultureInfo.InvariantCulture), rows[t][1]);
            Assert.Equal(t % 30 == 0, deferred[t].Length > 0);
        }

        var eventsPath = Path.Combine(dir, RunFolder.EventsFile);
        Assert.Equal(RunFolder.EventsHeader, File.ReadLines(eventsPath).First());
        var events = TestRuns.CsvRows(eventsPath);
        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Contains(e[2], EventTypes));

        var summary = ReadJson(dir, RunFolder.SummaryFile);
        Assert.Equal(1, summary["formatVersion"]!.GetValue<int>());
        Assert.True(summary["completed"]!.GetValue<bool>());
        Assert.Equal(Ticks, summary["ticks"]!.GetValue<long>());
        Assert.Null(summary["firstViolation"]);
        var metrics = summary["metrics"]!.AsObject();
        foreach (var name in SummaryMetrics)
        {
            var m = metrics[name]!.AsObject();
            Assert.Equal(["max", "mean", "p50", "p95", "p99"], m.Select(kv => kv.Key).Order(StringComparer.Ordinal));
        }

        // Percentiles are nearest rank over every recorded tick: recompute them from ticks.csv.
        foreach (var column in new[] { "regionCount", "largestRegionPlayers", "sectionChurn" })
        {
            AssertSummarizes(TestRuns.CsvColumn(ticksPath, column).Select(TestRuns.ParseDouble), metrics[column]!.AsObject());
        }

        AssertSummarizes(TestRuns.CsvColumn(ticksPath, "fusedFightPairs").Select(TestRuns.ParseDouble), summary["fusedFightPairs"]!.AsObject());
        AssertSummarizes(events.Where(e => e[2] == "RegionDied").Select(e => TestRuns.ParseDouble(e[7])), metrics["regionLifetimeSteps"]!.AsObject());

        var totals = summary["totals"]!;
        var merges = TestRuns.CsvColumn(ticksPath, "merges").Sum(long.Parse);
        var absorptions = TestRuns.CsvColumn(ticksPath, "absorptions").Sum(long.Parse);
        Assert.Equal(merges, totals["merges"]!.GetValue<long>());
        Assert.Equal(merges, events.Count(e => e[2] == "Merge"));
        Assert.Equal(absorptions, totals["absorptions"]!.GetValue<long>());
        Assert.True(absorptions > 0, $"seed {Seed}: lockstep growth should be collected by transient regions and absorbed");
        Assert.Equal((result.Merges, result.Absorptions), (merges, absorptions));
        Assert.Equal(result.FinalHash.ToString("x16", CultureInfo.InvariantCulture), TestRuns.CsvColumn(ticksPath, "stateHash")[^1]);

        var scheduling = summary["scheduling"]!;
        Assert.Equal((60, 30, 20), (scheduling["baseStepHz"]!.GetValue<int>(), scheduling["tickHz"]!.GetValue<int>(), scheduling["degradedTickHz"]!.GetValue<int>()));
        var overhead = scheduling["timerOverheadNanos"]!.GetValue<double>();
        Assert.True(double.IsFinite(overhead) && overhead >= 0, $"timer overhead {overhead} ns");
        Assert.Equal(result.TimerOverheadNanos, overhead);
        Assert.Equal(1000.0, summary["budget"]!["budgetMicros"]!.GetValue<double>());
        Assert.Contains("Infantry", summary["rates"]!["mergesPerSecondByClass"]!.AsObject().Select(kv => kv.Key));
        Assert.True(summary["wallClock"]!["seconds"]!.GetValue<double>() > 0);
    }

    /// <summary>A run at configured tick rates steps their period, and records them in the manifest and summary.</summary>
    [Theory]
    [InlineData(120, 30, 20)]
    [InlineData(60, 60, 20)]
    [InlineData(90, 30, 10)]
    public void RunAtConfiguredTickRatesStepsTheirPeriodAndRecordsThem(int baseStepHz, int tickHz, int degradedTickHz)
    {
        const ulong Seed = 8;
        const int Ticks = 30;
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", Seed, Ticks, population: 80)
            .With("scheduling.baseStepHz", baseStepHz)
            .With("scheduling.tickHz", tickHz)
            .With("scheduling.degradedTickHz", degradedTickHz));
        var dir = TestRuns.TempDir();

        var result = SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        var period = baseStepHz / tickHz;
        var steps = TestRuns.CsvColumn(Path.Combine(dir, RunFolder.TicksFile), "timeSteps");
        Assert.Equal(Enumerable.Range(1, Ticks).Select(t => ((t * period) - 1).ToString(CultureInfo.InvariantCulture)), steps);
        var parameters = ReadJson(dir, RunFolder.ManifestFile)["parameters"]!["scheduling"]!;
        Assert.Equal((baseStepHz, tickHz, degradedTickHz), (parameters["baseStepHz"]!.GetValue<int>(), parameters["tickHz"]!.GetValue<int>(), parameters["degradedTickHz"]!.GetValue<int>()));
        var summary = ReadJson(dir, RunFolder.SummaryFile);
        var scheduling = summary["scheduling"]!;
        Assert.Equal((baseStepHz, tickHz, degradedTickHz), (scheduling["baseStepHz"]!.GetValue<int>(), scheduling["tickHz"]!.GetValue<int>(), scheduling["degradedTickHz"]!.GetValue<int>()));
        var seconds = Ticks / (double)tickHz;
        Assert.Equal(summary["totals"]!["splits"]!.GetValue<long>() / seconds, summary["rates"]!["splitsPerSecond"]!.GetValue<double>());
        Assert.Equal((baseStepHz, tickHz, degradedTickHz), (result.Time.BaseStepHz, result.Time.TickHz, result.Time.DegradedTickHz));
    }

    [Fact]
    public void RowsAreWrittenEveryMetricsInterval()
    {
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", 4, 50).With("metrics.metricsEveryTicks", 10));
        var dir = TestRuns.TempDir();

        SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });

        Assert.Equal(["0", "10", "20", "30", "40"], TestRuns.CsvColumn(Path.Combine(dir, RunFolder.TicksFile), "tick"));
    }

    [Fact]
    public void IncompleteMarkerIsPresentDuringTheRunAndRemovedAfter()
    {
        var dir = TestRuns.TempDir();
        var probe = new MarkerProbe(Path.Combine(dir, RunFolder.IncompleteMarker));

        SimulationRunner.Run(TestRuns.Load(TestRuns.Small("Baseline", 3, 20)), dir, new RunOptions { Listeners = [probe], GitSha = "unknown" });

        Assert.Equal(20, probe.Seen.Count);
        Assert.All(probe.Seen, present => Assert.True(present));
        Assert.False(File.Exists(Path.Combine(dir, RunFolder.IncompleteMarker)));
    }

    /// <summary>A rerun into a finished run's folder clears it first rather than appending.</summary>
    [Fact]
    public void RerunIntoTheSameFolderStartsClean()
    {
        var dir = TestRuns.TempDir();
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", 3, 15));
        SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });
        Directory.CreateDirectory(Path.Combine(dir, "old-sub"));
        File.WriteAllText(Path.Combine(dir, "stale.txt"), "left over");

        SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });

        Assert.False(File.Exists(Path.Combine(dir, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "old-sub")));
        Assert.Equal(15, TestRuns.CsvRows(Path.Combine(dir, RunFolder.TicksFile)).Count);
    }

    /// <summary>
    /// Spec edge case (FR-024): an output folder that is non-empty but holds neither manifest.json
    /// nor _incomplete is not a run folder. The simulator refuses it with exit code 2, naming
    /// <c>--out</c>, and deletes nothing.
    /// </summary>
    [Fact]
    public void NonEmptyFolderThatIsNotARunFolderIsRefusedWithExitCodeTwoAndNothingDeleted()
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "keep"));
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(dir, "keep", "data.csv"), "1,2,3");
        var before = Snapshot(dir);
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", 3, 5));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var ex = Assert.Throws<ConfigValidationException>(() => SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" }));
        var code = Program.Run(["run", TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 5)), "--out", dir], stdout, stderr, new RunOptions { GitSha = "unknown" });

        var error = Assert.Single(ex.Errors);
        Assert.Equal("--out", error.Path);
        Assert.Contains("not a run folder", error.Message, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Contains("\"Path\":\"--out\"", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(before, Snapshot(dir));
    }

    /// <summary>A missing or empty folder, or a finished or interrupted run's folder, is accepted and cleared.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("finished-run")]
    [InlineData("interrupted-run")]
    public void RunFoldersAndEmptyFoldersAreAccepted(string variant)
    {
        const ulong Seed = 3;
        var dir = TestRuns.TempDir();
        if (variant != "missing")
        {
            Directory.CreateDirectory(dir);
        }

        if (variant == "finished-run")
        {
            File.WriteAllText(Path.Combine(dir, RunFolder.ManifestFile), "{}");
            File.WriteAllText(Path.Combine(dir, "old.csv"), "x");
        }
        else if (variant == "interrupted-run")
        {
            File.WriteAllText(Path.Combine(dir, RunFolder.IncompleteMarker), string.Empty);
            File.WriteAllText(Path.Combine(dir, RunFolder.TicksFile), "partial");
        }

        var result = SimulationRunner.Run(TestRuns.Load(TestRuns.Small("Baseline", Seed, 5)), dir, new RunOptions { GitSha = "unknown" });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.False(File.Exists(Path.Combine(dir, "old.csv")));
        Assert.Equal(
            [RunFolder.EventsFile, RunFolder.ManifestFile, RunFolder.SummaryFile, RunFolder.TicksFile],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(5, TestRuns.CsvRows(Path.Combine(dir, RunFolder.TicksFile)).Count);
    }

    [Fact]
    public void InterruptedRunKeepsItsIncompleteMarker()
    {
        var dir = TestRuns.TempDir();
        using var cancel = new CancellationTokenSource();
        var stopper = new StopAtTick(5, cancel);

        Assert.Throws<OperationCanceledException>(() => SimulationRunner.Run(
            TestRuns.Load(TestRuns.Small("Baseline", 3, 40)),
            dir,
            new RunOptions { Listeners = [stopper], CancellationToken = cancel.Token, GitSha = "unknown" }));

        Assert.True(File.Exists(Path.Combine(dir, RunFolder.IncompleteMarker)));
        Assert.False(File.Exists(Path.Combine(dir, RunFolder.SummaryFile)));
    }

    [Fact]
    public void ATypedConfigIsValidatedAndRunAndAnInvalidOneWritesNothing()
    {
        const ulong Seed = 2;
        var config = TestRuns.Load(TestRuns.Small("Baseline", Seed, 10)).Config;
        var dir = TestRuns.TempDir();
        var refused = TestRuns.TempDir();

        var result = SimulationRunner.Run(config, dir, new RunOptions { GitSha = "unknown" });
        var ex = Assert.Throws<ConfigValidationException>(() => SimulationRunner.Run(config with { DurationTicks = 0 }, refused));

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.Equal(ConfigJson.Hash(ConfigJson.Canonical(config)), result.ConfigHash);
        Assert.Equal("durationTicks", Assert.Single(ex.Errors).Path);
        Assert.False(Directory.Exists(refused));
    }

    [Fact]
    public void TheSourceRevisionIsTheGitHeadOrUnknown()
    {
        var repo = TestRuns.RepositoryRoot();
        var notARepo = TestRuns.TempDir();
        Directory.CreateDirectory(notARepo);

        Assert.Matches("^[0-9a-f]{40}$", RunManifest.GitSha(repo));
        Assert.Equal("unknown", RunManifest.GitSha(notARepo));
        Assert.Equal("unknown", RunManifest.GitSha(Path.Combine(notARepo, "missing")));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "")]
    public void EventDetailsAreCsvQuoted(string? detail, string expected)
    {
        Assert.Equal(expected, RunFolder.Csv(detail));
    }

    private static JsonObject ReadJson(string dir, string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!.AsObject();

    private static double NearestRank(double[] sorted, int percent) => sorted[(int)Math.Ceiling(percent / 100.0 * sorted.Length) - 1];

    /// <summary>The summary of <paramref name="values"/> is nearest-rank p50, p95, p99, the max and the mean, or all zero when there are none.</summary>
    private static void AssertSummarizes(IEnumerable<double> values, JsonObject summary)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            Assert.All(new[] { "p50", "p95", "p99", "max", "mean" }, key => Assert.Equal(0.0, summary[key]!.GetValue<double>()));
            return;
        }

        Assert.Equal(NearestRank(sorted, 50), summary["p50"]!.GetValue<double>());
        Assert.Equal(NearestRank(sorted, 95), summary["p95"]!.GetValue<double>());
        Assert.Equal(NearestRank(sorted, 99), summary["p99"]!.GetValue<double>());
        Assert.Equal(sorted[^1], summary["max"]!.GetValue<double>());
        Assert.Equal(sorted.Sum() / sorted.Length, summary["mean"]!.GetValue<double>(), 9);
    }

    /// <summary>Every file and directory under <paramref name="dir"/> with its contents, for checking nothing changed.</summary>
    private static List<string> Snapshot(string dir) =>
        [.. Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? $"{p}={File.ReadAllText(p)}" : p)];

    private sealed class MarkerProbe(string marker) : RunListener
    {
        public List<bool> Seen { get; } = [];

        public override void OnTick(in TickRow row) => Seen.Add(File.Exists(marker));
    }

    private sealed class StopAtTick(long tick, CancellationTokenSource cancel) : RunListener
    {
        public override void OnTick(in TickRow row)
        {
            if (row.Tick == tick)
            {
                cancel.Cancel();
            }
        }
    }

    /// <summary>Spec 014 research R5: the manifest's CPU model comes from /proc/cpuinfo, or "unknown".</summary>
    [Fact]
    public void CpuModelReadsCpuInfoOrFallsBackToUnknown()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mmo-sim-cpuinfo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cpuinfo = Path.Combine(folder, "cpuinfo");
        File.WriteAllText(cpuinfo, "processor\t: 0\nmodel name\t: Test CPU @ 1.00GHz\n");

        Assert.Equal("Test CPU @ 1.00GHz", RunManifest.CpuModel(cpuinfo));
        Assert.Equal("unknown", RunManifest.CpuModel(Path.Combine(folder, "missing")));
        File.WriteAllText(cpuinfo, "processor\t: 0\nModel without a colon\n");
        Assert.Equal("unknown", RunManifest.CpuModel(cpuinfo));
    }
}
