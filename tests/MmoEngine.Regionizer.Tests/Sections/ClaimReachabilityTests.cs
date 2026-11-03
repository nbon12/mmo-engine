using CsCheck;
using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Sections;

/// <summary>
/// Claim reachability (FR-003, FR-013, research D2): a claim spreads from the entity's own section
/// over same-layer, same-area eight-neighbours and through open portals only, so it never crosses an
/// area or layer boundary except through an open portal.
/// </summary>
public sealed class ClaimReachabilityTests
{
    private const int S = 8;
    private const int CrossLayerPortal = 1;
    private const int DoorPortal = 2;

    /// <summary>
    /// Layer 0 (surface) is split into area 1 (x &lt; 10) and area 2 (x ≥ 10); layer 1 (underground)
    /// is area 5. Portal 1 joins (0, 6, 6) and (1, 6, 6); portal 2 is a door from (0, 9, 14) in area 1
    /// to (0, 10, 14) in area 2. Each case puts one entity beside each end of one portal.
    /// </summary>
    public static TheoryData<ClaimAnchoring, int, int> PortalCases()
    {
        var data = new TheoryData<ClaimAnchoring, int, int>();
        foreach (var anchoring in Enum.GetValues<ClaimAnchoring>())
        {
            foreach (var portal in new[] { CrossLayerPortal, DoorPortal })
            {
                foreach (var gap in new[] { 0, 1 })
                {
                    data.Add(anchoring, portal, gap);
                }
            }
        }

        return data;
    }

    public static TheoryData<ClaimAnchoring, int> AnchoringsAndGaps() => new()
    {
        { ClaimAnchoring.Section, 0 },
        { ClaimAnchoring.Section, 1 },
        { ClaimAnchoring.Position, 0 },
        { ClaimAnchoring.Position, 1 },
    };

    [Theory]
    [MemberData(nameof(AnchoringsAndGaps))]
    public void ClaimStopsAtAnAreaBoundary(ClaimAnchoring anchoring, int mergeGap)
    {
        // Area 1 is x < 10 and area 2 is x >= 10 (sections of 8 m). Each entity's 24 m radius reaches
        // three sections past the boundary, but its claim must stop at it.
        var topology = SectionTopology.Build(S, [Kit1Fixtures.Layer(0, 0, 0, 20, 20, (x, _) => (ushort)(x < 10 ? 1 : 2))], []);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(S, anchoring, Kit1Fixtures.Radii(24), mergeGap));

        regionizer.AddEntity(1, EntityClass.Infantry, 0, 68.0, 84.0);
        regionizer.AddEntity(2, EntityClass.Infantry, 0, 92.0, 84.0);

        var west = Kit1Fixtures.ClaimKeys(regionizer, 1);
        var east = Kit1Fixtures.ClaimKeys(regionizer, 2);
        Assert.All(west, k => Assert.True(k.X < 10, $"entity 1 in area 1 claims {k} across the area boundary"));
        Assert.All(east, k => Assert.True(k.X >= 10, $"entity 2 in area 2 claims {k} across the area boundary"));
        Assert.Contains(new SectionKey(0, 9, 10), west);
        Assert.Contains(new SectionKey(0, 10, 10), east);
        Assert.Equal(OracleKeys(regionizer, [], 0, 68.0, 84.0, 24), west);
        Assert.Equal(OracleKeys(regionizer, [], 0, 92.0, 84.0, 24), east);

        // Their claims sit in neighbouring sections, but neither the claims nor the merge gap cross the boundary.
        Assert.NotEqual(regionizer.GetEntity(1).RegionId, regionizer.GetEntity(2).RegionId);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}, gap {mergeGap}");
    }

    [Theory]
    [MemberData(nameof(PortalCases))]
    public void ClaimStopsAtAClosedPortal(ClaimAnchoring anchoring, int portalId, int mergeGap)
    {
        var (regionizer, portals) = PortalScenario(anchoring, mergeGap, open: false, portalId);
        var (near, far) = Placements(portalId);

        var nearClaim = Kit1Fixtures.ClaimKeys(regionizer, 1);
        var farClaim = Kit1Fixtures.ClaimKeys(regionizer, 2);
        var nearOwn = Own(regionizer, near);
        var farOwn = Own(regionizer, far);
        Assert.All(nearClaim, k => Assert.True(SameAreaAndLayer(regionizer.Topology, k, nearOwn), $"entity 1 claims {k} through closed portal {portalId}"));
        Assert.All(farClaim, k => Assert.True(SameAreaAndLayer(regionizer.Topology, k, farOwn), $"entity 2 claims {k} through closed portal {portalId}"));
        Assert.Equal(OracleKeys(regionizer, portals, near.Layer, near.X, near.Y, 20), nearClaim);
        Assert.Equal(OracleKeys(regionizer, portals, far.Layer, far.X, far.Y, 20), farClaim);

        Assert.NotEqual(regionizer.GetEntity(1).RegionId, regionizer.GetEntity(2).RegionId);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}, portal {portalId} closed, gap {mergeGap}");
    }

    [Theory]
    [MemberData(nameof(PortalCases))]
    public void ClaimPassesThroughAnOpenPortal(ClaimAnchoring anchoring, int portalId, int mergeGap)
    {
        var (regionizer, portals) = PortalScenario(anchoring, mergeGap, open: true, portalId);
        var (near, far) = Placements(portalId);
        var portal = portals.Single(p => p.PortalId == portalId);

        var nearClaim = Kit1Fixtures.ClaimKeys(regionizer, 1);
        var farClaim = Kit1Fixtures.ClaimKeys(regionizer, 2);

        // Each claim reaches through the portal to the section on its far side, and no further than the geometry allows.
        Assert.Contains(portal.B, nearClaim);
        Assert.Contains(portal.A, farClaim);
        Assert.Contains(nearClaim, k => !SameAreaAndLayer(regionizer.Topology, k, Own(regionizer, near)));
        Assert.Equal(OracleKeys(regionizer, portals, near.Layer, near.X, near.Y, 20), nearClaim);
        Assert.Equal(OracleKeys(regionizer, portals, far.Layer, far.X, far.Y, 20), farClaim);

        // The claims now share sections, so the two entities are in one region.
        Assert.Equal(regionizer.GetEntity(1).RegionId, regionizer.GetEntity(2).RegionId);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}, portal {portalId} open, gap {mergeGap}");
    }

    [Theory]
    [MemberData(nameof(AnchoringsAndGaps))]
    public void TogglingAPortalAtABoundaryUpdatesTheClaimsThroughIt(ClaimAnchoring anchoring, int mergeGap)
    {
        var (regionizer, portals) = PortalScenario(anchoring, mergeGap, open: false, CrossLayerPortal);
        var (near, _) = Placements(CrossLayerPortal);
        var closed = Kit1Fixtures.ClaimKeys(regionizer, 1);

        regionizer.SetPortalOpen(CrossLayerPortal, true);
        var opened = Kit1Fixtures.ClaimKeys(regionizer, 1);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}, gap {mergeGap}, after opening");

        regionizer.SetPortalOpen(CrossLayerPortal, false);
        var reclosed = Kit1Fixtures.ClaimKeys(regionizer, 1);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}, gap {mergeGap}, after closing");

        Assert.Contains(new SectionKey(1, 6, 6), opened);
        Assert.DoesNotContain(new SectionKey(1, 6, 6), closed);
        Assert.Equal(closed, reclosed);
        Assert.Equal(OracleKeys(regionizer, portals, near.Layer, near.X, near.Y, 20), reclosed);
    }

    /// <summary>
    /// Property (research D2): for random entities, radii, portal states, toggles and moves on a
    /// topology with areas, layers and portals, every stored claim equals a brute-force recomputation.
    /// The cases include claims the library computes with its rectangle fast path (a uniform layer with
    /// no portal nearby) and with its walk, so the property holds the fast path to the walk's answer.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0x013A1UL)]
    [InlineData(ClaimAnchoring.Section, 0x013A2UL)]
    [InlineData(ClaimAnchoring.Section, 0x013A3UL)]
    [InlineData(ClaimAnchoring.Position, 0x013A1UL)]
    [InlineData(ClaimAnchoring.Position, 0x013A2UL)]
    [InlineData(ClaimAnchoring.Position, 0x013A3UL)]
    public void StoredClaimEqualsBruteForceRecomputationOnAreasAndPortals(ClaimAnchoring anchoring, ulong seed)
    {
        var seedText = new PCG(1, seed).ToString();
        var fastPath = 0;
        var walk = 0;

        PropertyCases.Array[40].Sample(
            cases =>
            {
                for (var c = 0; c < cases.Length; c++)
                {
                    var (fast, walked) = CheckCase(anchoring, cases[c], $"seed {seedText}, {anchoring}, case {c}");
                    fastPath += fast;
                    walk += walked;
                }
            },
            seed: seedText,
            iter: 1,
            threads: 1,
            print: _ => $"40 cases from seed {seedText}, {anchoring}");

        Assert.True(fastPath > 0, $"seed {seedText}, {anchoring}: no claim fit the rectangle fast path");
        Assert.True(walk > 0, $"seed {seedText}, {anchoring}: no claim needed the walk");
    }

    // ------------------------------------------------------------ property topology and cases

    private static readonly LayerSpec[] PropertyLayers =
    [
        Kit1Fixtures.UniformLayer(0, 0, 0, 40, 40, area: 0),
        Kit1Fixtures.Layer(1, 10, 10, 16, 16, (x, y) => (ushort)(x >= 12 && y >= 12 ? 3 : x < 8 ? 1 : 2)),
        Kit1Fixtures.UniformLayer(2, -6, 30, 12, 12, area: 4),
    ];

    /// <summary>
    /// Portals across layers, doors between areas of layer 1, a portal inside one area of the uniform
    /// layer 0, and a portal whose ends are 144 m apart, which no claim of radius 40 m can pass.
    /// </summary>
    private static readonly SectionKey[][] PropertyPortalEnds =
    [
        [new(0, 12, 12), new(1, 12, 12)],
        [new(0, 3, 34), new(2, 3, 34)],
        [new(1, 17, 17), new(1, 18, 17)],
        [new(1, 21, 21), new(1, 22, 22)],
        [new(0, 30, 5), new(0, 31, 5)],
        [new(2, -4, 40), new(1, 14, 24)],
    ];

    private static readonly EntityClass[] Claimers =
        [EntityClass.Infantry, EntityClass.GroundVehicle, EntityClass.Aircraft, EntityClass.Traveller, EntityClass.CapturePoint];

    /// <summary>A spot on a layer as fractions of its size; a snapped axis lands exactly on a section edge.</summary>
    private static readonly Gen<Spot> Spots =
        Gen.Select(Gen.Int[0, 2], Gen.Double[0, 0.999999], Gen.Double[0, 0.999999], Gen.Int[0, 3], Gen.Int[0, 3])
            .Select((layer, fx, fy, snapX, snapY) => new Spot(layer, fx, fy, snapX == 0, snapY == 0));

    private static readonly Gen<PlacedEntity> Entities =
        Gen.Select(Spots, Gen.Int[0, Claimers.Length - 1], Gen.Double[0, 0.999999], Gen.Double[0, 0.999999], Gen.Bool)
            .Select((spot, cls, moveX, moveY, moves) => new PlacedEntity(spot, Claimers[cls], moves ? new Spot(spot.Layer, moveX, moveY, false, false) : null));

    private static readonly Gen<PropertyCase> PropertyCases =
        Gen.Select(Gen.Double[0.5, 40].Array[5], Gen.Bool.Array[PropertyPortalEnds.Length], Entities.Array[1, 12], Gen.Bool.Array[PropertyPortalEnds.Length])
            .Select((radii, open, entities, toggles) => new PropertyCase(radii, open, entities, toggles));

    private static (int FastPath, int Walk) CheckCase(ClaimAnchoring anchoring, PropertyCase @case, string context)
    {
        var portals = PropertyPortalEnds.Select((ends, i) => new PortalSpec(i + 1, ends[0], ends[1], @case.Open[i])).ToArray();
        var topology = SectionTopology.Build(S, PropertyLayers, portals);
        var radii = new ClaimRadii(@case.Radii[0], @case.Radii[1], @case.Radii[2], @case.Radii[3], @case.Radii[4]);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(S, anchoring, radii));

        for (var id = 0; id < @case.Entities.Length; id++)
        {
            var entity = @case.Entities[id];
            var (x, y) = Position(entity.At);
            regionizer.AddEntity(id, entity.Class, PropertyLayers[entity.At.Layer].LayerId, x, y);
            if (entity.Class == EntityClass.CapturePoint)
            {
                regionizer.SetCapturePointOccupied(id, true);
            }
        }

        for (var p = 0; p < portals.Length; p++)
        {
            if (@case.Toggle[p])
            {
                regionizer.SetPortalOpen(portals[p].PortalId, !@case.Open[p]);
            }
        }

        for (var id = 0; id < @case.Entities.Length; id++)
        {
            if (@case.Entities[id].MoveTo is { } to)
            {
                var (x, y) = Position(to);
                regionizer.MoveEntity(id, PropertyLayers[to.Layer].LayerId, x, y);
            }
        }

        var fast = 0;
        var walk = 0;
        for (var id = 0; id < @case.Entities.Length; id++)
        {
            var view = regionizer.GetEntity(id);
            var radius = radii.For(view.Class);
            var expected = Kit1ClaimOracle.Claim(topology, portals, regionizer.IsPortalOpen, view.Layer, view.X, view.Y, radius, anchoring);
            var stored = view.ClaimedSections.ToArray();
            Assert.True(
                expected.SequenceEqual(stored),
                $"{context}: entity {id} ({view.Class}, r = {radius}) at ({view.X}, {view.Y}) on layer {view.Layer} claims [{string.Join(", ", Kit1Fixtures.Keys(topology, stored))}], expected [{string.Join(", ", Kit1Fixtures.Keys(topology, expected))}]");

            var layerId = view.Layer;
            if (Array.FindIndex(PropertyLayers, l => l.LayerId == layerId) == 1)
            {
                walk++;
            }
            else if (!PortalEndWithin(view.Layer, view.X, view.Y, radius))
            {
                fast++;
            }
        }

        Kit1Fixtures.AssertInvariantsHold(regionizer, context);
        return (fast, walk);
    }

    /// <summary>Whether a portal end on the layer lies within a generous box around the claim, so the fast path cannot apply.</summary>
    private static bool PortalEndWithin(int layer, double x, double y, double radius)
    {
        var reach = (int)(radius / S) + 3;
        var sx = (int)Math.Floor(x / S);
        var sy = (int)Math.Floor(y / S);
        return PropertyPortalEnds.SelectMany(e => e).Any(k => k.Layer == layer && Math.Abs(k.X - sx) <= reach && Math.Abs(k.Y - sy) <= reach);
    }

    private static (double X, double Y) Position(Spot spot)
    {
        var layer = PropertyLayers[spot.Layer];
        var x = (layer.OriginX + (spot.Fx * layer.Width)) * S;
        var y = (layer.OriginY + (spot.Fy * layer.Height)) * S;
        return (spot.SnapX ? Math.Floor(x / S) * S : x, spot.SnapY ? Math.Floor(y / S) * S : y);
    }

    // ------------------------------------------------------------ portal scenario

    /// <summary>The portal topology with both portals open or closed, and entity 1 and entity 2 beside the ends of <paramref name="portalId"/>.</summary>
    private static (Regionizer Regionizer, PortalSpec[] Portals) PortalScenario(ClaimAnchoring anchoring, int mergeGap, bool open, int portalId)
    {
        PortalSpec[] portals =
        [
            new(CrossLayerPortal, new SectionKey(0, 6, 6), new SectionKey(1, 6, 6), open),
            new(DoorPortal, new SectionKey(0, 9, 14), new SectionKey(0, 10, 14), open),
        ];
        var topology = SectionTopology.Build(
            S,
            [
                Kit1Fixtures.Layer(0, 0, 0, 20, 20, (x, _) => (ushort)(x < 10 ? 1 : 2)),
                Kit1Fixtures.UniformLayer(1, 0, 0, 20, 20, area: 5),
            ],
            portals);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(S, anchoring, Kit1Fixtures.Radii(20), mergeGap));
        var (near, far) = Placements(portalId);
        regionizer.AddEntity(1, EntityClass.Infantry, near.Layer, near.X, near.Y);
        regionizer.AddEntity(2, EntityClass.Infantry, far.Layer, far.X, far.Y);
        return (regionizer, portals);
    }

    private static (Placement Near, Placement Far) Placements(int portalId) => portalId switch
    {
        // Beside (0, 6, 6) on the surface and (1, 7, 6) underground.
        CrossLayerPortal => (new Placement(0, 52.0, 52.0), new Placement(1, 60.0, 52.0)),

        // Beside the door: (0, 9, 14) in area 1 and (0, 10, 14) in area 2.
        DoorPortal => (new Placement(0, 76.0, 116.0), new Placement(0, 84.0, 116.0)),
        _ => throw new ArgumentOutOfRangeException(nameof(portalId)),
    };

    private static int Own(Regionizer regionizer, Placement at)
    {
        Assert.True(regionizer.Topology.TrySectionAt(at.Layer, at.X, at.Y, out var own));
        return own;
    }

    private static bool SameAreaAndLayer(SectionTopology topology, SectionKey key, int own) =>
        key.Layer == topology.KeyOf(own).Layer
        && topology.TryIndexOf(key, out var index)
        && topology.AreaOf(index) == topology.AreaOf(own);

    private static SectionKey[] OracleKeys(Regionizer regionizer, PortalSpec[] portals, int layer, double x, double y, double radius) =>
        Kit1Fixtures.Keys(
            regionizer.Topology,
            Kit1ClaimOracle.Claim(regionizer.Topology, portals, regionizer.IsPortalOpen, layer, x, y, radius, regionizer.Options.Anchoring));

    private readonly record struct Placement(int Layer, double X, double Y);

    private sealed record Spot(int Layer, double Fx, double Fy, bool SnapX, bool SnapY);

    private sealed record PlacedEntity(Spot At, EntityClass Class, Spot? MoveTo);

    private sealed record PropertyCase(double[] Radii, bool[] Open, PlacedEntity[] Entities, bool[] Toggle);
}
