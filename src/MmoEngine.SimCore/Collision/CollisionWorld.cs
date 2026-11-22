using System;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// One collision world: a heightfield, static triangle meshes and the uniform grid over the meshes
/// (FR-004, specs/015-shared-simulation-core/data-model.md#collision-world). It is built only by
/// <see cref="CollisionFile.Decode"/> from one <c>MMCD</c> file, in file order, so the same file gives the
/// same world in every build. Its identity is the file's trailer checksum. It is immutable once built and
/// is shared read-only by any thread without locks. Its queries are partial files: the box query
/// (Phase 3) and the segment query (Phase 7).
/// </summary>
public sealed partial class CollisionWorld
{
    /// <summary>The most mesh triangles one grid cell may hold (contracts/collision-data-format.md#limits).</summary>
    public const int MaxTrianglesPerCell = 64;

    /// <summary>
    /// How far, in metres, a query box's computed edges and cell indices may reach past its nominal width
    /// through float rounding (<see cref="MaxQueryCandidates"/>). Coordinates are at most 8192 m, where a
    /// float step is about 0.001 m, so a few roundings stay well under 1/64 m (0.0156 m), and the smallest
    /// cell is 0.25 m.
    /// </summary>
    internal const float QueryWidthMargin = 1f / 64f;

    /// <summary>The heightfield triangles per heightfield cell.</summary>
    private const int TrianglesPerHeightfieldCell = 2;

    private readonly CollisionData _data;

    private CollisionWorld(CollisionData data, ulong checksum, Heightfield heightfield, MeshSet meshes, UniformGrid grid)
    {
        _data = data;
        Checksum = checksum;
        Heightfield = heightfield;
        Meshes = meshes;
        Grid = grid;
    }

    /// <summary>The file's XxHash64 trailer: the world's identity, by which a recorded stream names it (FR-021).</summary>
    public ulong Checksum { get; }

    /// <summary>
    /// The primary data the world was built from, so a decoded file re-encodes to the same bytes. Each read
    /// returns a new deep copy, so no caller can change the world's.
    /// </summary>
    public CollisionData Data => _data with
    {
        Heights = (float[])_data.Heights.Clone(),
        Meshes = CopyMeshes(_data.Meshes),
    };

    /// <summary>The terrain, its triangles generated on demand, and the playable area's boundary planes.</summary>
    public Heightfield Heightfield { get; }

    /// <summary>The mesh triangles with their normals and bounding boxes.</summary>
    public MeshSet Meshes { get; }

    /// <summary>The uniform grid over the kept mesh triangles.</summary>
    public UniformGrid Grid { get; }

    /// <summary>The number of heightfield triangles; their IDs are 0 to this − 1.</summary>
    public int HeightfieldTriangleCount => Heightfield.TriangleCount;

    /// <summary>The number of mesh triangles in the file, degenerate ones included; their IDs follow the heightfield's.</summary>
    public int MeshTriangleCount => Meshes.TriangleCount;

    /// <summary>The mesh triangles dropped at load as degenerate; their IDs stay unused.</summary>
    public int DegenerateTriangleCount => Meshes.DegenerateTriangleCount;

    /// <summary>The number of grid cells along X.</summary>
    public int GridCellsX => Grid.CellsX;

    /// <summary>The number of grid cells along Z.</summary>
    public int GridCellsZ => Grid.CellsZ;

    /// <summary>The terrain height under (x, z), by the fixed diagonal rule (<see cref="Heightfield.HeightAt"/>).</summary>
    public float HeightAt(float x, float z) => Heightfield.HeightAt(x, z);

    /// <summary>
    /// The most candidates a box query of width <paramref name="boxWidth"/> can return:
    /// <c>2·n(cellSize)² + 64·n(gridCellSize)²</c> with <c>n(c) = ⌊(w + 1/64)/c⌋ + 2</c>, the heightfield
    /// cells such a box can overlap per axis with their two triangles each, plus the grid cells it can
    /// overlap with at most 64 triangles each. An interval of real length L overlaps at most ⌊L/c⌋ + 2
    /// cells; <see cref="QueryWidthMargin"/> covers the float rounding that makes a computed box, and its
    /// cells, reach a little past w (without it, a whole-number ratio w/c falls one cell short per axis).
    /// A <c>SimWorld</c> sizes its candidate buffer from it for the widest box its capsule can sweep.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boxWidth"/> is negative or not finite.</exception>
    public int MaxQueryCandidates(float boxWidth)
    {
        if (!float.IsFinite(boxWidth) || boxWidth < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(boxWidth), boxWidth, "A box width is finite and not negative.");
        }

        var reach = boxWidth + QueryWidthMargin;
        var heightfieldCells = checked(SimMath.FloorToInt(reach / Heightfield.CellSize) + 2);
        var gridCells = checked(SimMath.FloorToInt(reach / Grid.CellSize) + 2);
        return checked((TrianglesPerHeightfieldCell * heightfieldCells * heightfieldCells) + (MaxTrianglesPerCell * gridCells * gridCells));
    }

    /// <summary>
    /// Triangle <paramref name="id"/>'s vertices and unit normal: a heightfield triangle's are generated on
    /// demand, its normal <c>(AB × AC) / SimMath.Sqrt(|AB × AC|²)</c> (it always faces +Y), and a mesh
    /// triangle's are the ones derived at load. The ID must be one a query returned: a heightfield ID or a
    /// kept mesh ID.
    /// </summary>
    internal void GetTriangle(int id, out Vec3 a, out Vec3 b, out Vec3 c, out Vec3 normal)
    {
        if (id < Heightfield.TriangleCount)
        {
            Heightfield.GetTriangle(id, out a, out b, out c);
            var cross = Vec3.Cross(b - a, c - a);
            var length = Vec3.Length(cross);
            normal = new Vec3(cross.X / length, cross.Y / length, cross.Z / length);
            return;
        }

        ref readonly var triangle = ref Meshes.TriangleAt(id - Meshes.FirstId);
        a = triangle.A;
        b = triangle.B;
        c = triangle.C;
        normal = triangle.Normal;
    }

    /// <summary>
    /// Builds the world from checked primary data, in file order: the heightfield, the mesh triangles
    /// with their normals and bounding boxes, then the grid. The world keeps <paramref name="data"/>, which
    /// the decoder created for it and nothing else holds. When the grid's counting pass breaks a limit
    /// (check 9 of contracts/collision-data-format.md#check-order), no world is constructed: it returns null
    /// and <paramref name="overflow"/> says where, for the decoder to report.
    /// </summary>
    internal static CollisionWorld? TryCreate(CollisionData data, ulong checksum, out GridOverflow overflow)
    {
        ArgumentNullException.ThrowIfNull(data);
        var heightfield = new Heightfield(data.OriginX, data.OriginZ, data.CellSize, data.Columns, data.Rows, data.Heights);
        var meshes = MeshSet.Build(data.Meshes, heightfield.TriangleCount);
        var grid = UniformGrid.TryBuild(heightfield, meshes, data.GridCellSize, out overflow);
        return grid is null ? null : new CollisionWorld(data, checksum, heightfield, meshes, grid);
    }

    private static MeshData[] CopyMeshes(MeshData[] meshes)
    {
        var copy = new MeshData[meshes.Length];
        for (var m = 0; m < meshes.Length; m++)
        {
            copy[m] = new MeshData(meshes[m].MeshId, (float[])meshes[m].Vertices.Clone(), (uint[])meshes[m].Indices.Clone());
        }

        return copy;
    }
}
