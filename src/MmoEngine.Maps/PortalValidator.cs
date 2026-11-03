using MmoEngine.Regionizer;

namespace MmoEngine.Maps;

/// <summary>
/// Checks a map's portal graph (contracts/map-format.md, research D11). Claims stop at closed
/// portals only if the map declares every opening, so this reports:
/// <list type="bullet">
/// <item><see cref="PortalFindingCode.Leak"/>: an adjacency or opening across an area or layer boundary that matches no portal;</item>
/// <item><see cref="PortalFindingCode.DeadPortal"/>: a portal endpoint outside its layer or blocked;</item>
/// <item><see cref="PortalFindingCode.MissingPolicy"/>: a portal whose closed-state policy is absent or incomplete;</item>
/// <item><see cref="PortalFindingCode.SealingFailure"/>: a sealed area that still reaches another area with every portal closed.</item>
/// </list>
/// Findings come in that order of codes; within a code, in file order (adjacency before openings).
/// The portal schedule is ignored.
/// </summary>
public static class PortalValidator
{
    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    public static IReadOnlyList<PortalFinding> Validate(MapDocument map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var findings = new List<PortalFinding>();
        var portalEnds = new HashSet<(SectionKey, SectionKey)>();
        foreach (var portal in map.Portals)
        {
            portalEnds.Add((portal.A, portal.B));
            portalEnds.Add((portal.B, portal.A));
        }

        // An entry whose endpoints equal a portal's (in either order) is that portal. The others are
        // walkable whatever the portals do, and are leaks when they cross a boundary.
        var walkable = new Dictionary<SectionKey, List<MapConnection>>();
        foreach (var entry in map.Adjacency.Concat(map.Openings))
        {
            if (portalEnds.Contains((entry.A, entry.B)))
            {
                continue;
            }

            AddEdge(walkable, entry.A, entry);
            AddEdge(walkable, entry.B, entry);
            var crossesLayer = entry.A.Layer != entry.B.Layer;
            var areaA = map.AreaIdAt(entry.A);
            var areaB = map.AreaIdAt(entry.B);
            if (crossesLayer || areaA != areaB)
            {
                var boundary = !crossesLayer ? "an area boundary"
                    : areaA == areaB ? "a layer boundary"
                    : "layer and area boundaries";
                var kind = entry.Kind ?? (entry.Type == MapConnectionType.Adjacency ? "adjacency" : "opening");
                findings.Add(new PortalFinding(
                    PortalFindingCode.Leak,
                    entry.Location,
                    $"{kind} from {Describe(map, entry.A)} to {Describe(map, entry.B)} crosses {boundary} and matches no portal"));
            }
        }

        foreach (var portal in map.Portals)
        {
            var problems = new[] { EndpointProblem(map, "a", portal.A), EndpointProblem(map, "b", portal.B) }
                .Where(p => p is not null)
                .ToArray();
            if (problems.Length > 0)
            {
                findings.Add(new PortalFinding(PortalFindingCode.DeadPortal, portal.Location, string.Join("; ", problems)));
            }
        }

        foreach (var portal in map.Portals)
        {
            var message = PolicyProblem(portal.ClosedPolicy);
            if (message is not null)
            {
                findings.Add(new PortalFinding(PortalFindingCode.MissingPolicy, portal.Location, message));
            }
        }

        foreach (var area in map.SealedAreas)
        {
            var failure = WalkSealedArea(map, area, walkable);
            if (failure is not null)
            {
                findings.Add(failure);
            }
        }

        return findings;
    }

    private static void AddEdge(Dictionary<SectionKey, List<MapConnection>> edges, SectionKey from, MapConnection entry)
    {
        if (!edges.TryGetValue(from, out var list))
        {
            list = [];
            edges.Add(from, list);
        }

        list.Add(entry);
    }

    private static string Describe(MapDocument map, SectionKey key) =>
        $"{MapReader.Format(key)} ({map.AreaNames[map.AreaIdAt(key)]})";

    private static string? EndpointProblem(MapDocument map, string end, SectionKey key)
    {
        if (!map.TryGetLayer(key.Layer, out var layer))
        {
            return $"endpoint {end} {MapReader.Format(key)} is on layer {key.Layer}, which does not exist";
        }

        if (!layer.Contains(key.X, key.Y))
        {
            return $"endpoint {end} {MapReader.Format(key)} is outside layer {key.Layer}";
        }

        return layer.IsBlocked(key.X, key.Y) ? $"endpoint {end} {MapReader.Format(key)} is blocked" : null;
    }

    private static string? PolicyProblem(ClosedPolicy? policy)
    {
        if (policy is null)
        {
            return "has no closedPolicy";
        }

        var missing = new List<string>(3);
        if (policy.EntityTransfers is null)
        {
            missing.Add("entityTransfers");
        }

        if (policy.DoorAndObjectiveEvents is null)
        {
            missing.Add("doorAndObjectiveEvents");
        }

        if (policy.DeferredProjectiles is null)
        {
            missing.Add("deferredProjectiles");
        }

        return missing.Count == 0 ? null : $"closedPolicy lacks {string.Join(", ", missing)}";
    }

    /// <summary>
    /// With every portal closed, walks breadth first from a sealed area over implicit adjacency
    /// (eight neighbours, same layer and area, neither blocked) and over the adjacency and openings
    /// that are not portals. Each part of the area not yet reached starts a walk, taking layers in
    /// file order and sections row by row, so the first foreign section reached is deterministic.
    /// </summary>
    private static PortalFinding? WalkSealedArea(
        MapDocument map,
        string area,
        Dictionary<SectionKey, List<MapConnection>> walkable)
    {
        var areaId = map.AreaIdOf(area);
        var visited = new HashSet<SectionKey>();
        var queue = new Queue<SectionKey>();
        foreach (var layer in map.Layers)
        {
            for (var y = layer.OriginY; y < layer.OriginY + layer.Height; y++)
            {
                for (var x = layer.OriginX; x < layer.OriginX + layer.Width; x++)
                {
                    var start = new SectionKey(layer.Id, x, y);
                    if (layer.AreaIdAt(x, y) != areaId || !visited.Add(start))
                    {
                        continue;
                    }

                    queue.Enqueue(start);
                    var failure = Walk(map, area, areaId, walkable, visited, queue);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }
        }

        return null;
    }

    private static PortalFinding? Walk(
        MapDocument map,
        string area,
        ushort areaId,
        Dictionary<SectionKey, List<MapConnection>> walkable,
        HashSet<SectionKey> visited,
        Queue<SectionKey> queue)
    {
        while (queue.TryDequeue(out var key))
        {
            map.TryGetLayer(key.Layer, out var layer);
            if (!layer!.IsBlocked(key.X, key.Y))
            {
                foreach (var (dx, dy) in Neighbours)
                {
                    var next = new SectionKey(key.Layer, key.X + dx, key.Y + dy);
                    if (layer.Contains(next.X, next.Y) && !layer.IsBlocked(next.X, next.Y)
                        && layer.AreaIdAt(next.X, next.Y) == areaId && visited.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            if (!walkable.TryGetValue(key, out var entries))
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var other = entry.A == key ? entry.B : entry.A;
                if (map.AreaIdAt(other) != areaId)
                {
                    return new PortalFinding(
                        PortalFindingCode.SealingFailure,
                        $"sealedAreas:{area}",
                        $"with all portals closed, sealed area {area} reaches {Describe(map, other)} through {entry.Location}");
                }

                if (visited.Add(other))
                {
                    queue.Enqueue(other);
                }
            }
        }

        return null;
    }
}
