using System;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Collision;

/// <summary>
/// Capsule queries against a collision world (FR-003, specs/015-shared-simulation-core/research.md R7):
/// the sweep by conservative advancement, and the nearest triangle to a resting capsule's axis, which the
/// movement step's push-out and headroom test use. The capsule is its axis segment and a radius. Every
/// query takes its candidates from <see cref="CollisionWorld.QueryBox"/> into the caller's span, works in
/// the local frame of <see cref="ClosestPoints.LocalOrigin"/> of the axis bottom, allocates nothing and
/// reads no mutable state.
/// </summary>
public static class CapsuleSweep
{
    /// <summary>The time-of-impact iteration cap per triangle (data-model.md#movement-configuration).</summary>
    public const int MaxIterations = 8;

    /// <summary>The sweep tolerance as a fraction of the skin: a contact within r + skin + this·skin stops.</summary>
    public const float ToleranceFactor = 0.1f;

    /// <summary>The minimum approach as a fraction of |d|: slower approaches never close the gap.</summary>
    public const float MinimumApproachFactor = 1e-4f;

    /// <summary>The four boundary planes, IDs −4 to −1.</summary>
    private const int BoundaryPlaneCount = 4;

    /// <summary>A stop time past every displacement: no hit.</summary>
    private const float NoHit = 2f;

    /// <summary>
    /// Sweeps the capsule whose axis runs from <paramref name="axisBottom"/> to <paramref name="axisTop"/>,
    /// of radius <paramref name="radius"/>, by <paramref name="displacement"/>, and returns the first contact
    /// at distance r + <paramref name="skin"/>. Candidates are the box query over the swept box grown by
    /// r + skin, each culled by its bounding box and its plane; the four boundary planes (IDs −4 to −1) are
    /// tested as walls. Each triangle runs conservative advancement on f(t) = dist(axis at t, triangle) −
    /// (r + skin): at most <see cref="MaxIterations"/> Newton steps, the axis recomputed from the start each
    /// time, stopping when f ≤ 0.1·skin, missing when the approach speed is at most 1e-4·|d| (the gap never
    /// closes, which also lets a capsule slide along what it touches) or a step passes t = 1, and stopping
    /// short at the cap. Since f is convex, no step passes the true time of impact. The least stop time is the
    /// sweep's <see cref="SweepHit.T"/>. A boundary plane that stops at exactly that time is the contact, so
    /// boundary planes win ties; otherwise the contact is the nearest, at that time, of the planes and
    /// triangles that hit, ties to the lower ID. Measuring the contact at the common time, rather than taking
    /// whichever triangle's stop landed earliest inside its tolerance band, keeps the edge two coplanar
    /// triangles share from tilting a flat wall's normal. Triangles are double-sided.
    /// </summary>
    /// <param name="queryCount">The box query's count, or −1 when it overflowed <paramref name="candidates"/>; the sweep is then a miss and the caller refuses the step.</param>
    public static SweepHit Sweep(
        CollisionWorld world, Vec3 axisBottom, Vec3 axisTop, float radius, float skin, Vec3 displacement, Span<int> candidates, out int queryCount)
    {
        ArgumentNullException.ThrowIfNull(world);
        var target = radius + skin;
        var tolerance = ToleranceFactor * skin;
        var minimumApproach = MinimumApproachFactor * Vec3.Length(displacement);
        var endBottom = axisBottom + displacement;
        var endTop = axisTop + displacement;
        var minX = SimMath.Min(SimMath.Min(axisBottom.X, endBottom.X), SimMath.Min(axisTop.X, endTop.X));
        var minY = SimMath.Min(SimMath.Min(axisBottom.Y, endBottom.Y), SimMath.Min(axisTop.Y, endTop.Y));
        var minZ = SimMath.Min(SimMath.Min(axisBottom.Z, endBottom.Z), SimMath.Min(axisTop.Z, endTop.Z));
        var maxX = SimMath.Max(SimMath.Max(axisBottom.X, endBottom.X), SimMath.Max(axisTop.X, endTop.X));
        var maxY = SimMath.Max(SimMath.Max(axisBottom.Y, endBottom.Y), SimMath.Max(axisTop.Y, endTop.Y));
        var maxZ = SimMath.Max(SimMath.Max(axisBottom.Z, endBottom.Z), SimMath.Max(axisTop.Z, endTop.Z));
        (minX, minY, minZ) = (minX - target, minY - target, minZ - target);
        (maxX, maxY, maxZ) = (maxX + target, maxY + target, maxZ + target);
        queryCount = world.QueryBox(minX, minY, minZ, maxX, maxY, maxZ, candidates);
        if (queryCount < 0)
        {
            return default;
        }

        // Pass 1: every plane's and triangle's stop time, and the least of them. A candidate that does not
        // hit is marked −1 in the span, which is the caller's scratch, so pass 2 skips it.
        var heightfield = world.Heightfield;
        var bestT = NoHit;
        Span<float> planeStops = stackalloc float[BoundaryPlaneCount];
        for (var i = 0; i < BoundaryPlaneCount; i++)
        {
            heightfield.GetBoundaryPlane(Heightfield.BoundaryMinX + i, out var normal, out var offset);
            planeStops[i] = PlaneStop(normal, offset, axisBottom, displacement, target, tolerance, minimumApproach);
            bestT = SimMath.Min(bestT, planeStops[i]);
        }

        var origin = ClosestPoints.LocalOrigin(axisBottom.X, axisBottom.Z);
        var bottom = axisBottom - origin;
        var top = axisTop - origin;
        var cullReach = target + skin;
        for (var k = 0; k < queryCount; k++)
        {
            world.GetTriangle(candidates[k], out var a, out var b, out var c, out var faceNormal);
            var hits = false;
            if (SimMath.Max(SimMath.Max(a.Y, b.Y), c.Y) >= minY && SimMath.Min(SimMath.Min(a.Y, b.Y), c.Y) <= maxY)
            {
                a -= origin;
                b -= origin;
                c -= origin;
                if (!OutsidePlaneReach(faceNormal, a, bottom, top, displacement, cullReach)
                    && Advance(bottom, top, displacement, a, b, c, faceNormal, target, tolerance, minimumApproach, out var t))
                {
                    hits = true;
                    bestT = SimMath.Min(bestT, t);
                }
            }

            if (!hits)
            {
                candidates[k] = -1;
            }
        }

        if (!(bestT <= 1f))
        {
            return default;
        }

        // A boundary plane that stops at exactly the sweep's time is the contact: boundary planes win ties.
        for (var i = 0; i < BoundaryPlaneCount; i++)
        {
            if (planeStops[i] == bestT)
            {
                return PlaneHit(heightfield, Heightfield.BoundaryMinX + i, axisBottom, displacement, bestT, out _);
            }
        }

        // Pass 2: the nearest, at the sweep's time, of the planes and triangles that hit; ties to the lower ID.
        var best = default(SweepHit);
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i < BoundaryPlaneCount; i++)
        {
            if (planeStops[i] <= 1f)
            {
                var hit = PlaneHit(heightfield, Heightfield.BoundaryMinX + i, axisBottom, displacement, bestT, out var distance);
                if (distance < bestDistance)
                {
                    (best, bestDistance) = (hit, distance);
                }
            }
        }

        var step = displacement * bestT;
        var atBottom = bottom + step;
        var atTop = top + step;
        for (var k = 0; k < queryCount; k++)
        {
            var id = candidates[k];
            if (id < 0)
            {
                continue;
            }

            world.GetTriangle(id, out var a, out var b, out var c, out var faceNormal);
            var contact = ContactAt(atBottom, atTop, displacement, a - origin, b - origin, c - origin, faceNormal, out var distance);
            if (distance < bestDistance)
            {
                best = new SweepHit(bestT, id, contact.Normal, contact.FaceNormal, contact.IsFace, contact.Point + origin);
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// The triangle nearest the axis from <paramref name="axisBottom"/> to <paramref name="axisTop"/> among
    /// those within <paramref name="reach"/> of the axis's box: its distance, ID and the unit direction from
    /// it toward the axis (the face normal, turned toward the axis, for a face contact or a crossing). Ties
    /// go to the lower ID. Returns false, with distance +∞, when no triangle is within the box; and with
    /// <paramref name="queryCount"/> −1 when the box query overflowed <paramref name="candidates"/>.
    /// </summary>
    internal static bool Nearest(
        CollisionWorld world, Vec3 axisBottom, Vec3 axisTop, float reach, Span<int> candidates, out int queryCount, out float distance, out int triangleId, out Vec3 direction)
    {
        distance = float.PositiveInfinity;
        triangleId = 0;
        direction = new Vec3(0f, 1f, 0f);
        queryCount = world.QueryBox(
            SimMath.Min(axisBottom.X, axisTop.X) - reach,
            SimMath.Min(axisBottom.Y, axisTop.Y) - reach,
            SimMath.Min(axisBottom.Z, axisTop.Z) - reach,
            SimMath.Max(axisBottom.X, axisTop.X) + reach,
            SimMath.Max(axisBottom.Y, axisTop.Y) + reach,
            SimMath.Max(axisBottom.Z, axisTop.Z) + reach,
            candidates);
        if (queryCount <= 0)
        {
            return false;
        }

        var origin = ClosestPoints.LocalOrigin(axisBottom.X, axisBottom.Z);
        var bottom = axisBottom - origin;
        var top = axisTop - origin;
        var found = false;
        for (var k = 0; k < queryCount; k++)
        {
            var id = candidates[k];
            world.GetTriangle(id, out var a, out var b, out var c, out var faceNormal);
            a -= origin;
            b -= origin;
            c -= origin;
            var d = ClosestPoints.SegmentTriangle(bottom, top, a, b, c, out _, out _, out var separation, out var feature);
            if (d < distance)
            {
                distance = d;
                triangleId = id;
                direction = feature == TriangleFeature.Face || d <= 0f
                    ? Facing(faceNormal, d <= 0f ? Midpoint(bottom, top) - a : separation)
                    : Divide(separation, d);
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// <paramref name="normal"/> turned to face <paramref name="toward"/> (a vector from the triangle toward
    /// the capsule): negated when their dot product is negative, and when it is 0, turned to have y ≥ 0.
    /// Negation is written as <c>0f − x</c>, so it makes no −0.
    /// </summary>
    internal static Vec3 Facing(Vec3 normal, Vec3 toward)
    {
        var side = Vec3.Dot(normal, toward);
        return side < 0f || (side == 0f && normal.Y < 0f) ? Negate(normal) : normal;
    }

    /// <summary>The vector with every component negated as <c>0f − x</c>, so +0 stays +0.</summary>
    internal static Vec3 Negate(Vec3 v) => new(0f - v.X, 0f - v.Y, 0f - v.Z);

    /// <summary>
    /// Conservative advancement against one triangle, in the local frame: true with the stop time when the
    /// capsule touches the triangle within the displacement.
    /// </summary>
    private static bool Advance(
        Vec3 bottom, Vec3 top, Vec3 displacement, Vec3 a, Vec3 b, Vec3 c, Vec3 faceNormal, float target, float tolerance, float minimumApproach, out float t)
    {
        t = 0f;
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            // The axis at t, recomputed from the start, so no error accumulates.
            var step = displacement * t;
            var contact = ContactAt(bottom + step, top + step, displacement, a, b, c, faceNormal, out var distance);
            var approach = 0f - Vec3.Dot(displacement, contact.Direction);
            if (approach <= minimumApproach)
            {
                // The gap is not closing (f is convex, so it never will): a miss, or a capsule sliding along
                // or leaving what it touches.
                return false;
            }

            var f = distance - target;
            if (f <= tolerance || iteration == MaxIterations - 1)
            {
                return true;
            }

            t += f / approach;
            if (t > 1f)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The contact between the axis from <paramref name="bottom"/> to <paramref name="top"/> and one triangle,
    /// in the local frame, with its distance: the unit direction from the triangle toward the axis (the face
    /// normal against the motion when the axis crosses the face), the face normal turned toward the capsule,
    /// whether the closest point is inside the face, the contact normal (the face normal for a face contact
    /// or a crossing, else the direction), and the closest point on the triangle.
    /// </summary>
    private static Contact ContactAt(Vec3 bottom, Vec3 top, Vec3 displacement, Vec3 a, Vec3 b, Vec3 c, Vec3 faceNormal, out float distance)
    {
        distance = ClosestPoints.SegmentTriangle(bottom, top, a, b, c, out _, out var onTriangle, out var separation, out var feature);
        if (distance > 0f)
        {
            var facing = Facing(faceNormal, separation);
            var isFace = feature == TriangleFeature.Face;
            var direction = Divide(separation, distance);
            return new Contact(direction, isFace ? facing : direction, facing, isFace, onTriangle);
        }

        // The axis crosses the face: the face normal against the motion.
        var against = Facing(faceNormal, Negate(displacement));
        return new Contact(against, against, against, true, onTriangle);
    }

    /// <summary>A boundary plane's stop time, or <see cref="NoHit"/> when the capsule does not reach it within the displacement.</summary>
    private static float PlaneStop(Vec3 normal, float offset, Vec3 axisBottom, Vec3 displacement, float target, float tolerance, float minimumApproach)
    {
        var approach = 0f - Vec3.Dot(normal, displacement);
        if (approach <= minimumApproach)
        {
            return NoHit;
        }

        var f = (Vec3.Dot(normal, axisBottom) - offset) - target;
        var t = f <= tolerance ? 0f : f / approach;
        return t > 1f ? NoHit : t;
    }

    /// <summary>The contact with boundary plane <paramref name="id"/> at <paramref name="t"/>, with the axis's distance from it then.</summary>
    private static SweepHit PlaneHit(Heightfield heightfield, int id, Vec3 axisBottom, Vec3 displacement, float t, out float distance)
    {
        heightfield.GetBoundaryPlane(id, out var normal, out var offset);
        var gap = Vec3.Dot(normal, axisBottom) - offset;
        distance = gap - ((0f - Vec3.Dot(normal, displacement)) * t);
        var at = axisBottom + (displacement * t);
        return new SweepHit(t, id, normal, normal, true, at - (normal * distance));
    }

    /// <summary>
    /// True when the triangle's plane is further than <paramref name="reach"/> from all four ends of the swept
    /// axis on the same side, so the capsule can never come within reach of the triangle.
    /// </summary>
    private static bool OutsidePlaneReach(Vec3 normal, Vec3 a, Vec3 bottom, Vec3 top, Vec3 displacement, float reach)
    {
        var s0 = Vec3.Dot(normal, bottom - a);
        var s1 = Vec3.Dot(normal, top - a);
        var s2 = Vec3.Dot(normal, (bottom + displacement) - a);
        var s3 = Vec3.Dot(normal, (top + displacement) - a);
        var low = SimMath.Min(SimMath.Min(s0, s1), SimMath.Min(s2, s3));
        var high = SimMath.Max(SimMath.Max(s0, s1), SimMath.Max(s2, s3));
        return low > reach || high < 0f - reach;
    }

    private static Vec3 Midpoint(Vec3 a, Vec3 b) => (a + b) * 0.5f;

    /// <summary>Each component divided by <paramref name="s"/>, so a vector along an axis normalises exactly.</summary>
    private static Vec3 Divide(Vec3 v, float s) => new(v.X / s, v.Y / s, v.Z / s);

    /// <summary>What a sweep knows at its contact, in the local frame.</summary>
    private readonly struct Contact(Vec3 direction, Vec3 normal, Vec3 faceNormal, bool isFace, Vec3 point)
    {
        public Vec3 Direction { get; } = direction;

        public Vec3 Normal { get; } = normal;

        public Vec3 FaceNormal { get; } = faceNormal;

        public bool IsFace { get; } = isFace;

        public Vec3 Point { get; } = point;
    }
}
