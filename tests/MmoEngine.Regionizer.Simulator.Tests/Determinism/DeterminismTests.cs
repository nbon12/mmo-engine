using System.Diagnostics;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Determinism;

/// <summary>Reproducible runs (US2, FR-017, FR-019).</summary>
public sealed class DeterminismTests
{
    /// <summary>
    /// US2.1: Given the Baseline scenario, a seed and a fixed configuration, When it runs twice in
    /// separate processes, Then the per-tick state hashes are identical for every tick.
    /// </summary>
    [Fact]
    public void SameSeedInSeparateProcessesGivesIdenticalHashes()
    {
        const ulong Seed = 20261103;
        const int Ticks = 60;
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", Seed, Ticks));
        var first = TestRuns.TempDir();
        var second = TestRuns.TempDir();

        RunInSeparateProcess(config, first);
        RunInSeparateProcess(config, second);

        var a = TestRuns.CsvColumn(Path.Combine(first, RunFolder.TicksFile), "stateHash");
        var b = TestRuns.CsvColumn(Path.Combine(second, RunFolder.TicksFile), "stateHash");
        Assert.True(a.Count == Ticks, $"seed {Seed}: expected {Ticks} hashes, got {a.Count}");
        Assert.True(a.SequenceEqual(b), $"seed {Seed}: hash sequences differ at tick {FirstDifference(a, b)}");
        Assert.All(a, h => Assert.Matches("^[0-9a-f]{16}$", h));
    }

    /// <summary>
    /// US2.2: Given two different seeds, When Baseline runs with each, Then their per-tick state hash
    /// sequences differ.
    /// </summary>
    [Fact]
    public void DifferentSeedsGiveDifferentHashes()
    {
        const int Ticks = 40;
        var a = TestRuns.Run(TestRuns.Load(TestRuns.Small("Baseline", 1, Ticks)), new RunOptions { KeepTickHashes = true });
        var b = TestRuns.Run(TestRuns.Load(TestRuns.Small("Baseline", 2, Ticks)), new RunOptions { KeepTickHashes = true });

        Assert.Equal(Ticks, a.TickHashes.Count);
        Assert.Equal(Ticks, b.TickHashes.Count);
        Assert.True(a.TickHashes[0] != b.TickHashes[0], "seeds 1 and 2: the first tick's hashes should differ");
        Assert.False(a.TickHashes.SequenceEqual(b.TickHashes), "seeds 1 and 2: hash sequences should differ");
    }

    [Theory]
    [InlineData("Lockstep", "Section", 0)]
    [InlineData("Lockstep", "Position", 1)]
    [InlineData("Interleaved", "Section", 1)]
    [InlineData("Interleaved", "Position", 0)]
    public void SameSeedInOneProcessGivesIdenticalHashesInEveryMode(string scheduling, string anchoring, int mergeGap)
    {
        const ulong Seed = 31;
        var runConfig = TestRuns.Small("Migration", Seed, 80)
            .With("scheduling.mode", scheduling)
            .With("regionizer.anchoring", anchoring)
            .With("regionizer.mergeGap", mergeGap)
            .With("scenarioParameters.migrationEverySeconds", 1);

        var a = TestRuns.Run(TestRuns.Load(runConfig), new RunOptions { KeepTickHashes = true });
        var b = TestRuns.Run(TestRuns.Load(runConfig), new RunOptions { KeepTickHashes = true });

        Assert.True(a.Completed, $"seed {Seed}: {a.FirstViolation}");
        Assert.True(a.TickHashes.SequenceEqual(b.TickHashes), $"seed {Seed}: hashes differ at tick {FirstDifference(a.TickHashes, b.TickHashes)}");
        Assert.Equal(a.FinalHash, b.FinalHash);
    }

    /// <summary>FR-017: lockstep ticks regions in ascending ID, and each tick moves its entities in ascending ID.</summary>
    [Fact]
    public void RegionsAndEntitiesAreIteratedInIdOrder()
    {
        const ulong Seed = 8;
        var order = new TickOrderRecorder();
        var result = TestRuns.Run(
            TestRuns.Load(TestRuns.Small("Baseline", Seed, 30)),
            new RunOptions { Observers = [order], Listeners = [order] });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.True(order.RegionTicks.Count > 30, $"seed {Seed}: too few region ticks recorded");
        foreach (var (step, regions) in order.RegionsByStep)
        {
            Assert.True(regions.SequenceEqual(regions.Order()), $"seed {Seed}: regions ticked out of ID order at step {step}: {string.Join(",", regions)}");
        }

        foreach (var (region, step, entities) in order.RegionTicks)
        {
            Assert.True(entities.SequenceEqual(entities.Distinct().Order()), $"seed {Seed}: region {region} moved entities out of ID order at step {step}");
        }
    }

    private static void RunInSeparateProcess(string config, string output)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "regionizer-sim.dll");
        Assert.True(File.Exists(dll), $"{dll} should be built next to the tests");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in new[] { dll, "run", config, "--out", output })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[EnvironmentNames.VariableName] = "Test";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "the simulator process should finish");
        Assert.True(process.ExitCode == ExitCodes.Clean, $"exit code {process.ExitCode}: {stderr.Result}");
        Assert.Contains("\"exitCode\":0", stdout.Result, StringComparison.Ordinal);
    }

    private static int FirstDifference<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
            {
                return i;
            }
        }

        return a.Count == b.Count ? -1 : n;
    }

    /// <summary>Records which region ticks at each step and which entities each region tick moves.</summary>
    private sealed class TickOrderRecorder : RunListener, IRegionizerObserver
    {
        private List<int>? _current;

        public List<(long Region, long Step, List<int> Entities)> RegionTicks { get; } = [];

        public SortedDictionary<long, List<long>> RegionsByStep { get; } = [];

        public void OnRegionTickStarted(long regionId, long tickCounter, long now)
        {
            _current = [];
            RegionTicks.Add((regionId, now, _current));
            if (!RegionsByStep.TryGetValue(now, out var list))
            {
                list = [];
                RegionsByStep.Add(now, list);
            }

            list.Add(regionId);
        }

        public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update) => _current?.Add(entityId);

        public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
        {
        }

        public void OnStateChanged(long regionId, RegionState from, RegionState to)
        {
        }

        public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
        {
        }

        public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
        {
        }

        public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
        {
        }

        public void OnRegionDied(long regionId, long ageSteps)
        {
        }

        public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
        {
        }

        public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
        {
        }

        public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
        {
        }
    }
}
