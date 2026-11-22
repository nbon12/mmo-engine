using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Movement;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Small collision worlds for the Phase 3 movement tests, each built as <see cref="CollisionData"/> and
/// passed through <see cref="CollisionFile.Encode"/> then <see cref="CollisionFile.Decode"/>
/// (<see cref="TestWorlds.Load"/>), the only way a world is built (FR-004). Every world shares one layout:
/// a heightfield of 65 × 65 samples 1 m apart from origin (−32, −32), so the playable area is [−32, 32] in
/// X and in Z, with 4 m grid cells; the character starts at x = 0, z = <see cref="StartZ"/>, facing yaw 0
/// (forward +Z, right −X), at rest <see cref="ContactSkin"/> above the floor (where a sweep leaves a
/// capsule), and every feature lies ahead of it along +Z. Vertical walls are zero-thickness quads from the
/// floor (y = 0) to <see cref="WallHeight"/>. Geometry is computed in double and stored as float. Every
/// call returns a new world.
/// </summary>
public static class MovementWorlds
{
    /// <summary>The placeholder <c>movement.capsuleRadius</c> of <c>simcore/config/default.json</c>.</summary>
    public const float CapsuleRadius = 0.4f;

    /// <summary>The placeholder <c>movement.contactSkin</c> of <c>simcore/config/default.json</c>.</summary>
    public const float ContactSkin = 0.01f;

    /// <summary>The start's Z; its X is 0.</summary>
    public const float StartZ = -16f;

    /// <summary>The height of every vertical wall's top edge.</summary>
    public const float WallHeight = 4f;

    /// <summary>Half the width, along X, of the thin wall, the stair, the ledge and the steep face.</summary>
    public const float HalfWidth = 10f;

    /// <summary>The thickness of <see cref="LowLedge"/>'s slab.</summary>
    public const float LedgeThickness = 0.5f;

    /// <summary>The Z where <see cref="Wall"/>, <see cref="SteepFace"/>, <see cref="Corner"/> and <see cref="ThinWall"/> meet the heading line: 3 m ahead.</summary>
    public const float FeatureZ = StartZ + 3f;

    private const int Samples = 65;
    private const float Origin = -32f;
    private const float GridCellSize = 4f;
    private const uint MeshId = 1;

    /// <summary>
    /// The 64 m square, every height 0, with no meshes. <see cref="MovementWorld.Planes"/> holds the
    /// floor, (0, 1, 0)·p = 0; <see cref="MovementWorld.TopHeight"/> is 0 and
    /// <see cref="MovementWorld.FootPoint"/> the floor under the start.
    /// </summary>
    public static MovementWorld Flat() => Create(
        FlatHeights(),
        [],
        planes: [Floor(0f)],
        topHeight: 0f,
        frontEdge: float.NaN,
        backEdge: float.NaN,
        footPoint: new Vec3(0f, 0f, StartZ),
        joins: []);

    /// <summary>
    /// A zero-thickness vertical quad 32 m long whose line crosses the heading 3 m ahead of the start, at
    /// P = (0, 0, <see cref="FeatureZ"/>), and runs along w = (sin α, 0, cos α), so α is its angle to the
    /// start heading (90 meets it head on). The plane is n·p = n·P with n = (cos α, 0, −sin α), facing the
    /// start, which is 3·sin α from it. <see cref="MovementWorld.FrontEdge"/> is P's Z,
    /// <see cref="MovementWorld.FootPoint"/> is P, and <see cref="MovementWorld.TopHeight"/> the top edge.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The angle is outside [10, 170] degrees, where the start would touch the wall.</exception>
    public static MovementWorld Wall(float angleDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(angleDegrees, 10f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(angleDegrees, 170f);
        var alpha = Radians(angleDegrees);
        var (sin, cos) = (System.Math.Sin(alpha), System.Math.Cos(alpha));
        const double halfLength = 16.0;
        var mesh = new MeshBuilder();
        mesh.VerticalQuad(
            (-halfLength * sin, FeatureZ - (halfLength * cos)),
            (halfLength * sin, FeatureZ + (halfLength * cos)));
        var normal = Direction(cos, 0.0, -sin);
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(normal, (float)(-sin * FeatureZ))],
            topHeight: WallHeight,
            frontEdge: FeatureZ,
            backEdge: float.NaN,
            footPoint: new Vec3(0f, 0f, FeatureZ),
            joins: []);
    }

    /// <summary>
    /// A box step of height <paramref name="rise"/> on the floor, x in ±<see cref="HalfWidth"/>, whose
    /// front face is 2 m ahead of the start and whose top runs 20 m deep along +Z, so a 2 s run at run
    /// speed ends on it (a closed box without its bottom face: top, front, back and both sides).
    /// <see cref="MovementWorld.Planes"/> holds the front face (normal (0, 0, −1), facing the start) and
    /// then the top ((0, 1, 0)·p = rise); <see cref="MovementWorld.TopHeight"/> is the rise,
    /// <see cref="MovementWorld.FrontEdge"/> and <see cref="MovementWorld.BackEdge"/> the front and back
    /// faces' Z, and <see cref="MovementWorld.FootPoint"/> the front face's foot on the heading line.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The rise is not in (0, 4] m.</exception>
    public static MovementWorld Stair(float rise)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rise);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rise, 4f);
        const float front = StartZ + 2f;
        const float back = front + 20f;
        var mesh = new MeshBuilder();
        mesh.Box(-HalfWidth, 0f, front, HalfWidth, rise, back, bottom: false);
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(new Vec3(0f, 0f, -1f), -front), Floor(rise)],
            topHeight: rise,
            frontEdge: front,
            backEdge: back,
            footPoint: new Vec3(0f, 0f, front),
            joins: []);
    }

    /// <summary>
    /// A heightfield flat for 4 m ahead of the start, then rising at <paramref name="degrees"/> along +Z for
    /// 6 m (horizontal), then flat to the far edge; it spans the whole width. The foot is the sample row at
    /// z = <see cref="StartZ"/> + 4 and the top the row at <see cref="StartZ"/> + 10, and row k of the ramp
    /// has height (float)(k·tan θ). <see cref="MovementWorld.Planes"/> holds the ramp, n = (0, cos θ,
    /// −sin θ) facing up and toward the start, then the upper flat; <see cref="MovementWorld.TopHeight"/>
    /// is the upper flat's height, <see cref="MovementWorld.FrontEdge"/> and
    /// <see cref="MovementWorld.BackEdge"/> the foot's and the top edge's Z, and
    /// <see cref="MovementWorld.FootPoint"/> the foot on the heading line.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not in (0, 80] degrees.</exception>
    public static MovementWorld Slope(float degrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(degrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degrees, 80f);
        const float foot = StartZ + 4f;
        const int rampRows = 6;
        var theta = Radians(degrees);
        var tan = System.Math.Tan(theta);
        var footRow = (int)(foot - Origin);
        var heights = FlatHeights();
        for (var j = 0; j < Samples; j++)
        {
            var k = System.Math.Clamp(j - footRow, 0, rampRows);
            var height = (float)(k * tan);
            for (var i = 0; i < Samples; i++)
            {
                heights[(j * Samples) + i] = height;
            }
        }

        var top = (float)(rampRows * tan);
        var normal = Direction(0.0, System.Math.Cos(theta), -System.Math.Sin(theta));
        return Create(
            heights,
            [],
            planes: [new WorldPlane(normal, (float)(-System.Math.Sin(theta) * foot)), Floor(top)],
            topHeight: top,
            frontEdge: foot,
            backEdge: foot + rampRows,
            footPoint: new Vec3(0f, 0f, foot),
            joins: []);
    }

    /// <summary>
    /// A horizontal slab <see cref="LedgeThickness"/> thick over the flat floor, x in
    /// ±<see cref="HalfWidth"/>, its underside at <paramref name="underside"/>, its near edge 2 m ahead of
    /// the start and its far edge 4 m beyond (a closed box). <see cref="MovementWorld.Planes"/> holds the
    /// underside ((0, −1, 0)·p = −underside, facing the floor) and then the near face (normal (0, 0, −1));
    /// <see cref="MovementWorld.TopHeight"/> is the underside, the ceiling over the floor;
    /// <see cref="MovementWorld.FrontEdge"/> and <see cref="MovementWorld.BackEdge"/> the near and far
    /// edges' Z; <see cref="MovementWorld.FootPoint"/> the floor under the near edge on the heading line.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The underside is not in (0, 4] m.</exception>
    public static MovementWorld LowLedge(float underside)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(underside);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(underside, 4f);
        const float near = StartZ + 2f;
        const float far = near + 4f;
        var mesh = new MeshBuilder();
        mesh.Box(-HalfWidth, underside, near, HalfWidth, underside + LedgeThickness, far, bottom: true);
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(new Vec3(0f, -1f, 0f), -underside), new WorldPlane(new Vec3(0f, 0f, -1f), -near)],
            topHeight: underside,
            frontEdge: near,
            backEdge: far,
            footPoint: new Vec3(0f, 0f, near),
            joins: []);
    }

    /// <summary>
    /// Three 4 m vertical facets in one mesh, each turned <paramref name="facetDegrees"/> (φ) from the last
    /// toward the start's side, so the wall is concave toward the start. Facet 1 runs along +Z at
    /// x = <see cref="CapsuleRadius"/> + <see cref="ContactSkin"/>, from z = <see cref="StartZ"/> − 1, so
    /// the start touches it at the skin and runs along it; facet k runs along (−sin((k − 1)φ), 0,
    /// cos((k − 1)φ)). <see cref="MovementWorld.Planes"/> holds the three facets in order, each normal
    /// (−cos((k − 1)φ), 0, −sin((k − 1)φ)) facing the start; <see cref="MovementWorld.Joins"/> the two
    /// joins at floor level; <see cref="MovementWorld.FootPoint"/> facet 1's first foot corner.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The angle is not in (0, 45] degrees.</exception>
    public static MovementWorld FacetedConcaveWall(float facetDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(facetDegrees);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(facetDegrees, 45f);
        const double facetLength = 4.0;
        var phi = Radians(facetDegrees);
        var points = new (double X, double Z)[4];
        points[0] = (CapsuleRadius + ContactSkin, StartZ - 1.0);
        var planes = new WorldPlane[3];
        for (var k = 0; k < 3; k++)
        {
            var (sin, cos) = (System.Math.Sin(k * phi), System.Math.Cos(k * phi));
            points[k + 1] = (points[k].X - (facetLength * sin), points[k].Z + (facetLength * cos));
            var normal = Direction(-cos, 0.0, -sin);
            planes[k] = new WorldPlane(normal, (float)((-cos * points[k].X) - (sin * points[k].Z)));
        }

        var mesh = new MeshBuilder();
        mesh.VerticalStrip(points);
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: planes,
            topHeight: WallHeight,
            frontEdge: float.NaN,
            backEdge: float.NaN,
            footPoint: Floor(points[0]),
            joins: [Floor(points[1]), Floor(points[2])]);
    }

    /// <summary>
    /// A planar face of height <paramref name="height"/> rising at <paramref name="degrees"/> (θ) along +Z
    /// from a foot line on the floor at z = <see cref="FeatureZ"/> to its top edge, x in
    /// ±<see cref="HalfWidth"/>. The start is a release point, not a rest: the feet 0.5 m above the face's
    /// top height, one capsule radius before the top edge in Z, so a falling capsule lands on the face;
    /// <see cref="MovementWorld.StartGrounded"/> is false. <see cref="MovementWorld.Planes"/> holds the
    /// face, n = (0, cos θ, −sin θ); <see cref="MovementWorld.TopHeight"/> is the height,
    /// <see cref="MovementWorld.FrontEdge"/> and <see cref="MovementWorld.BackEdge"/> the foot's and the
    /// top edge's Z, and <see cref="MovementWorld.FootPoint"/> the foot on the heading line.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The height is not in (0, 8] m or the angle not in [30, 80] degrees.</exception>
    public static MovementWorld SteepFace(float height, float degrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 8f);
        ArgumentOutOfRangeException.ThrowIfLessThan(degrees, 30f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(degrees, 80f);
        var theta = Radians(degrees);
        var top = (float)(FeatureZ + (height / System.Math.Tan(theta)));
        var mesh = new MeshBuilder();
        mesh.Quad(
            new Vec3(-HalfWidth, 0f, FeatureZ),
            new Vec3(HalfWidth, 0f, FeatureZ),
            new Vec3(HalfWidth, height, top),
            new Vec3(-HalfWidth, height, top));
        var normal = Direction(0.0, System.Math.Cos(theta), -System.Math.Sin(theta));
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(normal, (float)(-System.Math.Sin(theta) * FeatureZ))],
            topHeight: height,
            frontEdge: FeatureZ,
            backEdge: top,
            footPoint: new Vec3(0f, 0f, FeatureZ),
            joins: [],
            start: new Vec3(0f, height + 0.5f, top - CapsuleRadius),
            startGrounded: false);
    }

    /// <summary>
    /// Two zero-thickness vertical walls, 8 m long, meeting at 90 degrees at C = (0, 0,
    /// <see cref="FeatureZ"/>), 3 m ahead of the start, and running back from C at ±45 degrees, so the
    /// corner is concave toward the start and the heading is its bisector. <see cref="MovementWorld.Planes"/>
    /// holds the −X wall (normal (1, 0, −1)/√2) and then the +X wall (normal (−1, 0, −1)/√2), both facing
    /// the start; <see cref="MovementWorld.Joins"/> and <see cref="MovementWorld.FootPoint"/> are C.
    /// </summary>
    public static MovementWorld Corner()
    {
        const double length = 8.0;
        var back = length / System.Math.Sqrt(2.0);
        var mesh = new MeshBuilder();
        mesh.VerticalStrip([(-back, FeatureZ - back), (0.0, FeatureZ), (back, FeatureZ - back)]);
        var offset = (float)(-FeatureZ / System.Math.Sqrt(2.0));
        var corner = new Vec3(0f, 0f, FeatureZ);
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(Direction(1.0, 0.0, -1.0), offset), new WorldPlane(Direction(-1.0, 0.0, -1.0), offset)],
            topHeight: WallHeight,
            frontEdge: FeatureZ,
            backEdge: float.NaN,
            footPoint: corner,
            joins: [corner]);
    }

    /// <summary>
    /// One zero-thickness vertical quad across the heading 3 m ahead of the start, at
    /// z = <see cref="FeatureZ"/>, x in ±<see cref="HalfWidth"/>. <see cref="MovementWorld.Planes"/> holds
    /// it with normal (0, 0, −1), facing the start; <see cref="MovementWorld.FrontEdge"/> is its Z and
    /// <see cref="MovementWorld.FootPoint"/> its foot on the heading line.
    /// </summary>
    public static MovementWorld ThinWall()
    {
        var mesh = new MeshBuilder();
        mesh.VerticalQuad((-HalfWidth, FeatureZ), (HalfWidth, FeatureZ));
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(new Vec3(0f, 0f, -1f), -FeatureZ)],
            topHeight: WallHeight,
            frontEdge: FeatureZ,
            backEdge: float.NaN,
            footPoint: new Vec3(0f, 0f, FeatureZ),
            joins: []);
    }

    /// <summary>
    /// Two zero-thickness vertical quads parallel to the heading, at x = ±<paramref name="width"/>/2 from
    /// z = <see cref="StartZ"/> − 4 to <see cref="StartZ"/> + 4, so the start stands midway between them.
    /// <see cref="MovementWorld.Planes"/> holds the +X wall (normal (−1, 0, 0)) and then the −X wall
    /// (normal (1, 0, 0)), both facing the start; <see cref="MovementWorld.FootPoint"/> is the +X wall's
    /// foot beside the start. A slot narrower than the capsule's diameter overlaps the start on both sides.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive.</exception>
    public static MovementWorld Slot(float width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var half = width / 2.0;
        var mesh = new MeshBuilder();
        mesh.VerticalQuad((half, StartZ - 4.0), (half, StartZ + 4.0));
        mesh.VerticalQuad((-half, StartZ - 4.0), (-half, StartZ + 4.0));
        return Create(
            FlatHeights(),
            [mesh.Build()],
            planes: [new WorldPlane(new Vec3(-1f, 0f, 0f), (float)-half), new WorldPlane(new Vec3(1f, 0f, 0f), (float)-half)],
            topHeight: WallHeight,
            frontEdge: float.NaN,
            backEdge: float.NaN,
            footPoint: new Vec3((float)half, 0f, StartZ),
            joins: []);
    }

    private static MovementWorld Create(
        float[] heights,
        MeshData[] meshes,
        WorldPlane[] planes,
        float topHeight,
        float frontEdge,
        float backEdge,
        Vec3 footPoint,
        Vec3[] joins,
        Vec3? start = null,
        bool startGrounded = true)
    {
        var data = new CollisionData(Origin, Origin, 1f, GridCellSize, Samples, Samples, heights, meshes);
        return new MovementWorld(
            TestWorlds.Load(data),
            start ?? new Vec3(0f, ContactSkin, StartZ),
            0,
            startGrounded,
            planes,
            topHeight,
            frontEdge,
            backEdge,
            footPoint,
            joins);
    }

    private static float[] FlatHeights() => new float[Samples * Samples];

    private static WorldPlane Floor(float height) => new(new Vec3(0f, 1f, 0f), height);

    private static Vec3 Floor((double X, double Z) point) => new((float)point.X, 0f, (float)point.Z);

    private static double Radians(float degrees) => degrees * System.Math.PI / 180.0;

    /// <summary>The unit vector along (x, y, z), with any −0 component written as +0.</summary>
    private static Vec3 Direction(double x, double y, double z)
    {
        var length = System.Math.Sqrt((x * x) + (y * y) + (z * z));
        return new Vec3((float)((x / length) + 0.0), (float)((y / length) + 0.0), (float)((z / length) + 0.0));
    }

    /// <summary>Collects one mesh's vertices and index triples.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<float> _vertices = [];
        private readonly List<uint> _indices = [];

        /// <summary>A vertical quad from the floor to <see cref="WallHeight"/> between two floor points.</summary>
        public void VerticalQuad((double X, double Z) a, (double X, double Z) b) => VerticalStrip([a, b]);

        /// <summary>Vertical quads from the floor to <see cref="WallHeight"/> along a polyline, sharing the join vertices.</summary>
        public void VerticalStrip((double X, double Z)[] points)
        {
            var first = (uint)(_vertices.Count / 3);
            foreach (var (x, z) in points)
            {
                Add(new Vec3((float)x, 0f, (float)z));
                Add(new Vec3((float)x, WallHeight, (float)z));
            }

            for (var k = 0u; k + 1 < points.Length; k++)
            {
                var bottom = first + (2 * k);
                Triangle(bottom, bottom + 2, bottom + 3);
                Triangle(bottom, bottom + 3, bottom + 1);
            }
        }

        /// <summary>The quad a, b, c, d as the triangles (a, b, c) and (a, c, d).</summary>
        public void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            var first = (uint)(_vertices.Count / 3);
            Add(a);
            Add(b);
            Add(c);
            Add(d);
            Triangle(first, first + 1, first + 2);
            Triangle(first, first + 2, first + 3);
        }

        /// <summary>An axis-aligned box's faces: top, front (min Z), back (max Z), both sides, and the bottom when asked.</summary>
        public void Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool bottom)
        {
            Quad(new Vec3(minX, maxY, minZ), new Vec3(maxX, maxY, minZ), new Vec3(maxX, maxY, maxZ), new Vec3(minX, maxY, maxZ));
            Quad(new Vec3(minX, minY, minZ), new Vec3(maxX, minY, minZ), new Vec3(maxX, maxY, minZ), new Vec3(minX, maxY, minZ));
            Quad(new Vec3(minX, minY, maxZ), new Vec3(maxX, minY, maxZ), new Vec3(maxX, maxY, maxZ), new Vec3(minX, maxY, maxZ));
            Quad(new Vec3(minX, minY, minZ), new Vec3(minX, minY, maxZ), new Vec3(minX, maxY, maxZ), new Vec3(minX, maxY, minZ));
            Quad(new Vec3(maxX, minY, minZ), new Vec3(maxX, minY, maxZ), new Vec3(maxX, maxY, maxZ), new Vec3(maxX, maxY, minZ));
            if (bottom)
            {
                Quad(new Vec3(minX, minY, minZ), new Vec3(maxX, minY, minZ), new Vec3(maxX, minY, maxZ), new Vec3(minX, minY, maxZ));
            }
        }

        public MeshData Build() => new(MeshId, [.. _vertices], [.. _indices]);

        private void Add(Vec3 vertex)
        {
            _vertices.Add(vertex.X);
            _vertices.Add(vertex.Y);
            _vertices.Add(vertex.Z);
        }

        private void Triangle(uint a, uint b, uint c)
        {
            _indices.Add(a);
            _indices.Add(b);
            _indices.Add(c);
        }
    }
}

/// <summary>
/// A plane n·p = <see cref="Offset"/> with unit normal n. Walls, faces and the ledge's near face and
/// underside face the start, which lies on their positive side; floors and tops ((0, 1, 0)·p = height)
/// face up, so a character resting on one is on its positive side.
/// </summary>
public readonly record struct WorldPlane(Vec3 Normal, float Offset)
{
    /// <summary>n·p − offset in double: positive on the start's side.</summary>
    public double SignedDistance(Vec3 point) =>
        ((double)Normal.X * point.X) + ((double)Normal.Y * point.Y) + ((double)Normal.Z * point.Z) - Offset;
}

/// <summary>
/// A <see cref="MovementWorlds"/> world: the decoded <see cref="CollisionWorld"/>, the start (the feet;
/// at rest height, <see cref="MovementWorlds.ContactSkin"/> above the floor, unless the builder says
/// otherwise) and its yaw, and the geometry the tests assert against. Each builder documents what its
/// planes, heights, edges (Z coordinates along the heading) and points are; a field a world has no use
/// for is NaN (an edge) or empty (a list).
/// </summary>
public sealed record MovementWorld(
    CollisionWorld World,
    Vec3 Start,
    ushort Yaw,
    bool StartGrounded,
    IReadOnlyList<WorldPlane> Planes,
    float TopHeight,
    float FrontEdge,
    float BackEdge,
    Vec3 FootPoint,
    IReadOnlyList<Vec3> Joins)
{
    /// <summary>The world's first plane, its main feature.</summary>
    public WorldPlane Plane => Planes[0];

    /// <summary>
    /// A character at rest at <see cref="Start"/> facing <see cref="Yaw"/>: standing, velocity 0, ground
    /// normal (0, 1, 0), grounded when <see cref="StartGrounded"/>, <c>lastSequence</c> 0, slot 0 active,
    /// with the placeholder weapons.
    /// </summary>
    public CharacterRecord StartRecord(uint id = 1) => new()
    {
        Id = id,
        Position = (Start.X, Start.Y, Start.Z),
        AimYaw = Yaw,
        Flags = StartGrounded ? TestStates.Grounded : (byte)0,
    };

    /// <summary><see cref="StartRecord"/> as a canonical state at step 0, for <c>SimWorld.Reset</c>.</summary>
    public byte[] StartState(uint id = 1) => TestStates.Build(0, [StartRecord(id)]);

    /// <summary><see cref="StartRecord"/> as the core's <see cref="CharacterState"/>, for <c>MovementStep.Step</c>.</summary>
    public CharacterState StartCharacter(uint id = 1) => new()
    {
        Id = id,
        Position = Start,
        Velocity = new Vec3(0f, 0f, 0f),
        GroundNormal = new Vec3(0f, 1f, 0f),
        AimYaw = Yaw,
        Stance = Stance.Standing,
        Flags = StartGrounded ? CharacterFlags.Grounded : default,
        ActiveSlot = 0,
        WeaponCount = (byte)TestStates.PlaceholderWeapons.Count,
    };
}
