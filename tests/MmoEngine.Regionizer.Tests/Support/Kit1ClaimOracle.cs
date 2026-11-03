using Xunit;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>
/// A brute-force recomputation of research D1 (geometry) and D2 (reachability), written without the
/// library's fast path or walk: it measures every section of the topology against the anchor, then
/// floods out from the entity's own section over same-layer, same-area eight-neighbours and open
/// portals, keeping only sections inside the geometry.
/// </summary>
internal static class Kit1ClaimOracle
{
    /// <summary>Whether section <paramref name="key"/> lies within <paramref name="radius"/> of the anchor (D1).</summary>
    public static bool WithinGeometry(SectionKey key, SectionKey own, double x, double y, double radius, ClaimAnchoring anchoring, int sectionSize)
    {
        double s = sectionSize;
        double dx;
        double dy;
        if (anchoring == ClaimAnchoring.Section)
        {
            // Gap between the anchor's section square and this one, per axis.
            dx = Gap(own.X * s, (own.X + 1) * s, key.X * s, (key.X + 1) * s);
            dy = Gap(own.Y * s, (own.Y + 1) * s, key.Y * s, (key.Y + 1) * s);
        }
        else
        {
            // Distance from the point to the nearest point of the square.
            dx = x - Math.Clamp(x, key.X * s, (key.X + 1) * s);
            dy = y - Math.Clamp(y, key.Y * s, (key.Y + 1) * s);
        }

        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    /// <summary>Every section of the topology inside the D1 geometry of the anchor, on any layer.</summary>
    public static HashSet<int> Geometry(SectionTopology topology, int ownSection, double x, double y, double radius, ClaimAnchoring anchoring)
    {
        var own = topology.KeyOf(ownSection);
        var inside = new HashSet<int>();
        for (var i = 0; i < topology.SectionCount; i++)
        {
            if (WithinGeometry(topology.KeyOf(i), own, x, y, radius, anchoring, topology.SectionSize))
            {
                inside.Add(i);
            }
        }

        return inside;
    }

    /// <summary>Geometry restricted to the anchor's own layer: the claim D1 gives on an open map with no areas or portals.</summary>
    public static int[] OpenWorldClaim(SectionTopology topology, int ownSection, double x, double y, double radius, ClaimAnchoring anchoring)
    {
        var layer = topology.KeyOf(ownSection).Layer;
        return [.. Geometry(topology, ownSection, x, y, radius, anchoring).Where(i => topology.KeyOf(i).Layer == layer).Order()];
    }

    /// <summary>The D1 and D2 claim of an entity at (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="layer"/>.</summary>
    public static int[] Claim(
        SectionTopology topology,
        IReadOnlyList<PortalSpec> portals,
        Func<int, bool> portalOpen,
        int layer,
        double x,
        double y,
        double radius,
        ClaimAnchoring anchoring)
    {
        Assert.True(topology.TrySectionAt(layer, x, y, out var own), $"({x}, {y}) on layer {layer} is outside the topology");
        var inside = Geometry(topology, own, x, y, radius, anchoring);
        var reached = new HashSet<int> { own };
        var frontier = new Stack<int>();
        frontier.Push(own);
        while (frontier.Count > 0)
        {
            var current = frontier.Pop();
            var key = topology.KeyOf(current);
            var area = topology.AreaOf(current);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0 || dy != 0)
                        && topology.TryIndexOf(key with { X = key.X + dx, Y = key.Y + dy }, out var next)
                        && topology.AreaOf(next) == area
                        && inside.Contains(next)
                        && reached.Add(next))
                    {
                        frontier.Push(next);
                    }
                }
            }

            foreach (var portal in portals)
            {
                if (!portalOpen(portal.PortalId))
                {
                    continue;
                }

                var other = portal.A == key ? portal.B : portal.B == key ? portal.A : (SectionKey?)null;
                if (other is { } far
                    && topology.TryIndexOf(far, out var through)
                    && inside.Contains(through)
                    && reached.Add(through))
                {
                    frontier.Push(through);
                }
            }
        }

        return [.. reached.Order()];
    }

    private static double Gap(double aMin, double aMax, double bMin, double bMax) =>
        Math.Max(0, Math.Max(bMin - aMax, aMin - bMax));
}
