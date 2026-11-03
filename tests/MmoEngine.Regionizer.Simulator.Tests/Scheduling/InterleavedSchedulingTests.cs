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
    /// one region ticks at 20 Hz, at least one merge joins two regions with different tick counters,
    /// the run ends with zero invariant violations, and a rerun with the same seed gives identical
    /// per-tick hashes.
    /// </summary>
    [Fact]
    public void MigrationInterleavedHitsTwentyHzAndMergeLaterWithZeroViolations()
    {
        const ulong Seed = 20261103;
        const int Ticks = 300;
        var runConfig = TestRuns.Small("Migration", Seed, Ticks, population: 160)
            .With("scheduling.mode", "Interleaved")
            .With("scheduling.dropTo20HzProbability", 0.05)
            .With("scenarioParameters.migrationEverySeconds", 3);
        var loaded = TestRuns.Load(runConfig);
        var probe = new TickCounterMergeProbe();

        var first = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true, Observers = [probe], AfterStep = probe.Attach });
        var rerun = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true });

        Assert.True(first.FirstViolation is null, $"seed {Seed}: {first.FirstViolation}");
        Assert.True(first.Completed && first.ExitCode == ExitCodes.Clean, $"seed {Seed}: run did not complete cleanly");
        Assert.Equal(20, loaded.Config.Scheduling.DegradedTickHz);
        Assert.True(first.Scheduling.TwentyHzTicks > 0, $"seed {Seed}: no region ticked at 20 Hz");
        Assert.True(probe.MergesAcrossTickCounters > 0, $"seed {Seed}: no merge joined two regions with different tick counters ({probe.Merges} merges seen)");
        Assert.True(first.MergesRequestedAgainstTicking > 0, $"seed {Seed}: no merge was requested against a ticking region");
        Assert.True(first.InvariantChecks >= Ticks * 2, $"seed {Seed}: only {first.InvariantChecks} invariant checks ran");
        Assert.True(first.TickHashes.Count == Ticks && first.TickHashes.SequenceEqual(rerun.TickHashes), $"seed {Seed}: rerun hashes differ");
    }

    /// <summary>Lockstep ticks every region on the first step of each continent tick, at the configured tick rate.</summary>
    [Theory]
    [InlineData(60, 30, 20, new long[] { 0, 2, 4, 6, 8, 10 })]
    [InlineData(60, 20, 10, new long[] { 0, 3, 6, 9 })]
    [InlineData(120, 30, 20, new long[] { 0, 4, 8 })]
    [InlineData(60, 60, 20, new long[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })]
    public void LockstepTicksAtTheConfiguredTickRate(int baseStepHz, int tickHz, int degradedTickHz, long[] expectedSteps)
    {
        var host = new RecordingHost();
        var scheduler = TickSchedulers.Create(new SchedulingConfig { Mode = SchedulingMode.Lockstep, BaseStepHz = baseStepHz, TickHz = tickHz, DegradedTickHz = degradedTickHz }, 1);

        for (var step = 0L; step < 12; step++)
        {
            scheduler.RunStep(step, [4], host);
        }

        Assert.Equal(expectedSteps, host.Events.Where(e => e.Start).Select(e => e.Step));
        Assert.Equal(baseStepHz / tickHz, scheduler.Time.TickPeriodSteps);
    }

    /// <summary>A region dropped to the degraded rate ticks every baseStepHz / degradedTickHz steps (10 Hz: 6 steps).</summary>
    [Fact]
    public void ADropSpacesTicksByTheConfiguredDegradedPeriod()
    {
        var host = new RecordingHost();
        var config = Scheduling(1, 2, 2) with { DegradedTickHz = 10 };
        var scheduler = new InterleavedScheduler(config, new DeterministicRandom(1));

        for (var step = 0L; step <= 20; step++)
        {
            scheduler.RunStep(step, [7], host);
        }

        Assert.Equal([0L, 2, 8, 14, 20], host.Events.Where(e => e.Start).Select(e => e.Step));
        Assert.Equal(4, scheduler.Stats.TwentyHzTicks);
        Assert.Equal(3, scheduler.Stats.Drops);
        Assert.Equal(26, scheduler.NextDueOf(7));
        Assert.Equal(6, scheduler.Time.DegradedPeriodSteps);
    }

    /// <summary>A run at non-default rates spaces its region ticks by the configured period in both modes.</summary>
    [Theory]
    [InlineData("Lockstep", 120, 30, 20)]
    [InlineData("Interleaved", 120, 30, 20)]
    [InlineData("Lockstep", 60, 20, 10)]
    [InlineData("Interleaved", 90, 30, 15)]
    public void RunsSpaceRegionTicksByTheConfiguredPeriods(string mode, int baseStepHz, int tickHz, int degradedTickHz)
    {
        const ulong Seed = 17;
        const int Ticks = 60;
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", Seed, Ticks, population: 80)
            .With("scheduling.mode", mode)
            .With("scheduling.baseStepHz", baseStepHz)
            .With("scheduling.tickHz", tickHz)
            .With("scheduling.degradedTickHz", degradedTickHz)
            .With("scheduling.dropTo20HzProbability", 0.2));
        var gaps = new TickGapRecorder();

        var result = TestRuns.Run(loaded, new RunOptions { Observers = [gaps] });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.Equal(Ticks, result.TicksRun);
        var period = baseStepHz / tickHz;
        var degraded = baseStepHz / degradedTickHz;
        Assert.Contains(period, gaps.Gaps);
        Assert.All(gaps.Gaps, gap => Assert.True(gap >= period, $"seed {Seed}: a region ticked {gap} steps after its previous tick, faster than every {period}"));
        if (mode == "Lockstep")
        {
            Assert.All(gaps.Gaps, gap => Assert.True(gap % period == 0, $"seed {Seed}: a lockstep region ticked {gap} steps after its previous tick"));
            Assert.Equal(0, result.Scheduling.TwentyHzTicks);
        }
        else
        {
            Assert.Contains(degraded, gaps.Gaps);
            Assert.True(result.Scheduling.TwentyHzTicks > 0, $"seed {Seed}: no region ticked at the degraded rate");
        }
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

    /// <summary>
    /// Counts merges where both sides hold an entity and their tick counters differ, reading both
    /// regions from the core when <see cref="IRegionizerObserver.OnMerged"/> is raised (before the
    /// absorbed region's data moves). The core is attached through the step hook.
    /// </summary>
    private sealed class TickCounterMergeProbe : ObserverBase
    {
        private Regionizer? _regionizer;

        public int Merges { get; private set; }

        public int MergesAcrossTickCounters { get; private set; }

        public void Attach(StepHookContext context) => _regionizer ??= context.Regionizer;

        public override void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
        {
            if (_regionizer is null)
            {
                return;
            }

            var absorber = _regionizer.GetRegion(absorberId);
            var absorbed = _regionizer.GetRegion(absorbedId);
            if (absorber.Entities.Length == 0 || absorbed.Entities.Length == 0)
            {
                return;
            }

            Merges++;
            if (absorber.TickCounter != absorbed.TickCounter)
            {
                MergesAcrossTickCounters++;
            }
        }
    }

    /// <summary>The steps between each region's consecutive tick starts.</summary>
    private sealed class TickGapRecorder : ObserverBase
    {
        private readonly Dictionary<long, long> _last = [];

        public HashSet<long> Gaps { get; } = [];

        public override void OnRegionTickStarted(long regionId, long tickCounter, long now)
        {
            if (_last.TryGetValue(regionId, out var previous))
            {
                Gaps.Add(now - previous);
            }

            _last[regionId] = now;
        }
    }

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
