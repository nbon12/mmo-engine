using System.Text.Json.Nodes;
using MmoEngine.Harness.Golden;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Tests.Rules;
using MmoEngine.Harness.Tests.Support;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Harness.Tests.Golden;

/// <summary>US3: golden runs prove determinism (spec 014, User Story 3). Runs are real simulator runs, made in process.</summary>
public sealed class GoldenRunTests
{
    private static readonly JsonObject TinyConfig = new()
    {
        ["scenario"] = "Baseline",
        ["seed"] = 11,
        ["durationTicks"] = 40,
        ["population"] = new JsonObject { ["total"] = 250, ["limit"] = 10000 },
        ["scheduling"] = new JsonObject { ["mode"] = "Interleaved" },
    };

    /// <summary>US3 scenario 1: a recorded golden run, replayed by the same build on the same runner class, matches every tick.</summary>
    [Fact]
    public void ReplayOnTheSameRunnerClassMatchesEveryTick()
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var replay = Simulate(TinyConfig);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.Match, comparison.Outcome);
        Assert.Equal(HarnessExitCodes.Ok, comparison.ExitCode);
        Assert.Null(comparison.FirstDivergentTick);
        Assert.Contains("all 40 ticks match", comparison.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// US3 scenario 2: a replay whose state differs from tick t on is reported at tick t, with both hashes,
    /// exit 7. Here the difference is a changed parameter (the seed), so the state differs from the first tick.
    /// </summary>
    [Fact]
    public void ReplayWithAChangedParameterIsReportedAtItsFirstDivergentTick()
    {
        var (golden, goldenRun) = RecordGolden(TinyConfig);
        var changed = TinyConfig.DeepClone().AsObject();
        changed["seed"] = 12;
        var replay = Simulate(changed);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.Diverged, comparison.Outcome);
        Assert.Equal(HarnessExitCodes.Diverged, comparison.ExitCode);
        Assert.Equal(0, comparison.FirstDivergentTick);
        Assert.Equal(TickHash(goldenRun, 0), comparison.GoldenHash);
        Assert.Equal(TickHash(replay, 0), comparison.RunHash);
        Assert.NotEqual(comparison.GoldenHash, comparison.RunHash);
        Assert.Equal("0", comparison.GoldenRow!["tick"]);
        Assert.Equal("0", comparison.RunRow!["tick"]);
    }

    /// <summary>US3 scenario 2, mid-run: state that differs only from tick 25 on is reported at tick 25, not at the end.</summary>
    [Theory]
    [InlineData(25L)]
    [InlineData(39L)]
    public void DivergenceFromTickTIsReportedAtTickT(long fromTick)
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var replay = Simulate(TinyConfig);
        RunFolders.CorruptHashesFrom(replay, fromTick);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.Diverged, comparison.Outcome);
        Assert.Equal(fromTick, comparison.FirstDivergentTick);
        Assert.Equal("0123456789abcdef", comparison.RunHash);
        Assert.Contains($"first divergent tick {fromTick}", comparison.Message, StringComparison.Ordinal);
        Assert.False(comparison.GoldenRow!.ContainsKey("bookkeepingMicros"));
    }

    /// <summary>A replay that stops early diverges at the first missing tick.</summary>
    [Fact]
    public void ShorterReplayDivergesAtItsFirstMissingTick()
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var shorter = TinyConfig.DeepClone().AsObject();
        shorter["durationTicks"] = 30;
        var replay = Simulate(shorter);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.Diverged, comparison.Outcome);
        Assert.Equal(30, comparison.FirstDivergentTick);
        Assert.Contains("ended before", comparison.Message, StringComparison.Ordinal);
    }

    /// <summary>US3 scenario 3: a golden run from another runner class is "not comparable", exit 8, naming both classes.</summary>
    [Fact]
    public void GoldenFromAnotherRunnerClassIsNotComparable()
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var goldenJson = Path.Combine(golden, "golden.json");
        var json = JsonNode.Parse(File.ReadAllText(goldenJson))!.AsObject();
        json["runnerClass"]!["id"] = "other-cpu-0000000000000000";
        File.WriteAllText(goldenJson, json.ToJsonString());
        var replay = Simulate(TinyConfig);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.NotComparable, comparison.Outcome);
        Assert.Equal(HarnessExitCodes.NotComparable, comparison.ExitCode);
        Assert.Contains("other-cpu-0000000000000000", comparison.Message, StringComparison.Ordinal);
        Assert.Contains(RunnerClass.FromManifest(RunFolderReader.Open(replay).Manifest).Id, comparison.Message, StringComparison.Ordinal);
        Assert.Null(comparison.FirstDivergentTick);
    }

    /// <summary>Edge case: a golden run in an older or newer format is not comparable, never a divergence.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void GoldenInAnotherFormatVersionIsNotComparable(int version)
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var goldenJson = Path.Combine(golden, "golden.json");
        var json = JsonNode.Parse(File.ReadAllText(goldenJson))!.AsObject();
        json["formatVersion"] = version;
        File.WriteAllText(goldenJson, json.ToJsonString());

        var comparison = GoldenRuns.Compare(golden, Simulate(TinyConfig));

        Assert.Equal(GoldenOutcome.NotComparable, comparison.Outcome);
    }

    /// <summary>Edge case: a replayed run folder in another run-output format version is not comparable.</summary>
    [Fact]
    public void ReplayInAnotherRunFormatVersionIsNotComparable()
    {
        var (golden, _) = RecordGolden(TinyConfig);
        var replay = Simulate(TinyConfig);
        RunFolders.EditManifest(replay, m => m["formatVersion"] = 2);

        var comparison = GoldenRuns.Compare(golden, replay);

        Assert.Equal(GoldenOutcome.NotComparable, comparison.Outcome);
        Assert.Contains("format version is 2", comparison.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// US3 scenario 4: a run's manifest records the runner class (CPU model, class name) and every pinned
    /// hardware-intrinsic variable, set or unset.
    /// </summary>
    [Fact]
    public void ManifestRecordsTheRunnerClassAndEveryIntrinsicVariable()
    {
        var run = Simulate(TinyConfig);
        var manifest = RunFolderReader.Open(run).Manifest;
        var runtime = manifest["runtime"]!.AsObject();

        Assert.Equal(RunnerClass.CpuModel(), runtime["cpuModel"]!.GetValue<string>());
        Assert.NotNull(runtime["runnerClass"]);
        var intrinsics = runtime["hardwareIntrinsics"]!.AsObject();
        foreach (var variable in RunnerClass.IntrinsicVariables)
        {
            Assert.Equal(Environment.GetEnvironmentVariable(variable) ?? "unset", intrinsics[variable]!.GetValue<string>());
        }

        var recorded = RunnerClass.FromManifest(manifest);
        Assert.Equal(RunnerClass.Current().Id, recorded.Id);
    }

    /// <summary>The golden folder holds the config, golden.json and the deterministic tick columns only.</summary>
    [Fact]
    public void RecordWritesConfigGoldenJsonAndDeterministicTicks()
    {
        var (golden, run) = RecordGolden(TinyConfig);

        Assert.True(File.Exists(Path.Combine(golden, "config.json")));
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(golden, "golden.json")))!;
        Assert.Equal(GoldenRuns.FormatVersion, json["formatVersion"]!.GetValue<int>());
        Assert.Equal(40, json["ticks"]!.GetValue<int>());
        Assert.Equal(RunFolderReader.Open(run).RunId, json["sourceRunId"]!.GetValue<string>());
        var header = File.ReadLines(Path.Combine(golden, "ticks.csv")).First().Split(',');
        Assert.DoesNotContain("bookkeepingMicros", header);
        Assert.DoesNotContain("serializedMicros", header);
        Assert.Contains("stateHash", header);
    }

    private static (string Golden, string Run) RecordGolden(JsonObject config)
    {
        var run = Simulate(config);
        var root = RunFolders.TempFolder();
        var configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, config.ToJsonString());
        var golden = Path.Combine(root, "golden");
        GoldenRuns.Record(run, configPath, golden, "tiny");
        return (golden, run);
    }

    /// <summary>Runs the simulator in process, writing a run folder.</summary>
    internal static string Simulate(JsonObject config)
    {
        var scenarios = Path.Combine(RuleFileParserTests.RepoRoot(), "simulator", "scenarios");
        var loaded = RunConfigLoader.Load(config.DeepClone().AsObject(), scenariosDirectory: scenarios, environmentName: "Test");
        Assert.True(loaded.Succeeded, string.Join("; ", loaded.Errors));
        var output = Path.Combine(RunFolders.TempFolder(), "run");
        var result = SimulationRunner.Run(loaded.Loaded!, output, new RunOptions { GitSha = "test" });
        Assert.Equal(0, result.ExitCode);
        return output;
    }

    private static string TickHash(string runFolder, long tick)
    {
        var lines = File.ReadAllLines(Path.Combine(runFolder, "ticks.csv"));
        var header = lines[0].Split(',');
        return lines.Skip(1).Select(l => l.Split(',')).First(f => f[0] == tick.ToString(System.Globalization.CultureInfo.InvariantCulture))[Array.IndexOf(header, "stateHash")];
    }
}
