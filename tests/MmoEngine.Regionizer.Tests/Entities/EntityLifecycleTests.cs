using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Entities;

/// <summary>
/// Entity lifecycles (FR-002, FR-003, spec Edge Cases): the two-step teleport, capture points,
/// heavy projectiles and a region whose entities all leave. Each test steps region ticks explicitly
/// on the 60 Hz time base and runs the invariant checker at every tick boundary.
/// </summary>
public sealed class EntityLifecycleTests
{
    private const int S = 16;
    private const int TicksPerStep = 2;

    public static TheoryData<ClaimAnchoring, bool> AnchoringsAndDestinations() => new()
    {
        { ClaimAnchoring.Section, true },
        { ClaimAnchoring.Section, false },
        { ClaimAnchoring.Position, true },
        { ClaimAnchoring.Position, false },
    };

    /// <summary>
    /// A teleport into another region's sections removes the entity from its old region during that
    /// region's tick and adds it on the destination region's next tick; into unowned territory it is
    /// added at the step boundary. At no point does the entity belong to two regions.
    /// </summary>
    [Theory]
    [MemberData(nameof(AnchoringsAndDestinations))]
    public void TwoStepTeleportNeverLeavesTheEntityInTwoRegions(ClaimAnchoring anchoring, bool destinationOwned)
    {
        var log = new Kit1EventLog();
        var regionizer = new Regionizer(SectionTopology.OpenWorld(S, 640, 640), Kit1Fixtures.Options(S, anchoring, Kit1Fixtures.Radii(20)), log);
        regionizer.AddEntity(1, EntityClass.Infantry, 0, 40.0, 40.0);
        regionizer.AddEntity(2, EntityClass.Infantry, 0, 400.0, 400.0);
        var source = regionizer.GetEntity(1).RegionId;
        var destination = regionizer.GetEntity(2).RegionId;
        Assert.NotEqual(source, destination);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: before the teleport");

        // Owned destination: beside entity 2. Unowned: open ground far from both.
        (double X, double Y) target = destinationOwned ? (410.0, 404.0) : (600.0, 100.0);
        Assert.Equal(destinationOwned ? destination : 0, regionizer.OwnerOfLocation(0, target.X, target.Y));

        // Step 1, during the source region's tick: begin the teleport.
        regionizer.AdvanceTo(TicksPerStep);
        Assert.True(regionizer.TryMarkTicking(source));
        regionizer.BeginTeleport(1, 0, target.X, target.Y);
        AssertInTransit(regionizer, 1);
        regionizer.MarkNotTicking(source);
        AssertInTransit(regionizer, 1);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: after the source region's tick");

        // The source region has lost its only entity, so its tick end killed it.
        Assert.False(regionizer.TryGetState(source, out _), $"{anchoring}: source region {source} outlived its last entity");

        regionizer.AdvanceTo(2 * TicksPerStep);
        if (destinationOwned)
        {
            // Step 2, during the destination region's next tick: complete it there.
            Assert.True(regionizer.TryMarkTicking(destination));
            AssertInTransit(regionizer, 1);
            regionizer.CompleteTeleport(1);
            AssertInExactlyOneRegion(regionizer, 1, destination);
            regionizer.MarkNotTicking(destination);
        }
        else
        {
            // Step 2, at the step boundary: the arrival creates a region of its own.
            regionizer.CompleteTeleport(1);
        }

        var arrived = regionizer.GetEntity(1).RegionId;
        AssertInExactlyOneRegion(regionizer, 1, arrived);
        if (destinationOwned)
        {
            Assert.Equal(destination, arrived);
        }
        else
        {
            Assert.Contains((arrived, RegionCreationReason.NewClaim, 0L), log.Created);
            Assert.NotEqual(destination, arrived);
        }

        Assert.NotEqual(source, arrived);
        Assert.Equal((target.X, target.Y), (regionizer.GetEntity(1).X, regionizer.GetEntity(1).Y));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: after the teleport completed");

        // The owner changes seen by an observer never give entity 1 two owners: each change leaves the previous one.
        long owner = 0;
        foreach (var (_, from, to) in log.EntityOwnerChanges.Where(c => c.EntityId == 1))
        {
            Assert.Equal(owner, from);
            Assert.True(from == 0 || to == 0, $"{anchoring}: entity 1 moved straight from region {from} to {to} without leaving first");
            owner = to;
        }

        Assert.Equal(arrived, owner);
    }

    /// <summary>
    /// An unoccupied capture point claims only its own section; an occupied one claims its zone; once
    /// it becomes unoccupied it stops claiming the zone, and its region drops the zone at the next tick end.
    /// </summary>
    // Regression (fixed in the core): an unoccupied capture point claims only its own section.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 88.0, 88.0)]
    [InlineData(ClaimAnchoring.Section, 130.5, 71.25)]
    [InlineData(ClaimAnchoring.Position, 88.0, 88.0)]
    [InlineData(ClaimAnchoring.Position, 130.5, 71.25)]
    public void CapturePointClaimsItsZoneOnlyWhileOccupied(ClaimAnchoring anchoring, double x, double y)
    {
        const double zone = 30;
        var topology = SectionTopology.OpenWorld(S, 320, 320);
        var regionizer = new Regionizer(topology, Kit1Fixtures.Options(S, anchoring, new ClaimRadii(8, 8, 8, 8, zone)));
        Assert.True(topology.TrySectionAt(0, x, y, out var own));
        var zoneSections = Kit1ClaimOracle.OpenWorldClaim(topology, own, x, y, zone, anchoring);
        Assert.True(zoneSections.Length > 1, $"the {zone} m zone covers only {zoneSections.Length} section(s)");

        // Unoccupied when placed: its own section only.
        regionizer.AddEntity(7, EntityClass.CapturePoint, 0, x, y);
        var region = regionizer.GetEntity(7).RegionId;
        Assert.Equal([own], regionizer.GetEntity(7).ClaimedSections.ToArray());
        Assert.Equal([own], Kit1Fixtures.SectionsOf(regionizer, region));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: unoccupied capture point placed");

        // Occupied during a tick: it claims its zone, which its region owns from the tick end.
        regionizer.AdvanceTo(TicksPerStep);
        Assert.True(regionizer.TryMarkTicking(region));
        regionizer.SetCapturePointOccupied(7, true);
        Assert.Equal(zoneSections, regionizer.GetEntity(7).ClaimedSections.ToArray());
        regionizer.MarkNotTicking(region);
        Assert.Equal(region, regionizer.GetEntity(7).RegionId);
        Assert.Equal(zoneSections, Kit1Fixtures.SectionsOf(regionizer, region));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: capture point occupied");

        // Unoccupied again during a tick: back to its own section, and the zone is dropped at that tick end.
        regionizer.AdvanceTo(2 * TicksPerStep);
        Assert.True(regionizer.TryMarkTicking(region));
        regionizer.SetCapturePointOccupied(7, false);
        Assert.Equal([own], regionizer.GetEntity(7).ClaimedSections.ToArray());
        regionizer.MarkNotTicking(region);
        Assert.Equal([own], Kit1Fixtures.SectionsOf(regionizer, region));
        Assert.Equal(1, regionizer.ClaimCountOf(own));
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: capture point unoccupied again");
    }

    public static TheoryData<ClaimAnchoring, double, double> ProjectileFlights() => new()
    {
        // Across the infantry's region and out into unowned ground, in both anchorings.
        { ClaimAnchoring.Section, 100.0, 100.0 },
        { ClaimAnchoring.Section, 300.0, 250.0 },
        { ClaimAnchoring.Position, 100.0, 100.0 },
        { ClaimAnchoring.Position, 300.0, 250.0 },
    };

    [Theory]
    [MemberData(nameof(ProjectileFlights))]
    public void HeavyProjectileClaimsNothing(ClaimAnchoring anchoring, double landX, double landY)
    {
        var log = new Kit1EventLog();
        var regionizer = new Regionizer(SectionTopology.OpenWorld(S, 320, 320), Kit1Fixtures.Options(S, anchoring, Kit1Fixtures.Radii(20)), log);
        regionizer.AddEntity(1, EntityClass.Infantry, 0, 100.0, 100.0);
        var region = regionizer.GetEntity(1).RegionId;
        var claimTotal = Kit1Fixtures.TotalClaimCount(regionizer);
        var owned = Kit1Fixtures.SectionsOf(regionizer, region);
        var created = log.Created.Count;

        // Fired from inside the region, it flies through the region's sections during a tick and lands.
        regionizer.AdvanceTo(TicksPerStep);
        Assert.True(regionizer.TryMarkTicking(region));
        regionizer.AddEntity(2, EntityClass.HeavyProjectile, 0, 104.0, 98.0);
        Assert.Empty(regionizer.GetEntity(2).ClaimedSections.ToArray());
        foreach (var (x, y) in new[] { (140.0, 120.0), (200.0, 160.0), (landX, landY) })
        {
            regionizer.MoveEntity(2, 0, x, y);
            Assert.Empty(regionizer.GetEntity(2).ClaimedSections.ToArray());
            Assert.Equal(claimTotal, Kit1Fixtures.TotalClaimCount(regionizer));
        }

        regionizer.MarkNotTicking(region);

        // Nothing it passed over was claimed, owned or created on its account.
        Assert.Empty(regionizer.GetEntity(2).ClaimedSections.ToArray());
        Assert.Equal(claimTotal, Kit1Fixtures.TotalClaimCount(regionizer));
        Assert.Equal(owned, Kit1Fixtures.SectionsOf(regionizer, region));
        Assert.Equal(created, log.Created.Count);
        Assert.Equal([region], regionizer.RegionIds.ToArray());
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: projectile landed at ({landX}, {landY})");
    }

    public static TheoryData<ClaimAnchoring, int, bool> Departures() => new()
    {
        { ClaimAnchoring.Section, 1, true },
        { ClaimAnchoring.Section, 3, true },
        { ClaimAnchoring.Section, 3, false },
        { ClaimAnchoring.Position, 1, true },
        { ClaimAnchoring.Position, 3, true },
        { ClaimAnchoring.Position, 3, false },
    };

    /// <summary>
    /// When every entity leaves a region, its sections go dead and it dies at its next tick end, even
    /// with split damping that would never let it split.
    /// </summary>
    [Theory]
    [MemberData(nameof(Departures))]
    public void RemovingEveryEntityKillsTheRegionAtItsNextTickEnd(ClaimAnchoring anchoring, int entityCount, bool removeDuringTick)
    {
        var log = new Kit1EventLog();
        var options = Kit1Fixtures.Options(S, anchoring, Kit1Fixtures.Radii(20), splitMinSections: 100_000, splitMinDeadPercent: 100, splitMinAgeSteps: 1_000_000);
        var regionizer = new Regionizer(SectionTopology.OpenWorld(S, 320, 320), options, log);
        for (var id = 1; id <= entityCount; id++)
        {
            regionizer.AddEntity(id, EntityClass.Infantry, 0, 100.0 + (10 * id), 100.0);
        }

        var region = regionizer.GetEntity(1).RegionId;
        Assert.Equal([region], regionizer.RegionIds.ToArray());
        var owned = Kit1Fixtures.SectionsOf(regionizer, region);

        regionizer.AdvanceTo(TicksPerStep);
        if (removeDuringTick)
        {
            Assert.True(regionizer.TryMarkTicking(region));
            RemoveAll(regionizer, entityCount);
        }
        else
        {
            // Removed at a boundary: the region lives on, owning only dead sections, until it next ticks.
            RemoveAll(regionizer, entityCount);
            Assert.True(regionizer.TryGetState(region, out var state));
            Assert.Equal(RegionState.Ready, state);
            Assert.Equal(owned.Length, regionizer.GetRegion(region).DeadSectionCount);
            Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: every entity removed at a boundary");
            regionizer.AdvanceTo(2 * TicksPerStep);
            Assert.True(regionizer.TryMarkTicking(region));
        }

        Assert.True(regionizer.TryGetState(region, out var during));
        Assert.Equal(RegionState.Ticking, during);
        Assert.All(owned, s => Assert.Equal(0, regionizer.ClaimCountOf(s)));

        var result = regionizer.MarkNotTicking(region);

        Assert.Equal(RegionState.Dead, result.State);
        Assert.False(regionizer.TryGetState(region, out _));
        Assert.Empty(regionizer.RegionIds.ToArray());
        Assert.All(owned, s => Assert.Equal(0, regionizer.OwnerOf(s)));
        Assert.Equal([region], log.Died);
        Kit1Fixtures.AssertInvariantsHold(regionizer, $"{anchoring}: after the region's tick end");
    }

    /// <summary>A layer-changing move in <see cref="LayerChangeOnlyThroughOpenPortalOrDirectEntryLayer"/>.</summary>
    public enum LayerMove
    {
        /// <summary>From the portal's surface endpoint to its underground endpoint, the portal open: allowed.</summary>
        AcrossOpenPortal,

        /// <summary>The same move with the portal closed: rejected.</summary>
        AcrossClosedPortal,

        /// <summary>From a surface section beside the open portal's endpoint to the underground: rejected.</summary>
        FromNonEndpointSection,

        /// <summary>From the open portal's endpoint to an underground section that is not its far end: rejected.</summary>
        FromEndpointToAnotherSection,

        /// <summary>Onto and off the extra layer of <see cref="SectionTopology.OpenWorld"/> (direct entry): allowed.</summary>
        OntoAndOffOpenWorldDirectEntryLayer,

        /// <summary>Onto and off a built layer with <see cref="LayerSpec.AllowsDirectEntry"/>: allowed.</summary>
        OntoAndOffBuiltDirectEntryLayer,
    }

    /// <summary>The cases of <see cref="LayerChangeOnlyThroughOpenPortalOrDirectEntryLayer"/>.</summary>
    public static TheoryData<LayerMove, ClaimAnchoring, int> LayerMoves()
    {
        var data = new TheoryData<LayerMove, ClaimAnchoring, int>();
        var seed = 7100;
        foreach (var move in Enum.GetValues<LayerMove>())
        {
            data.Add(move, ClaimAnchoring.Section, ++seed);
            data.Add(move, ClaimAnchoring.Position, ++seed);
        }

        return data;
    }

    /// <summary>
    /// FR-001: a move changes an entity's layer only across an open portal from its current section,
    /// or onto or off a layer that allows direct entry; its claim then follows it. Any other layer
    /// change throws <see cref="InvalidOperationException"/> and leaves the entity's layer, position,
    /// region and claim, and every claim count, as they were. Each move is made during the entity's
    /// region's tick, and the invariant checker runs at the tick boundary after it.
    /// </summary>
    [Theory]
    [MemberData(nameof(LayerMoves))]
    public void LayerChangeOnlyThroughOpenPortalOrDirectEntryLayer(LayerMove move, ClaimAnchoring anchoring, int seed)
    {
        const int portalId = 7;
        const int surface = 0;
        const int underground = 1;
        const int portalX = 8;
        const int row = 8;
        const int entity = 1;
        var directLayer = move == LayerMove.OntoAndOffOpenWorldDirectEntryLayer ? 2 : 3;
        var topology = move switch
        {
            LayerMove.AcrossClosedPortal => PortalLayers(open: false),
            LayerMove.OntoAndOffOpenWorldDirectEntryLayer => SectionTopology.OpenWorld(Kit2Harness.SectionSize, Kit2Harness.SectionSize * 32, Kit2Harness.SectionSize * 32, directLayer),
            LayerMove.OntoAndOffBuiltDirectEntryLayer => SectionTopology.Build(
                Kit2Harness.SectionSize,
                [Kit1Fixtures.UniformLayer(surface, 0, 0, 32, 32), Kit1Fixtures.UniformLayer(directLayer, 0, 0, 32, 32) with { AllowsDirectEntry = true }],
                []),
            _ => PortalLayers(open: true),
        };
        var h = new Kit2Harness(nameof(LayerChangeOnlyThroughOpenPortalOrDirectEntryLayer), seed, Kit2Harness.Options(anchoring), topology);
        var (startX, targetLayer, targetX) = move switch
        {
            LayerMove.FromNonEndpointSection => (portalX - 1, underground, portalX - 1),
            LayerMove.FromEndpointToAnotherSection => (portalX, underground, portalX + 1),
            LayerMove.OntoAndOffOpenWorldDirectEntryLayer or LayerMove.OntoAndOffBuiltDirectEntryLayer => (5, directLayer, 5),
            _ => (portalX, underground, portalX),
        };
        h.True(topology.AllowsDirectEntry(directLayer) == (move is LayerMove.OntoAndOffOpenWorldDirectEntryLayer or LayerMove.OntoAndOffBuiltDirectEntryLayer), "only the direct-entry cases have a direct-entry layer");
        h.Equal(false, topology.AllowsDirectEntry(surface), "the surface layer allows direct entry");

        // Given an entity on the surface, during its region's tick.
        h.Add(entity, startX, row, layer: surface);
        h.AssertClean();
        var region = h.RegionOf(entity);
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(region);
        var before = h.R.GetEntity(entity);
        var (layer0, x0, y0) = (before.Layer, before.X, before.Y);
        var claim0 = string.Join(",", h.ClaimOf(entity));
        var total0 = h.TotalClaims();
        var (tx, ty) = h.Spot(targetX, row);

        var allowed = move is LayerMove.AcrossOpenPortal or LayerMove.OntoAndOffOpenWorldDirectEntryLayer or LayerMove.OntoAndOffBuiltDirectEntryLayer;
        if (!allowed)
        {
            // When it moves to another layer any other way, Then the move is rejected and nothing changes.
            h.Throws<InvalidOperationException>(() => h.R.MoveEntity(entity, targetLayer, tx, ty), $"{move}: a move from layer {surface} to layer {targetLayer}");
            var after = h.R.GetEntity(entity);
            h.Equal(layer0, after.Layer, $"{move}: layer after the rejected move");
            h.Equal((x0, y0), (after.X, after.Y), $"{move}: position after the rejected move");
            h.Equal(region, after.RegionId, $"{move}: region after the rejected move");
            h.Equal(claim0, string.Join(",", h.ClaimOf(entity)), $"{move}: claim after the rejected move");
            h.Equal(total0, h.TotalClaims(), $"{move}: total claim count after the rejected move");
            h.End(region);
            h.AssertClean();
            return;
        }

        // When it moves onto the other layer: across the open portal, or onto the direct-entry layer.
        h.R.MoveEntity(entity, targetLayer, tx, ty);

        // Then it stands on that layer and its claim follows: through the portal, both endpoints' footprints.
        var moved = h.R.GetEntity(entity);
        h.Equal(targetLayer, moved.Layer, $"{move}: layer after the move");
        h.Equal((tx, ty), (moved.X, moved.Y), $"{move}: position after the move");
        h.Equal(region, moved.RegionId, $"{move}: region after the move");
        var expected = move == LayerMove.AcrossOpenPortal
            ? h.Footprint(underground, portalX, row).Union(h.Footprint(surface, portalX, row))
            : h.Footprint(directLayer, targetX, row);
        h.SameSections(expected, h.ClaimOf(entity), $"{move}: claim after the move");

        // ... and a further move shows it is on the new layer: off the endpoint it claims that layer
        // only, and off the direct-entry layer (back to the surface) it claims the surface only.
        var (backLayer, backX) = move == LayerMove.AcrossOpenPortal ? (underground, portalX + 3) : (surface, targetX + 1);
        var (bx, by) = h.Spot(backX, row);
        h.R.MoveEntity(entity, backLayer, bx, by);
        h.Equal(backLayer, h.R.GetEntity(entity).Layer, $"{move}: layer after the second move");
        h.SameSections(h.Footprint(backLayer, backX, row), h.ClaimOf(entity), $"{move}: claim after the second move");
        h.Equal(RegionState.Ready, h.End(region).State, $"{move}: state of the region after its tick end");
        h.Equal(region, h.RegionOf(entity), $"{move}: region after the tick end");
        h.AssertClean();

        static SectionTopology PortalLayers(bool open) =>
            SectionTopology.Build(
                Kit2Harness.SectionSize,
                [Kit1Fixtures.UniformLayer(surface, 0, 0, 32, 32), Kit1Fixtures.UniformLayer(underground, 0, 0, 32, 32)],
                [new PortalSpec(portalId, new SectionKey(surface, portalX, row), new SectionKey(underground, portalX, row), open)]);
    }

    /// <summary>
    /// FR-003: a heavy projectile must start in a section its firing region owns. Fired into unowned
    /// ground, just beside the firing region's claim or far from it, it is refused: the projectile does
    /// not exist afterwards, no region was created and no claim count changed. Fired from inside the
    /// region, it is accepted.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, true, 7201)]
    [InlineData(ClaimAnchoring.Section, false, 7202)]
    [InlineData(ClaimAnchoring.Position, true, 7203)]
    [InlineData(ClaimAnchoring.Position, false, 7204)]
    public void HeavyProjectileCannotStartInAnUnownedSection(ClaimAnchoring anchoring, bool besideTheRegion, int seed)
    {
        const int shooter = 1;
        const int projectile = 2;
        const int row = 8;
        var h = new Kit2Harness(nameof(HeavyProjectileCannotStartInAnUnownedSection), seed, Kit2Harness.Options(anchoring));
        h.Add(shooter, 8, row);
        h.AssertClean();
        var region = h.RegionOf(shooter);
        var owned = string.Join(",", h.SectionsOf(region).Order());
        var created = h.Events.Of<Kit2Created>().Count();
        var total = h.TotalClaims();

        // Section mode owns columns 7–9, position mode column 8: column 10 is unowned either way.
        var column = besideTheRegion ? 10 : 20;
        var (x, y) = h.Spot(column, row);
        h.Equal(0L, h.R.OwnerOfLocation(0, x, y), $"owner of the launch point in column {column}");

        // When the firing region, during its tick, fires a heavy projectile from unowned ground.
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(region);
        h.Throws<InvalidOperationException>(() => h.R.AddEntity(projectile, EntityClass.HeavyProjectile, 0, x, y), $"a heavy projectile starting in unowned column {column}");

        // Then it does not exist, no region was created and the claims are unchanged.
        h.Equal(false, h.R.HasEntity(projectile), "the refused projectile exists");
        h.Equal(created, h.Events.Of<Kit2Created>().Count(), "regions created");
        h.Equal(region.ToString(), string.Join(",", h.R.RegionIds.ToArray()), "live regions");
        h.Equal(total, h.TotalClaims(), "total claim count");
        h.Equal(owned, string.Join(",", h.SectionsOf(region).Order()), "sections of the firing region");
        h.Equal(shooter.ToString(), string.Join(",", h.EntitiesOf(region)), "entities of the firing region");

        // Control: fired from the shooter's own section, it is accepted into the firing region and claims nothing.
        var (sx, sy) = h.Spot(8, row);
        h.R.AddEntity(projectile, EntityClass.HeavyProjectile, 0, sx, sy);
        h.Equal(region, h.RegionOf(projectile), "region of a projectile fired from an owned section");
        h.Equal(0, h.ClaimOf(projectile).Length, "sections the accepted projectile claims");
        h.Equal(total, h.TotalClaims(), "total claim count after the accepted projectile");
        h.End(region);
        h.AssertClean();
    }

    private static void RemoveAll(Regionizer regionizer, int entityCount)
    {
        for (var id = 1; id <= entityCount; id++)
        {
            regionizer.RemoveEntity(id);
        }
    }

    private static void AssertInTransit(Regionizer regionizer, int entityId)
    {
        var entity = regionizer.GetEntity(entityId);
        Assert.True(entity.InTransit, $"entity {entityId} is not in transit");
        Assert.Equal(0, entity.RegionId);
        Assert.Empty(entity.ClaimedSections.ToArray());
        Assert.Empty(Kit1Fixtures.RegionsListing(regionizer, entityId));
    }

    private static void AssertInExactlyOneRegion(Regionizer regionizer, int entityId, long regionId)
    {
        Assert.False(regionizer.GetEntity(entityId).InTransit, $"entity {entityId} is still in transit");
        Assert.Equal(regionId, regionizer.GetEntity(entityId).RegionId);
        Assert.Equal([regionId], Kit1Fixtures.RegionsListing(regionizer, entityId));
    }
}
