using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Sweep;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Sweep;

/// <summary><c>regionizer-sim sweep</c> exit codes and outputs, and how the driver reports runs that violate or fail.</summary>
public sealed class SweepCommandTests
{
    [Fact]
    public void CleanSweepExitsZeroAndWritesTheIndexTheMemoReads()
    {
        var spec = SweepSpecs.Spec("cli-clean", 5, 2, SweepSpecs.Base(20), "OneAtATime", ("regionizer.anchoring", ["Section", "Position"]));
        var output = TestRuns.TempDir();

        var (code, stdout, stderr) = Invoke(["sweep", SweepSpecs.Write(spec), "--out", output, "--parallelism", "2"]);

        Assert.True(code == ExitCodes.Clean, stderr);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.Equal("cli-clean", line["sweep"]!.GetValue<string>());
        Assert.Equal(4, line["runs"]!.GetValue<int>());
        Assert.Equal(4, line["completed"]!.GetValue<int>());
        Assert.Equal(0, line["violations"]!.GetValue<int>());
        Assert.Equal(Path.Combine(output, "cli-clean", "sweep.json"), line["index"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(output, "cli-clean", "sweep.json.tmp")));

        // The memo generator reads the sweep: every run is complete and consistent with its manifest.
        var read = SweepResults.Read(output, "cli-clean");
        Assert.Equal("Baseline", read.Scenario);
        Assert.Equal(["Section", "Position"], read.Cells.Select(c => SweepResults.Label(c.Parameters.Single().Value)));
        Assert.All(read.Cells, c => Assert.Equal(2, c.Runs.Count));

        var logs = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(logs, l => l.Contains("Sweep {Sweep} finished", StringComparison.Ordinal));
        Assert.Equal(4, logs.Count(l => l.Contains("finished: hash", StringComparison.Ordinal)));
    }

    [Fact]
    public void CellsCsvHasOneRowPerRunWithTheSweptValuesAndSummaryMetrics()
    {
        var spec = SweepSpecs.Spec("cells", 5, 2, SweepSpecs.Base(20), "OneAtATime", ("regionizer.anchoring", ["Section", "Position"]));
        var outcome = SweepSpecs.Run(SweepSpecs.Plan(spec), TestRuns.TempDir(), parallelism: 2);

        var lines = File.ReadAllLines(outcome.CellsPath);

        var header = lines[0].Split(',');
        Assert.Equal(CellsCsv.Header(["regionizer.anchoring"]), lines[0]);
        Assert.Equal(["cellId", "seedIndex", "seed", "runId", "completed", "regionizer.anchoring", "regionCount.p50"], header[..7]);
        Assert.Equal(CellsCsv.BookkeepingColumn, header[^1]);
        Assert.Equal(5, lines.Length);
        for (var r = 0; r < outcome.Runs.Count; r++)
        {
            var run = outcome.Runs[r];
            var row = lines[r + 1].Split(',');
            var summary = SweepSpecs.ReadJson(Path.Combine(run.FolderPath, RunFolder.SummaryFile));
            var label = $"{run.Run.Cell.CellId} seed {run.Run.Seed}";
            Assert.Equal(header.Length, row.Length);
            Assert.Equal([run.Run.Cell.CellId, $"{run.Run.SeedIndex}", $"{run.Run.Seed}", run.RunId, "true"], row[..5]);
            Assert.Equal(r < 2 ? "Section" : "Position", row[5]);
            Assert.True(summary["metrics"]!["regionCount"]!["p99"]!.ToJsonString() == row[Array.IndexOf(header, "regionCount.p99")], label);
            Assert.True(summary["metrics"]!["largestRegionShare"]!["max"]!.ToJsonString() == row[Array.IndexOf(header, "largestRegionShare.max")], label);
            Assert.True(summary["totals"]!["merges"]!.ToJsonString() == row[Array.IndexOf(header, "merges")], label);
            Assert.True(summary["totals"]!["oversizedFusionTicks"]!.ToJsonString() == row[Array.IndexOf(header, "oversizedFusionTicks")], label);
            Assert.True(summary["fusedFightPairs"]!["p50"]!.ToJsonString() == row[Array.IndexOf(header, "fusedFightPairs.p50")], label);
            Assert.True(summary["budget"]!["bookkeepingP99Micros"]!.ToJsonString() == row[^1], label);
        }
    }

    [Fact]
    public void ARunWhoseSummaryCannotBeReadLeavesItsMetricsEmpty()
    {
        var spec = SweepSpecs.Spec("unreadable", 5, 2, SweepSpecs.Base(10), "OneAtATime", ("regionizer.anchoring", ["Section"]));
        var plan = SweepSpecs.Plan(spec);
        var outcome = SweepSpecs.Run(plan, TestRuns.TempDir(), parallelism: 1);
        File.WriteAllText(Path.Combine(outcome.Runs[1].FolderPath, RunFolder.SummaryFile), "{ not json");

        CellsCsv.Write(outcome.CellsPath, plan, outcome.Runs);

        var rows = TestRuns.CsvRows(outcome.CellsPath);
        Assert.NotEqual(string.Empty, rows[0][^1]);
        Assert.Equal("true", rows[1][4]);
        Assert.All(rows[1][6..], field => Assert.Equal(string.Empty, field));
    }

    /// <summary>
    /// OUTSIDE-BUG: contracts/run-output.md gives <c>summary.json</c>'s <c>fusedFightPairs</c> the same
    /// shape as every metric (p50, p95, p99, max, mean), but Output/RunSummary.cs writes only p50, p95
    /// and max, so cells.csv cannot report the fused-fight-pairs p99 the sweep table needs.
    /// </summary>
    // OUTSIDE-BUG: Output/RunSummary.cs omits fusedFightPairs.p99 and .mean from summary.json (contracts/run-output.md).
    [Fact]
    public void CellsCsvReportsTheFusedFightPairsP99FromTheSummary()
    {
        var spec = SweepSpecs.Spec("fused-p99", 5, 1, SweepSpecs.Base(20), "OneAtATime", ("regionizer.anchoring", ["Section"]));
        var outcome = SweepSpecs.Run(SweepSpecs.Plan(spec), TestRuns.TempDir(), parallelism: 1);

        var p99 = TestRuns.CsvColumn(outcome.CellsPath, "fusedFightPairs.p99");

        Assert.True(p99.Single().Length > 0, $"seed {outcome.Runs[0].Run.Seed}: summary.json has no fusedFightPairs.p99");
    }

    [Fact]
    public void AViolationInAnyRunExitsThreeAndTheOtherRunsStillRun()
    {
        var spec = SweepSpecs.Spec("cli-violation", 8, 2, SweepSpecs.Base(20), "OneAtATime", ("regionizer.mergeGap", [0, 1]));
        var plan = SweepSpecs.Plan(spec);
        var violatingSeed = plan.Seeds[1];
        var output = TestRuns.TempDir();

        var (code, stdout, stderr) = Invoke(["sweep", SweepSpecs.Write(spec), "--out", output], new RunOptions { AfterStep = SweepSpecs.CorruptAtTick(3, violatingSeed) });

        Assert.Equal(ExitCodes.Violation, code);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.Equal(2, line["violations"]!.GetValue<int>());
        Assert.Equal(2, line["completed"]!.GetValue<int>());
        Assert.Contains("stopped: {Kind} rule {Rule}", stderr, StringComparison.Ordinal);
        var index = SweepSpecs.ReadJson(Path.Combine(output, "cli-violation", "sweep.json"));
        foreach (var cell in index["cells"]!.AsArray())
        {
            Assert.True(cell!["runs"]![0]!["completed"]!.GetValue<bool>());
            Assert.False(cell["runs"]![1]!["completed"]!.GetValue<bool>(), $"seed {violatingSeed} violated but is listed as completed");
        }

        var completed = TestRuns.CsvColumn(Path.Combine(output, "cli-violation", "cells.csv"), "completed");
        Assert.Equal(["true", "false", "true", "false"], completed);
    }

    [Fact]
    public void ARunThatFailsIsReportedAndTheSweepExitsOne()
    {
        var spec = SweepSpecs.Spec("failing", 8, 2, SweepSpecs.Base(10), "OneAtATime", ("regionizer.mergeGap", [0]));
        var plan = SweepSpecs.Plan(spec);
        var failingSeed = plan.Seeds[0];
        var options = new RunOptions
        {
            MovementSourceFactory = (world, config) => config.Seed == failingSeed ? throw new InvalidOperationException("no movement") : world.Movement,
        };

        var outcome = SweepSpecs.Run(plan, TestRuns.TempDir(), parallelism: 2, options);

        Assert.Equal(ExitCodes.Error, outcome.ExitCode);
        Assert.Equal(1, outcome.Errors);
        Assert.Equal(1, outcome.Completed);
        var failed = outcome.Runs[0];
        Assert.Equal("InvalidOperationException: no movement", failed.Error);
        Assert.Null(failed.FinalHash);
        Assert.False(failed.Completed);
        var row = TestRuns.CsvRows(outcome.CellsPath)[0];
        Assert.Equal("false", row[4]);
        Assert.All(row[6..], field => Assert.Equal(string.Empty, field));
    }

    [Fact]
    public void AViolationOutranksAFailureInTheExitCode()
    {
        var spec = SweepSpecs.Spec("mixed", 8, 2, SweepSpecs.Base(20), "OneAtATime", ("regionizer.mergeGap", [0]));
        var plan = SweepSpecs.Plan(spec);
        var options = new RunOptions
        {
            AfterStep = SweepSpecs.CorruptAtTick(2, plan.Seeds[1]),
            MovementSourceFactory = (world, config) => config.Seed == plan.Seeds[0] ? throw new InvalidOperationException("no movement") : world.Movement,
        };

        var outcome = SweepSpecs.Run(plan, TestRuns.TempDir(), parallelism: 1, options);

        Assert.Equal(ExitCodes.Violation, outcome.ExitCode);
        Assert.Equal(1, outcome.Errors);
        Assert.Equal(1, outcome.Violations);
        Assert.Equal(1, outcome.Runs[1].FirstViolation!.RuleNumber);
    }

    /// <summary>
    /// A sweep too long for one machine runs as shards: each runs every n-th run into the run's usual folder, so
    /// together the shards hold every run exactly once, and each shard's index is one the memo can read.
    /// </summary>
    [Fact]
    public void ShardsTogetherRunEveryRunOnceInTheirUsualFolders()
    {
        var spec = SweepSpecs.Write(SweepSpecs.Spec("sharded", 7, 2, SweepSpecs.Base(20), "OneAtATime", ("regionizer.anchoring", ["Section", "Position"]), ("regionizer.mergeGap", [1])));
        var first = TestRuns.TempDir();
        var second = TestRuns.TempDir();

        var a = Invoke(["sweep", spec, "--out", first, "--parallelism", "2", "--shard", "1/2"]);
        var b = Invoke(["sweep", spec, "--out", second, "--parallelism", "2", "--shard", "2/2"]);

        Assert.True(a.Code == ExitCodes.Clean, a.Stderr);
        Assert.True(b.Code == ExitCodes.Clean, b.Stderr);
        Assert.Equal("1/2", JsonNode.Parse(a.Stdout.Trim())!["shard"]!.GetValue<string>());
        static List<string> Folders(string root) =>
            [.. SweepResults.Read(root, "sharded").Cells.SelectMany(c => c.Runs.Select(r => $"{c.CellId}/seed-{r.SeedIndex}"))];
        var inFirst = Folders(first);
        var inSecond = Folders(second);
        Assert.Equal(3, inFirst.Count);
        Assert.Equal(3, inSecond.Count);
        Assert.Empty(inFirst.Intersect(inSecond));
        Assert.Equal(
            ["c000/seed-0", "c000/seed-1", "c001/seed-0", "c001/seed-1", "c002/seed-0", "c002/seed-1"],
            inFirst.Concat(inSecond).Order(StringComparer.Ordinal));
        Assert.All(inFirst, f => Assert.True(File.Exists(Path.Combine(first, "sharded", f, "summary.json"))));
    }

    [Fact]
    public void ShardOfOneIsTheWholePlanAndShardsRenumberTheirRuns()
    {
        var plan = SweepSpecLoader.Load(SweepSpecs.Spec("plan", 3, 3, SweepSpecs.Base(10), "OneAtATime", ("regionizer.mergeGap", [0, 1]))).Plan!;

        Assert.Equal(plan.Runs.Select(r => r.Folder), plan.Shard(1, 1).Runs.Select(r => r.Folder));
        var third = plan.Shard(3, 3);
        Assert.Equal([0, 1], third.Runs.Select(r => r.Index));
        Assert.Equal([plan.Runs[2].Folder, plan.Runs[5].Folder], third.Runs.Select(r => r.Folder));
        Assert.Equal(plan.Cells, third.Cells);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.Shard(4, 3));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("text")]
    [InlineData("missing-out")]
    [InlineData("missing-spec")]
    [InlineData("absent-file")]
    [InlineData("shard-zero")]
    [InlineData("shard-past-count")]
    [InlineData("shard-text")]
    [InlineData("shard-no-count")]
    public void InvalidArgumentsExitTwo(string variant)
    {
        var spec = SweepSpecs.Write(SweepSpecs.Spec("args", 1, 1, SweepSpecs.Base(10), "OneAtATime", ("regionizer.mergeGap", [0])));
        var output = TestRuns.TempDir();
        string[] args = variant switch
        {
            "zero" => ["sweep", spec, "--out", output, "--parallelism", "0"],
            "negative" => ["sweep", spec, "--out", output, "--parallelism", "-2"],
            "text" => ["sweep", spec, "--out", output, "--parallelism", "many"],
            "missing-out" => ["sweep", spec],
            "missing-spec" => ["sweep", "--out", output],
            "shard-zero" => ["sweep", spec, "--out", output, "--shard", "0/2"],
            "shard-past-count" => ["sweep", spec, "--out", output, "--shard", "3/2"],
            "shard-text" => ["sweep", spec, "--out", output, "--shard", "a/b"],
            "shard-no-count" => ["sweep", spec, "--out", output, "--shard", "1"],
            _ => ["sweep", Path.Combine(TestRuns.TempDir(), "none.json"), "--out", output],
        };

        var (code, stdout, stderr) = Invoke(args);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Equal(string.Empty, stdout);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void AnOutputPathThatIsAFileExitsOne()
    {
        var spec = SweepSpecs.Write(SweepSpecs.Spec("blocked", 1, 1, SweepSpecs.Base(10), "OneAtATime", ("regionizer.mergeGap", [0])));
        var file = Path.Combine(Path.GetDirectoryName(spec)!, "not-a-directory");
        File.WriteAllText(file, "x");

        var (code, stdout, stderr) = Invoke(["sweep", spec, "--out", file]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("\"@l\":\"Fatal\"", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenariosOptionPointsAtTheDefaultsDirectory()
    {
        var spec = SweepSpecs.Write(SweepSpecs.Spec("scenarios-dir", 1, 1, SweepSpecs.Base(5), "OneAtATime", ("regionizer.mergeGap", [0])));
        var empty = TestRuns.TempDir();
        Directory.CreateDirectory(empty);

        var (missing, _, stderr) = Invoke(["sweep", spec, "--out", TestRuns.TempDir(), "--scenarios", empty]);
        var (found, _, _) = Invoke(["sweep", spec, "--out", TestRuns.TempDir(), "--scenarios", Path.Combine(TestRuns.RepositoryRoot(), "simulator", "scenarios")]);

        Assert.Equal(ExitCodes.InvalidConfig, missing);
        Assert.Contains("base.scenario", stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Clean, found);
    }

    [Fact]
    public void SweepCommandIsRegisteredWithHelp()
    {
        Assert.Contains("sweep", Simulator.Cli.Commands.CliCommands.All().Select(c => c.Name));

        var (code, stdout, _) = Invoke(["sweep", "--help"]);

        Assert.Equal(ExitCodes.Clean, code);
        Assert.Contains("--parallelism", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ParallelismBelowOneIsRefusedByTheDriver()
    {
        var plan = SweepSpecs.Plan(SweepSpecs.Spec("p0", 1, 1, SweepSpecs.Base(5), "OneAtATime", ("regionizer.mergeGap", [0])));

        Assert.Throws<ArgumentOutOfRangeException>(() => SweepDriver.Run(plan, TestRuns.TempDir(), new SweepRunOptions { Parallelism = 0 }));
    }

    [Fact]
    public void ACancelledSweepStopsAndLeavesAnIndexOfUnfinishedRuns()
    {
        var plan = SweepSpecs.Plan(SweepSpecs.Spec("cancelled", 1, 2, SweepSpecs.Base(5), "OneAtATime", ("regionizer.mergeGap", [0, 1])));
        var output = TestRuns.TempDir();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SweepSpecs.Run(plan, output, parallelism: 2, new RunOptions { CancellationToken = cancel.Token }));

        var index = SweepSpecs.ReadJson(Path.Combine(output, "cancelled", "sweep.json"));
        Assert.All(index["cells"]!.AsArray(), cell => Assert.All(cell!["runs"]!.AsArray(), run => Assert.False(run!["completed"]!.GetValue<bool>())));
        Assert.False(File.Exists(Path.Combine(output, "cancelled", "cells.csv")));
    }

    [Fact]
    public void TheIndexIsValidJsonWhileRunsAreInProgress()
    {
        var spec = SweepSpecs.Spec("progress", 2, 2, SweepSpecs.Base(10), "OneAtATime", ("regionizer.mergeGap", [0, 1]));
        var plan = SweepSpecs.Plan(spec);
        var output = TestRuns.TempDir();
        var indexPath = Path.Combine(output, "progress", "sweep.json");
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
        var options = new RunOptions
        {
            AfterStep = context =>
            {
                if (context.Step == 0)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                    seen.Add(doc.RootElement.GetProperty("cells").GetArrayLength());
                }
            },
        };

        var outcome = SweepSpecs.Run(plan, output, parallelism: 2, options);

        Assert.Equal(ExitCodes.Clean, outcome.ExitCode);
        Assert.Equal(4, seen.Count);
        Assert.All(seen, cells => Assert.Equal(2, cells));
    }

    private static (int Code, string Stdout, string Stderr) Invoke(string[] args, RunOptions? options = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr, (options ?? new RunOptions()) with { GitSha = "unknown" });
        return (code, stdout.ToString(), stderr.ToString());
    }
}
