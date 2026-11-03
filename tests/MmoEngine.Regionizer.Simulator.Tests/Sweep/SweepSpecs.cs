using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Sweep;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Sweep;

/// <summary>
/// Sweep specifications for tests: a small base run (a 2 km map, a small population, a few ticks)
/// and explicit parameters, seeds and sweep seed. Holds no business rules.
/// </summary>
internal static class SweepSpecs
{
    /// <summary>A sweep spec as JSON, over a <see cref="TestRuns.Small"/> base.</summary>
    public static JsonObject Spec(string name, ulong sweepSeed, int seeds, JsonObject baseConfig, string mode, params (string Path, JsonArray Values)[] parameters)
    {
        var list = new JsonArray();
        foreach (var (path, values) in parameters)
        {
            list.Add(new JsonObject { ["path"] = path, ["values"] = values });
        }

        return new JsonObject
        {
            ["name"] = name,
            ["base"] = baseConfig,
            ["seeds"] = seeds,
            ["sweepSeed"] = sweepSeed,
            ["mode"] = mode,
            ["parameters"] = list,
        };
    }

    /// <summary>A small Baseline base config of <paramref name="ticks"/> ticks.</summary>
    public static JsonObject Base(long ticks, string scenario = "Baseline") => TestRuns.Small(scenario, 1, ticks);

    /// <summary>Loads a spec, failing the test on any error.</summary>
    public static SweepPlan Plan(JsonObject spec)
    {
        var result = SweepSpecLoader.Load(spec);
        Assert.True(result.Succeeded, "sweep spec should load: " + string.Join("; ", result.Errors));
        return result.Plan!;
    }

    /// <summary>Runs a plan at a given parallelism with a fixed git SHA (no git process per test).</summary>
    public static SweepOutcome Run(SweepPlan plan, string outputDirectory, int parallelism, RunOptions? options = null) =>
        SweepDriver.Run(plan, outputDirectory, new SweepRunOptions
        {
            Parallelism = parallelism,
            RunOptions = (options ?? new RunOptions()) with { GitSha = "unknown" },
        });

    /// <summary>Writes a spec to a JSON file in a new temp directory and returns its path.</summary>
    public static string Write(JsonObject spec)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sweep.json");
        File.WriteAllText(path, spec.ToJsonString());
        return path;
    }

    public static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    /// <summary>A run folder's per-tick state hashes, from <c>ticks.csv</c>.</summary>
    public static IReadOnlyList<string> Hashes(string runFolder) =>
        TestRuns.CsvColumn(Path.Combine(runFolder, RunFolder.TicksFile), "stateHash");

    /// <summary>
    /// A summary without the values that depend on wall-clock time (contracts/run-output.md): the
    /// bookkeeping and serialized costs, the budget reading, the timer-overhead calibration and the
    /// wall clock.
    /// </summary>
    public static string DeterministicSummary(string runFolder)
    {
        var summary = ReadJson(Path.Combine(runFolder, RunFolder.SummaryFile));
        summary.Remove("wallClock");
        summary["scheduling"]?.AsObject().Remove("timerOverheadNanos");
        summary["metrics"]!.AsObject().Remove("bookkeepingMicros");
        summary["metrics"]!.AsObject().Remove("serializedMicros");
        summary["budget"]!.AsObject().Remove("bookkeepingP99Micros");
        summary["budget"]!.AsObject().Remove("withinBudget");
        return summary.ToJsonString();
    }

    /// <summary>Corrupts the regionizer at <paramref name="tick"/> in runs whose seed is <paramref name="seed"/> (rule 1).</summary>
    public static Action<StepHookContext> CorruptAtTick(long tick, ulong seed) => context =>
    {
        if (context.Seed != seed || context.Step != tick * 2)
        {
            return;
        }

        var regions = context.Regionizer.RegionIds.ToArray();
        var owner = regions.First(id => context.Regionizer.GetRegion(id).SectionCount > 0);
        var other = regions.First(id => id != owner);
        context.Regionizer.TestHookListSectionInRegion(other, context.Regionizer.GetRegion(owner).Sections[0]);
    };
}
