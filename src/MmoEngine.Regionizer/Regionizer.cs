using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MmoEngine.Regionizer;

/// <summary>
/// The regionizer: groups sections into regions that follow the fight (constitution, Sections and
/// regions). Single-threaded engine infrastructure: callers drive ticks with
/// <see cref="TryMarkTicking"/> and <see cref="MarkNotTicking"/> and move entities during their
/// region's tick. See specs/013-regionizer-simulator/research.md D1–D9 for the rules implemented here.
/// </summary>
public sealed partial class Regionizer
{
    private readonly IRegionizerObserver _observer;
    private readonly IReadOnlyList<IRegionDataExtension> _extensions;
    private readonly ClaimCalculator _claims;
    private readonly bool[] _portalOpen;
    private readonly long[] _owner;
    private readonly int[] _claimCount;
    private readonly EntityTable _entities = new();
    private readonly Dictionary<long, Region> _regions = [];
    private readonly List<long> _regionIds = [];
    private readonly List<Violation> _breaches = [];
    private readonly List<int> _mergeScratch = [];
    private readonly List<TimerView> _timerScratch = [];
    private int[] _claimScratch = new int[256];
    private long _nextRegionId = 1;
    private int _nextTimerId = 1;
    private int _serialDepth;
    private long _serialStart;

    public Regionizer(
        SectionTopology topology,
        RegionizerOptions options,
        IRegionizerObserver? observer = null,
        IReadOnlyList<IRegionDataExtension>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.SectionSize != topology.SectionSize)
        {
            throw new ArgumentException($"Options section size {options.SectionSize} differs from the topology's {topology.SectionSize}.");
        }

        Topology = topology;
        Options = options;
        _observer = observer ?? NullObserver.Instance;
        _extensions = extensions ?? [];
        _portalOpen = new bool[topology.PortalCount];
        for (var p = 0; p < topology.PortalCount; p++)
        {
            _portalOpen[p] = topology.PortalAt(p).Open;
        }

        _owner = new long[topology.SectionCount];
        _claimCount = new int[topology.SectionCount];
        _claims = new ClaimCalculator(topology, _portalOpen);
        _localStamp = new int[topology.SectionCount];
        _localIndex = new int[topology.SectionCount];
    }

    public SectionTopology Topology { get; }

    public RegionizerOptions Options { get; }

    public RegionizerStats Stats { get; } = new();

    /// <summary>The continent time base, in 60 Hz base steps.</summary>
    public long Now { get; private set; }

    /// <summary>Live (non-dead) region ids, ascending.</summary>
    public ReadOnlySpan<long> RegionIds => CollectionsMarshal.AsSpan(_regionIds);

    /// <summary>Entity ids, ascending.</summary>
    public ReadOnlySpan<int> EntityIds => CollectionsMarshal.AsSpan(_entities.Ids);

    /// <summary>Moves the time base forward. It never moves back.</summary>
    public void AdvanceTo(long now)
    {
        if (now < Now)
        {
            throw new ArgumentOutOfRangeException(nameof(now), $"Time cannot move back from {Now} to {now}.");
        }

        Now = now;
    }

    public RegionView GetRegion(long regionId) =>
        _regions.TryGetValue(regionId, out var region) ? new RegionView(region) : throw new KeyNotFoundException($"No live region {regionId}.");

    /// <summary>The state of a live region; false if it is dead or unknown.</summary>
    public bool TryGetState(long regionId, out RegionState state)
    {
        if (_regions.TryGetValue(regionId, out var region))
        {
            state = region.State;
            return true;
        }

        state = RegionState.Dead;
        return false;
    }

    public EntityView GetEntity(int entityId) =>
        _entities.Exists(entityId) ? new EntityView(_entities, entityId) : throw new KeyNotFoundException($"No entity {entityId}.");

    public bool HasEntity(int entityId) => _entities.Exists(entityId);

    public long OwnerOf(int sectionIndex) => _owner[sectionIndex];

    public int ClaimCountOf(int sectionIndex) => _claimCount[sectionIndex];

    public bool IsPortalOpen(int portalId) =>
        Topology.TryPortalSlot(portalId, out var slot) ? _portalOpen[slot] : throw new KeyNotFoundException($"No portal {portalId}.");

    // ---------------------------------------------------------------- entities

    public void AddEntity(int entityId, EntityClass cls, int layer, double x, double y, double z = 0, int fightTag = -1)
    {
        var section = SectionAt(layer, x, y);
        _entities.Add(entityId, _claims.Capacity(Options.ClaimRadius.For(cls)));
        _entities.Class[entityId] = cls;
        _entities.FightTag[entityId] = fightTag;
        SetPosition(entityId, layer, x, y, z, section);
        Place(entityId);
    }

    public void MoveEntity(int entityId, int layer, double x, double y, double z = 0)
    {
        RequireIdle(entityId);
        var section = SectionAt(layer, x, y);
        var previous = _entities.Anchor[entityId];
        SetPosition(entityId, layer, x, y, z, section);
        if (_entities.Class[entityId] == EntityClass.HeavyProjectile)
        {
            FollowSectionOwner(entityId);
            return;
        }

        if (Options.Anchoring == ClaimAnchoring.Section && previous == section)
        {
            return;
        }

        RecomputeClaim(entityId);
    }

    public void RemoveEntity(int entityId)
    {
        if (!_entities.Exists(entityId))
        {
            throw new KeyNotFoundException($"No entity {entityId}.");
        }

        if (!_entities.InTransit[entityId])
        {
            ReleaseClaims(entityId);
            var regionId = _entities.Region[entityId];
            if (regionId != 0)
            {
                var region = _regions[regionId];
                region.RemoveEntity(entityId);
                region.Timers.RemoveAll(t => t.EntityId == entityId);
                _observer.OnEntityOwnerChanged(entityId, regionId, 0);
            }
        }

        _entities.Remove(entityId);
    }

    public void SetCapturePointOccupied(int entityId, bool occupied)
    {
        RequireIdle(entityId);
        if (_entities.Class[entityId] != EntityClass.CapturePoint)
        {
            throw new InvalidOperationException($"Entity {entityId} is not a capture point.");
        }

        if (_entities.Occupied[entityId] == occupied)
        {
            return;
        }

        _entities.Occupied[entityId] = occupied;
        RecomputeClaim(entityId);
    }

    /// <summary>First half of the two-step teleport: removes the entity and its claims, and records its destination.</summary>
    public void BeginTeleport(int entityId, int layer, double x, double y, double z = 0)
    {
        RequireIdle(entityId);
        _ = SectionAt(layer, x, y);
        var regionId = _entities.Region[entityId];
        ReleaseClaims(entityId);
        if (regionId != 0)
        {
            var region = _regions[regionId];
            region.RemoveEntity(entityId);
            List<(TimerView, long)>? carried = null;
            for (var i = region.Timers.Count - 1; i >= 0; i--)
            {
                var timer = region.Timers[i];
                if (timer.EntityId != entityId)
                {
                    continue;
                }

                carried ??= [];
                var remaining = Options.TimerMode == TimerMode.TickCounter ? timer.Deadline - region.TickCounter : timer.Deadline;
                carried.Add((timer, remaining));
                region.Timers.RemoveAt(i);
            }

            _entities.TransitTimers[entityId] = carried;
            _observer.OnEntityOwnerChanged(entityId, regionId, 0);
        }

        _entities.Region[entityId] = 0;
        _entities.InTransit[entityId] = true;
        _entities.Anchor[entityId] = -1;
        _entities.TransitTarget[entityId] = (layer, x, y, z);
    }

    /// <summary>The region owning the section at a location, or 0.</summary>
    public long OwnerOfLocation(int layer, double x, double y) =>
        Topology.TrySectionAt(layer, x, y, out var section) ? _owner[section] : 0;

    /// <summary>Second half of the two-step teleport: adds the entity at its destination.</summary>
    public void CompleteTeleport(int entityId)
    {
        if (!_entities.Exists(entityId) || !_entities.InTransit[entityId])
        {
            throw new InvalidOperationException($"Entity {entityId} is not in transit.");
        }

        var (layer, x, y, z) = _entities.TransitTarget[entityId];
        _entities.InTransit[entityId] = false;
        SetPosition(entityId, layer, x, y, z, SectionAt(layer, x, y));
        Place(entityId);

        var carried = _entities.TransitTimers[entityId];
        _entities.TransitTimers[entityId] = null;
        var regionId = _entities.Region[entityId];
        if (carried is null || regionId == 0)
        {
            return;
        }

        var region = _regions[regionId];
        foreach (var (timer, remaining) in carried)
        {
            var deadline = Options.TimerMode == TimerMode.TickCounter ? region.TickCounter + remaining : remaining;
            InsertTimer(region, timer with { Deadline = deadline });
        }
    }

    // ---------------------------------------------------------------- portals

    public void SetPortalOpen(int portalId, bool open)
    {
        if (!Topology.TryPortalSlot(portalId, out var slot))
        {
            throw new KeyNotFoundException($"No portal {portalId}.");
        }

        if (_portalOpen[slot] == open)
        {
            return;
        }

        _portalOpen[slot] = open;
        var portal = Topology.PortalAt(slot);
        Topology.TryIndexOf(portal.A, out var a);
        Topology.TryIndexOf(portal.B, out var b);
        foreach (var id in _entities.Ids)
        {
            if (_entities.InTransit[id] || _entities.Class[id] == EntityClass.HeavyProjectile)
            {
                continue;
            }

            var anchor = _entities.Anchor[id];
            var radius = RadiusOf(id);
            if (_claims.BoxContains(anchor, _entities.X[id], _entities.Y[id], radius, Options.Anchoring, a)
                || _claims.BoxContains(anchor, _entities.X[id], _entities.Y[id], radius, Options.Anchoring, b))
            {
                RecomputeClaim(id);
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private int SectionAt(int layer, double x, double y)
    {
        if (!Topology.TrySectionAt(layer, x, y, out var section))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) on layer {layer} is outside the map.");
        }

        return section;
    }

    private void SetPosition(int entityId, int layer, double x, double y, double z, int section)
    {
        _entities.Layer[entityId] = layer;
        _entities.X[entityId] = x;
        _entities.Y[entityId] = y;
        _entities.Z[entityId] = z;
        _entities.Anchor[entityId] = section;
    }

    private void RequireIdle(int entityId)
    {
        if (!_entities.Exists(entityId))
        {
            throw new KeyNotFoundException($"No entity {entityId}.");
        }

        if (_entities.InTransit[entityId])
        {
            throw new InvalidOperationException($"Entity {entityId} is in transit.");
        }
    }

    private double RadiusOf(int entityId)
    {
        var cls = _entities.Class[entityId];
        return cls == EntityClass.CapturePoint && !_entities.Occupied[entityId] ? 0 : Options.ClaimRadius.For(cls);
    }

    private Region NewRegion(RegionState state, RegionCreationReason reason, long parentId, long tickCounter)
    {
        var region = new Region(_nextRegionId++, Now, tickCounter, state);
        _regions.Add(region.Id, region);
        _regionIds.Add(region.Id);
        Stats.RegionsCreated++;
        _observer.OnRegionCreated(region.Id, reason, parentId);
        return region;
    }

    private void RemoveRegion(Region region)
    {
        _regions.Remove(region.Id);
        var at = _regionIds.BinarySearch(region.Id);
        if (at >= 0)
        {
            _regionIds.RemoveAt(at);
        }

        _observer.OnRegionDied(region.Id, Now - region.CreatedAt);
    }

    private static bool IsLegal(RegionState from, RegionState to) => (from, to) switch
    {
        (RegionState.Transient, RegionState.Ready) => true,
        (RegionState.Ready, RegionState.Ticking) => true,
        (RegionState.Ready, RegionState.Transient) => true,
        (RegionState.Ticking, RegionState.Ready) => true,
        (RegionState.Ticking, RegionState.Transient) => true,
        (RegionState.Transient, RegionState.Dead) => true,
        (RegionState.Ready, RegionState.Dead) => true,
        _ => false,
    };

    /// <summary>The only way a region changes state. An illegal move is refused and recorded (invariant 4).</summary>
    private bool Transition(Region region, RegionState to)
    {
        if (region.State == to)
        {
            return true;
        }

        if (!IsLegal(region.State, to))
        {
            _breaches.Add(new Violation(InvariantRule.LegalState, $"region {region.Id}: illegal move {region.State} -> {to} refused"));
            return false;
        }

        var from = region.State;
        region.State = to;
        _observer.OnStateChanged(region.Id, from, to);
        return true;
    }

    private void EnterSerial()
    {
        if (_serialDepth++ == 0)
        {
            _serialStart = Stopwatch.GetTimestamp();
        }
    }

    private void ExitSerial()
    {
        if (--_serialDepth == 0)
        {
            Stats.SerializedTimestampTicks += Stopwatch.GetTimestamp() - _serialStart;
        }
    }

    internal void DrainBreaches(List<Violation> into)
    {
        into.AddRange(_breaches);
        _breaches.Clear();
    }

    internal EntityTable Entities => _entities;

    internal bool PortalOpenAtSlot(int slot) => _portalOpen[slot];

    internal IReadOnlyDictionary<long, Region> Regions => _regions;

    internal double ClaimRadiusOf(int entityId) => RadiusOf(entityId);
}
