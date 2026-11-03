using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Simulator.Movement;

/// <summary>
/// Plane geometry for the movement models, using only IEEE 754 +, −, ×, ÷ and square root, which are
/// correctly rounded on every platform (research D8). No transcendental functions.
/// </summary>
internal static class Planar
{
    /// <summary>Steps from (x, y) toward (tx, ty) by at most <paramref name="maxStep"/> metres.</summary>
    public static (double X, double Y, bool Arrived) MoveToward(double x, double y, double tx, double ty, double maxStep)
    {
        var dx = tx - x;
        var dy = ty - y;
        var d2 = (dx * dx) + (dy * dy);
        if (d2 <= maxStep * maxStep)
        {
            return (tx, ty, true);
        }

        var f = maxStep / Math.Sqrt(d2);
        return (x + (dx * f), y + (dy * f), false);
    }

    /// <summary>A uniform point in the disc of radius <paramref name="r"/> around (cx, cy).</summary>
    public static (double X, double Y) InDisc(DeterministicRandom random, double cx, double cy, double r)
    {
        var (ux, uy) = random.UnitDisc();
        return (cx + (ux * r), cy + (uy * r));
    }

    /// <summary>A uniform point on the annulus between radii <paramref name="inner"/> and <paramref name="outer"/>.</summary>
    public static (double X, double Y) OnAnnulus(DeterministicRandom random, double cx, double cy, double inner, double outer)
    {
        var u = random.NextDouble();
        var r = Math.Sqrt((inner * inner) + (u * ((outer * outer) - (inner * inner))));
        var (vx, vy) = random.UnitVector();
        return (cx + (vx * r), cy + (vy * r));
    }

    /// <summary>Projects (x, y) onto the disc of radius <paramref name="r"/> around (cx, cy) if it lies outside.</summary>
    public static (double X, double Y) IntoDisc(double x, double y, double cx, double cy, double r)
    {
        var dx = x - cx;
        var dy = y - cy;
        var d2 = (dx * dx) + (dy * dy);
        if (d2 <= r * r)
        {
            return (x, y);
        }

        var f = r / Math.Sqrt(d2);
        return (cx + (dx * f), cy + (dy * f));
    }

    public static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }
}
