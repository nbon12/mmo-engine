using System;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// The collision world's terrain: a regular grid of height samples
/// (specs/015-shared-simulation-core/data-model.md#heightfield,
/// contracts/collision-data-format.md#heightfield). Sample (i, j) is the vertex
/// <c>(OriginX + (float)i*CellSize, heights[j*Columns + i], OriginZ + (float)j*CellSize)</c>. That one
/// expression is used everywhere, so a vertex shared by neighbouring cells is bit-identical and the surface
/// is watertight. Each cell splits along its fixed v00 → v11 diagonal into triangle k = 0
/// (v00, v11, v10), used where u ≥ w, and k = 1 (v00, v01, v11); triangle ID = 2·(j·(Columns − 1) + i) + k.
/// Triangles are generated on demand and never stored. The playable area is the heightfield's X–Z extent,
/// bounded by four vertical boundary planes with IDs −4 to −1. It is immutable.
/// </summary>
public sealed class Heightfield
{
    /// <summary>The boundary plane x = <see cref="MinX"/>, normal +X.</summary>
    public const int BoundaryMinX = -4;

    /// <summary>The boundary plane x = <see cref="MaxX"/>, normal −X.</summary>
    public const int BoundaryMaxX = -3;

    /// <summary>The boundary plane z = <see cref="MinZ"/>, normal +Z.</summary>
    public const int BoundaryMinZ = -2;

    /// <summary>The boundary plane z = <see cref="MaxZ"/>, normal −Z.</summary>
    public const int BoundaryMaxZ = -1;

    private readonly float[] _heights;

    /// <summary>
    /// A heightfield over <paramref name="heights"/>, which it keeps without copying: the collision world
    /// that builds it owns the array and never changes it.
    /// </summary>
    internal Heightfield(float originX, float originZ, float cellSize, int columns, int rows, float[] heights)
    {
        OriginX = originX;
        OriginZ = originZ;
        CellSize = cellSize;
        Columns = columns;
        Rows = rows;
        _heights = heights;
        TriangleCount = 2 * (columns - 1) * (rows - 1);
        MinX = originX;
        MaxX = originX + ((float)(columns - 1) * cellSize);
        MinZ = originZ;
        MaxZ = originZ + ((float)(rows - 1) * cellSize);
    }

    /// <summary>X of sample (0, 0), in metres.</summary>
    public float OriginX { get; }

    /// <summary>Z of sample (0, 0), in metres.</summary>
    public float OriginZ { get; }

    /// <summary>The sample spacing in X and Z, in metres.</summary>
    public float CellSize { get; }

    /// <summary>The number of samples along X.</summary>
    public int Columns { get; }

    /// <summary>The number of samples along Z.</summary>
    public int Rows { get; }

    /// <summary>The number of heightfield triangles, 2·(Columns − 1)·(Rows − 1); their IDs are 0 to this − 1.</summary>
    public int TriangleCount { get; }

    /// <summary>The playable area's least X: <see cref="OriginX"/>.</summary>
    public float MinX { get; }

    /// <summary>The playable area's greatest X: <c>OriginX + (float)(Columns − 1)*CellSize</c>.</summary>
    public float MaxX { get; }

    /// <summary>The playable area's least Z: <see cref="OriginZ"/>.</summary>
    public float MinZ { get; }

    /// <summary>The playable area's greatest Z: <c>OriginZ + (float)(Rows − 1)*CellSize</c>.</summary>
    public float MaxZ { get; }

    /// <summary>
    /// Sample (i, j) by the one sample expression:
    /// <c>(OriginX + (float)i*CellSize, heights[j*Columns + i], OriginZ + (float)j*CellSize)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The sample is outside the grid.</exception>
    public Vec3 Sample(int i, int j)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, Columns);
        ArgumentOutOfRangeException.ThrowIfNegative(j);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(j, Rows);
        return SampleUnchecked(i, j);
    }

    /// <summary>
    /// Triangle <paramref name="id"/>'s vertices in order: for cell (i, j) and k = id mod 2, k = 0 is
    /// (v00, v11, v10) and k = 1 is (v00, v01, v11), with i = (id / 2) mod (Columns − 1) and
    /// j = (id / 2) / (Columns − 1).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a heightfield triangle ID.</exception>
    public void GetTriangle(int id, out Vec3 a, out Vec3 b, out Vec3 c)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, TriangleCount);
        var cell = id >> 1;
        var cellsX = Columns - 1;
        var i = cell % cellsX;
        var j = cell / cellsX;
        a = SampleUnchecked(i, j);
        if ((id & 1) == 0)
        {
            b = SampleUnchecked(i + 1, j + 1);
            c = SampleUnchecked(i + 1, j);
        }
        else
        {
            b = SampleUnchecked(i, j + 1);
            c = SampleUnchecked(i + 1, j + 1);
        }
    }

    /// <summary>
    /// The terrain height under (x, z), by the fixed diagonal rule. The cell is
    /// <c>clamp(FloorToInt((x − OriginX) / CellSize), 0, Columns − 2)</c> and likewise in Z, and
    /// u = (x − x00) / CellSize and w = (z − z00) / CellSize are measured from its v00 sample. Where u ≥ w the
    /// height is triangle k = 0's, <c>(h00 + (u*(h10 − h00))) + (w*(h11 − h10))</c>; otherwise k = 1's,
    /// <c>(h00 + (w*(h01 − h00))) + (u*(h11 − h01))</c>. Outside the playable area it extends the edge
    /// cell's plane. Used for spawn placement and tests.
    /// </summary>
    public float HeightAt(float x, float z)
    {
        var i = System.Math.Clamp(SimMath.FloorToInt((x - OriginX) / CellSize), 0, Columns - 2);
        var j = System.Math.Clamp(SimMath.FloorToInt((z - OriginZ) / CellSize), 0, Rows - 2);
        var v00 = SampleUnchecked(i, j);
        var u = (x - v00.X) / CellSize;
        var w = (z - v00.Z) / CellSize;
        var h00 = v00.Y;
        var h11 = HeightUnchecked(i + 1, j + 1);
        if (u >= w)
        {
            var h10 = HeightUnchecked(i + 1, j);
            return (h00 + (u * (h10 - h00))) + (w * (h11 - h10));
        }

        var h01 = HeightUnchecked(i, j + 1);
        return (h00 + (w * (h01 - h00))) + (u * (h11 - h01));
    }

    /// <summary>
    /// Boundary plane <paramref name="id"/> (−4 to −1): the points p with
    /// <c>Vec3.Dot(normal, p) == distance</c>, the normal facing into the playable area. −4 is x = MinX
    /// (normal +X, distance MinX), −3 is x = MaxX (normal −X, distance −MaxX), −2 is z = MinZ (normal +Z,
    /// distance MinZ) and −1 is z = MaxZ (normal −Z, distance −MaxZ).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a boundary plane ID.</exception>
    public void GetBoundaryPlane(int id, out Vec3 normal, out float distance)
    {
        switch (id)
        {
            case BoundaryMinX:
                normal = new Vec3(1f, 0f, 0f);
                distance = MinX;
                break;
            case BoundaryMaxX:
                normal = new Vec3(-1f, 0f, 0f);
                distance = -MaxX;
                break;
            case BoundaryMinZ:
                normal = new Vec3(0f, 0f, 1f);
                distance = MinZ;
                break;
            case BoundaryMaxZ:
                normal = new Vec3(0f, 0f, -1f);
                distance = -MaxZ;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, "A boundary plane ID is -4 to -1.");
        }
    }

    /// <summary>Sample (i, j) by the one sample expression, without the range check: the caller keeps i and j inside the grid.</summary>
    internal Vec3 SampleUnchecked(int i, int j) =>
        new(OriginX + ((float)i * CellSize), _heights[(j * Columns) + i], OriginZ + ((float)j * CellSize));

    /// <summary>Sample (i, j)'s height, without the range check: the caller keeps i and j inside the grid.</summary>
    internal float HeightUnchecked(int i, int j) => _heights[(j * Columns) + i];
}
