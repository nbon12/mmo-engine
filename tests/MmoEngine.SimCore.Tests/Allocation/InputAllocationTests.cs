using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: <c>InputCodec.TryDecode</c> and <c>InputValidator.Validate</c> sit on the tick
/// path and allocate nothing, on success and on every refusal or rejection
/// (contracts/input-command-format.md#api: a refusal's <c>FormatError</c> holds literals and formats its
/// detail only when read, and the validator returns a code). Every row warms its call up twice, then
/// counts <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three batches of 1,000 calls into
/// buffers allocated before the count (<see cref="ZeroAllocation"/>), and requires the smallest batch to
/// be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class InputAllocationTests
{
    /// <summary>The example of contracts/input-command-format.md#example: <c>49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00</c>.</summary>
    private const string ExampleHex = "490101000000640000008100004000FC00";

    /// <summary>The placeholder <c>input.weaponSlots</c>, as <c>InputValidatorTests</c> (T064) passes it.</summary>
    private const int WeaponSlots = 2;

    /// <summary>The valid example, then the six rows of contracts/input-command-format.md#decoding in contract order.</summary>
    public static TheoryData<string> InputCases =>
    [
        "valid",
        "empty",
        "format-id-0x4A",
        "only-format-id",
        "version-2",
        "truncated-12",
        "oversized-20",
    ];

    [Theory]
    [MemberData(nameof(InputCases))]
    public void DecodeAllocatesNothing(string inputCase)
    {
        var (input, expectedCode) = Input(inputCase);
        var expectAccepted = expectedCode == FormatErrorCode.None;
        var unexpected = 0;
        var error = default(FormatError);

        // The lambda's parameter is named, so `out _` below stays a discard.
        var counts = ZeroAllocation.MeasureBatches(call =>
        {
            if (InputCodec.TryDecode(input, out _, out error) != expectAccepted)
            {
                unexpected++;
            }
        });

        ZeroAllocation.AssertABatchAllocatedNothing(counts, $"{inputCase}: InputCodec.TryDecode of {input.Length} bytes");
        Assert.True(
            unexpected == 0,
            $"{inputCase}: TryDecode returned {!expectAccepted} on {unexpected} of {ZeroAllocation.TotalCalls} calls");
        Assert.Equal(expectedCode, error.Code);
    }

    /// <summary>
    /// <c>InputValidator.Validate(in command, lastSequence, 2)</c> (contracts/input-command-format.md#validation),
    /// one row per code: each rejection with the first row of <c>InputValidatorTests</c> (T064) that gives
    /// it, and <see cref="InputRejection.None"/> with the accepted row (6, 5, 0x01FF, 16384, 1). Every
    /// call must return the row's code.
    /// </summary>
    [Theory]
    [InlineData(InputRejection.SequenceNotIncreasing)]
    [InlineData(InputRejection.UnknownButtons)]
    [InlineData(InputRejection.PitchOutOfRange)]
    [InlineData(InputRejection.UnknownWeaponSlot)]
    [InlineData(InputRejection.None)]
    public void ValidateAllocatesNothing(InputRejection expected)
    {
        var (command, lastSequence) = ValidatorRow(expected);
        var unexpected = 0;
        var last = InputRejection.None;

        var counts = ZeroAllocation.MeasureBatches(_ =>
        {
            last = InputValidator.Validate(in command, lastSequence, WeaponSlots);
            if (last != expected)
            {
                unexpected++;
            }
        });

        ZeroAllocation.AssertABatchAllocatedNothing(counts, $"{expected}: InputValidator.Validate of {command} after sequence {lastSequence}");
        Assert.True(
            unexpected == 0,
            $"{expected}: Validate returned another code on {unexpected} of {ZeroAllocation.TotalCalls} calls, the last {last}");
        Assert.Equal(expected, last);
    }

    /// <summary>
    /// The command and last sequence of the row that gives <paramref name="expected"/>, built as T064's
    /// <c>new InputCommand(sequence, 0, (Buttons)buttons, 0, pitch, slot)</c> from
    /// (sequence, lastSequence, buttons, pitch, slot).
    /// </summary>
    private static (InputCommand Command, uint LastSequence) ValidatorRow(InputRejection expected) => expected switch
    {
        // (5, 5, 0x0200, 20000, 9): the sequence is checked first, so every later failure is masked.
        InputRejection.SequenceNotIncreasing => (new InputCommand(5, 0, (Buttons)0x0200, 0, 20000, 9), 5u),

        // (6, 5, 0x0200, 20000, 9): a reserved button bit, checked before the pitch and the slot.
        InputRejection.UnknownButtons => (new InputCommand(6, 0, (Buttons)0x0200, 0, 20000, 9), 5u),

        // (6, 5, 0x01FF, 16385, 9): every known button, the pitch one unit above the range.
        InputRejection.PitchOutOfRange => (new InputCommand(6, 0, (Buttons)0x01FF, 0, 16385, 9), 5u),

        // (6, 5, 0, 16384, 2): the slot equals weaponSlots.
        InputRejection.UnknownWeaponSlot => (new InputCommand(6, 0, 0, 0, 16384, 2), 5u),

        // (6, 5, 0x01FF, 16384, 1): every limit at its edge, accepted.
        InputRejection.None => (new InputCommand(6, 0, (Buttons)0x01FF, 0, 16384, 1), 5u),

        _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, "Unknown rejection code."),
    };

    /// <summary>A row's bytes, built from the example's, and the code it decodes with (<c>None</c> when it decodes).</summary>
    private static (byte[] Bytes, FormatErrorCode Code) Input(string inputCase)
    {
        var example = Convert.FromHexString(ExampleHex);
        return inputCase switch
        {
            "valid" => (example, FormatErrorCode.None),

            // 1: no byte at all.
            "empty" => ([], FormatErrorCode.Truncated),

            // 2: the identifier is not 0x49.
            "format-id-0x4A" => ([0x4A, .. example.AsSpan(1)], FormatErrorCode.UnknownMagic),

            // 3: the identifier alone.
            "only-format-id" => ([0x49], FormatErrorCode.Truncated),

            // 4: another version, at the full length.
            "version-2" => ([0x49, 0x02, .. example.AsSpan(2)], FormatErrorCode.UnsupportedVersion),

            // 5: shorter than 17 bytes.
            "truncated-12" => (example.AsSpan(0, 12).ToArray(), FormatErrorCode.Truncated),

            // 6: longer than 17 bytes.
            "oversized-20" => ([.. example, 0x00, 0x00, 0x00], FormatErrorCode.Oversized),

            _ => throw new ArgumentOutOfRangeException(nameof(inputCase), inputCase, "Unknown input case."),
        };
    }
}
