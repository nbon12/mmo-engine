using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Projectiles;

namespace MmoEngine.SimCore.Collision;

/// <summary>The segment query (specs/015-shared-simulation-core/data-model.md#collisionworld).</summary>
public sealed partial class CollisionWorld
{
    /// <summary>
    /// The first heightfield or mesh triangle that segment <paramref name="a"/> → <paramref name="b"/>
    /// meets, for a projectile's step (FR-010). It visits, slab by slab along Z, the heightfield cells and
    /// then the uniform-grid cells that the segment's X–Z projection crosses, and tests their triangles:
    /// the two on-demand triangles of each heightfield cell whose four corner heights do not all lie above
    /// or all below the segment's height range there, and each grid cell's mesh triangles whose bounding
    /// box meets the segment's box there. Every slab, cell range and height range is widened by
    /// <see cref="QueryWidthMargin"/>, so float rounding of the walk can only add cells, never skip one the
    /// segment crosses; a triangle reached through several cells is tested again, to the same result.
    /// Each test is double-sided with inclusive edges (<see cref="SegmentTriangle"/>), in the local frame
    /// of <see cref="ClosestPoints.LocalOrigin"/> of <paramref name="a"/>. The earliest hit wins, ties to
    /// the lower triangle ID; IDs below the heightfield triangle count are Terrain, the rest Mesh. The
    /// boundary planes (−4 to −1) are never tested: a projectile leaves the world by its OutOfBounds limit
    /// instead. Every loop is bounded by the heightfield's or the grid's cell counts and the 64-triangle
    /// cell limit; it allocates nothing and reads no mutable state, so any thread may call it.
    /// </summary>
    /// <returns>True when the segment meets a triangle; <paramref name="hit"/> is then the first one.</returns>
    public bool SegmentQuery(Vec3 a, Vec3 b, out SegmentHit hit)
    {
        var direction = b - a;
        var origin = ClosestPoints.LocalOrigin(a.X, a.Z);
        var start = a - origin;
        var along = (b - origin) - start;
        var zMin = SimMath.Min(a.Z, b.Z) - QueryWidthMargin;
        var zMax = SimMath.Max(a.Z, b.Z) + QueryWidthMargin;
        var bestT = 0f;
        var bestId = -1;

        // The heightfield cells, row by row (j ascending, then i ascending).
        var heightfield = Heightfield;
        var cellSize = heightfield.CellSize;
        var lastCellX = heightfield.Columns - 2;
        var lastCellZ = heightfield.Rows - 2;
        var j0 = SimMath.FloorToInt((zMin - heightfield.OriginZ) / cellSize);
        var j1 = SimMath.FloorToInt((zMax - heightfield.OriginZ) / cellSize);
        if (j1 >= 0 && j0 <= lastCellZ)
        {
            j0 = System.Math.Max(j0, 0);
            j1 = System.Math.Min(j1, lastCellZ);
            var cellsX = lastCellX + 1;
            for (var j = j0; j <= j1; j++)
            {
                var slabMin = heightfield.OriginZ + ((float)j * cellSize);
                var slabMax = heightfield.OriginZ + ((float)(j + 1) * cellSize);
                var piece = Piece(a, direction, slabMin, slabMax);
                var i0 = SimMath.FloorToInt((piece.MinX - heightfield.OriginX) / cellSize);
                var i1 = SimMath.FloorToInt((piece.MaxX - heightfield.OriginX) / cellSize);
                if (i1 < 0 || i0 > lastCellX)
                {
                    continue;
                }

                i0 = System.Math.Max(i0, 0);
                i1 = System.Math.Min(i1, lastCellX);
                for (var i = i0; i <= i1; i++)
                {
                    var h00 = heightfield.HeightUnchecked(i, j);
                    var h10 = heightfield.HeightUnchecked(i + 1, j);
                    var h01 = heightfield.HeightUnchecked(i, j + 1);
                    var h11 = heightfield.HeightUnchecked(i + 1, j + 1);
                    var low = SimMath.Min(SimMath.Min(h00, h10), SimMath.Min(h01, h11));
                    var high = SimMath.Max(SimMath.Max(h00, h10), SimMath.Max(h01, h11));
                    if (high < piece.MinY || low > piece.MaxY)
                    {
                        continue;
                    }

                    // The cell's corners by the one sample expression, so shared vertices are bit-identical.
                    var v00 = heightfield.SampleUnchecked(i, j) - origin;
                    var v10 = heightfield.SampleUnchecked(i + 1, j) - origin;
                    var v01 = heightfield.SampleUnchecked(i, j + 1) - origin;
                    var v11 = heightfield.SampleUnchecked(i + 1, j + 1) - origin;
                    var id = TrianglesPerHeightfieldCell * ((j * cellsX) + i);
                    Consider(SegmentTriangle(start, along, v00, v11, v10, out var t0), t0, id, ref bestT, ref bestId);
                    Consider(SegmentTriangle(start, along, v00, v01, v11, out var t1), t1, id + 1, ref bestT, ref bestId);
                }
            }
        }

        // The grid cells, row by row, through the one clamped cell function: the edge rows and columns also
        // hold the triangles beyond the playable area, so their slabs reach to infinity on the outer side.
        var grid = Grid;
        var meshes = Meshes;
        var cellStart = grid.CellStart;
        var cellTriangles = grid.CellTriangles;
        var cz0 = grid.CellZ(zMin);
        var cz1 = grid.CellZ(zMax);
        for (var cz = cz0; cz <= cz1; cz++)
        {
            var slabMin = cz == 0 ? float.NegativeInfinity : grid.OriginZ + ((float)cz * grid.CellSize);
            var slabMax = cz == grid.CellsZ - 1 ? float.PositiveInfinity : grid.OriginZ + ((float)(cz + 1) * grid.CellSize);
            var piece = Piece(a, direction, slabMin, slabMax);
            var cx0 = grid.CellX(piece.MinX);
            var cx1 = grid.CellX(piece.MaxX);
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var cell = (cz * grid.CellsX) + cx;
                for (var k = cellStart[cell]; k < cellStart[cell + 1]; k++)
                {
                    var id = cellTriangles[k];
                    ref readonly var triangle = ref meshes.TriangleAt(id - meshes.FirstId);
                    if (triangle.Min.X > piece.MaxX || triangle.Max.X < piece.MinX
                        || triangle.Min.Y > piece.MaxY || triangle.Max.Y < piece.MinY
                        || triangle.Min.Z > piece.MaxZ || triangle.Max.Z < piece.MinZ)
                    {
                        continue;
                    }

                    var hits = SegmentTriangle(start, along, triangle.A - origin, triangle.B - origin, triangle.C - origin, out var t);
                    Consider(hits, t, id, ref bestT, ref bestId);
                }
            }
        }

        hit = bestId < 0 ? default : new SegmentHit(bestT, bestId, bestId < Heightfield.TriangleCount ? HitKind.Terrain : HitKind.Mesh);
        return bestId >= 0;
    }

    /// <summary>
    /// Whether segment <paramref name="p"/> → p + <paramref name="pq"/> meets triangle
    /// (<paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>), from either side, and the fraction
    /// t at which it meets its plane. The line is inside the triangle when the three signed volumes
    /// <c>pq·(pc × pb)</c>, <c>pq·(pa × pc)</c> and <c>pq·(pb × pa)</c> (Ericson, <i>Real-Time Collision
    /// Detection</i>, §5.3.4, with pa = a − p and so on) are all ≥ 0 or all ≤ 0, so both windings hit and
    /// an edge (a zero volume) belongs to the triangle. Each volume is a function of one edge's two vertices
    /// alone, and swapping them negates it exactly in float, so two triangles that share an edge, with the
    /// shared vertices' bits, never both reject a line through it: the surface is watertight. A line in
    /// the triangle's plane (all three volumes zero) misses. t = (n·pa) / (n·pq) with n = (b − a) × (c − a),
    /// kept when it lies in [0, 1] and written as +0 when it is −0.
    /// </summary>
    private static bool SegmentTriangle(Vec3 p, Vec3 pq, Vec3 a, Vec3 b, Vec3 c, out float t)
    {
        var pa = a - p;
        var pb = b - p;
        var pc = c - p;
        var u = Vec3.Dot(pq, Vec3.Cross(pc, pb));
        var v = Vec3.Dot(pq, Vec3.Cross(pa, pc));
        var w = Vec3.Dot(pq, Vec3.Cross(pb, pa));
        var inside = (u >= 0f && v >= 0f && w >= 0f) || (u <= 0f && v <= 0f && w <= 0f);
        var inPlane = u == 0f && v == 0f && w == 0f;
        var normal = Vec3.Cross(b - a, c - a);
        var denominator = Vec3.Dot(normal, pq);
        if (!inside || inPlane || denominator == 0f)
        {
            t = 0f;
            return false;
        }

        var fraction = Vec3.Dot(normal, pa) / denominator;
        t = fraction + 0f;
        return fraction >= 0f && fraction <= 1f;
    }

    /// <summary>Keeps a hit at <paramref name="t"/> on <paramref name="id"/> when it is earlier, or as early with a lower ID.</summary>
    private static void Consider(bool hits, float t, int id, ref float bestT, ref int bestId)
    {
        if (hits && (bestId < 0 || t < bestT || (t == bestT && id < bestId)))
        {
            bestT = t;
            bestId = id;
        }
    }

    /// <summary>
    /// The box around the part of segment <paramref name="a"/> → a + <paramref name="d"/> whose Z lies in
    /// [<paramref name="slabMin"/>, <paramref name="slabMax"/>], every side widened by
    /// <see cref="QueryWidthMargin"/>. The part's fractions are clamped to [0, 1], so a slab the segment does
    /// not enter gives the box of the nearer end; a level segment (d.Z = 0) gives the whole segment.
    /// </summary>
    private static SlabPiece Piece(Vec3 a, Vec3 d, float slabMin, float slabMax)
    {
        var low = 0f;
        var high = 1f;
        if (d.Z != 0f)
        {
            var first = (slabMin - QueryWidthMargin - a.Z) / d.Z;
            var second = (slabMax + QueryWidthMargin - a.Z) / d.Z;
            low = SimMath.Clamp(SimMath.Min(first, second), 0f, 1f);
            high = SimMath.Clamp(SimMath.Max(first, second), 0f, 1f);
        }

        var x0 = a.X + (d.X * low);
        var x1 = a.X + (d.X * high);
        var y0 = a.Y + (d.Y * low);
        var y1 = a.Y + (d.Y * high);
        var z0 = a.Z + (d.Z * low);
        var z1 = a.Z + (d.Z * high);
        return new SlabPiece(
            SimMath.Min(x0, x1) - QueryWidthMargin,
            SimMath.Max(x0, x1) + QueryWidthMargin,
            SimMath.Min(y0, y1) - QueryWidthMargin,
            SimMath.Max(y0, y1) + QueryWidthMargin,
            SimMath.Min(z0, z1) - QueryWidthMargin,
            SimMath.Max(z0, z1) + QueryWidthMargin);
    }

    /// <summary>The widened box of a segment's part within one slab of cells.</summary>
    private readonly record struct SlabPiece(float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ);
}
