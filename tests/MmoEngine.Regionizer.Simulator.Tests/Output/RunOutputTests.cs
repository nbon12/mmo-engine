using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Output;

/// <summary>The run folder (FR-024, contracts/run-output.md).</summary>
public sealed class RunOutputTests
{
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
    public void CompletedRunFolderHoldsManifestOneRowPerTickEventsAndSummary()
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
        var runtime = manifest["runtime"]!.AsObject();
        Assert.StartsWith(".NET", runtime["framework"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(runtime["os"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(runtime["architecture"]!.GetValue<string>()));
        Assert.True(runtime["processorCount"]!.GetValue<int>() >= 1);
        Assert.False(string.IsNullOrEmpty(runtime["machineName"]!.GetValue<string>()));
        Assert.Contains("DOTNET_EnableAVX2", runtime["hardwareIntrinsics"]!.AsObject().Select(kv => kv.Key));
        Assert.NotNull(runtime["vectorIsHardwareAccelerated"]);
        Assert.NotNull(manifest["startedUtc"]);
        Assert.NotNull(manifest["finishedUtc"]);
        Assert.Equal(Ticks, manifest["ticks"]!.GetValue<long>());
        Assert.Equal(1, manifest["metricsEveryTicks"]!.GetValue<int>());

        var ticksPath = Path.Combine(dir, RunFolder.TicksFile);
        Assert.Equal(RunFolder.TicksHeader, File.ReadLines(ticksPath).First());
        var rows = TestRuns.CsvRows(ticksPath);
        Assert.Equal(Ticks, rows.Count);
        for (var t = 0; t < Ticks; t++)
        {
            Assert.Equal(16, rows[t].Length);
            Assert.Equal(t.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[t][0]);
            Assert.Equal(((2 * t) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), rows[t][1]);
            Assert.Equal(t % 30 == 0, rows[t][9].Length > 0);
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
        foreach (var (column, metric) in new[] { ("regionCount", "regionCount"), ("largestRegionPlayers", "largestRegionPlayers"), ("sectionChurn", "sectionChurn") })
        {
            var values = TestRuns.CsvColumn(ticksPath, column).Select(TestRuns.ParseDouble).Order().ToArray();
            Assert.Equal(NearestRank(values, 50), metrics[metric]!["p50"]!.GetValue<double>());
            Assert.Equal(NearestRank(values, 95), metrics[metric]!["p95"]!.GetValue<double>());
            Assert.Equal(NearestRank(values, 99), metrics[metric]!["p99"]!.GetValue<double>());
            Assert.Equal(values[^1], metrics[metric]!["max"]!.GetValue<double>());
        }

        var merges = TestRuns.CsvColumn(ticksPath, "merges").Sum(long.Parse);
        Assert.Equal(merges, summary["totals"]!["merges"]!.GetValue<long>());
        Assert.Equal(merges, events.Count(e => e[2] == "Merge"));
        Assert.Equal(result.FinalHash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), rows[^1][15]);
        Assert.Equal(1000.0, summary["budget"]!["budgetMicros"]!.GetValue<double>());
        Assert.Contains("Infantry", summary["rates"]!["mergesPerSecondByClass"]!.AsObject().Select(kv => kv.Key));
        Assert.NotNull(summary["fusedFightPairs"]!["p95"]);
        Assert.True(summary["wallClock"]!["seconds"]!.GetValue<double>() > 0);
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

    [Fact]
    public void RerunIntoTheSameFolderStartsClean()
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "old-sub"));
        File.WriteAllText(Path.Combine(dir, "stale.txt"), "left over");
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", 3, 15));

        SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });
        SimulationRunner.Run(loaded, dir, new RunOptions { GitSha = "unknown" });

        Assert.False(File.Exists(Path.Combine(dir, "stale.txt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "old-sub")));
        Assert.Equal(15, TestRuns.CsvRows(Path.Combine(dir, RunFolder.TicksFile)).Count);
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
}
