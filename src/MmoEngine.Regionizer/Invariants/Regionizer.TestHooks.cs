namespace MmoEngine.Regionizer;

/// <summary>
/// Deliberate corruption for tests of the invariant checker and the simulator's monitors. Internal,
/// visible only to the test assemblies.
/// </summary>
public sealed partial class Regionizer
{
    /// <summary>Lists a section under a second region without changing its owner (breaks rule 1).</summary>
    internal void TestHookListSectionInRegion(long regionId, int section) => _regions[regionId].Sections.Add(section);

    /// <summary>Overwrites a section's claim count (breaks rule 2).</summary>
    internal void TestHookSetClaimCount(int section, int count) => _claimCount[section] = count;

    /// <summary>Points an entity at another region without moving it (breaks rule 5).</summary>
    internal void TestHookSetEntityRegion(int entityId, long regionId) => _entities.Region[entityId] = regionId;

    /// <summary>Adds a section to an entity's stored claim and counts (breaks rule 6 when across a boundary).</summary>
    internal void TestHookAddClaimedSection(int entityId, int section)
    {
        ref var buffer = ref _entities.Claims[entityId];
        var count = _entities.ClaimCount[entityId];
        if (buffer.Length == count)
        {
            Array.Resize(ref buffer, count + 1);
        }

        buffer[count] = section;
        _entities.ClaimCount[entityId] = count + 1;
        buffer.AsSpan(0, count + 1).Sort();
        _claimCount[section]++;
        TotalClaimCount++;
        if (_owner[section] == 0)
        {
            AssignSection(section, _regions[_entities.Region[entityId]]);
        }
    }

    /// <summary>Gives a ticking region a new section directly (breaks rule 3).</summary>
    internal void TestHookGrowRegion(long regionId, int section)
    {
        var region = _regions[regionId];
        if (_owner[section] == 0)
        {
            OwnedSectionCount++;
        }

        _owner[section] = regionId;
        region.Sections.Add(section);
        region.SectionVersion++;
        if (_claimCount[section] == 0)
        {
            region.DeadCount++;
        }
    }

    /// <summary>
    /// Takes a section off a region's list. Its owner stays (breaks rule 1: owned but not listed), or,
    /// with <paramref name="clearOwner"/>, it loses its owner as if dropped while still claimed.
    /// </summary>
    internal void TestHookUnlistSection(long regionId, int section, bool clearOwner)
    {
        _regions[regionId].Sections.Remove(section);
        if (clearOwner && _owner[section] != 0)
        {
            _owner[section] = 0;
            OwnedSectionCount--;
        }
    }

    /// <summary>Lists an entity under a region without changing the region it names (breaks rule 5).</summary>
    internal void TestHookListEntityInRegion(long regionId, int entityId) => _regions[regionId].InsertEntity(entityId);

    /// <summary>Moves an entity on its layer without recomputing its claim (breaks rules 2 and 5).</summary>
    internal void TestHookMoveWithoutReclaim(int entityId, double x, double y)
    {
        var layer = _entities.Layer[entityId];
        SetPosition(entityId, layer, x, y, _entities.Z[entityId], SectionAt(layer, x, y));
    }

    /// <summary>Asks for a state move through the guarded transition (an illegal one is refused and recorded).</summary>
    internal bool TestHookTransition(long regionId, RegionState to) => Transition(_regions[regionId], to);

    /// <summary>Overwrites a timer's deadline (makes it fire early or late).</summary>
    internal void TestHookSetTimerDeadline(int timerId, long deadline)
    {
        foreach (var region in _regions.Values)
        {
            for (var i = 0; i < region.Timers.Count; i++)
            {
                if (region.Timers[i].TimerId == timerId)
                {
                    region.Timers[i] = region.Timers[i] with { Deadline = deadline };
                    return;
                }
            }
        }

        throw new KeyNotFoundException($"No timer {timerId}.");
    }
}
