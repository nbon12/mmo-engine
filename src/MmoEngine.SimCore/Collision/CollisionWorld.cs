using System;
using MmoEngine.SimCore.Formats;
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
    /// <c>2·(⌈w/cellSize⌉+1)² + 64·(⌈w/gridCellSize⌉+1)²</c>, the heightfield cells such a box can overlap
    /// with their two triangles each, plus the grid cells it can overlap with at most 64 triangles each. A
    /// <c>SimWorld</c> sizes its candidate buffer from it for the widest box its capsule can sweep.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="boxWidth"/> is negative or not finite.</exception>
    public int MaxQueryCandidates(float boxWidth)
    {
        if (!float.IsFinite(boxWidth) || boxWidth < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(boxWidth), boxWidth, "A box width is finite and not negative.");
        }

        var heightfieldCells = checked(SimMath.TruncateToInt(System.MathF.Ceiling(boxWidth / Heightfield.CellSize)) + 1);
        var gridCells = checked(SimMath.TruncateToInt(System.MathF.Ceiling(boxWidth / Grid.CellSize)) + 1);
        return checked((TrianglesPerHeightfieldCell * heightfieldCells * heightfieldCells) + (MaxTrianglesPerCell * gridCells * gridCells));
    }

    /// <summary>
    /// Builds the world from checked primary data, in file order: the heightfield, the mesh triangles
    /// with their normals and bounding boxes, then the grid. The world keeps <paramref name="data"/>, which
    /// the decoder created for it and nothing else holds.
    /// </summary>
    internal static CollisionWorld Create(CollisionData data, ulong checksum)
    {
        ArgumentNullException.ThrowIfNull(data);
        var heightfield = new Heightfield(data.OriginX, data.OriginZ, data.CellSize, data.Columns, data.Rows, data.Heights);
        var meshes = MeshSet.Build(data.Meshes, heightfield.TriangleCount);
        var grid = UniformGrid.Build(heightfield, meshes, data.GridCellSize);
        return new CollisionWorld(data, checksum, heightfield, meshes, grid);
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
