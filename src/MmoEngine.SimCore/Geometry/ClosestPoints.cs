using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Geometry;

/// <summary>
/// Which feature of a triangle a closest point lies on: the interior of its face, one of its edges, or one
/// of its vertices. A capsule sweep takes a face contact's normal from the face and an edge or vertex
/// contact's from the direction between the closest points (research R7).
/// </summary>
internal enum TriangleFeature : byte
{
    /// <summary>The interior of the face.</summary>
    Face = 0,

    /// <summary>An edge, between its two vertices.</summary>
    Edge = 1,

    /// <summary>A vertex.</summary>
    Vertex = 2,
}

/// <summary>
/// Closest points between points, segments and triangles, in <see cref="float"/>, after Ericson,
/// <i>Real-Time Collision Detection</i>, §5.1.5 (point–triangle), §5.1.9 (segment–segment) and §5.1.10
/// (segment–triangle) (FR-003, specs/015-shared-simulation-core/research.md R6 and R7). Each query
/// returns the distance and writes the closest points.
/// </summary>
/// <remarks>
/// <para>
/// The queries work in the frame they are given. A caller subtracts <see cref="LocalOrigin"/> of a point
/// near the query from every input first, which is exact for the point it was snapped from (Sterbenz's
/// lemma) and keeps the coordinates within about 34 m of the origin wherever in the 8 km world the query
/// lies, runs the query, and adds the origin back to any point it keeps.
/// </para>
/// <para>
/// Determinism: every candidate is tried in a fixed order and replaces the best only when strictly
/// closer, every square root is <see cref="SimMath.Sqrt"/>, and every expression keeps <see cref="Vec3"/>'s
/// parenthesisation. The distance is the length of the separation vector, built from the inputs' own
/// differences (such as <c>(p − a) − ((b − a)·v + (c − a)·w)</c>), never from the returned points, which
/// carry their coordinates' own rounding. Segment pairs whose directions have sin²θ below 1e-10 are
/// treated as parallel (§5.1.9 then takes s = 0).
/// </para>
/// </remarks>
public static class ClosestPoints
{
    /// <summary>The spacing of the local-origin lattice in X and Z, m.</summary>
    public const float LatticeSpacing = 64f;

    /// <summary>Two segment directions with sin²θ below this are parallel: <c>a·e − b² &lt; 1e-10·(a·e)</c>.</summary>
    internal const float ParallelThreshold = 1e-10f;

    /// <summary>The reciprocal of <see cref="LatticeSpacing"/>, a power of two, so the scaling is exact.</summary>
    private const float InverseLatticeSpacing = 1f / LatticeSpacing;

    /// <summary>
    /// The point of the 64 m lattice nearest (<paramref name="x"/>, <paramref name="z"/>) in X and in Z
    /// (ties to even), at y +0. A position minus it is exact in float and lies within 32 m of the origin
    /// along each axis.
    /// </summary>
    public static Vec3 LocalOrigin(float x, float z) => new(Snap(x), 0f, Snap(z));

    /// <summary>
    /// The distance from <paramref name="p"/> to triangle (<paramref name="a"/>, <paramref name="b"/>,
    /// <paramref name="c"/>) and its closest point on it, by Ericson §5.1.5's region tests in order: vertex
    /// A, vertex B, edge AB, vertex C, edge AC, edge BC, then the face.
    /// </summary>
    public static float PointTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out Vec3 closest) =>
        PointTriangle(p, a, b, c, out closest, out _, out _);

    /// <summary>
    /// The distance between segments [<paramref name="p1"/>, <paramref name="q1"/>] and
    /// [<paramref name="p2"/>, <paramref name="q2"/>] and their closest points, by Ericson §5.1.9. A
    /// segment of zero length is a point; directions with sin²θ below 1e-10 are parallel and take s = 0.
    /// The pair §5.1.9 finds is then checked against each end measured against the other segment, in the
    /// order P1, Q1, P2, Q2, and replaced by one that is strictly closer, so segments that meet at an end
    /// measure 0 exactly.
    /// </summary>
    public static float SegmentSegment(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out Vec3 c1, out Vec3 c2) =>
        SegmentSegment(p1, q1, p2, q2, out c1, out c2, out _, out _);

    /// <summary>
    /// The distance between segment [<paramref name="p"/>, <paramref name="q"/>] and triangle
    /// (<paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>) and their closest points, by
    /// Ericson §5.1.10: 0 at the crossing point when the segment passes through the triangle, otherwise the
    /// closest of, in this order, P against the triangle, Q against the triangle, and the segment against
    /// edges AB, BC and CA.
    /// </summary>
    public static float SegmentTriangle(Vec3 p, Vec3 q, Vec3 a, Vec3 b, Vec3 c, out Vec3 onSegment, out Vec3 onTriangle) =>
        SegmentTriangle(p, q, a, b, c, out onSegment, out onTriangle, out _, out _);

    /// <summary>
    /// <see cref="PointTriangle(Vec3, Vec3, Vec3, Vec3, out Vec3)"/>, also writing the separation
    /// <c>p − closest</c> (computed from the inputs' differences) and the feature the closest point lies on.
    /// </summary>
    internal static float PointTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out Vec3 closest, out Vec3 separation, out TriangleFeature feature)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = Vec3.Dot(ab, ap);
        var d2 = Vec3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f)
        {
            // Vertex region A.
            closest = a;
            separation = ap;
            feature = TriangleFeature.Vertex;
            return Vec3.Length(separation);
        }

        var bp = p - b;
        var d3 = Vec3.Dot(ab, bp);
        var d4 = Vec3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3)
        {
            // Vertex region B.
            closest = b;
            separation = bp;
            feature = TriangleFeature.Vertex;
            return Vec3.Length(separation);
        }

        var vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            // Edge region AB.
            var v = d1 / (d1 - d3);
            var along = ab * v;
            closest = a + along;
            separation = ap - along;
            feature = TriangleFeature.Edge;
            return Vec3.Length(separation);
        }

        var cp = p - c;
        var d5 = Vec3.Dot(ab, cp);
        var d6 = Vec3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6)
        {
            // Vertex region C.
            closest = c;
            separation = cp;
            feature = TriangleFeature.Vertex;
            return Vec3.Length(separation);
        }

        var vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            // Edge region AC.
            var w = d2 / (d2 - d6);
            var along = ac * w;
            closest = a + along;
            separation = ap - along;
            feature = TriangleFeature.Edge;
            return Vec3.Length(separation);
        }

        var va = (d3 * d6) - (d5 * d4);
        var d43 = d4 - d3;
        var d56 = d5 - d6;
        if (va <= 0f && d43 >= 0f && d56 >= 0f)
        {
            // Edge region BC.
            var w = d43 / (d43 + d56);
            var along = (c - b) * w;
            closest = b + along;
            separation = bp - along;
            feature = TriangleFeature.Edge;
            return Vec3.Length(separation);
        }

        // Face region: the barycentric point (u, v, w) with u = 1 − v − w. Its separation is then projected
        // onto the face normal, which removes the barycentric point's rounding within the plane: that
        // rounding is about 1e-6 m and would otherwise be the whole distance of a point on the face.
        var denominator = 1f / ((va + vb) + vc);
        var faceV = vb * denominator;
        var faceW = vc * denominator;
        var barycentric = ap - ((ab * faceV) + (ac * faceW));
        var normal = Vec3.Cross(ab, ac);
        separation = normal * (Vec3.Dot(barycentric, normal) / Vec3.Dot(normal, normal));
        closest = p - separation;
        feature = TriangleFeature.Face;
        return Vec3.Length(separation);
    }

    /// <summary>
    /// <see cref="SegmentSegment(Vec3, Vec3, Vec3, Vec3, out Vec3, out Vec3)"/>, also writing the separation
    /// <c>c1 − c2</c>, computed as <c>((p1 − p2) + d1·s) − d2·t</c>, and the second segment's parameter
    /// <paramref name="t"/> (0 at <paramref name="p2"/>, 1 at <paramref name="q2"/>).
    /// </summary>
    internal static float SegmentSegment(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out Vec3 c1, out Vec3 c2, out Vec3 separation, out float t)
    {
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vec3.Dot(d1, d1);
        var e = Vec3.Dot(d2, d2);
        var f = Vec3.Dot(d2, r);
        float s;
        var refine = false;
        var b = 0f;
        var denominator = 0f;
        if (a <= 0f && e <= 0f)
        {
            // Both segments are points.
            s = 0f;
            t = 0f;
        }
        else if (a <= 0f)
        {
            // The first segment is a point: s = 0, so t = f / e.
            s = 0f;
            t = SimMath.Clamp(f / e, 0f, 1f);
        }
        else
        {
            var c = Vec3.Dot(d1, r);
            if (e <= 0f)
            {
                // The second segment is a point: t = 0, so s = −c / a.
                t = 0f;
                s = SimMath.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                b = Vec3.Dot(d1, d2);
                var ae = a * e;
                denominator = ae - (b * b);

                // Not parallel: the closest point on line 1 to line 2, clamped to segment 1; parallel: s = 0.
                var parallel = denominator < ParallelThreshold * ae;
                s = parallel ? 0f : SimMath.Clamp(((b * f) - (c * e)) / denominator, 0f, 1f);

                // The point on segment 2 closest to S1(s); if it is clamped, s is recomputed for it.
                t = ((b * s) + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = SimMath.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = SimMath.Clamp((b - c) / a, 0f, 1f);
                }

                refine = !parallel;
            }
        }

        var bestS = s;
        separation = (r + (d1 * s)) - (d2 * t);
        var best = Vec3.Length(separation);

        if (refine)
        {
            // One step of iterative refinement: the same solve applied to the residual separation, whose
            // dot products are small and so carry little rounding, then clamped. For a pair that nearly
            // meets, b·f − c·e above loses the digits that place s and t (and may clamp an end that should
            // not be); this recovers them. Kept only when strictly closer.
            var g1 = Vec3.Dot(d1, separation);
            var g2 = Vec3.Dot(d2, separation);
            var refinedS = SimMath.Clamp(s + (((b * g2) - (g1 * e)) / denominator), 0f, 1f);
            var refinedT = SimMath.Clamp(t + (((a * g2) - (b * g1)) / denominator), 0f, 1f);
            var refined = (r + (d1 * refinedS)) - (d2 * refinedT);
            var refinedDistance = Vec3.Length(refined);
            if (refinedDistance < best)
            {
                (best, bestS, t, separation) = (refinedDistance, refinedS, refinedT, refined);
            }
        }

        // Each end against the other segment, kept only when strictly closer. Where the segments meet at
        // an end, or an end lies on the other segment, the pair above is off by the rounding of s and t
        // (about 1e-6 m); these measure that end exactly.
        var endT = PointSegmentParameter(r, d2, e);
        var candidate = r - (d2 * endT);
        var distance = Vec3.Length(candidate);
        if (distance < best)
        {
            (best, bestS, t, separation) = (distance, 0f, endT, candidate);
        }

        var fromQ1 = q1 - p2;
        endT = PointSegmentParameter(fromQ1, d2, e);
        candidate = fromQ1 - (d2 * endT);
        distance = Vec3.Length(candidate);
        if (distance < best)
        {
            (best, bestS, t, separation) = (distance, 1f, endT, candidate);
        }

        var fromP1 = p2 - p1;
        var endS = PointSegmentParameter(fromP1, d1, a);
        candidate = (d1 * endS) - fromP1;
        distance = Vec3.Length(candidate);
        if (distance < best)
        {
            (best, bestS, t, separation) = (distance, endS, 0f, candidate);
        }

        fromP1 = q2 - p1;
        endS = PointSegmentParameter(fromP1, d1, a);
        candidate = (d1 * endS) - fromP1;
        distance = Vec3.Length(candidate);
        if (distance < best)
        {
            (best, bestS, t, separation) = (distance, endS, 1f, candidate);
        }

        c1 = p1 + (d1 * bestS);
        c2 = p2 + (d2 * t);
        return best;
    }

    /// <summary>
    /// The parameter of the point of segment (start, start + <paramref name="direction"/>) nearest the
    /// point at <paramref name="offset"/> from its start: <c>clamp((offset·direction) / lengthSquared, 0, 1)</c>,
    /// and 0 for a segment of zero length.
    /// </summary>
    private static float PointSegmentParameter(Vec3 offset, Vec3 direction, float lengthSquared) =>
        lengthSquared <= 0f ? 0f : SimMath.Clamp(Vec3.Dot(offset, direction) / lengthSquared, 0f, 1f);

    /// <summary>
    /// <see cref="SegmentTriangle(Vec3, Vec3, Vec3, Vec3, Vec3, out Vec3, out Vec3)"/>, also writing the
    /// separation <c>onSegment − onTriangle</c> (zero at a crossing) and the triangle feature
    /// <paramref name="onTriangle"/> lies on (the face at a crossing).
    /// </summary>
    internal static float SegmentTriangle(
        Vec3 p, Vec3 q, Vec3 a, Vec3 b, Vec3 c, out Vec3 onSegment, out Vec3 onTriangle, out Vec3 separation, out TriangleFeature feature)
    {
        var ab = b - a;
        var ac = c - a;
        var normal = Vec3.Cross(ab, ac);
        var sideP = Vec3.Dot(normal, p - a);
        var sideQ = Vec3.Dot(normal, q - a);
        if (((sideP <= 0f && sideQ >= 0f) || (sideP >= 0f && sideQ <= 0f)) && sideP != sideQ)
        {
            // The segment meets the triangle's plane at one point; when that point is inside, it is both
            // closest points.
            var crossing = p + ((q - p) * (sideP / (sideP - sideQ)));
            if (Inside(crossing, a, b, c, normal))
            {
                onSegment = crossing;
                onTriangle = crossing;
                separation = new Vec3(0f, 0f, 0f);
                feature = TriangleFeature.Face;
                return 0f;
            }
        }

        var best = PointTriangle(p, a, b, c, out var bestTriangle, out var bestSeparation, out var bestFeature);
        var bestSegment = p;

        var distance = PointTriangle(q, a, b, c, out var closest, out var candidate, out var candidateFeature);
        if (distance < best)
        {
            best = distance;
            bestSegment = q;
            bestTriangle = closest;
            bestSeparation = candidate;
            bestFeature = candidateFeature;
        }

        Edge(p, q, a, b, ref best, ref bestSegment, ref bestTriangle, ref bestSeparation, ref bestFeature);
        Edge(p, q, b, c, ref best, ref bestSegment, ref bestTriangle, ref bestSeparation, ref bestFeature);
        Edge(p, q, c, a, ref best, ref bestSegment, ref bestTriangle, ref bestSeparation, ref bestFeature);

        onSegment = bestSegment;
        onTriangle = bestTriangle;
        separation = bestSeparation;
        feature = bestFeature;
        return best;
    }

    /// <summary>The segment against one triangle edge, kept when strictly closer than the best so far.</summary>
    private static void Edge(
        Vec3 p, Vec3 q, Vec3 from, Vec3 to, ref float best, ref Vec3 bestSegment, ref Vec3 bestTriangle, ref Vec3 bestSeparation, ref TriangleFeature bestFeature)
    {
        var distance = SegmentSegment(p, q, from, to, out var onSegment, out var onEdge, out var separation, out var t);
        if (distance < best)
        {
            best = distance;
            bestSegment = onSegment;
            bestTriangle = onEdge;
            bestSeparation = separation;
            bestFeature = t <= 0f || t >= 1f ? TriangleFeature.Vertex : TriangleFeature.Edge;
        }
    }

    /// <summary>True when <paramref name="x"/>, in the triangle's plane, is on the inner side of all three edges or on one.</summary>
    private static bool Inside(Vec3 x, Vec3 a, Vec3 b, Vec3 c, Vec3 normal) =>
        Vec3.Dot(Vec3.Cross(b - a, x - a), normal) >= 0f
        && Vec3.Dot(Vec3.Cross(c - b, x - b), normal) >= 0f
        && Vec3.Dot(Vec3.Cross(a - c, x - c), normal) >= 0f;

    /// <summary>The multiple of 64 nearest <paramref name="value"/>, ties to even, with −0 written as +0.</summary>
    private static float Snap(float value) => (System.MathF.Round(value * InverseLatticeSpacing) * LatticeSpacing) + 0f;
}
