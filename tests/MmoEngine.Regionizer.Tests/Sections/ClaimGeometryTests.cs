using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Sections;

/// <summary>
/// Claim geometry (FR-003, FR-004, research D1): a claim is the set of sections whose square lies
/// within the class's radius of the anchor, which is the entity's exact position (position-anchored)
/// or its whole section square (section-anchored). Claims are clipped to the map and never wrap.
/// </summary>
public sealed class ClaimGeometryTests
{
    private const int SectionSize = 8;

    /// <summary>A distinct radius per class, so a claim computed with the wrong class's radius shows.</summary>
    private static readonly ClaimRadii DistinctRadii = new(Infantry: 12, GroundVehicle: 28, Aircraft: 50, Traveller: 4, CapturePointZone: 20);

    /// <summary>Every class that claims, crossed with both anchorings and positions inside, on edges and on corners.</summary>
    public static TheoryData<ClaimAnchoring, EntityClass, double, double> ClassesAnchoringsAndPositions()
    {
        var data = new TheoryData<ClaimAnchoring, EntityClass, double, double>();
        (double X, double Y)[] positions = [(200.0, 200.0), (203.7, 196.1), (207.999, 200.5), (208.0, 216.0), (12.25, 300.0)];
        foreach (var anchoring in Enum.GetValues<ClaimAnchoring>())
        {
            foreach (var cls in new[] { EntityClass.Infantry, EntityClass.GroundVehicle, EntityClass.Aircraft, EntityClass.Traveller, EntityClass.CapturePoint })
            {
                foreach (var (x, y) in positions)
                {
                    data.Add(anchoring, cls, x, y);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClassesAnchoringsAndPositions))]
    public void ClaimIsTheSectionsWithinTheClassRadiusOfTheAnchor(ClaimAnchoring anchoring, EntityClass cls, double x, double y)
    {
        var topology = SectionTopology.OpenWorld(SectionSize, 512, 512);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(SectionSize, anchoring, DistinctRadii));

        regionizer.AddEntity(1, cls, 0, x, y);
        if (cls == EntityClass.CapturePoint)
        {
            regionizer.SetCapturePointOccupied(1, true);
        }

        Assert.True(topology.TrySectionAt(0, x, y, out var own));
        var expected = Kit1ClaimOracle.OpenWorldClaim(topology, own, x, y, DistinctRadii.For(cls), anchoring);
        Assert.Equal(Kit1Fixtures.Keys(topology, expected), Kit1Fixtures.ClaimKeys(regionizer, 1));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring} {cls} at ({x}, {y})");
    }

    /// <summary>
    /// Section-anchored claims are the rounded square around the entity's section, counted by hand at
    /// s = 16: the 3 × 3 block below r = 16; plus the four arms at r = 16; plus the corners once
    /// r ≥ 16√2 ≈ 22.63; at r = 32 the 5 × 5 block plus three-section arms at distance 3.
    /// </summary>
    [Theory]
    [InlineData(15.99, 9)]
    [InlineData(16.0, 21)]
    [InlineData(22.62, 21)]
    [InlineData(22.63, 25)]
    [InlineData(32.0, 37)]
    public void SectionAnchoredClaimIsTheRoundedSquareAroundTheSection(double radius, int expectedCount)
    {
        var claim = SingleClaim(ClaimAnchoring.Section, radius, x: 81.0, y: 94.5);

        Assert.Equal(expectedCount, claim.Length);

        // The rounded square has the square's eight symmetries about the entity's section (5, 5).
        var offsets = claim.Select(k => (Dx: k.X - 5, Dy: k.Y - 5)).ToHashSet();
        Assert.All(offsets, o =>
        {
            Assert.Contains((-o.Dx, o.Dy), offsets);
            Assert.Contains((o.Dx, -o.Dy), offsets);
            Assert.Contains((o.Dy, o.Dx), offsets);
        });

        // It depends on the section, not the position inside it.
        Assert.Equal(claim, SingleClaim(ClaimAnchoring.Section, radius, x: 95.9, y: 80.0));
    }

    /// <summary>
    /// Position-anchored claims from the centre (88, 88) of section (5, 5) at s = 16, counted by hand:
    /// edge neighbours at 8 m, diagonal neighbours at 11.31 m, the next ring's straight sections at
    /// 24 m, its knight's-move sections at 25.30 m and its corners at 33.94 m.
    /// </summary>
    [Theory]
    [InlineData(7.99, 1)]
    [InlineData(8.0, 5)]
    [InlineData(11.32, 9)]
    [InlineData(24.0, 13)]
    [InlineData(25.3, 21)]
    [InlineData(33.95, 25)]
    public void PositionAnchoredClaimIsTheSectionsWithinTheRadiusOfThePoint(double radius, int expectedCount)
    {
        var claim = SingleClaim(ClaimAnchoring.Position, radius, x: 88.0, y: 88.0);

        Assert.Equal(expectedCount, claim.Length);
        Assert.Contains(new SectionKey(0, 5, 5), claim);
    }

    /// <summary>
    /// Spec edge cases: a radius of 0 claims only the entity's own section, and an entity exactly on a
    /// section edge or corner belongs to the one section floor(x / s), floor(y / s) and claims from it.
    /// </summary>
    // Regression (fixed in the core): a radius of 0 claims only the own section; touching squares are not claimed.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 88.0, 88.0)]
    [InlineData(ClaimAnchoring.Section, 0.5, 0.5)]
    [InlineData(ClaimAnchoring.Section, 80.0, 88.0)]
    [InlineData(ClaimAnchoring.Section, 80.0, 96.0)]
    [InlineData(ClaimAnchoring.Position, 88.0, 88.0)]
    [InlineData(ClaimAnchoring.Position, 0.5, 0.5)]
    [InlineData(ClaimAnchoring.Position, 80.0, 88.0)]
    [InlineData(ClaimAnchoring.Position, 88.0, 96.0)]
    [InlineData(ClaimAnchoring.Position, 80.0, 96.0)]
    public void RadiusZeroClaimsOnlyTheOwnSection(ClaimAnchoring anchoring, double x, double y)
    {
        var claim = SingleClaim(anchoring, 0, x, y);

        Assert.Equal([new SectionKey(0, (int)Math.Floor(x / 16), (int)Math.Floor(y / 16))], claim);
    }

    /// <summary>
    /// An entity exactly on a section edge or corner claims from the floored section. Four areas meet
    /// at the corner (80, 96) at s = 16, so a claim started from the wrong section would stay in the
    /// wrong area.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 80.0, 88.0, 5, 5)]
    [InlineData(ClaimAnchoring.Section, 88.0, 96.0, 5, 6)]
    [InlineData(ClaimAnchoring.Section, 80.0, 96.0, 5, 6)]
    [InlineData(ClaimAnchoring.Section, 79.999, 95.999, 4, 5)]
    [InlineData(ClaimAnchoring.Position, 80.0, 88.0, 5, 5)]
    [InlineData(ClaimAnchoring.Position, 88.0, 96.0, 5, 6)]
    [InlineData(ClaimAnchoring.Position, 80.0, 96.0, 5, 6)]
    [InlineData(ClaimAnchoring.Position, 79.999, 95.999, 4, 5)]
    public void EntityOnASectionEdgeOrCornerClaimsFromTheFlooredSection(ClaimAnchoring anchoring, double x, double y, int ownX, int ownY)
    {
        var layer = Kit1Fixtures.Layer(0, 0, 0, 12, 12, (sx, sy) => (ushort)((sx >= 5 ? 1 : 0) + (sy >= 6 ? 2 : 0)));
        var topology = SectionTopology.Build(16, [layer], []);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(16, anchoring, Kit1Fixtures.Radii(20)));

        regionizer.AddEntity(1, EntityClass.Infantry, 0, x, y);

        var own = Kit1Fixtures.IndexOf(topology, 0, ownX, ownY);
        var claim = regionizer.GetEntity(1).ClaimedSections.ToArray();
        Assert.Contains(own, claim);
        Assert.All(claim, s => Assert.Equal(topology.AreaOf(own), topology.AreaOf(s)));
        Assert.True(claim.Length > 1, $"a radius of 20 m claims only {claim.Length} section(s)");
        Assert.Equal(
            Kit1Fixtures.Keys(topology, Kit1ClaimOracle.Claim(topology, [], _ => false, 0, x, y, 20, anchoring)),
            Kit1Fixtures.ClaimKeys(regionizer, 1));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring} at ({x}, {y})");
    }

    /// <summary>
    /// On a 10 × 10 map at s = 16 with r = 40, claims near the edge keep only the sections inside the
    /// map, counted by hand: 15 and 8 at a corner, 26 and 16 at the middle of an edge (section- and
    /// position-anchored). Nothing wraps to the far side.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0.5, 0.5, 15)]
    [InlineData(ClaimAnchoring.Section, 159.5, 159.5, 15)]
    [InlineData(ClaimAnchoring.Section, 0.5, 159.5, 15)]
    [InlineData(ClaimAnchoring.Section, 80.5, 0.5, 26)]
    [InlineData(ClaimAnchoring.Section, 159.5, 80.5, 26)]
    [InlineData(ClaimAnchoring.Position, 0.5, 0.5, 8)]
    [InlineData(ClaimAnchoring.Position, 159.5, 159.5, 8)]
    [InlineData(ClaimAnchoring.Position, 0.5, 159.5, 8)]
    [InlineData(ClaimAnchoring.Position, 80.5, 0.5, 16)]
    [InlineData(ClaimAnchoring.Position, 159.5, 80.5, 16)]
    public void ClaimsAreClippedAtTheMapEdge(ClaimAnchoring anchoring, double x, double y, int expectedCount)
    {
        var topology = SectionTopology.OpenWorld(16, 160, 160);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(16, anchoring, Kit1Fixtures.Radii(40)));

        regionizer.AddEntity(1, EntityClass.Infantry, 0, x, y);

        var claim = Kit1Fixtures.ClaimKeys(regionizer, 1);
        Assert.Equal(expectedCount, claim.Length);
        Assert.All(claim, k =>
        {
            Assert.InRange(k.X, 0, 9);
            Assert.InRange(k.Y, 0, 9);
        });

        // Nothing wraps: every claimed section is within the radius's reach of the entity's section.
        var ownX = (int)Math.Floor(x / 16);
        var ownY = (int)Math.Floor(y / 16);
        Assert.All(claim, k => Assert.True(Math.Abs(k.X - ownX) <= 3 && Math.Abs(k.Y - ownY) <= 3, $"{k} is out of reach of section ({ownX}, {ownY}): the claim wrapped"));

        Assert.True(topology.TrySectionAt(0, x, y, out var own));
        Assert.Equal(Kit1Fixtures.Keys(topology, Kit1ClaimOracle.OpenWorldClaim(topology, own, x, y, 40, anchoring)), claim);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring} at ({x}, {y})");
    }

    /// <summary>
    /// US1.8: section-anchored claims do not change while the entity moves within one section;
    /// position-anchored claims change on the tick the radius covers a new section.
    /// </summary>
    [Theory]
    [InlineData(EntityClass.Infantry)]
    [InlineData(EntityClass.GroundVehicle)]
    [InlineData(EntityClass.Aircraft)]
    [InlineData(EntityClass.Traveller)]
    public void SectionAnchoredClaimIsStableWithinSection_PositionAnchoredFollowsPosition(EntityClass cls)
    {
        // Section (5, 5) spans [80, 96) at s = 16. From its centre, r = 10 reaches the four edge
        // neighbours (8 m) but not the diagonals (11.3 m); at (95, 88) the right-hand diagonals are
        // 8.06 m away. Both points are in section (5, 5).
        (double X, double Y) start = (88.0, 88.0);
        (double X, double Y) moved = (95.0, 88.0);

        // Given section-anchored claims, when the entity moves within its section, the claim does not change.
        var sectionMode = new Regionizer(SectionTopology.OpenWorld(16, 320, 320), Kit1Fixtures.Options(16, ClaimAnchoring.Section, Kit1Fixtures.Radii(10)));
        sectionMode.AddEntity(1, cls, 0, start.X, start.Y);
        var before = Kit1Fixtures.ClaimKeys(sectionMode, 1);
        var region = sectionMode.GetEntity(1).RegionId;

        Assert.True(sectionMode.TryMarkTicking(region));
        sectionMode.MoveEntity(1, 0, moved.X, moved.Y);
        Assert.Equal(before, Kit1Fixtures.ClaimKeys(sectionMode, 1));
        sectionMode.MarkNotTicking(region);

        Assert.Equal(before, Kit1Fixtures.ClaimKeys(sectionMode, 1));
        Kit1Fixtures.AssertInvariantsHold(sectionMode, $"section-anchored {cls} after the move");

        // Given position-anchored claims, when the same entity moves so its radius covers a new
        // section, the claim changes on that tick.
        var topology = SectionTopology.OpenWorld(16, 320, 320);
        var positionMode = new Regionizer(topology, Kit1Fixtures.Options(16, ClaimAnchoring.Position, Kit1Fixtures.Radii(10)));
        positionMode.AddEntity(1, cls, 0, start.X, start.Y);
        var startClaim = Kit1Fixtures.ClaimKeys(positionMode, 1);
        region = positionMode.GetEntity(1).RegionId;
        Assert.DoesNotContain(new SectionKey(0, 6, 4), startClaim);
        Assert.DoesNotContain(new SectionKey(0, 6, 6), startClaim);

        Assert.True(positionMode.TryMarkTicking(region));
        positionMode.MoveEntity(1, 0, moved.X, moved.Y);
        var duringTick = Kit1Fixtures.ClaimKeys(positionMode, 1);
        positionMode.MarkNotTicking(region);

        Assert.NotEqual(startClaim, duringTick);
        Assert.Contains(new SectionKey(0, 6, 4), duringTick);
        Assert.Contains(new SectionKey(0, 6, 6), duringTick);
        Assert.True(topology.TrySectionAt(0, moved.X, moved.Y, out var own));
        Assert.Equal(Kit1Fixtures.Keys(topology, Kit1ClaimOracle.OpenWorldClaim(topology, own, moved.X, moved.Y, 10, ClaimAnchoring.Position)), duringTick);
        Kit1Fixtures.AssertInvariantsHold(positionMode, $"position-anchored {cls} after the move");
    }

    [Theory]
    [InlineData(95.999, 88.0, 96.0, 88.0)]
    [InlineData(88.0, 80.0, 88.0, 79.999)]
    [InlineData(80.0, 80.0, 79.999, 79.999)]
    public void SectionAnchoredClaimChangesWhenTheEntityCrossesASectionEdge(double fromX, double fromY, double toX, double toY)
    {
        var topology = SectionTopology.OpenWorld(16, 320, 320);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(16, ClaimAnchoring.Section, Kit1Fixtures.Radii(10)));
        regionizer.AddEntity(1, EntityClass.Infantry, 0, fromX, fromY);
        var before = Kit1Fixtures.ClaimKeys(regionizer, 1);

        regionizer.MoveEntity(1, 0, toX, toY);

        var after = Kit1Fixtures.ClaimKeys(regionizer, 1);
        Assert.NotEqual(before, after);
        Assert.True(topology.TrySectionAt(0, toX, toY, out var own));
        Assert.Equal(Kit1Fixtures.Keys(topology, Kit1ClaimOracle.OpenWorldClaim(topology, own, toX, toY, 10, ClaimAnchoring.Section)), after);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"after crossing from ({fromX}, {fromY}) to ({toX}, {toY})");
    }

    /// <summary>The sorted claim keys of one infantry entity on a 320 m open map at s = 16.</summary>
    private static SectionKey[] SingleClaim(ClaimAnchoring anchoring, double radius, double x, double y)
    {
        var regionizer = new Regionizer(SectionTopology.OpenWorld(16, 320, 320), Kit1Fixtures.Options(16, anchoring, Kit1Fixtures.Radii(radius)));
        regionizer.AddEntity(1, EntityClass.Infantry, 0, x, y);
        return Kit1Fixtures.ClaimKeys(regionizer, 1);
    }
}
