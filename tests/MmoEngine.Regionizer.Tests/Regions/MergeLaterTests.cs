using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Regions;

/// <summary>
/// Merge-later against ticking regions (FR-006, FR-007, research D3 and D4): a ticking region never
/// grows, sections it needs go into transient regions, and a region still owing a merge to a
/// ticking region waits as transient until that tick ends. Entities stand on row 8 of a 32×32 open
/// world; a section-anchored claim is the 3×3 block around the entity's section.
/// </summary>
public sealed class MergeLaterTests
{
    private const int Row = 8;

    /// <summary>
    /// US1.2: a new entity's claim lands beside a ticking region. Placed in a section the region owns,
    /// the entity joins it and its new sections go to the region's transient helper; placed just
    /// outside, it starts a region that owes the ticking one a merge. Either way the new sections sit
    /// in a transient region recorded on the ticking one, absorbed at its tick end.
    /// </summary>
    [Theory]
    [InlineData(5, 0, 1201)]
    [InlineData(5, 1, 1202)]
    [InlineData(6, 0, 1203)]
    [InlineData(6, 1, 1204)]
    public void TickingRegionNeverGrows_TransientAbsorbedAtTickEnd(int newEntityColumn, int mergeGap, int seed)
    {
        // Given a region (entity 1 in column 4, claiming 3–5) that is ticking.
        var h = new Kit2Harness(nameof(TickingRegionNeverGrows_TransientAbsorbedAtTickEnd), seed, Kit2Harness.Options(mergeGap: mergeGap));
        h.Add(1, 4, Row);
        var ticking = h.RegionOf(1);
        h.AssertClean();
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(ticking);
        var sectionsAtStart = h.SectionsOf(ticking);

        // When a new entity's claim lands beside it during the tick, and its own entity also steps left.
        h.Add(2, newEntityColumn, Row);
        h.MoveInTick(1, 3, Row);

        // Then the ticking region's section set does not change during the tick ...
        h.SameSections(sectionsAtStart, h.SectionsOf(ticking), "sections of the ticking region during its tick");
        h.Equal(RegionState.Ticking, h.StateOf(ticking), "state of the ticking region");

        // ... and every newly claimed section belongs to a transient region recorded on the ticking one.
        var claimed = h.Footprint(1).Union(h.Footprint(2)).ToArray();
        var newSections = claimed.Except(sectionsAtStart).ToArray();
        h.True(newSections.Length > 0, "the claims reach sections the ticking region does not own");
        var transients = new HashSet<long>();
        foreach (var section in newSections)
        {
            var owner = h.R.OwnerOf(section);
            h.True(owner != ticking && owner != 0, $"section {h.Keys([section])} is owned outside the ticking region");
            h.Equal(RegionState.Transient, h.StateOf(owner), $"state of region {owner} holding new section {h.Keys([section])}");
            h.True(h.PendingOf(ticking).Contains(owner), $"region {owner} is recorded as a pending merge on the ticking region");
            h.True(h.PendingOf(owner).Contains(ticking), $"region {owner} records the ticking region as its partner");
            transients.Add(owner);
        }

        // ... and the transient regions are absorbed when the ticking region's tick ends.
        var result = h.End(ticking);
        h.AssertClean();
        h.Equal(RegionState.Ready, result.State, "state after the tick end");
        h.Equal(0L, result.AbsorbedInto, "the ticking region was not absorbed");
        foreach (var transient in transients)
        {
            h.Equal(RegionState.Dead, h.StateOf(transient), $"state of transient region {transient} after the tick end");
            h.Equal(ticking, h.Events.MergesBetween(ticking, transient).Single().Absorber, $"absorber of transient region {transient}");
        }

        h.SameSections(sectionsAtStart.Union(claimed), h.SectionsOf(ticking), "sections after the tick end (old dead ones kept, new ones absorbed)");
        h.Equal(ticking, h.RegionOf(2), "region of the new entity");
        h.Equal(0, h.PendingOf(ticking).Length, "pending merges after the tick end");
    }

    /// <summary>The cases of <see cref="RegionOwingTwoTickingPartnersStaysTransientUntilTheLaterTickEnd"/>.</summary>
    public static TheoryData<int, bool, int> OwingCases()
    {
        var data = new TheoryData<int, bool, int>();
        var seed = 1300;
        for (var owingRank = 0; owingRank < 3; owingRank++)
        {
            data.Add(owingRank, true, ++seed);
            data.Add(owingRank, false, ++seed);
        }

        return data;
    }

    /// <summary>
    /// A region whose claims touch two ticking regions owes both. At its tick end it becomes
    /// transient; it stays transient (merged with the first partner, still owing the other) until the
    /// later partner's tick ends, which completes the merge into one ready region.
    /// </summary>
    /// <param name="owingRank">Creation order of the owing region among the three (0 = lowest id).</param>
    /// <param name="leftEndsFirst">Whether the left partner's tick ends before the right one's.</param>
    [Theory]
    [MemberData(nameof(OwingCases))]
    public void RegionOwingTwoTickingPartnersStaysTransientUntilTheLaterTickEnd(int owingRank, bool leftEndsFirst, int seed)
    {
        var h = new Kit2Harness(nameof(RegionOwingTwoTickingPartnersStaysTransientUntilTheLaterTickEnd), seed, Kit2Harness.Options());
        const int left = 1;
        const int right = 2;
        const int innerLeft = 3;
        const int innerRight = 4;

        // Left partner at column 4 (claims 3–5), right partner at 14 (13–15), and the owing region's
        // two entities at 8 and 10 (claims 7–11), each group created in the order the case asks for.
        var groups = new List<Action>
        {
            () => h.Add(left, 4, Row),
            () => h.Add(right, 14, Row),
        };
        groups.Insert(owingRank, () =>
        {
            h.Add(innerLeft, 8, Row);
            h.Add(innerRight, 10, Row);
        });
        foreach (var add in groups)
        {
            add();
        }

        h.AssertClean();
        var owing = h.RegionOf(innerLeft);
        var leftRegion = h.RegionOf(left);
        var rightRegion = h.RegionOf(right);
        h.Equal(owing, h.RegionOf(innerRight), "the owing region holds both inner entities");
        h.Equal(3, new[] { owing, leftRegion, rightRegion }.Distinct().Count(), "regions before the tick");

        // All three tick. The inner entities step out until their claims share a section with each partner.
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(leftRegion);
        h.Start(rightRegion);
        h.Start(owing);
        h.MoveInTick(innerLeft, 6, Row);
        h.MoveInTick(innerRight, 12, Row);

        // The owing region's tick ends first: both partners still tick, so it becomes transient.
        var owingEnd = h.End(owing);
        h.Equal(RegionState.Transient, owingEnd.State, "tick-end state of the owing region");
        h.Equal(RegionState.Transient, h.StateOf(owing), "state of the owing region");
        h.Equal($"{Math.Min(leftRegion, rightRegion)},{Math.Max(leftRegion, rightRegion)}", string.Join(",", h.PendingOf(owing)), "pending partners of the owing region");

        // The first partner's tick end absorbs one side; the survivor still owes the other and stays transient.
        var (first, second) = leftEndsFirst ? (leftRegion, rightRegion) : (rightRegion, leftRegion);
        h.End(first);
        var survivor = h.RegionOf(innerLeft);
        h.Equal(Math.Min(owing, first), survivor, "survivor after the first partner's tick end");
        h.Equal(RegionState.Transient, h.StateOf(survivor), "state of the survivor while the other partner ticks");
        h.Equal(second.ToString(), string.Join(",", h.PendingOf(survivor)), "pending partners of the survivor");
        h.Equal(RegionState.Ticking, h.StateOf(second), "the later partner still ticks");
        h.True(h.RegionOf(leftEndsFirst ? right : left) == second, "the later partner's entity is not merged yet");

        // The later tick end completes the merge: one ready region holding all four entities.
        h.End(second);
        h.AssertClean();
        var final = Math.Min(Math.Min(owing, leftRegion), rightRegion);
        h.Equal(1, h.R.RegionIds.Length, "live regions after the later tick end");
        h.Equal(final, h.R.RegionIds[0], "the lowest id survives");
        h.Equal(RegionState.Ready, h.StateOf(final), "state of the merged region");
        h.Equal("1,2,3,4", string.Join(",", h.EntitiesOf(final)), "entities of the merged region");
        h.Equal(0, h.PendingOf(final).Length, "pending merges of the merged region");
    }

    /// <summary>
    /// A merge requested against a region that ticks at a slower cadence (20 Hz, every 3 base steps)
    /// while the requester ticks at 30 Hz (every 2): the requester waits as transient, cannot start
    /// another tick, and the merge completes at the slow region's tick end. Timers on both sides keep
    /// their deadlines, checked against a shadow model built from the observed ticks.
    /// </summary>
    [Theory]
    [InlineData(TimerMode.TickCounter, true, 1401)]
    [InlineData(TimerMode.TickCounter, false, 1402)]
    [InlineData(TimerMode.Absolute, true, 1403)]
    [InlineData(TimerMode.Absolute, false, 1404)]
    public void PendingMergeOnASlowerCadenceRegionCompletesAtItsTickEnd(TimerMode timerMode, bool slowHasLowerId, int seed)
    {
        var h = new Kit2Harness(nameof(PendingMergeOnASlowerCadenceRegionCompletesAtItsTickEnd), seed, Kit2Harness.Options(timerMode: timerMode));
        const int fastEntity = 1;
        const int slowEntity = 2;
        const long fastPeriod = 2;
        const long slowPeriod = 3;

        // The fast entity in column 4 (claims 3–5) walks one section right per tick toward the slow one
        // in column 9 (claims 8–10). Its third tick, at step 6, brings it to column 7 (claims 6–8):
        // the first shared section, on a step where both regions tick.
        if (slowHasLowerId)
        {
            h.Add(slowEntity, 9, Row);
            h.Add(fastEntity, 4, Row);
        }
        else
        {
            h.Add(fastEntity, 4, Row);
            h.Add(slowEntity, 9, Row);
        }

        h.AssertClean();
        var fast = h.RegionOf(fastEntity);
        var slow = h.RegionOf(slowEntity);
        var period = new Dictionary<long, long> { [fast] = fastPeriod, [slow] = slowPeriod };

        // Timers on both sides, set at step 0 before any tick: 6 owner ticks, or 10 base steps.
        var delay = timerMode == TimerMode.TickCounter ? 6L : 10L;
        var mark = h.Events.Log.Count;
        var timers = new Dictionary<int, ShadowTimer>
        {
            [h.R.AddTimer(fastEntity, delay)] = new(fastEntity, fast, h.R.Now, delay),
            [h.R.AddTimer(slowEntity, delay)] = new(slowEntity, slow, h.R.Now, delay),
        };

        const long touchStep = 6;
        for (long step = 1; step <= 30; step++)
        {
            h.AdvanceTo(step);

            // At a shared step the slow tick encloses the fast one: start slow, start fast, end fast, end slow.
            var due = h.R.RegionIds.ToArray().Where(r => step % period[r] == 0).OrderByDescending(r => period[r]).ToArray();
            foreach (var region in due)
            {
                h.Start(region);
            }

            if (due.Contains(fast) && step <= touchStep)
            {
                h.MoveInTick(fastEntity, h.SectionOf(fastEntity).X + 1, Row);
            }

            foreach (var region in due.Reverse().Where(r => h.StateOf(r) == RegionState.Ticking))
            {
                var result = h.End(region);
                if (step == touchStep && region == fast)
                {
                    // The claims now share a section, but the slow region still ticks.
                    h.True(h.ClaimOf(fastEntity).Intersect(h.ClaimOf(slowEntity)).Any(), "the claims share a section");
                    h.Equal(RegionState.Transient, result.State, "tick-end state of the fast region");
                    h.Equal(false, h.R.TryMarkTicking(fast), "a transient region cannot start a tick");
                    h.Equal(RegionState.Ticking, h.StateOf(slow), "the slow region still ticks");
                    h.True(h.RegionOf(fastEntity) != h.RegionOf(slowEntity), "no merge before the slow tick end");
                }
            }

            h.AssertClean();
            var together = h.RegionOf(fastEntity) == h.RegionOf(slowEntity);
            h.Equal(step >= touchStep, together, "one region from the slow region's tick end at the touch step on");
        }

        // The merge happened at the slow region's tick end on the touch step, and the survivor keeps its cadence.
        var merge = h.Events.MergesBetween(fast, slow).Single();
        h.Equal(touchStep, merge.Step, "step of the merge");
        h.Equal(Math.Min(fast, slow), merge.Absorber, "absorber");
        AssertTimersKeptTheirDeadlines(h, timerMode, mark, timers);
    }

    /// <summary>
    /// The shadow timer model (research D6): replays the observed events from the moment the timers
    /// were set, following each timer's entity across owner changes. In tick-counter mode a timer must
    /// fire on its owner's delay-th tick since it was set, counted across owners; in absolute mode on
    /// the first owner tick at or after its deadline. Each must fire exactly once.
    /// </summary>
    private static void AssertTimersKeptTheirDeadlines(Kit2Harness h, TimerMode mode, int mark, Dictionary<int, ShadowTimer> timers)
    {
        var owner = timers.Values.ToDictionary(t => t.Entity, t => t.Owner);
        var ownerTicks = timers.Keys.ToDictionary(t => t, _ => 0L);
        var expectedFireStep = timers.Keys.ToDictionary(t => t, _ => -1L);
        var fired = timers.Keys.ToDictionary(t => t, _ => 0);
        foreach (var e in h.Events.Log.Skip(mark))
        {
            switch (e)
            {
                case Kit2EntityOwner o when owner.ContainsKey(o.Entity):
                    owner[o.Entity] = o.New;
                    break;
                case Kit2TickStarted t:
                    foreach (var (id, timer) in timers)
                    {
                        if (owner[timer.Entity] != t.Region || expectedFireStep[id] >= 0)
                        {
                            continue;
                        }

                        ownerTicks[id]++;
                        var due = mode == TimerMode.TickCounter ? ownerTicks[id] == timer.Delay : t.Now >= timer.SetAtStep + timer.Delay;
                        if (due)
                        {
                            expectedFireStep[id] = t.Now;
                        }
                    }

                    break;
                case Kit2TimerFired f when timers.ContainsKey(f.TimerId):
                    fired[f.TimerId]++;
                    h.Equal(expectedFireStep[f.TimerId], f.Now, $"step timer {f.TimerId} fired on ({mode}, owner ticks since set {ownerTicks[f.TimerId]})");
                    h.Equal(owner[timers[f.TimerId].Entity], f.Region, $"region timer {f.TimerId} fired in");
                    break;
            }
        }

        foreach (var id in timers.Keys)
        {
            h.True(expectedFireStep[id] >= 0, $"timer {id} came due within the run");
            h.Equal(1, fired[id], $"times timer {id} fired");
        }
    }

    /// <summary>A timer as the shadow model sees it when it is set.</summary>
    private readonly record struct ShadowTimer(int Entity, long Owner, long SetAtStep, long Delay);
}
