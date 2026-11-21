using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// A hit event, one element of a step's <c>events.hits</c> list (FR-010,
/// specs/015-shared-simulation-core/contracts/state-serialization.md#step-events), in ascending
/// projectile ID.
/// </summary>
public record struct ProjectileHit
{
    /// <summary>The projectile that hit.</summary>
    public uint ProjectileId { get; set; }

    /// <summary>Terrain, Mesh or Character.</summary>
    public HitKind Kind { get; set; }

    /// <summary>The character ID, or the triangle ID for terrain and meshes.</summary>
    public uint TargetId { get; set; }

    /// <summary>Head, Torso or Legs; None for terrain and meshes.</summary>
    public HitboxPart Hitbox { get; set; }

    /// <summary>The impact point, in metres.</summary>
    public Vec3 Point { get; set; }

    /// <summary>The fraction of the step's segment, in [0, 1]; 0 for a start inside a hitbox.</summary>
    public float T { get; set; }
}
