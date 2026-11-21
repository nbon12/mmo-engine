using CsCheck;
using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Tests.Collision;

/// <summary>
/// The core's closest-point queries against double-precision references written here (FR-003, research
/// R6 and R7): <c>ClosestPoints.PointTriangle</c>, <c>SegmentSegment</c> and <c>SegmentTriangle</c>
/// (Ericson §5.1.5, §5.1.9 and §5.1.10) each return the distance and the closest points, and
/// <c>ClosestPoints.LocalOrigin</c> gives the point of the 64 m lattice nearest a position. The queries
/// work in the frame they are given: a caller subtracts the local origin from every input first
/// (exact for the point it was snapped from, by Sterbenz's lemma), runs the query, and adds the origin to
/// any point it keeps. These tests do the same, so the queries see coordinates within about 34 m of the
/// origin however far out the case lies.
/// </summary>
/// <remarks>
/// <para>
/// Each row draws 25,000 cases from CsCheck with the declared seed <see cref="PropertySeed"/> (stream 1,
/// 2 or 3 by row); a failure prints the seed, the case index and every input bit for bit. The mixture
/// covers positions offset by up to ±7,000 m in X and Z and positions within 64 m of the world origin,
/// points on or within 1 mm of the triangle's plane, segments crossing the triangle, near-parallel pairs
/// with sin²θ just below and just above the parallel threshold 1e-10 (built near the origin, where float
/// coordinates can hold such angles), and segments of zero length (no direction). Every case lies within
/// 2 m of its first input, and every triangle is well shaped (no edge under 0.1 m, |AB × AC| at least
/// 0.1·maxEdge²): slivers lose float accuracy in any closest-point method and the loader's degenerate
/// rule is tested elsewhere.
/// </para>
/// <para>
/// Tolerances. The distance must be within 1e-6 m of the reference. At up to 34 m from the local origin
/// one ulp of a coordinate is 3.8e-6 m, so the distance has to come from the separation vector (such as
/// the barycentric offset minus <c>p − a</c>), not from the returned points, which carry their own
/// coordinates' rounding; for the same reason a returned point must lie on its primitive within 1e-6 m
/// plus one ulp of each of its coordinates. Near-parallel segment pairs (only in the segment-segment row)
/// may also be off by min(|d1|, |d2|)·sinθ (at most 6e-5 m here): the distance varies by at most that along
/// the pair's overlap, and float cannot tell where on the overlap the minimum lies (Ericson's parallel
/// branch takes s = 0, and just above the threshold the denominator a·e − b² is below the rounding of
/// a·e), so §5.1.9 returns some pair on it. Segment-triangle needs no such allowance: there the minimum
/// falls on a segment end or a triangle vertex, which the other candidates measure exactly.
/// </para>
/// </remarks>
public sealed class ClosestPointsTests
{
    /// <summary>The declared fixed seed; each row uses its own PCG stream of it.</summary>
    private const ulong PropertySeed = 0x0015_0066_C105_E5A1UL;

    private const int CasesPerQuery = 25_000;

    private const double Tolerance = 1e-6;

    private const double LatticeSpacing = 64.0;

    /// <summary>Every case lies within this distance of its first input, per axis.</summary>
    private const double CaseExtent = 2.0;

    private const double FarOffset = 7000.0;

    private const double NearOffset = 64.0;

    private const double ParallelThreshold = 1e-10;

    /// <summary>Each side of the parallel threshold must be drawn at least this often in a row.</summary>
    private const int MinimumNearParallelPerSide = 1000;

    public static TheoryData<string> Queries => ["point-triangle", "segment-segment", "segment-triangle"];

    /// <summary>
    /// FR-003, research R7: given the row's 25,000 cases, when the query runs in the local frame, each
    /// returned distance is within 1e-6 m of the double-precision reference and each returned point lies on
    /// its primitive within 1e-6 m (with the allowances in the class remarks), and the local origin is a
    /// point of the 64 m lattice within 32 m of the case's first input, subtracted from it exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(Queries))]
    public void MatchDoubleReferences(string query)
    {
        var stream = query switch
        {
            "point-triangle" => 1u,
            "segment-segment" => 2u,
            "segment-triangle" => 3u,
            _ => throw new ArgumentOutOfRangeException(nameof(query), query, "Unknown query."),
        };
        var draw = new Draw(new PCG(stream, PropertySeed));
        var below = 0;
        var above = 0;
        for (var index = 0; index < CasesPerQuery; index++)
        {
            var sample = query switch
            {
                "point-triangle" => PointTriangleCase(draw, index),
                "segment-segment" => SegmentSegmentCase(draw, index),
                _ => SegmentTriangleCase(draw, index),
            };
            if (sample.NearParallel)
            {
                if (sample.SinSquared < ParallelThreshold)
                {
                    below++;
                }
                else
                {
                    above++;
                }
            }

            Evaluate(query, stream, index, sample);
        }

        if (query != "point-triangle")
        {
            Assert.True(
                below >= MinimumNearParallelPerSide && above >= MinimumNearParallelPerSide,
                $"{query}: near-parallel cases drawn below / above sin²θ = 1e-10: {below} / {above}; each side needs {MinimumNearParallelPerSide}");
        }
    }

    // ---------------------------------------------------------------- evaluation

    private static void Evaluate(string query, uint stream, int index, Sample sample)
    {
        var inputs = sample.Inputs;
        var anchor = inputs[0];
        var origin = ClosestPoints.LocalOrigin(anchor.X, anchor.Z);
        string Describe(string what) => $"{what}; {query}, seed 0x{PropertySeed:X16} stream {stream}, case {index} ({sample.Kind}), "
            + $"inputs [{string.Join(", ", inputs.Select(Bits))}], local origin {Bits(origin)}";

        CheckLocalOrigin(anchor, origin, Describe);

        var local = new Vec3[inputs.Length];
        var exact = new DoubleVec3[inputs.Length];
        for (var k = 0; k < inputs.Length; k++)
        {
            local[k] = inputs[k] - origin;
            exact[k] = DoubleVec3.From(local[k]);
        }

        switch (query)
        {
            case "point-triangle":
                {
                    var distance = ClosestPoints.PointTriangle(local[0], local[1], local[2], local[3], out var onTriangle);
                    var reference = DoubleGeometry.PointTriangle(exact[0], exact[1], exact[2], exact[3]);
                    CheckDistance(distance, reference, 0.0, Describe);
                    CheckOn(onTriangle, DoubleGeometry.PointTriangle(DoubleVec3.From(onTriangle), exact[1], exact[2], exact[3]), "the point on the triangle", Describe);
                    break;
                }

            case "segment-segment":
                {
                    var distance = ClosestPoints.SegmentSegment(local[0], local[1], local[2], local[3], out var onFirst, out var onSecond);
                    var reference = DoubleGeometry.SegmentSegment(exact[0], exact[1], exact[2], exact[3]);
                    var allowance = sample.NearParallel ? NearParallelAllowance(exact[0], exact[1], exact[2], exact[3]) : 0.0;
                    CheckDistance(distance, reference, allowance, Describe);
                    CheckOn(onFirst, DoubleGeometry.PointSegment(DoubleVec3.From(onFirst), exact[0], exact[1]), "the point on the first segment", Describe);
                    CheckOn(onSecond, DoubleGeometry.PointSegment(DoubleVec3.From(onSecond), exact[2], exact[3]), "the point on the second segment", Describe);
                    break;
                }

            default:
                {
                    var distance = ClosestPoints.SegmentTriangle(local[0], local[1], local[2], local[3], local[4], out var onSegment, out var onTriangle);
                    var reference = DoubleGeometry.SegmentTriangle(exact[0], exact[1], exact[2], exact[3], exact[4]);
                    CheckDistance(distance, reference, 0.0, Describe);
                    CheckOn(onSegment, DoubleGeometry.PointSegment(DoubleVec3.From(onSegment), exact[0], exact[1]), "the point on the segment", Describe);
                    CheckOn(onTriangle, DoubleGeometry.PointTriangle(DoubleVec3.From(onTriangle), exact[2], exact[3], exact[4]), "the point on the triangle", Describe);
                    break;
                }
        }
    }

    /// <summary>
    /// The local origin is on the 64 m lattice at height 0, it is the lattice point nearest the anchor in X
    /// and in Z (within half a spacing), and the anchor minus it is exact.
    /// </summary>
    private static void CheckLocalOrigin(Vec3 anchor, Vec3 origin, Func<string, string> describe)
    {
        Check(BitConverter.SingleToUInt32Bits(origin.Y) == 0u, () => describe($"LocalOrigin's y is {origin.Y:R}, not +0"));
        foreach (var (position, snapped, axis) in new[] { (anchor.X, origin.X, "x"), (anchor.Z, origin.Z, "z") })
        {
            Check(
                float.IsFinite(snapped) && System.Math.IEEERemainder(snapped, LatticeSpacing) == 0.0,
                () => describe($"LocalOrigin's {axis}, {snapped:R}, is not a multiple of 64"));
            Check(
                System.Math.Abs((double)position - snapped) <= LatticeSpacing / 2,
                () => describe($"LocalOrigin's {axis}, {snapped:R}, is more than 32 m from {position:R}"));
            Check(
                (double)(position - snapped) == (double)position - snapped,
                () => describe($"{position:R} − {snapped:R} is not exact in float"));
        }
    }

    private static void CheckDistance(float distance, double reference, double allowance, Func<string, string> describe) =>
        Check(
            float.IsFinite(distance) && distance >= 0f && System.Math.Abs(distance - reference) <= Tolerance + allowance,
            () => describe($"distance {distance:R}, reference {reference:R} (difference {distance - reference:E3}, allowed {Tolerance + allowance:E3})"));

    private static void CheckOn(Vec3 point, double offPrimitive, string what, Func<string, string> describe)
    {
        var allowed = Tolerance + UlpLength(point);
        Check(
            float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z) && offPrimitive <= allowed,
            () => describe($"{what}, {Bits(point)}, is {offPrimitive:E3} m off it (allowed {allowed:E3})"));
    }

    private static void Check(bool ok, Func<string> message)
    {
        if (!ok)
        {
            Assert.Fail(message());
        }
    }

    /// <summary>min(|d1|, |d2|)·sinθ: how much the distance can vary along a near-parallel pair's overlap.</summary>
    private static double NearParallelAllowance(DoubleVec3 p1, DoubleVec3 q1, DoubleVec3 p2, DoubleVec3 q2)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var sin = DoubleVec3.Cross(d1, d2).Length / (d1.Length * d2.Length);
        return System.Math.Min(d1.Length, d2.Length) * sin;
    }

    /// <summary>The length of one ulp in each coordinate of <paramref name="point"/>.</summary>
    private static double UlpLength(Vec3 point)
    {
        static double Ulp(float value) => (double)MathF.BitIncrement(MathF.Abs(value)) - MathF.Abs(value);
        var (x, y, z) = (Ulp(point.X), Ulp(point.Y), Ulp(point.Z));
        return System.Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    private static string Bits(Vec3 v) => FormattableString.Invariant(
        $"({v.X:R} 0x{BitConverter.SingleToUInt32Bits(v.X):X8}, {v.Y:R} 0x{BitConverter.SingleToUInt32Bits(v.Y):X8}, {v.Z:R} 0x{BitConverter.SingleToUInt32Bits(v.Z):X8})");

    // ---------------------------------------------------------------- the cases

    /// <summary>A case: its kind, its inputs in call order (the first is the local origin's anchor), and for a near-parallel case the sin²θ of its float inputs.</summary>
    private sealed record Sample(string Kind, Vec3[] Inputs, bool NearParallel = false, double SinSquared = double.NaN);

    /// <summary>
    /// p, a, b, c. Kinds by index mod 4: <c>far</c> and <c>near-origin</c> (p is the anchor and the triangle
    /// lies within 2 m of it), and <c>far-on-plane</c> and <c>near-origin-on-plane</c> (p is a point of the
    /// triangle moved at most 1 mm along its normal, either way).
    /// </summary>
    private static Sample PointTriangleCase(Draw draw, int index)
    {
        var far = index % 2 == 0;
        var center = Anchor(draw, far ? FarOffset : NearOffset);
        var (a, b, c) = WellShapedTriangle(draw, center);
        if (index % 4 < 2)
        {
            return new(far ? "far" : "near-origin", [center, a, b, c]);
        }

        var onPlane = PointIn(draw, a, b, c) + (Normal(a, b, c) * draw.Uniform(-1e-3, 1e-3));
        return new(far ? "far-on-plane" : "near-origin-on-plane", [ToVec3(onPlane), a, b, c]);
    }

    /// <summary>
    /// p1, q1, p2, q2. Kinds by index mod 5: <c>far</c> and <c>near-origin</c> (random segments within 2 m of
    /// p1); <c>parallel-below</c> and <c>parallel-above</c> (near the origin: separated by 0.05 to 1.5 m,
    /// overlapping, with a target sin²θ in [0.25e-10, 1e-10) or (1e-10, 4e-10]; the recorded sin²θ is the
    /// float inputs' own); <c>zero-length</c> (the first, the second or both segments a single point).
    /// </summary>
    private static Sample SegmentSegmentCase(Draw draw, int index)
    {
        switch (index % 5)
        {
            case 0:
            case 1:
                {
                    var center = Anchor(draw, index % 5 == 0 ? FarOffset : NearOffset);
                    return new(index % 5 == 0 ? "far" : "near-origin", [center, Around(draw, center), Around(draw, center), Around(draw, center)]);
                }

            case 2:
            case 3:
                {
                    var below = index % 5 == 2;
                    var p1 = Anchor(draw, CaseExtent);
                    var q1 = ToVec3(DoubleVec3.From(p1) + (draw.Direction() * draw.Uniform(0.5, 3.0)));
                    var d1 = DoubleVec3.From(q1) - DoubleVec3.From(p1);
                    var p2 = ToVec3(DoubleVec3.From(p1) + (d1 * draw.Uniform(-0.5, 0.5)) + (draw.Perpendicular(d1) * draw.Uniform(0.05, 1.5)));
                    var q2 = ToVec3(DoubleVec3.From(p2) + Tilted(draw, d1, below, draw.Uniform(0.5, 3.0)));
                    return new(below ? "parallel-below" : "parallel-above", [p1, q1, p2, q2], true, SinSquared(DoubleVec3.From(q1) - DoubleVec3.From(p1), DoubleVec3.From(q2) - DoubleVec3.From(p2)));
                }

            default:
                {
                    var center = Anchor(draw, draw.Below(2) == 0 ? FarOffset : NearOffset);
                    var (q1, p2, q2) = (Around(draw, center), Around(draw, center), Around(draw, center));
                    return draw.Below(3) switch
                    {
                        0 => new("zero-length-first", [center, center, p2, q2]),
                        1 => new("zero-length-second", [center, q1, p2, p2]),
                        _ => new("zero-length-both", [center, center, p2, p2]),
                    };
                }
        }
    }

    /// <summary>
    /// p, q, a, b, c. Kinds by index mod 5: <c>far</c> and <c>near-origin</c> (random, within 2 m of p);
    /// <c>crossing</c> (far out, the segment passing through the triangle's interior, distance 0);
    /// <c>parallel-to-edge</c> (near the origin: 0.05 to 1.5 m from edge AB, with a target sin²θ to it in
    /// [0.25e-10, 4e-10], half below the threshold and half above); <c>zero-length</c>.
    /// </summary>
    private static Sample SegmentTriangleCase(Draw draw, int index)
    {
        switch (index % 5)
        {
            case 0:
            case 1:
                {
                    var center = Anchor(draw, index % 5 == 0 ? FarOffset : NearOffset);
                    var (a, b, c) = WellShapedTriangle(draw, center);
                    return new(index % 5 == 0 ? "far" : "near-origin", [center, Around(draw, center), a, b, c]);
                }

            case 2:
                {
                    var (a, b, c) = WellShapedTriangle(draw, Anchor(draw, FarOffset));
                    var through = PointIn(draw, a, b, c);
                    var direction = Normal(a, b, c) + (draw.Direction() * 0.5);
                    direction /= direction.Length;
                    var p = ToVec3(through - (direction * draw.Uniform(0.01, 1.0)));
                    var q = ToVec3(through + (direction * draw.Uniform(0.01, 1.0)));
                    return new("crossing", [p, q, a, b, c]);
                }

            case 3:
                {
                    var below = draw.Below(2) == 0;
                    var (a, b, c) = WellShapedTriangle(draw, Anchor(draw, CaseExtent));
                    var edge = DoubleVec3.From(b) - DoubleVec3.From(a);
                    var start = DoubleVec3.From(a) + (edge * draw.Uniform(-0.3, 1.0)) + (draw.Perpendicular(edge) * draw.Uniform(0.05, 1.5));
                    var p = ToVec3(start);
                    var q = ToVec3(DoubleVec3.From(p) + Tilted(draw, edge, below, draw.Uniform(0.5, 3.0)));
                    return new("parallel-to-edge", [p, q, a, b, c], true, SinSquared(DoubleVec3.From(q) - DoubleVec3.From(p), edge));
                }

            default:
                {
                    var center = Anchor(draw, draw.Below(2) == 0 ? FarOffset : NearOffset);
                    var (a, b, c) = WellShapedTriangle(draw, center);
                    return new("zero-length", [center, center, a, b, c]);
                }
        }
    }

    /// <summary>A position with X and Z in ±<paramref name="range"/> and Y in ±2 m, rounded to float.</summary>
    private static Vec3 Anchor(Draw draw, double range) =>
        ToVec3(new DoubleVec3(draw.Uniform(-range, range), draw.Uniform(-CaseExtent, CaseExtent), draw.Uniform(-range, range)));

    /// <summary>A point within <see cref="CaseExtent"/> of <paramref name="center"/> on each axis, rounded to float.</summary>
    private static Vec3 Around(Draw draw, Vec3 center) => ToVec3(DoubleVec3.From(center) + new DoubleVec3(
        draw.Uniform(-CaseExtent, CaseExtent), draw.Uniform(-CaseExtent, CaseExtent), draw.Uniform(-CaseExtent, CaseExtent)));

    /// <summary>A triangle around <paramref name="center"/> with no edge under 0.1 m and |AB × AC|² ≥ 0.01·maxEdge⁴, measured on its float vertices.</summary>
    private static (Vec3 A, Vec3 B, Vec3 C) WellShapedTriangle(Draw draw, Vec3 center)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var (a, b, c) = (Around(draw, center), Around(draw, center), Around(draw, center));
            var (da, db, dc) = (DoubleVec3.From(a), DoubleVec3.From(b), DoubleVec3.From(c));
            var edges = new[] { (db - da).Length, (dc - db).Length, (da - dc).Length };
            var cross = DoubleVec3.Cross(db - da, dc - da).Length;
            var longest = edges.Max();
            if (edges.Min() >= 0.1 && cross * cross >= 0.01 * System.Math.Pow(longest, 4))
            {
                return (a, b, c);
            }
        }

        throw new InvalidOperationException("No well-shaped triangle in 10,000 draws.");
    }

    /// <summary>A uniformly drawn point of the triangle, in double.</summary>
    private static DoubleVec3 PointIn(Draw draw, Vec3 a, Vec3 b, Vec3 c)
    {
        var (u, v) = (draw.Uniform(0.0, 1.0), draw.Uniform(0.0, 1.0));
        if (u + v > 1.0)
        {
            (u, v) = (1.0 - u, 1.0 - v);
        }

        var da = DoubleVec3.From(a);
        return da + ((DoubleVec3.From(b) - da) * u) + ((DoubleVec3.From(c) - da) * v);
    }

    private static DoubleVec3 Normal(Vec3 a, Vec3 b, Vec3 c)
    {
        var n = DoubleVec3.Cross(DoubleVec3.From(b) - DoubleVec3.From(a), DoubleVec3.From(c) - DoubleVec3.From(a));
        return n / n.Length;
    }

    /// <summary>
    /// A vector of <paramref name="length"/> along <paramref name="axis"/>, tilted off it toward a random
    /// perpendicular by an angle whose sin² is drawn log-uniformly from [0.25e-10, 1e-10) when
    /// <paramref name="below"/>, else from (1e-10, 4e-10].
    /// </summary>
    private static DoubleVec3 Tilted(Draw draw, DoubleVec3 axis, bool below, double length)
    {
        var sinSquared = ParallelThreshold * System.Math.Pow(4.0, below ? -draw.Uniform(0.02, 1.0) : draw.Uniform(0.02, 1.0));
        var sin = System.Math.Sqrt(sinSquared);
        return ((axis / axis.Length) * (length * System.Math.Sqrt(1.0 - sinSquared))) + (draw.Perpendicular(axis) * (length * sin));
    }

    private static double SinSquared(DoubleVec3 u, DoubleVec3 v)
    {
        var cross = DoubleVec3.Cross(u, v).Length;
        return (cross * cross) / ((u.Length * u.Length) * (v.Length * v.Length));
    }

    private static Vec3 ToVec3(DoubleVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>Uniform draws from a CsCheck <see cref="PCG"/>, so a seed fixes every case.</summary>
    private sealed class Draw(PCG pcg)
    {
        private static readonly Gen<double> UnitInterval = Gen.Double[0.0, 1.0];

        public double Uniform(double low, double high) => low + ((high - low) * UnitInterval.Generate(pcg, null!, out _));

        /// <summary>An integer in [0, <paramref name="count"/>).</summary>
        public int Below(int count) => (int)pcg.Next((uint)count);

        /// <summary>A random unit vector (rejection sampling in the unit ball).</summary>
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

        /// <summary>A random unit vector perpendicular to <paramref name="axis"/>.</summary>
        public DoubleVec3 Perpendicular(DoubleVec3 axis)
        {
            var unit = axis / axis.Length;
            for (var attempt = 0; attempt < 10_000; attempt++)
            {
                var w = DoubleVec3.Cross(unit, Direction());
                var length = w.Length;
                if (length >= 0.1)
                {
                    return w / length;
                }
            }

            throw new InvalidOperationException("No perpendicular in 10,000 draws.");
        }
    }
}

/// <summary>A double-precision vector for the test references; a float <see cref="Vec3"/> converts exactly.</summary>
internal readonly record struct DoubleVec3(double X, double Y, double Z)
{
    public double Length => System.Math.Sqrt(Dot(this, this));

    public static DoubleVec3 From(Vec3 v) => new(v.X, v.Y, v.Z);

    public static DoubleVec3 operator +(DoubleVec3 a, DoubleVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static DoubleVec3 operator -(DoubleVec3 a, DoubleVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static DoubleVec3 operator *(DoubleVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static DoubleVec3 operator /(DoubleVec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(DoubleVec3 a, DoubleVec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static DoubleVec3 Cross(DoubleVec3 a, DoubleVec3 b) => new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));
}

/// <summary>
/// Double-precision distances between points, segments and triangles, written for the tests independently
/// of the core's float code (<see cref="ClosestPointsTests"/> and <c>CapsuleSweepTests</c> use them). Each
/// is the minimum over a convex set, found without Ericson's region tests: a point projects to a
/// triangle's plane when the projection is inside, else to the nearest edge; two segments meet at their
/// interior critical point when it lies in both, else the minimum is on a boundary (an end against the
/// other segment); a segment and a triangle are 0 apart when the segment crosses it, else the minimum is
/// an end against the triangle or the segment against an edge. The triangles must not be degenerate.
/// </summary>
internal static class DoubleGeometry
{
    public static double PointSegment(DoubleVec3 p, DoubleVec3 a, DoubleVec3 b)
    {
        var ab = b - a;
        var lengthSquared = DoubleVec3.Dot(ab, ab);
        var t = lengthSquared == 0.0 ? 0.0 : System.Math.Clamp(DoubleVec3.Dot(p - a, ab) / lengthSquared, 0.0, 1.0);
        return (p - (a + (ab * t))).Length;
    }

    public static double PointTriangle(DoubleVec3 p, DoubleVec3 a, DoubleVec3 b, DoubleVec3 c)
    {
        var n = DoubleVec3.Cross(b - a, c - a);
        var unit = n / n.Length;
        var height = DoubleVec3.Dot(p - a, unit);
        var projection = p - (unit * height);
        if (Inside(projection, a, b, c, n))
        {
            return System.Math.Abs(height);
        }

        return System.Math.Min(PointSegment(p, a, b), System.Math.Min(PointSegment(p, b, c), PointSegment(p, c, a)));
    }

    public static double SegmentSegment(DoubleVec3 p1, DoubleVec3 q1, DoubleVec3 p2, DoubleVec3 q2)
    {
        var best = System.Math.Min(
            System.Math.Min(PointSegment(p1, p2, q2), PointSegment(q1, p2, q2)),
            System.Math.Min(PointSegment(p2, p1, q1), PointSegment(q2, p1, q1)));
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var (a, b, e) = (DoubleVec3.Dot(d1, d1), DoubleVec3.Dot(d1, d2), DoubleVec3.Dot(d2, d2));
        var (c, f) = (DoubleVec3.Dot(d1, r), DoubleVec3.Dot(d2, r));
        var denominator = (a * e) - (b * b);
        if (denominator > 1e-24 * a * e)
        {
            var s = ((b * f) - (c * e)) / denominator;
            var t = ((a * f) - (b * c)) / denominator;
            if (s is >= 0.0 and <= 1.0 && t is >= 0.0 and <= 1.0)
            {
                best = System.Math.Min(best, ((p1 + (d1 * s)) - (p2 + (d2 * t))).Length);
            }
        }

        return best;
    }

    public static double SegmentTriangle(DoubleVec3 p, DoubleVec3 q, DoubleVec3 a, DoubleVec3 b, DoubleVec3 c)
    {
        var n = DoubleVec3.Cross(b - a, c - a);
        var (dp, dq) = (DoubleVec3.Dot(n, p - a), DoubleVec3.Dot(n, q - a));
        if (dp * dq <= 0.0 && dp != dq)
        {
            var crossing = p + ((q - p) * (dp / (dp - dq)));
            if (Inside(crossing, a, b, c, n))
            {
                return 0.0;
            }
        }

        return System.Math.Min(
            System.Math.Min(SegmentSegment(p, q, a, b), System.Math.Min(SegmentSegment(p, q, b, c), SegmentSegment(p, q, c, a))),
            System.Math.Min(PointTriangle(p, a, b, c), PointTriangle(q, a, b, c)));
    }

    /// <summary>True when <paramref name="x"/>, in the triangle's plane, is on the inner side of all three edges.</summary>
    private static bool Inside(DoubleVec3 x, DoubleVec3 a, DoubleVec3 b, DoubleVec3 c, DoubleVec3 n) =>
        DoubleVec3.Dot(DoubleVec3.Cross(b - a, x - a), n) >= 0.0
        && DoubleVec3.Dot(DoubleVec3.Cross(c - b, x - b), n) >= 0.0
        && DoubleVec3.Dot(DoubleVec3.Cross(a - c, x - c), n) >= 0.0;
}
