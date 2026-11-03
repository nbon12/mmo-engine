using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Regions;

/// <summary>
/// Tick end and splits (FR-007 to FR-010, research D4), driven in lockstep on a fixed time base with
/// the invariant checker at every tick boundary. Entities stand on row 8 of a 32×32 open world; with
/// the harness radii a section-anchored claim is the 3×3 block around the entity's section and a
/// position-anchored one is the entity's own section. Ticks end at even steps, so the odd minimum
/// ages used here are never met with equality; the even-T_min test checks the equality case.
/// </summary>
public sealed class TickEndAndSplitTests
{
    private const int Row = 8;
    private const int Left1 = 1;
    private const int Left2 = 2;
    private const int Right1 = 3;
    private const int Right2 = 4;

    /// <summary>
    /// US1.3: a region holding two islands of entities that meets every damping threshold splits at
    /// its tick end into two children; each entity belongs to the child owning its section, and each
    /// child carries the parent's timers for its entities and the parent's tick counter.
    /// </summary>
    [Theory]
    [InlineData(TimerMode.TickCounter, ClaimAnchoring.Section, 3101)]
    [InlineData(TimerMode.TickCounter, ClaimAnchoring.Position, 3102)]
    [InlineData(TimerMode.Absolute, ClaimAnchoring.Section, 3103)]
    [InlineData(TimerMode.Absolute, ClaimAnchoring.Position, 3104)]
    public void TwoIslandsMeetingDampingSplit_EntitiesAndTimersFollowSections(TimerMode timerMode, ClaimAnchoring anchoring, int seed)
    {
        // Given a region holding two islands (N_min 9, p_dead 20%, T_min 13 steps).
        const long minAge = 13;
        var h = new Kit2Harness(
            nameof(TwoIslandsMeetingDampingSplit_EntitiesAndTimersFollowSections),
            seed,
            Kit2Harness.Options(anchoring, splitMinSections: 9, splitMinDeadPercent: 20, splitMinAgeSteps: minAge, timerMode: timerMode));
        var parent = ArrangeOneRegionOfTwoGroups(h);
        var timerOf = new Dictionary<int, int>
        {
            [Left1] = h.R.AddTimer(Left1, 1000),
            [Left2] = h.R.AddTimer(Left2, 2000),
            [Right1] = h.R.AddTimer(Right1, 3000),
            [Right2] = h.R.AddTimer(Right2, 4000),
        };
        var visited = WalkGroupsApart(h, parent, minAge);
        while (h.R.Now + Kit2Harness.LockstepPeriod - h.R.GetRegion(parent).CreatedAt < minAge)
        {
            h.Equal(0, h.Lockstep()[parent].SplitChildren, "split children of a region younger than T_min");
        }

        var region = h.R.GetRegion(parent);
        var live = Footprints(h, Left1, Left2, Right1, Right2);
        h.SameSections(visited, region.Sections.ToArray(), "sections before the split (dead ones still owned)");
        h.True(region.SectionCount >= 9, "the region owns at least N_min sections");
        h.True(region.DeadSectionCount * 100 >= 20 * region.SectionCount, "at least p_dead of its sections are dead");
        var parentTimers = region.Timers.ToArray();
        var tickCounterBefore = region.TickCounter;

        // When its tick ends, once it is older than T_min.
        var result = h.Lockstep()[parent];
        h.True(h.R.Now - h.R.GetRegion(parent).CreatedAt > minAge, "the region is older than T_min");

        // Then it splits into two children ...
        h.Equal(2, result.SplitChildren, "regions the parent split into");
        h.Equal(2, h.R.RegionIds.Length, "live regions after the split");
        var left = h.RegionOf(Left1);
        var right = h.RegionOf(Right1);
        h.True(left != right, "the two groups are in different regions");
        h.Equal(left, h.RegionOf(Left2), "region of the second left entity");
        h.Equal(right, h.RegionOf(Right2), "region of the second right entity");
        var split = h.Events.Of<Kit2Split>().Single();
        h.Equal(parent, split.Parent, "parent of the recorded split");
        h.Equal($"{Math.Min(left, right)},{Math.Max(left, right)}", string.Join(",", split.Children.Order()), "children of the recorded split");

        // ... each entity belongs to the child that owns its section, and each child owns exactly its island ...
        foreach (var entity in new[] { Left1, Left2, Right1, Right2 })
        {
            var key = h.SectionOf(entity);
            h.Equal(h.RegionOf(entity), h.R.OwnerOf(h.Index(key.Layer, key.X, key.Y)), $"owner of entity {entity}'s section");
        }

        h.SameSections(Footprints(h, Left1, Left2), h.SectionsOf(left), "sections of the left child");
        h.SameSections(Footprints(h, Right1, Right2), h.SectionsOf(right), "sections of the right child");
        foreach (var section in visited.Except(live))
        {
            h.Equal(0L, h.R.OwnerOf(section), $"owner of dropped dead section {h.Keys([section])}");
        }

        // ... and each child carries the parent's timers for its entities and the parent's tick counter.
        foreach (var (child, members) in new[] { (left, new[] { Left1, Left2 }), (right, new[] { Right1, Right2 }) })
        {
            var expected = parentTimers.Where(t => members.Contains(t.EntityId)).OrderBy(t => t.TimerId).ToArray();
            var actual = h.R.GetRegion(child).Timers.ToArray().OrderBy(t => t.TimerId).ToArray();
            h.Equal(string.Join(" ", expected), string.Join(" ", actual), $"timers of child {child}");
            h.Equal(string.Join(",", members.Select(m => timerOf[m])), string.Join(",", actual.Select(t => t.TimerId)), $"timer ids of child {child}");
            h.Equal(tickCounterBefore + 1, h.R.GetRegion(child).TickCounter, $"tick counter of child {child} (the parent's)");
        }
    }

    /// <summary>US1.4: a region holding two islands that is younger than T_min does not split at its tick ends; once older, it does.</summary>
    [Theory]
    [InlineData(13, ClaimAnchoring.Section, 3201)]
    [InlineData(21, ClaimAnchoring.Section, 3202)]
    [InlineData(21, ClaimAnchoring.Position, 3203)]
    public void RegionYoungerThanTminDoesNotSplit(long minAge, ClaimAnchoring anchoring, int seed)
    {
        var h = new Kit2Harness(
            nameof(RegionYoungerThanTminDoesNotSplit),
            seed,
            Kit2Harness.Options(anchoring, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: minAge));
        var parent = ArrangeOneRegionOfTwoGroups(h);
        var visited = WalkGroupsApart(h, parent, minAge);
        var createdAt = h.R.GetRegion(parent).CreatedAt;

        // Given the two islands, every tick end while the region is younger than T_min leaves it whole.
        while (h.R.Now + Kit2Harness.LockstepPeriod - createdAt < minAge)
        {
            var result = h.Lockstep()[parent];
            h.Equal(0, result.SplitChildren, $"split children at age {h.R.Now - createdAt} (T_min {minAge})");
            h.Equal(1, h.R.RegionIds.Length, "live regions");
            h.SameSections(visited, h.SectionsOf(parent), "sections of the unsplit region (dead ones kept)");
        }

        // Control: the first tick end at an age past T_min splits it.
        h.Equal(2, h.Lockstep()[parent].SplitChildren, $"split children at age {h.R.Now - createdAt} (T_min {minAge})");
        h.Equal(minAge + 1, h.R.Now - createdAt, "age at the split");
    }

    /// <summary>
    /// FR-008's "at least T_min old" includes equality: with an even T_min a tick end falls exactly at
    /// age T_min, and the region splits there. At the tick end one tick earlier (age T_min − 2) every
    /// other threshold already holds, and it does not split.
    /// </summary>
    [Theory]
    [InlineData(12, ClaimAnchoring.Section, 3251)]
    [InlineData(12, ClaimAnchoring.Position, 3252)]
    [InlineData(16, ClaimAnchoring.Section, 3253)]
    public void EvenTminSplitsAtTheTickEndWhereAgeEqualsTmin(long minAge, ClaimAnchoring anchoring, int seed)
    {
        // Given a region holding two islands, with N_min 1 and p_dead 0 so only its age can block a split.
        var h = new Kit2Harness(
            nameof(EvenTminSplitsAtTheTickEndWhereAgeEqualsTmin),
            seed,
            Kit2Harness.Options(anchoring, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: minAge));
        var parent = ArrangeOneRegionOfTwoGroups(h);
        var createdAt = h.R.GetRegion(parent).CreatedAt;
        h.Equal(0L, createdAt, "creation step of the region");
        _ = WalkGroupsApart(h, parent, minAge);

        // When ticks end up to one tick before T_min, Then none splits it.
        var last = default(TickEndResult);
        var lastAge = -1L;
        while (h.R.Now + Kit2Harness.LockstepPeriod - createdAt < minAge)
        {
            last = h.Lockstep()[parent];
            lastAge = h.R.Now - createdAt;
            h.Equal(0, last.SplitChildren, $"split children at age {lastAge} (T_min {minAge})");
        }

        h.Equal(minAge - Kit2Harness.LockstepPeriod, lastAge, "age at the last tick end before T_min");
        h.Equal(RegionState.Ready, last.State, "state after the tick end one tick before T_min");
        h.Equal(1, h.R.RegionIds.Length, "live regions one tick before T_min");

        // When the tick end falls at exactly T_min, Then it splits there.
        var result = h.Lockstep()[parent];
        h.Equal(minAge, h.R.Now - createdAt, "age at this tick end");
        h.Equal(2, result.SplitChildren, $"split children at age exactly T_min {minAge}");
        h.Equal(2, h.R.RegionIds.Length, "live regions after the split");
        h.True(h.RegionOf(Left1) != h.RegionOf(Right1), "the two groups are in different regions");
    }

    /// <summary>
    /// The split happens only when the region owns at least N_min sections and at least p_dead of them
    /// are dead. The layout gives 27 owned sections (columns 5–13 of rows 7–9) of which 9 are dead
    /// (columns 8–10), 33.3%; the rows sit on each side of those values.
    /// </summary>
    [Theory]
    [InlineData(27, 33.0, true, 3301)]
    [InlineData(28, 33.0, false, 3302)]
    [InlineData(27, 34.0, false, 3303)]
    [InlineData(1, 0.0, true, 3304)]
    [InlineData(1, 100.0, false, 3305)]
    public void SplitHappensOnlyWhenEveryDampingThresholdHolds(int minSections, double minDeadPercent, bool expectSplit, int seed)
    {
        var h = new Kit2Harness(
            nameof(SplitHappensOnlyWhenEveryDampingThresholdHolds),
            seed,
            Kit2Harness.Options(splitMinSections: minSections, splitMinDeadPercent: minDeadPercent, splitMinAgeSteps: 0));
        h.Add(Left1, 9, Row);
        h.Add(Right1, 9, Row);
        var parent = h.RegionOf(Left1);
        var visited = h.Footprint(Left1);
        h.MoveAtBoundary(Left1, 6, Row);
        visited.UnionWith(h.Footprint(Left1));
        h.MoveAtBoundary(Right1, 12, Row);
        visited.UnionWith(h.Footprint(Right1));
        h.AssertClean();
        var dead = visited.Except(Footprints(h, Left1, Right1)).ToArray();
        h.Equal(27, visited.Count, "sections the layout owns");
        h.Equal(9, dead.Length, "dead sections in the layout");
        h.Equal(27, h.R.GetRegion(parent).SectionCount, "sections the region owns");
        h.Equal(9, h.R.GetRegion(parent).DeadSectionCount, "dead sections the region counts");

        var result = h.Lockstep()[parent];

        h.Equal(expectSplit ? 2 : 0, result.SplitChildren, $"split children with N_min {minSections}, p_dead {minDeadPercent}");
        h.Equal(expectSplit ? 2 : 1, h.R.RegionIds.Length, "live regions after the tick end");
        h.Equal(expectSplit, h.RegionOf(Left1) != h.RegionOf(Right1), "the two entities are in different regions");
        foreach (var section in dead)
        {
            h.Equal(expectSplit ? 0L : parent, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])}");
        }
    }

    /// <summary>Dead sections stay owned by their region through every tick end until the split check runs, which drops them.</summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 3401)]
    [InlineData(ClaimAnchoring.Position, 3402)]
    public void DeadSectionsStayOwnedUntilTheSplitCheck(ClaimAnchoring anchoring, int seed)
    {
        const long minAge = 13;
        var h = new Kit2Harness(
            nameof(DeadSectionsStayOwnedUntilTheSplitCheck),
            seed,
            Kit2Harness.Options(anchoring, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: minAge));
        const int walker = 1;
        h.Add(walker, 4, Row);
        var region = h.RegionOf(walker);
        var createdAt = h.R.GetRegion(region).CreatedAt;
        var visited = h.Footprint(walker);
        h.AssertClean();

        // The walker moves one section right per tick; its old sections go dead but stay owned.
        while (h.R.Now + Kit2Harness.LockstepPeriod - createdAt < minAge)
        {
            var result = h.Lockstep(() =>
            {
                if (h.SectionOf(walker).X < 8)
                {
                    h.MoveInTick(walker, h.SectionOf(walker).X + 1, Row);
                }
            })[region];
            visited.UnionWith(h.Footprint(walker));
            var dead = visited.Except(h.Footprint(walker)).ToArray();
            h.Equal(0, result.SplitChildren, "split children before T_min");
            h.SameSections(visited, h.SectionsOf(region), "sections owned before the split check");
            h.Equal(dead.Length, h.R.GetRegion(region).DeadSectionCount, "dead sections counted before the split check");
            foreach (var section in dead)
            {
                h.Equal(region, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])}");
                h.Equal(0, h.R.ClaimCountOf(section), $"claims on dead section {h.Keys([section])}");
            }
        }

        h.True(visited.Count > h.Footprint(walker).Count, "the walk left dead sections behind");

        // The first tick end past T_min runs the split check: one island, so no split, but the dead sections go.
        var check = h.Lockstep()[region];
        h.Equal(0, check.SplitChildren, "split children of a single island");
        h.Equal(RegionState.Ready, h.StateOf(region), "the region keeps its id and stays ready");
        h.SameSections(h.Footprint(walker), h.SectionsOf(region), "sections after the split check");
        h.Equal(0, h.R.GetRegion(region).DeadSectionCount, "dead sections after the split check");
        foreach (var section in visited.Except(h.Footprint(walker)))
        {
            h.Equal(0L, h.R.OwnerOf(section), $"owner of dropped section {h.Keys([section])}");
        }
    }

    /// <summary>
    /// A split never creates a child for an island holding only dead sections: the bridge between two
    /// groups goes dead when its entities leave, and the split yields one child per live group only.
    /// </summary>
    [Theory]
    [InlineData(0, 3501)]
    [InlineData(1, 3502)]
    public void IslandOfOnlyDeadSectionsGetsNoChild(int mergeGap, int seed)
    {
        var h = new Kit2Harness(
            nameof(IslandOfOnlyDeadSectionsGetsNoChild),
            seed,
            Kit2Harness.Options(mergeGap: mergeGap, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: 0));
        const int left = 1;
        const int right = 2;
        int[] bridge = [3, 4, 5];

        // Overlapping claims chain from column 2 to 12: left at 3, bridge at 5, 7 and 9, right at 11.
        h.Add(left, 3, Row);
        h.Add(bridge[0], 5, Row);
        h.Add(bridge[1], 7, Row);
        h.Add(bridge[2], 9, Row);
        h.Add(right, 11, Row);
        var parent = h.RegionOf(left);
        var owned = h.SectionsOf(parent);
        h.Equal(1, h.R.RegionIds.Length, "the chain is one region");

        // The bridge leaves at a boundary; columns 5–9 go dead.
        foreach (var entity in bridge)
        {
            h.R.RemoveEntity(entity);
        }

        h.AssertClean();
        var dead = owned.Except(Footprints(h, left, right)).ToArray();
        h.Equal(15, dead.Length, "dead sections between the two groups");

        var result = h.Lockstep()[parent];

        h.Equal(2, result.SplitChildren, "regions after the split");
        h.Equal(2, h.R.RegionIds.Length, "live regions: one per live island");
        foreach (var id in h.R.RegionIds.ToArray())
        {
            h.True(h.EntitiesOf(id).Length > 0, $"region {id} holds entities");
            h.Equal(0, h.R.GetRegion(id).DeadSectionCount, $"dead sections of region {id}");
        }

        h.SameSections(h.Footprint(left), h.SectionsOf(h.RegionOf(left)), "sections of the left child");
        h.SameSections(h.Footprint(right), h.SectionsOf(h.RegionOf(right)), "sections of the right child");
        foreach (var section in dead)
        {
            h.Equal(0L, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])}");
        }
    }

    /// <summary>
    /// The largest island keeps the parent's id and the others become new regions; on a tie the island
    /// holding the lowest section index (the left one, on the same rows) keeps it. A left or right
    /// group of two entities claims 12 sections, a group of one claims 9.
    /// </summary>
    [Theory]
    [InlineData(2, 1, true, 3601)]
    [InlineData(1, 2, false, 3602)]
    [InlineData(1, 1, true, 3603)]
    [InlineData(2, 2, true, 3604)]
    public void LargestIslandKeepsTheParentId(int leftCount, int rightCount, bool leftKeeps, int seed)
    {
        var h = new Kit2Harness(
            nameof(LargestIslandKeepsTheParentId),
            seed,
            Kit2Harness.Options(splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: 0));
        int[] leftGroup = leftCount == 2 ? [1, 2] : [1];
        int[] rightGroup = rightCount == 2 ? [6, 7] : [6];
        int[] bridge = [3, 4, 5];

        // Left at column 4 (and 3), bridge at 6, 8 and 10, right at 12 (and 13): one chained region.
        h.Add(1, 4, Row);
        if (leftCount == 2)
        {
            h.Add(2, 3, Row);
        }

        h.Add(3, 6, Row);
        h.Add(4, 8, Row);
        h.Add(5, 10, Row);
        h.Add(6, 12, Row);
        if (rightCount == 2)
        {
            h.Add(7, 13, Row);
        }

        var parent = h.RegionOf(1);
        h.Equal(1, h.R.RegionIds.Length, "the chain is one region");
        foreach (var entity in bridge)
        {
            h.R.RemoveEntity(entity);
        }

        h.AssertClean();
        var leftSize = Footprints(h, leftGroup).Count;
        var rightSize = Footprints(h, rightGroup).Count;
        h.Equal(leftCount == 2 ? 12 : 9, leftSize, "sections of the left island");
        h.Equal(rightCount == 2 ? 12 : 9, rightSize, "sections of the right island");

        var parentCreatedAt = h.R.GetRegion(parent).CreatedAt;

        var result = h.Lockstep()[parent];

        h.Equal(2, result.SplitChildren, "regions after the split");
        var (keeperGroup, otherGroup) = leftKeeps ? (leftGroup, rightGroup) : (rightGroup, leftGroup);

        // FR-008: the child keeping the parent's id keeps its age; every other child starts at the split.
        var splitAt = h.R.Now;
        h.True(splitAt != parentCreatedAt, $"the split (step {splitAt}) comes after the parent's creation (step {parentCreatedAt})");
        h.Equal(parentCreatedAt, h.R.GetRegion(parent).CreatedAt, "creation step of the keeper");
        foreach (var id in h.R.RegionIds.ToArray().Where(id => id != parent))
        {
            h.Equal(splitAt, h.R.GetRegion(id).CreatedAt, $"creation step of new child {id}");
        }

        h.Equal(parent, h.RegionOf(keeperGroup[0]), $"region of the {(leftKeeps ? "left" : "right")} island (sizes {leftSize} and {rightSize})");
        var child = h.RegionOf(otherGroup[0]);
        h.True(child > parent, $"the other island's region {child} is new, after parent {parent}");
        foreach (var entity in keeperGroup)
        {
            h.Equal(parent, h.RegionOf(entity), $"region of keeper-island entity {entity}");
        }

        foreach (var entity in otherGroup)
        {
            h.Equal(child, h.RegionOf(entity), $"region of other-island entity {entity}");
        }

        var created = h.Events.Of<Kit2Created>().Single(c => c.Region == child);
        h.Equal(RegionCreationReason.Split, created.Reason, "creation reason of the new child");
        h.Equal(parent, created.Parent, "parent recorded for the new child");
    }

    /// <summary>
    /// FR-007's order: a region that still owes a merge to a ticking region becomes transient at its
    /// tick end and neither drops its dead sections nor splits, even though damping holds. The split
    /// runs once the merge completes, at the partner's tick end.
    /// </summary>
    [Theory]
    [InlineData(3701)]
    [InlineData(3702)]
    public void RegionOwingAMergeBecomesTransientInsteadOfSplitting(int seed)
    {
        var h = new Kit2Harness(
            nameof(RegionOwingAMergeBecomesTransientInsteadOfSplitting),
            seed,
            Kit2Harness.Options(splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: 0));
        const int partnerEntity = 1;
        const int left = 2;
        const int right = 6;
        int[] bridge = [3, 4, 5];

        // The partner at column 1 (claims 0–2); the other region chains left (6), bridge (8, 10, 12) and right (14).
        h.Add(partnerEntity, 1, Row);
        h.Add(left, 6, Row);
        h.Add(bridge[0], 8, Row);
        h.Add(bridge[1], 10, Row);
        h.Add(bridge[2], 12, Row);
        h.Add(right, 14, Row);
        var partner = h.RegionOf(partnerEntity);
        var owing = h.RegionOf(left);
        foreach (var entity in bridge)
        {
            h.R.RemoveEntity(entity);
        }

        h.AssertClean();
        h.True(partner < owing, "the partner region has the lower id");

        // Both tick; the left entity steps to column 3 (claims 2–4), sharing column 2 with the partner.
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(partner);
        h.Start(owing);
        var ownedBefore = h.SectionsOf(owing);
        h.MoveInTick(left, 3, Row);
        var dead = ownedBefore.Except(Footprints(h, left, right)).ToArray();

        // The owing region's tick ends while the partner ticks: transient, no split, dead sections kept.
        var owingEnd = h.End(owing);
        h.Equal(RegionState.Transient, owingEnd.State, "tick-end state of the owing region");
        h.Equal(0, owingEnd.SplitChildren, "split children of the owing region");
        h.Equal(dead.Length, h.R.GetRegion(owing).DeadSectionCount, "dead sections of the owing region");
        foreach (var section in dead)
        {
            h.Equal(owing, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])} after the transient tick end");
        }

        // The partner's tick end merges the owing region in, then runs the split check on the result.
        var partnerEnd = h.End(partner);
        h.AssertClean();
        h.Equal(2, partnerEnd.SplitChildren, "regions after the partner's tick end");
        h.Equal(partner, h.RegionOf(partnerEntity), "the partner keeps its id");
        h.Equal(partner, h.RegionOf(left), "the left entity shares the partner's island");
        h.True(h.RegionOf(right) != partner, "the right entity's island became its own region");
        foreach (var section in dead)
        {
            h.Equal(0L, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])} after the split check");
        }
    }

    /// <summary>Adds four entities in one section (column 9), so they form one region, and returns it.</summary>
    private static long ArrangeOneRegionOfTwoGroups(Kit2Harness h)
    {
        h.Add(Left1, 9, Row);
        h.Add(Left2, 9, Row);
        h.Add(Right1, 9, Row);
        h.Add(Right2, 9, Row);
        h.AssertClean();
        var region = h.RegionOf(Left1);
        h.Equal(1, h.R.RegionIds.Length, "the four entities form one region");
        return region;
    }

    /// <summary>
    /// Four lockstep ticks in which the left group steps one section left and the right group one
    /// section right (to columns 5 and 13). The region is younger than T_min throughout and does not
    /// split. Returns every section the claims covered on the way, which the region still owns.
    /// </summary>
    private static HashSet<int> WalkGroupsApart(Kit2Harness h, long region, long minAge)
    {
        var visited = Footprints(h, Left1, Left2, Right1, Right2);
        for (var tick = 0; tick < 4; tick++)
        {
            var result = h.Lockstep(() =>
            {
                foreach (var entity in new[] { Left1, Left2 })
                {
                    h.MoveInTick(entity, h.SectionOf(entity).X - 1, Row);
                }

                foreach (var entity in new[] { Right1, Right2 })
                {
                    h.MoveInTick(entity, h.SectionOf(entity).X + 1, Row);
                }
            })[region];
            h.True(h.R.Now - h.R.GetRegion(region).CreatedAt < minAge, "the walk ends before T_min");
            h.Equal(0, result.SplitChildren, "split children while younger than T_min");
            visited.UnionWith(Footprints(h, Left1, Left2, Right1, Right2));
        }

        h.Equal(1, h.R.RegionIds.Length, "one region after the walk");
        return visited;
    }

    /// <summary>The union of the claim oracle over several entities.</summary>
    private static HashSet<int> Footprints(Kit2Harness h, params int[] entities)
    {
        var all = new HashSet<int>();
        foreach (var entity in entities)
        {
            all.UnionWith(h.Footprint(entity));
        }

        return all;
    }
}
