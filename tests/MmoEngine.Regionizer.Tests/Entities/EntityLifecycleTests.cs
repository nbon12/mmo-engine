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
