using System;
using System.Collections.Immutable;

namespace MmoEngine.SimCore.Hitboxes;

/// <summary>
/// A point of a hitbox capsule in metres relative to the feet, before the yaw rotation:
/// <see cref="Fwd"/> along the aim heading, <see cref="Right"/> to the character's right and
/// <see cref="Up"/> along +Y (specs/015-shared-simulation-core/data-model.md#hitbox-layout-and-hitboxes).
/// </summary>
public readonly record struct HitboxLocalPoint(float Fwd, float Right, float Up);

/// <summary>
/// One capsule of a stance's layout: the local segment from <see cref="A"/> to <see cref="B"/>, with
/// <c>A.Up ≤ B.Up</c>, and its radius in metres.
/// </summary>
public readonly record struct HitboxCapsuleLayout(HitboxLocalPoint A, HitboxLocalPoint B, float Radius);

/// <summary>
/// One stance's hitbox layout, compiled from the configuration's <c>hitboxes</c> section (FR-011,
/// specs/015-shared-simulation-core/data-model.md#hitbox-layout-and-hitboxes): the head, torso and legs
/// capsules relative to the feet, rotated by yaw only; the bounding sphere, centred
/// <see cref="BoundCenterUp"/> above the feet on the vertical axis so it does not depend on yaw; and the
/// eye height, where projectiles start. <c>CompiledConfig</c> holds one per stance.
/// </summary>
public sealed class HitboxLayout
{
    /// <summary>The number of capsules per stance: head, torso and legs.</summary>
    public const int CapsuleCount = 3;

    /// <exception cref="ArgumentException"><paramref name="capsules"/> does not hold exactly three capsules.</exception>
    public HitboxLayout(ImmutableArray<HitboxCapsuleLayout> capsules, float boundCenterUp, float boundRadius, float eyeHeight)
    {
        if (capsules.IsDefault || capsules.Length != CapsuleCount)
        {
            throw new ArgumentException("A hitbox layout has exactly three capsules: head, torso and legs.", nameof(capsules));
        }

        Capsules = capsules;
        BoundCenterUp = boundCenterUp;
        BoundRadius = boundRadius;
        EyeHeight = eyeHeight;
    }

    /// <summary>The three capsules in <see cref="HitboxPart"/> order: Head, Torso, Legs.</summary>
    public ImmutableArray<HitboxCapsuleLayout> Capsules { get; }

    /// <summary>The height of the bounding sphere's centre above the feet, in metres.</summary>
    public float BoundCenterUp { get; }

    /// <summary>The bounding sphere's radius, in metres: it encloses all three capsules, plus a 0.01 m margin.</summary>
    public float BoundRadius { get; }

    /// <summary>The projectile origin's height above the feet, in metres; below the stance height.</summary>
    public float EyeHeight { get; }
}
