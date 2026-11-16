using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Collision data for tests, as <see cref="CollisionData"/> values
/// (contracts/collision-data-format.md). Every call returns new arrays, so a test may change what it
/// gets. <see cref="Load"/> turns data into a <see cref="CollisionWorld"/> through the file format, the
/// only way a world is built (FR-004).
/// </summary>
public static class TestWorlds
{
    /// <summary>Negative zero, written by its bits so no constant folding can turn it into +0.</summary>
    public static float NegativeZero => BitConverter.UInt32BitsToSingle(0x80000000u);

    /// <summary>
    /// The <c>tiny-world</c> fixture of contracts/collision-data-format.md#tiny-world: origin (−1, −1),
    /// cell size 1, grid cell size 4, a 3 × 3 heightfield whose <c>heights[8]</c> is negative zero, and
    /// one mesh (ID 1) holding a wall quad and a degenerate triangle with a repeated index. It encodes
    /// to 188 bytes with checksum <c>9224e3fa3b3fca9c</c>.
    /// </summary>
    public static CollisionData TinyWorld() => new(
        OriginX: -1f,
        OriginZ: -1f,
        CellSize: 1f,
        GridCellSize: 4f,
        Columns: 3,
        Rows: 3,
        Heights: TinyHeights(),
        Meshes:
        [
            new MeshData(
                MeshId: 1,
                Vertices:
                [
                    -0.5f, 0f, 0.5f,
                    0.5f, 0f, 0.5f,
                    0.5f, 1f, 0.5f,
                    -0.5f, 1f, 0.5f,
                ],
                Indices:
                [
                    0, 1, 2,
                    0, 2, 3,
                    0, 0, 1,
                ]),
        ]);

    /// <summary><see cref="TinyWorld"/>'s heightfield with no meshes (the <c>heightfield-only</c> case).</summary>
    public static CollisionData HeightfieldOnly() => TinyWorld() with { Meshes = [] };

    /// <summary>
    /// A flat heightfield, every height 0, of <paramref name="samples"/> × <paramref name="samples"/>
    /// samples from origin (−32, −32), with no meshes. With the defaults the playable area is
    /// [−32, 32] in X and in Z.
    /// </summary>
    public static CollisionData Flat(int samples = 65, float cellSize = 1f, float gridCellSize = 4f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);
        return new CollisionData(
            OriginX: -32f,
            OriginZ: -32f,
            CellSize: cellSize,
            GridCellSize: gridCellSize,
            Columns: samples,
            Rows: samples,
            Heights: new float[samples * samples],
            Meshes: []);
    }

    /// <summary>The world built from <paramref name="data"/>'s encoded file: <c>Decode(Encode(data))</c>.</summary>
    public static CollisionWorld Load(CollisionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return CollisionFile.Decode(CollisionFile.Encode(data));
    }

    private static float[] TinyHeights() =>
    [
        0f, 0f, 0f,
        0f, 0.25f, 0f,
        0f, 0f, NegativeZero,
    ];
}
