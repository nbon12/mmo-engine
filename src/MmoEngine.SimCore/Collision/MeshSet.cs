using System;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// One kept mesh triangle with what is derived from it at load
/// (specs/015-shared-simulation-core/contracts/collision-data-format.md#derived-data-built-at-load):
/// its vertices <see cref="A"/>, <see cref="B"/> and <see cref="C"/> in index order, its unit normal
/// <c>(AB × AC) / SimMath.Sqrt(|AB × AC|²)</c>, and its axis-aligned bounding box. Triangles are
/// double-sided; the normal's sign only follows the vertex order.
/// </summary>
public readonly record struct MeshTriangle(Vec3 A, Vec3 B, Vec3 C, Vec3 Normal, Vec3 Min, Vec3 Max);

/// <summary>
/// The collision world's static triangle meshes (specs/015-shared-simulation-core/data-model.md#mesh-set).
/// Mesh triangle ID = the heightfield triangle count + the triangle's global index in file order. A
/// degenerate triangle (an edge shorter than 1 mm, or |AB × AC|² ≤ 1e-8·(maxEdge²)², or ≤ 4e-12) is dropped
/// and counted, and its ID stays unused, so IDs never shift. It is immutable.
/// </summary>
public sealed class MeshSet
{
    /// <summary>An edge shorter than 1 mm is degenerate: |e|² below this.</summary>
    internal const float MinEdgeLengthSquared = 1e-6f;

    /// <summary>A triangle is degenerate when |AB × AC|² ≤ this × (maxEdge²)².</summary>
    internal const float SliverFactor = 1e-8f;

    /// <summary>A triangle is degenerate when |AB × AC|² ≤ this.</summary>
    internal const float MinCrossLengthSquared = 4e-12f;

    private readonly MeshTriangle[] _triangles;
    private readonly bool[] _kept;

    private MeshSet(int firstId, MeshTriangle[] triangles, bool[] kept, int degenerateCount)
    {
        FirstId = firstId;
        _triangles = triangles;
        _kept = kept;
        DegenerateTriangleCount = degenerateCount;
    }

    /// <summary>The first mesh triangle ID: the heightfield triangle count.</summary>
    public int FirstId { get; }

    /// <summary>The number of mesh triangles in the file, degenerate ones included; IDs run from <see cref="FirstId"/>.</summary>
    public int TriangleCount => _triangles.Length;

    /// <summary>The triangles dropped at load as degenerate.</summary>
    public int DegenerateTriangleCount { get; }

    /// <summary>
    /// The kept triangle <paramref name="id"/>. False for a heightfield or boundary ID, a dropped degenerate
    /// triangle's ID, or an ID past the last mesh triangle.
    /// </summary>
    public bool TryGetMeshTriangle(int id, out MeshTriangle triangle)
    {
        var index = id - FirstId;
        if (id < FirstId || index >= _triangles.Length || !_kept[index])
        {
            triangle = default;
            return false;
        }

        triangle = _triangles[index];
        return true;
    }

    /// <summary>True when global triangle <paramref name="index"/> (ID − <see cref="FirstId"/>) was kept.</summary>
    internal bool IsKept(int index) => _kept[index];

    /// <summary>Global triangle <paramref name="index"/>'s derived data; meaningful only when it was kept.</summary>
    internal ref readonly MeshTriangle TriangleAt(int index) => ref _triangles[index];

    /// <summary>
    /// Derives every triangle of <paramref name="meshes"/> in file order: A, B and C from the mesh's
    /// vertices by index, then the degenerate test, the normal and the bounding box. The data must already
    /// be checked: every index below its mesh's vertex count.
    /// </summary>
    internal static MeshSet Build(MeshData[] meshes, int firstId)
    {
        var total = 0;
        foreach (var mesh in meshes)
        {
            total = checked(total + (mesh.Indices.Length / 3));
        }

        var triangles = new MeshTriangle[total];
        var kept = new bool[total];
        var degenerate = 0;
        var index = 0;
        foreach (var mesh in meshes)
        {
            var vertices = mesh.Vertices;
            var indices = mesh.Indices;
            var count = indices.Length / 3;
            for (var t = 0; t < count; t++)
            {
                var a = Vertex(vertices, indices[3 * t]);
                var b = Vertex(vertices, indices[(3 * t) + 1]);
                var c = Vertex(vertices, indices[(3 * t) + 2]);
                if (TryDerive(a, b, c, out var triangle))
                {
                    triangles[index] = triangle;
                    kept[index] = true;
                }
                else
                {
                    degenerate++;
                }

                index++;
            }
        }

        return new MeshSet(firstId, triangles, kept, degenerate);
    }

    /// <summary>
    /// The derived triangle, or false when it is degenerate: an edge with |e|² &lt; 1e-6, or
    /// |AB × AC|² ≤ 1e-8·(maxEdge²)², or |AB × AC|² ≤ 4e-12.
    /// </summary>
    private static bool TryDerive(Vec3 a, Vec3 b, Vec3 c, out MeshTriangle triangle)
    {
        var ab = b - a;
        var ac = c - a;
        var bc = c - b;
        var ab2 = Vec3.LengthSquared(ab);
        var ac2 = Vec3.LengthSquared(ac);
        var bc2 = Vec3.LengthSquared(bc);
        var cross = Vec3.Cross(ab, ac);
        var cross2 = Vec3.LengthSquared(cross);
        var maxEdge2 = SimMath.Max(SimMath.Max(ab2, ac2), bc2);
        if (ab2 < MinEdgeLengthSquared
            || ac2 < MinEdgeLengthSquared
            || bc2 < MinEdgeLengthSquared
            || cross2 <= SliverFactor * (maxEdge2 * maxEdge2)
            || cross2 <= MinCrossLengthSquared)
        {
            triangle = default;
            return false;
        }

        var length = SimMath.Sqrt(cross2);
        var normal = new Vec3(cross.X / length, cross.Y / length, cross.Z / length);
        var min = new Vec3(
            SimMath.Min(SimMath.Min(a.X, b.X), c.X),
            SimMath.Min(SimMath.Min(a.Y, b.Y), c.Y),
            SimMath.Min(SimMath.Min(a.Z, b.Z), c.Z));
        var max = new Vec3(
            SimMath.Max(SimMath.Max(a.X, b.X), c.X),
            SimMath.Max(SimMath.Max(a.Y, b.Y), c.Y),
            SimMath.Max(SimMath.Max(a.Z, b.Z), c.Z));
        triangle = new MeshTriangle(a, b, c, normal, min, max);
        return true;
    }

    private static Vec3 Vertex(float[] vertices, uint index)
    {
        var v = checked((int)index * 3);
        return new Vec3(vertices[v], vertices[v + 1], vertices[v + 2]);
    }
}
