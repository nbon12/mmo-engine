using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Performance;

/// <summary>
/// FR-027: the bookkeeping reading measures the core, not the simulator's observers. The core calls the
/// observers synchronously inside each timed call, so they only queue work there: events reach
/// <c>events.csv</c>, and the timer monitor applies its shadow model, after the call returns.
/// </summary>
public sealed class BookkeepingIsolationTests
{
    private const ulong Seed = 29;

    /// <summary>
    /// A run's event writes all happen outside measured core calls. The listener is called in the same
    /// method that writes each event to <c>events.csv</c>, right after the write, and reads the run's
    /// meter there. A probe observer shows that observer calls do happen inside measured calls, so the
    /// check would catch a write made from one.
    /// </summary>
    [Theory]
    [InlineData("Lockstep")]
    [InlineData("Interleaved")]
    public void NoEventIsWrittenInsideAMeasuredCoreCall(string scheduling)
    {
        var meter = new BookkeepingMeter();
        var writes = new WriteProbe(meter);
        var callbacks = new CallbackProbe(meter);
        var config = TestRuns.Small("Baseline", Seed, 60, population: 300)
            .With("scheduling.mode", scheduling)
            .With("scheduling.dropTo20HzProbability", 0.2)
            .With("timers.setProbabilityPerEntityPerSecond", 0.5);
        var label = $"Baseline {scheduling} seed {Seed}";

        var result = TestRuns.Run(TestRuns.Load(config), new RunOptions { Meter = meter, Listeners = [writes], Observers = [callbacks] });

        Assert.True(result.Completed, $"{label}: {result.FirstViolation}");
        var written = File.ReadAllLines(Path.Combine(result.OutputDirectory, RunFolder.EventsFile)).Length - 1;
        Assert.True(writes.Events == written, $"{label}: the listener saw {writes.Events} events, events.csv holds {written}");
        Assert.True(writes.Types.Contains(RunEventType.Merge) && writes.Types.Contains(RunEventType.RegionDied), $"{label}: the run wrote no merge or region death, so the check would be vacuous");
        Assert.True(writes.InsideCall == 0, $"{label}: {writes.InsideCall} of {writes.Events} events were written inside a measured core call");
        Assert.True(callbacks.Calls > 0 && callbacks.OutsideCall == 0, $"{label}: {callbacks.OutsideCall} of {callbacks.Calls} observer calls ran outside a measured core call");
        Assert.True(meter.Calls > 0, $"{label}: no core call was measured");
        Assert.True(result.TimersFired > 0, $"{label}: no timer fired, so the timer monitor's queue was not exercised");
        Assert.False(meter.InCall, $"{label}: the meter was left inside a call");
    }

    /// <summary>
    /// The observer calls of a steady-state tick (state changes, tick starts, section and owner changes,
    /// timers firing; no region created, merged or split) allocate nothing once warmed up, and the timer
    /// monitor only queues them: its shadow model sees them at <see cref="TimerMonitor.Flush"/>.
    /// </summary>
    [Fact]
    public void SteadyStateObserverCallsAllocateNothingAndOnlyQueue()
    {
        const int Ticks = 500;
        EntitySpec[] entities =
        [
            new(0, EntityClass.Infantry, 0, 100, 100, 0, 0),
            new(1, EntityClass.Infantry, 0, 120, 100, 0, 1),
            new(2, EntityClass.Traveller, 0, 140, 100, 0, -1),
        ];
        var config = TestRuns.Load(TestRuns.Small("Baseline", Seed, 10).With("scheduling.mode", "Interleaved")).Config;
        var metrics = new MetricsCollector(new WorldState(entities), config.Metrics, _ => { });
        var timers = new TimerMonitor(TimerMode.TickCounter);
        var scheduler = TickSchedulers.Create(config.Scheduling, Seed).Observer!;
        var observer = new CompositeObserver([metrics, timers, scheduler]);
        observer.OnRegionCreated(1, RegionCreationReason.NewClaim, 0);
        observer.OnRegionCreated(2, RegionCreationReason.NewClaim, 0);
        observer.OnEntityOwnerChanged(0, 0, 1);
        observer.OnEntityOwnerChanged(1, 0, 1);
        observer.OnEntityOwnerChanged(2, 0, 2);

        // Each phase sets one timer per tick, the k-th due after k + 1 owner ticks, and fires it on time.
        // Warm-up: the timer monitor's queue grows to a phase's worth.
        RunTicks(observer, timers, firstTimer: 0, firstNow: 0, Ticks, start: () => { });
        timers.Flush();
        metrics.FlushEvents();
        long before = 0;
        RunTicks(observer, timers, firstTimer: Ticks, firstNow: Ticks * 2, Ticks, start: () => before = GC.GetAllocatedBytesForCurrentThread());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var queued = timers.Queued;

        Assert.True(allocated == 0, $"seed {Seed}: steady-state observer calls allocated {allocated} bytes over {Ticks} ticks");
        Assert.True(queued == Ticks * 4, $"seed {Seed}: the timer monitor queued {queued} notifications, expected {Ticks * 4}");
        Assert.Empty(timers.Faults);
        Assert.Equal(0, timers.Queued);
        Assert.Equal(2L * Ticks, timers.TimersFired);
        Assert.Equal(0, timers.Pending);
        Assert.Equal(0, metrics.PendingEvents);
    }

    /// <summary>
    /// Sets <paramref name="count"/> timers on entity 0 in region 1 (as the engine does, outside the core's
    /// calls), then calls <paramref name="start"/> and runs that many steady ticks of region 1: each
    /// starts, changes state, gives a section an owner, moves entity 0 to region 2 and back, and fires
    /// the timer due (the k-th timer, set with a delay of k + 1 owner ticks).
    /// </summary>
    private static void RunTicks(CompositeObserver observer, TimerMonitor timers, int firstTimer, long firstNow, int count, Action start)
    {
        for (var k = 0; k < count; k++)
        {
            timers.OnTimerSet(firstTimer + k, entityId: 0, regionId: 1, delay: k + 1, now: firstNow);
        }

        start();
        for (var k = 0; k < count; k++)
        {
            var now = firstNow + (2 * k) + 2;
            observer.OnStateChanged(1, RegionState.Ready, RegionState.Ticking);
            observer.OnRegionTickStarted(1, k + 1, now);
            observer.OnSectionOwnerChanged(k, 0, 1);
            observer.OnEntityOwnerChanged(0, 1, 2);
            observer.OnEntityOwnerChanged(0, 2, 1);
            observer.OnTimerFired(firstTimer + k, 0, 1, now, k + 1);
            observer.OnStateChanged(1, RegionState.Ticking, RegionState.Ready);
        }
    }

    /// <summary>Records, for every event written, whether a core call was being measured.</summary>
    private sealed class WriteProbe(BookkeepingMeter meter) : RunListener
    {
        public int Events { get; private set; }

        public int InsideCall { get; private set; }

        public HashSet<RunEventType> Types { get; } = [];

        public override void OnEvent(in RunEvent runEvent)
        {
            Events++;
            Types.Add(runEvent.Type);
            if (meter.InCall)
            {
                InsideCall++;
            }
        }
    }

    /// <summary>Records, for every observer call, whether a core call was being measured.</summary>
    private sealed class CallbackProbe(BookkeepingMeter meter) : ObserverBase
    {
        public int Calls { get; private set; }

        public int OutsideCall { get; private set; }

        public override void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) => See();

        public override void OnStateChanged(long regionId, RegionState from, RegionState to) => See();

        public override void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId) => See();

        public override void OnSplit(long parentId, ReadOnlySpan<long> childIds) => See();

        public override void OnRegionDied(long regionId, long ageSteps) => See();

        public override void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner) => See();

        public override void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner) => See();

        public override void OnRegionTickStarted(long regionId, long tickCounter, long now) => See();

        public override void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick) => See();

        private void See()
        {
            Calls++;
            if (!meter.InCall)
            {
                OutsideCall++;
            }
        }
    }
}
