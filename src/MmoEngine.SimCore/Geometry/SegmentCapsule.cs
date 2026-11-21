using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Geometry;

/// <summary>
/// The segment–capsule test of the projectile step (FR-010, FR-011, research R11,
/// specs/015-shared-simulation-core/data-model.md#projectile-step step 3). A step's segment a → b is first
/// clipped to the target's bounding sphere with <see cref="ClipToSphere"/>, giving [t0, t1] ⊆ [0, 1], and
/// the short clipped segment p = a + ((b − a)·t0) → q = a + ((b − a)·t1) is then solved against each
/// capsule with <see cref="Intersect"/>, giving tc; the parameter on the original segment is
/// <c>t0 + ((t1 − t0)·tc)</c>, so a start inside gives exactly 0. Clipping first keeps the capsule's
/// quadratic on a segment about a metre long, where float keeps its digits: research R11 measured no
/// hit/miss disagreement and a worst hit-point error of 0.11 mm against double over 300,000 segments up to
/// 90 m, against 83 disagreements unclipped.
/// </summary>
/// <remarks>
/// Every quadratic is solved in the stable form of Ray Tracing Gems ch. 7 (Haines et al., "Precision
/// Improvements for Ray/Sphere Intersection"): the discriminant is built from the squared distance of the
/// line to the centre, not as the difference b² − a·c, and each root is taken from the sum that does not
/// cancel. Float32 only, with fixed parenthesisation, <see cref="SimMath"/> for the square roots, one code
/// path for every build, and no allocation.
/// </remarks>
public static class SegmentCapsule
{
    /// <summary>
    /// A segment whose direction makes sin²θ below this with the capsule's axis is parallel to it, and is
    /// left to the end spheres: <c>|n⊥|² &lt; 1e-10·|n|²</c>, the threshold <see cref="ClosestPoints"/> uses.
    /// </summary>
    private const float ParallelThreshold = 1e-10f;

    /// <summary>
    /// Clips segment <paramref name="a"/> → <paramref name="b"/> to the sphere at <paramref name="centre"/>
    /// with <paramref name="radius"/>. With d = b − a and f = a − centre, the line misses when
    /// Δ = r² − |f − (((f·d) / (d·d))·d)|² &lt; 0; otherwise
    /// q = −(f·d) + sign(−(f·d))·SimMath.Sqrt((d·d)·Δ), and the roots (f·f − r²) / q and q / (d·d), sorted,
    /// bound the part of the line inside the sphere. The segment misses when that part lies wholly before
    /// its start or past its end; otherwise the roots are clamped to [0, 1], so 0 ≤ t0 ≤ t1 ≤ 1. A
    /// zero-length segment is tested as a point: inside (|f|² ≤ r²) gives t0 = t1 = 0.
    /// </summary>
    /// <returns>True when part of the segment lies inside the sphere; t0 and t1 are then that part's bounds.</returns>
    public static bool ClipToSphere(Vec3 a, Vec3 b, Vec3 centre, float radius, out float t0, out float t1)
    {
        var d = b - a;
        var f = a - centre;
        var dd = Vec3.Dot(d, d);
        var radiusSq = radius * radius;
        var c = Vec3.Dot(f, f) - radiusSq;
        if (dd == 0f)
        {
            t0 = 0f;
            t1 = 0f;
            return c <= 0f;
        }

        var fd = Vec3.Dot(f, d);
        var perpendicular = f - (d * (fd / dd));
        var delta = radiusSq - Vec3.Dot(perpendicular, perpendicular);
        if (delta < 0f)
        {
            t0 = 0f;
            t1 = 0f;
            return false;
        }

        SolveStable(-fd, dd, delta, c, out var low, out var high);
        if (high < 0f || low > 1f)
        {
            t0 = 0f;
            t1 = 0f;
            return false;
        }

        t0 = SimMath.Clamp(low, 0f, 1f);
        t1 = SimMath.Clamp(high, 0f, 1f);
        return true;
    }

    /// <summary>
    /// The first point of segment <paramref name="p"/> → <paramref name="q"/> inside the capsule around
    /// <paramref name="capsuleA"/> → <paramref name="capsuleB"/> with <paramref name="radius"/>: the union of
    /// the cylinder between the end planes (Ericson, <i>Real-Time Collision Detection</i>, §5.3.7, solved in
    /// the plane across the axis) and the two end spheres, keeping the smallest entry t in [0, 1]. A start
    /// inside the capsule (within the radius of the axis segment) returns t = 0. A zero-length segment is
    /// tested as a point. A segment parallel to the axis meets the capsule only through its end spheres.
    /// </summary>
    /// <returns>True when the segment enters (or starts inside) the capsule; t is then the entry fraction.</returns>
    public static bool Intersect(Vec3 p, Vec3 q, Vec3 capsuleA, Vec3 capsuleB, float radius, out float t)
    {
        var axis = capsuleB - capsuleA;
        var m = p - capsuleA;
        var n = q - p;
        var dd = Vec3.Dot(axis, axis);
        var md = Vec3.Dot(m, axis);
        var nd = Vec3.Dot(n, axis);
        var nn = Vec3.Dot(n, n);
        var radiusSq = radius * radius;

        // m⊥ and n⊥: the start and the direction across the axis. A capsule whose axis has zero length is a
        // sphere, and every point lies at or beyond its end plane.
        var mPerpendicular = dd > 0f ? m - (axis * (md / dd)) : m;
        var nPerpendicular = dd > 0f ? n - (axis * (nd / dd)) : n;
        var cylinderC = Vec3.Dot(mPerpendicular, mPerpendicular) - radiusSq;

        // A start inside: within the radius of A beyond A's end plane, of B beyond B's, and of the axis
        // between them.
        var startInside = md <= 0f
            ? Vec3.Dot(m, m) - radiusSq <= 0f
            : (md >= dd ? Vec3.LengthSquared(p - capsuleB) - radiusSq <= 0f : cylinderC <= 0f);
        if (startInside)
        {
            t = 0f;
            return true;
        }

        if (nn == 0f)
        {
            t = 0f;
            return false;
        }

        var found = false;
        var best = 1f;

        // The cylinder between the end planes, entered from outside it: |m⊥ + t·n⊥|² = r² with c > 0, whose
        // first root is ahead only when the segment moves toward the axis.
        var nnPerpendicular = Vec3.Dot(nPerpendicular, nPerpendicular);
        if (cylinderC > 0f && nnPerpendicular >= ParallelThreshold * nn)
        {
            var mn = Vec3.Dot(mPerpendicular, nPerpendicular);
            if (mn < 0f)
            {
                var across = mPerpendicular - (nPerpendicular * (mn / nnPerpendicular));
                var delta = radiusSq - Vec3.Dot(across, across);
                if (delta >= 0f)
                {
                    // The entry lies on the cylinder only between the end planes; elsewhere an end sphere
                    // holds it.
                    SolveStable(-mn, nnPerpendicular, delta, cylinderC, out var entry, out _);
                    var along = md + (entry * nd);
                    if (entry <= best && along >= 0f && along <= dd)
                    {
                        best = entry;
                        found = true;
                    }
                }
            }
        }

        // The end spheres, entered from outside each.
        if (SphereEntry(m, n, nn, radiusSq, out var entryA) && entryA <= best)
        {
            best = entryA;
            found = true;
        }

        if (SphereEntry(p - capsuleB, n, nn, radiusSq, out var entryB) && entryB <= best)
        {
            best = entryB;
            found = true;
        }

        t = found ? best : 0f;
        return found;
    }

    /// <summary>
    /// The entry of the segment from f (its start relative to a sphere's centre) along n (|n|² =
    /// <paramref name="nn"/> &gt; 0) into the sphere of squared radius <paramref name="radiusSq"/>, within the
    /// segment. A start inside or on the sphere enters at 0; a start outside enters only when the segment
    /// moves toward the centre and the line comes within the radius.
    /// </summary>
    private static bool SphereEntry(Vec3 f, Vec3 n, float nn, float radiusSq, out float t)
    {
        // A start on or inside the sphere enters at 0. Intersect's own start-inside test already holds the
        // regions beyond the end planes, where this is the same comparison; between them it can differ from
        // the cylinder's only by rounding at the surface.
        var c = Vec3.Dot(f, f) - radiusSq;
        var fn = Vec3.Dot(f, n);
        if (c <= 0f || fn >= 0f)
        {
            t = 0f;
            return c <= 0f;
        }

        var across = f - (n * (fn / nn));
        var delta = radiusSq - Vec3.Dot(across, across);
        if (delta < 0f)
        {
            t = 0f;
            return false;
        }

        SolveStable(-fn, nn, delta, c, out var entry, out _);
        var within = entry <= 1f;
        t = within ? entry : 0f;
        return within;
    }

    /// <summary>
    /// The two roots, sorted, of a·t² − 2·h·t + c = 0, where <paramref name="h"/> = −(f·d), a =
    /// <paramref name="a"/> &gt; 0 and the reduced discriminant is a·Δ with Δ = <paramref name="delta"/> ≥ 0:
    /// q = h + sign(h)·SimMath.Sqrt(a·Δ), and the roots c / q and q / a. A zero q (h and Δ both 0, a line
    /// that touches the sphere where the segment starts) gives the double root 0.
    /// </summary>
    private static void SolveStable(float h, float a, float delta, float c, out float low, out float high)
    {
        var root = SimMath.Sqrt(a * delta);
        var q = h >= 0f ? h + root : h - root;
        var second = q / a;
        var first = q == 0f ? second : c / q;
        low = SimMath.Min(first, second);
        high = SimMath.Max(first, second);
    }
}
