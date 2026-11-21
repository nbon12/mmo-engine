using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// The closed-form ballistic path of a projectile (FR-009,
/// specs/015-shared-simulation-core/data-model.md#projectile, research R11). Gravity only, no drag: k steps
/// after launch, with <c>t = (float)k / (float)stepRateHz</c>, the position is <c>x0 + (vx*t)</c>,
/// <c>y0 + ((vy*t) + ((halfG*t)*t))</c> and <c>z0 + (vz*t)</c>, in float with that parenthesisation and no
/// fused multiply-add. Both ends of every swept segment come from this one function of k, so consecutive
/// segments join exactly, and every build can evaluate any step directly. It allocates nothing.
/// </summary>
public static class Ballistics
{
    /// <summary>
    /// The position <paramref name="k"/> steps after launch: <paramref name="projectile"/>'s
    /// <see cref="Projectile.Origin"/> moved along its <see cref="Projectile.LaunchVelocity"/> for
    /// <c>t = (float)k / (float)stepRateHz</c> seconds, falling by <c>(halfG*t)*t</c>. k = 0 gives the origin's
    /// bits.
    /// </summary>
    /// <param name="projectile">The projectile; only its origin and launch velocity are read.</param>
    /// <param name="k">The steps since launch.</param>
    /// <param name="halfG">The class's compiled <c>HalfG</c>, (−0.5·gravity)·gravityFactor, in m/s².</param>
    /// <param name="stepRateHz">The validated step rate, 20 to 60 Hz.</param>
    public static Vec3 PositionAt(in Projectile projectile, uint k, float halfG, int stepRateHz)
    {
        var t = (float)k / (float)stepRateHz;
        var origin = projectile.Origin;
        var velocity = projectile.LaunchVelocity;
        return new Vec3(
            origin.X + (velocity.X * t),
            origin.Y + ((velocity.Y * t) + ((halfG * t) * t)),
            origin.Z + (velocity.Z * t));
    }
}
