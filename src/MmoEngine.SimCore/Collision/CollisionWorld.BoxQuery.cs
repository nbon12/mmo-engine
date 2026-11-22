using System;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>The box query (specs/015-shared-simulation-core/data-model.md#collisionworld).</summary>
public sealed partial class CollisionWorld
{
    /// <summary>
    /// Writes into <paramref name="output"/> the IDs of the triangles that may touch the box
    /// [<paramref name="minX"/>, <paramref name="maxX"/>] × [<paramref name="minY"/>, <paramref name="maxY"/>]
    /// × [<paramref name="minZ"/>, <paramref name="maxZ"/>] and returns how many it wrote, ascending and
    /// without duplicates. First the heightfield cells the box overlaps, j outer and i inner, both triangles
    /// of each, skipping a cell whose four corner heights all lie below <paramref name="minY"/> or all above
    /// <paramref name="maxY"/>; then the uniform grid's cells the box overlaps, through the one clamped cell
    /// function, keeping a kept mesh triangle only when its bounding box overlaps the box and only in the
    /// cell that holds (max(tri.minX, minX), max(tri.minZ, minZ)), so a triangle registered in several of
    /// those cells is written once. Overlap is inclusive. The mesh IDs are merged into ascending order as
    /// they are found. It reads no mutable state, so any thread may call it.
    /// </summary>
    /// <returns>
    /// The count, or −1 when the result would not fit in <paramref name="output"/>: the query never
    /// truncates, and the caller refuses the step with <c>QueryCapacityExceeded</c>. The span's contents are
    /// then unspecified.
    /// </returns>
    public int QueryBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, Span<int> output)
    {
        var count = 0;

        // The heightfield cells overlapping the box in X and Z, if any.
        var heightfield = Heightfield;
        var lastCellX = heightfield.Columns - 2;
        var lastCellZ = heightfield.Rows - 2;
        var i0 = SimMath.FloorToInt((minX - heightfield.OriginX) / heightfield.CellSize);
        var i1 = SimMath.FloorToInt((maxX - heightfield.OriginX) / heightfield.CellSize);
        var j0 = SimMath.FloorToInt((minZ - heightfield.OriginZ) / heightfield.CellSize);
        var j1 = SimMath.FloorToInt((maxZ - heightfield.OriginZ) / heightfield.CellSize);
        if (i1 >= 0 && i0 <= lastCellX && j1 >= 0 && j0 <= lastCellZ)
        {
            i0 = System.Math.Max(i0, 0);
            i1 = System.Math.Min(i1, lastCellX);
            j0 = System.Math.Max(j0, 0);
            j1 = System.Math.Min(j1, lastCellZ);
            var cellsX = lastCellX + 1;
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var h00 = heightfield.HeightUnchecked(i, j);
                    var h10 = heightfield.HeightUnchecked(i + 1, j);
                    var h01 = heightfield.HeightUnchecked(i, j + 1);
                    var h11 = heightfield.HeightUnchecked(i + 1, j + 1);
                    var low = SimMath.Min(SimMath.Min(h00, h10), SimMath.Min(h01, h11));
                    var high = SimMath.Max(SimMath.Max(h00, h10), SimMath.Max(h01, h11));
                    if (high < minY || low > maxY)
                    {
                        continue;
                    }

                    if (count + TrianglesPerHeightfieldCell > output.Length)
                    {
                        return -1;
                    }

                    var id = TrianglesPerHeightfieldCell * ((j * cellsX) + i);
                    output[count] = id;
                    output[count + 1] = id + 1;
                    count += TrianglesPerHeightfieldCell;
                }
            }
        }

        // The grid cells overlapping the box, by the one clamped cell function.
        var grid = Grid;
        var meshes = Meshes;
        var cellStart = grid.CellStart;
        var cellTriangles = grid.CellTriangles;
        var cx0 = grid.CellX(minX);
        var cx1 = grid.CellX(maxX);
        var cz0 = grid.CellZ(minZ);
        var cz1 = grid.CellZ(maxZ);
        var firstMesh = count;
        for (var cz = cz0; cz <= cz1; cz++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var cell = (cz * grid.CellsX) + cx;
                for (var k = cellStart[cell]; k < cellStart[cell + 1]; k++)
                {
                    var id = cellTriangles[k];
                    ref readonly var triangle = ref meshes.TriangleAt(id - meshes.FirstId);
                    if (triangle.Min.X > maxX || triangle.Max.X < minX
                        || triangle.Min.Y > maxY || triangle.Max.Y < minY
                        || triangle.Min.Z > maxZ || triangle.Max.Z < minZ)
                    {
                        continue;
                    }

                    // The reference point: the triangle is kept only in the cell that holds it.
                    if (grid.CellX(SimMath.Max(triangle.Min.X, minX)) != cx || grid.CellZ(SimMath.Max(triangle.Min.Z, minZ)) != cz)
                    {
                        continue;
                    }

                    if (count == output.Length)
                    {
                        return -1;
                    }

                    // Insert into the ascending mesh IDs found so far; one cell's list is ascending, so the
                    // insertion usually lands at the end.
                    var at = count;
                    while (at > firstMesh && output[at - 1] > id)
                    {
                        output[at] = output[at - 1];
                        at--;
                    }

                    output[at] = id;
                    count++;
                }
            }
        }

        return count;
    }
}
