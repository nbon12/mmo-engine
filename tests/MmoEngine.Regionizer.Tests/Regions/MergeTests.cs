using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Regions;

/// <summary>
/// Merges forced by claims (FR-005, research D3), driven in lockstep on a fixed time base with the
/// invariant checker at every tick boundary. Entities stand on row 8 of a 32×32 open world; with the
/// harness radii a section-anchored claim is the 3×3 block around the entity's section.
/// </summary>
public sealed class MergeTests
{
    private const int Row = 8;

    /// <summary>US1.1: claims that come to share a section merge at that tick end, caused by the mover.</summary>
    [Theory]
    [InlineData(EntityClass.Infantry, 1101)]
    [InlineData(EntityClass.Infantry, 1102)]
    [InlineData(EntityClass.GroundVehicle, 1103)]
    [InlineData(EntityClass.Aircraft, 1104)]
    [InlineData(EntityClass.Traveller, 1105)]
    public void ClaimsSharingASectionMergeAtTickEnd_CauseIsMoverClass(EntityClass moverClass, int seed)
    {
        // Given two entities in separate regions whose claims are three sections apart: the mover in
        // column 4 claims columns 3–5 and the other, in column 10, claims 9–11, leaving 6–8 unclaimed.
        var h = new Kit2Harness(nameof(ClaimsSharingASectionMergeAtTickEnd_CauseIsMoverClass), seed, Kit2Harness.Options(mergeGap: 0));
        const int mover = 1;
        const int other = 2;
        const int otherColumn = 10;
        h.Add(mover, 4, Row, moverClass);
        h.Add(other, otherColumn, Row);
        h.AssertClean();
        var moverRegion = h.RegionOf(mover);
        var otherRegion = h.RegionOf(other);
        h.True(moverRegion != otherRegion, "the two entities start in separate regions");

        // When the mover walks 4 m per tick toward the other until their claims share a section.
        const double stepMetres = 4;
        var mergedAt = -1L;
        for (var tick = 0; tick < 40 && mergedAt < 0; tick++)
        {
            h.Lockstep(() =>
            {
                var e = h.R.GetEntity(mover);
                h.MoveInTick(mover, e.X + stepMetres, e.Y);
                h.True(h.RegionOf(mover) != h.RegionOf(other), "no merge happens before the tick end");
            });

            // The 3×3 claims share column otherColumn − 1 once the mover stands in otherColumn − 2.
            var column = h.SectionOf(mover).X;
            var shouldShare = column >= otherColumn - 2;
            h.Equal(shouldShare, h.ClaimOf(mover).Intersect(h.ClaimOf(other)).Any(), $"claims share a section with the mover in column {column}");
            var together = h.RegionOf(mover) == h.RegionOf(other);
            h.Equal(shouldShare, together, $"one region after the tick end with the mover in column {column}");
            if (together)
            {
                mergedAt = h.R.Now;
            }
        }

        // Then both belong to one region, and the merge is recorded with the mover's class as its cause.
        h.True(mergedAt > 0, "the claims came to share a section within 40 ticks");
        var merges = h.Events.MergesBetween(moverRegion, otherRegion).ToList();
        h.Equal(1, merges.Count, "merges recorded between the two regions");
        h.Equal(mergedAt, merges[0].Step, "step of the recorded merge");
        h.Equal(moverClass, merges[0].Cause, "cause class of the merge");
        h.Equal(mover, merges[0].CauseEntity, "cause entity of the merge");
    }

    // CORE-BUG: US1.9 says claims one unclaimed section apart merge at gap 1; the core merges only claims in neighbouring sections (D3).
    // spec.md US1.9: "Given two claims separated by exactly one unclaimed section, When the tick ends
    // with a merge gap of 1, Then their regions merge". The core follows research D3 (a new section's
    // eight-neighbours' owners merge), which matches FR-005's "within the merge gap of one" and the
    // issue's "a one-section merge gap adds up to another √2 × s". Under it one unclaimed section
    // between the claims never merges them, so the gap-1 rows fail. AdjacentClaimsMergeOnlyWithGapOne
    // covers the D3 reading. Either the scenario's wording or the core must change.

    /// <summary>US1.9: with one unclaimed section between two claims, the merge gap decides whether they merge.</summary>
    [Theory]
    [InlineData(1, ClaimAnchoring.Section, true, 1901)]
    [InlineData(0, ClaimAnchoring.Section, false, 1902)]
    [InlineData(1, ClaimAnchoring.Position, true, 1903)]
    [InlineData(0, ClaimAnchoring.Position, false, 1904)]
    public void MergeGapDecidesWhetherOneSectionApartMerges(int mergeGap, ClaimAnchoring anchoring, bool expectMerge, int seed)
    {
        var h = new Kit2Harness(nameof(MergeGapDecidesWhetherOneSectionApartMerges), seed, Kit2Harness.Options(anchoring, mergeGap));
        var half = anchoring == ClaimAnchoring.Section ? 1 : 0;
        const int mover = 1;
        const int other = 2;
        const int otherColumn = 16;

        h.Add(mover, 4, Row);
        h.Add(other, otherColumn, Row);
        h.AssertClean();
        var moverRegion = h.RegionOf(mover);
        var otherRegion = h.RegionOf(other);
        h.True(moverRegion != otherRegion, "the two entities start in separate regions");

        // When the mover walks, during its tick, to where its claim ends at column
        // otherColumn − half − 2, so exactly one unclaimed section lies between the two claims.
        var targetColumn = otherColumn - (2 * half) - 2;
        h.Lockstep(() =>
        {
            h.MoveInTick(mover, targetColumn, Row);
            h.True(h.RegionOf(mover) != h.RegionOf(other), "no merge happens before the tick end");
        });

        var moverMax = h.ClaimOf(mover).Max(s => h.Topology.KeyOf(s).X);
        var otherMin = h.ClaimOf(other).Min(s => h.Topology.KeyOf(s).X);
        h.Equal(2, otherMin - moverMax, "column distance between the claims' facing edges");
        for (var y = Row - 1; y <= Row + 1; y++)
        {
            h.Equal(0, h.R.ClaimCountOf(h.Index(0, moverMax + 1, y)), $"claims on the section between, ({moverMax + 1},{y})");
        }

        // Then the regions merge with a gap of 1 and stay separate with a gap of 0.
        h.Equal(expectMerge, h.RegionOf(mover) == h.RegionOf(other), $"one region after the tick end with merge gap {mergeGap}");
        h.Equal(expectMerge, h.StateOf(Math.Max(moverRegion, otherRegion)) == RegionState.Dead, "the higher-id region was absorbed");

        // The outcome holds on later ticks.
        for (var tick = 0; tick < 3; tick++)
        {
            h.Lockstep();
            h.Equal(expectMerge, h.RegionOf(mover) == h.RegionOf(other), "one region on a later tick");
        }
    }

    /// <summary>
    /// Research D3's reading of the merge gap: claims in neighbouring sections (no section between
    /// them, none shared) merge with a gap of 1 and stay separate with a gap of 0.
    /// </summary>
    [Theory]
    [InlineData(1, ClaimAnchoring.Section, true, 1911)]
    [InlineData(0, ClaimAnchoring.Section, false, 1912)]
    [InlineData(1, ClaimAnchoring.Position, true, 1913)]
    [InlineData(0, ClaimAnchoring.Position, false, 1914)]
    public void AdjacentClaimsMergeOnlyWithGapOne(int mergeGap, ClaimAnchoring anchoring, bool expectMerge, int seed)
    {
        var h = new Kit2Harness(nameof(AdjacentClaimsMergeOnlyWithGapOne), seed, Kit2Harness.Options(anchoring, mergeGap));
        var half = anchoring == ClaimAnchoring.Section ? 1 : 0;
        const int mover = 1;
        const int other = 2;
        const int otherColumn = 16;
        h.Add(mover, 4, Row);
        h.Add(other, otherColumn, Row);
        h.AssertClean();
        h.True(h.RegionOf(mover) != h.RegionOf(other), "the two entities start in separate regions");

        // The mover's claim ends at otherColumn − half − 1, beside the other claim's first column.
        var targetColumn = otherColumn - (2 * half) - 1;
        h.Lockstep(() =>
        {
            h.MoveInTick(mover, targetColumn, Row);
            h.True(h.RegionOf(mover) != h.RegionOf(other), "no merge happens before the tick end");
        });

        var moverMax = h.ClaimOf(mover).Max(s => h.Topology.KeyOf(s).X);
        var otherMin = h.ClaimOf(other).Min(s => h.Topology.KeyOf(s).X);
        h.Equal(1, otherMin - moverMax, "column distance between the claims' facing edges");
        h.True(!h.ClaimOf(mover).Intersect(h.ClaimOf(other)).Any(), "the claims share no section");
        h.Equal(expectMerge, h.RegionOf(mover) == h.RegionOf(other), $"one region after the tick end with merge gap {mergeGap}");
    }

    // CORE-BUG: a zero claim radius in section mode claims the whole 3×3 block, so zero-radius entities in neighbouring sections merge.
    // spec.md, Edge Cases: "a claim radius of zero claims only the entity's own section." In section
    // mode the core's square-to-square distance is 0 for every edge or corner neighbour, and 0 <= r²
    // holds at r = 0, so each claim covers the neighbours too and the two claims below share sections.

    /// <summary>
    /// A zero claim radius claims only the entity's own section (spec edge case), so two such entities
    /// in neighbouring sections share no section and, with merge gap 0, stay in separate regions.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 2401)]
    [InlineData(ClaimAnchoring.Position, 2402)]
    public void ZeroRadiusEntitiesInNeighbouringSectionsStaySeparate(ClaimAnchoring anchoring, int seed)
    {
        var h = new Kit2Harness(nameof(ZeroRadiusEntitiesInNeighbouringSectionsStaySeparate), seed, Kit2Harness.Options(anchoring, mergeGap: 0, radius: 0));
        h.Add(1, 4, Row);
        h.Add(2, 8, Row);
        h.AssertClean();
        h.True(h.RegionOf(1) != h.RegionOf(2), "the two entities start in separate regions");
        h.Lockstep(() => h.MoveInTick(2, 5, Row));

        h.SameSections([h.Index(0, 4, Row)], h.ClaimOf(1), "claim of the first zero-radius entity");
        h.SameSections([h.Index(0, 5, Row)], h.ClaimOf(2), "claim of the second zero-radius entity");
        h.True(h.RegionOf(1) != h.RegionOf(2), "zero-radius entities in neighbouring sections stay in separate regions");
    }

    /// <summary>Three regions chain-merge in one tick end and end as one region, whatever the order of their tick ends.</summary>
    [Theory]
    [InlineData(new[] { 0, 1, 2 }, 2101)]
    [InlineData(new[] { 0, 2, 1 }, 2102)]
    [InlineData(new[] { 1, 0, 2 }, 2103)]
    [InlineData(new[] { 1, 2, 0 }, 2104)]
    [InlineData(new[] { 2, 0, 1 }, 2105)]
    [InlineData(new[] { 2, 1, 0 }, 2106)]
    public void ThreeRegionChainMergeEndsAsOneRegionWhateverTheOrder(int[] endOrder, int seed)
    {
        // Given three regions on one row: left (column 4), middle (10) and right (16), claims apart.
        var h = new Kit2Harness(nameof(ThreeRegionChainMergeEndsAsOneRegionWhateverTheOrder), seed, Kit2Harness.Options());
        int[] entities = [1, 2, 3];
        h.Add(1, 4, Row);
        h.Add(2, 10, Row);
        h.Add(3, 16, Row);
        h.AssertClean();
        var regions = entities.Select(h.RegionOf).ToArray();
        h.Equal(3, regions.Distinct().Count(), "regions before the tick");

        // When, in one tick, the left and right entities walk into claims that share a section with
        // the middle one, and the three ticks end in the given order.
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        foreach (var region in regions)
        {
            h.Start(region);
        }

        h.MoveInTick(1, 8, Row);
        h.MoveInTick(3, 12, Row);
        h.Equal(3, entities.Select(h.RegionOf).Distinct().Count(), "regions before any tick end");
        foreach (var at in endOrder)
        {
            h.End(regions[at]);
        }

        h.AssertClean();

        // Then the result is one region, the oldest (lowest id), holding all three entities.
        var survivor = regions.Min();
        h.Equal(1, h.R.RegionIds.Length, "live regions after the chain merge");
        h.Equal(survivor, h.R.RegionIds[0], "surviving region");
        h.Equal("1,2,3", string.Join(",", h.EntitiesOf(survivor)), "entities of the surviving region");
        foreach (var region in regions.Where(r => r != survivor))
        {
            h.Equal(RegionState.Dead, h.StateOf(region), $"state of absorbed region {region}");
        }
    }

    /// <summary>When two regions merge, the lower id absorbs the higher and keeps its age, whichever side's claim forced it.</summary>
    [Theory]
    [InlineData(true, 2201)]
    [InlineData(false, 2202)]
    public void LowerIdAbsorbsAndKeepsItsAge(bool lowerIdMoves, int seed)
    {
        var h = new Kit2Harness(nameof(LowerIdAbsorbsAndKeepsItsAge), seed, Kit2Harness.Options());

        // The lower-id region is created at step 0, the higher one a tick later.
        h.Add(1, 4, Row);
        var older = h.RegionOf(1);
        h.Lockstep();
        h.Add(2, 10, Row);
        var younger = h.RegionOf(2);
        h.AssertClean();
        h.True(older < younger, "the older region has the lower id");
        var olderCreatedAt = h.R.GetRegion(older).CreatedAt;
        h.True(olderCreatedAt < h.R.GetRegion(younger).CreatedAt, "the older region was created first");

        // One side walks into a claim that shares a section with the other.
        h.Lockstep(() =>
        {
            if (lowerIdMoves)
            {
                h.MoveInTick(1, 8, Row);
            }
            else
            {
                h.MoveInTick(2, 6, Row);
            }
        });

        h.Equal(older, h.RegionOf(1), "region of the lower-id side's entity");
        h.Equal(older, h.RegionOf(2), "region of the higher-id side's entity");
        h.Equal(RegionState.Dead, h.StateOf(younger), "state of the higher-id region");
        h.Equal(olderCreatedAt, h.R.GetRegion(older).CreatedAt, "creation time kept by the absorber");
        var merge = h.Events.MergesBetween(older, younger).Single();
        h.Equal(older, merge.Absorber, "absorber");
        h.Equal(younger, merge.Absorbed, "absorbed");
        h.Equal(1, h.Events.Of<Kit2Died>().Count(d => d.Region == younger), "deaths recorded for the absorbed region");
    }

    /// <summary>A claim landing on a dead section (owned, zero claims) forces a merge with its owner (FR-005, D3).</summary>
    [Theory]
    [InlineData(2301)]
    [InlineData(2302)]
    public void ClaimOnADeadSectionForcesAMerge(int seed)
    {
        var h = new Kit2Harness(nameof(ClaimOnADeadSectionForcesAMerge), seed, Kit2Harness.Options(mergeGap: 0));
        const int leaver = 1;
        const int arriver = 2;
        h.Add(leaver, 6, Row);
        h.Add(arriver, 0, Row);
        var owner = h.RegionOf(leaver);
        var arriverRegion = h.RegionOf(arriver);
        h.AssertClean();

        // The leaver walks two sections right; columns 5–6 of its old claim go dead but stay owned
        // (splits are off, so no split check ever drops them).
        h.Lockstep(() => h.MoveInTick(leaver, 7, Row));
        h.Lockstep(() => h.MoveInTick(leaver, 8, Row));
        var deadColumn5 = Enumerable.Range(Row - 1, 3).Select(y => h.Index(0, 5, y)).ToArray();
        foreach (var section in deadColumn5)
        {
            h.Equal(owner, h.R.OwnerOf(section), $"owner of dead section {h.Keys([section])}");
            h.Equal(0, h.R.ClaimCountOf(section), $"claims on dead section {h.Keys([section])}");
        }

        // Control: one section short (claim 1–3, unowned column 4 between) does not merge.
        h.Lockstep(() => h.MoveInTick(arriver, 2, Row));
        h.True(h.RegionOf(arriver) != owner, "no merge while the claim stops beside the dead sections");

        // The arriver's claim (columns 3–5) lands on the dead column 5: the regions merge at the tick end.
        h.Lockstep(() =>
        {
            h.MoveInTick(arriver, 4, Row);
            h.True(h.RegionOf(arriver) != owner, "no merge before the tick end");
        });

        h.Equal(h.RegionOf(leaver), h.RegionOf(arriver), "the two entities share a region");
        h.Equal(Math.Min(owner, arriverRegion), h.RegionOf(arriver), "the lower id absorbed");
        var merge = h.Events.MergesBetween(owner, arriverRegion).Single();
        h.Equal(arriver, merge.CauseEntity, "cause entity of the merge");
    }
}
