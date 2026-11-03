namespace MmoEngine.Regionizer;

/// <summary>
/// Receives every change the regionizer makes, in the order it makes them. Arguments are values or
/// spans, so observing allocates nothing. Implementations must not call back into the regionizer.
/// </summary>
public interface IRegionizerObserver
{
    void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId);

    void OnStateChanged(long regionId, RegionState from, RegionState to);

    /// <summary>A claim required regions <paramref name="a"/> and <paramref name="b"/> to merge.</summary>
    void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId);

    void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId);

    /// <summary>A split; <paramref name="childIds"/> includes the parent's id, which the largest child keeps.</summary>
    void OnSplit(long parentId, ReadOnlySpan<long> childIds);

    void OnRegionDied(long regionId, long ageSteps);

    void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner);

    void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner);

    void OnRegionTickStarted(long regionId, long tickCounter, long now);

    void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick);
}

/// <summary>
/// A kind of region data owned outside the library. The constitution requires every new kind of
/// region data to define how it merges and splits.
/// </summary>
public interface IRegionDataExtension
{
    void OnMerge(long absorberId, long absorbedId);

    void OnSplit(long parentId, ReadOnlySpan<long> childIds, Func<int, long> ownerOfEntity);
}

/// <summary>Cumulative counters. Callers take deltas per step.</summary>
public sealed class RegionizerStats
{
    /// <summary>Stopwatch ticks spent in work that touches more than one region (research D9).</summary>
    public long SerializedTimestampTicks { get; internal set; }

    public long Merges { get; internal set; }

    public long Splits { get; internal set; }

    public long RegionsCreated { get; internal set; }
}

/// <summary>A read-only view of one region. Valid until the next regionizer call.</summary>
public readonly ref struct RegionView
{
    private readonly Region _region;

    internal RegionView(Region region) => _region = region;

    public long Id => _region.Id;

    public RegionState State => _region.State;

    public long TickCounter => _region.TickCounter;

    public long CreatedAt => _region.CreatedAt;

    public int SectionCount => _region.Sections.Count;

    public int DeadSectionCount => _region.DeadCount;

    public ReadOnlySpan<int> Sections => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_region.Sections);

    public ReadOnlySpan<int> Entities => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_region.Entities);

    public ReadOnlySpan<TimerView> Timers => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_region.Timers);

    public ReadOnlySpan<long> PendingMergePartners => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_region.PendingPartners);
}

/// <summary>A read-only view of one entity. Valid until the next regionizer call.</summary>
public readonly ref struct EntityView
{
    private readonly EntityTable _table;
    private readonly int _id;

    internal EntityView(EntityTable table, int id)
    {
        _table = table;
        _id = id;
    }

    public int Id => _id;

    public EntityClass Class => _table.Class[_id];

    public int Layer => _table.Layer[_id];

    public double X => _table.X[_id];

    public double Y => _table.Y[_id];

    public double Z => _table.Z[_id];

    public int FightTag => _table.FightTag[_id];

    public long RegionId => _table.Region[_id];

    public bool InTransit => _table.InTransit[_id];

    public ReadOnlySpan<int> ClaimedSections => _table.Claims[_id].AsSpan(0, _table.ClaimCount[_id]);
}

internal sealed class NullObserver : IRegionizerObserver
{
    public static readonly NullObserver Instance = new();

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
    {
    }

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
    }

    public void OnRegionDied(long regionId, long ageSteps)
    {
    }

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
    }

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
    }
}
