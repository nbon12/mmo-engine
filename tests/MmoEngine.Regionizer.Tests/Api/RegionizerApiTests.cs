using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Api;

/// <summary>
/// The façade's read views and its refusals (contracts/regionizer-api.md): what the entity and region
/// views report, a regionizer without an observer, and every call refused for an unknown id, a
/// position off the map, an entity in transit, a region that is not ticking or a step backwards.
/// Every refusal throws a specific exception and leaves the regionizer as it was.
/// </summary>
public sealed class RegionizerApiTests
{
    private const int Row = 8;
    private const int Infantry = 1;
    private const int Unknown = 999;

    /// <summary>
    /// The entity and region views report exactly what was placed: id, class, layer, position, height,
    /// fight tag, region, transit flag and claim; and the region's id, state, tick counter, creation
    /// step, sections, dead count, entities, timers and pending merges. An entity id past the table's
    /// first 64 slots grows the table.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 9501)]
    [InlineData(ClaimAnchoring.Position, 9502)]
    public void EntityAndRegionViewsReportWhatWasPlaced(ClaimAnchoring anchoring, int seed)
    {
        const int vehicle = 130;
        var h = new Kit2Harness(nameof(EntityAndRegionViewsReportWhatWasPlaced), seed, Kit2Harness.Options(anchoring));
        h.AdvanceTo(6);
        var (x, y) = h.Spot(10, 12);
        h.R.AddEntity(vehicle, EntityClass.GroundVehicle, 0, x, y, z: 12.5, fightTag: 3);
        var region = h.RegionOf(vehicle);
        var timer = h.R.AddTimer(vehicle, 4);

        var e = h.R.GetEntity(vehicle);
        h.Equal(vehicle, e.Id, "entity id");
        h.Equal(EntityClass.GroundVehicle, e.Class, "entity class");
        h.Equal(0, e.Layer, "entity layer");
        h.Equal((x, y, 12.5), (e.X, e.Y, e.Z), "entity position");
        h.Equal(3, e.FightTag, "entity fight tag");
        h.Equal(region, e.RegionId, "entity region");
        h.Equal(false, e.InTransit, "entity in transit");
        h.SameSections(h.Footprint(0, 10, 12), e.ClaimedSections.ToArray(), "entity claim");
        h.Equal(true, h.R.HasEntity(vehicle), "HasEntity of the vehicle");
        h.Equal(vehicle.ToString(), string.Join(",", h.R.EntityIds.ToArray()), "entity ids");

        var r = h.R.GetRegion(region);
        h.Equal(region, r.Id, "region id");
        h.Equal(RegionState.Ready, r.State, "region state");
        h.Equal(0L, r.TickCounter, "region tick counter");
        h.Equal(6L, r.CreatedAt, "region creation step");
        h.Equal(h.Footprint(0, 10, 12).Count, r.SectionCount, "region section count");
        h.Equal(0, r.DeadSectionCount, "region dead section count");
        h.SameSections(h.Footprint(0, 10, 12), r.Sections.ToArray(), "region sections");
        h.Equal(vehicle.ToString(), string.Join(",", r.Entities.ToArray()), "region entities");
        h.Equal(new TimerView(timer, vehicle, 4, 4, 0).ToString(), string.Join(";", r.Timers.ToArray()), "region timers");
        h.Equal(0, r.PendingMergePartners.Length, "region pending merges");
        h.Equal(true, h.R.TryGetState(region, out var state) && state == RegionState.Ready, "TryGetState of the live region");
        h.Equal(1L, h.R.Stats.RegionsCreated, "regions created");

        var own = h.Index(0, 10, 12);
        h.Equal(region, h.R.OwnerOf(own), "owner of the vehicle's section");
        h.Equal(1, h.R.ClaimCountOf(own), "claims on the vehicle's section");
        h.Equal(region, h.R.OwnerOfLocation(0, x, y), "owner of the vehicle's location");
        h.Equal(0L, h.R.OwnerOfLocation(0, -1, y), "owner of a location off the map");
        h.Equal(0L, h.R.OwnerOfLocation(5, x, y), "owner of a location on a layer that does not exist");

        // Defaults: no height and no fight tag.
        var (ix, iy) = h.Spot(24, 24);
        h.R.AddEntity(Infantry, EntityClass.Infantry, 0, ix, iy);
        var infantry = h.R.GetEntity(Infantry);
        h.Equal((0.0, -1), (infantry.Z, infantry.FightTag), "default height and fight tag");
        h.Equal($"{Infantry},{vehicle}", string.Join(",", h.R.EntityIds.ToArray()), "entity ids, ascending");

        // While its region ticks, the view shows the tick: ticking, with the counter advanced.
        h.AdvanceTo(8);
        h.Start(region);
        var ticking = h.R.GetRegion(region);
        h.Equal((RegionState.Ticking, 1L), (ticking.State, ticking.TickCounter), "state and tick counter during the tick");
        h.End(region);

        // In transit the entity has no region and no claim.
        var (tx, ty) = h.Spot(20, 4);
        h.R.BeginTeleport(vehicle, 0, tx, ty);
        var transit = h.R.GetEntity(vehicle);
        h.Equal((true, 0L, 0), (transit.InTransit, transit.RegionId, transit.ClaimedSections.Length), "transit flag, region and claim size in transit");
        h.AssertClean();
    }

    /// <summary>
    /// A regionizer built without an observer uses its own null observer: timers still fire (a 3-tick
    /// timer is gone after the third tick and not before) and a region still splits.
    /// </summary>
    [Theory]
    [InlineData(ClaimAnchoring.Section, 9511)]
    [InlineData(ClaimAnchoring.Position, 9512)]
    public void RegionizerWithoutAnObserverStillFiresTimersAndSplits(ClaimAnchoring anchoring, int seed)
    {
        var h = new Kit2Harness(
            nameof(RegionizerWithoutAnObserverStillFiresTimersAndSplits),
            seed,
            Kit2Harness.Options(anchoring, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: 0),
            observe: false);
        h.Add(1, 9, Row);
        h.Add(2, 9, Row);
        var region = h.RegionOf(1);
        var timer = h.R.AddTimer(1, 3);

        h.Lockstep();
        h.Lockstep();
        h.Equal(timer.ToString(), string.Join(",", h.R.GetRegion(region).Timers.ToArray().Select(t => t.TimerId)), "timers before the third tick");
        h.Lockstep();
        h.Equal(3L, h.R.GetRegion(region).TickCounter, "tick counter after three ticks");
        h.Equal(0, h.R.GetRegion(region).Timers.Length, "timers after the third tick (the timer fired)");

        var result = h.Lockstep(() =>
        {
            h.MoveInTick(1, 4, Row);
            h.MoveInTick(2, 14, Row);
        })[region];

        h.Equal(2, result.SplitChildren, "regions after the split");
        h.Equal(1L, h.R.Stats.Splits, "splits counted");
        h.Equal(2, h.R.RegionIds.Length, "live regions");
        h.Equal(region, h.RegionOf(1), "the west island keeps the parent id");
        h.True(h.RegionOf(2) != region, "the east island is a new region");
        h.Equal(0, h.Events.Log.Count, "events recorded by the observer the regionizer was not given");
    }

    /// <summary>The time base never moves back; moving to the same step is allowed.</summary>
    [Theory]
    [InlineData(10L, 9L, 9521)]
    [InlineData(10L, 0L, 9522)]
    public void TimeNeverMovesBack(long now, long earlier, int seed)
    {
        var h = new Kit2Harness(nameof(TimeNeverMovesBack), seed, Kit2Harness.Options());
        h.R.AdvanceTo(now);

        h.Throws<ArgumentOutOfRangeException>(() => h.R.AdvanceTo(earlier), $"moving the time base from {now} back to {earlier}");

        h.Equal(now, h.R.Now, "time base after the refused move");
        h.R.AdvanceTo(now);
        h.Equal(now, h.R.Now, "time base after moving to the same step");
    }

    /// <summary>Each call naming an unknown entity, region or portal, and the exception it throws.</summary>
    public static TheoryData<string, string, int> UnknownIdCalls() => new()
    {
        { "GetEntity", nameof(KeyNotFoundException), 9531 },
        { "RemoveEntity", nameof(KeyNotFoundException), 9532 },
        { "MoveEntity", nameof(KeyNotFoundException), 9533 },
        { "SetCapturePointOccupied", nameof(KeyNotFoundException), 9534 },
        { "BeginTeleport", nameof(KeyNotFoundException), 9535 },
        { "AddTimer", nameof(KeyNotFoundException), 9536 },
        { "CompleteTeleport", nameof(InvalidOperationException), 9537 },
        { "GetRegion", nameof(KeyNotFoundException), 9538 },
        { "MarkNotTicking", nameof(InvalidOperationException), 9539 },
        { "RunDueTimers", nameof(InvalidOperationException), 9540 },
        { "IsPortalOpen", nameof(KeyNotFoundException), 9541 },
        { "SetPortalOpen", nameof(KeyNotFoundException), 9542 },
    };

    /// <summary>A call naming an unknown id throws the stated exception and changes nothing.</summary>
    [Theory]
    [MemberData(nameof(UnknownIdCalls))]
    public void UnknownIdsAreRefused(string call, string exception, int seed)
    {
        var h = OneEntity(nameof(UnknownIdsAreRefused), seed);
        var before = Snapshot(h);
        var (x, y) = h.Spot(12, Row);
        Action act = call switch
        {
            "GetEntity" => () => h.R.GetEntity(Unknown),
            "RemoveEntity" => () => h.R.RemoveEntity(Unknown),
            "MoveEntity" => () => h.R.MoveEntity(Unknown, 0, x, y),
            "SetCapturePointOccupied" => () => h.R.SetCapturePointOccupied(Unknown, true),
            "BeginTeleport" => () => h.R.BeginTeleport(Unknown, 0, x, y),
            "AddTimer" => () => h.R.AddTimer(Unknown, 5),
            "CompleteTeleport" => () => h.R.CompleteTeleport(Unknown),
            "GetRegion" => () => h.R.GetRegion(Unknown),
            "MarkNotTicking" => () => h.R.MarkNotTicking(Unknown),
            "RunDueTimers" => () => h.R.RunDueTimers(Unknown),
            "IsPortalOpen" => () => h.R.IsPortalOpen(Unknown),
            "SetPortalOpen" => () => h.R.SetPortalOpen(Unknown, true),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, "unknown call"),
        };

        var thrown = Record.Exception(act);

        h.Equal(exception, thrown?.GetType().Name, $"exception from {call} with an unknown id");
        h.Equal(before, Snapshot(h), $"state after the refused {call}");
        h.Equal(false, h.R.HasEntity(Unknown), "HasEntity of the unknown id");
        h.Equal(false, h.R.TryGetState(Unknown, out var state), "TryGetState of the unknown region");
        h.Equal(RegionState.Dead, state, "state reported for the unknown region");
        h.Equal(false, h.R.TryMarkTicking(Unknown), "TryMarkTicking of the unknown region");
    }

    /// <summary>Each call naming a position off the map: west of it, past its east edge, not a number, or on a layer that does not exist.</summary>
    public static TheoryData<string, double, int, int> OffMapCalls()
    {
        var data = new TheoryData<string, double, int, int>();
        var seed = 9550;
        foreach (var call in new[] { "AddEntity", "MoveEntity", "BeginTeleport" })
        {
            data.Add(call, -0.5, 0, ++seed);
            data.Add(call, Kit2Harness.SectionSize * Kit2Harness.MapSections, 0, ++seed);
            data.Add(call, double.NaN, 0, ++seed);
            data.Add(call, 100.0, 4, ++seed);
        }

        return data;
    }

    /// <summary>A position off the map is refused with <see cref="ArgumentOutOfRangeException"/> and changes nothing.</summary>
    [Theory]
    [MemberData(nameof(OffMapCalls))]
    public void PositionsOffTheMapAreRefused(string call, double x, int layer, int seed)
    {
        const int newcomer = 2;
        var h = OneEntity(nameof(PositionsOffTheMapAreRefused), seed);
        var before = Snapshot(h);
        const double y = 100.0;
        Action act = call switch
        {
            "AddEntity" => () => h.R.AddEntity(newcomer, EntityClass.Infantry, layer, x, y),
            "MoveEntity" => () => h.R.MoveEntity(Infantry, layer, x, y),
            "BeginTeleport" => () => h.R.BeginTeleport(Infantry, layer, x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, "unknown call"),
        };

        h.Throws<ArgumentOutOfRangeException>(act, $"{call} to ({x}, {y}) on layer {layer}");

        h.Equal(before, Snapshot(h), $"state after the refused {call}");
        h.Equal(false, h.R.HasEntity(newcomer), "the refused newcomer exists");
        h.Equal(false, h.R.GetEntity(Infantry).InTransit, "the entity is in transit");
    }

    /// <summary>Each call refused for an entity in transit.</summary>
    public static TheoryData<string, int> TransitCalls() => new()
    {
        { "MoveEntity", 9571 },
        { "SetCapturePointOccupied", 9572 },
        { "BeginTeleport", 9573 },
        { "AddTimer", 9574 },
    };

    /// <summary>
    /// An entity in transit (between the two steps of a teleport) cannot move, change occupancy, start
    /// another teleport or get a timer: each throws <see cref="InvalidOperationException"/>, and it
    /// stays in transit with no region until the teleport completes.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransitCalls))]
    public void EntityInTransitIsRefusedUntilItArrives(string call, int seed)
    {
        var h = OneEntity(nameof(EntityInTransitIsRefusedUntilItArrives), seed);
        var (tx, ty) = h.Spot(20, Row);
        h.R.BeginTeleport(Infantry, 0, tx, ty);
        var (x, y) = h.Spot(21, Row);
        Action act = call switch
        {
            "MoveEntity" => () => h.R.MoveEntity(Infantry, 0, x, y),
            "SetCapturePointOccupied" => () => h.R.SetCapturePointOccupied(Infantry, true),
            "BeginTeleport" => () => h.R.BeginTeleport(Infantry, 0, x, y),
            "AddTimer" => () => h.R.AddTimer(Infantry, 5),
            _ => throw new ArgumentOutOfRangeException(nameof(call), call, "unknown call"),
        };

        h.Throws<InvalidOperationException>(act, $"{call} for an entity in transit");

        var e = h.R.GetEntity(Infantry);
        h.Equal((true, 0L), (e.InTransit, e.RegionId), $"transit flag and region after the refused {call}");
        h.R.CompleteTeleport(Infantry);
        var arrived = h.R.GetEntity(Infantry);
        h.Equal((false, tx, ty), (arrived.InTransit, arrived.X, arrived.Y), "the teleport completes at its first destination");
        h.AssertClean();
    }

    /// <summary>
    /// Occupancy applies only to capture points: on infantry it throws and changes nothing. Setting a
    /// capture point to the occupancy it already has changes nothing either.
    /// </summary>
    [Theory]
    [InlineData(9581)]
    [InlineData(9582)]
    public void OccupancyAppliesOnlyToCapturePoints(int seed)
    {
        const int point = 2;
        var h = OneEntity(nameof(OccupancyAppliesOnlyToCapturePoints), seed);
        var (px, py) = h.Spot(20, Row);
        h.R.AddEntity(point, EntityClass.CapturePoint, 0, px, py);
        var before = Snapshot(h);

        h.Throws<InvalidOperationException>(() => h.R.SetCapturePointOccupied(Infantry, true), "occupying an infantry entity");
        h.Equal(before, Snapshot(h), "state after the refused occupancy");

        h.R.SetCapturePointOccupied(point, false);
        h.Equal(before, Snapshot(h), "state after setting an unoccupied point unoccupied");
        h.R.SetCapturePointOccupied(point, true);
        var occupied = Snapshot(h);
        h.True(occupied != before, "occupying the point changes its claim");
        h.R.SetCapturePointOccupied(point, true);
        h.Equal(occupied, Snapshot(h), "state after occupying an occupied point again");
        h.AssertClean();
    }

    /// <summary>Completing a teleport needs an entity in transit: an idle one is refused and stays where it is.</summary>
    [Theory]
    [InlineData(9591)]
    [InlineData(9592)]
    public void CompleteTeleportRequiresAnEntityInTransit(int seed)
    {
        var h = OneEntity(nameof(CompleteTeleportRequiresAnEntityInTransit), seed);
        var before = Snapshot(h);

        h.Throws<InvalidOperationException>(() => h.R.CompleteTeleport(Infantry), "completing a teleport that never began");

        h.Equal(before, Snapshot(h), "state after the refused completion");
    }

    /// <summary>
    /// Tick ends and due timers need a ticking region: on a ready one they throw. A ticking region
    /// cannot start again, and a timer needs a delay of at least one.
    /// </summary>
    [Theory]
    [InlineData(9601)]
    [InlineData(9602)]
    public void TickCallsRequireTheRightRegionState(int seed)
    {
        var h = OneEntity(nameof(TickCallsRequireTheRightRegionState), seed);
        var region = h.RegionOf(Infantry);

        h.Throws<InvalidOperationException>(() => h.R.MarkNotTicking(region), "ending the tick of a ready region");
        h.Throws<InvalidOperationException>(() => h.R.RunDueTimers(region), "running the timers of a ready region");
        h.Throws<ArgumentOutOfRangeException>(() => h.R.AddTimer(Infantry, 0), "a timer with delay 0");
        h.Equal(RegionState.Ready, h.StateOf(region), "state after the refused calls");
        h.Equal(0, h.R.GetRegion(region).Timers.Length, "timers after the refused delay");

        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        h.Start(region);
        h.Equal(false, h.R.TryMarkTicking(region), "starting a ticking region again");
        h.Equal(1L, h.R.GetRegion(region).TickCounter, "tick counter after the refused second start");
        h.Equal(RegionState.Ready, h.End(region).State, "state after the tick end");
        h.AssertClean();
    }

    /// <summary>An entity id already in use is refused, and the entity holding it is unchanged.</summary>
    [Theory]
    [InlineData(9611)]
    [InlineData(9612)]
    public void DuplicateEntityIdIsRefused(int seed)
    {
        var h = OneEntity(nameof(DuplicateEntityIdIsRefused), seed);
        var before = Snapshot(h);
        var (x, y) = h.Spot(20, Row);

        h.Throws<ArgumentException>(() => h.R.AddEntity(Infantry, EntityClass.Aircraft, 0, x, y), "adding an entity id already in use");

        h.Equal(before, Snapshot(h), "state after the refused add");
        h.Equal(EntityClass.Infantry, h.R.GetEntity(Infantry).Class, "class of the entity holding the id");
    }

    /// <summary>An infantry entity in its own region at section (8, 8), with a portal-free open world.</summary>
    private static Kit2Harness OneEntity(string name, int seed)
    {
        var h = new Kit2Harness(name, seed, Kit2Harness.Options());
        h.Add(Infantry, 8, Row);
        h.AssertClean();
        return h;
    }

    /// <summary>Everything a refused call must leave as it was, as one comparable string.</summary>
    private static string Snapshot(Kit2Harness h)
    {
        var parts = new List<string> { $"now {h.R.Now}", $"entities {string.Join(",", h.R.EntityIds.ToArray())}", $"claims {h.TotalClaims()}" };
        foreach (var id in h.R.EntityIds.ToArray())
        {
            var e = h.R.GetEntity(id);
            parts.Add($"entity {id}: layer {e.Layer} at ({e.X}, {e.Y}, {e.Z}) region {e.RegionId} transit {e.InTransit} claim {string.Join(",", e.ClaimedSections.ToArray())}");
        }

        foreach (var id in h.R.RegionIds.ToArray())
        {
            var r = h.R.GetRegion(id);
            parts.Add($"region {id}: {r.State} tick {r.TickCounter} sections {string.Join(",", r.Sections.ToArray().Order())} entities {string.Join(",", r.Entities.ToArray())} timers {r.Timers.Length}");
        }

        return string.Join(" | ", parts);
    }
}
