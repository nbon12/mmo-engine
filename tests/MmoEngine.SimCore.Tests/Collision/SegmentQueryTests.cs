using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Collision;

/// <summary>
/// The segment query of data-model.md#collisionworld, the world half of FR-010:
/// <c>CollisionWorld.SegmentQuery(a, b, out SegmentHit hit)</c> visits the heightfield cells and the
/// uniform-grid cells the segment a → b crosses, tests their triangles double-sided with inclusive edges,
/// and keeps the earliest hit with ties to the lower triangle ID (collision-data-format.md#triangle-ids).
/// <c>Kind</c> is <c>HitKind.Terrain</c> for a heightfield triangle and <c>HitKind.Mesh</c> for a mesh
/// triangle; the boundary planes (IDs −4 to −1) exist only in the capsule sweep, so a segment never hits
/// them. Expected t values come from <see cref="ProjectileReference.SegmentHeightfield"/> and
/// <see cref="ProjectileReference.SegmentTriangle"/> in double; expected IDs from the reference, from the
/// mesh triangles listed here from the <see cref="CollisionData"/>, or from the heightfield's ID rule
/// 2·(j·(columns − 1) + i) + k. Every query here goes through <see cref="Query"/>, which fails on any
/// reported ID below 0.
/// </summary>
public sealed class SegmentQueryTests
{
    /// <summary>The tolerance on a hit's <c>T</c> against the double reference.</summary>
    private const double TTolerance = 1e-5;

    /// <summary>The <c>ProjectileWorlds.Flat()</c> heightfield: origin (−512, −512), cell 4 m, 257 × 257 samples, every height 0.</summary>
    private const float FlatOrigin = -512f;

    private const float FlatCellSize = 4f;

    private const int FlatSamples = 257;

    /// <summary>The flat heightfield's cell (128, 128), from (0, 0) to (4, 4) in X and Z.</summary>
    private const int CentreCell = 128;

    public static TheoryData<string> OrderRows =>
    [
        "terrain-before-mesh",
        "mesh-before-terrain",
        "two-coincident-mesh-triangles",
        "shared-heightfield-edge",
        "shared-cell-boundary",
    ];

    /// <summary>
    /// FR-010, collision-data-format.md#triangle-ids: Given the row's world and segment, When
    /// <c>SegmentQuery</c> runs, Then it returns true with <c>T</c> equal to the reference t within 1e-5 and
    /// <c>TriangleId</c> and <c>Kind</c> exactly the row's. <c>terrain-before-mesh</c>: over
    /// <c>ProjectileWorlds.Wall()</c>, a segment descending toward the wall meets the ground at t = 0.5, in
    /// the interior of a heightfield triangle, and passes the wall's plane below it: Terrain, the triangle ID
    /// <c>ProjectileReference.SegmentHeightfield</c> names. <c>mesh-before-terrain</c>: over the same world, a
    /// segment meets the wall at t = 0.5 and would meet the ground at t = 2/3: Mesh, the wall triangle the
    /// reference meets first. <c>two-coincident-mesh-triangles</c>: two mesh triangles with the same vertices
    /// in the same order, met at the same t = 0.6: the lower ID. <c>shared-heightfield-edge</c>: a slanted
    /// segment through a point of cell (128, 128)'s v00 → v11 diagonal, which both of its triangles hold:
    /// Terrain, the lower ID (k = 0). <c>shared-cell-boundary</c>: a slanted segment through a point of the
    /// edge z = 4 m between cells (128, 128) and (128, 129), held by the first's k = 1 triangle and the
    /// second's k = 0 triangle: Terrain, the lower ID (the first's k = 1).
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderRows))]
    public void EarliestTriangleWinsAndTiesGoToTheLowerId(string caseName)
    {
        var row = Case(caseName);
        var world = TestWorlds.Load(row.Data);

        var found = Query(world, row.A, row.B, out var hit, caseName);

        Assert.True(found, FormattableString.Invariant($"{caseName}: SegmentQuery from {row.A} to {row.B} found nothing; expected {row.Kind} triangle {row.TriangleId} at t {row.ReferenceT:R}"));
        Assert.True(
            System.Math.Abs(hit.T - row.ReferenceT) <= TTolerance,
            FormattableString.Invariant($"{caseName}: hit at T {hit.T:R}; the reference t is {row.ReferenceT:R} (tolerance {TTolerance:E0})"));
        Assert.True(
            hit.TriangleId == row.TriangleId && hit.Kind == row.Kind,
            FormattableString.Invariant($"{caseName}: hit {hit.Kind} triangle {hit.TriangleId}; expected {row.Kind} triangle {row.TriangleId}"));
    }

    /// <summary>
    /// data-model.md#mesh-set (triangles are double-sided): Given <c>ProjectileWorlds.Wall()</c> and a level
    /// segment 1 m above the ground crossing the wall's plane z = 12.5 m at x = 0.5 m, When
    /// <c>SegmentQuery</c> runs once from the front (travelling +Z) and once from the back (the same segment
    /// reversed), Then both return true with <c>Kind == HitKind.Mesh</c>, the same <c>TriangleId</c>, which
    /// is the wall triangle the reference meets, and <c>T</c> equal to the reference t (0.5) within 1e-5.
    /// </summary>
    [Fact]
    public void TrianglesAreDoubleSided()
    {
        var data = ProjectileWorlds.Wall();
        var world = TestWorlds.Load(data);
        var front = new Vec3(0.5f, 1f, 5f);
        var back = new Vec3(0.5f, 1f, 20f);
        var (expectedId, frontT) = FirstMeshTriangle(data, front, back, "front");
        var (_, backT) = FirstMeshTriangle(data, back, front, "back");

        var fromFront = Query(world, front, back, out var frontHit, "from the front");
        var fromBack = Query(world, back, front, out var backHit, "from the back");

        Assert.True(fromFront && fromBack, $"the wall was hit from the front: {fromFront}, from the back: {fromBack}; both must hit");
        Assert.True(
            frontHit.Kind == HitKind.Mesh && backHit.Kind == HitKind.Mesh,
            $"the wall hits are {frontHit.Kind} (front) and {backHit.Kind} (back); both must be Mesh");
        Assert.True(
            frontHit.TriangleId == expectedId && backHit.TriangleId == expectedId,
            $"the wall hits name triangle {frontHit.TriangleId} (front) and {backHit.TriangleId} (back); both must be the reference's {expectedId}");
        Assert.True(
            System.Math.Abs(frontHit.T - frontT) <= TTolerance && System.Math.Abs(backHit.T - backT) <= TTolerance,
            FormattableString.Invariant($"the wall hits are at T {frontHit.T:R} (front) and {backHit.T:R} (back); the reference has {frontT:R} and {backT:R}"));
    }

    /// <summary>
    /// collision-data-format.md#triangle-ids (boundary planes exist only in the capsule sweep): Given
    /// <c>ProjectileWorlds.Flat()</c>, with no mesh, and a segment from (500, 1, 0) to (530, −1, 0) that is
    /// 0.2 m above the ground where it leaves the playable area across x = maxX = 512 m and passes height 0
    /// only outside it, When <c>SegmentQuery</c> runs, Then it returns false: neither the boundary plane
    /// x = maxX (ID −3) nor anything else is hit. (<see cref="Query"/> also fails any query in this class
    /// that reports an ID in −4..−1.)
    /// </summary>
    [Fact]
    public void BoundaryPlanesAreNeverHit()
    {
        var data = ProjectileWorlds.Flat();
        RequireFlat(data);
        Assert.Empty(data.Meshes);
        var world = TestWorlds.Load(data);
        var inside = new Vec3(500f, 1f, 0f);
        var outside = new Vec3(530f, -1f, 0f);
        Assert.True(
            world.Heightfield.MaxX == 512f && inside.X < world.Heightfield.MaxX && outside.X > world.Heightfield.MaxX,
            FormattableString.Invariant($"the segment must cross x = maxX; maxX is {world.Heightfield.MaxX:R}"));

        var found = Query(world, inside, outside, out var hit, "leaving across x = maxX");

        Assert.False(found, $"a segment leaving the playable area across x = maxX with no geometry hit {hit.Kind} triangle {hit.TriangleId} at T {hit.T}");
    }

    /// <summary>
    /// data-model.md#collisionworld (the segment query visits every cell the segment crosses): Given
    /// <c>ProjectileWorlds.Flat()</c> with a 1 m uniform grid and one small mesh triangle lying wholly in the
    /// grid cell (544, 536), from (32, 24) to (33, 25) m, and a level segment 10 m above the ground, 90 m
    /// long, from (−39.5, 10, −29.5) to (32.5, 10, 24.5), which crosses over 100 grid cells and ends in that
    /// cell, meeting the triangle at t ≈ 0.998, When <c>SegmentQuery</c> runs, Then it returns true with
    /// <c>Kind == HitKind.Mesh</c>, that triangle's ID (the first mesh ID, the heightfield triangle count) and
    /// <c>T</c> equal to the reference t within 1e-5.
    /// </summary>
    [Fact]
    public void SegmentCrossingManyCellsFindsTheFarTriangle()
    {
        var a = new Vec3(-39.5f, 10f, -29.5f);
        var b = new Vec3(32.5f, 10f, 24.5f);
        float[] vertices =
        [
            32.446f, 9.85f, 24.272f,
            32.266f, 9.85f, 24.512f,
            32.356f, 10.2f, 24.392f,
        ];
        var data = ProjectileWorlds.Flat() with
        {
            GridCellSize = 1f,
            Meshes = [new MeshData(1, vertices, [0, 1, 2])],
        };
        RequireFlat(data);
        var world = TestWorlds.Load(data);
        var triangleId = HeightfieldTriangleCount(data);
        var lastCell = (X: world.Grid.CellX(b.X), Z: world.Grid.CellZ(b.Z));
        for (var v = 0; v < vertices.Length; v += 3)
        {
            Assert.True(
                world.Grid.CellX(vertices[v]) == lastCell.X && world.Grid.CellZ(vertices[v + 2]) == lastCell.Z,
                FormattableString.Invariant($"triangle vertex {v / 3} is in grid cell ({world.Grid.CellX(vertices[v])}, {world.Grid.CellZ(vertices[v + 2])}), not the segment's last cell {lastCell}"));
        }

        var length = (DoubleVec3.From(b) - DoubleVec3.From(a)).Length;
        var cellsCrossed = System.Math.Abs(lastCell.X - world.Grid.CellX(a.X)) + System.Math.Abs(lastCell.Z - world.Grid.CellZ(a.Z));
        Assert.True(
            System.Math.Abs(length - 90.0) <= 1e-3 && cellsCrossed >= 100,
            FormattableString.Invariant($"the segment is {length:R} m long and crosses {cellsCrossed} grid cell boundaries; expected 90 m and at least 100"));
        var reference = ProjectileReference.SegmentTriangle(a, b, Vertex(vertices, 0), Vertex(vertices, 1), Vertex(vertices, 2));
        Assert.True(
            reference is > 0.99 and < 1.0,
            FormattableString.Invariant($"the reference meets the triangle at t {reference}; expected just before the segment's end"));

        var found = Query(world, a, b, out var hit, "across many cells");

        Assert.True(found, "a 90 m segment ending at a mesh triangle in its last grid cell found nothing");
        Assert.True(
            hit.Kind == HitKind.Mesh && hit.TriangleId == triangleId,
            $"the hit is {hit.Kind} triangle {hit.TriangleId}; expected Mesh triangle {triangleId}");
        Assert.True(
            System.Math.Abs(hit.T - reference!.Value) <= TTolerance,
            FormattableString.Invariant($"the hit is at T {hit.T:R}; the reference t is {reference.Value:R}"));
    }

    /// <summary>
    /// data-model.md#collisionworld: Given <c>ProjectileWorlds.Wall()</c>, whose heightfield and grid span
    /// |x|, |z| ≤ 512 m, and a segment from (600, 5, 600) to (700, −5, 650), which passes height 0 with both
    /// ends (and every point between) beyond the heightfield and the grid, When <c>SegmentQuery</c> runs,
    /// Then it returns false.
    /// </summary>
    [Fact]
    public void SegmentOutsideTheWorldFindsNothing()
    {
        var data = ProjectileWorlds.Wall();
        RequireFlat(data);
        var world = TestWorlds.Load(data);
        var a = new Vec3(600f, 5f, 600f);
        var b = new Vec3(700f, -5f, 650f);
        Assert.True(
            a.X > world.Heightfield.MaxX && b.X > world.Heightfield.MaxX && a.Z > world.Heightfield.MaxZ && b.Z > world.Heightfield.MaxZ,
            "both ends must lie beyond the heightfield's +X and +Z edges");

        var found = Query(world, a, b, out var hit, "outside the world");

        Assert.False(found, $"a segment wholly outside the heightfield and grid hit {hit.Kind} triangle {hit.TriangleId} at T {hit.T}");
    }

    /// <summary>
    /// <c>SegmentQuery</c>, failing when a found hit names an ID below 0: a boundary plane (−4 to −1) is
    /// never hit by a segment, and no other ID is negative.
    /// </summary>
    private static bool Query(CollisionWorld world, Vec3 a, Vec3 b, out SegmentHit hit, string context)
    {
        var found = world.SegmentQuery(a, b, out hit);
        if (found)
        {
            Assert.True(
                hit.TriangleId >= 0,
                $"{context}: SegmentQuery reported triangle {hit.TriangleId}; boundary planes (−4 to −1) are never hit by a segment");
        }

        return found;
    }

    /// <summary>A row of <see cref="EarliestTriangleWinsAndTiesGoToTheLowerId"/>, with its Given checked against the references.</summary>
    private static QueryCase Case(string caseName)
    {
        switch (caseName)
        {
            case "terrain-before-mesh":
                {
                    var data = ProjectileWorlds.Wall();
                    RequireFlat(data);
                    var a = new Vec3(0.3f, 2f, 4f);
                    var b = new Vec3(0.3f, -2f, 16f);
                    var (t, id) = Terrain(data, a, b, caseName);
                    foreach (var (meshId, v0, v1, v2) in MeshTriangles(data))
                    {
                        var wall = ProjectileReference.SegmentTriangle(a, b, v0, v1, v2);
                        Assert.True(
                            wall is null || wall.Value > t,
                            FormattableString.Invariant($"{caseName}: the reference meets wall triangle {meshId} at t {wall}, not after the ground at t {t:R}"));
                    }

                    return new QueryCase(data, a, b, HitKind.Terrain, id, t);
                }

            case "mesh-before-terrain":
                {
                    var data = ProjectileWorlds.Wall();
                    RequireFlat(data);
                    var a = new Vec3(0.5f, 2f, 5f);
                    var b = new Vec3(0.5f, -1f, 20f);
                    var (id, t) = FirstMeshTriangle(data, a, b, caseName);
                    var (groundT, _) = Terrain(data, a, b, caseName);
                    Assert.True(
                        groundT > t,
                        FormattableString.Invariant($"{caseName}: the reference meets the ground at t {groundT:R}, not after the wall at t {t:R}"));
                    return new QueryCase(data, a, b, HitKind.Mesh, id, t);
                }

            case "two-coincident-mesh-triangles":
                {
                    var data = ProjectileWorlds.Flat() with
                    {
                        Meshes = [new MeshData(1, [-2f, 0f, 6f, 2f, 0f, 6f, 0f, 3f, 6f], [0, 1, 2, 0, 1, 2])],
                    };
                    RequireFlat(data);
                    var a = new Vec3(0.2f, 1f, 0f);
                    var b = new Vec3(0.2f, 1f, 10f);
                    var triangles = MeshTriangles(data);
                    Assert.Equal(2, triangles.Count);
                    var first = ProjectileReference.SegmentTriangle(a, b, triangles[0].V0, triangles[0].V1, triangles[0].V2);
                    var second = ProjectileReference.SegmentTriangle(a, b, triangles[1].V0, triangles[1].V1, triangles[1].V2);
                    Assert.True(
                        first is not null && second is not null && first.Value == second.Value,
                        FormattableString.Invariant($"{caseName}: the reference meets the coincident triangles at t {first} and {second}; expected the same t"));
                    return new QueryCase(data, a, b, HitKind.Mesh, System.Math.Min(triangles[0].Id, triangles[1].Id), first.Value);
                }

            case "shared-heightfield-edge":
                {
                    // Cell (128, 128) spans (0, 0) to (4, 4); (1, 0, 1) is on its v00 → v11 diagonal, held by
                    // k = 0 (v00, v11, v10) and k = 1 (v00, v01, v11). The lower ID is k = 0's.
                    var data = ProjectileWorlds.Flat();
                    RequireFlat(data);
                    var a = new Vec3(0f, 2f, 2f);
                    var b = new Vec3(2f, -2f, 0f);
                    var (t, _) = Terrain(data, a, b, caseName);
                    return new QueryCase(data, a, b, HitKind.Terrain, HeightfieldTriangleId(data, CentreCell, CentreCell, 0), t);
                }

            case "shared-cell-boundary":
                {
                    // (2, 0, 4) is on the edge z = 4 between cells (128, 128) and (128, 129): the first's
                    // k = 1 triangle (v00, v01, v11) holds its top edge v01 → v11, the second's k = 0 triangle
                    // (v00, v11, v10) holds its bottom edge v00 → v10. The lower ID is the first's k = 1.
                    var data = ProjectileWorlds.Flat();
                    RequireFlat(data);
                    var a = new Vec3(1f, 2f, 3f);
                    var b = new Vec3(3f, -2f, 5f);
                    var (t, _) = Terrain(data, a, b, caseName);
                    var lower = HeightfieldTriangleId(data, CentreCell, CentreCell, 1);
                    Assert.True(lower < HeightfieldTriangleId(data, CentreCell, CentreCell + 1, 0), $"{caseName}: triangle {lower} must be the lower of the two IDs");
                    return new QueryCase(data, a, b, HitKind.Terrain, lower, t);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of OrderRows.");
        }
    }

    /// <summary>The reference's terrain hit, which must exist.</summary>
    private static (double T, int TriangleId) Terrain(CollisionData data, Vec3 a, Vec3 b, string context)
    {
        var reference = ProjectileReference.SegmentHeightfield(data, a, b);
        Assert.True(reference.HasValue, $"{context}: the reference finds no heightfield triangle between {a} and {b}");
        var (t, id) = reference.Value;
        return (t, id);
    }

    /// <summary>
    /// The mesh triangle the reference meets first along a → b, ties to the lower ID, and its t; it must
    /// meet one.
    /// </summary>
    private static (int Id, double T) FirstMeshTriangle(CollisionData data, Vec3 a, Vec3 b, string context)
    {
        (int Id, double T)? best = null;
        foreach (var (id, v0, v1, v2) in MeshTriangles(data))
        {
            var t = ProjectileReference.SegmentTriangle(a, b, v0, v1, v2);
            if (t is double value && (best is null || value < best.Value.T))
            {
                best = (id, value);
            }
        }

        Assert.True(best.HasValue, $"{context}: the reference meets no mesh triangle between {a} and {b}");
        return best.Value;
    }

    /// <summary>
    /// Every mesh triangle of the data in ID order: ID = heightfield triangle count + the triangle's global
    /// index in file order (collision-data-format.md#triangle-ids), with its three vertices.
    /// </summary>
    private static List<(int Id, Vec3 V0, Vec3 V1, Vec3 V2)> MeshTriangles(CollisionData data)
    {
        var triangles = new List<(int Id, Vec3 V0, Vec3 V1, Vec3 V2)>();
        var id = HeightfieldTriangleCount(data);
        foreach (var mesh in data.Meshes)
        {
            for (var k = 0; k + 2 < mesh.Indices.Length; k += 3)
            {
                triangles.Add((
                    id,
                    Vertex(mesh.Vertices, (int)mesh.Indices[k]),
                    Vertex(mesh.Vertices, (int)mesh.Indices[k + 1]),
                    Vertex(mesh.Vertices, (int)mesh.Indices[k + 2])));
                id++;
            }
        }

        return triangles;
    }

    private static Vec3 Vertex(float[] vertices, int index) => new(vertices[3 * index], vertices[(3 * index) + 1], vertices[(3 * index) + 2]);

    private static int HeightfieldTriangleCount(CollisionData data) => 2 * (data.Columns - 1) * (data.Rows - 1);

    /// <summary>The heightfield triangle ID rule: 2·(j·(columns − 1) + i) + k.</summary>
    private static int HeightfieldTriangleId(CollisionData data, int i, int j, int k) => (2 * ((j * (data.Columns - 1)) + i)) + k;

    /// <summary>The arrange's check that the data has <c>ProjectileWorlds.Flat()</c>'s heightfield, on which the cases' cells are worked out.</summary>
    private static void RequireFlat(CollisionData data)
    {
        Assert.True(
            data.OriginX == FlatOrigin && data.OriginZ == FlatOrigin && data.CellSize == FlatCellSize
                && data.Columns == FlatSamples && data.Rows == FlatSamples && data.Heights.All(h => h == 0f),
            FormattableString.Invariant(
                $"the world's heightfield is origin ({data.OriginX:R}, {data.OriginZ:R}), cell {data.CellSize:R}, {data.Columns} x {data.Rows}; expected ProjectileWorlds.Flat()'s (-512, -512), 4, 257 x 257, every height 0"));
    }

    /// <summary>One row: the world, the segment, and the expected kind, triangle ID and reference t.</summary>
    private sealed record QueryCase(CollisionData Data, Vec3 A, Vec3 B, HitKind Kind, int TriangleId, double ReferenceT);
}
