using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Collision;

/// <summary>
/// The box query of data-model.md#collisionworld (FR-004): <c>CollisionWorld.QueryBox</c> writes into the
/// caller's span the heightfield triangles of every cell the box overlaps whose four corner heights reach
/// the box's Y range, then every kept mesh triangle whose bounding box overlaps the box, found through the
/// uniform grid and kept only in the grid cell holding (max(tri.minX, box.minX), max(tri.minZ, box.minZ)),
/// so the output is ascending with no duplicates. A span too small for the result is never truncated:
/// the query returns −1 and a world step that needs it refuses with <c>QueryCapacityExceeded</c>.
/// </summary>
public sealed class BoxQueryTests
{
    /// <summary>The span every query writes into: the per-cell limit.</summary>
    private const int Capacity = 64;

    private const ulong Salt = 7;

    /// <summary>The <see cref="SpanningWorld"/>'s one mesh triangle: after its 2·4·4 = 32 heightfield triangles.</summary>
    private const int SpanningTriangleId = 32;

    /// <summary><c>tiny-world</c>'s degenerate triangle (a repeated index), dropped at load.</summary>
    private const int DegenerateTriangleId = 10;

    public static TheoryData<string> Boxes =>
    [
        "tiny-whole-world",
        "tiny-quad-cell",
        "tiny-above-corner-heights",
        "spanning-triangle",
    ];

    /// <summary>
    /// FR-004: given the world, when <c>QueryBox</c> runs over the case's box with a 64-slot span, the count
    /// c is at least 0 and <c>output[..c]</c> is strictly ascending and holds exactly the triangles the
    /// rule selects (computed here from the <see cref="CollisionData"/>, and equal to the case's IDs worked
    /// out by hand): in <c>tiny-world</c> never the dropped degenerate ID 10, and the spanning triangle,
    /// registered in all four grid cells the box covers, exactly once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Boxes))]
    public void CandidatesAreAscendingWithoutDuplicates(string box)
    {
        var query = Case(box);
        var world = TestWorlds.Load(query.Data);
        var output = new int[Capacity];

        var count = world.QueryBox(query.MinX, query.MinY, query.MinZ, query.MaxX, query.MaxY, query.MaxZ, output);

        Assert.True(count >= 0, $"{box}: QueryBox returned {count} with a {Capacity}-slot span");
        var found = output[..count];
        for (var k = 1; k < found.Length; k++)
        {
            Assert.True(found[k - 1] < found[k], $"{box}: output [{string.Join(", ", found)}] is not strictly ascending at {k}");
        }

        var expected = Expected(query);
        Assert.Equal(query.ExpectedIds, expected);
        Assert.Equal(expected, found);
        if (query.DroppedId is int dropped)
        {
            Assert.DoesNotContain(dropped, found);
        }

        if (query.SpanningId is int spanning)
        {
            Assert.Single(found, id => id == spanning);
        }
    }

    /// <summary>
    /// data-model.md#collisionworld: overflowing the span is never truncated. <c>QueryBox</c> over all of
    /// <c>tiny-world</c> (ten candidates) into a 1-slot span returns −1. A <see cref="SimWorld"/> built with
    /// the test-only constructor and a candidate capacity of 1 on <c>tiny-world</c>, holding one character
    /// that would walk forward, refuses its first step with <c>QueryCapacityExceeded</c> at element index 0,
    /// hash 0 and the previous step number, and commits nothing: the step number and the committed
    /// character (the state that is serialised and hashed) are as <c>Reset</c> loaded them. A world whose
    /// every query overflows can never step, so the committed state is read through the world's internal
    /// views rather than through a later step's bytes.
    /// </summary>
    [Fact]
    public void OverflowRefusesTheStepWithQueryCapacityExceeded()
    {
        var tiny = TestWorlds.Load(TestWorlds.TinyWorld());

        Assert.Equal(-1, tiny.QueryBox(-1.5f, -1f, -1.5f, 1.5f, 2f, 1.5f, new int[1]));

        const uint previousStep = 7;
        var state = TestStates.Build(
            previousStep,
            [TestStates.StandingCharacter(1, 0f, -0.5f) with { Position = (0f, 0.135f, -0.5f) }]);
        var loaded = StateSerializer.ReadLoaded(state, StateErrorContext.State);
        var world = new SimWorld(CompiledConfig.Create(TestConfigs.At(30)), tiny, 1);
        world.Reset(state, Salt);
        var walk = new InputCommand(1, 0, Buttons.Forward, 0, 0, 0);

        var report = world.Step([new CharacterCommand(0, walk)]);

        Assert.Equal(StepFaultCode.QueryCapacityExceeded, report.Fault.Code);
        Assert.Equal(0, report.Fault.ElementIndex);
        Assert.Equal(0UL, report.Hash);
        Assert.Equal(previousStep, report.Step);
        Assert.Equal(previousStep, world.CurrentStep);
        Assert.Equal(loaded.Characters, world.CommittedCharacters.ToArray());
        Assert.True(world.CommittedProjectiles.IsEmpty, "a refused step committed projectiles");
    }

    /// <summary>
    /// A box over a world, with the triangle IDs the rule selects, worked out by hand from the world's
    /// layout and listed in ascending order; the world's dropped degenerate ID, which must never appear;
    /// and the ID of a triangle registered in several grid cells the box covers, which must appear once.
    /// </summary>
    private sealed record BoxCase(
        CollisionData Data,
        float MinX,
        float MinY,
        float MinZ,
        float MaxX,
        float MaxY,
        float MaxZ,
        int[] ExpectedIds,
        int? DroppedId,
        int? SpanningId);

    /// <summary>
    /// The cases. <c>tiny-world</c> (contracts/collision-data-format.md#tiny-world) has cells 1 m square
    /// from (−1, −1), heightfield triangles 0 to 7 (cell (i, j) holds 2·(2j + i) and 2·(2j + i) + 1), every
    /// cell's corner heights spanning [0, 0.25] (the centre sample is 0.25), and in its one 4 m grid cell
    /// the wall quad 8 and 9 (x in [−0.5, 0.5], y in [0, 1], z = 0.5) and the dropped triangle 10. No box
    /// edge lies on a cell edge or a bounding box face, so whether overlap is inclusive does not matter.
    /// </summary>
    private static BoxCase Case(string box) => box switch
    {
        // Past the playable area on every side: all four cells and the quad.
        "tiny-whole-world" => new(TestWorlds.TinyWorld(), -1.5f, -1f, -1.5f, 1.5f, 2f, 1.5f, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9], DegenerateTriangleId, null),

        // Inside cell (1, 1), which the quad crosses at z = 0.5: that cell's two triangles and the quad.
        "tiny-quad-cell" => new(TestWorlds.TinyWorld(), 0.1f, -1f, 0.1f, 0.9f, 2f, 0.9f, [6, 7, 8, 9], DegenerateTriangleId, null),

        // The whole area from y = 0.5 up, above every corner height: every cell is skipped, the quad is not.
        "tiny-above-corner-heights" => new(TestWorlds.TinyWorld(), -1.5f, 0.5f, -1.5f, 1.5f, 2f, 1.5f, [8, 9], DegenerateTriangleId, null),

        // Cells (1..2, 1..2) of the 2 m heightfield, IDs 2·(4j + i) + k, and the one mesh triangle, whose
        // bounding box covers all four 4 m grid cells the box also covers.
        "spanning-triangle" => new(SpanningWorld(), -1f, -0.5f, -1f, 1f, 2f, 1f, [10, 11, 12, 13, 18, 19, 20, 21, SpanningTriangleId], null, SpanningTriangleId),

        _ => throw new ArgumentOutOfRangeException(nameof(box), box, "Unknown box case."),
    };

    /// <summary>
    /// A flat 5 × 5 heightfield (heights 0) with 2 m cells from (−4, −4), so the playable area is [−4, 4] in
    /// X and Z and its 4 m grid has four cells meeting at the origin, and one mesh holding a single triangle,
    /// (−2, 0.5, −2), (2, 1, −2), (−2, 1.5, 2), whose bounding box (x and z in [−2, 2]) overlaps all four.
    /// </summary>
    private static CollisionData SpanningWorld() => new(
        OriginX: -4f,
        OriginZ: -4f,
        CellSize: 2f,
        GridCellSize: 4f,
        Columns: 5,
        Rows: 5,
        Heights: new float[25],
        Meshes:
        [
            new MeshData(
                MeshId: 1,
                Vertices:
                [
                    -2f, 0.5f, -2f,
                    2f, 1f, -2f,
                    -2f, 1.5f, 2f,
                ],
                Indices: [0, 1, 2]),
        ]);

    /// <summary>
    /// The rule of data-model.md#collisionworld, applied to the primary data directly: for each cell
    /// (j outer, i inner) whose X and Z extent overlaps the box and whose four corner heights' range
    /// overlaps [minY, maxY], both of its triangle IDs; then, in file order, each mesh triangle that is not
    /// degenerate (here: no repeated index; the test worlds have no other degenerate triangle) whose
    /// bounding box overlaps the box, as heightfield triangle count + its global index. Ascending by
    /// construction.
    /// </summary>
    private static int[] Expected(BoxCase query)
    {
        var data = query.Data;
        var ids = new List<int>();
        for (var j = 0; j < data.Rows - 1; j++)
        {
            for (var i = 0; i < data.Columns - 1; i++)
            {
                var minX = data.OriginX + (i * data.CellSize);
                var minZ = data.OriginZ + (j * data.CellSize);
                float[] corners =
                [
                    data.Heights[(j * data.Columns) + i],
                    data.Heights[(j * data.Columns) + i + 1],
                    data.Heights[((j + 1) * data.Columns) + i],
                    data.Heights[((j + 1) * data.Columns) + i + 1],
                ];
                if (Overlaps(minX, minX + data.CellSize, query.MinX, query.MaxX)
                    && Overlaps(minZ, minZ + data.CellSize, query.MinZ, query.MaxZ)
                    && Overlaps(corners.Min(), corners.Max(), query.MinY, query.MaxY))
                {
                    var cell = (j * (data.Columns - 1)) + i;
                    ids.Add(2 * cell);
                    ids.Add((2 * cell) + 1);
                }
            }
        }

        var id = 2 * (data.Columns - 1) * (data.Rows - 1);
        foreach (var mesh in data.Meshes)
        {
            for (var t = 0; t < mesh.Indices.Length; t += 3, id++)
            {
                var (a, b, c) = ((int)mesh.Indices[t], (int)mesh.Indices[t + 1], (int)mesh.Indices[t + 2]);
                if (a == b || b == c || a == c)
                {
                    continue;
                }

                var overlaps = true;
                for (var axis = 0; axis < 3; axis++)
                {
                    float[] values = [mesh.Vertices[(3 * a) + axis], mesh.Vertices[(3 * b) + axis], mesh.Vertices[(3 * c) + axis]];
                    var (low, high) = axis switch
                    {
                        0 => (query.MinX, query.MaxX),
                        1 => (query.MinY, query.MaxY),
                        _ => (query.MinZ, query.MaxZ),
                    };
                    overlaps &= Overlaps(values.Min(), values.Max(), low, high);
                }

                if (overlaps)
                {
                    ids.Add(id);
                }
            }
        }

        return [.. ids];
    }

    private static bool Overlaps(float min, float max, float low, float high) => min <= high && max >= low;
}
