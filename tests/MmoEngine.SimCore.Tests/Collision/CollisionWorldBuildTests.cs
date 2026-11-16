using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;
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

    /// <summary>
    /// tiny-world's third mesh triangle, and the triangle each degenerate case puts in its place: always
    /// ID 10, after the wall quad's 8 and 9.
    /// </summary>
    private const int DegenerateId = 10;

    /// <summary>
    /// One row per clause of the contract's degeneracy rule. <c>repeated-index</c> is tiny-world itself,
    /// whose triangle (0, 0, 1) has a zero-length edge. The others keep tiny-world's heightfield and wall
    /// quad and replace triangle 10 with three distinct vertices whose every edge is at least 1 mm:
    /// <c>collinear</c> has |AB × AC|² = 0; <c>sliver</c> has |AB × AC|² = 1e-10, at most 1e-8·(maxEdge²)²
    /// but above 4e-12, so only the sliver clause catches it; <c>tiny-area</c> has edges of 1.2 mm and
    /// |AB × AC|² ≈ 2.07e-12, at most 4e-12 but above 1e-8·(maxEdge²)², so only the absolute clause
    /// catches it.
    /// </summary>
    public static TheoryData<string> DegenerateCases => ["repeated-index", "collinear", "sliver", "tiny-area"];

    /// <summary>
    /// Edge case "degenerate triangles": a mesh with a degenerate (zero-area) triangle loads, and that
    /// triangle is ignored. The world counts it, keeps its ID unused, registers it in no grid cell, and
    /// keeps the wall quad's two triangles in cell (0, 0).
    /// </summary>
    [Theory]
    [MemberData(nameof(DegenerateCases))]
    public void DegenerateTrianglesLoadAndAreIgnored(string caseName)
    {
        var world = TestWorlds.Load(DegenerateWorld(caseName));

        Assert.True(world.DegenerateTriangleCount == 1, $"{caseName}: DegenerateTriangleCount is {world.DegenerateTriangleCount}, expected 1");
        Assert.False(world.Meshes.TryGetMeshTriangle(DegenerateId, out _), $"{caseName}: triangle 10 is degenerate, so its ID must stay unused");
        Assert.True(world.Meshes.TryGetMeshTriangle(8, out _), $"{caseName}: triangle 8 (the wall quad's first half) must be kept");
        Assert.True(world.Meshes.TryGetMeshTriangle(9, out _), $"{caseName}: triangle 9 (the wall quad's second half) must be kept");

        var cellStart = world.Grid.CellStart.ToArray();
        var cellTriangles = world.Grid.CellTriangles.ToArray();
        Assert.DoesNotContain(DegenerateId, cellTriangles);
        int[] cell00 = [8, 9];
        Assert.Equal(cell00, cellTriangles[cellStart[0]..cellStart[1]]);
    }

    /// <summary>
    /// The control for the sliver clause: the contract's near-sliver (0.46, 0, 0), (0.47, 0, 0),
    /// (0.46, 0.5, 0), 10 mm by 0.5 m, is not degenerate. It is kept as triangle 10 with the finite unit
    /// normal (0, 0, 1), and cell (0, 0) holds [8, 9, 10].
    /// </summary>
    [Fact]
    public void NearSliverTriangleIsKept()
    {
        var world = TestWorlds.Load(WithTriangleTen(0.46f, 0f, 0f, 0.47f, 0f, 0f, 0.46f, 0.5f, 0f));

        Assert.Equal(0, world.DegenerateTriangleCount);
        Assert.True(world.Meshes.TryGetMeshTriangle(DegenerateId, out var triangle), "triangle 10 (the near-sliver) must be kept");
        var normal = triangle.Normal;
        Assert.True(
            float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z)
                && System.MathF.Abs(normal.X) <= 1e-6f && System.MathF.Abs(normal.Y) <= 1e-6f && System.MathF.Abs(normal.Z - 1f) <= 1e-6f,
            $"triangle 10's normal is ({normal.X:R}, {normal.Y:R}, {normal.Z:R}), expected (0, 0, 1) within 1e-6");

        var cellStart = world.Grid.CellStart.ToArray();
        var cellTriangles = world.Grid.CellTriangles.ToArray();
        int[] cell00 = [8, 9, 10];
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
    /// <c>MaxQueryCandidates(w)</c> is <c>2·n(cellSize)² + 64·n(gridCellSize)²</c> with
    /// <c>n(c) = ⌊(w + 1/64)/c⌋ + 2</c>: the heightfield cells a box of width w can overlap per axis, two
    /// triangles each, plus the grid cells it can overlap, 64 triangles each. The 1/64 m margin covers the
    /// float rounding of the box's edges and of the cell function, so a whole-number ratio w/c gets the
    /// extra cell its rounded edges can reach. 1.22 m is the placeholder capsule's widest box
    /// (3 · 0.4 + 2 · 0.01, research R6); the (1f, …) and (4f, …) rows are whole-number ratios, and the
    /// larger rows take the grid term past one grid cell.
    /// </summary>
    [Theory]
    [InlineData(1f, 4f, 1.22f, 274)]
    [InlineData(1f, 4f, 0.5f, 264)]
    [InlineData(1f, 4f, 1f, 274)]
    [InlineData(1f, 4f, 4f, 648)]
    [InlineData(1f, 4f, 4.5f, 648)]
    [InlineData(1f, 4f, 8.5f, 1224)]
    [InlineData(1f, 1f, 1f, 594)]
    [InlineData(0.25f, 1f, 1.22f, 648)]
    public void MaxQueryCandidatesCoversTheWidestCapsuleBox(float cellSize, float gridCellSize, float width, int expected)
    {
        var world = TestWorlds.Load(TestWorlds.Flat(cellSize: cellSize, gridCellSize: gridCellSize));

        var heightfieldCells = (int)System.Math.Floor(((double)width + (1.0 / 64.0)) / cellSize) + 2;
        var gridCells = (int)System.Math.Floor(((double)width + (1.0 / 64.0)) / gridCellSize) + 2;
        var formula = (2 * heightfieldCells * heightfieldCells) + (64 * gridCells * gridCells);
        Assert.Equal(expected, formula);

        Assert.Equal(expected, world.MaxQueryCandidates(width));
    }

    /// <summary>
    /// Worlds and box widths for <see cref="MaxQueryCandidatesCoversEveryFloatBoxOfThatWidth"/>: whole-number
    /// ratios w/c (the 4 m box on 4 m grid cells, and the 1 m box that a 0.3 m capsule with a 0.05 m skin
    /// sweeps on 1 m cells), the placeholder capsule's box, the widest capsule the configuration allows
    /// (3 · 1 + 2 · 0.05) on the smallest cells, a world near the 8192 m limit where float spacing is
    /// coarsest, and cell sizes that do not round exactly.
    /// </summary>
    public static TheoryData<string> FloatBoxCases =>
    [
        "whole-grid-ratio",
        "capsule-0.3-skin-0.05",
        "placeholder-capsule",
        "widest-capsule-smallest-cells",
        "near-the-limit",
        "inexact-cells",
    ];

    /// <summary>
    /// The capacity holds for real float boxes, not only for exact widths: for boxes whose low edge is a
    /// heightfield or grid cell boundary or one of its float neighbours, and whose high edge is
    /// <c>low + w</c> rounded in float plus up to two more float steps of rounding, the cells the box
    /// overlaps (grid cells by <c>UniformGrid.CellX</c>, heightfield cells by the same floor of the offset
    /// from the origin over the cell size) need at most <c>MaxQueryCandidates(w)</c> slots. The
    /// counterexample to the earlier <c>⌈w/c⌉ + 1</c> count: on 4 m grid cells from origin 0, the box
    /// [3.9999998, 3.9999998 + 4f = 8] spans grid cells 0 to 2, three cells for a 4 m width.
    /// </summary>
    [Theory]
    [MemberData(nameof(FloatBoxCases))]
    public void MaxQueryCandidatesCoversEveryFloatBoxOfThatWidth(string caseName)
    {
        var (origin, cellSize, gridCellSize, samples, width) = caseName switch
        {
            "whole-grid-ratio" => (0f, 1f, 4f, 17, 4f),
            "capsule-0.3-skin-0.05" => (0f, 1f, 4f, 17, (3f * 0.3f) + (2f * 0.05f)),
            "placeholder-capsule" => (-32f, 1f, 4f, 65, (3f * 0.4f) + (2f * 0.01f)),
            "widest-capsule-smallest-cells" => (-32f, 0.25f, 1f, 257, (3f * 1f) + (2f * 0.05f)),
            "near-the-limit" => (8100f, 1f, 4f, 65, 4f),
            "inexact-cells" => (-1000.3f, 0.37f, 1.11f, 33, 1.11f),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
        var world = TestWorlds.Load(new CollisionData(origin, origin, cellSize, gridCellSize, samples, samples, new float[samples * samples], []));
        var capacity = world.MaxQueryCandidates(width);

        var lowEdges = new List<float>();
        for (var k = 0; k < samples; k++)
        {
            AddNeighbours(lowEdges, origin + ((float)k * cellSize));
        }

        for (var k = 0; k <= world.GridCellsX; k++)
        {
            AddNeighbours(lowEdges, origin + ((float)k * gridCellSize));
        }

        var failures = new List<string>();
        var boxes = 0;
        foreach (var low in lowEdges)
        {
            var high = low + width;
            for (var extra = 0; extra <= 2; extra++)
            {
                var heightfieldCells = HeightfieldCell(high, origin, cellSize) - HeightfieldCell(low, origin, cellSize) + 1;
                var gridCells = world.Grid.CellX(high) - world.Grid.CellX(low) + 1;
                var needed = (2 * heightfieldCells * heightfieldCells) + (64 * gridCells * gridCells);
                if (needed > capacity)
                {
                    failures.Add($"[{low:R}, {high:R}] overlaps {heightfieldCells} heightfield and {gridCells} grid cells per axis, {needed} slots");
                }

                boxes++;
                high = MathF.BitIncrement(high);
            }
        }

        Assert.True(boxes > 0, $"{caseName}: no boxes were tried");
        Assert.True(
            failures.Count == 0,
            $"{caseName}: MaxQueryCandidates({width:R}) is {capacity}, but {failures.Count} of {boxes} boxes need more; first: {string.Join("; ", failures.Take(5))}");
    }

    /// <summary>The edge itself, its two float neighbours below and one above.</summary>
    private static void AddNeighbours(List<float> edges, float edge)
    {
        var below = MathF.BitDecrement(edge);
        edges.Add(MathF.BitDecrement(below));
        edges.Add(below);
        edges.Add(edge);
        edges.Add(MathF.BitIncrement(edge));
    }

    /// <summary>The heightfield cell holding x, unclamped: the floor of the offset from the origin over the cell size.</summary>
    private static int HeightfieldCell(float x, float origin, float cellSize) => SimMath.FloorToInt((x - origin) / cellSize);

    private static CollisionData DegenerateWorld(string caseName) => caseName switch
    {
        "repeated-index" => TestWorlds.TinyWorld(),
        "collinear" => WithTriangleTen(0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0f),
        "sliver" => WithTriangleTen(0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 1e-5f, 0f),
        "tiny-area" => WithTriangleTen(0f, 0f, 0f, 0.0012f, 0f, 0f, 0f, 0.0012f, 0f),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown degenerate case."),
    };

    /// <summary>
    /// tiny-world with its mesh's third triangle replaced: the wall quad's four vertices and triangles
    /// (0, 1, 2) and (0, 2, 3) (IDs 8 and 9), then three new vertices A, B and C forming triangle
    /// (4, 5, 6) (ID 10).
    /// </summary>
    private static CollisionData WithTriangleTen(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz) =>
        TestWorlds.TinyWorld() with
        {
            Meshes =
            [
                new MeshData(
                    MeshId: 1,
                    Vertices:
                    [
                        -0.5f, 0f, 0.5f,
                        0.5f, 0f, 0.5f,
                        0.5f, 1f, 0.5f,
                        -0.5f, 1f, 0.5f,
                        ax, ay, az,
                        bx, by, bz,
                        cx, cy, cz,
                    ],
                    Indices:
                    [
                        0, 1, 2,
                        0, 2, 3,
                        4, 5, 6,
                    ]),
            ],
        };

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
