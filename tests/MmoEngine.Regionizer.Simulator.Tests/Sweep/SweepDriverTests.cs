using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Sweep;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Sweep;

/// <summary>The sweep driver (US4, FR-025, contracts/config-and-cli.md).</summary>
public sealed class SweepDriverTests
{
    private const string RadiusPath = "regionizer.claimRadius.infantry";

    /// <summary>
    /// US4.2: Given a sweep of infantry claim radius over 8 to 40 m in 4 m steps with 10 seeds per cell,
    /// When the sweep driver runs it, Then it produces exactly one result per cell and seed (90 run
    /// folders and 90 cells.csv rows), and each result's manifest reproduces that result's hash sequence.
    /// The scenario's nine radii and ten seeds are kept; each run is 20 entities for 3 ticks, so the
    /// whole sweep stays fast.
    /// </summary>
    [Fact]
    public void SweepProducesOneReproducibleResultPerCellAndSeed()
    {
        const ulong SweepSeed = 20261103;
        const int Ticks = 3;
        const int Seeds = 10;
        double[] radii = [8, 12, 16, 20, 24, 28, 32, 36, 40];
        var baseConfig = TestRuns.Small("Baseline", 1, Ticks, population: 20);
        var values = new JsonArray();
        foreach (var radius in radii)
        {
            values.Add(radius);
        }

        var spec = SweepSpecs.Spec("infantry-radius", SweepSeed, Seeds, baseConfig, "OneAtATime", (RadiusPath, values));
        var plan = SweepSpecs.Plan(spec);
        var output = TestRuns.TempDir();

        var outcome = SweepSpecs.Run(plan, output, parallelism: 4);

        Assert.True(outcome.ExitCode == ExitCodes.Clean, $"sweep seed {SweepSeed}: exit code {outcome.ExitCode}, {string.Join("; ", outcome.Runs.Select(r => r.Error ?? r.FirstViolation?.ToString()))}");
        var sweepDir = Path.Combine(output, "infantry-radius");
        string[] cellIds = [.. Enumerable.Range(0, radii.Length).Select(c => $"c{c:000}")];
        Assert.Equal(cellIds, Directory.GetDirectories(sweepDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var runFolders = Directory.GetDirectories(sweepDir).SelectMany(Directory.GetDirectories).ToList();
        Assert.True(runFolders.Count == radii.Length * Seeds, $"sweep seed {SweepSeed}: {runFolders.Count} run folders, expected {radii.Length * Seeds}");

        var index = SweepSpecs.ReadJson(Path.Combine(sweepDir, "sweep.json"));
        var cells = index["cells"]!.AsArray();
        Assert.Equal(radii.Length, cells.Count);
        string[] seedFolders = [.. Enumerable.Range(0, Seeds).Select(i => $"seed-{i}")];
        for (var c = 0; c < cells.Count; c++)
        {
            var cell = cells[c]!;
            var cellId = cellIds[c];
            Assert.Equal(cellId, cell["cellId"]!.GetValue<string>());
            Assert.Equal(radii[c], cell["parameters"]![RadiusPath]!.GetValue<double>());
            Assert.Single(cell["parameters"]!.AsObject());
            Assert.Equal(seedFolders.Order(StringComparer.Ordinal), Directory.GetDirectories(Path.Combine(sweepDir, cellId)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var runs = cell["runs"]!.AsArray();
            Assert.Equal(Seeds, runs.Count);
            for (var i = 0; i < runs.Count; i++)
            {
                var seed = DeterministicRandom.SweepSeed(SweepSeed, i);
                var run = runs[i]!;
                Assert.Equal(i, run["seedIndex"]!.GetValue<int>());
                Assert.Equal(seed, run["seed"]!.GetValue<ulong>());
                Assert.Equal($"{cellId}/seed-{i}", run["folder"]!.GetValue<string>());
                Assert.True(run["completed"]!.GetValue<bool>(), $"{cellId} seed {seed}: not completed");

                var folder = Path.Combine(sweepDir, cellId, $"seed-{i}");
                Assert.False(File.Exists(Path.Combine(folder, RunFolder.IncompleteMarker)), $"{cellId} seed {seed}: still incomplete");
                var recorded = SweepSpecs.Hashes(folder);
                Assert.True(recorded.Count == Ticks, $"{cellId} seed {seed}: {recorded.Count} ticks recorded");

                // The manifest alone reproduces the run: its parameters are the full effective configuration.
                var manifest = SweepSpecs.ReadJson(Path.Combine(folder, RunFolder.ManifestFile));
                Assert.Equal(seed, manifest["seed"]!.GetValue<ulong>());
                Assert.Equal(radii[c], manifest["parameters"]!["regionizer"]!["claimRadius"]!["infantry"]!.GetValue<double>());
                var rerun = TestRuns.Run(TestRuns.Load(manifest["parameters"]!.AsObject()));
                Assert.True(manifest["configHash"]!.GetValue<string>() == rerun.ConfigHash, $"{cellId} seed {seed}: the manifest's rerun has config hash {rerun.ConfigHash}");
                Assert.True(recorded.SequenceEqual(SweepSpecs.Hashes(rerun.OutputDirectory)), $"{cellId} seed {seed}: the manifest's rerun gives other hashes");
            }
        }

        var rows = TestRuns.CsvRows(Path.Combine(sweepDir, "cells.csv"));
        Assert.True(rows.Count == radii.Length * Seeds, $"sweep seed {SweepSeed}: cells.csv has {rows.Count} rows, expected {radii.Length * Seeds}");
        Assert.Equal(
            cellIds.SelectMany(cellId => Enumerable.Range(0, Seeds).Select(i => $"{cellId}/{i}")),
            rows.Select(r => $"{r[0]}/{r[1]}"));
    }

    /// <summary>
    /// US4.3: Given the same sweep, When it runs once on one worker and once on several, Then every
    /// cell and seed produces the same hash sequence and the same summary, apart from wall-clock
    /// measurements such as bookkeeping cost.
    /// </summary>
    [Fact]
    public void SweepResultsDoNotDependOnParallelism()
    {
        const ulong SweepSeed = 77;
        var baseConfig = SweepSpecs.Base(40).With("scheduling.mode", "Interleaved").With("scheduling.dropTo20HzProbability", 0.2);
        JsonObject Spec() => SweepSpecs.Spec("parallelism", SweepSeed, 2, baseConfig.DeepClone().AsObject(), "OneAtATime", (RadiusPath, [8, 16, 24]));
        var one = TestRuns.TempDir();
        var four = TestRuns.TempDir();

        var serial = SweepSpecs.Run(SweepSpecs.Plan(Spec()), one, parallelism: 1);
        var parallel = SweepSpecs.Run(SweepSpecs.Plan(Spec()), four, parallelism: 4);

        Assert.True(serial.ExitCode == ExitCodes.Clean && parallel.ExitCode == ExitCodes.Clean, $"sweep seed {SweepSeed}: exit codes {serial.ExitCode} and {parallel.ExitCode}");
        Assert.Equal(6, serial.Runs.Count);
        Assert.Equal(serial.Runs.Count, parallel.Runs.Count);
        for (var r = 0; r < serial.Runs.Count; r++)
        {
            var a = serial.Runs[r];
            var b = parallel.Runs[r];
            var label = $"{a.Run.Cell.CellId} seed {a.Run.Seed}";
            Assert.Equal(a.Run.Folder, b.Run.Folder);
            Assert.Equal(a.RunId, b.RunId);
            Assert.True(a.FinalHash == b.FinalHash, $"{label}: final hashes differ");
            Assert.True(SweepSpecs.Hashes(a.FolderPath).SequenceEqual(SweepSpecs.Hashes(b.FolderPath)), $"{label}: hash sequences differ");
            Assert.True(SweepSpecs.DeterministicSummary(a.FolderPath) == SweepSpecs.DeterministicSummary(b.FolderPath), $"{label}: summaries differ");
            Assert.True(
                File.ReadAllText(Path.Combine(a.FolderPath, RunFolder.EventsFile)) == File.ReadAllText(Path.Combine(b.FolderPath, RunFolder.EventsFile)),
                $"{label}: events differ");
        }

        Assert.Equal(File.ReadAllText(serial.IndexPath), File.ReadAllText(parallel.IndexPath));
        Assert.Equal(WithoutLastColumn(serial.CellsPath), WithoutLastColumn(parallel.CellsPath));
    }

    /// <summary>FR-025: seed <c>i</c> is the same in every cell, so cells differ only by their swept values.</summary>
    [Fact]
    public void SeedIIsTheSameInEveryCell()
    {
        const ulong SweepSeed = 4242;
        // The target fight size only labels oversized fusions, so the simulated state of seed i must match across cells.
        var spec = SweepSpecs.Spec("common-seeds", SweepSeed, 2, SweepSpecs.Base(30), "OneAtATime", ("metrics.targetFightSize", [100, 600, 2400]));
        var plan = SweepSpecs.Plan(spec);

        var outcome = SweepSpecs.Run(plan, TestRuns.TempDir(), parallelism: 3);

        Assert.Equal(ExitCodes.Clean, outcome.ExitCode);
        var index = SweepSpecs.ReadJson(outcome.IndexPath);
        Assert.Equal(SweepSeed, index["sweepSeed"]!.GetValue<ulong>());
        var seeds = index["seeds"]!.AsArray();
        Assert.Equal(2, seeds.Count);
        for (var i = 0; i < seeds.Count; i++)
        {
            var expected = DeterministicRandom.SweepSeed(SweepSeed, i);
            Assert.Equal(i, seeds[i]!["index"]!.GetValue<int>());
            Assert.Equal(expected, seeds[i]!["seed"]!.GetValue<ulong>());
            var runs = outcome.Runs.Where(r => r.Run.SeedIndex == i).ToList();
            Assert.Equal(3, runs.Count);
            Assert.All(runs, r =>
            {
                Assert.Equal(expected, r.Run.Seed);
                Assert.Equal(expected, r.Run.Loaded.Config.Seed);
                Assert.Equal(expected, SweepSpecs.ReadJson(Path.Combine(r.FolderPath, RunFolder.ManifestFile))["seed"]!.GetValue<ulong>());
            });
            Assert.All(index["cells"]!.AsArray(), cell => Assert.Equal(expected, cell!["runs"]![i]!["seed"]!.GetValue<ulong>()));

            var first = SweepSpecs.Hashes(runs[0].FolderPath);
            Assert.All(runs, r => Assert.True(SweepSpecs.Hashes(r.FolderPath).SequenceEqual(first), $"seed {expected}: {r.Run.Cell.CellId} simulated a different world"));
        }

        Assert.NotEqual(seeds[0]!["seed"]!.GetValue<ulong>(), seeds[1]!["seed"]!.GetValue<ulong>());
        Assert.False(
            SweepSpecs.Hashes(outcome.Runs[0].FolderPath).SequenceEqual(SweepSpecs.Hashes(outcome.Runs[1].FolderPath)),
            $"sweep seed {SweepSeed}: seeds 0 and 1 should simulate different worlds");
    }

    /// <summary>Grid mode is the cartesian product, first parameter slowest; every run's configuration carries its cell's values.</summary>
    [Fact]
    public void GridExpansionCoversTheCartesianProductInAFixedOrder()
    {
        var spec = SweepSpecs.Spec(
            "grid",
            9,
            2,
            SweepSpecs.Base(10),
            "Grid",
            ("regionizer.sectionSize", [8, 16]),
            ("regionizer.anchoring", ["Section", "Position"]),
            ("regionizer.mergeGap", [0, 1]));

        var plan = SweepSpecs.Plan(spec);

        string[] expected =
        [
            "8/Section/0", "8/Section/1", "8/Position/0", "8/Position/1",
            "16/Section/0", "16/Section/1", "16/Position/0", "16/Position/1",
        ];
        Assert.Equal(expected, plan.Cells.Select(c => string.Join('/', c.Assignments.Select(a => a.Value!.ToString()))));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => $"c{i:000}"), plan.Cells.Select(c => c.CellId));
        Assert.All(plan.Cells, c => Assert.Equal(["regionizer.sectionSize", "regionizer.anchoring", "regionizer.mergeGap"], c.Assignments.Select(a => a.Path)));
        Assert.Equal(16, plan.Runs.Count);
        for (var r = 0; r < plan.Runs.Count; r++)
        {
            var run = plan.Runs[r];
            Assert.Equal(r, run.Index);
            Assert.Equal(plan.Cells[r / 2], run.Cell);
            Assert.Equal(r % 2, run.SeedIndex);
            var config = run.Loaded.Config.Regionizer;
            Assert.Equal(expected[r / 2], $"{config.SectionSize}/{config.Anchoring}/{config.MergeGap}");
        }
    }

    /// <summary>FR-026: an invalid sweep specification stops the sweep with exit code 2, naming the field, before any run.</summary>
    [Theory]
    [InlineData("seeds", "seeds")]
    [InlineData("mode", "mode")]
    [InlineData("swept-value", "parameters[0].values[1]")]
    [InlineData("swept-seed", "parameters[0].path")]
    [InlineData("base", "base.population.total")]
    [InlineData("unknown-field", "colour")]
    public void InvalidSweepSpecExitsTwoNamingThePath(string variant, string path)
    {
        var spec = SweepSpecs.Spec("invalid", 1, 1, SweepSpecs.Base(10), "OneAtATime", ("regionizer.sectionSize", [8, 12]));
        spec["parameters"]![0]!["values"] = variant == "swept-value" ? new JsonArray(8, 12) : new JsonArray(8, 16);
        switch (variant)
        {
            case "seeds":
                spec["seeds"] = 0;
                break;
            case "mode":
                spec["mode"] = 7;
                break;
            case "swept-seed":
                spec["parameters"]![0]!["path"] = "seed";
                break;
            case "base":
                spec["base"]!["population"]!["total"] = -1;
                break;
            case "unknown-field":
                spec["colour"] = "blue";
                break;
        }

        var output = TestRuns.TempDir();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = Program.Run(["sweep", SweepSpecs.Write(spec), "--out", output], stdout, stderr, new RunOptions { GitSha = "unknown" });

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Contains($"\"Path\":\"{path}\"", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.False(Directory.Exists(output), $"{variant}: the sweep wrote output");
    }

    private static string[] WithoutLastColumn(string csv) =>
        [.. File.ReadAllLines(csv).Select(line => line[..line.LastIndexOf(',')])];
}
