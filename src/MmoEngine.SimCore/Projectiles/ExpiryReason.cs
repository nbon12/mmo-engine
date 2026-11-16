namespace MmoEngine.SimCore.Projectiles;

/// <summary>Why a projectile expired without hitting anything (data-model.md#reason-and-outcome-codes). 0 is never written.</summary>
public enum ExpiryReason : byte
{
    /// <summary>It flew its class's maximum range.</summary>
    Range = 1,

    /// <summary>It lived its class's lifetime.</summary>
    Lifetime = 2,

    /// <summary>It left the playable area.</summary>
    OutOfBounds = 3,
}
