using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Collision;

/// <summary>
/// The collision world a decoder builds from primary data (FR-004,
/// contracts/collision-data-format.md#derived-data-built-at-load): heightfield triangles generated on
/// demand by the one sample expression and the fixed v00 → v11 diagonal, mesh triangles with their
/// normals and bounding boxes, degenerate triangles dropped with their IDs kept unused, the uniform
/// grid in CSR form, the playable area and its boundary planes, and the query capacity a
/// <c>SimWorld</c> sizes its candidate buffer from.
/// </summary>
public sealed class CollisionWorldBuildTests
{
    /// <summary>tiny-world's heightfield triangles: 2 · (3 − 1) · (3 − 1).</summary>
    private const int TinyHeightfieldTriangles = 8;

    /// <summary>tiny-world's third mesh triangle, indices (0, 0, 1): a repeated index, so degenerate.</summary>
    private const int DegenerateId = 10;

    /// <summary>
    /// Edge case "degenerate triangles": tiny-world's mesh holds the triangle (0, 0, 1). The world loads,
    /// counts it, keeps its ID unused and registers it in no grid cell.
    /// </summary>
    [Fact]
    public void DegenerateTrianglesLoadAndAreIgnored()
    {
        var world = TestWorlds.Load(TestWorlds.TinyWorld());

        Assert.Equal(1, world.DegenerateTriangleCount);
        Assert.False(world.Meshes.TryGetMeshTriangle(DegenerateId, out _), "triangle 10 (indices 0, 0, 1) is degenerate, so its ID must stay unused");
        Assert.True(world.Meshes.TryGetMeshTriangle(8, out _), "triangle 8 (the wall quad's first half) must be kept");
        Assert.True(world.Meshes.TryGetMeshTriangle(9, out _), "triangle 9 (the wall quad's second half) must be kept");

        var cellStart = world.Grid.CellStart.ToArray();
        var cellTriangles = world.Grid.CellTriangles.ToArray();
        Assert.DoesNotContain(DegenerateId, cellTriangles);
        int[] cell00 = [8, 9];
        Assert.Equal(cell00, cellTriangles[cellStart[0]..cellStart[1]]);
    }

    /// <summary>
    /// tiny-world's derived data, value by value: the triangle counts and IDs, the wall quad's normals
    /// and bounding boxes, heightfield triangle 3 bit for bit, the single grid cell, the playable area and
    /// its four boundary planes. Two decodes of the same bytes give the same bits.
    /// </summary>
    [Fact]
    public void TinyWorldDerivesItsTrianglesAndGrid()
    {
        var bytes = CollisionFile.Encode(TestWorlds.TinyWorld());
        var world = CollisionFile.Decode(bytes);

        Assert.Equal(TinyHeightfieldTriangles, world.HeightfieldTriangleCount);
        Assert.Equal(3, world.MeshTriangleCount);

        // Mesh IDs are 8 to 10: below 8 is the heightfield, above 10 is nothing.
        Assert.False(world.Meshes.TryGetMeshTriangle(TinyHeightfieldTriangles - 1, out _), "ID 7 is a heightfield triangle, not a mesh triangle");
        Assert.False(world.Meshes.TryGetMeshTriangle(DegenerateId + 1, out _), "ID 11 is past the last mesh triangle");
        foreach (var id in new[] { 8, 9 })
        {
            Assert.True(world.Meshes.TryGetMeshTriangle(id, out var triangle), $"mesh triangle {id} must exist");
            AssertVector($"triangle {id} normal", triangle.Normal, 0f, 0f, 1f);
            AssertVector($"triangle {id} AABB min", triangle.Min, -0.5f, 0f, 0.5f);
            AssertVector($"triangle {id} AABB max", triangle.Max, 0.5f, 1f, 0.5f);
        }

        // ID 3 = 2 · (j · (columns − 1) + i) + k with i = 1, j = 0, k = 1: cell (1, 0)'s (v00, v01, v11).
        world.Heightfield.GetTriangle(3, out var a, out var b, out var c);
        AssertBits("heightfield triangle 3 vertex a", a, 0f, 0f, -1f);
        AssertBits("heightfield triangle 3 vertex b", b, 0f, 0.25f, 0f);
        AssertBits("heightfield triangle 3 vertex c", c, 1f, 0f, 0f);

        Assert.Equal(1, world.GridCellsX);
        Assert.Equal(1, world.GridCellsZ);
        int[] cellStart = [0, 2];
        int[] cellTriangles = [8, 9];
        Assert.Equal(cellStart, world.Grid.CellStart.ToArray());
        Assert.Equal(cellTriangles, world.Grid.CellTriangles.ToArray());

        Assert.Equal(-1f, world.Heightfield.MinX);
        Assert.Equal(1f, world.Heightfield.MaxX);
        Assert.Equal(-1f, world.Heightfield.MinZ);
        Assert.Equal(1f, world.Heightfield.MaxZ);

        // Each boundary plane is the set of points p with Dot(normal, p) == distance, its normal facing
        // into the playable area, through the side the contract names.
        AssertBoundaryPlane(world, -4, new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0.3f));
        AssertBoundaryPlane(world, -3, new Vec3(-1f, 0f, 0f), new Vec3(1f, 0f, -0.3f));
        AssertBoundaryPlane(world, -2, new Vec3(0f, 0f, 1f), new Vec3(0.3f, 0f, -1f));
        AssertBoundaryPlane(world, -1, new Vec3(0f, 0f, -1f), new Vec3(-0.3f, 0f, 1f));

        var again = CollisionFile.Decode(bytes);
        foreach (var id in new[] { 8, 9 })
        {
            Assert.True(world.Meshes.TryGetMeshTriangle(id, out var first), $"mesh triangle {id} must exist in the first decode");
            Assert.True(again.Meshes.TryGetMeshTriangle(id, out var second), $"mesh triangle {id} must exist in the second decode");
            AssertBits($"triangle {id} normal, second decode", second.Normal, first.Normal.X, first.Normal.Y, first.Normal.Z);
            AssertBits($"triangle {id} AABB min, second decode", second.Min, first.Min.X, first.Min.Y, first.Min.Z);
            AssertBits($"triangle {id} AABB max, second decode", second.Max, first.Max.X, first.Max.Y, first.Max.Z);
        }

        Assert.Equal(world.Grid.CellStart.ToArray(), again.Grid.CellStart.ToArray());
        Assert.Equal(world.Grid.CellTriangles.ToArray(), again.Grid.CellTriangles.ToArray());
    }

    /// <summary>
    /// <c>HeightAt</c> follows the fixed v00 → v11 diagonal of tiny-world's cell (1, 0), whose v01
    /// corner is the 0.25 m sample: triangle k = 0 (u ≥ w) is level, triangle k = 1 tilts, and a point on
    /// the diagonal reads 0 from either side.
    /// </summary>
    [Theory]
    [InlineData(0.75f, -0.75f, 0f, 0f)]
    [InlineData(0.25f, -0.25f, 0.125f, 1e-6f)]
    [InlineData(0.5f, -0.5f, 0f, 0f)]
    [InlineData(0f, 0f, 0.25f, 0f)]
    public void HeightAtFollowsTheFixedDiagonal(float x, float z, float expected, float tolerance)
    {
        var world = TestWorlds.Load(TestWorlds.TinyWorld());

        var height = world.HeightAt(x, z);

        if (tolerance == 0f)
        {
            Assert.True(height == expected, $"HeightAt({x}, {z}) is {height:R}, expected exactly {expected:R}");
        }
        else
        {
            Assert.True(System.MathF.Abs(height - expected) <= tolerance, $"HeightAt({x}, {z}) is {height:R}, expected {expected:R} within {tolerance:R}");
        }
    }

    /// <summary>
    /// Every vertex of every heightfield triangle is the one sample expression
    /// <c>(originX + (float)i·cellSize, h, originZ + (float)j·cellSize)</c>, bit for bit, on a grid whose
    /// origin and cell size do not round exactly, so a vertex shared by neighbouring cells is identical in
    /// each and the surface is watertight.
    /// </summary>
    [Fact]
    public void SharedVerticesAreBitIdentical()
    {
        const int samples = 9;
        const float originX = -1000.3f;
        const float originZ = 2000.7f;
        const float cellSize = 0.37f;
        var heights = new float[samples * samples];
        for (var n = 0; n < heights.Length; n++)
        {
            heights[n] = ((float)((n * 37) % 23) * 0.173f) - 1.9f;
        }

        var data = new CollisionData(originX, originZ, cellSize, 4f, samples, samples, heights, []);
        var world = TestWorlds.Load(data);
        Assert.Equal(2 * (samples - 1) * (samples - 1), world.HeightfieldTriangleCount);

        var seen = new Dictionary<(int I, int J), (uint X, uint Y, uint Z)>();
        var mismatches = new List<string>();
        for (var j = 0; j < samples - 1; j++)
        {
            for (var i = 0; i < samples - 1; i++)
            {
                for (var k = 0; k < 2; k++)
                {
                    var id = (2 * ((j * (samples - 1)) + i)) + k;
                    world.Heightfield.GetTriangle(id, out var a, out var b, out var c);

                    // k = 0 is (v00, v11, v10); k = 1 is (v00, v01, v11).
                    var corners = k == 0
                        ? new (int I, int J)[] { (i, j), (i + 1, j + 1), (i + 1, j) }
                        : new (int I, int J)[] { (i, j), (i, j + 1), (i + 1, j + 1) };
                    Vec3[] vertices = [a, b, c];
                    for (var v = 0; v < 3; v++)
                    {
                        var (si, sj) = corners[v];
                        var expected = (
                            Bits(originX + ((float)si * cellSize)),
                            Bits(heights[(sj * samples) + si]),
                            Bits(originZ + ((float)sj * cellSize)));
                        var actual = (Bits(vertices[v].X), Bits(vertices[v].Y), Bits(vertices[v].Z));
                        if (actual != expected)
                        {
                            mismatches.Add($"triangle {id} vertex {v} (sample {si}, {sj}) is {Hex(actual)}, the sample expression gives {Hex(expected)}");
                        }

                        if (seen.TryGetValue((si, sj), out var earlier))
                        {
                            if (earlier != actual)
                            {
                                mismatches.Add($"sample ({si}, {sj}) is {Hex(earlier)} in one triangle and {Hex(actual)} in triangle {id}");
                            }
                        }
                        else
                        {
                            seen.Add((si, sj), actual);
                        }
                    }
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} vertex mismatches; first: {string.Join("; ", mismatches.Take(5))}");
        Assert.Equal(samples * samples, seen.Count);
    }

    /// <summary>
    /// <c>MaxQueryCandidates(w)</c> is <c>2·(⌈w/cellSize⌉+1)² + 64·(⌈w/gridCellSize⌉+1)²</c>: the heightfield
    /// cells a box of width w can overlap, two triangles each, plus the grid cells it can overlap, 64
    /// triangles each. On <see cref="TestWorlds.Flat"/> (cell size 1, grid cell size 4), 1.22 m is the
    /// placeholder capsule's widest box (3 · 0.4 + 2 · 0.01, research R6), and the larger rows take the grid
    /// term past one grid cell.
    /// </summary>
    [Theory]
    [InlineData(1.22f, 274)]
    [InlineData(0.5f, 264)]
    [InlineData(4f, 306)]
    [InlineData(4.5f, 648)]
    [InlineData(8.5f, 1224)]
    public void MaxQueryCandidatesCoversTheWidestCapsuleBox(float width, int expected)
    {
        const float cellSize = 1f;
        const float gridCellSize = 4f;
        var world = TestWorlds.Load(TestWorlds.Flat(cellSize: cellSize, gridCellSize: gridCellSize));

        var heightfieldCells = (int)System.MathF.Ceiling(width / cellSize) + 1;
        var gridCells = (int)System.MathF.Ceiling(width / gridCellSize) + 1;
        var formula = (2 * heightfieldCells * heightfieldCells) + (64 * gridCells * gridCells);
        Assert.Equal(expected, formula);

        Assert.Equal(expected, world.MaxQueryCandidates(width));
    }

    private static void AssertBoundaryPlane(CollisionWorld world, int id, Vec3 expectedNormal, Vec3 pointOnSide)
    {
        world.Heightfield.GetBoundaryPlane(id, out var normal, out var distance);

        AssertVector($"boundary plane {id} normal", normal, expectedNormal.X, expectedNormal.Y, expectedNormal.Z);
        var onPlane = Vec3.Dot(normal, pointOnSide);
        Assert.True(onPlane == distance, $"boundary plane {id}: Dot(normal, {pointOnSide.X}, {pointOnSide.Y}, {pointOnSide.Z}) is {onPlane:R}, but the plane's distance is {distance:R}");
        var centre = Vec3.Dot(normal, new Vec3(0f, 0f, 0f));
        Assert.True(centre > distance, $"boundary plane {id}: the playable area's centre is not on the side its normal faces");
    }

    private static void AssertVector(string label, Vec3 actual, float x, float y, float z) =>
        Assert.True(
            actual.X == x && actual.Y == y && actual.Z == z,
            $"{label} is ({actual.X:R}, {actual.Y:R}, {actual.Z:R}), expected ({x:R}, {y:R}, {z:R})");

    private static void AssertBits(string label, Vec3 actual, float x, float y, float z)
    {
        var expected = (Bits(x), Bits(y), Bits(z));
        var bits = (Bits(actual.X), Bits(actual.Y), Bits(actual.Z));
        Assert.True(bits == expected, $"{label} is {Hex(bits)}, expected {Hex(expected)}");
    }

    private static string Hex((uint X, uint Y, uint Z) bits) => $"(0x{bits.X:X8}, 0x{bits.Y:X8}, 0x{bits.Z:X8})";

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
