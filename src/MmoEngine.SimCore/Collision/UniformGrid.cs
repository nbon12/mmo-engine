using System;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// The uniform grid over the mesh triangles (specs/015-shared-simulation-core/data-model.md#uniform-grid,
/// contracts/collision-data-format.md#derived-data-built-at-load). It covers the heightfield's extent in
/// <see cref="CellSize"/> cells, <c>CellsX = max(1, ⌈extentX / CellSize⌉)</c> by
/// <c>CellsZ = max(1, ⌈extentZ / CellSize⌉)</c>, computed in float. Cell (cx, cz) is index
/// <c>cz·CellsX + cx</c>. Every kept triangle is registered in every cell its bounding box overlaps, by the
/// one clamped cell function <see cref="CellX"/> / <see cref="CellZ"/>, which registration and queries
/// share, so a triangle outside the playable area falls into an edge cell and is still found. The lists
/// are in CSR form and ascending, because cells are filled visiting triangles in ID order. It is immutable.
/// Heightfield triangles are not in it.
/// </summary>
public sealed class UniformGrid
{
    private readonly int[] _cellStart;
    private readonly int[] _cellTriangles;

    private UniformGrid(float originX, float originZ, float cellSize, int cellsX, int cellsZ, int[] cellStart, int[] cellTriangles)
    {
        OriginX = originX;
        OriginZ = originZ;
        CellSize = cellSize;
        CellsX = cellsX;
        CellsZ = cellsZ;
        _cellStart = cellStart;
        _cellTriangles = cellTriangles;
    }

    /// <summary>X of the grid's corner: the heightfield's <c>OriginX</c>.</summary>
    public float OriginX { get; }

    /// <summary>Z of the grid's corner: the heightfield's <c>OriginZ</c>.</summary>
    public float OriginZ { get; }

    /// <summary>The grid cell size in metres (the file's <c>gridCellSize</c>).</summary>
    public float CellSize { get; }

    /// <summary>The number of cells along X.</summary>
    public int CellsX { get; }

    /// <summary>The number of cells along Z.</summary>
    public int CellsZ { get; }

    /// <summary>
    /// <c>CellsX·CellsZ + 1</c> offsets into <see cref="CellTriangles"/>: cell c's triangles are
    /// <c>CellTriangles[CellStart[c]..CellStart[c + 1]]</c>.
    /// </summary>
    public ReadOnlySpan<int> CellStart => _cellStart;

    /// <summary>Every cell's mesh triangle IDs, cell after cell, each cell's list ascending.</summary>
    public ReadOnlySpan<int> CellTriangles => _cellTriangles;

    /// <summary>The cell column of <paramref name="x"/>: <c>clamp(FloorToInt((x − OriginX) / CellSize), 0, CellsX − 1)</c>.</summary>
    public int CellX(float x) => System.Math.Clamp(SimMath.FloorToInt((x - OriginX) / CellSize), 0, CellsX - 1);

    /// <summary>The cell row of <paramref name="z"/>: <c>clamp(FloorToInt((z − OriginZ) / CellSize), 0, CellsZ − 1)</c>.</summary>
    public int CellZ(float z) => System.Math.Clamp(SimMath.FloorToInt((z - OriginZ) / CellSize), 0, CellsZ - 1);

    /// <summary>
    /// The grid over <paramref name="heightfield"/>'s extent, with <paramref name="meshes"/>' kept triangles
    /// registered: a counting pass in ID order, the prefix sums into <c>CellStart</c>, then a filling pass in
    /// ID order, so every cell's list is ascending.
    /// </summary>
    internal static UniformGrid Build(Heightfield heightfield, MeshSet meshes, float cellSize)
    {
        var extentX = (float)(heightfield.Columns - 1) * heightfield.CellSize;
        var extentZ = (float)(heightfield.Rows - 1) * heightfield.CellSize;
        var cellsX = System.Math.Max(1, SimMath.TruncateToInt(System.MathF.Ceiling(extentX / cellSize)));
        var cellsZ = System.Math.Max(1, SimMath.TruncateToInt(System.MathF.Ceiling(extentZ / cellSize)));
        var cellStart = new int[checked((cellsX * cellsZ) + 1)];
        var grid = new UniformGrid(heightfield.OriginX, heightfield.OriginZ, cellSize, cellsX, cellsZ, cellStart, []);

        // Counting pass, in ID order: cellStart[c + 1] counts cell c's registrations.
        var total = 0;
        for (var index = 0; index < meshes.TriangleCount; index++)
        {
            if (!meshes.IsKept(index))
            {
                continue;
            }

            grid.Cover(in meshes.TriangleAt(index), out var cx0, out var cz0, out var cx1, out var cz1);
            for (var cz = cz0; cz <= cz1; cz++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    cellStart[(cz * cellsX) + cx + 1]++;
                    total = checked(total + 1);
                }
            }
        }

        for (var c = 0; c < cellsX * cellsZ; c++)
        {
            cellStart[c + 1] += cellStart[c];
        }

        // Filling pass, in ID order, so every cell's list is ascending.
        var cellTriangles = new int[total];
        var next = new int[cellsX * cellsZ];
        Array.Copy(cellStart, next, next.Length);
        for (var index = 0; index < meshes.TriangleCount; index++)
        {
            if (!meshes.IsKept(index))
            {
                continue;
            }

            var id = meshes.FirstId + index;
            grid.Cover(in meshes.TriangleAt(index), out var cx0, out var cz0, out var cx1, out var cz1);
            for (var cz = cz0; cz <= cz1; cz++)
            {
                for (var cx = cx0; cx <= cx1; cx++)
                {
                    var cell = (cz * cellsX) + cx;
                    cellTriangles[next[cell]] = id;
                    next[cell]++;
                }
            }
        }

        return new UniformGrid(heightfield.OriginX, heightfield.OriginZ, cellSize, cellsX, cellsZ, cellStart, cellTriangles);
    }

    /// <summary>The range of cells a triangle's bounding box overlaps, by the one clamped cell function.</summary>
    private void Cover(in MeshTriangle triangle, out int cx0, out int cz0, out int cx1, out int cz1)
    {
        cx0 = CellX(triangle.Min.X);
        cz0 = CellZ(triangle.Min.Z);
        cx1 = CellX(triangle.Max.X);
        cz1 = CellZ(triangle.Max.Z);
    }
}
