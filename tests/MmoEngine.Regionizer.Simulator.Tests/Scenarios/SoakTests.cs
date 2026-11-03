using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scenarios;

/// <summary>The Soak scenario, shortened (US3, FR-012, FR-020).</summary>
public sealed class SoakTests
{
    /// <summary>
    /// US3.3: Given the Soak scenario (migration, stacked areas and air traffic) with a shortened duration
    /// in interleaved mode, When it runs, Then it ends with zero violations, and every synthetic timer
    /// fired exactly once, when FR-012 says for the active timer mode.
    /// </summary>
    [Theory]
    [InlineData(TimerMode.TickCounter)]
    [InlineData(TimerMode.Absolute)]
    public void ShortSoakInterleavedHasZeroViolationsAndExactTimers(TimerMode timerMode)
    {
        const ulong Seed = 20261103;
        const long Ticks = 450;

        // On the stacked map's own 2 km square: doors cycle every 4 s (closed 2 s, open 2 s), fights
        // migrate every 5 s, and timers are set often with short delays so that hundreds fire inside
        // 15 simulated seconds.
        var runConfig = TestRuns.Small("Soak", Seed, Ticks, population: 200)
            .With("map.file", ScenarioRuns.StackedBaseWithSchedule(everySeconds: 4, openSeconds: 2))
            .With("regionizer.timerMode", timerMode.ToString())
            .With("scheduling.dropTo20HzProbability", 0.05)
            .With("scenarioParameters.migrationEverySeconds", 5)
            .With("timers.setProbabilityPerEntityPerSecond", 0.2)
            .With("timers.delayTicksMin", 5)
            .With("timers.delayTicksMax", 90);
        var loaded = TestRuns.Load(runConfig);
        Assert.Equal(SchedulingMode.Interleaved, loaded.Config.Scheduling.Mode);
        Assert.True(loaded.Config.AirLayer.Enabled, "Soak's defaults turn the air layer on");
        var firings = new FiringRecorder();
        var watcher = new SoakWatcher();

        var result = TestRuns.Run(loaded, new RunOptions { Observers = [firings], Listeners = [watcher], AfterStep = watcher.Check });

        var label = $"{timerMode} seed {Seed}";
        Assert.True(result.FirstViolation is null, $"{label}: {result.FirstViolation}");
        Assert.True(result.Completed && result.TicksRun == Ticks, $"{label}: ran {result.TicksRun} of {Ticks} ticks");
        Assert.True(result.TimerFaults.Count == 0, $"{label}: {string.Join("; ", result.TimerFaults.Select(f => f.Detail))}");
        Assert.True(result.TimersFired > 200, $"{label}: only {result.TimersFired} timers fired");
        Assert.True(firings.Duplicates == 0, $"{label}: {firings.Duplicates} timers fired more than once");
        Assert.Equal(result.TimersFired, firings.Distinct);
        Assert.True(result.TimersSet >= result.TimersFired, $"{label}: {result.TimersFired} fired but only {result.TimersSet} were set");

        // Every part of the soak was exercised: 20 Hz ticks, door toggles, migrations and the air layer.
        Assert.True(result.Scheduling.TwentyHzTicks > 0, $"{label}: no region ticked at 20 Hz");
        Assert.True(watcher.PortalChanges >= 8, $"{label}: only {watcher.PortalChanges} door toggles");
        Assert.True(watcher.TeleportsBegun > 0, $"{label}: no infantry teleported");
        Assert.True(watcher.AircraftOnAirLayer > 0, $"{label}: no aircraft used the air layer");
        Assert.True(watcher.BunkerInfantry > 0, $"{label}: no infantry stood in the bunker layer");
    }

    /// <summary>Records every timer firing, independently of the timer monitor.</summary>
    private sealed class FiringRecorder : IRegionizerObserver
    {
        private readonly HashSet<int> _fired = [];

        public int Distinct => _fired.Count;

        public int Duplicates { get; private set; }

        public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
        {
            if (!_fired.Add(timerId))
            {
                Duplicates++;
            }
        }

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

        public void OnRegionTickStarted(long regionId, long tickCounter, long now)
        {
        }
    }

    /// <summary>Counts door toggles and teleports, and samples aircraft and bunker layers after each step.</summary>
    private sealed class SoakWatcher : RunListener
    {
        public int PortalChanges { get; private set; }

        public int TeleportsBegun { get; private set; }

        public int AircraftOnAirLayer { get; private set; }

        public int BunkerInfantry { get; private set; }

        public override void OnPortalChanged(long step, int portalId, bool open) => PortalChanges++;

        public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update)
        {
            if (update.Op == MovementOp.TeleportBegin)
            {
                TeleportsBegun++;
            }
        }

        public void Check(StepHookContext context)
        {
            var state = context.State;
            for (var id = 0; id < state.Count; id++)
            {
                // The stacked map has layers 0 (surface) and 1 (bunker), so the air layer is 2.
                AircraftOnAirLayer += state.Class[id] == EntityClass.Aircraft && state.Layer[id] == 2 ? 1 : 0;
                BunkerInfantry += state.Class[id] == EntityClass.Infantry && state.Layer[id] == 1 ? 1 : 0;
            }
        }
    }
}
