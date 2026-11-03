using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Monitors;

/// <summary>The invariant and timer monitors that run after every step (FR-020, research D6).</summary>
public sealed class MonitorTests
{
    /// <summary>
    /// US3.2: Given a run in which a section is made to belong to two regions at tick t, When the
    /// monitors run at tick t, Then the run fails at tick t and reports rule 1, the tick number and the seed.
    /// </summary>
    [Theory]
    [InlineData("Lockstep")]
    [InlineData("Interleaved")]
    public void SectionInTwoRegionsFailsRunAtThatTickWithRuleAndSeed(string scheduling)
    {
        const ulong Seed = 404;
        const long CorruptTick = 12;
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", Seed, 60).With("scheduling.mode", scheduling));
        var dir = TestRuns.TempDir();
        var hook = new SectionCorruption(CorruptTick);

        var result = SimulationRunner.Run(loaded, dir, new RunOptions { AfterStep = hook.Apply, GitSha = "unknown" });

        Assert.True(hook.Applied, $"seed {Seed}: the corruption was not applied");
        Assert.Equal(ExitCodes.Violation, result.ExitCode);
        Assert.False(result.Completed);
        var failure = result.FirstViolation;
        Assert.NotNull(failure);
        Assert.Equal(RunFailureKind.Violation, failure.Kind);
        Assert.Equal(InvariantRule.SectionSingleOwner, failure.Rule);
        Assert.Equal(1, failure.RuleNumber);
        Assert.Equal(CorruptTick, failure.Tick);
        Assert.Equal(CorruptTick * 2, failure.TimeSteps);
        Assert.Equal(Seed, failure.Seed);
        Assert.Equal(CorruptTick + 1, result.TicksRun);

        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunFolder.SummaryFile)))!;
        Assert.False(summary["completed"]!.GetValue<bool>());
        Assert.Equal(1, summary["firstViolation"]!["ruleNumber"]!.GetValue<int>());
        Assert.Equal(CorruptTick, summary["firstViolation"]!["tick"]!.GetValue<long>());
        Assert.Equal(Seed, summary["firstViolation"]!["seed"]!.GetValue<ulong>());

        var violations = TestRuns.CsvRows(Path.Combine(dir, RunFolder.EventsFile)).Where(e => e[2] == "Violation").ToList();
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(CorruptTick.ToString(System.Globalization.CultureInfo.InvariantCulture), v[0]));
        Assert.StartsWith("rule 1 ", violations[0][8].Trim('"'), StringComparison.Ordinal);
        var lastRow = TestRuns.CsvRows(Path.Combine(dir, RunFolder.TicksFile))[^1];
        Assert.Equal(CorruptTick.ToString(System.Globalization.CultureInfo.InvariantCulture), lastRow[0]);
        Assert.NotEqual("0", lastRow[14]);
        Assert.False(File.Exists(Path.Combine(dir, RunFolder.IncompleteMarker)));
    }

    /// <summary>A timer made late through the core's test hook is caught by the timer monitor (FR-020).</summary>
    [Theory]
    [InlineData("TickCounter", "Lockstep", 5)]
    [InlineData("TickCounter", "Interleaved", 5)]
    [InlineData("Absolute", "Lockstep", 10)]
    [InlineData("Absolute", "Interleaved", 10)]
    public void LateTimerIsReportedByTheTimerMonitor(string timerMode, string scheduling, long lateBy)
    {
        const ulong Seed = 77;
        var loaded = TestRuns.Load(TestRuns.Small("Baseline", Seed, 200)
            .With("regionizer.timerMode", timerMode)
            .With("scheduling.mode", scheduling)
            .With("timers.setProbabilityPerEntityPerSecond", 0.5)
            .With("timers.delayTicksMin", 20)
            .With("timers.delayTicksMax", 40));
        var dir = TestRuns.TempDir();
        var hook = new LateTimer(20, lateBy, timerMode == "Absolute");

        var result = SimulationRunner.Run(loaded, dir, new RunOptions { AfterStep = hook.Apply, GitSha = "unknown" });

        Assert.True(hook.TimerId > 0, $"seed {Seed}: no timer was available to delay");
        Assert.Equal(ExitCodes.Violation, result.ExitCode);
        var failure = result.FirstViolation;
        Assert.NotNull(failure);
        Assert.Equal(RunFailureKind.TimerFault, failure.Kind);
        Assert.Equal(7, failure.RuleNumber);
        Assert.Equal(Seed, failure.Seed);
        var fault = Assert.Single(result.TimerFaults);
        Assert.Equal(hook.TimerId, fault.TimerId);
        Assert.Contains("late", fault.Detail, StringComparison.Ordinal);
        Assert.True(failure.TimeSteps > hook.AppliedAt, $"seed {Seed}: the fault was reported before the timer could be late");
        Assert.Contains(TestRuns.CsvRows(Path.Combine(dir, RunFolder.EventsFile)), e => e[2] == "TimerFault");
    }

    /// <summary>Without interference every synthetic timer fires exactly once at its delay, across merges, splits and teleports.</summary>
    [Theory]
    [InlineData("TickCounter", "Lockstep")]
    [InlineData("TickCounter", "Interleaved")]
    [InlineData("Absolute", "Lockstep")]
    [InlineData("Absolute", "Interleaved")]
    public void SyntheticTimersFireExactlyOnceInEveryMode(string timerMode, string scheduling)
    {
        const ulong Seed = 61;
        var loaded = TestRuns.Load(TestRuns.Small("Migration", Seed, 400, population: 200)
            .With("regionizer.timerMode", timerMode)
            .With("scheduling.mode", scheduling)
            .With("scheduling.dropTo20HzProbability", 0.05)
            .With("scenarioParameters.migrationEverySeconds", 3)
            .With("movement.redeployProbabilityPerSecond", 0.05)
            .With("timers.setProbabilityPerEntityPerSecond", 0.2)
            .With("timers.delayTicksMin", 5)
            .With("timers.delayTicksMax", 60));

        var result = TestRuns.Run(loaded);

        Assert.True(result.FirstViolation is null, $"seed {Seed}: {result.FirstViolation}");
        Assert.Empty(result.TimerFaults);
        Assert.True(result.TimersFired > 100, $"seed {Seed}: only {result.TimersFired} timers fired");
        Assert.True(result.TimersFired <= result.TimersSet, $"seed {Seed}: more timers fired than were set");
        Assert.True(result.Merges > 0 && result.Splits > 0, $"seed {Seed}: merges {result.Merges}, splits {result.Splits}");
    }

    [Fact]
    public void ShadowCountsOwnerTicksAcrossAnOwnerChange()
    {
        var monitor = new TimerMonitor(TimerMode.TickCounter);
        monitor.OnTimerSet(1, entityId: 0, regionId: 5, delay: 3, now: 0);

        monitor.OnRegionTickStarted(5, 1, 2);
        monitor.OnEntityOwnerChanged(0, 5, 6);
        monitor.OnRegionTickStarted(5, 2, 4);
        monitor.OnRegionTickStarted(6, 9, 4);
        monitor.OnRegionTickStarted(6, 10, 6);
        monitor.OnTimerFired(1, 0, 6, 6, 10);

        Assert.Empty(monitor.Faults);
        Assert.Equal(1, monitor.TimersFired);
        Assert.Equal(0, monitor.Pending);
    }

    [Theory]
    [InlineData(2, "fired after 2 owner ticks, expected exactly 3")]
    [InlineData(4, "is late")]
    public void ShadowReportsEarlyAndLateTickCounterTimers(int ownerTicks, string expected)
    {
        var monitor = new TimerMonitor(TimerMode.TickCounter);
        monitor.OnTimerSet(1, 0, 5, 3, 0);

        for (var t = 1; t <= ownerTicks; t++)
        {
            monitor.OnRegionTickStarted(5, t, t * 2);
        }

        monitor.OnTimerFired(1, 0, 5, ownerTicks * 2, ownerTicks);

        var fault = Assert.Single(monitor.Faults);
        Assert.Contains(expected, fault.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowReportsATimerThatFiresTwice()
    {
        var monitor = new TimerMonitor(TimerMode.TickCounter);
        monitor.OnTimerSet(1, 0, 5, 1, 0);
        monitor.OnRegionTickStarted(5, 1, 2);

        monitor.OnTimerFired(1, 0, 5, 2, 1);
        monitor.OnTimerFired(1, 0, 5, 2, 1);

        Assert.Contains("second time", Assert.Single(monitor.Faults).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new long[] { 2, 4, 6 }, 10L, false)]
    [InlineData(new long[] { 2, 4, 10 }, 10L, false)]
    [InlineData(new long[] { 2, 10, 12 }, 12L, true)]
    [InlineData(new long[] { 2 }, 4L, false)]
    public void AbsoluteTimersMustFireAtTheFirstOwnerTickAtOrAfterTheDeadline(long[] ownerTickSteps, long firedAt, bool late)
    {
        // Set at step 0 with a delay of 10 steps: the deadline is step 10.
        var monitor = new TimerMonitor(TimerMode.Absolute);
        monitor.OnTimerSet(1, 0, 5, 10, 0);

        foreach (var step in ownerTickSteps)
        {
            monitor.OnRegionTickStarted(5, 0, step);
        }

        monitor.OnTimerFired(1, 0, 5, firedAt, 0);

        var expectedFaults = late || firedAt < 10 ? 1 : 0;
        Assert.Equal(expectedFaults, monitor.Faults.Count);
    }

    [Fact]
    public void ARemovedEntitysTimersAreForgotten()
    {
        var monitor = new TimerMonitor(TimerMode.TickCounter);
        monitor.OnTimerSet(1, 0, 5, 1, 0);
        monitor.OnTimerSet(2, 0, 5, 2, 0);

        monitor.OnEntityRemoved(0);
        monitor.OnEntityRemoved(0);
        monitor.OnRegionTickStarted(5, 1, 2);
        monitor.OnRegionTickStarted(5, 2, 4);
        monitor.OnRegionTickStarted(5, 3, 6);

        Assert.Equal(0, monitor.Pending);
        Assert.Empty(monitor.Faults);
    }

    [Fact]
    public void ARegionThatDiesStillHoldingATimerIsAFault()
    {
        var monitor = new TimerMonitor(TimerMode.TickCounter);
        monitor.OnTimerSet(1, 0, 5, 10, 0);

        monitor.OnRegionDied(5, 100);

        Assert.Contains("still owned by region 5", Assert.Single(monitor.Faults).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ATimerUnknownToTheMonitorIsAFault()
    {
        var monitor = new TimerMonitor(TimerMode.Absolute);

        monitor.OnTimerFired(9, 0, 5, 2, 1);

        Assert.Contains("never set", Assert.Single(monitor.Faults).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunFailureKind.Violation, InvariantRule.ClaimsInRegion, "invariant rule 2 (ClaimsInRegion) broken at tick 7 (step 14), seed 99: detail")]
    [InlineData(RunFailureKind.TimerFault, null, "timer fault at tick 7 (step 14), seed 99: detail")]
    public void FailuresDescribeRuleTickAndSeed(RunFailureKind kind, InvariantRule? rule, string expected)
    {
        var failure = new RunFailure(kind, rule, 7, 14, 99, "detail", 1);

        Assert.Equal(expected, failure.ToString());
        Assert.Equal(rule is null ? 7 : (int)rule, failure.RuleNumber);
    }

    [Fact]
    public void InvariantMonitorReportsTheFirstViolation()
    {
        var regionizer = new Regionizer(
            SectionTopology.OpenWorld(16, 512, 512),
            new RegionizerOptions(16, ClaimAnchoring.Section, 0, new ClaimRadii(16, 16, 16, 16, 16), 8, 25, 0, TimerMode.TickCounter));
        regionizer.AddEntity(0, EntityClass.Infantry, 0, 100, 100);
        regionizer.AddEntity(1, EntityClass.Infantry, 0, 400, 400);
        var monitor = new InvariantMonitor();

        Assert.Null(monitor.Check(regionizer));
        var regions = regionizer.RegionIds.ToArray();
        regionizer.TestHookListSectionInRegion(regions[1], regionizer.GetRegion(regions[0]).Sections[0]);
        var violation = monitor.Check(regionizer);

        Assert.Equal(InvariantRule.SectionSingleOwner, violation?.Rule);
        Assert.Equal(2, monitor.Checks);
    }

    /// <summary>Lists a section owned by one region under another at the first step of a tick (breaks rule 1).</summary>
    private sealed class SectionCorruption(long tick)
    {
        public bool Applied { get; private set; }

        public void Apply(StepHookContext context)
        {
            if (Applied || context.Step != tick * 2)
            {
                return;
            }

            var regions = context.Regionizer.RegionIds.ToArray();
            var owner = regions.First(id => context.Regionizer.GetRegion(id).SectionCount > 0);
            var other = regions.First(id => id != owner);
            context.Regionizer.TestHookListSectionInRegion(other, context.Regionizer.GetRegion(owner).Sections[0]);
            Applied = true;
        }
    }

    /// <summary>Pushes one pending timer's deadline later through the core's test hook.</summary>
    private sealed class LateTimer(long tick, long lateBy, bool absolute)
    {
        public int TimerId { get; private set; }

        public long AppliedAt { get; private set; }

        public void Apply(StepHookContext context)
        {
            if (TimerId != 0 || context.Tick < tick)
            {
                return;
            }

            var regionizer = context.Regionizer;
            foreach (var id in regionizer.RegionIds.ToArray())
            {
                var region = regionizer.GetRegion(id);
                foreach (var timer in region.Timers)
                {
                    var remaining = absolute ? timer.Deadline - regionizer.Now : timer.Deadline - region.TickCounter;
                    if (remaining >= (absolute ? 6 : 3))
                    {
                        regionizer.TestHookSetTimerDeadline(timer.TimerId, timer.Deadline + lateBy);
                        TimerId = timer.TimerId;
                        AppliedAt = context.Step;
                        return;
                    }
                }
            }
        }
    }
}
