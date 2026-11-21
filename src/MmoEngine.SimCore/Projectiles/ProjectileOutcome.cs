namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// What one projectile step did (<see cref="ProjectileStep.Step"/>,
/// specs/015-shared-simulation-core/data-model.md#lifecycle): it flies on, it hit something, or it expired.
/// A projectile that hit or expired leaves the state in that step.
/// </summary>
public enum ProjectileOutcome : byte
{
    /// <summary>No hit and no limit passed: its position is now the segment's end, p(k + 1).</summary>
    Flying = 0,

    /// <summary>It hit terrain, a mesh or a character's hitbox within its range.</summary>
    Hit = 1,

    /// <summary>It passed its range, its lifetime or the ±8192 m bound, or hit something beyond its range.</summary>
    Expired = 2,
}
