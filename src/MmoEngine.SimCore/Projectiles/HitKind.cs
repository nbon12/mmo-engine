namespace MmoEngine.SimCore.Projectiles;

/// <summary>What a projectile hit (data-model.md#reason-and-outcome-codes).</summary>
public enum HitKind : byte
{
    /// <summary>A heightfield triangle.</summary>
    Terrain = 0,

    /// <summary>A mesh triangle.</summary>
    Mesh = 1,

    /// <summary>A character's hitbox.</summary>
    Character = 2,
}
