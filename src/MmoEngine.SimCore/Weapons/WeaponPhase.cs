namespace MmoEngine.SimCore.Weapons;

/// <summary>The phase a weapon is in (data-model.md#weapon-phases).</summary>
public enum WeaponPhase : byte
{
    /// <summary>Ready to fire.</summary>
    Ready = 0,

    /// <summary>Firing, waiting out the time between shots.</summary>
    Firing = 1,

    /// <summary>Reloading.</summary>
    Reloading = 2,

    /// <summary>Being equipped after a weapon switch.</summary>
    Switching = 3,

    /// <summary>Overheated, cooling down to the recovery level.</summary>
    Overheated = 4,
}
