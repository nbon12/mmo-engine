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

    /// <summary>
    /// Sweeps the capsule whose axis runs from <paramref name="axisBottom"/> to <paramref name="axisTop"/>,
    /// of radius <paramref name="radius"/>, by <paramref name="displacement"/>, and returns the first contact
    /// at distance r + <paramref name="skin"/>. Candidates are the box query over the swept box grown by
    /// r + skin, each culled by its bounding box and its plane; the four boundary planes (IDs −4 to −1) are
    /// tested as walls. Each triangle runs conservative advancement on f(t) = dist(axis at t, triangle) −
    /// (r + skin): at most <see cref="MaxIterations"/> Newton steps, the axis recomputed from the start each
    /// time, stopping when f ≤ 0.1·skin, missing when the approach speed is at most 1e-4·|d| (the gap never
    /// closes, which also lets a capsule slide along what it touches) or a step passes t = 1, and stopping
    /// short at the cap. Since f is convex, no step passes the true time of impact. The least t wins, ties to
    /// the lower ID, so a boundary plane wins a tie. Triangles are double-sided.
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

        var best = default(SweepHit);
        var bestT = 2f;

        // The boundary planes first, IDs −4 to −1, so they win ties with any triangle.
        var heightfield = world.Heightfield;
        for (var id = Heightfield.BoundaryMinX; id <= Heightfield.BoundaryMaxZ; id++)
        {
            heightfield.GetBoundaryPlane(id, out var normal, out var offset);
            var approach = 0f - Vec3.Dot(normal, displacement);
            if (approach <= minimumApproach)
            {
                continue;
            }

            var gap = Vec3.Dot(normal, axisBottom) - offset;
            var f = gap - target;
            var t = f <= tolerance ? 0f : f / approach;
            if (t > 1f || !(t < bestT))
            {
                continue;
            }

            var at = axisBottom + (displacement * t);
            best = new SweepHit(t, id, normal, normal, true, at - (normal * (gap - (approach * t))));
            bestT = t;
        }

        // The candidate triangles, ascending, in the local frame of the axis bottom.
        var origin = ClosestPoints.LocalOrigin(axisBottom.X, axisBottom.Z);
        var bottom = axisBottom - origin;
        var top = axisTop - origin;
        var cullReach = target + skin;
        for (var k = 0; k < queryCount; k++)
        {
            var id = candidates[k];
            world.GetTriangle(id, out var a, out var b, out var c, out var faceNormal);
            if (SimMath.Max(SimMath.Max(a.Y, b.Y), c.Y) < minY || SimMath.Min(SimMath.Min(a.Y, b.Y), c.Y) > maxY)
            {
                continue;
            }

            a -= origin;
            b -= origin;
            c -= origin;
            if (OutsidePlaneReach(faceNormal, a, bottom, top, displacement, cullReach))
            {
                continue;
            }

            if (Advance(bottom, top, displacement, a, b, c, faceNormal, target, tolerance, minimumApproach, out var t, out var contact)
                && t < bestT)
            {
                best = new SweepHit(t, id, contact.Normal, contact.FaceNormal, contact.IsFace, contact.Point + origin);
                bestT = t;
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
    /// Conservative advancement against one triangle, in the local frame. True with the contact time and
    /// what is known at it when the capsule touches the triangle within the displacement.
    /// </summary>
    private static bool Advance(
        Vec3 bottom, Vec3 top, Vec3 displacement, Vec3 a, Vec3 b, Vec3 c, Vec3 faceNormal, float target, float tolerance, float minimumApproach, out float t, out Contact contact)
    {
        t = 0f;
        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            // The axis at t, recomputed from the start, so no error accumulates.
            var step = displacement * t;
            var atBottom = bottom + step;
            var atTop = top + step;
            var distance = ClosestPoints.SegmentTriangle(atBottom, atTop, a, b, c, out _, out var onTriangle, out var separation, out var feature);

            Vec3 direction;
            Vec3 facing;
            if (distance > 0f)
            {
                direction = Divide(separation, distance);
                facing = Facing(faceNormal, separation);
            }
            else
            {
                // The axis crosses the face: the face normal against the motion.
                facing = Facing(faceNormal, Negate(displacement));
                direction = facing;
            }

            var approach = 0f - Vec3.Dot(displacement, direction);
            if (approach <= minimumApproach)
            {
                // The gap is not closing (f is convex, so it never will): a miss, or a capsule sliding along
                // or leaving what it touches.
                contact = default;
                return false;
            }

            var f = distance - target;
            if (f <= tolerance || iteration == MaxIterations - 1)
            {
                var isFace = feature == TriangleFeature.Face || distance <= 0f;
                contact = new Contact(isFace ? facing : direction, facing, isFace, onTriangle);
                return true;
            }

            t += f / approach;
            if (t > 1f)
            {
                contact = default;
                return false;
            }
        }

        contact = default;
        return false;
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
    private readonly struct Contact(Vec3 normal, Vec3 faceNormal, bool isFace, Vec3 point)
    {
        public Vec3 Normal { get; } = normal;

        public Vec3 FaceNormal { get; } = faceNormal;

        public bool IsFace { get; } = isFace;

        public Vec3 Point { get; } = point;
    }
}
