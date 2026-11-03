namespace MmoEngine.Regionizer;

/// <summary>Claims, section ownership and merges (research D1–D3).</summary>
public sealed partial class Regionizer
{
    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    /// <summary>Puts a placed, unowned entity into the region owning its section (or a new one) and claims.</summary>
    private void Place(int entityId)
    {
        var section = _entities.Anchor[entityId];
        var regionId = _owner[section];
        if (_entities.Class[entityId] == EntityClass.HeavyProjectile)
        {
            // Callers checked the section is owned: the firing region, or the destination's region.
            _regions[regionId].InsertEntity(entityId);
            _entities.Region[entityId] = regionId;
            _observer.OnEntityOwnerChanged(entityId, 0, regionId);
            return;
        }
        if (regionId == 0)
        {
            EnterSerial();
            regionId = NewRegion(RegionState.Ready, RegionCreationReason.NewClaim, 0, 0).Id;
            ExitSerial();
        }

        _regions[regionId].InsertEntity(entityId);
        _entities.Region[entityId] = regionId;
        _observer.OnEntityOwnerChanged(entityId, 0, regionId);

        var count = _claims.Compute(section, _entities.X[entityId], _entities.Y[entityId], RadiusOf(entityId), Options.Anchoring, ref _claimScratch);
        CopyClaim(entityId, count);
        var claim = _entities.Claims[entityId];
        for (var i = 0; i < count; i++)
        {
            IncrementClaim(entityId, claim[i]);
        }
    }

    /// <summary>Recomputes an entity's claim and applies the difference to claim counts and ownership.</summary>
    private void RecomputeClaim(int entityId)
    {
        var count = _claims.Compute(_entities.Anchor[entityId], _entities.X[entityId], _entities.Y[entityId], RadiusOf(entityId), Options.Anchoring, ref _claimScratch);
        var old = _entities.Claims[entityId];
        var oldCount = _entities.ClaimCount[entityId];
        if (oldCount == count && old.AsSpan(0, count).SequenceEqual(_claimScratch.AsSpan(0, count)))
        {
            return;
        }

        // Removals first, so a section that only moves within this claim never looks dead.
        int i = 0, j = 0;
        while (i < oldCount)
        {
            if (j < count && _claimScratch[j] < old[i])
            {
                j++;
            }
            else if (j < count && _claimScratch[j] == old[i])
            {
                i++;
                j++;
            }
            else
            {
                DecrementClaim(old[i]);
                i++;
            }
        }

        // Additions: the new sections not in the old claim. Copy first, because a merge can follow.
        var added = 0;
        i = 0;
        j = 0;
        while (j < count)
        {
            if (i < oldCount && old[i] < _claimScratch[j])
            {
                i++;
            }
            else if (i < oldCount && old[i] == _claimScratch[j])
            {
                i++;
                j++;
            }
            else
            {
                added++;
                j++;
            }
        }

        if (added == 0)
        {
            CopyClaim(entityId, count);
            return;
        }

        // Stash the additions at the end of the scratch buffer, then commit the new claim.
        EnsureScratch(count + added);
        var write = count;
        i = 0;
        j = 0;
        while (j < count)
        {
            if (i < oldCount && old[i] < _claimScratch[j])
            {
                i++;
            }
            else if (i < oldCount && old[i] == _claimScratch[j])
            {
                i++;
                j++;
            }
            else
            {
                _claimScratch[write++] = _claimScratch[j];
                j++;
            }
        }

        CopyClaim(entityId, count);
        for (var k = count; k < write; k++)
        {
            IncrementClaim(entityId, _claimScratch[k]);
        }
    }

    private void EnsureScratch(int size)
    {
        if (_claimScratch.Length < size)
        {
            Array.Resize(ref _claimScratch, Math.Max(size, _claimScratch.Length * 2));
        }
    }

    private void CopyClaim(int entityId, int count)
    {
        ref var buffer = ref _entities.Claims[entityId];
        if (buffer.Length < count)
        {
            Array.Resize(ref buffer, Math.Max(count, buffer.Length * 2));
        }

        _claimScratch.AsSpan(0, count).CopyTo(buffer);
        _entities.ClaimCount[entityId] = count;
    }

    private void ReleaseClaims(int entityId)
    {
        var claim = _entities.Claims[entityId];
        for (var i = 0; i < _entities.ClaimCount[entityId]; i++)
        {
            DecrementClaim(claim[i]);
        }

        _entities.ClaimCount[entityId] = 0;
    }

    private void DecrementClaim(int section)
    {
        TotalClaimCount--;
        if (--_claimCount[section] == 0 && _owner[section] != 0)
        {
            _regions[_owner[section]].DeadCount++;
        }
    }

    /// <summary>A claim lands on a section: give it an owner, or ask for the merges it forces (D3).</summary>
    private void IncrementClaim(int entityId, int section)
    {
        var owner = _owner[section];
        TotalClaimCount++;
        if (++_claimCount[section] == 1 && owner != 0)
        {
            _regions[owner].DeadCount--;
        }

        var regionId = _entities.Region[entityId];
        if (owner == regionId)
        {
            return;
        }

        if (owner != 0)
        {
            RequestMerge(regionId, owner, entityId);
            return;
        }

        var region = _regions[regionId];
        var target = region.State == RegionState.Ticking ? TransientHelperFor(region, entityId) : region;
        AssignSection(section, target);
        GapCheck(section, entityId);
    }

    private void AssignSection(int section, Region region)
    {
        var old = _owner[section];
        if (old == 0)
        {
            OwnedSectionCount++;
        }

        _owner[section] = region.Id;
        region.Sections.Add(section);
        region.SectionVersion++;
        if (_claimCount[section] == 0)
        {
            region.DeadCount++;
        }

        _observer.OnSectionOwnerChanged(section, old, region.Id);
    }

    /// <summary>With a merge gap of 1, owners of a newly owned section's neighbours must merge with its owner.</summary>
    private void GapCheck(int section, int entityId)
    {
        if (Options.MergeGap == 0)
        {
            return;
        }

        var key = Topology.KeyOf(section);
        var area = Topology.AreaOf(section);
        ref readonly var layer = ref Topology.LayerAt(Topology.LayerSlotOf(section));
        foreach (var (dx, dy) in Neighbours)
        {
            if (!layer.Contains(key.X + dx, key.Y + dy))
            {
                continue;
            }

            var n = layer.IndexOf(key.X + dx, key.Y + dy);
            if (Topology.AreaOf(n) == area)
            {
                MergeIfOwnedElsewhere(section, n, entityId);
            }
        }

        foreach (var link in Topology.LinksOf(section))
        {
            if (_portalOpen[link.PortalSlot])
            {
                MergeIfOwnedElsewhere(section, link.Other, entityId);
            }
        }
    }

    private void MergeIfOwnedElsewhere(int section, int neighbour, int entityId)
    {
        var other = _owner[neighbour];
        var mine = _owner[section];
        if (other != 0 && other != mine)
        {
            RequestMerge(mine, other, entityId);
        }
    }

    /// <summary>The transient region collecting the sections a ticking region needs this tick (invariant 3).</summary>
    private Region TransientHelperFor(Region ticking, int entityId)
    {
        if (ticking.TransientHelper != 0
            && _regions.TryGetValue(ticking.TransientHelper, out var existing)
            && existing.State == RegionState.Transient
            && existing.HasPartner(ticking.Id))
        {
            return existing;
        }

        EnterSerial();
        var helper = NewRegion(RegionState.Transient, RegionCreationReason.Transient, ticking.Id, ticking.TickCounter);
        var cls = _entities.Class[entityId];
        helper.AddPartner(ticking.Id, cls, entityId);
        ticking.AddPartner(helper.Id, cls, entityId);
        ticking.TransientHelper = helper.Id;
        _observer.OnMergeRequested(ticking.Id, helper.Id, cls, entityId);
        ExitSerial();
        return helper;
    }

    /// <summary>Two regions' claims touch: merge now if neither is ticking, otherwise record a pending merge.</summary>
    private void RequestMerge(long a, long b, int entityId)
    {
        if (a == b)
        {
            return;
        }

        EnterSerial();
        var ra = _regions[a];
        var rb = _regions[b];
        var cls = _entities.Class[entityId];
        if (ra.HasPartner(b))
        {
            ExitSerial();
            return;
        }

        _observer.OnMergeRequested(a, b, cls, entityId);
        if (ra.State != RegionState.Ticking && rb.State != RegionState.Ticking)
        {
            ExecuteMerge(a, b, cls, entityId);
        }
        else
        {
            ra.AddPartner(b, cls, entityId);
            rb.AddPartner(a, cls, entityId);
            RefreshState(ra);
            RefreshState(rb);
        }

        ExitSerial();
    }

    /// <summary>Merges two non-ticking regions; the lower id absorbs the higher. Returns the survivor's id.</summary>
    private long ExecuteMerge(long x, long y, EntityClass cls, int entityId)
    {
        EnterSerial();
        var absorber = _regions[Math.Min(x, y)];
        var absorbed = _regions[Math.Max(x, y)];
        _observer.OnMerged(absorber.Id, absorbed.Id, cls, entityId);

        foreach (var section in absorbed.Sections)
        {
            _owner[section] = absorber.Id;
            absorber.Sections.Add(section);
            _observer.OnSectionOwnerChanged(section, absorbed.Id, absorber.Id);
        }

        absorber.DeadCount += absorbed.DeadCount;
        absorber.SectionVersion++;

        MergeSorted(absorber.Entities, absorbed.Entities);
        foreach (var entity in absorbed.Entities)
        {
            _entities.Region[entity] = absorber.Id;
            _observer.OnEntityOwnerChanged(entity, absorbed.Id, absorber.Id);
        }

        MergeTimers(absorber, absorbed);

        for (var i = 0; i < absorbed.PendingPartners.Count; i++)
        {
            var partnerId = absorbed.PendingPartners[i];
            if (partnerId == absorber.Id)
            {
                continue;
            }

            var (causeClass, causeEntity) = absorbed.PendingCauses[i];
            var partner = _regions[partnerId];
            partner.RemovePartner(absorbed.Id);
            partner.AddPartner(absorber.Id, causeClass, causeEntity);
            absorber.AddPartner(partnerId, causeClass, causeEntity);
            if (partner.TransientHelper == absorbed.Id)
            {
                partner.TransientHelper = absorber.Id;
            }
        }

        absorber.RemovePartner(absorbed.Id);
        foreach (var extension in _extensions)
        {
            extension.OnMerge(absorber.Id, absorbed.Id);
        }

        Transition(absorbed, RegionState.Dead);
        RemoveRegion(absorbed);
        Stats.Merges++;
        RefreshState(absorber);
        ExitSerial();
        return absorber.Id;
    }

    /// <summary>A non-ticking region with pending merges waits as transient; without them it is ready.</summary>
    private void RefreshState(Region region)
    {
        if (region.State is RegionState.Ticking or RegionState.Dead)
        {
            return;
        }

        Transition(region, region.PendingPartners.Count > 0 ? RegionState.Transient : RegionState.Ready);
    }

    private void MergeSorted(List<int> into, List<int> from)
    {
        _mergeScratch.Clear();
        int i = 0, j = 0;
        while (i < into.Count || j < from.Count)
        {
            if (j >= from.Count || (i < into.Count && into[i] < from[j]))
            {
                _mergeScratch.Add(into[i++]);
            }
            else
            {
                _mergeScratch.Add(from[j++]);
            }
        }

        into.Clear();
        into.AddRange(_mergeScratch);
    }

    private void MergeTimers(Region absorber, Region absorbed)
    {
        if (absorbed.Timers.Count == 0)
        {
            return;
        }

        // Tick-counter mode: shift the absorbed deadlines so the remaining ticks survive (D6).
        var offset = Options.TimerMode == TimerMode.TickCounter ? absorber.TickCounter - absorbed.TickCounter : 0;
        _timerScratch.Clear();
        int i = 0, j = 0;
        while (i < absorber.Timers.Count || j < absorbed.Timers.Count)
        {
            if (j >= absorbed.Timers.Count || (i < absorber.Timers.Count && absorber.Timers[i].TimerId < absorbed.Timers[j].TimerId))
            {
                _timerScratch.Add(absorber.Timers[i++]);
            }
            else
            {
                var t = absorbed.Timers[j++];
                _timerScratch.Add(t with { Deadline = t.Deadline + offset });
            }
        }

        absorber.Timers.Clear();
        absorber.Timers.AddRange(_timerScratch);
    }

    private static void InsertTimer(Region region, TimerView timer)
    {
        var at = region.Timers.Count;
        while (at > 0 && region.Timers[at - 1].TimerId > timer.TimerId)
        {
            at--;
        }

        region.Timers.Insert(at, timer);
    }
}
