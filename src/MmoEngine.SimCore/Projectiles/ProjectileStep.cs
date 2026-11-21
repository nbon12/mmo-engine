using System;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// One step of one projectile (FR-009, FR-010, specs/015-shared-simulation-core/data-model.md#projectile-step):
/// the segment p(k) → p(k + 1) of its closed-form path is swept against the collision world and the
/// hitboxes it is given, and the projectile hits, expires or flies on. Stage 5 of every world step calls it
/// once per projectile, in ascending ID; the continent server can call it inside its own tick. It takes
/// state in and writes state out, reads no mutable state, throws nothing and allocates nothing.
/// </summary>
public static class ProjectileStep
{
    /// <summary>The coordinate bound of state: an end point beyond it in any axis expires the projectile with OutOfBounds.</summary>
    public const float CoordinateLimit = 8192f;

    /// <summary>
    /// Steps <paramref name="projectile"/> in world step <paramref name="step"/>, with k = step − LaunchStep:
    /// <list type="number">
    /// <item>a = <c>Ballistics.PositionAt(k)</c> and b = <c>PositionAt(k + 1)</c>, with the class's
    /// <c>HalfG</c> and the step rate;</item>
    /// <item>the world candidate is <see cref="CollisionWorld.SegmentQuery"/>'s;</item>
    /// <item>the character candidates are every target but the owner: the segment is clipped to its bounding
    /// sphere with <see cref="SegmentCapsule.ClipToSphere"/>, then tested against its three capsules with
    /// <see cref="SegmentCapsule.Intersect"/>, at t = t0 + ((t1 − t0)·tc), so a start inside hits at 0;</item>
    /// <item>the earliest hit wins by the key (t, world before character, target ID, hitbox index),
    /// compared as exact floats;</item>
    /// <item>its point is a + ((b − a)·t); within the class's range of the origin
    /// (|point − Origin|² ≤ <c>MaxRangeSq</c>) it is a hit <c>{Id, kind, target ID, hitbox, point, t}</c>
    /// (the triangle ID and <see cref="HitboxPart.None"/> for terrain and meshes), and beyond it the
    /// projectile expires with Range;</item>
    /// <item>with no hit, the first limit that applies expires it: Range when |b − Origin|² &gt;
    /// <c>MaxRangeSq</c>, Lifetime when k + 1 ≥ <c>LifetimeSteps</c>, OutOfBounds when a coordinate of b is
    /// beyond ±8192 m; otherwise its Position becomes b and it flies on.</item>
    /// </list>
    /// Only Position changes, and only when the projectile flies on. Every loop is bounded by the targets'
    /// length.
    /// </summary>
    /// <param name="projectile">The projectile; its Position is set to b when it flies on.</param>
    /// <param name="step">The world step being computed, at or after the projectile's LaunchStep.</param>
    /// <param name="config">The compiled configuration: the class's HalfG, MaxRangeSq and LifetimeSteps, and the step rate.</param>
    /// <param name="collision">The collision world.</param>
    /// <param name="targets">The characters it may hit; the owner's hitboxes are never tested.</param>
    /// <param name="hit">The hit, when the outcome is <see cref="ProjectileOutcome.Hit"/>; otherwise default.</param>
    /// <param name="expiry">The reason, when the outcome is <see cref="ProjectileOutcome.Expired"/>; otherwise default.</param>
    public static ProjectileOutcome Step(
        ref Projectile projectile,
        uint step,
        CompiledConfig config,
        CollisionWorld collision,
        ProjectileTargets targets,
        out ProjectileHit hit,
        out ExpiryReason expiry)
    {
        hit = default;
        expiry = default;
        int projectileClass = projectile.Class;
        var halfG = config.HalfG[projectileClass];
        var stepRateHz = config.StepRateHz;
        var k = unchecked(step - projectile.LaunchStep);
        var a = Ballistics.PositionAt(in projectile, k, halfG, stepRateHz);
        var b = Ballistics.PositionAt(in projectile, unchecked(k + 1u), halfG, stepRateHz);
        var d = b - a;

        // 2. The world candidate.
        var found = collision.SegmentQuery(a, b, out var world);
        var bestT = world.T;
        var bestKind = world.Kind;
        var bestTarget = unchecked((uint)world.TriangleId);
        var bestPart = HitboxPart.None;
        var bestIndex = 0;

        // 3. The character candidates, every one but the owner.
        var ids = targets.CharacterIds;
        var hitboxes = targets.Hitboxes;
        var bounds = targets.Bounds;
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var sphere = bounds[i];
            if (id == projectile.OwnerId || !SegmentCapsule.ClipToSphere(a, b, sphere.Centre, sphere.Radius, out var t0, out var t1))
            {
                continue;
            }

            // Every capsule entry is at or after t0, so a sphere entered after the best hit cannot win.
            if (found && t0 > bestT)
            {
                continue;
            }

            var p = a + (d * t0);
            var q = a + (d * t1);
            for (var h = 0; h < HitboxLayout.CapsuleCount; h++)
            {
                var capsule = hitboxes[(HitboxLayout.CapsuleCount * i) + h];
                if (!SegmentCapsule.Intersect(p, q, capsule.A, capsule.B, capsule.Radius, out var tc))
                {
                    continue;
                }

                // 4. The key: t, then world before character, then target ID, then hitbox index.
                var t = t0 + ((t1 - t0) * tc);
                var wins = !found
                    || t < bestT
                    || (t == bestT && bestKind == HitKind.Character && (id < bestTarget || (id == bestTarget && h < bestIndex)));
                if (wins)
                {
                    found = true;
                    bestT = t;
                    bestKind = HitKind.Character;
                    bestTarget = id;
                    bestPart = capsule.Part;
                    bestIndex = h;
                }
            }
        }

        var origin = projectile.Origin;
        if (found)
        {
            // 5. The hit, within the class's range of the origin; beyond it the projectile expires.
            var point = a + (d * bestT);
            if (Vec3.LengthSquared(point - origin) <= config.MaxRangeSq[projectileClass])
            {
                hit = new ProjectileHit
                {
                    ProjectileId = projectile.Id,
                    Kind = bestKind,
                    TargetId = bestTarget,
                    Hitbox = bestPart,
                    Point = point,
                    T = bestT,
                };
                return ProjectileOutcome.Hit;
            }

            expiry = ExpiryReason.Range;
            return ProjectileOutcome.Expired;
        }

        // 6. No hit: the first limit passed, in contract order.
        if (Vec3.LengthSquared(b - origin) > config.MaxRangeSq[projectileClass])
        {
            expiry = ExpiryReason.Range;
            return ProjectileOutcome.Expired;
        }

        if ((long)k + 1L >= config.LifetimeSteps[projectileClass])
        {
            expiry = ExpiryReason.Lifetime;
            return ProjectileOutcome.Expired;
        }

        if (MathF.Abs(b.X) > CoordinateLimit || MathF.Abs(b.Y) > CoordinateLimit || MathF.Abs(b.Z) > CoordinateLimit)
        {
            expiry = ExpiryReason.OutOfBounds;
            return ProjectileOutcome.Expired;
        }

        projectile.Position = b;
        return ProjectileOutcome.Flying;
    }
}
