using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Allocation;

/// <summary>
/// Runs the zero-allocation tests one at a time, after the parallel tests. They measure with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, a per-thread counter: a test sharing its thread
/// pool with parallel tests could see their JIT, type loading or continuations land on its thread.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ZeroAllocationCollection
{
    public const string Name = "Zero allocation (per-thread allocation counters)";
}

/// <summary>
/// US7.2 and FR-028: once warmed up, steady-state bookkeeping allocates no managed memory. Three groups
/// of four entities (one of each claiming class) patrol back and forth inside their own regions on a
/// 40-step cycle; every region ticks every step, each group sets a timer every step, and due timers
/// fire. Damping never allows a split, and the groups never come within merge range of each other,
/// so after the warm-up no region is created, merged or split.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class ZeroAllocationTests
{
    private const int Groups = 3;
    private const int CyclePeriod = 40;
    private const int WarmUpSteps = 2 * CyclePeriod;
    private const int MeasuredSteps = 5 * CyclePeriod;
    private const long TimerDelay = 3;

    private static readonly EntityClass[] GroupClasses =
        [EntityClass.Infantry, EntityClass.GroundVehicle, EntityClass.Aircraft, EntityClass.Traveller];

    [Theory]
    [InlineData(ClaimAnchoring.Section, 0, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Section, 0, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Section, 1, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Section, 1, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Position, 0, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Position, 0, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Position, 1, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Position, 1, TimerMode.Absolute)]
    public void SteadyStateBookkeepingAllocatesNothing(ClaimAnchoring anchoring, int mergeGap, TimerMode timerMode)
    {
        var observer = new Kit3CountingObserver();
        var regionizer = new Regionizer(Kit3World.OpenWorld(64, 16), Kit3World.Options(timerMode, anchoring, mergeGap), observer);
        var entityIds = new int[Groups * GroupClasses.Length];
        for (var g = 0; g < Groups; g++)
        {
            for (var i = 0; i < GroupClasses.Length; i++)
            {
                var id = (g * GroupClasses.Length) + i;
                entityIds[id] = id;
                regionizer.AddEntity(id, GroupClasses[i], 0, X(id, 0), Y(id));
            }
        }

        var now = 0L;
        for (var step = 0; step < WarmUpSteps; step++)
        {
            Step(regionizer, regionizer.RegionIds.ToArray(), entityIds, step, ref now);
        }

        var context = $"{anchoring}, gap {mergeGap}, {timerMode}";
        Kit3Invariants.AssertClean(regionizer, $"{context}, after warm-up");
        var regionIds = regionizer.RegionIds.ToArray();
        var created = observer.RegionsCreated;
        var mergeRequests = observer.MergeRequests;
        var merges = observer.Merges;
        var splits = observer.Splits;
        var firedBefore = observer.TimersFired;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var step = WarmUpSteps; step < WarmUpSteps + MeasuredSteps; step++)
        {
            Step(regionizer, regionIds, entityIds, step, ref now);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The measured window was steady state: no region created, merged or split, timers firing.
        Assert.Equal(created, observer.RegionsCreated);
        Assert.Equal(mergeRequests, observer.MergeRequests);
        Assert.Equal(merges, observer.Merges);
        Assert.Equal(splits, observer.Splits);
        Assert.True(observer.TimersFired > firedBefore, $"{context}: no timer fired in the measured window");
        Assert.Equal(regionIds, regionizer.RegionIds.ToArray());
        Kit3Invariants.AssertClean(regionizer, $"{context}, after the measured window");
        Assert.True(allocated == 0, $"{context}: steady-state bookkeeping allocated {allocated} bytes over {MeasuredSteps} steps");
    }

    /// <summary>
    /// One lockstep step at 30 Hz: every region starts, every entity moves, each group's first entity
    /// gets a timer, due timers fire, every region ends. Static and closure-free, so the test itself
    /// allocates nothing.
    /// </summary>
    private static void Step(Regionizer regionizer, long[] regionIds, int[] entityIds, int step, ref long now)
    {
        now += 2;
        regionizer.AdvanceTo(now);
        foreach (var id in regionIds)
        {
            regionizer.TryMarkTicking(id);
        }

        foreach (var id in entityIds)
        {
            regionizer.MoveEntity(id, 0, X(id, step), Y(id));
        }

        for (var g = 0; g < Groups; g++)
        {
            regionizer.AddTimer(g * GroupClasses.Length, TimerDelay);
        }

        foreach (var id in regionIds)
        {
            regionizer.RunDueTimers(id);
        }

        foreach (var id in regionIds)
        {
            regionizer.MarkNotTicking(id);
        }
    }

    /// <summary>A triangle wave over the cycle: 0 to 50 m east of the group's base column and back.</summary>
    private static double X(int entityId, int step)
    {
        var group = entityId / GroupClasses.Length;
        var phase = (step + ((entityId % GroupClasses.Length) * 7)) % CyclePeriod;
        var offset = phase < CyclePeriod / 2 ? phase : CyclePeriod - phase;
        return Kit3World.Centre(6 + (20 * group)) + (offset * 2.5);
    }

    private static double Y(int entityId) => Kit3World.Centre(8) + ((entityId % GroupClasses.Length) * 6.0);
}
