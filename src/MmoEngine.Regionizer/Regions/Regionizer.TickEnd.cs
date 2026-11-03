namespace MmoEngine.Regionizer;

/// <summary>Tick start and end, split damping and islands (research D4, D5).</summary>
public sealed partial class Regionizer
{
    private readonly int[] _localStamp;
    private readonly int[] _localIndex;
    private int _stamp;
    private int[] _parent = new int[256];

    /// <summary>Moves a ready region to ticking and increments its tick counter. False if it is not ready.</summary>
    public bool TryMarkTicking(long regionId)
    {
        if (!_regions.TryGetValue(regionId, out var region) || region.State != RegionState.Ready)
        {
            return false;
        }

        Transition(region, RegionState.Ticking);
        _tickingCount++;
        region.TickCounter++;
        region.VersionAtTickStart = region.SectionVersion;
        region.TransientHelper = 0;
        _observer.OnRegionTickStarted(region.Id, region.TickCounter, Now);
        return true;
    }

    /// <summary>
    /// Ends a region's tick in Folia's order: process pending merges; become transient if a merge is
    /// still owed to a ticking region; otherwise, if the split damping thresholds hold, drop dead
    /// sections and split into islands (FR-007).
    /// </summary>
    public TickEndResult MarkNotTicking(long regionId)
    {
        var result = EndTick(regionId);
        ApplyDeferredPortals();
        return result;
    }

    private TickEndResult EndTick(long regionId)
    {
        if (!_regions.TryGetValue(regionId, out var region) || region.State != RegionState.Ticking)
        {
            throw new InvalidOperationException($"Region {regionId} is not ticking.");
        }

        if (region.SectionVersion != region.VersionAtTickStart)
        {
            _breaches.Add(new Violation(InvariantRule.TickingRegionFrozen, $"region {regionId}: section set changed during its tick"));
        }

        region.TransientHelper = 0;
        _tickingCount--;
        Transition(region, RegionState.Ready);

        var survivor = regionId;
        while (true)
        {
            var current = _regions[survivor];
            var partner = 0L;
            foreach (var p in current.PendingPartners)
            {
                if (_regions[p].State != RegionState.Ticking)
                {
                    partner = p;
                    break;
                }
            }

            if (partner == 0)
            {
                break;
            }

            var (cls, entity) = current.CauseFor(partner);
            survivor = ExecuteMerge(survivor, partner, cls, entity);
        }

        var final = _regions[survivor];
        RefreshState(final);
        if (survivor != regionId)
        {
            return new TickEndResult(RegionState.Dead, survivor, 0);
        }

        if (final.State == RegionState.Transient)
        {
            return new TickEndResult(RegionState.Transient, 0, 0);
        }

        if (final.Entities.Count == 0)
        {
            DropSections(final, deadOnly: false);
            Transition(final, RegionState.Dead);
            RemoveRegion(final);
            return new TickEndResult(RegionState.Dead, 0, 0);
        }

        if (!DampingAllowsSplit(final))
        {
            return new TickEndResult(RegionState.Ready, 0, 0);
        }

        EnterSerial();
        DropSections(final, deadOnly: true);
        var children = Split(final);
        ExitSerial();
        return new TickEndResult(RegionState.Ready, 0, children);
    }

    /// <summary>Fires the ticking region's due timers, each exactly once (research D6).</summary>
    public void RunDueTimers(long regionId)
    {
        if (!_regions.TryGetValue(regionId, out var region) || region.State != RegionState.Ticking)
        {
            throw new InvalidOperationException($"Region {regionId} is not ticking.");
        }

        var timers = region.Timers;
        var keep = 0;
        for (var i = 0; i < timers.Count; i++)
        {
            var timer = timers[i];
            var due = Options.TimerMode == TimerMode.TickCounter ? timer.Deadline <= region.TickCounter : timer.Deadline <= Now;
            if (due)
            {
                _observer.OnTimerFired(timer.TimerId, timer.EntityId, region.Id, Now, region.TickCounter);
            }
            else
            {
                timers[keep++] = timer;
            }
        }

        timers.RemoveRange(keep, timers.Count - keep);
    }

    /// <summary>Sets a timer on an entity's region. The delay is in owner ticks or base steps, by timer mode.</summary>
    public int AddTimer(int entityId, long delay)
    {
        RequireIdle(entityId);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, 1);
        var regionId = _entities.Region[entityId];
        if (regionId == 0)
        {
            throw new InvalidOperationException($"Entity {entityId} belongs to no region.");
        }

        var region = _regions[regionId];
        var setAt = Options.TimerMode == TimerMode.TickCounter ? region.TickCounter : Now;
        var timer = new TimerView(_nextTimerId++, entityId, setAt + delay, delay, setAt);
        region.Timers.Add(timer);
        return timer.TimerId;
    }

    private bool DampingAllowsSplit(Region region)
    {
        var sections = region.Sections.Count;
        return sections >= Options.SplitMinSections
            && region.DeadCount * 100.0 >= Options.SplitMinDeadPercent * sections
            && Now - region.CreatedAt >= Options.SplitMinAgeSteps;
    }

    /// <summary>Drops a region's dead sections (or all of them), which stop being owned.</summary>
    private void DropSections(Region region, bool deadOnly)
    {
        var sections = region.Sections;
        var keep = 0;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (deadOnly && _claimCount[section] > 0)
            {
                sections[keep++] = section;
                continue;
            }

            _owner[section] = 0;
            _observer.OnSectionOwnerChanged(section, region.Id, 0);
        }

        if (keep != sections.Count)
        {
            sections.RemoveRange(keep, sections.Count - keep);
            region.SectionVersion++;
        }

        region.DeadCount = 0;
    }

    /// <summary>
    /// Splits a region into islands: sections claimed by the same entity are joined, and with a merge
    /// gap of 1 so are neighbouring sections. The largest island keeps the region's id. Returns the
    /// number of resulting regions, or 0 when there is one island.
    /// </summary>
    private int Split(Region region)
    {
        var sections = region.Sections;
        var n = sections.Count;
        if (n <= 1)
        {
            return 0;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_localStamp);
            _stamp = 1;
        }

        if (_parent.Length < n)
        {
            _parent = new int[Math.Max(n, _parent.Length * 2)];
        }

        for (var i = 0; i < n; i++)
        {
            _localStamp[sections[i]] = _stamp;
            _localIndex[sections[i]] = i;
            _parent[i] = i;
        }

        foreach (var entity in region.Entities)
        {
            var claim = _entities.Claims[entity];
            var first = -1;
            for (var k = 0; k < _entities.ClaimCount[entity]; k++)
            {
                if (_localStamp[claim[k]] != _stamp)
                {
                    continue;
                }

                var local = _localIndex[claim[k]];
                if (first < 0)
                {
                    first = local;
                }
                else
                {
                    Union(first, local);
                }
            }
        }

        if (Options.MergeGap == 1)
        {
            for (var i = 0; i < n; i++)
            {
                UnionGapNeighbours(sections[i], i);
            }
        }

        // Count islands and find each one's size and smallest section index.
        var islands = new Dictionary<int, (int Size, int MinSection)>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            islands[root] = islands.TryGetValue(root, out var info)
                ? (info.Size + 1, Math.Min(info.MinSection, sections[i]))
                : (1, sections[i]);
        }

        if (islands.Count <= 1)
        {
            return 0;
        }

        var ordered = islands.OrderByDescending(kv => kv.Value.Size).ThenBy(kv => kv.Value.MinSection).Select(kv => kv.Key).ToList();
        var keeperRoot = ordered[0];
        var regionOfRoot = new Dictionary<int, Region> { [keeperRoot] = region };
        var children = new List<Region>();
        foreach (var root in ordered.Skip(1).OrderBy(r => islands[r].MinSection))
        {
            var child = NewRegion(RegionState.Ready, RegionCreationReason.Split, region.Id, region.TickCounter);
            regionOfRoot[root] = child;
            children.Add(child);
        }

        // Sections.
        var all = sections.ToArray();
        sections.Clear();
        foreach (var section in all)
        {
            var target = regionOfRoot[Find(_localIndex[section])];
            target.Sections.Add(section);
            if (target != region)
            {
                _owner[section] = target.Id;
                _observer.OnSectionOwnerChanged(section, region.Id, target.Id);
            }
        }

        region.SectionVersion++;

        // Entities follow the island that owns their section; ascending order is preserved.
        var entities = region.Entities.ToArray();
        region.Entities.Clear();
        foreach (var entity in entities)
        {
            var anchor = _entities.Anchor[entity];
            var target = anchor >= 0 && _localStamp[anchor] == _stamp ? regionOfRoot[Find(_localIndex[anchor])] : region;
            target.Entities.Add(entity);
            if (target != region)
            {
                _entities.Region[entity] = target.Id;
                _observer.OnEntityOwnerChanged(entity, region.Id, target.Id);
            }
        }

        // Timers follow their entity.
        var timers = region.Timers.ToArray();
        region.Timers.Clear();
        foreach (var timer in timers)
        {
            _regions[_entities.Region[timer.EntityId]].Timers.Add(timer);
        }

        var childIds = new long[children.Count + 1];
        childIds[0] = region.Id;
        for (var c = 0; c < children.Count; c++)
        {
            childIds[c + 1] = children[c].Id;
        }

        foreach (var extension in _extensions)
        {
            extension.OnSplit(region.Id, childIds, e => _entities.Region[e]);
        }

        Stats.Splits++;
        _observer.OnSplit(region.Id, childIds);
        return childIds.Length;
    }

    private void UnionGapNeighbours(int section, int local)
    {
        var key = Topology.KeyOf(section);
        var area = Topology.AreaOf(section);
        ref readonly var layer = ref Topology.LayerAt(Topology.LayerSlotOf(section));
        foreach (var (dx, dy) in Neighbours)
        {
            if (!layer.Contains(key.X + dx, key.Y + dy))
            {
                continue;
            }

            var other = layer.IndexOf(key.X + dx, key.Y + dy);
            if (_localStamp[other] == _stamp && Topology.AreaOf(other) == area)
            {
                Union(local, _localIndex[other]);
            }
        }

        foreach (var link in Topology.LinksOf(section))
        {
            if (_portalOpen[link.PortalSlot] && _localStamp[link.Other] == _stamp)
            {
                Union(local, _localIndex[link.Other]);
            }
        }
    }

    private int Find(int i)
    {
        while (_parent[i] != i)
        {
            _parent[i] = _parent[_parent[i]];
            i = _parent[i];
        }

        return i;
    }

    private void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb)
        {
            _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }
}
