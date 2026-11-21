using System;
using MmoEngine.SimCore.Hitboxes;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// The characters a projectile step may hit (specs/015-shared-simulation-core/data-model.md#projectile-step
/// step 3), over the caller's hitbox scratch exactly as <see cref="HitboxBuilder"/> writes it: the character
/// IDs in index order, three <see cref="Hitbox"/> per character in Head, Torso, Legs order, and one bounding
/// sphere per character. The world passes the hitboxes it posed in stage 4; sub-spec 6 can pass hitboxes
/// built from rewound poses. Its <c>default</c> value is the empty target set. It copies nothing and
/// allocates nothing.
/// </summary>
public readonly ref struct ProjectileTargets
{
    /// <summary>
    /// Targets over <paramref name="characterIds"/>, with character i's hitboxes at
    /// <c>hitboxes[3i..3i + 3]</c> and its bounding sphere at <c>bounds[i]</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="hitboxes"/> does not hold exactly three hitboxes per ID, or <paramref name="bounds"/>
    /// not exactly one bounding sphere per ID.
    /// </exception>
    public ProjectileTargets(ReadOnlySpan<uint> characterIds, ReadOnlySpan<Hitbox> hitboxes, ReadOnlySpan<HitboxBounds> bounds)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(hitboxes.Length, characterIds.Length * HitboxLayout.CapsuleCount, nameof(hitboxes));
        ArgumentOutOfRangeException.ThrowIfNotEqual(bounds.Length, characterIds.Length, nameof(bounds));
        CharacterIds = characterIds;
        Hitboxes = hitboxes;
        Bounds = bounds;
    }

    /// <summary>The target characters' IDs, in index order.</summary>
    public ReadOnlySpan<uint> CharacterIds { get; }

    /// <summary>Three hitboxes per character, Head, Torso and Legs, in index order.</summary>
    public ReadOnlySpan<Hitbox> Hitboxes { get; }

    /// <summary>One bounding sphere per character, in index order.</summary>
    public ReadOnlySpan<HitboxBounds> Bounds { get; }
}
