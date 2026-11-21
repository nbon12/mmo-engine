using System;
using MmoEngine.SimCore.Hitboxes;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// The characters a projectile step may hit (specs/015-shared-simulation-core/data-model.md#projectile-step
/// step 3), over the caller's hitbox scratch exactly as <see cref="HitboxBuilder"/> writes it: the character
/// IDs in index order, three <see cref="Hitbox"/> per character in Head, Torso, Legs order, and one bounding
/// sphere per character. The world passes the hitboxes it posed in stage 4; sub-spec 6 can pass hitboxes
/// built from rewound poses. Its <c>default</c> value is the empty target set. It copies nothing, allocates
/// nothing and throws nothing, because the world builds one on every step's tick path.
/// </summary>
public readonly ref struct ProjectileTargets
{
    /// <summary>
    /// Targets over <paramref name="characterIds"/>, with character i's hitboxes at
    /// <c>hitboxes[3i..3i + 3]</c> and its bounding sphere at <c>bounds[i]</c>. The spans are meant to hold
    /// three hitboxes and one bounding sphere per ID; the targets are the first n characters, with
    /// n = min(<paramref name="characterIds"/>.Length, ⌊<paramref name="hitboxes"/>.Length / 3⌋,
    /// <paramref name="bounds"/>.Length), and entries past the shortest span are ignored, so the spans are
    /// sliced to n IDs, 3n hitboxes and n bounding spheres and a step never reads past them.
    /// </summary>
    public ProjectileTargets(ReadOnlySpan<uint> characterIds, ReadOnlySpan<Hitbox> hitboxes, ReadOnlySpan<HitboxBounds> bounds)
    {
        var count = System.Math.Min(System.Math.Min(characterIds.Length, hitboxes.Length / HitboxLayout.CapsuleCount), bounds.Length);
        CharacterIds = characterIds[..count];
        Hitboxes = hitboxes[..(count * HitboxLayout.CapsuleCount)];
        Bounds = bounds[..count];
    }

    /// <summary>The target characters' IDs, in index order.</summary>
    public ReadOnlySpan<uint> CharacterIds { get; }

    /// <summary>Three hitboxes per character, Head, Torso and Legs, in index order.</summary>
    public ReadOnlySpan<Hitbox> Hitboxes { get; }

    /// <summary>One bounding sphere per character, in index order.</summary>
    public ReadOnlySpan<HitboxBounds> Bounds { get; }
}
