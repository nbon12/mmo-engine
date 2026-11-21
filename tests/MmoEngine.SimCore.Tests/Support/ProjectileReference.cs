using System.Globalization;
using System.Runtime.CompilerServices;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The double-precision reference for projectile flight (US5; FR-009, FR-010 and FR-011): the closed-form
/// ballistic path, segment tests against a capsule, a triangle, the heightfield and the meshes, the
/// configured hitbox layout posed in double, and a first-hit search along a whole flight
/// (<see cref="PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{ReferenceTarget}, long)"/>).
/// <para>
/// Test code only, and independent of the code under test: it never calls <c>Ballistics</c>,
/// <c>SegmentCapsule</c>, <c>CollisionWorld.SegmentQuery</c>, <c>HitboxBuilder</c> or <c>ProjectileStep</c>.
/// It reads the geometry from <see cref="CollisionData"/> (the primary data of
/// contracts/collision-data-format.md, before any core code derives anything from it) and the hitbox
/// rows from the raw <see cref="SimConfig"/> of contracts/config-rules.md, and it computes in double.
/// The only compiled values it uses are the inputs of the path: <c>HalfG</c>, <c>LifetimeSteps</c> and the
/// step rate, Phase 2's conversions, which <c>Config/CompiledConfigTests</c> covers.
/// </para>
/// <para>
/// Ties: two segment fractions within <see cref="TieTolerance"/> of each other are treated as equal, and
/// the tie goes by the key of data-model.md#projectile-step step 4 (world before character, then the
/// lower target ID, then the lower hitbox index) or, between triangles, to the lower triangle ID. The
/// format's watertight shared vertices make the float core see a segment through a shared edge or
/// vertex at one t, so the reference names the lower ID there too instead of whichever side double
/// rounding happens to favour. Triangle edges are inclusive within <see cref="EdgeTolerance"/> for the
/// same reason.
/// </para>
/// <c>System.Math</c> is written in full, because <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
public static class ProjectileReference
{
    /// <summary>The coordinate bound (data-model.md#projectile-step step 6): past it a projectile expires OutOfBounds, so the search stops.</summary>
    public const double CoordinateLimit = 8192.0;

    /// <summary>Segment fractions closer than this are a tie, broken by the hit key.</summary>
    public const double TieTolerance = 1e-12;

    /// <summary>How far outside a triangle's edges, in barycentric units, a segment still counts as hitting it.</summary>
    public const double EdgeTolerance = 1e-12;

    private const int WorldRank = 0;
    private const int CharacterRank = 1;

    /// <summary>
    /// The closed-form position after <paramref name="k"/> steps in double: with t = k / hz,
    /// (x0 + vx·t, y0 + vy·t + halfG·t², z0 + vz·t) (data-model.md#projectile, research.md R11).
    /// </summary>
    /// <param name="origin">The launch point, in metres.</param>
    /// <param name="velocity">The launch velocity, in m/s.</param>
    /// <param name="halfG">The class's <c>HalfG</c>, in m/s² (negative: down).</param>
    /// <param name="hz">The step rate.</param>
    /// <param name="k">Steps since launch; 0 is the origin.</param>
    public static DVec3 ClosedForm(DVec3 origin, DVec3 velocity, double halfG, int hz, long k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
        var t = (double)k / hz;
        return new DVec3(
            origin.X + (velocity.X * t),
            origin.Y + (velocity.Y * t) + (halfG * t * t),
            origin.Z + (velocity.Z * t));
    }

    /// <summary>
    /// <see cref="ClosedForm(DVec3, DVec3, double, int, long)"/> for <paramref name="projectile"/>'s origin
    /// and launch velocity, with its class's <c>HalfG</c> and the step rate of <paramref name="config"/>.
    /// </summary>
    public static DVec3 ClosedForm(ReferenceProjectile projectile, CompiledConfig config, long k)
    {
        ArgumentNullException.ThrowIfNull(config);
        return ClosedForm(projectile.Origin, projectile.LaunchVelocity, config.HalfG[projectile.Class], config.StepRateHz, k);
    }

    /// <summary>
    /// The first fraction t in [0, 1] at which the segment a + (b − a)·t touches the capsule (every point
    /// within <paramref name="radius"/> of the axis segment from <paramref name="capA"/> to
    /// <paramref name="capB"/>), solved exactly in double on the whole, unclipped segment: the entry into
    /// the infinite cylinder where its axis parameter lies on the axis, and the entries into the two end
    /// spheres, the smallest kept. A start inside (or on) the capsule gives 0. Null when the segment
    /// misses, and for a zero-length segment outside the capsule.
    /// </summary>
    public static double? SegmentCapsule(DVec3 a, DVec3 b, DVec3 capA, DVec3 capB, double radius)
    {
        if (DistanceToSegment(a, capA, capB) <= radius)
        {
            return 0.0;
        }

        var d = b - a;
        if (DVec3.LengthSquared(d) == 0.0)
        {
            return null;
        }

        double? best = null;
        var axis = capB - capA;
        var axisLengthSq = DVec3.LengthSquared(axis);
        if (axisLengthSq > 0.0)
        {
            // The parts of the start offset and of the direction perpendicular to the axis.
            var m = a - capA;
            var mAxis = DVec3.Dot(m, axis);
            var dAxis = DVec3.Dot(d, axis);
            var mPerp = m - (axis * (mAxis / axisLengthSq));
            var dPerp = d - (axis * (dAxis / axisLengthSq));
            var qa = DVec3.LengthSquared(dPerp);
            var qc = DVec3.LengthSquared(mPerp) - (radius * radius);

            // A start inside the infinite cylinder (but outside the capsule) can only enter through an end
            // sphere; a direction along the axis never crosses the side.
            if (qa > 0.0 && qc > 0.0 && SmallerRoot(qa, 2.0 * DVec3.Dot(mPerp, dPerp), qc) is { } side && side is >= 0.0 and <= 1.0)
            {
                var along = (mAxis + (side * dAxis)) / axisLengthSq;
                if (along is >= 0.0 and <= 1.0)
                {
                    best = side;
                }
            }
        }

        best = Smaller(best, SphereEntry(a, d, capA, radius));
        best = Smaller(best, SphereEntry(a, d, capB, radius));
        return best;
    }

    /// <summary>
    /// The fraction t in [0, 1] at which the segment a + (b − a)·t meets the triangle
    /// (<paramref name="v0"/>, <paramref name="v1"/>, <paramref name="v2"/>), from either side
    /// (Möller–Trumbore accepting either sign of the determinant), with inclusive edges. Null when it misses
    /// or runs parallel to the triangle's plane.
    /// </summary>
    public static double? SegmentTriangle(DVec3 a, DVec3 b, DVec3 v0, DVec3 v1, DVec3 v2)
    {
        var d = b - a;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = DVec3.Cross(d, e2);
        var determinant = DVec3.Dot(e1, p);
        if (determinant == 0.0)
        {
            return null;
        }

        var inverse = 1.0 / determinant;
        var s = a - v0;
        var u = DVec3.Dot(s, p) * inverse;
        if (u < -EdgeTolerance || u > 1.0 + EdgeTolerance)
        {
            return null;
        }

        var q = DVec3.Cross(s, e1);
        var v = DVec3.Dot(d, q) * inverse;
        if (v < -EdgeTolerance || u + v > 1.0 + EdgeTolerance)
        {
            return null;
        }

        var t = DVec3.Dot(e2, q) * inverse;
        return t is >= 0.0 and <= 1.0 ? t : null;
    }

    /// <summary>
    /// The earliest heightfield triangle the segment meets, with its fraction and triangle ID
    /// (2·(j·(columns − 1) + i) + k, contracts/collision-data-format.md#triangle-ids), ties to the lower ID.
    /// It tests both triangles of every cell the segment's X–Z box touches, plus one cell of margin on
    /// each side so a segment along a cell boundary sees the cells on both sides, skipping only cells whose
    /// four corner heights all lie above or all below the segment's Y range. Vertices come from the
    /// format's one sample expression, evaluated in float as the core does, so they are the core's
    /// vertices to the bit. Null when nothing is hit or the box misses the heightfield.
    /// </summary>
    public static (double T, int TriangleId)? SegmentHeightfield(CollisionData world, DVec3 a, DVec3 b)
    {
        ArgumentNullException.ThrowIfNull(world);
        var cellsX = world.Columns - 1;
        var cellsZ = world.Rows - 1;
        if (!CellRange(world.OriginX, world.CellSize, cellsX, System.Math.Min(a.X, b.X), System.Math.Max(a.X, b.X), out var i0, out var i1)
            || !CellRange(world.OriginZ, world.CellSize, cellsZ, System.Math.Min(a.Z, b.Z), System.Math.Max(a.Z, b.Z), out var j0, out var j1))
        {
            return null;
        }

        var minY = System.Math.Min(a.Y, b.Y);
        var maxY = System.Math.Max(a.Y, b.Y);
        (double T, int TriangleId)? best = null;
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var v00 = Sample(world, i, j);
                var v10 = Sample(world, i + 1, j);
                var v01 = Sample(world, i, j + 1);
                var v11 = Sample(world, i + 1, j + 1);
                var low = System.Math.Min(System.Math.Min(v00.Y, v10.Y), System.Math.Min(v01.Y, v11.Y));
                var high = System.Math.Max(System.Math.Max(v00.Y, v10.Y), System.Math.Max(v01.Y, v11.Y));
                if (low > maxY || high < minY)
                {
                    continue;
                }

                var id = 2 * ((j * cellsX) + i);
                best = EarlierTriangle(best, SegmentTriangle(a, b, v00, v11, v10), id);
                best = EarlierTriangle(best, SegmentTriangle(a, b, v00, v01, v11), id + 1);
            }
        }

        return best;
    }

    /// <summary>
    /// The earliest mesh triangle the segment meets, with its fraction and triangle ID (the heightfield
    /// triangle count plus the triangle's global index in file order), ties to the lower ID. Every mesh
    /// triangle whose bounding box overlaps the segment's is tested, both sides. Null when nothing is hit.
    /// </summary>
    public static (double T, int TriangleId)? SegmentMeshes(CollisionData world, DVec3 a, DVec3 b)
    {
        ArgumentNullException.ThrowIfNull(world);
        return SegmentMeshes(MeshTriangles(world), a, b);
    }

    /// <summary>
    /// The earliest of <see cref="SegmentHeightfield"/> and <see cref="SegmentMeshes(CollisionData, DVec3, DVec3)"/>,
    /// ties to the lower ID (so terrain wins a tie with a mesh), with its kind:
    /// <see cref="HitKind.Terrain"/> below the heightfield triangle count, <see cref="HitKind.Mesh"/> above.
    /// Boundary planes are never reported (contracts/collision-data-format.md#triangle-ids).
    /// </summary>
    public static (double T, int TriangleId, HitKind Kind)? SegmentWorld(CollisionData world, DVec3 a, DVec3 b)
    {
        ArgumentNullException.ThrowIfNull(world);
        return SegmentWorld(world, MeshTriangles(world), a, b);
    }

    /// <summary>
    /// The stance's three configured capsules (contracts/config-rules.md#hitboxes-section-4-instance-s--stance)
    /// posed at <paramref name="feet"/> with <paramref name="yaw"/>, in double: with sy = sin and cy = cos
    /// of the binary angle (2π·yaw/65536), a local point (lf, lr, lu) becomes
    /// (fx + (sy·lf − cy·lr), fy + lu, fz + (cy·lf + sy·lr)) (data-model.md#hitbox-layout-and-hitboxes).
    /// </summary>
    /// <returns>Head, Torso and Legs, in that order.</returns>
    public static IReadOnlyList<ReferenceCapsule> LayoutCapsules(Stance stance, DVec3 feet, ushort yaw, SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var hitboxes = config.Hitboxes ?? throw new ArgumentException("The configuration has no hitboxes section.", nameof(config));
        var rows = stance switch
        {
            Stance.Standing => hitboxes.Standing,
            Stance.Crouched => hitboxes.Crouched,
            _ => throw new ArgumentOutOfRangeException(nameof(stance), stance, "A stance is Standing or Crouched."),
        };
        if (rows is null || rows.Count != HitboxLayout.CapsuleCount)
        {
            throw new ArgumentException($"The {stance} hitboxes must have exactly three capsules.", nameof(config));
        }

        var halfTurns = yaw / 32768.0;
        var sy = double.SinPi(halfTurns);
        var cy = double.CosPi(halfTurns);
        var capsules = new ReferenceCapsule[HitboxLayout.CapsuleCount];
        for (var c = 0; c < capsules.Length; c++)
        {
            var row = rows[c] ?? throw new ArgumentException($"The {stance} hitbox {c} is missing.", nameof(config));
            var localA = row.A ?? throw new ArgumentException($"The {stance} hitbox {c} has no a.", nameof(config));
            var localB = row.B ?? throw new ArgumentException($"The {stance} hitbox {c} has no b.", nameof(config));
            capsules[c] = new ReferenceCapsule((HitboxPart)c, Pose(localA, feet, sy, cy), Pose(localB, feet, sy, cy), row.Radius);
        }

        return capsules;
    }

    /// <summary><see cref="LayoutCapsules(Stance, DVec3, ushort, SimConfig)"/> with the validated configuration <paramref name="config"/> was compiled from.</summary>
    public static IReadOnlyList<ReferenceCapsule> LayoutCapsules(Stance stance, DVec3 feet, ushort yaw, CompiledConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return LayoutCapsules(stance, feet, yaw, config.Config);
    }

    /// <summary>The distance from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>, in double.</summary>
    public static double DistanceToSegment(DVec3 point, DVec3 a, DVec3 b)
    {
        var ab = b - a;
        var lengthSq = DVec3.LengthSquared(ab);
        if (lengthSq == 0.0)
        {
            return DVec3.Length(point - a);
        }

        var s = System.Math.Clamp(DVec3.Dot(point - a, ab) / lengthSq, 0.0, 1.0);
        return DVec3.Length(point - (a + (ab * s)));
    }

    /// <summary>
    /// The first hit along the projectile's flight, in double, or null. Segment k runs from
    /// <see cref="ClosedForm(DVec3, DVec3, double, int, long)"/>(k) to (k + 1) for k from
    /// <paramref name="firstK"/> to <c>LifetimeSteps[class]</c> − 1, and the first segment that meets
    /// terrain, a mesh or the capsule of a target other than the owner gives the hit: the earliest t on it,
    /// ties by (world before character, target ID, hitbox index). It is geometry only. It applies no range
    /// rule, so a hit beyond the class's range is still predicted, and a test that expects an expiry asserts
    /// that the prediction is null or later than the expiry step. It stops after the first segment whose
    /// end has a coordinate past ±<see cref="CoordinateLimit"/>. The targets are posed once, with
    /// <see cref="LayoutCapsules(Stance, DVec3, ushort, SimConfig)"/>: a test whose targets move passes the
    /// pose they hold when the projectile reaches them.
    /// </summary>
    /// <param name="world">The collision data the world was built from.</param>
    /// <param name="config">Gives <c>HalfG</c>, <c>LifetimeSteps</c>, the step rate and the hitbox rows.</param>
    /// <param name="projectile">The launch state; its <c>LaunchStep</c> turns k into a world step.</param>
    /// <param name="targets">The characters whose capsules can be hit; the owner is skipped.</param>
    /// <param name="firstK">
    /// The first segment the world will sweep: 0 for a projectile spawned in the step it is swept, and
    /// <c>step + 1 − launchStep</c> for one loaded in flight at <c>step</c>
    /// (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>),
    /// whose earlier segments the world never sweeps.
    /// </param>
    public static PredictedHit? PredictFirstHit(
        CollisionData world,
        CompiledConfig config,
        ReferenceProjectile projectile,
        IReadOnlyList<ReferenceTarget> targets,
        long firstK = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfNegative(firstK);

        var halfG = (double)config.HalfG[projectile.Class];
        var hz = config.StepRateHz;
        var lifetime = config.LifetimeSteps[projectile.Class];
        var layout = config.Config;
        var capsules = new List<(uint TargetId, ReferenceCapsule Capsule)>();
        foreach (var target in targets)
        {
            if (target.Id == projectile.OwnerId)
            {
                continue;
            }

            foreach (var capsule in LayoutCapsules(target.Stance, target.Feet, target.Yaw, layout))
            {
                capsules.Add((target.Id, capsule));
            }
        }

        var meshes = MeshTriangles(world);
        var heightfieldTriangles = HeightfieldTriangleCount(world);
        for (var k = firstK; k < lifetime; k++)
        {
            var a = ClosedForm(projectile.Origin, projectile.LaunchVelocity, halfG, hz, k);
            var b = ClosedForm(projectile.Origin, projectile.LaunchVelocity, halfG, hz, k + 1);

            Candidate? best = null;
            if (SegmentWorld(world, meshes, a, b) is { } worldHit)
            {
                best = Earlier(best, new Candidate(worldHit.T, WorldRank, worldHit.TriangleId, -1));
            }

            foreach (var (targetId, capsule) in capsules)
            {
                if (SegmentCapsule(a, b, capsule.A, capsule.B, capsule.Radius) is { } t)
                {
                    best = Earlier(best, new Candidate(t, CharacterRank, targetId, (int)capsule.Part));
                }
            }

            if (best is { } hit)
            {
                var isWorld = hit.Rank == WorldRank;
                var kind = !isWorld ? HitKind.Character : hit.Id < heightfieldTriangles ? HitKind.Terrain : HitKind.Mesh;
                return new PredictedHit(
                    Step: checked((uint)(projectile.LaunchStep + k)),
                    K: checked((uint)k),
                    Kind: kind,
                    TargetId: checked((uint)hit.Id),
                    Hitbox: isWorld ? HitboxPart.None : (HitboxPart)hit.Part,
                    Point: a + ((b - a) * hit.T),
                    T: hit.T);
            }

            if (System.Math.Abs(b.X) > CoordinateLimit || System.Math.Abs(b.Y) > CoordinateLimit || System.Math.Abs(b.Z) > CoordinateLimit)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{ReferenceTarget}, long)"/>
    /// over the characters of a state built with <see cref="TestStates.Build"/>, such as
    /// <see cref="ProjectileWorlds.Shooter"/> and <see cref="ProjectileWorlds.Target"/>. It takes priority
    /// when a call fits several overloads, such as one passing an empty collection.
    /// </summary>
    [OverloadResolutionPriority(1)]
    public static PredictedHit? PredictFirstHit(
        CollisionData world,
        CompiledConfig config,
        ReferenceProjectile projectile,
        IReadOnlyList<CharacterRecord> characters,
        long firstK = 0)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return PredictFirstHit(world, config, projectile, Targets(characters, static c => c), firstK);
    }

    /// <summary>
    /// <see cref="PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{ReferenceTarget}, long)"/>
    /// over a committed state's characters (<c>StateSerializer.ReadCommitted(report.State).Characters</c>).
    /// </summary>
    public static PredictedHit? PredictFirstHit(
        CollisionData world,
        CompiledConfig config,
        ReferenceProjectile projectile,
        IReadOnlyList<CharacterState> characters,
        long firstK = 0)
    {
        ArgumentNullException.ThrowIfNull(characters);
        return PredictFirstHit(world, config, projectile, Targets(characters, static c => c), firstK);
    }

    private static ReferenceTarget[] Targets<T>(IReadOnlyList<T> characters, Func<T, ReferenceTarget> convert)
    {
        var targets = new ReferenceTarget[characters.Count];
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i] = convert(characters[i]);
        }

        return targets;
    }

    private static (double T, int TriangleId, HitKind Kind)? SegmentWorld(
        CollisionData world,
        IReadOnlyList<ReferenceTriangle> meshes,
        DVec3 a,
        DVec3 b)
    {
        var best = SegmentHeightfield(world, a, b);
        if (SegmentMeshes(meshes, a, b) is { } mesh)
        {
            best = EarlierTriangle(best, mesh.T, mesh.TriangleId);
        }

        if (best is not { } hit)
        {
            return null;
        }

        return (hit.T, hit.TriangleId, hit.TriangleId < HeightfieldTriangleCount(world) ? HitKind.Terrain : HitKind.Mesh);
    }

    private static (double T, int TriangleId)? SegmentMeshes(IReadOnlyList<ReferenceTriangle> triangles, DVec3 a, DVec3 b)
    {
        var min = new DVec3(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
        var max = new DVec3(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));
        (double T, int TriangleId)? best = null;
        foreach (var triangle in triangles)
        {
            if (triangle.Max.X < min.X || triangle.Min.X > max.X
                || triangle.Max.Y < min.Y || triangle.Min.Y > max.Y
                || triangle.Max.Z < min.Z || triangle.Min.Z > max.Z)
            {
                continue;
            }

            best = EarlierTriangle(best, SegmentTriangle(a, b, triangle.V0, triangle.V1, triangle.V2), triangle.Id);
        }

        return best;
    }

    /// <summary>Every mesh triangle in file order, with its ID and bounding box, in double.</summary>
    private static ReferenceTriangle[] MeshTriangles(CollisionData world)
    {
        var triangles = new List<ReferenceTriangle>();
        var id = HeightfieldTriangleCount(world);
        foreach (var mesh in world.Meshes)
        {
            var count = mesh.Indices.Length / 3;
            for (var t = 0; t < count; t++)
            {
                var v0 = Vertex(mesh, mesh.Indices[3 * t]);
                var v1 = Vertex(mesh, mesh.Indices[(3 * t) + 1]);
                var v2 = Vertex(mesh, mesh.Indices[(3 * t) + 2]);
                triangles.Add(new ReferenceTriangle(
                    id,
                    v0,
                    v1,
                    v2,
                    new DVec3(Min3(v0.X, v1.X, v2.X), Min3(v0.Y, v1.Y, v2.Y), Min3(v0.Z, v1.Z, v2.Z)),
                    new DVec3(Max3(v0.X, v1.X, v2.X), Max3(v0.Y, v1.Y, v2.Y), Max3(v0.Z, v1.Z, v2.Z))));
                id++;
            }
        }

        return [.. triangles];
    }

    private static int HeightfieldTriangleCount(CollisionData world) => 2 * (world.Columns - 1) * (world.Rows - 1);

    private static DVec3 Vertex(MeshData mesh, uint index)
    {
        var at = checked(3 * (int)index);
        return new DVec3(mesh.Vertices[at], mesh.Vertices[at + 1], mesh.Vertices[at + 2]);
    }

    /// <summary>Sample (i, j): <c>(originX + (float)i·cellSize, heights[j·columns + i], originZ + (float)j·cellSize)</c>, in float.</summary>
    private static DVec3 Sample(CollisionData world, int i, int j) => new(
        (float)(world.OriginX + ((float)i * world.CellSize)),
        world.Heights[(j * world.Columns) + i],
        (float)(world.OriginZ + ((float)j * world.CellSize)));

    /// <summary>The cells along one axis that [min, max] touches, with one cell of margin each side; false when it misses the extent.</summary>
    private static bool CellRange(float origin, float cellSize, int cells, double min, double max, out int first, out int last)
    {
        var end = (double)(float)(origin + ((float)cells * cellSize));
        if (max < origin || min > end)
        {
            first = 0;
            last = -1;
            return false;
        }

        first = (int)System.Math.Clamp(System.Math.Floor((min - origin) / cellSize) - 1.0, 0.0, cells - 1.0);
        last = (int)System.Math.Clamp(System.Math.Floor((max - origin) / cellSize) + 1.0, 0.0, cells - 1.0);
        return true;
    }

    private static DVec3 Pose(HitboxPointConfig local, DVec3 feet, double sy, double cy) => new(
        feet.X + ((sy * local.Fwd) - (cy * local.Right)),
        feet.Y + local.Up,
        feet.Z + ((cy * local.Fwd) + (sy * local.Right)));

    /// <summary>The entry fraction into the sphere, or null; a start inside gives 0.</summary>
    private static double? SphereEntry(DVec3 a, DVec3 d, DVec3 centre, double radius)
    {
        var m = a - centre;
        var qc = DVec3.LengthSquared(m) - (radius * radius);
        if (qc <= 0.0)
        {
            return 0.0;
        }

        return SmallerRoot(DVec3.LengthSquared(d), 2.0 * DVec3.Dot(m, d), qc) is { } t && t is >= 0.0 and <= 1.0 ? t : null;
    }

    /// <summary>The smaller real root of qa·t² + qb·t + qc with qa &gt; 0, in the cancellation-free form; null with none.</summary>
    private static double? SmallerRoot(double qa, double qb, double qc)
    {
        var discriminant = (qb * qb) - (4.0 * qa * qc);
        if (discriminant < 0.0)
        {
            return null;
        }

        var root = System.Math.Sqrt(discriminant);
        var q = -0.5 * (qb + (qb >= 0.0 ? root : -root));
        var first = q / qa;
        var second = q != 0.0 ? qc / q : first;
        return System.Math.Min(first, second);
    }

    private static double? Smaller(double? a, double? b) => (a, b) switch
    {
        ({ } x, { } y) => System.Math.Min(x, y),
        ({ } x, null) => x,
        (null, var y) => y,
    };

    private static (double T, int TriangleId)? EarlierTriangle((double T, int TriangleId)? best, double? t, int id)
    {
        if (t is not { } next)
        {
            return best;
        }

        if (best is not { } current
            || next < current.T - TieTolerance
            || (next <= current.T + TieTolerance && id < current.TriangleId))
        {
            return (next, id);
        }

        return best;
    }

    private static Candidate Earlier(Candidate? best, Candidate next)
    {
        if (best is not { } current || next.T < current.T - TieTolerance)
        {
            return next;
        }

        if (next.T > current.T + TieTolerance)
        {
            return current;
        }

        var byKey = (next.Rank, next.Id, next.Part).CompareTo((current.Rank, current.Id, current.Part));
        return byKey < 0 ? next : current;
    }

    private static double Min3(double a, double b, double c) => System.Math.Min(a, System.Math.Min(b, c));

    private static double Max3(double a, double b, double c) => System.Math.Max(a, System.Math.Max(b, c));

    /// <summary>A hit candidate on one segment: its fraction and its key (rank 0 world, 1 character; target ID; hitbox index).</summary>
    private readonly record struct Candidate(double T, int Rank, long Id, int Part);

    private sealed record ReferenceTriangle(int Id, DVec3 V0, DVec3 V1, DVec3 V2, DVec3 Min, DVec3 Max);
}

/// <summary>
/// A 3-vector of doubles for the reference computations. Any <see cref="Vec3"/> converts to it exactly, so
/// every reference method accepts the core's float vectors as they are.
/// </summary>
public readonly record struct DVec3(double X, double Y, double Z)
{
    /// <summary>The float vector's components, exactly.</summary>
    public static implicit operator DVec3(Vec3 vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>The tuple's components.</summary>
    public static implicit operator DVec3((double X, double Y, double Z) vector) => new(vector.X, vector.Y, vector.Z);

    public static DVec3 operator +(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static DVec3 operator -(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static DVec3 operator *(DVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static double Dot(DVec3 a, DVec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static DVec3 Cross(DVec3 a, DVec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    public static double LengthSquared(DVec3 a) => Dot(a, a);

    public static double Length(DVec3 a) => System.Math.Sqrt(LengthSquared(a));

    /// <summary>|a − b|.</summary>
    public static double Distance(DVec3 a, DVec3 b) => Length(a - b);

    /// <summary>a + (b − a)·t.</summary>
    public static DVec3 Lerp(DVec3 a, DVec3 b, double t) => a + ((b - a) * t);

    /// <summary>The components in invariant round-trip form, <c>(x, y, z)</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:R}, {Y:R}, {Z:R})");
}

/// <summary>A posed hitbox capsule in double: the part, the axis segment's ends and the radius.</summary>
public readonly record struct ReferenceCapsule(HitboxPart Part, DVec3 A, DVec3 B, double Radius)
{
    /// <summary>The midpoint of the axis, the part's centre that the aimed shots use.</summary>
    public DVec3 Centre => DVec3.Lerp(A, B, 0.5);
}

/// <summary>
/// The launch state the reference flies: the owner (never hit), the class, the origin, the launch
/// velocity and the launch step. A core <see cref="Projectile"/> and a <see cref="ProjectileRecord"/>
/// convert to it implicitly.
/// </summary>
public readonly record struct ReferenceProjectile(uint OwnerId, byte Class, DVec3 Origin, DVec3 LaunchVelocity, uint LaunchStep)
{
    public static implicit operator ReferenceProjectile(Projectile projectile) =>
        new(projectile.OwnerId, projectile.Class, projectile.Origin, projectile.LaunchVelocity, projectile.LaunchStep);

    public static implicit operator ReferenceProjectile(ProjectileRecord projectile) => new(
        projectile.OwnerId,
        projectile.Class,
        new DVec3(projectile.Origin.X, projectile.Origin.Y, projectile.Origin.Z),
        new DVec3(projectile.LaunchVelocity.X, projectile.LaunchVelocity.Y, projectile.LaunchVelocity.Z),
        projectile.LaunchStep);
}

/// <summary>
/// A character whose capsules the reference tests: its ID, its feet, its aim yaw and its stance. A
/// <see cref="CharacterRecord"/> and a core <see cref="CharacterState"/> convert to it implicitly.
/// </summary>
public readonly record struct ReferenceTarget(uint Id, DVec3 Feet, ushort Yaw, Stance Stance)
{
    public static implicit operator ReferenceTarget(CharacterRecord character) => new(
        character.Id,
        new DVec3(character.Position.X, character.Position.Y, character.Position.Z),
        character.AimYaw,
        (Stance)character.Stance);

    public static implicit operator ReferenceTarget(CharacterState character) =>
        new(character.Id, character.Position, character.AimYaw, character.Stance);
}

/// <summary>
/// A predicted first hit: the world step that reports it (<c>LaunchStep + K</c>), the segment index K,
/// what was hit (Terrain, Mesh or Character), the triangle or character ID, the part (None for the
/// world), the point a + (b − a)·t and the fraction t of segment K, all in double.
/// </summary>
public sealed record PredictedHit(uint Step, uint K, HitKind Kind, uint TargetId, HitboxPart Hitbox, DVec3 Point, double T);
