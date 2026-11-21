using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Tests.Inputs;

/// <summary>
/// Input validation (FR-019, contracts/input-command-format.md#validation): <c>InputValidator.Validate</c>
/// checks a decoded command against the character's <c>lastSequence</c> and <c>input.weaponSlots</c> in
/// contract order, sequence, buttons, pitch, weapon slot, and returns the first failure, or
/// <see cref="InputRejection.None"/> when the command is accepted.
/// </summary>
public sealed class InputValidatorTests
{
    /// <summary>The placeholder <c>input.weaponSlots</c>: slots 0 and 1 exist.</summary>
    private const int WeaponSlots = 2;

    /// <summary>
    /// Rows <c>(sequence, lastSequence, buttons, pitch, slot, expected)</c>. The first row breaks every
    /// rule at once and reports the sequence, and each following failure row passes the rules before its
    /// own, so the rows pin the order. The sequence rule is <c>sequence ≤ lastSequence</c> with no
    /// wrap-around: once a character has applied <c>0xFFFFFFFF</c>, every later sequence is refused.
    /// </summary>
    public static TheoryData<uint, uint, ushort, short, byte, InputRejection> Rows => new()
    {
        { 5u, 5u, 0x0200, 20000, 9, InputRejection.SequenceNotIncreasing },
        { 4u, 5u, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
        { 6u, 5u, 0x0200, 20000, 9, InputRejection.UnknownButtons },
        { 6u, 5u, 0x8000, 0, 0, InputRejection.UnknownButtons },
        { 6u, 5u, 0x01FF, 16385, 9, InputRejection.PitchOutOfRange },
        { 6u, 5u, 0x0000, -16385, 0, InputRejection.PitchOutOfRange },
        { 6u, 5u, 0x0000, 16384, 2, InputRejection.UnknownWeaponSlot },
        { 6u, 5u, 0x01FF, 16384, 1, InputRejection.None },
        { 6u, 5u, 0x0000, -16384, 0, InputRejection.None },
        { 1u, 0u, 0x0000, 0, 0, InputRejection.None },
        { 0u, 0u, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
        { 0u, 0xFFFFFFFFu, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
        { 1u, 0xFFFFFFFFu, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
        { 0x7FFFFFFFu, 0xFFFFFFFFu, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
        { 0xFFFFFFFFu, 0xFFFFFFFFu, 0x0000, 0, 0, InputRejection.SequenceNotIncreasing },
    };

    /// <summary>
    /// FR-019: given <c>weaponSlots</c> 2, <c>Validate</c> returns exactly the expected code for each row:
    /// the first rule of the contract's table the command breaks, or None. Client step and yaw take no
    /// part (every value is valid), so they are 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void ValidateReportsTheFirstFailureInContractOrder(
        uint sequence, uint lastSequence, ushort buttons, short pitch, byte slot, InputRejection expected)
    {
        var command = new InputCommand(sequence, 0, (Buttons)buttons, 0, pitch, slot);

        var rejection = InputValidator.Validate(command, lastSequence, WeaponSlots);

        Assert.Equal(expected, rejection);
    }
}
