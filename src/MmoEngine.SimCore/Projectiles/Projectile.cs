using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Projectiles;

/// <summary>
/// One projectile in flight (specs/015-shared-simulation-core/data-model.md#projectile). Its members are
/// named and ordered as the canonical state's projectile record
/// (contracts/state-serialization.md#projectile-record).
/// </summary>
public record struct Projectile
{
    /// <summary>Unique and ascending, taken from <c>nextProjectileId</c>; never 0.</summary>
    public uint Id { get; set; }

    /// <summary>The shooter's character ID; the owner's hitboxes are never tested.</summary>
    public uint OwnerId { get; set; }

    /// <summary>The projectile class index, below the number of projectile classes.</summary>
    public byte Class { get; set; }

    /// <summary>L, the step it was fired in; at most the world's step.</summary>
    public uint LaunchStep { get; set; }

    /// <summary>The shooter's eye at launch, in metres; |v| ≤ 8192.</summary>
    public Vec3 Origin { get; set; }

    /// <summary>The launch velocity, direction × muzzle speed, in m/s.</summary>
    public Vec3 LaunchVelocity { get; set; }

    /// <summary>p(n − L + 1) at the end of step n: derived, but stored and hashed, so a one-ulp error shows at its own step.</summary>
    public Vec3 Position { get; set; }
}
