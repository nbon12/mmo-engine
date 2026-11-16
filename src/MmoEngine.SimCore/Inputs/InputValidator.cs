namespace MmoEngine.SimCore.Inputs;

/// <summary>
/// Validates a decoded input command against the character it is for (FR-019,
/// specs/015-shared-simulation-core/contracts/input-command-format.md#validation). The checks run in the
/// contract's order and the first failure is returned: <see cref="InputRejection.SequenceNotIncreasing"/>,
/// <see cref="InputRejection.UnknownButtons"/>, <see cref="InputRejection.PitchOutOfRange"/>, then
/// <see cref="InputRejection.UnknownWeaponSlot"/>. The client step and the aim yaw are not checked: every
/// value is valid. It neither throws nor allocates, because it runs on the tick path (stage 1 of every
/// world step).
/// </summary>
public static class InputValidator
{
    /// <summary>The reserved button bits, 9 to 15, which must be 0.</summary>
    private const ushort ReservedButtons = 0xFE00;

    /// <summary>The least valid aim pitch, −90° as a binary angle.</summary>
    private const short MinPitch = -16384;

    /// <summary>The greatest valid aim pitch, +90° as a binary angle.</summary>
    private const short MaxPitch = 16384;

    /// <summary>
    /// The first rule <paramref name="command"/> breaks, in contract order, or
    /// <see cref="InputRejection.None"/> when it is accepted: its sequence must be greater than
    /// <paramref name="lastSequence"/> (no wrap-around), no reserved button bit may be set, its pitch must
    /// lie in [−16384, 16384], and its weapon slot must be below <paramref name="weaponSlots"/>.
    /// </summary>
    public static InputRejection Validate(in InputCommand command, uint lastSequence, int weaponSlots)
    {
        if (command.Sequence <= lastSequence)
        {
            return InputRejection.SequenceNotIncreasing;
        }

        if (((ushort)command.Buttons & ReservedButtons) != 0)
        {
            return InputRejection.UnknownButtons;
        }

        if (command.AimPitch < MinPitch || command.AimPitch > MaxPitch)
        {
            return InputRejection.PitchOutOfRange;
        }

        if (command.WeaponSlot >= weaponSlots)
        {
            return InputRejection.UnknownWeaponSlot;
        }

        return InputRejection.None;
    }
}
