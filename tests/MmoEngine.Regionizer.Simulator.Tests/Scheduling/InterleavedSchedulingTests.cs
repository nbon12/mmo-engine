using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scheduling;

/// <summary>Lockstep and interleaved scheduling (FR-018, research D7) and the location queue.</summary>
public sealed class InterleavedSchedulingTests
{
    /// <summary>
    /// US3.1: Given the Migration scenario in interleaved mode with a seed, When it runs, Then at least
    /// one region ticks at 20 Hz and at least one merge lands on a ticking region, the run ends with
    /// zero invariant violations, and a rerun with the same seed gives identical per-tick hashes.
    /// </summary>
    [Fact]
    public void MigrationInterleavedHitsTwentyHzAndMergeLaterWithZeroViolations()
    {
        const ulong Seed = 20261103;
        var runConfig = TestRuns.Small("Migration", Seed, 600, population: 240)
            .With("scheduling.mode", "Interleaved")
            .With("scheduling.dropTo20HzProbability", 0.05)
            .With("scenarioParameters.migrationEverySeconds", 5);
        var loaded = TestRuns.Load(runConfig);

        var first = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true });
        var rerun = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true });

        Assert.True(first.FirstViolation is null, $"seed {Seed}: {first.FirstViolation}");
        Assert.True(first.Completed && first.ExitCode == ExitCodes.Clean, $"seed {Seed}: run did not complete cleanly");
        Assert.True(first.Scheduling.TwentyHzTicks > 0, $"seed {Seed}: no region ticked at 20 Hz");
        Assert.True(first.MergesRequestedAgainstTicking > 0, $"seed {Seed}: no merge was requested against a ticking region");
        Assert.True(first.InvariantChecks >= 600, $"seed {Seed}: only {first.InvariantChecks} invariant checks ran");
        Assert.True(first.TickHashes.Count == 600 && first.TickHashes.SequenceEqual(rerun.TickHashes), $"seed {Seed}: rerun hashes differ");
    }

    [Fact]
    public void LockstepKeepsThirtyHertzAndNeverMergesIntoATickingRegion()
    {
        const ulong Seed = 20261103;
        var runConfig = TestRuns.Small("Migration", Seed, 300, population: 240)
            .With("scheduling.dropTo20HzProbability", 1)
            .With("scenarioParameters.migrationEverySeconds", 5);

        var result = TestRuns.Run(TestRuns.Load(runConfig));

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.True(result.Merges > 0, $"seed {Seed}: expected merges");
        Assert.Equal(0, result.Scheduling.TwentyHzTicks);
        Assert.Equal(0, result.MergesRequestedAgainstTicking);
    }

    [Fact]
    public void InterleavedEventsFormAValidOrderWithEachDueRegionStartedAndEndedOnce()
    {
        var host = new RecordingHost();
        var scheduler = new InterleavedScheduler(Scheduling(0, 1, 1), new DeterministicRandom(9));
        long[] regions = [1, 2, 3, 4, 5, 6];

        for (var step = 0L; step < 12; step++)
        {
            scheduler.RunStep(step, regions, host);
        }

        foreach (var step in host.Events.Select(e => e.Step).Distinct())
        {
            var events = host.Events.Where(e => e.Step == step).ToList();
            foreach (var id in regions)
            {
                var starts = events.FindIndex(e => e.Region == id && e.Start);
                var ends = events.FindIndex(e => e.Region == id && !e.Start);
                Assert.Equal(starts < 0, ends < 0);
                Assert.True(starts < ends || starts < 0, $"region {id} ended before it started at step {step}");
                Assert.True(events.Count(e => e.Region == id) is 0 or 2);
            }
        }

        // The seeded order really interleaves: at step 0 some region starts while another is ticking.
        var open = 0;
        var maxOpen = 0;
        foreach (var e in host.Events.Where(e => e.Step == 0))
        {
            open += e.Start ? 1 : -1;
            maxOpen = Math.Max(maxOpen, open);
        }

        Assert.True(maxOpen > 1, "seed 9: no region started while another was ticking");
        Assert.Equal([0L, 2, 4, 6, 8, 10], host.Events.Where(e => e.Region == 1 && e.Start).Select(e => e.Step));
    }

    [Fact]
    public void ADropTo20HzSpacesTicksThreeStepsApartForTheDrawnNumberOfTicks()
    {
        // Probability 1 and exactly 2 slow ticks: 30 Hz at step 0 (drop drawn at its end), then
        // ticks every 3 steps; each time the slow ticks run out, the next tick end drops again.
        var host = new RecordingHost();
        var scheduler = new InterleavedScheduler(Scheduling(1, 2, 2), new DeterministicRandom(1));

        for (var step = 0L; step <= 14; step++)
        {
            scheduler.RunStep(step, [7], host);
        }

        Assert.Equal([0L, 2, 5, 8, 11, 14], host.Events.Where(e => e.Start).Select(e => e.Step));
        Assert.Equal(5, scheduler.Stats.TwentyHzTicks);
        Assert.Equal(3, scheduler.Stats.Drops);
        Assert.Equal(17, scheduler.NextDueOf(7));
        Assert.Equal(-1, scheduler.NextDueOf(8));
    }

    [Fact]
    public void ManyDueRegionsAreAllScheduledInBothModes()
    {
        long[] regions = [.. Enumerable.Range(1, 40).Select(i => (long)i)];
        var interleavedHost = new RecordingHost();
        var lockstepHost = new RecordingHost();

        new InterleavedScheduler(Scheduling(0, 1, 1), new DeterministicRandom(3)).RunStep(0, regions, interleavedHost);
        new LockstepScheduler().RunStep(0, regions, lockstepHost);

        Assert.Equal(regions, interleavedHost.Events.Where(e => e.Start).Select(e => e.Region).Order());
        Assert.Equal(regions, lockstepHost.Events.Where(e => e.Start).Select(e => e.Region));
    }

    [Theory]
    [InlineData(SchedulingMode.Lockstep, typeof(LockstepScheduler))]
    [InlineData(SchedulingMode.Interleaved, typeof(InterleavedScheduler))]
    public void TheConfiguredModePicksTheScheduler(SchedulingMode mode, Type expected)
    {
        var scheduler = TickSchedulers.Create(Scheduling(0, 1, 1) with { Mode = mode }, 1);

        Assert.IsType(expected, scheduler);
        Assert.Equal(mode, scheduler.Mode);
        Assert.Equal(mode == SchedulingMode.Interleaved, scheduler.Observer is not null);
    }

    [Fact]
    public void AnUnknownModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TickSchedulers.Create(Scheduling(0, 1, 1) with { Mode = (SchedulingMode)9 }, 1));
    }

    [Theory]
    [InlineData(0L, 0L, true)]
    [InlineData(1L, 0L, false)]
    [InlineData(2L, 1L, true)]
    [InlineData(7L, 3L, false)]
    public void TheTimeBaseCountsSixtyHertzStepsAndThirtyHertzTicks(long step, long tick, bool firstStepOfTick)
    {
        Assert.Equal(tick, TimeBase.TickOf(step));
        Assert.Equal(firstStepOfTick, TimeBase.FirstStepOf(tick) == step);
        Assert.Equal(!firstStepOfTick, TimeBase.EndsTick(step));
    }

    [Theory]
    [InlineData(0L, 1L)]
    [InlineData(10L, 10L)]
    [InlineData(10L, 0L)]
    public void PortalCyclesNeedAnOpenTimeShorterThanTheCycle(long every, long open)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortalCycleEvents(1, every, open));
    }

    [Fact]
    public void ARegionThatCannotStartIsRetriedNextStepAndNeverEnded()
    {
        var host = new RecordingHost { Refuse = 3 };
        var scheduler = new InterleavedScheduler(Scheduling(0, 1, 1), new DeterministicRandom(2));

        scheduler.RunStep(0, [3], host);
        host.Refuse = 0;
        scheduler.RunStep(1, [3], host);

        Assert.Equal([(1L, true), (1L, false)], host.Events.Where(e => e.Region == 3).Select(e => (e.Step, e.Start)));
    }

    [Fact]
    public void LockstepTicksEveryRegionInIdOrderOnEvenSteps()
    {
        var host = new RecordingHost();
        var scheduler = new LockstepScheduler();

        for (var step = 0L; step < 4; step++)
        {
            scheduler.RunStep(step, [2, 5, 9], host);
        }

        Assert.Equal(
            [(0L, 2L, true), (0, 2, false), (0, 5, true), (0, 5, false), (0, 9, true), (0, 9, false), (2, 2, true), (2, 2, false), (2, 5, true), (2, 5, false), (2, 9, true), (2, 9, false)],
            host.Events.Select(e => (e.Step, e.Region, e.Start)));
    }

    [Fact]
    public void ArrivalsWaitAStepAndGoToTheirDestinationsOwner()
    {
        var queue = new LocationQueue();
        var owner = new Dictionary<double, long> { [10] = 0, [20] = 4, [30] = 4 };
        var completed = new List<int>();
        queue.Enqueue(new Arrival(1, 5, 0, 10, 0));
        queue.Enqueue(new Arrival(2, 5, 0, 20, 0));
        queue.Enqueue(new Arrival(3, 5, 0, 30, 0));

        Assert.Equal(0, queue.CompleteUnowned(5, a => owner[a.X], a => completed.Add(a.EntityId)));
        Assert.Equal(1, queue.CompleteUnowned(6, a => owner[a.X], a => completed.Add(a.EntityId)));
        Assert.Equal([1], completed);

        owner[30] = 8;
        Assert.Equal(1, queue.CompleteForRegion(4, a => owner[a.X], a => completed.Add(a.EntityId)));
        Assert.Equal([1, 2], completed);
        Assert.Equal(1, queue.Count);

        Assert.Equal(0, queue.CompleteUnowned(7, a => owner[a.X], a => completed.Add(a.EntityId)));
        Assert.Equal(1, queue.CompleteForRegion(8, a => owner[a.X], a => completed.Add(a.EntityId)));
        Assert.Equal([1, 2, 3], completed);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(1L, true)]
    [InlineData(19L, true)]
    [InlineData(20L, false)]
    [InlineData(59L, false)]
    [InlineData(60L, true)]
    [InlineData(80L, false)]
    public void PortalCyclesOpenAtTheCycleStartAndCloseAfterTheOpenTime(long step, bool openAfter)
    {
        var actions = new PortalRecorder();
        var cycle = new PortalCycleEvents(3, 60, 20);

        for (var s = 0L; s <= step; s++)
        {
            cycle.OnStepBoundary(s, actions);
        }

        Assert.Equal(openAfter, actions.Open);
    }

    private static SchedulingConfig Scheduling(double p, int min, int max) =>
        new() { Mode = SchedulingMode.Interleaved, DropTo20HzProbability = p, DropTicksMin = min, DropTicksMax = max };

    private sealed class RecordingHost : ITickHost
    {
        private long _step;

        public List<(long Step, long Region, bool Start)> Events { get; } = [];

        /// <summary>A region that refuses to start (as a transient one would).</summary>
        public long Refuse { get; set; }

        public bool StartTick(long regionId, long step, int periodSteps)
        {
            _step = step;
            if (regionId == Refuse)
            {
                return false;
            }

            Events.Add((step, regionId, true));
            return true;
        }

        public TickEndResult EndTick(long regionId)
        {
            Events.Add((_step, regionId, false));
            return new TickEndResult(RegionState.Ready, 0, 0);
        }
    }

    private sealed class PortalRecorder : IStepBoundaryActions
    {
        public bool Open { get; private set; }

        public long Step => 0;

        public Regionizer Regionizer => throw new NotSupportedException();

        public Simulator.Movement.WorldState State => throw new NotSupportedException();

        public void SetPortalOpen(int portalId, bool open) => Open = open;
    }
}
