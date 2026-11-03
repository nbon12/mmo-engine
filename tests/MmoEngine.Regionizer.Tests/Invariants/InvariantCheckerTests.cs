using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Invariants;

/// <summary>
/// The invariant checker (FR-014): each test starts from a clean state, breaks one rule through the
/// internal test hooks, and asserts that exactly that rule is reported.
/// </summary>
public sealed class InvariantCheckerTests
{
    private const int InfantryA = 1;
    private const int InfantryB = 2;
    private const int InfantryC = 3;

    /// <summary>Which kind of boundary a corrupted claim crosses.</summary>
    public enum Kit3Boundary
    {
        AreaEdge,
        ClosedPortal,
    }

    /// <summary>
    /// A merge (C walks into A's claim) and a split (C walks away again) in every anchoring mode and
    /// merge gap: the checker reports nothing at any boundary, and appends nothing to a list that
    /// already holds entries.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0)]
    [InlineData(ClaimAnchoring.Section, 1)]
    [InlineData(ClaimAnchoring.Position, 0)]
    [InlineData(ClaimAnchoring.Position, 1)]
    public void CleanStateReportsNothing(ClaimAnchoring anchoring, int mergeGap)
    {
        var observer = new Kit3RecordingObserver();
        var options = Kit3World.Options(TimerMode.TickCounter, anchoring, mergeGap, splitMinSections: 1, splitMinDeadPercent: 10);
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), options, observer);
        var driver = new Kit3Driver(regionizer);
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(24), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryC, EntityClass.Infantry, 0, Kit3World.Centre(14), Kit3World.Centre(3));
        regionizer.AddTimer(InfantryC, 50);
        Kit3Invariants.AssertClean(regionizer, $"{anchoring}, gap {mergeGap}, after setup");

        for (var tick = 1; tick <= 12; tick++)
        {
            var target = tick switch
            {
                3 => 4,
                8 => 14,
                _ => -1,
            };
            driver.Step(tick * 2L, body: id =>
            {
                if (target >= 0 && regionizer.GetEntity(InfantryC).RegionId == id)
                {
                    regionizer.MoveEntity(InfantryC, 0, Kit3World.Centre(target), Kit3World.Centre(3));
                }
            }, context: $"{anchoring}, gap {mergeGap}, tick {tick}");
        }

        Assert.NotEmpty(observer.Merges);
        Assert.NotEmpty(observer.Splits);
        var sentinel = new Violation(InvariantRule.LegalState, "sentinel");
        var violations = new List<Violation> { sentinel };
        Assert.Equal(0, InvariantChecker.Check(regionizer, violations));
        Assert.Equal(new[] { sentinel }, violations);
    }

    /// <summary>Rule 1: a section listed by a second region, without its owner changing.</summary>
    [Fact]
    public void SectionListedByTwoRegionsReportsSectionSingleOwner()
    {
        var (regionizer, regionA, regionB) = TwoSeparateRegions();
        var sectionOfA = Kit3World.Index(regionizer.Topology, 0, 2, 3);
        Assert.Equal(regionA, regionizer.OwnerOf(sectionOfA));

        regionizer.TestHookListSectionInRegion(regionB, sectionOfA);

        AssertOnlyRule(regionizer, InvariantRule.SectionSingleOwner);
    }

    /// <summary>Rule 2: a live section's claim count no longer matches the claims on it.</summary>
    [Fact]
    public void ClaimCountOverwrittenReportsClaimsInRegion()
    {
        var (regionizer, _, _) = TwoSeparateRegions();
        var sectionOfA = Kit3World.Index(regionizer.Topology, 0, 2, 3);
        var count = regionizer.ClaimCountOf(sectionOfA);
        Assert.True(count > 0);

        regionizer.TestHookSetClaimCount(sectionOfA, count + 1);

        AssertOnlyRule(regionizer, InvariantRule.ClaimsInRegion);
    }

    /// <summary>Rule 3: a ticking region gains a section during its tick.</summary>
    [Fact]
    public void TickingRegionGrowingReportsTickingRegionFrozen()
    {
        var (regionizer, regionA, _) = TwoSeparateRegions();
        var unowned = Kit3World.Index(regionizer.Topology, 0, 12, 6);
        Assert.Equal(0, regionizer.OwnerOf(unowned));

        regionizer.AdvanceTo(2);
        Assert.True(regionizer.TryMarkTicking(regionA));
        regionizer.TestHookGrowRegion(regionA, unowned);
        regionizer.MarkNotTicking(regionA);

        AssertOnlyRule(regionizer, InvariantRule.TickingRegionFrozen);
    }

    /// <summary>Rule 4: an illegal move (ticking straight to dead) is refused, recorded and reported.</summary>
    [Fact]
    public void IllegalTransitionReportsLegalState()
    {
        var (regionizer, regionA, _) = TwoSeparateRegions();

        regionizer.AdvanceTo(2);
        Assert.True(regionizer.TryMarkTicking(regionA));
        Assert.False(regionizer.TestHookTransition(regionA, RegionState.Dead));
        Assert.True(regionizer.TryGetState(regionA, out var state));
        Assert.Equal(RegionState.Ticking, state);
        regionizer.MarkNotTicking(regionA);

        AssertOnlyRule(regionizer, InvariantRule.LegalState);
    }

    /// <summary>
    /// Rule 5: an entity names region B while region A lists it. The entity is a heavy projectile,
    /// which claims nothing: an infantry entity named elsewhere would also break rule 2, because its
    /// claims would sit outside the region it names.
    /// </summary>
    [Fact]
    public void EntityNamingAnotherRegionReportsEntitySingleOwner()
    {
        const int projectile = 9;
        var (regionizer, regionA, regionB) = TwoSeparateRegions();
        regionizer.AddEntity(projectile, EntityClass.HeavyProjectile, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        Assert.Equal(regionA, regionizer.GetEntity(projectile).RegionId);
        Kit3Invariants.AssertClean(regionizer, "projectile placed");

        regionizer.TestHookSetEntityRegion(projectile, regionB);

        AssertOnlyRule(regionizer, InvariantRule.EntitySingleOwner);
    }

    /// <summary>
    /// Rule 6: A's stored claim gains B's section across an area edge or a closed portal. The hook also
    /// bumps that section's claim count, which would break rule 2 as well, so the test restores the
    /// count to isolate rule 6.
    /// </summary>
    [Theory]
    [InlineData(Kit3Boundary.AreaEdge)]
    [InlineData(Kit3Boundary.ClosedPortal)]
    public void ClaimAcrossABoundaryReportsPortalCrossing(Kit3Boundary boundary)
    {
        const int portalId = 7;
        var (topology, layerB, ax, bx) = boundary == Kit3Boundary.AreaEdge
            ? (Kit3World.TwoAreas(16, 8, edgeColumn: 8), 0, 7, 8)
            : (Kit3World.TwoLayers(8, portalId, 3, 3, open: false), 1, 3, 3);
        var regionizer = new Regionizer(topology, Kit3World.Options(TimerMode.TickCounter));
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(ax), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, layerB, Kit3World.Centre(bx), Kit3World.Centre(3));
        Assert.NotEqual(regionizer.GetEntity(InfantryA).RegionId, regionizer.GetEntity(InfantryB).RegionId);
        Kit3Invariants.AssertClean(regionizer, $"{boundary}, before corruption");
        var farSide = Kit3World.Index(topology, layerB, bx, 3);
        var count = regionizer.ClaimCountOf(farSide);

        regionizer.TestHookAddClaimedSection(InfantryA, farSide);
        regionizer.TestHookSetClaimCount(farSide, count);

        Assert.Contains(farSide, regionizer.GetEntity(InfantryA).ClaimedSections.ToArray());
        AssertOnlyRule(regionizer, InvariantRule.PortalCrossing);
    }

    /// <summary>
    /// Rule 2 as FR-014 states it: every section in an entity's claim belongs to its region. A section
    /// in A's claim is handed to region B consistently (B lists and owns it, A no longer lists it), so
    /// rule 1 still holds and only that claimed section is reported, naming A's region and B.
    /// </summary>
    [Theory]
    [InlineData(9101)]
    [InlineData(9102)]
    public void SectionInAClaimOwnedByAnotherRegionReportsClaimsInRegion(int seed)
    {
        var (h, regionA, regionB) = TwoRegions(nameof(SectionInAClaimOwnedByAnotherRegionReportsClaimsInRegion), seed);
        var claimed = h.Index(0, 3, 3);
        h.True(h.ClaimOf(InfantryA).Contains(claimed), "A claims section (3, 3)");

        h.R.TestHookUnlistSection(regionA, claimed, clearOwner: false);
        h.R.TestHookGrowRegion(regionB, claimed);

        h.Equal(regionB, h.R.OwnerOf(claimed), "owner of the handed-over section");
        h.True(h.SectionsOf(regionB).Contains(claimed) && !h.SectionsOf(regionA).Contains(claimed), "only B lists the section");
        h.SameViolations(
            [new Violation(InvariantRule.ClaimsInRegion, $"entity {InfantryA} of region {regionA} claims {h.Topology.KeyOf(claimed)}, owned by region {regionB}")],
            h.Check(),
            "violations for a claimed section owned by another region");
    }

    /// <summary>
    /// Rule 1, full-map fallback: a section keeps its owner but its region no longer lists it. The
    /// owned-section total then disagrees with the lists, and the scan names that section.
    /// </summary>
    [Theory]
    [InlineData(9111)]
    [InlineData(9112)]
    public void SectionOwnedButNotListedIsNamedByTheFullScan(int seed)
    {
        var (h, regionA, _) = TwoRegions(nameof(SectionOwnedButNotListedIsNamedByTheFullScan), seed);
        var claimed = h.Index(0, 3, 3);

        h.R.TestHookUnlistSection(regionA, claimed, clearOwner: false);

        h.Equal(regionA, h.R.OwnerOf(claimed), "owner of the unlisted section");
        h.SameViolations(
            [new Violation(InvariantRule.SectionSingleOwner, $"section {h.Topology.KeyOf(claimed)} is owned by region {regionA} but not listed by it")],
            h.Check(),
            "violations for an owned but unlisted section");
    }

    /// <summary>
    /// Rule 1, full-map fallback: a claimed (live) section is dropped from its region and loses its
    /// owner. The claim total then disagrees with the listed sections, and the scan names the live
    /// section with no owner; A's claim on it is reported under rule 2 as well.
    /// </summary>
    [Theory]
    [InlineData(9121)]
    [InlineData(9122)]
    public void LiveSectionWithoutAnOwnerIsNamedByTheFullScan(int seed)
    {
        var (h, regionA, _) = TwoRegions(nameof(LiveSectionWithoutAnOwnerIsNamedByTheFullScan), seed);
        var claimed = h.Index(0, 3, 3);

        h.R.TestHookUnlistSection(regionA, claimed, clearOwner: true);

        h.Equal(0L, h.R.OwnerOf(claimed), "owner of the dropped section");
        h.Equal(1, h.R.ClaimCountOf(claimed), "claims on the dropped section");
        var key = h.Topology.KeyOf(claimed);
        h.SameViolations(
            [
                new Violation(InvariantRule.SectionSingleOwner, $"live section {key} has no owner"),
                new Violation(InvariantRule.ClaimsInRegion, $"entity {InfantryA} of region {regionA} claims {key}, owned by region 0"),
            ],
            h.Check(),
            "violations for a live section with no owner");
    }

    /// <summary>
    /// Rule 1: a region lists an unowned, unclaimed section. The checker names the section listed by
    /// a region that does not own it, and the region's dead-section count, now one short.
    /// </summary>
    [Theory]
    [InlineData(9131)]
    [InlineData(9132)]
    public void SectionListedByARegionThatDoesNotOwnItReportsSectionSingleOwner(int seed)
    {
        var (h, _, regionB) = TwoRegions(nameof(SectionListedByARegionThatDoesNotOwnItReportsSectionSingleOwner), seed);
        var unowned = h.Index(0, 12, 6);
        h.Equal(0L, h.R.OwnerOf(unowned), "owner of section (12, 6)");

        h.R.TestHookListSectionInRegion(regionB, unowned);

        h.SameViolations(
            [
                new Violation(InvariantRule.SectionSingleOwner, $"section {h.Topology.KeyOf(unowned)} is listed by region {regionB} but owned by 0"),
                new Violation(InvariantRule.SectionSingleOwner, $"region {regionB} counts 0 dead sections, actually 1"),
            ],
            h.Check(),
            "violations for a section listed by a region that does not own it");
    }

    /// <summary>Rule 5: one entity listed by two regions.</summary>
    [Theory]
    [InlineData(9141)]
    [InlineData(9142)]
    public void EntityListedByTwoRegionsReportsEntitySingleOwner(int seed)
    {
        var (h, regionA, regionB) = TwoRegions(nameof(EntityListedByTwoRegionsReportsEntitySingleOwner), seed);

        h.R.TestHookListEntityInRegion(regionB, InfantryA);

        h.Equal($"{InfantryA},{InfantryB}", string.Join(",", h.EntitiesOf(regionB)), "entities B lists");
        h.SameViolations(
            [new Violation(InvariantRule.EntitySingleOwner, $"entity {InfantryA} is listed by regions {regionA} and {regionB}")],
            h.Check(),
            "violations for an entity listed by two regions");
    }

    /// <summary>Rule 5: an entity in transit (two-step teleport) has no owner, so a region listing it is reported.</summary>
    [Theory]
    [InlineData(9151)]
    [InlineData(9152)]
    public void EntityInTransitListedByARegionReportsEntitySingleOwner(int seed)
    {
        var (h, regionA, _) = TwoRegions(nameof(EntityInTransitListedByARegionReportsEntitySingleOwner), seed);
        var (x, y) = h.Spot(12, 20);
        h.R.BeginTeleport(InfantryA, 0, x, y);
        h.AssertClean();

        h.R.TestHookListEntityInRegion(regionA, InfantryA);

        h.Equal(true, h.R.GetEntity(InfantryA).InTransit, "A is in transit");
        h.SameViolations(
            [new Violation(InvariantRule.EntitySingleOwner, $"entity {InfantryA} is in transit but listed by region {regionA}")],
            h.Check(),
            "violations for an entity in transit listed by a region");
    }

    /// <summary>
    /// Rule 5: an entity not in transit that names no region. A heavy projectile is used because it
    /// claims nothing, so no claim count is left without its claimant.
    /// </summary>
    [Theory]
    [InlineData(9161)]
    [InlineData(9162)]
    public void EntityWithNoOwnerReportsEntitySingleOwner(int seed)
    {
        const int projectile = 9;
        var (h, regionA, _) = TwoRegions(nameof(EntityWithNoOwnerReportsEntitySingleOwner), seed);
        var (x, y) = h.Spot(2, 3);
        h.R.AddEntity(projectile, EntityClass.HeavyProjectile, 0, x, y);
        h.Equal(regionA, h.RegionOf(projectile), "region of the projectile fired inside A's region");
        h.AssertClean();

        h.R.TestHookSetEntityRegion(projectile, 0);

        h.SameViolations(
            [new Violation(InvariantRule.EntitySingleOwner, $"entity {projectile} has no owner")],
            h.Check(),
            "violations for an entity with no owner");
    }

    /// <summary>
    /// Rules 5 and 2: an infantry entity names region B while A lists it and owns its sections. The
    /// checker names the listing, the entity's own section and every section of its claim.
    /// </summary>
    [Theory]
    [InlineData(9171)]
    [InlineData(9172)]
    public void InfantryNamingAnotherRegionReportsItsSectionAndItsClaim(int seed)
    {
        var (h, regionA, regionB) = TwoRegions(nameof(InfantryNamingAnotherRegionReportsItsSectionAndItsClaim), seed);
        var claim = h.ClaimOf(InfantryA);
        h.Equal(9, claim.Length, "sections A claims");

        h.R.TestHookSetEntityRegion(InfantryA, regionB);

        var expected = new List<Violation>
        {
            new(InvariantRule.EntitySingleOwner, $"entity {InfantryA} names region {regionB} but is not listed by it"),
            new(InvariantRule.EntitySingleOwner, $"entity {InfantryA}'s section {h.Topology.KeyOf(h.Index(0, 2, 3))} is owned by {regionA}, not its region {regionB}"),
        };
        expected.AddRange(claim.Select(s => new Violation(InvariantRule.ClaimsInRegion, $"entity {InfantryA} of region {regionB} claims {h.Topology.KeyOf(s)}, owned by region {regionA}")));
        h.SameViolations(expected, h.Check(), "violations for an infantry entity naming another region");
    }

    /// <summary>Rule 2: a heavy projectile must claim nothing; one with a claimed section is reported, with the count it adds.</summary>
    [Theory]
    [InlineData(9181)]
    [InlineData(9182)]
    public void HeavyProjectileWithAClaimReportsClaimsInRegion(int seed)
    {
        const int projectile = 9;
        var (h, _, _) = TwoRegions(nameof(HeavyProjectileWithAClaimReportsClaimsInRegion), seed);
        var (x, y) = h.Spot(2, 3);
        h.R.AddEntity(projectile, EntityClass.HeavyProjectile, 0, x, y);
        var own = h.Index(0, 2, 3);
        h.Equal(1, h.R.ClaimCountOf(own), "claims on the projectile's section before the corruption");

        h.R.TestHookAddClaimedSection(projectile, own);

        h.SameViolations(
            [
                new Violation(InvariantRule.ClaimsInRegion, $"heavy projectile {projectile} claims sections"),
                new Violation(InvariantRule.ClaimsInRegion, $"section {h.Topology.KeyOf(own)} has claim count 2, expected 1"),
            ],
            h.Check(),
            "violations for a heavy projectile with a claim");
    }

    /// <summary>
    /// Rules 5 and 2, section by section: A moves four sections east without its claim being
    /// recomputed. The checker names its unowned new section, each stale claimed section and each
    /// section of the claim it should hold. The claim totals still agree, so the counts of the
    /// should-be-claimed sections, which no region lists, are checked one by one.
    /// </summary>
    [Theory]
    [InlineData(9191)]
    [InlineData(9192)]
    public void ClaimLeftBehindByAMoveIsNamedSectionBySection(int seed)
    {
        var (h, regionA, _) = TwoRegions(nameof(ClaimLeftBehindByAMoveIsNamedSectionBySection), seed);
        var stale = h.ClaimOf(InfantryA);
        var (x, y) = h.Spot(6, 3);

        h.R.TestHookMoveWithoutReclaim(InfantryA, x, y);

        var anchor = h.Index(0, 6, 3);
        var should = h.Footprint(0, 6, 3).Order().ToArray();
        h.Equal(string.Join(",", stale), string.Join(",", h.ClaimOf(InfantryA)), "A's stored claim after the move");
        h.Equal(0L, h.R.OwnerOf(anchor), "owner of A's new section");
        var expected = new List<Violation>
        {
            new(InvariantRule.EntitySingleOwner, $"entity {InfantryA}'s section {h.Topology.KeyOf(anchor)} is owned by 0, not its region {regionA}"),
        };
        foreach (var s in stale)
        {
            var key = h.Topology.KeyOf(s);
            expected.Add(new(InvariantRule.ClaimsInRegion, $"entity {InfantryA} claims {key}, outside its claim"));
            expected.Add(new(InvariantRule.ClaimsInRegion, $"section {key} has claim count 1, expected 0"));
        }

        foreach (var s in should)
        {
            var key = h.Topology.KeyOf(s);
            expected.Add(new(InvariantRule.ClaimsInRegion, $"entity {InfantryA} does not claim {key}, which is inside its claim"));
            expected.Add(new(InvariantRule.ClaimsInRegion, $"entity {InfantryA} of region {regionA} claims {key}, owned by region 0"));
            expected.Add(new(InvariantRule.ClaimsInRegion, $"section {key} has claim count 0, expected 1"));
        }

        h.SameViolations(expected, h.Check(), "violations for a claim left behind by a move");
    }

    /// <summary>
    /// Rule 2 with many stray sections: 41 unowned sections far beyond A's radius are added to its
    /// stored claim, more than its claim buffer holds. Each is named, as outside the claim and as a
    /// count with no claimant, and the stored claim keeps all 50 sections.
    /// </summary>
    [Theory]
    [InlineData(9201)]
    [InlineData(9202)]
    public void StrayClaimsBeyondTheRadiusAreEachNamed(int seed)
    {
        var (h, regionA, _) = TwoRegions(nameof(StrayClaimsBeyondTheRadiusAreEachNamed), seed);
        var strays = Enumerable.Range(0, 32).Select(x => h.Index(0, x, 20)).Concat(Enumerable.Range(0, 9).Select(x => h.Index(0, x, 21))).ToArray();
        h.Equal(41, strays.Length, "stray sections");
        h.True(strays.All(s => h.R.OwnerOf(s) == 0), "the stray sections start unowned");
        var claim = h.ClaimOf(InfantryA);

        foreach (var s in strays)
        {
            h.R.TestHookAddClaimedSection(InfantryA, s);
        }

        h.SameSections(claim.Concat(strays), h.ClaimOf(InfantryA), "A's stored claim after the strays");
        h.Equal(50, h.ClaimOf(InfantryA).Length, "sections in A's stored claim");
        h.True(strays.All(s => h.R.OwnerOf(s) == regionA), "each stray section went to A's region");
        var expected = strays.SelectMany(s => new[]
        {
            new Violation(InvariantRule.ClaimsInRegion, $"entity {InfantryA} claims {h.Topology.KeyOf(s)}, outside its claim"),
            new Violation(InvariantRule.ClaimsInRegion, $"section {h.Topology.KeyOf(s)} has claim count 1, expected 0"),
        });
        h.SameViolations(expected, h.Check(), "violations for 41 stray claimed sections");
    }

    /// <summary>A at section (2, 3) and B at (24, 3) of the 32 × 32 harness world, each in its own region; clean.</summary>
    private static (Kit2Harness H, long RegionA, long RegionB) TwoRegions(string name, int seed)
    {
        var h = new Kit2Harness(name, seed, Kit2Harness.Options());
        h.Add(InfantryA, 2, 3);
        h.Add(InfantryB, 24, 3);
        var regionA = h.RegionOf(InfantryA);
        var regionB = h.RegionOf(InfantryB);
        h.True(regionA != regionB, "A and B start in separate regions");
        h.SameSections(h.Footprint(InfantryA), h.SectionsOf(regionA), "sections of A's region");
        h.SameSections(h.Footprint(InfantryB), h.SectionsOf(regionB), "sections of B's region");
        h.AssertClean();
        return (h, regionA, regionB);
    }

    /// <summary>A in its own region at section (2, 3) and B in another at (24, 3); clean.</summary>
    private static (Regionizer Regionizer, long RegionA, long RegionB) TwoSeparateRegions()
    {
        var regionizer = new Regionizer(Kit3World.OpenWorld(32, 8), Kit3World.Options(TimerMode.TickCounter));
        regionizer.AddEntity(InfantryA, EntityClass.Infantry, 0, Kit3World.Centre(2), Kit3World.Centre(3));
        regionizer.AddEntity(InfantryB, EntityClass.Infantry, 0, Kit3World.Centre(24), Kit3World.Centre(3));
        var regionA = regionizer.GetEntity(InfantryA).RegionId;
        var regionB = regionizer.GetEntity(InfantryB).RegionId;
        Assert.NotEqual(regionA, regionB);
        Kit3Invariants.AssertClean(regionizer, "before corruption");
        return (regionizer, regionA, regionB);
    }

    private static void AssertOnlyRule(Regionizer regionizer, InvariantRule rule)
    {
        var violations = new List<Violation>();
        var found = InvariantChecker.Check(regionizer, violations);
        Assert.Equal(violations.Count, found);
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(rule, v.Rule));
    }
}
