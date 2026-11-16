using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: <c>InputCodec.TryDecode</c> sits on the tick path and allocates nothing, on
/// success and on every refusal (contracts/input-command-format.md#api: a refusal's <c>FormatError</c>
/// holds literals and formats its detail only when read). Every row warms its call up twice, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across 1,000 calls into buffers allocated before
/// the count, and requires exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class InputAllocationTests
{
    private const int WarmUpCalls = 2;
    private const int MeasuredCalls = 1_000;

    /// <summary>The example of contracts/input-command-format.md#example: <c>49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00</c>.</summary>
    private const string ExampleHex = "490101000000640000008100004000FC00";

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
        ReadOnlySpan<byte> bytes = input;
        var expectAccepted = expectedCode == FormatErrorCode.None;
        var unexpected = 0;
        var error = default(FormatError);
        for (var i = 0; i < WarmUpCalls; i++)
        {
            if (InputCodec.TryDecode(bytes, out _, out error) != expectAccepted)
            {
                unexpected++;
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredCalls; i++)
        {
            if (InputCodec.TryDecode(bytes, out _, out error) != expectAccepted)
            {
                unexpected++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated == 0,
            $"{inputCase}: InputCodec.TryDecode of {input.Length} bytes allocated {allocated} bytes over {MeasuredCalls} calls");
        Assert.True(
            unexpected == 0,
            $"{inputCase}: TryDecode returned {!expectAccepted} on {unexpected} of {WarmUpCalls + MeasuredCalls} calls");
        Assert.Equal(expectedCode, error.Code);
    }

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
