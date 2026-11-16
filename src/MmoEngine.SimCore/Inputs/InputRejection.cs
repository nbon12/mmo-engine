namespace MmoEngine.SimCore.Inputs;

/// <summary>
/// Why a decoded input command was rejected (data-model.md#reason-and-outcome-codes). Validation
/// checks in this order and reports the first failure. A rejected command does not touch the state.
/// </summary>
public enum InputRejection : byte
{
    /// <summary>The command was accepted.</summary>
    None = 0,

    /// <summary>The sequence is not greater than the character's last sequence.</summary>
    SequenceNotIncreasing = 1,

    /// <summary>A reserved button bit, 9 to 15, is set.</summary>
    UnknownButtons = 2,

    /// <summary>The aim pitch is outside [−16384, 16384].</summary>
    PitchOutOfRange = 3,

    /// <summary>The weapon slot is not below <c>input.weaponSlots</c>.</summary>
    UnknownWeaponSlot = 4,
}
