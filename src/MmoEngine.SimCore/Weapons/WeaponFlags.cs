using System;

namespace MmoEngine.SimCore.Weapons;

/// <summary>A weapon state's flags (data-model.md#reason-and-outcome-codes).</summary>
[Flags]
public enum WeaponFlags : byte
{
    /// <summary>Bit 0: a semi-automatic weapon fired and its trigger has not been released since.</summary>
    TriggerLatched = 1,
}
