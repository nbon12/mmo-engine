namespace MmoEngine.SimCore.Weapons;

/// <summary>
/// Why a held trigger fired no shot this step (data-model.md#reason-and-outcome-codes). It is reported
/// only while Fire is held and no shot fired.
/// </summary>
public enum FireBlock : byte
{
    /// <summary>Not blocked.</summary>
    None = 0,

    /// <summary>The active weapon is switching.</summary>
    Switching = 1,

    /// <summary>The active weapon is reloading.</summary>
    Reloading = 2,

    /// <summary>The magazine is empty.</summary>
    Empty = 3,

    /// <summary>The active weapon is overheated.</summary>
    Overheated = 4,

    /// <summary>A semi-automatic weapon has fired and the trigger has not been released.</summary>
    AwaitingRelease = 5,

    /// <summary>The world already holds <c>world.maxProjectiles</c> projectiles, or projectile IDs are used up.</summary>
    ProjectileLimit = 6,
}
