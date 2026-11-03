namespace MmoEngine.Regionizer;

/// <summary>A region's private data: its sections, entities, timers and merge bookkeeping.</summary>
internal sealed class Region(long id, long createdAt, long tickCounter, RegionState state)
{
    public long Id { get; } = id;

    public RegionState State { get; set; } = state;

    public long CreatedAt { get; set; } = createdAt;

    public long TickCounter { get; set; } = tickCounter;

    /// <summary>Owned section indices, unordered.</summary>
    public List<int> Sections { get; } = [];

    /// <summary>Owned sections whose claim count is zero.</summary>
    public int DeadCount { get; set; }

    /// <summary>Entity ids, ascending.</summary>
    public List<int> Entities { get; } = [];

    /// <summary>Timers, ascending by timer id.</summary>
    public List<TimerView> Timers { get; } = [];

    /// <summary>Regions this one must merge with once neither is ticking, ascending.</summary>
    public List<long> PendingPartners { get; } = [];

    /// <summary>The cause recorded for each pending partner, parallel to <see cref="PendingPartners"/>.</summary>
    public List<(EntityClass Class, int Entity)> PendingCauses { get; } = [];

    /// <summary>Bumped whenever the section set changes; checked across a tick (invariant 3).</summary>
    public int SectionVersion { get; set; }

    public int VersionAtTickStart { get; set; }

    /// <summary>The transient region collecting sections this ticking region needs, or 0.</summary>
    public long TransientHelper { get; set; }

    public bool HasPartner(long partner) => PendingPartners.BinarySearch(partner) >= 0;

    public void AddPartner(long partner, EntityClass cls, int entity)
    {
        var at = PendingPartners.BinarySearch(partner);
        if (at >= 0)
        {
            return;
        }

        at = ~at;
        PendingPartners.Insert(at, partner);
        PendingCauses.Insert(at, (cls, entity));
    }

    public (EntityClass Class, int Entity) CauseFor(long partner)
    {
        var at = PendingPartners.BinarySearch(partner);
        return at >= 0 ? PendingCauses[at] : (EntityClass.Infantry, -1);
    }

    public void RemovePartner(long partner)
    {
        var at = PendingPartners.BinarySearch(partner);
        if (at >= 0)
        {
            PendingPartners.RemoveAt(at);
            PendingCauses.RemoveAt(at);
        }
    }

    public void InsertEntity(int entityId)
    {
        var at = Entities.BinarySearch(entityId);
        if (at < 0)
        {
            Entities.Insert(~at, entityId);
        }
    }

    public void RemoveEntity(int entityId)
    {
        var at = Entities.BinarySearch(entityId);
        if (at >= 0)
        {
            Entities.RemoveAt(at);
        }
    }
}
