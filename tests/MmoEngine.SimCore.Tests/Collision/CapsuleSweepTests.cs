using CsCheck;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Collision;

/// <summary>
/// The capsule sweep (FR-003, research R7): <c>CapsuleSweep.Sweep</c> moves a capsule's axis by a
/// displacement and returns the first contact as a <c>SweepHit</c>, found by conservative advancement on
/// f(t) = dist(axis at t, triangle) − (r + skin) against each candidate triangle and the four boundary
/// planes. It never passes the true time of impact, keeps the earliest contact with ties to the lower ID
/// (so a boundary plane, IDs −4 to −1, wins a tie with geometry), and treats triangles as double-sided.
/// The capsule is the placeholder one: radius 0.4 m, skin 0.01 m, standing (axis 1.0 m) or crouched
/// (axis 0.4 m).
/// </summary>
public sealed class CapsuleSweepTests
{
    /// <summary>The declared fixed seed of <see cref="NeverPassesTheTrueTimeOfImpact"/>; a failure prints it.</summary>
    private const ulong PropertySeed = 0x0015_0068_5EE9_C0DEUL;

    private const int Sweeps = 5000;

    private const float Radius = MovementWorlds.CapsuleRadius;

    private const float Skin = MovementWorlds.ContactSkin;

    /// <summary>
    /// How far, as contact distance, a sweep may run past the moment the axis comes within r + skin. A
    /// Newton step lands at or before that moment in exact arithmetic; in float, close to a triangle edge
    /// Ericson's region test can put the closest point up to about 0.5 mm along the face while the
    /// distance stays within 1e-7 m, which tilts the step's direction. In a prototype, over 45,000 hitting
    /// sweeps like these (half of them with triangles half the size), the deepest stop was 1.3e-5 m inside
    /// r + skin and the latest 2e-5 past the r + skin time in t, so a slack of 1e-6 in t would not hold;
    /// this allows 1e-4 m of contact distance, a hundredth of the skin.
    /// </summary>
    private const double PassAllowance = 1e-4;

    /// <summary>The single-triangle world's heightfield triangle count: its one mesh triangle's ID.</summary>
    private const int SingleTriangleId = 2;

    /// <summary>The first mesh triangle's ID in the walled worlds, after their 2·2·2 heightfield triangles.</summary>
    private const int FirstMeshId = 8;

    private const int MinimumHitsAndMisses = 1000;

    /// <summary>
    /// FR-003: given 5,000 sweeps drawn by CsCheck from the declared seed, each of the capsule against a
    /// single well-shaped triangle (in a world whose terrain lies 100 m below and whose boundary planes are
    /// over 25 m away), starting at least r + skin + 2 mm from it, with displacements of 0.02 to 1 m aimed
    /// near a point of the triangle, when <c>Sweep</c> runs: the axis never comes closer to the triangle
    /// (double-precision reference) than r + skin − 1e-4 m before the sweep stops, so a hit's
    /// <c>T</c> is at most the reference time of that distance (found by bisection here) and a miss
    /// means the reference never reaches it; a hit names the triangle, and its axis-to-triangle distance
    /// at <c>T</c> is at least r. Both hits and misses are drawn at least 1,000 times each.
    /// </summary>
    [Fact]
    public void NeverPassesTheTrueTimeOfImpact()
    {
        var pcg = new PCG(1, PropertySeed);
        var candidates = new int[256];
        var hits = 0;
        for (var index = 0; index < Sweeps; index++)
        {
            var sweep = NextSweep(pcg);
            string Describe(string what) => $"{what}; seed 0x{PropertySeed:X16}, sweep {index}: {sweep}";
            var world = SingleTriangleWorld(sweep.A, sweep.B, sweep.C);

            var hit = CapsuleSweep.Sweep(world, sweep.Bottom, sweep.Top, Radius, Skin, sweep.Displacement, candidates, out var queryCount);

            Check(queryCount >= 0, () => Describe($"the query overflowed {candidates.Length} slots (count {queryCount})"));
            var reference = sweep.FirstTimeWithin((double)Radius + Skin - PassAllowance);
            if (hit.Hit)
            {
                hits++;
                Check(float.IsFinite(hit.T) && hit.T >= 0f && hit.T <= 1f, () => Describe($"hit at T {hit.T:R}, outside [0, 1]"));
                Check(hit.TriangleId == SingleTriangleId, () => Describe($"hit triangle {hit.TriangleId}, not the mesh triangle {SingleTriangleId}"));
                Check(hit.T <= reference, () => Describe($"hit at T {hit.T:R}, after the reference time {reference:R} at which the axis is within r + skin − {PassAllowance:E0}"));
                var distance = sweep.DistanceAt(hit.T);
                Check(distance >= Radius, () => Describe($"at T {hit.T:R} the axis is {distance:R} m from the triangle, inside the radius"));
            }
            else
            {
                Check(reference > 1.0, () => Describe($"no hit, but the axis comes within r + skin − {PassAllowance:E0} at t {reference:R}"));
            }
        }

        var misses = Sweeps - hits;
        Assert.True(
            hits >= MinimumHitsAndMisses && misses >= MinimumHitsAndMisses,
            $"{hits} hits and {misses} misses drawn; each needs {MinimumHitsAndMisses}");
    }

    /// <summary>
    /// Research R7, collision-data-format.md#triangle-ids: the earliest contact wins and a tie goes to the
    /// lower ID. Two coincident mesh triangles (IDs 8 and 9, the same vertices in the same order) swept
    /// into head-on give <c>TriangleId</c> 8; a mesh wall lying exactly on the playable area's +X edge,
    /// swept into along +X, gives the boundary plane x = maxX, ID −3, instead of the wall's 8 or 9.
    /// </summary>
    [Fact]
    public void TiesGoToTheLowerIdAndBoundaryPlanesWin()
    {
        var candidates = new int[256];
        var coincident = WalledWorld(
            [3f, 0f, -3f, 3f, 0f, 3f, 3f, 6f, 0f],
            [0, 1, 2, 0, 1, 2]);

        var twin = CapsuleSweep.Sweep(coincident, Bottom(0f, 0f), Top(0f, 0f), Radius, Skin, new Vec3(4f, 0f, 0f), candidates, out _);

        Assert.True(twin.Hit, "the capsule swept 4 m at coincident triangles 3 m ahead did not hit them");
        Assert.Equal(FirstMeshId, twin.TriangleId);

        var onTheEdge = WalledWorld(
            [8f, 0f, -4f, 8f, 0f, 4f, 8f, 6f, 4f, 8f, 6f, -4f],
            [0, 1, 2, 0, 2, 3]);

        var edge = CapsuleSweep.Sweep(onTheEdge, Bottom(5f, 0f), Top(5f, 0f), Radius, Skin, new Vec3(4f, 0f, 0f), candidates, out _);

        Assert.True(edge.Hit, "the capsule swept 4 m at the +X edge 3 m ahead did not hit it");
        Assert.InRange(edge.TriangleId, Heightfield.BoundaryMinX, Heightfield.BoundaryMaxZ);
        Assert.Equal(Heightfield.BoundaryMaxX, edge.TriangleId);
    }

    /// <summary>
    /// Research R7: triangles are double-sided. A vertical quad in the plane x = 0, swept at from its front
    /// (from x = −3 along +X) and from its back (from x = +3 along −X), is hit both times; the mirrored
    /// starts give bit-identical <c>T</c>, and the contact normals are opposite, each facing its capsule.
    /// </summary>
    [Fact]
    public void TrianglesAreDoubleSided()
    {
        var candidates = new int[256];
        var quad = WalledWorld(
            [0f, 0f, -3f, 0f, 0f, 3f, 0f, 6f, 3f, 0f, 6f, -3f],
            [0, 1, 2, 0, 2, 3]);

        var front = CapsuleSweep.Sweep(quad, Bottom(-3f, 0f), Top(-3f, 0f), Radius, Skin, new Vec3(4f, 0f, 0f), candidates, out _);
        var back = CapsuleSweep.Sweep(quad, Bottom(3f, 0f), Top(3f, 0f), Radius, Skin, new Vec3(-4f, 0f, 0f), candidates, out _);

        Assert.True(front.Hit, "the sweep at the quad's front did not hit it");
        Assert.True(back.Hit, "the sweep at the quad's back did not hit it");
        Assert.Equal(BitConverter.SingleToUInt32Bits(front.T), BitConverter.SingleToUInt32Bits(back.T));
        Assert.True(
            front.Normal.X == -back.Normal.X && front.Normal.Y == -back.Normal.Y && front.Normal.Z == -back.Normal.Z,
            $"the contact normals {front.Normal} and {back.Normal} are not opposite");
        Assert.True(Facing(front.Normal, Bottom(-3f, 0f)) > 0f, $"the front contact normal {front.Normal} faces away from the capsule");
        Assert.True(Facing(back.Normal, Bottom(3f, 0f)) > 0f, $"the back contact normal {back.Normal} faces away from the capsule");
    }

    /// <summary>The standing capsule's axis bottom with its feet at (<paramref name="x"/>, 2, <paramref name="z"/>), 2 m above the walled worlds' floor.</summary>
    private static Vec3 Bottom(float x, float z) => new(x, 2f + Radius, z);

    /// <summary>The standing capsule's axis top: the feet plus standHeight 1.8 less the radius.</summary>
    private static Vec3 Top(float x, float z) => new(x, 2f + 1.8f - Radius, z);

    /// <summary>The normal's dot product with the offset from the plane x = 0 to the capsule's axis bottom.</summary>
    private static float Facing(Vec3 normal, Vec3 bottom) => Vec3.Dot(normal, new Vec3(bottom.X, 0f, 0f));

    /// <summary>
    /// A flat floor (heights 0) of 3 × 3 samples 8 m apart from (−8, −8), so the playable area is [−8, 8] in
    /// X and Z and the heightfield triangles are IDs 0 to 7, with 4 m grid cells and one mesh of the given
    /// vertices and index triples (IDs from 8).
    /// </summary>
    private static CollisionWorld WalledWorld(float[] vertices, uint[] indices) => TestWorlds.Load(new CollisionData(
        OriginX: -8f,
        OriginZ: -8f,
        CellSize: 8f,
        GridCellSize: 4f,
        Columns: 3,
        Rows: 3,
        Heights: new float[9],
        Meshes: [new MeshData(1, vertices, indices)]));

    /// <summary>
    /// The playable area [−32, 32] in X and Z (2 × 2 samples 64 m apart, one 64 m grid cell) with its terrain
    /// at y = −100, far below every sweep, and one mesh holding the triangle (a, b, c), ID 2.
    /// </summary>
    private static CollisionWorld SingleTriangleWorld(Vec3 a, Vec3 b, Vec3 c) => TestWorlds.Load(new CollisionData(
        OriginX: -32f,
        OriginZ: -32f,
        CellSize: 64f,
        GridCellSize: 64f,
        Columns: 2,
        Rows: 2,
        Heights: [-100f, -100f, -100f, -100f],
        Meshes: [new MeshData(1, [a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z], [0, 1, 2])]));

    /// <summary>
    /// Draws a sweep: a well-shaped triangle within [−2, 2] × [−1, 2] × [−2, 2] (no edge under 0.2 m,
    /// |AB × AC|² ≥ 0.01·maxEdge⁴); a standing or crouched capsule; a displacement of 0.02 to 1 m in a
    /// random direction; and a start that puts a random point of the axis 0.2 to 1.6 displacements behind
    /// a random point of the triangle, jittered by up to 0.35 m per axis, kept only when the axis starts at
    /// least r + skin + 2 mm from the triangle.
    /// </summary>
    private static Sweep NextSweep(PCG pcg)
    {
        var draw = new Draw(pcg);
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var (a, b, c) = Triangle(draw);
            var height = draw.Below(2) == 0 ? 1.8 : 1.2;
            var displacement = draw.Direction() * draw.Uniform(0.02, 1.0);
            var (u, v) = (draw.Uniform(0.0, 1.0), draw.Uniform(0.0, 1.0));
            if (u + v > 1.0)
            {
                (u, v) = (1.0 - u, 1.0 - v);
            }

            var target = a + ((b - a) * u) + ((c - a) * v);
            // ±0.35 m gives about 2,300 hits and 2,700 misses with this seed against an exact time of impact;
            // ±0.6 m gave only 766 hits.
            var jitter = new DoubleVec3(draw.Uniform(-0.35, 0.35), draw.Uniform(-0.35, 0.35), draw.Uniform(-0.35, 0.35));
            var axisPoint = target - (displacement * draw.Uniform(0.2, 1.6)) + jitter;
            var feet = axisPoint - new DoubleVec3(0.0, Radius + draw.Uniform(0.0, height - (2.0 * Radius)), 0.0);
            var sweep = new Sweep(
                ToVec3(a),
                ToVec3(b),
                ToVec3(c),
                ToVec3(feet + new DoubleVec3(0.0, Radius, 0.0)),
                ToVec3(feet + new DoubleVec3(0.0, height - Radius, 0.0)),
                ToVec3(displacement));
            if (sweep.DistanceAt(0.0) >= (double)Radius + Skin + 0.002)
            {
                return sweep;
            }
        }

        throw new InvalidOperationException("No clear start in 10,000 draws.");
    }

    private static (DoubleVec3 A, DoubleVec3 B, DoubleVec3 C) Triangle(Draw draw)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            DoubleVec3 Vertex() => DoubleVec3.From(ToVec3(new DoubleVec3(draw.Uniform(-2.0, 2.0), draw.Uniform(-1.0, 2.0), draw.Uniform(-2.0, 2.0))));
            var (a, b, c) = (Vertex(), Vertex(), Vertex());
            var edges = new[] { (b - a).Length, (c - b).Length, (a - c).Length };
            var cross = DoubleVec3.Cross(b - a, c - a).Length;
            if (edges.Min() >= 0.2 && cross * cross >= 0.01 * System.Math.Pow(edges.Max(), 4))
            {
                return (a, b, c);
            }
        }

        throw new InvalidOperationException("No well-shaped triangle in 10,000 draws.");
    }

    private static Vec3 ToVec3(DoubleVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static void Check(bool ok, Func<string> message)
    {
        if (!ok)
        {
            Assert.Fail(message());
        }
    }

    /// <summary>One drawn sweep, as the float values the core receives.</summary>
    private sealed record Sweep(Vec3 A, Vec3 B, Vec3 C, Vec3 Bottom, Vec3 Top, Vec3 Displacement)
    {
        /// <summary>The double-precision distance from the axis, moved by t·displacement, to the triangle.</summary>
        public double DistanceAt(double t)
        {
            var d = DoubleVec3.From(Displacement);
            return DoubleGeometry.SegmentTriangle(
                DoubleVec3.From(Bottom) + (d * t),
                DoubleVec3.From(Top) + (d * t),
                DoubleVec3.From(A),
                DoubleVec3.From(B),
                DoubleVec3.From(C));
        }

        /// <summary>
        /// The first t in [0, 1] at which the axis is within <paramref name="level"/> of the triangle, or
        /// +∞ when it never is. The distance is convex in t, so a ternary search finds its minimum and a
        /// bisection on [0, minimum] finds the first crossing.
        /// </summary>
        public double FirstTimeWithin(double level)
        {
            if (DistanceAt(0.0) <= level)
            {
                return 0.0;
            }

            var (low, high) = (0.0, 1.0);
            for (var i = 0; i < 100; i++)
            {
                var (m1, m2) = (low + ((high - low) / 3.0), high - ((high - low) / 3.0));
                if (DistanceAt(m1) <= DistanceAt(m2))
                {
                    high = m2;
                }
                else
                {
                    low = m1;
                }
            }

            var nearest = (low + high) / 2.0;
            if (DistanceAt(nearest) > level)
            {
                return double.PositiveInfinity;
            }

            (low, high) = (0.0, nearest);
            for (var i = 0; i < 100; i++)
            {
                var middle = (low + high) / 2.0;
                if (DistanceAt(middle) <= level)
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }
            }

            return high;
        }

        public override string ToString() => FormattableString.Invariant(
            $"triangle {Bits(A)} {Bits(B)} {Bits(C)}, axis {Bits(Bottom)} to {Bits(Top)}, displacement {Bits(Displacement)}");

        private static string Bits(Vec3 v) => FormattableString.Invariant(
            $"(0x{BitConverter.SingleToUInt32Bits(v.X):X8} {v.X:R}, 0x{BitConverter.SingleToUInt32Bits(v.Y):X8} {v.Y:R}, 0x{BitConverter.SingleToUInt32Bits(v.Z):X8} {v.Z:R})");
    }

    /// <summary>Uniform draws from a CsCheck <see cref="PCG"/>, so the seed fixes every sweep.</summary>
    private sealed class Draw(PCG pcg)
    {
        private static readonly Gen<double> UnitInterval = Gen.Double[0.0, 1.0];

        public double Uniform(double low, double high) => low + ((high - low) * UnitInterval.Generate(pcg, null!, out _));

        public int Below(int count) => (int)pcg.Next((uint)count);

        public DoubleVec3 Direction()
        {
            for (var attempt = 0; attempt < 10_000; attempt++)
            {
                var v = new DoubleVec3(Uniform(-1.0, 1.0), Uniform(-1.0, 1.0), Uniform(-1.0, 1.0));
                var length = v.Length;
                if (length >= 0.1 && length <= 1.0)
                {
                    return v / length;
                }
            }

            throw new InvalidOperationException("No direction in 10,000 draws.");
        }
    }
}
