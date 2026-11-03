namespace MmoEngine.Regionizer;

/// <summary>
/// Checks the regionizer's invariants at a tick boundary (no region ticking). It recomputes every
/// claim with its own straightforward walk, independent of the library's fast path, and compares the
/// result with the library's state. Allocates; for tests and the simulator's monitors, not the server
/// tick path.
/// </summary>
public static class InvariantChecker
{
    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    /// <summary>Appends every violation found to <paramref name="into"/> and returns how many were found.</summary>
    public static int Check(Regionizer regionizer, List<Violation> into)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        ArgumentNullException.ThrowIfNull(into);
        var before = into.Count;
        regionizer.DrainBreaches(into);

        var topology = regionizer.Topology;
        var regions = regionizer.Regions;

        // Rule 4: at a boundary every live region is ready, with no merge left pending.
        foreach (var id in regionizer.RegionIds)
        {
            if (!regions.TryGetValue(id, out var region))
            {
                into.Add(new Violation(InvariantRule.LegalState, $"region {id} is listed but not stored"));
                continue;
            }

            if (region.State != RegionState.Ready)
            {
                into.Add(new Violation(InvariantRule.LegalState, $"region {id} is {region.State} at a tick boundary"));
            }

            if (region.PendingPartners.Count > 0)
            {
                into.Add(new Violation(InvariantRule.LegalState, $"region {id} still has {region.PendingPartners.Count} pending merges at a tick boundary"));
            }
        }

        // Rule 1: every owned section is listed by exactly its owner, and every live section is owned.
        var listedBy = new Dictionary<int, long>();
        foreach (var id in regionizer.RegionIds)
        {
            if (!regions.TryGetValue(id, out var region))
            {
                continue;
            }

            foreach (var section in region.Sections)
            {
                if (!listedBy.TryAdd(section, id))
                {
                    into.Add(new Violation(InvariantRule.SectionSingleOwner, $"section {topology.KeyOf(section)} belongs to regions {listedBy[section]} and {id}"));
                }
                else if (regionizer.OwnerOf(section) != id)
                {
                    into.Add(new Violation(InvariantRule.SectionSingleOwner, $"section {topology.KeyOf(section)} is listed by region {id} but owned by {regionizer.OwnerOf(section)}"));
                }
            }
        }

        for (var s = 0; s < topology.SectionCount; s++)
        {
            var owner = regionizer.OwnerOf(s);
            if (owner != 0 && !listedBy.ContainsKey(s))
            {
                into.Add(new Violation(InvariantRule.SectionSingleOwner, $"section {topology.KeyOf(s)} is owned by region {owner} but not listed by it"));
            }

            if (owner == 0 && regionizer.ClaimCountOf(s) > 0)
            {
                into.Add(new Violation(InvariantRule.SectionSingleOwner, $"live section {topology.KeyOf(s)} has no owner"));
            }
        }

        // Rules 2, 5 and 6: recompute claims and check membership.
        var entities = regionizer.Entities;
        var expectedCounts = new Dictionary<int, int>();
        var listedEntity = new Dictionary<int, long>();
        foreach (var id in regionizer.RegionIds)
        {
            if (!regions.TryGetValue(id, out var region))
            {
                continue;
            }

            foreach (var entity in region.Entities)
            {
                if (!listedEntity.TryAdd(entity, id))
                {
                    into.Add(new Violation(InvariantRule.EntitySingleOwner, $"entity {entity} is listed by regions {listedEntity[entity]} and {id}"));
                }
            }
        }

        foreach (var id in regionizer.EntityIds)
        {
            if (entities.InTransit[id])
            {
                if (listedEntity.ContainsKey(id))
                {
                    into.Add(new Violation(InvariantRule.EntitySingleOwner, $"entity {id} is in transit but listed by region {listedEntity[id]}"));
                }

                continue;
            }

            var regionId = entities.Region[id];
            var isProjectile = entities.Class[id] == EntityClass.HeavyProjectile;
            if (regionId == 0 && !isProjectile)
            {
                into.Add(new Violation(InvariantRule.EntitySingleOwner, $"entity {id} has no owner"));
                continue;
            }

            if (regionId != 0 && (!listedEntity.TryGetValue(id, out var listed) || listed != regionId))
            {
                into.Add(new Violation(InvariantRule.EntitySingleOwner, $"entity {id} names region {regionId} but is not listed by it"));
            }

            if (isProjectile)
            {
                if (entities.ClaimCount[id] != 0)
                {
                    into.Add(new Violation(InvariantRule.ClaimsInRegion, $"heavy projectile {id} claims sections"));
                }

                continue;
            }

            var anchor = entities.Anchor[id];
            if (regionizer.OwnerOf(anchor) != regionId)
            {
                into.Add(new Violation(InvariantRule.EntitySingleOwner, $"entity {id}'s section {topology.KeyOf(anchor)} is owned by {regionizer.OwnerOf(anchor)}, not its region {regionId}"));
            }

            var expected = IndependentClaim(regionizer, id);
            var stored = new HashSet<int>(regionizer.GetEntity(id).ClaimedSections.ToArray());
            foreach (var section in stored)
            {
                if (!expected.Contains(section))
                {
                    var crossing = topology.KeyOf(section).Layer != topology.KeyOf(anchor).Layer || topology.AreaOf(section) != topology.AreaOf(anchor);
                    into.Add(new Violation(
                        crossing ? InvariantRule.PortalCrossing : InvariantRule.ClaimsInRegion,
                        $"entity {id} claims {topology.KeyOf(section)}, outside its claim{(crossing ? " across an area boundary or closed portal" : string.Empty)}"));
                }
            }

            foreach (var section in expected)
            {
                if (!stored.Contains(section))
                {
                    into.Add(new Violation(InvariantRule.ClaimsInRegion, $"entity {id} does not claim {topology.KeyOf(section)}, which is inside its claim"));
                }

                expectedCounts[section] = expectedCounts.GetValueOrDefault(section) + 1;
                var owner = regionizer.OwnerOf(section);
                if (owner != regionId)
                {
                    into.Add(new Violation(InvariantRule.ClaimsInRegion, $"entity {id} of region {regionId} claims {topology.KeyOf(section)}, owned by region {owner}"));
                }
            }
        }

        for (var s = 0; s < topology.SectionCount; s++)
        {
            var count = regionizer.ClaimCountOf(s);
            var expected = expectedCounts.GetValueOrDefault(s);
            if (count != expected)
            {
                into.Add(new Violation(InvariantRule.ClaimsInRegion, $"section {topology.KeyOf(s)} has claim count {count}, expected {expected}"));
            }
        }

        foreach (var id in regionizer.RegionIds)
        {
            if (regions.TryGetValue(id, out var region))
            {
                var dead = 0;
                foreach (var section in region.Sections)
                {
                    if (regionizer.ClaimCountOf(section) == 0)
                    {
                        dead++;
                    }
                }

                if (dead != region.DeadCount)
                {
                    into.Add(new Violation(InvariantRule.SectionSingleOwner, $"region {id} counts {region.DeadCount} dead sections, actually {dead}"));
                }
            }
        }

        return into.Count - before;
    }

    /// <summary>Research D1 and D2, written out plainly: a walk over same-area neighbours and open portals.</summary>
    private static HashSet<int> IndependentClaim(Regionizer regionizer, int entityId)
    {
        var topology = regionizer.Topology;
        var entities = regionizer.Entities;
        var s = topology.SectionSize;
        var anchor = entities.Anchor[entityId];
        var own = topology.KeyOf(anchor);
        var r = regionizer.ClaimRadiusOf(entityId);
        var px = entities.X[entityId];
        var py = entities.Y[entityId];
        var sectionMode = regionizer.Options.Anchoring == ClaimAnchoring.Section;

        bool Within(SectionKey k)
        {
            double dx, dy;
            if (sectionMode)
            {
                dx = Math.Max(0, Math.Abs(k.X - own.X) - 1) * (double)s;
                dy = Math.Max(0, Math.Abs(k.Y - own.Y) - 1) * (double)s;
            }
            else
            {
                double left = k.X * (double)s, bottom = k.Y * (double)s;
                dx = Math.Max(Math.Max(left - px, 0), px - (left + s));
                dy = Math.Max(Math.Max(bottom - py, 0), py - (bottom + s));
            }

            return (dx * dx) + (dy * dy) <= r * r;
        }

        var claim = new HashSet<int> { anchor };
        var queue = new Queue<int>();
        queue.Enqueue(anchor);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var key = topology.KeyOf(current);
            var area = topology.AreaOf(current);
            foreach (var (dx, dy) in Neighbours)
            {
                var next = new SectionKey(key.Layer, key.X + dx, key.Y + dy);
                if (topology.TryIndexOf(next, out var n) && topology.AreaOf(n) == area && Within(next) && claim.Add(n))
                {
                    queue.Enqueue(n);
                }
            }

            foreach (var link in topology.LinksOf(current))
            {
                if (regionizer.PortalOpenAtSlot(link.PortalSlot) && Within(topology.KeyOf(link.Other)) && claim.Add(link.Other))
                {
                    queue.Enqueue(link.Other);
                }
            }
        }

        return claim;
    }
}
