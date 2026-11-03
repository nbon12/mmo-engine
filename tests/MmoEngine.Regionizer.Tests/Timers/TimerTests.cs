using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Timers;

/// <summary>
/// Timers in both modes across merges and splits (FR-012, research D6). Simulation tests on a fixed
/// time base: every step is explicit and the invariant checker runs at every tick boundary.
/// </summary>
public sealed class TimerTests
{
    private const int InfantryA = 1;
    private const int InfantryB = 2;

    /// <summary>Base steps per tick at 30 Hz on the 60 Hz time base.</summary>
    private const long Period30Hz = 2;

    /// <summary>
    /// US1.7: a timer with a 90-tick delay is set on region R2; 30 of R2's ticks later R2 merges into
    /// R1, whose tick counter is 10 ahead. The timer fires once, on its owner's 90th tick since it was
    /// set. In absolute mode the delay is the same 90 ticks expressed in base steps.
    /// </summary>
    [Theory]
    [InlineData(TimerMode.TickCounter)]
    [InlineData(TimerMode.Absolute)]
    public void TimerFiresExactlyOnceAtDelayAcrossMerge(TimerMode mode)
    {
        const long delayTicks = 90;
        const long ticksBeforeMerge = 30;
        const long r1HeadStart = 10;
        var observer = new Kit3RecordingObserver();
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), Kit3World.Options(mode), observer);
        var driver = new Kit3Driver(regionizer);

        // A alone in R1 for 10 ticks, so R1's counter runs ahead of the region B will found.
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        var r1 = regionizer.GetEntity(InfantryA).RegionId;
        var now = 0L;
        for (var k = 0; k < r1HeadStart; k++)
        {
            now += Period30Hz;
            driver.Step(now);
        }

        // B founds R2 ten sections away (claims three sections apart) and gets the timer.
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(14), Kit3World.Centre(3));
        var r2 = regionizer.GetEntity(InfantryB).RegionId;
        Assert.NotEqual(r1, r2);
        var delay = mode == TimerMode.TickCounter ? delayTicks : delayTicks * Period30Hz;
        var setNow = now;
        var timerId = regionizer.AddTimer(InfantryB, delay);

        var ownerTicks = 0L;
        long? ownerTicksAtFiring = null;
        long? counterGapAtMerge = null;
        for (var k = 0; k < delayTicks + 40; k++)
        {
            now += Period30Hz;
            var firedBefore = observer.TimersFired.Count;
            driver.Begin(now);
            if (driver.Started.Contains(regionizer.GetEntity(InfantryB).RegionId))
            {
                ownerTicks++;
            }

            if (observer.TimersFired.Count > firedBefore)
            {
                ownerTicksAtFiring = ownerTicks;
            }

            if (ownerTicks == ticksBeforeMerge && regionizer.GetEntity(InfantryB).RegionId == r2)
            {
                // During R2's 30th tick B walks until its claim shares a section with A's.
                counterGapAtMerge = regionizer.GetRegion(r1).TickCounter - regionizer.GetRegion(r2).TickCounter;
                regionizer.MoveEntity(InfantryB, 0, Kit3World.Centre(4), Kit3World.Centre(3));
            }

            driver.End($"{mode}, tick at {now}");
        }

        Assert.Contains(new Kit3Merge(r1, r2, EntityClass.Infantry, InfantryB), observer.Merges);
        Assert.Equal(r1HeadStart, counterGapAtMerge);
        Assert.Equal(r1, regionizer.GetEntity(InfantryB).RegionId);

        var firing = Assert.Single(observer.TimersFired, f => f.TimerId == timerId);
        Assert.Equal(InfantryB, firing.EntityId);
        Assert.Equal(r1, firing.RegionId);
        Assert.Equal(delayTicks, ownerTicksAtFiring);
        Assert.Equal(setNow + (delayTicks * Period30Hz), firing.Now);
    }

    /// <summary>
    /// A timer on B, whose region splits five ticks after the timer was set. The parent ticks every 2
    /// base steps and B's child every 3. Tick-counter mode: the child copies the parent's counter (5),
    /// so the 20-tick delay ends at the child's counter 20, its 15th tick: steps 12, 15, …, 54. Absolute
    /// mode: the deadline is step 40, so it fires at the child's first tick at or after 40, which is 42.
    /// </summary>
    [Theory]
    [InlineData(TimerMode.TickCounter, 20, 54)]
    [InlineData(TimerMode.Absolute, 40, 42)]
    public void TimerFiresExactlyOnceAtDelayAcrossSplit(TimerMode mode, long delay, long expectedFireNow)
    {
        const long splitAt = 10;
        var observer = new Kit3RecordingObserver();
        var options = Kit3World.Options(mode, splitMinSections: 1, splitMinDeadPercent: 10, splitMinAgeSteps: 0);
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), options, observer);
        var driver = new Kit3Driver(regionizer);

        // A and B share a claimed column, so they start in one region.
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(4), Kit3World.Centre(3));
        var parent = regionizer.GetEntity(InfantryA).RegionId;
        Assert.Equal(parent, regionizer.GetEntity(InfantryB).RegionId);
        var timerId = regionizer.AddTimer(InfantryB, delay);

        bool Due(long regionId, long now) => regionId == parent ? now % 2 == 0 : now % 3 == 0;

        for (var now = 1L; now <= expectedFireNow + 20; now++)
        {
            var at = now;
            driver.Begin(now, id => Due(id, at));
            if (now == splitAt)
            {
                // B walks away: its old sections go dead and the region holds two islands.
                regionizer.MoveEntity(InfantryB, 0, Kit3World.Centre(12), Kit3World.Centre(3));
            }

            driver.End($"{mode}, step {now}");
            if (now == splitAt)
            {
                Assert.Single(observer.Splits);
            }
        }

        var child = regionizer.GetEntity(InfantryB).RegionId;
        Assert.NotEqual(parent, child);
        Assert.Equal(parent, regionizer.GetEntity(InfantryA).RegionId);

        var firing = Assert.Single(observer.TimersFired, f => f.TimerId == timerId);
        Assert.Equal(child, firing.RegionId);
        Assert.Equal(expectedFireNow, firing.Now);
    }

    /// <summary>
    /// Absolute mode with an owner that ticks every 3 base steps (20 Hz): the timer fires at the
    /// owner's first tick at or after set time + delay. The timer is set at the boundary after any
    /// tick at its set time.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 3)]
    [InlineData(0, 3, 3)]
    [InlineData(0, 4, 6)]
    [InlineData(1, 3, 6)]
    [InlineData(2, 1, 3)]
    [InlineData(3, 3, 6)]
    [InlineData(0, 90, 90)]
    [InlineData(5, 90, 96)]
    public void AbsoluteTimerFiresAtFirstOwnerTickAtOrAfterDeadlineAtTwentyHertz(long setAt, long delay, long expectedFireNow)
    {
        var observer = new Kit3RecordingObserver();
        var regionizer = new Regionizer(Kit3World.OpenWorld(8, 8), Kit3World.Options(TimerMode.Absolute), observer);
        var driver = new Kit3Driver(regionizer);
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(3), Kit3World.Centre(3));
        var owner = regionizer.GetEntity(InfantryA).RegionId;

        var timerId = setAt == 0 ? regionizer.AddTimer(InfantryA, delay) : 0;
        for (var now = 1L; now <= expectedFireNow + 9; now++)
        {
            var at = now;
            driver.Step(now, _ => at % 3 == 0, context: $"set {setAt}, delay {delay}, step {now}");
            if (now == setAt)
            {
                timerId = regionizer.AddTimer(InfantryA, delay);
            }
        }

        var firing = Assert.Single(observer.TimersFired, f => f.TimerId == timerId);
        Assert.Equal(owner, firing.RegionId);
        Assert.Equal(expectedFireNow, firing.Now);
        Assert.Equal(expectedFireNow / 3, firing.RegionTick);
    }

    // CORE-BUG: a heavy projectile that leaves its region (FollowSectionOwner) leaves its timer behind, and the region's next split throws KeyNotFoundException looking up region 0.
    [Fact]
    public void SplitAfterAProjectileWithATimerLeftForUnownedGroundCompletes()
    {
        const int projectile = 3;
        var options = Kit3World.Options(TimerMode.TickCounter, splitMinSections: 1, splitMinDeadPercent: 10);
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), options);
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(4), Kit3World.Centre(3));
        regionizer.AddEntity(projectile, EntityClass.HeavyProjectile, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        var region = regionizer.GetEntity(InfantryA).RegionId;
        Assert.Equal(region, regionizer.GetEntity(projectile).RegionId);
        regionizer.AddTimer(projectile, 5);

        // The projectile flies onto unowned ground; then B walks away so the region holds two islands.
        regionizer.MoveEntity(projectile, 0, Kit3World.Centre(25), Kit3World.Centre(3));
        Assert.Equal(0, regionizer.GetEntity(projectile).RegionId);
        regionizer.AdvanceTo(2);
        Assert.True(regionizer.TryMarkTicking(region));
        regionizer.MoveEntity(InfantryB, 0, Kit3World.Centre(12), Kit3World.Centre(3));

        var result = regionizer.MarkNotTicking(region);

        Assert.Equal(2, result.SplitChildren);
        Kit3Invariants.AssertClean(regionizer, "after the split");
    }

    // CORE-BUG: a heavy projectile's timer stays with the region it was set in after the projectile moves into another region, so it fires there even after the projectile is removed.
    [Fact]
    public void RemovedProjectileTimerNeverFiresAfterTheProjectileChangedRegion()
    {
        const int projectile = 3;
        var observer = new Kit3RecordingObserver();
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), Kit3World.Options(TimerMode.TickCounter), observer);
        var driver = new Kit3Driver(regionizer);
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(20), Kit3World.Centre(3));
        regionizer.AddEntity(projectile, EntityClass.HeavyProjectile, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        var timerId = regionizer.AddTimer(projectile, 5);

        // The projectile flies into B's region, then is removed (it hit something).
        regionizer.MoveEntity(projectile, 0, Kit3World.Centre(20), Kit3World.Centre(3));
        Assert.Equal(regionizer.GetEntity(InfantryB).RegionId, regionizer.GetEntity(projectile).RegionId);
        regionizer.RemoveEntity(projectile);
        for (var now = Period30Hz; now <= 20 * Period30Hz; now += Period30Hz)
        {
            driver.Step(now);
        }

        Assert.DoesNotContain(observer.TimersFired, f => f.TimerId == timerId);
    }

    // CORE-BUG: CompleteTeleport drops the carried timers of a heavy projectile that lands on unowned ground, so the timer never fires (FR-012 requires exactly once).
    [Fact]
    public void ProjectileTimerFiresOnceAfterTeleportToUnownedGround()
    {
        const int projectile = 3;
        var observer = new Kit3RecordingObserver();
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), Kit3World.Options(TimerMode.TickCounter), observer);
        var driver = new Kit3Driver(regionizer);
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(projectile, EntityClass.HeavyProjectile, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        var timerId = regionizer.AddTimer(projectile, 5);

        // Two-step teleport to unowned ground, then the projectile flies back into A's region.
        regionizer.BeginTeleport(projectile, 0, Kit3World.Centre(25), Kit3World.Centre(3));
        regionizer.CompleteTeleport(projectile);
        regionizer.MoveEntity(projectile, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        Assert.Equal(regionizer.GetEntity(InfantryA).RegionId, regionizer.GetEntity(projectile).RegionId);
        for (var now = Period30Hz; now <= 20 * Period30Hz; now += Period30Hz)
        {
            driver.Step(now);
        }

        Assert.Single(observer.TimersFired, f => f.TimerId == timerId);
    }
}
