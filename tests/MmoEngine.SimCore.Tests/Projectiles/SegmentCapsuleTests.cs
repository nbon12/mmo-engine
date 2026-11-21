using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Tests.Collision;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Projectiles;

/// <summary>
/// The segment–capsule test of the projectile step (FR-010, FR-011, research R11,
/// data-model.md#projectile-step step 3): <c>SegmentCapsule.ClipToSphere</c> clips a step's segment
/// a → b to a bounding sphere in Ray Tracing Gems ch. 7's stable form, giving [t0, t1] ⊆ [0, 1], and
/// <c>SegmentCapsule.Intersect</c> solves the cylinder and its two end spheres on the short clipped
/// segment p → q, giving tc with a start inside returning 0. The parameter on the original segment is
/// <c>t0 + ((t1 − t0) * tc)</c>, so a start inside gives exactly 0, and the hit point is
/// <c>a + ((b − a) * t)</c> per component, as step 5 computes it. Here, as in the step, the clipped end
/// points are <c>p = a + ((b − a) * t0)</c> and <c>q = a + ((b − a) * t1)</c> in float, and the bounding
/// sphere of a lone capsule is centred on the axis midpoint with radius half the axis length + the
/// capsule radius + 0.01 m, the margin data-model.md#hitbox-layout-and-hitboxes gives
/// <c>BoundRadius</c>. Expected values come from <see cref="ProjectileReference.SegmentCapsule"/>
/// (the exact quadratic on the unclipped segment, in double) or from the geometry of each known row.
/// </summary>
public sealed class SegmentCapsuleTests
{
    /// <summary>The declared fixed SplitMix64 seed of <see cref="ClippedTestMatchesADoubleReference"/>; a failure prints it.</summary>
    private const ulong Seed = 0x5E6C0A75UL;

    private const int Segments = 100_000;

    /// <summary>The longest drawn segment, research R11's: three times a placeholder small-arms step at 20 Hz (600 m/s · 0.05 s = 30 m).</summary>
    private const double MaxSegmentLength = 90.0;

    /// <summary>Research R11's measured worst hit-point error of the clipped test against double, 0.11 mm.</summary>
    private const double HitPointTolerance = 1.1e-4;

    /// <summary>The bounding sphere's margin over the capsule, as <c>BoundRadius</c> has.</summary>
    private const double BoundMargin = 0.01;

    /// <summary>How far outside a capsule's bounding sphere the drawn near points may lie.</summary>
    private const double NearMargin = 0.25;

    /// <summary>The cube, centred on the origin, that holds each drawn capsule's first end point: ±2 m.</summary>
    private const double CapsuleSpread = 2.0;

    /// <summary>
    /// The knife-edge band: a drawn segment whose least double distance to the capsule's axis lies within
    /// 1 mm of the radius is drawn again. Its hit or miss is decided by the float rounding of its inputs,
    /// not by the method under test: in a numpy float32 prototype of this test, a segment 60 m long whose
    /// line enters the capsule only 3.4e-5 m deep was the one hit beyond 0.11 mm (1.15e-4 m), because the
    /// float rounding of b − a and of a + ((b − a) * t0), about 2e-6 m across the line, moves a grazing
    /// entry point about 50 times as far along it. With the band, the prototype's worst error over the
    /// 100,000 segments was 2.4e-5 m, and about 220 draws fell in the band.
    /// </summary>
    private const double KnifeEdgeBand = 1e-3;

    /// <summary>The least number of hits and of misses among the drawn segments, so neither branch passes vacuously.</summary>
    private const int MinimumHitsAndMisses = 10_000;

    /// <summary>The least number of drawn segments that start inside their capsule (reference t = 0).</summary>
    private const int MinimumStartsInside = 100;

    /// <summary>The tolerance on a known row's t and on its hit point's distance from the expected point.</summary>
    private const double KnownTolerance = 1e-6;

    /// <summary>The known rows' capsule radius, 0.25 m, about the axis from <see cref="KnownCapsuleA"/> to <see cref="KnownCapsuleB"/>.</summary>
    private const float KnownRadius = 0.25f;

    /// <summary>The known rows' capsule: a vertical axis from (0, 1, 0) to (0, 1.5, 0), radius 0.25 m.</summary>
    private static readonly Vec3 KnownCapsuleA = new(0f, 1f, 0f);

    private static readonly Vec3 KnownCapsuleB = new(0f, 1.5f, 0f);

    /// <summary>The names of the known rows, in the order <see cref="KnownRows"/> gives them.</summary>
    private static readonly string[] KnownCaseNames =
    [
        "cylinder-side",
        "end-cap-a",
        "end-cap-b",
        "along-the-axis-into-cap-a",
        "misses-by-1mm",
        "starts-inside",
        "zero-length-inside",
        "zero-length-outside",
    ];

    /// <summary>
    /// The rows of <see cref="KnownSegmentsHitWhereTheGeometrySays"/>, each against the capsule from (0, 1, 0)
    /// to (0, 1.5, 0) with radius 0.25 m, whose bounding sphere is centred at (0, 1.25, 0) with radius 0.51 m.
    /// </summary>
    public static TheoryData<string> KnownRows => [.. KnownCaseNames];

    /// <summary>
    /// FR-010, FR-011, research R11: Given 100,000 segments up to 90 m long and capsules with radius 0.12 to
    /// 0.23 m and axis 0.1 to 0.8 m, drawn from SplitMix64 with seed <see cref="Seed"/> (each segment passes
    /// through a point drawn inside the capsule's bounding sphere grown by 0.25 m, so hits, misses, grazing
    /// passes and starts inside all occur; the knife-edge band is drawn again, see
    /// <see cref="KnifeEdgeBand"/>), When each is tested with <c>SegmentCapsule.ClipToSphere</c> on its
    /// capsule's bounding sphere then <c>SegmentCapsule.Intersect</c> on the clipped segment, and with
    /// <see cref="ProjectileReference.SegmentCapsule"/>, Then the two never disagree on hit or miss, every
    /// clip gives 0 ≤ t0 ≤ t1 ≤ 1, every hit's t is in [0, 1] and its point <c>a + ((b − a) * t)</c> is
    /// within 1.1e-4 m (0.11 mm) of the reference point; at least 10,000 segments hit, 10,000 miss and 100
    /// start inside.
    /// </summary>
    [Fact]
    public void ClippedTestMatchesADoubleReference()
    {
        var state = Seed;
        var hits = 0;
        var misses = 0;
        var startsInside = 0;
        var worstError = 0.0;
        for (var index = 0; index < Segments; index++)
        {
            var draw = NextDraw(ref state);
            string Describe(string what) => FormattableString.Invariant($"{what}; seed 0x{Seed:X8}, segment {index}: {draw}");

            var core = ClipThenIntersect(draw.A, draw.B, draw.CapsuleA, draw.CapsuleB, draw.Radius, out var clip);
            var reference = ProjectileReference.SegmentCapsule(draw.A, draw.B, draw.CapsuleA, draw.CapsuleB, draw.Radius);

            if (clip.Clipped)
            {
                Check(
                    clip.T0 >= 0f && clip.T0 <= clip.T1 && clip.T1 <= 1f,
                    () => Describe(FormattableString.Invariant($"ClipToSphere gave t0 {clip.T0:R} and t1 {clip.T1:R}, not 0 <= t0 <= t1 <= 1")));
            }

            Check(
                core.Hit == reference.HasValue,
                () => Describe(core.Hit
                    ? FormattableString.Invariant($"the clipped test hit at t {core.T:R}, the double reference misses")
                    : FormattableString.Invariant($"the clipped test missed (clipped {clip.Clipped}), the double reference hits at t {reference!.Value:R}")));
            if (!core.Hit)
            {
                misses++;
                continue;
            }

            hits++;
            var referenceT = reference!.Value;
            if (referenceT == 0.0)
            {
                startsInside++;
            }

            Check(core.T >= 0f && core.T <= 1f, () => Describe(FormattableString.Invariant($"hit at t {core.T:R}, outside [0, 1]")));
            var point = draw.A + ((draw.B - draw.A) * core.T);
            var a = DoubleVec3.From(draw.A);
            var referencePoint = a + ((DoubleVec3.From(draw.B) - a) * referenceT);
            var error = (DoubleVec3.From(point) - referencePoint).Length;
            worstError = System.Math.Max(worstError, error);
            Check(
                error <= HitPointTolerance,
                () => Describe(FormattableString.Invariant(
                    $"hit point {point} (t {core.T:R}) is {error:E3} m from the reference point at t {referenceT:R}, over {HitPointTolerance:E2} m")));
        }

        Assert.True(
            hits >= MinimumHitsAndMisses && misses >= MinimumHitsAndMisses && startsInside >= MinimumStartsInside,
            FormattableString.Invariant(
                $"seed 0x{Seed:X8}: {hits} hits ({startsInside} starting inside) and {misses} misses drawn, worst hit-point error {worstError:E3} m; each of hits and misses needs {MinimumHitsAndMisses}, starts inside {MinimumStartsInside}"));
    }

    /// <summary>
    /// FR-010, research R11: Given the capsule from (0, 1, 0) to (0, 1.5, 0) with radius 0.25 m and a
    /// segment whose answer the geometry fixes, When it is tested with <c>ClipToSphere</c> then
    /// <c>Intersect</c>, Then it hits or misses as the row says, and a hit is at the row's t within 1e-6 with
    /// its point <c>a + ((b − a) * t)</c> within 1e-6 · max(1, |b − a|) m of the row's entry point (the t
    /// tolerance carried along the segment):
    /// <c>cylinder-side</c> enters the side at x = −0.25, t = 0.5; <c>end-cap-a</c> crosses below A and
    /// enters A's end sphere at x = −0.2, t = 0.625; <c>end-cap-b</c> crosses above B and enters B's end
    /// sphere at z = 0.2, t = 0.4; <c>along-the-axis-into-cap-a</c> runs up the axis itself (the cylinder's
    /// parallel case) and enters at the cap sphere's pole y = 0.75, t = 0.375; <c>misses-by-1mm</c> passes the
    /// side 0.251 m from the axis, inside the bounding sphere, and misses; <c>starts-inside</c> starts inside
    /// the capsule and hits with t bits 0x00000000; <c>zero-length-inside</c> is a point inside
    /// and hits with t exactly 0; <c>zero-length-outside</c> is a point 5 mm outside the side, inside the bounding
    /// sphere, and misses.
    /// </summary>
    [Theory]
    [MemberData(nameof(KnownRows))]
    public void KnownSegmentsHitWhereTheGeometrySays(string caseName)
    {
        var row = Known(caseName);

        var result = ClipThenIntersect(row.A, row.B, KnownCapsuleA, KnownCapsuleB, KnownRadius, out var clip);

        Assert.True(
            result.Hit == row.Hits,
            FormattableString.Invariant(
                $"{caseName}: {(result.Hit ? $"hit at t {result.T:R}" : "missed")} (clipped {clip.Clipped}, t0 {clip.T0:R}, t1 {clip.T1:R}); the geometry says {(row.Hits ? $"a hit at t {row.T:R}" : "a miss")}"));
        if (!row.Hits)
        {
            return;
        }

        if (row.ExactZero)
        {
            Assert.True(
                BitConverter.SingleToUInt32Bits(result.T) == 0u,
                FormattableString.Invariant($"{caseName}: t is {result.T:R} (bits 0x{BitConverter.SingleToUInt32Bits(result.T):X8}); a start inside gives exactly 0 (bits 0x00000000)"));
        }

        if (row.T == 0f)
        {
            Assert.True(
                result.T == 0f,
                FormattableString.Invariant($"{caseName}: t is {result.T:R}; the row's t is exactly 0"));
        }

        Assert.True(
            System.Math.Abs(result.T - row.T) <= KnownTolerance,
            FormattableString.Invariant($"{caseName}: t is {result.T:R}, expected {row.T:R} within {KnownTolerance:E0}"));
        var point = row.A + ((row.B - row.A) * result.T);
        var error = (DoubleVec3.From(point) - DoubleVec3.From(row.Point)).Length;
        var allowed = KnownTolerance * System.Math.Max(1.0, (DoubleVec3.From(row.B) - DoubleVec3.From(row.A)).Length);
        Assert.True(
            error <= allowed,
            FormattableString.Invariant($"{caseName}: hit point {point} is {error:E3} m from the entry point {row.Point}, over {allowed:E2} m"));
    }

    /// <summary>
    /// Research R11: Given the known capsule's bounding sphere (centre (0, 1.25, 0), radius 0.51 m) and a
    /// segment along X whose line passes 1e-3 m outside it (z = 0.511), When <c>ClipToSphere</c> runs, Then
    /// it returns false, so the capsule test is never reached; and for every row of
    /// <see cref="KnownRows"/> that <c>ClipToSphere</c> accepts, 0 ≤ t0 ≤ t1 ≤ 1 (an invariant over the
    /// whole table, as the random draws check it for every clipped segment).
    /// </summary>
    [Fact]
    public void SegmentMissingTheBoundingSphereNeverReachesTheCapsuleTest()
    {
        var centre = Midpoint(KnownCapsuleA, KnownCapsuleB);
        var radius = BoundRadius(KnownCapsuleA, KnownCapsuleB, KnownRadius);
        Assert.True(
            System.Math.Abs(radius - 0.51f) <= 1e-6f && centre.Equals(new Vec3(0f, 1.25f, 0f)),
            FormattableString.Invariant($"the known bounding sphere is centred at {centre} with radius {radius:R}, expected (0, 1.25, 0) and 0.51"));
        var outside = new Vec3(-2f, 1.25f, 0.511f);
        var beyond = new Vec3(2f, 1.25f, 0.511f);

        var clipped = SegmentCapsule.ClipToSphere(outside, beyond, centre, radius, out var t0, out var t1);

        Assert.False(
            clipped,
            FormattableString.Invariant($"a segment passing 1e-3 m outside the bounding sphere was clipped to [{t0:R}, {t1:R}]"));

        foreach (var caseName in KnownCaseNames)
        {
            var row = Known(caseName);
            if (SegmentCapsule.ClipToSphere(row.A, row.B, centre, radius, out var rowT0, out var rowT1))
            {
                Assert.True(
                    rowT0 >= 0f && rowT0 <= rowT1 && rowT1 <= 1f,
                    FormattableString.Invariant($"{caseName}: ClipToSphere gave t0 {rowT0:R} and t1 {rowT1:R}, not 0 <= t0 <= t1 <= 1"));
            }
        }
    }

    /// <summary>
    /// <c>ClipToSphere</c> on the capsule's bounding sphere, then <c>Intersect</c> on the clipped segment: the
    /// clip-then-solve path of data-model.md#projectile-step step 3, here with a lone capsule's sphere. The
    /// combined parameter is <c>t0 + ((t1 − t0) * tc)</c>.
    /// </summary>
    private static (bool Hit, float T) ClipThenIntersect(Vec3 a, Vec3 b, Vec3 capsuleA, Vec3 capsuleB, float radius, out ClipResult clip)
    {
        var clipped = SegmentCapsule.ClipToSphere(a, b, Midpoint(capsuleA, capsuleB), BoundRadius(capsuleA, capsuleB, radius), out var t0, out var t1);
        clip = new ClipResult(clipped, t0, t1);
        if (!clipped)
        {
            return (false, 0f);
        }

        var d = b - a;
        var p = a + (d * t0);
        var q = a + (d * t1);
        if (!SegmentCapsule.Intersect(p, q, capsuleA, capsuleB, radius, out var tc))
        {
            return (false, 0f);
        }

        return (true, t0 + ((t1 - t0) * tc));
    }

    /// <summary>The axis midpoint, rounded once from double: the bounding sphere's centre.</summary>
    private static Vec3 Midpoint(Vec3 capsuleA, Vec3 capsuleB) => ToVec3((DoubleVec3.From(capsuleA) + DoubleVec3.From(capsuleB)) * 0.5);

    /// <summary>Half the axis length + the radius + 0.01 m, rounded once from double.</summary>
    private static float BoundRadius(Vec3 capsuleA, Vec3 capsuleB, float radius) =>
        (float)((0.5 * (DoubleVec3.From(capsuleB) - DoubleVec3.From(capsuleA)).Length) + radius + BoundMargin);

    private static Vec3 ToVec3(DoubleVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>
    /// One random segment and capsule. The capsule's first end point is uniform in the ±2 m cube, its axis a
    /// uniform direction 0.1 to 0.8 m long, its radius uniform in [0.12, 0.23]. The segment passes through a
    /// point uniform in the volume of the bounding sphere grown by <see cref="NearMargin"/>, along a uniform
    /// direction, 0 to 90 m long, with that point at a uniform fraction in [−0.2, 1.2] of the way from a to
    /// b, so some segments stop short of the capsule, some start past it and some start inside it. A draw in
    /// the knife-edge band is replaced by the next draw.
    /// </summary>
    private static Draw NextDraw(ref ulong state)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var radius = (float)Uniform(ref state, 0.12, 0.23);
            var axisLength = Uniform(ref state, 0.1, 0.8);
            var first = new DoubleVec3(
                Uniform(ref state, -CapsuleSpread, CapsuleSpread),
                Uniform(ref state, -CapsuleSpread, CapsuleSpread),
                Uniform(ref state, -CapsuleSpread, CapsuleSpread));
            var axis = Direction(ref state);
            var capsuleA = ToVec3(first);
            var capsuleB = ToVec3(first + (axis * axisLength));

            var centre = DoubleVec3.From(Midpoint(capsuleA, capsuleB));
            var reach = BoundRadius(capsuleA, capsuleB, radius) + NearMargin;
            var near = centre + (Direction(ref state) * (System.Math.Cbrt(Unit(ref state)) * reach));
            var heading = Direction(ref state);
            var length = Uniform(ref state, 0.0, MaxSegmentLength);
            var fraction = Uniform(ref state, -0.2, 1.2);
            var a = ToVec3(near - (heading * (length * fraction)));
            var b = ToVec3(DoubleVec3.From(a) + (heading * length));

            var closest = DoubleGeometry.SegmentSegment(DoubleVec3.From(a), DoubleVec3.From(b), DoubleVec3.From(capsuleA), DoubleVec3.From(capsuleB));
            if (System.Math.Abs(closest - radius) >= KnifeEdgeBand)
            {
                return new Draw(a, b, capsuleA, capsuleB, radius);
            }
        }

        throw new InvalidOperationException("1,000 draws in a row fell in the knife-edge band.");
    }

    /// <summary>A double in [0, 1) from the top 53 bits of the next SplitMix64 value.</summary>
    private static double Unit(ref ulong state) => (SplitMix64.Next(ref state) >> 11) * (1.0 / (1UL << 53));

    private static double Uniform(ref ulong state, double low, double high) => low + ((high - low) * Unit(ref state));

    /// <summary>A uniform unit direction, by rejection from the unit ball.</summary>
    private static DoubleVec3 Direction(ref ulong state)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var v = new DoubleVec3(Uniform(ref state, -1.0, 1.0), Uniform(ref state, -1.0, 1.0), Uniform(ref state, -1.0, 1.0));
            var lengthSquared = DoubleVec3.Dot(v, v);
            if (lengthSquared > 1e-6 && lengthSquared <= 1.0)
            {
                return v / System.Math.Sqrt(lengthSquared);
            }
        }

        throw new InvalidOperationException("No direction in 10,000 draws.");
    }

    /// <summary>A known row: the segment, whether it hits, and for a hit the entry t and point.</summary>
    private static KnownSegment Known(string caseName) => caseName switch
    {
        "cylinder-side" => new(new Vec3(-1f, 1.25f, 0f), new Vec3(0.5f, 1.25f, 0f), true, 0.5f, new Vec3(-0.25f, 1.25f, 0f)),
        "end-cap-a" => new(new Vec3(-1.2f, 0.85f, 0f), new Vec3(0.4f, 0.85f, 0f), true, 0.625f, new Vec3(-0.2f, 0.85f, 0f)),
        "end-cap-b" => new(new Vec3(0f, 1.65f, 1f), new Vec3(0f, 1.65f, -1f), true, 0.4f, new Vec3(0f, 1.65f, 0.2f)),
        "along-the-axis-into-cap-a" => new(new Vec3(0f, 0f, 0f), new Vec3(0f, 2f, 0f), true, 0.375f, new Vec3(0f, 0.75f, 0f)),
        "misses-by-1mm" => new(new Vec3(-1f, 1.25f, 0.251f), new Vec3(1f, 1.25f, 0.251f), false, 0f, default),
        "starts-inside" => new(new Vec3(0.05f, 1.2f, 0f), new Vec3(2f, 1.2f, 0f), true, 0f, new Vec3(0.05f, 1.2f, 0f)) { ExactZero = true },
        "zero-length-inside" => new(new Vec3(0f, 1.25f, 0.1f), new Vec3(0f, 1.25f, 0.1f), true, 0f, new Vec3(0f, 1.25f, 0.1f)),
        "zero-length-outside" => new(new Vec3(0.255f, 1.25f, 0f), new Vec3(0.255f, 1.25f, 0f), false, 0f, default),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of KnownRows."),
    };

    private static void Check(bool ok, Func<string> message)
    {
        if (!ok)
        {
            Assert.Fail(message());
        }
    }

    /// <summary>What <c>ClipToSphere</c> returned for a segment.</summary>
    private readonly record struct ClipResult(bool Clipped, float T0, float T1);

    /// <summary>A known row of <see cref="KnownSegmentsHitWhereTheGeometrySays"/>.</summary>
    private sealed record KnownSegment(Vec3 A, Vec3 B, bool Hits, float T, Vec3 Point)
    {
        /// <summary>True when the hit's t must have the bits of +0, as a start inside gives.</summary>
        public bool ExactZero { get; init; }
    }

    /// <summary>One drawn segment and capsule, as the float values the core receives.</summary>
    private readonly record struct Draw(Vec3 A, Vec3 B, Vec3 CapsuleA, Vec3 CapsuleB, float Radius)
    {
        public override string ToString() => FormattableString.Invariant(
            $"segment {Bits(A)} to {Bits(B)}, capsule {Bits(CapsuleA)} to {Bits(CapsuleB)} radius 0x{BitConverter.SingleToUInt32Bits(Radius):X8} {Radius:R}");

        private static string Bits(Vec3 v) => FormattableString.Invariant(
            $"(0x{BitConverter.SingleToUInt32Bits(v.X):X8} {v.X:R}, 0x{BitConverter.SingleToUInt32Bits(v.Y):X8} {v.Y:R}, 0x{BitConverter.SingleToUInt32Bits(v.Z):X8} {v.Z:R})");
    }
}
