using System;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Hitboxes;

/// <summary>
/// Poses a stance's three hitbox capsules in world space (FR-011,
/// specs/015-shared-simulation-core/data-model.md#hitbox-layout-and-hitboxes). With <c>(sy, cy)</c> from
/// <see cref="SimMath.SinCosBinaryAngle"/>, a local point (lf, lr, lu) relative to the feet (fx, fy, fz)
/// becomes <c>wx = fx + ((sy*lf) - (cy*lr))</c>, <c>wy = fy + lu</c> and
/// <c>wz = fz + ((cy*lf) + (sy*lr))</c>, in float with that parenthesisation and no fused multiply-add, so
/// the same pose gives the same hitboxes to the bit in every build (US5 scenario 5). Stage 4 of every world
/// step calls it per character; it allocates nothing.
/// </summary>
public static class HitboxBuilder
{
    /// <summary>
    /// Writes the Head, Torso and Legs capsules, in that order, into the first three elements of
    /// <paramref name="destination"/>, and returns the character's bounding sphere: centred
    /// <see cref="HitboxLayout.BoundCenterUp"/> above the feet on the vertical axis, with radius
    /// <see cref="HitboxLayout.BoundRadius"/>.
    /// </summary>
    /// <param name="layout">The stance's compiled layout.</param>
    /// <param name="position">The feet: the character's post-movement position.</param>
    /// <param name="aimYaw">The aim yaw as a binary angle; yaw 0 faces +Z.</param>
    /// <param name="destination">At least three elements.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> holds fewer than three elements.</exception>
    public static HitboxBounds Build(HitboxLayout layout, Vec3 position, ushort aimYaw, Span<Hitbox> destination)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (destination.Length < HitboxLayout.CapsuleCount)
        {
            throw new ArgumentException("The destination holds fewer than three hitboxes.", nameof(destination));
        }

        SimMath.SinCosBinaryAngle(aimYaw, out var sy, out var cy);
        var capsules = layout.Capsules;
        destination[0] = Pose(HitboxPart.Head, capsules[0], position, sy, cy);
        destination[1] = Pose(HitboxPart.Torso, capsules[1], position, sy, cy);
        destination[2] = Pose(HitboxPart.Legs, capsules[2], position, sy, cy);

        return new HitboxBounds(new Vec3(position.X, position.Y + layout.BoundCenterUp, position.Z), layout.BoundRadius);
    }

    private static Hitbox Pose(HitboxPart part, HitboxCapsuleLayout capsule, Vec3 feet, float sy, float cy) =>
        new(part, ToWorld(capsule.A, feet, sy, cy), ToWorld(capsule.B, feet, sy, cy), capsule.Radius);

    private static Vec3 ToWorld(HitboxLocalPoint local, Vec3 feet, float sy, float cy) => new(
        feet.X + ((sy * local.Fwd) - (cy * local.Right)),
        feet.Y + local.Up,
        feet.Z + ((cy * local.Fwd) + (sy * local.Right)));
}
