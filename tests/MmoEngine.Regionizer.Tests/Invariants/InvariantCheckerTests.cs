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
