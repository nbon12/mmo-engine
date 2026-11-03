using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Analysis;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Cli;

/// <summary>
/// The commands of specs/013-regionizer-simulator/quickstart.md, run in process through
/// <c>Program.Run</c> at small sizes and in unique temp folders, with the exit codes and files the
/// quickstart says to expect (tasks T073). The portal validator is another tool; the map tests cover it.
/// </summary>
public sealed class QuickstartTests
{
    private const ulong ChecksSeed = 20261103;

    private static readonly string[] RunFiles = [RunFolder.EventsFile, RunFolder.ManifestFile, RunFolder.SummaryFile, RunFolder.TicksFile];

    /// <summary>
    /// "One run": <c>regionizer-sim run simulator/scenarios/Baseline.json --out &lt;dir&gt; --ticks 5</c> exits 0
    /// and leaves manifest.json, ticks.csv, events.csv and summary.json with no _incomplete marker; running the
    /// same command again gives identical stateHash columns.
    /// </summary>
    [Fact]
    public void OneRunExitsZeroWritesTheRunFolderAndRepeatsItsStateHashes()
    {
        var config = Path.Combine(TestRuns.RepositoryRoot(), "simulator", "scenarios", "Baseline.json");
        var first = TestRuns.TempDir();
        var second = TestRuns.TempDir();

        var (firstCode, _, firstErrors) = Invoke(["run", config, "--out", first, "--ticks", "5"]);
        var (secondCode, _, secondErrors) = Invoke(["run", config, "--out", second, "--ticks", "5"]);

        Assert.True(firstCode == ExitCodes.Clean, firstErrors);
        Assert.True(secondCode == ExitCodes.Clean, secondErrors);
        foreach (var folder in new[] { first, second })
        {
            Assert.Equal(RunFiles, Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.False(File.Exists(Path.Combine(folder, RunFolder.IncompleteMarker)));
        }

        var hashes = TestRuns.CsvColumn(Path.Combine(first, RunFolder.TicksFile), "stateHash");
        var seed = JsonNode.Parse(File.ReadAllText(Path.Combine(first, RunFolder.ManifestFile)))!["seed"]!.GetValue<ulong>();
        Assert.True(hashes.Count == 5, $"seed {seed}: {hashes.Count} ticks recorded");
        Assert.True(
            hashes.SequenceEqual(TestRuns.CsvColumn(Path.Combine(second, RunFolder.TicksFile), "stateHash")),
            $"seed {seed}: the second run's stateHash column differs");
    }

    /// <summary>
    /// "A sweep": <c>regionizer-sim sweep &lt;sweep.json&gt; --out &lt;dir&gt;</c> exits 0 with one run folder per
    /// cell and seed, plus cells.csv (and sweep.json). The spec is the committed infantry-radius sweep cut to
    /// two radii, two seeds, 20 entities and 3 ticks.
    /// </summary>
    [Fact]
    public void ASweepExitsZeroWithOneRunFolderPerCellAndSeedAndCellsCsv()
    {
        var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuns.RepositoryRoot(), "simulator", "sweeps", "infantry-radius.json")))!.AsObject();
        spec["seeds"] = 2;
        spec["parameters"]![0]!["values"] = new JsonArray(8, 12);
        var baseConfig = spec["base"]!.AsObject();
        baseConfig["durationTicks"] = 3;
        baseConfig["population"]!["total"] = 20;
        baseConfig["map"] = new JsonObject { ["widthMetres"] = 2000, ["heightMetres"] = 2000 };
        var sweepSeed = spec["sweepSeed"]!.GetValue<ulong>();
        var specPath = Path.Combine(TestRuns.TempDir(), "infantry-radius.json");
        Directory.CreateDirectory(Path.GetDirectoryName(specPath)!);
        File.WriteAllText(specPath, spec.ToJsonString());
        var output = TestRuns.TempDir();

        var (code, _, stderr) = Invoke(["sweep", specPath, "--out", output]);

        Assert.True(code == ExitCodes.Clean, $"sweep seed {sweepSeed}: {stderr}");
        var sweepDir = Path.Combine(output, "infantry-radius");
        Assert.True(File.Exists(Path.Combine(sweepDir, "sweep.json")), $"sweep seed {sweepSeed}: no sweep.json");
        string[] expected = ["c000/seed-0", "c000/seed-1", "c001/seed-0", "c001/seed-1"];
        var folders = Directory.GetDirectories(sweepDir)
            .SelectMany(Directory.GetDirectories)
            .Select(f => Path.GetRelativePath(sweepDir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);
        Assert.Equal(expected, folders);
        Assert.All(expected, run => Assert.Equal(RunFiles, Directory.GetFiles(Path.Combine(sweepDir, run)).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
        Assert.Equal(["c000/0", "c000/1", "c001/0", "c001/1"], TestRuns.CsvRows(Path.Combine(sweepDir, "cells.csv")).Select(r => $"{r[0]}/{r[1]}"));
    }

    /// <summary>
    /// "Analytic checks": <c>regionizer-sim check --out &lt;dir&gt;</c> exits 0 and checks.json shows all three
    /// checks (claim-radius floor, merge distance, lane bridging) within tolerance.
    /// </summary>
    [Fact]
    public void CheckExitsZeroWithAllThreeChecksWithinTolerance()
    {
        var output = TestRuns.TempDir();

        var (code, stdout, stderr) = Invoke(["check", "--out", output]);

        Assert.True(code == ExitCodes.Clean, $"seed {ChecksSeed}: {stdout} {stderr}");
        var checks = JsonNode.Parse(File.ReadAllText(Path.Combine(output, AnalyticChecks.FileName)))!;
        Assert.Equal(ChecksSeed, checks["seed"]!.GetValue<ulong>());
        Assert.True(checks["passed"]!.GetValue<bool>(), $"seed {ChecksSeed}: checks.json did not pass");
        foreach (var name in new[] { "claimRadiusFloor", "mergeDistance" })
        {
            var cases = checks[name]!.AsArray();
            Assert.True(cases.Count > 0, $"seed {ChecksSeed}: {name} has no cases");
            Assert.All(cases, c => Assert.True(c!["passed"]!.GetValue<bool>(), $"seed {ChecksSeed}: a {name} case is outside tolerance: {c.ToJsonString()}"));
        }

        Assert.True(checks["laneBridging"]!["passed"]!.GetValue<bool>(), $"seed {ChecksSeed}: lane bridging is outside tolerance");
    }

    /// <summary>
    /// "The memo": <c>regionizer-sim memo &lt;results&gt; --rules simulator/sweeps/memo-rules.json --out &lt;memo.md&gt;</c>
    /// over a small generated result set exits 0 and writes a memo with one row per value, none unreadable.
    /// </summary>
    [Fact]
    public void MemoExitsZeroWithARowPerValue()
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        MemoResultSet.Write(results);
        var memo = Path.Combine(root, "docs", "simulator", "decision-memo.md");
        var values = MemoResultSet.Values();

        var (code, stdout, stderr) = Invoke(["memo", results, "--rules", MemoResultSet.RulesPath(), "--out", memo]);

        Assert.True(code == ExitCodes.Clean, stderr);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.Equal(values.Count, line["values"]!.GetValue<int>());
        Assert.Empty(line["unreadable"]!.AsArray());
        var markdown = File.ReadAllText(memo);
        Assert.All(values, v => Assert.Contains(v.Title, markdown, StringComparison.Ordinal));
    }

    private static (int Code, string Stdout, string Stderr) Invoke(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr, new RunOptions { GitSha = "unknown" });
        return (code, stdout.ToString(), stderr.ToString());
    }
}
