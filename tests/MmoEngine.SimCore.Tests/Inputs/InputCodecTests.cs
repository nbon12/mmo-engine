using CsCheck;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Tests.Inputs;

/// <summary>
/// The 17-byte input command, format version 1 (FR-018, contracts/input-command-format.md). Decoding
/// checks only the frame, in the order of #decoding, and refuses with a <see cref="FormatError"/> of
/// format <c>input</c>; it checks no value, so every command re-encodes to the same 17 bytes and the
/// validator reports bad values with its own codes.
/// </summary>
public sealed class InputCodecTests
{
    /// <summary>Fixed seed of <see cref="EncodeThenDecodeIsTheIdentity"/>; a failure prints it.</summary>
    private const ulong PropertySeed = 0x0015_0026_4901_0011UL;

    private const int PropertyIterations = 2000;

    /// <summary>The example of contracts/input-command-format.md#example: <c>49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00</c>.</summary>
    private const string ExampleHex = "490101000000640000008100004000FC00";

    public static TheoryData<string> RefusalCases =>
    [
        "empty",
        "format-id-0x4A",
        "only-format-id",
        "version-2",
        "version-2-longer",
        "version-0",
        "truncated-12",
        "truncated-16",
        "oversized-20",
    ];

    /// <summary>Sequence 1, client step 100, Forward and Fire held, yaw 16384, pitch −1024, slot 0.</summary>
    private static InputCommand Example => new(1, 100, Buttons.Forward | Buttons.Fire, 16384, -1024, 0);

    [Fact]
    public void ExampleEncodesToItsContractBytes()
    {
        var encoded = new byte[InputCodec.EncodedSize];
        Assert.True(InputCodec.TryEncode(Example, encoded), "TryEncode refused a 17-byte destination");
        Assert.Equal(ExampleHex, Convert.ToHexString(encoded));

        var decoded = DecodeOrFail(encoded, "the example", out var error);
        Assert.Equal(Example, decoded);
        Assert.Equal(FormatErrorCode.None, error.Code);

        // A destination one byte short is refused, and nothing is written into it.
        var tooShort = new byte[InputCodec.EncodedSize - 1];
        Assert.False(InputCodec.TryEncode(Example, tooShort), "TryEncode accepted a 16-byte destination");
        Assert.Equal(new byte[InputCodec.EncodedSize - 1], tooShort);

        // Decoding checks no value: reserved buttons, a pitch past ±16384 and slot 255 round-trip, and
        // the validator, not the decoder, refuses them.
        var unvalidated = new InputCommand(7, 8, (Buttons)0xFE00, 65535, 32767, 255);
        var unvalidatedBytes = new byte[InputCodec.EncodedSize];
        Assert.True(InputCodec.TryEncode(unvalidated, unvalidatedBytes), "TryEncode refused a command the validator would reject");
        Assert.Equal("4901070000000800000000FEFFFFFF7FFF", Convert.ToHexString(unvalidatedBytes));
        Assert.Equal(unvalidated, DecodeOrFail(unvalidatedBytes, "buttons 0xFE00, pitch 32767, slot 255", out _));
    }

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void DecodeRefusesInContractOrder(string caseName)
    {
        var refusal = Refusal(caseName);

        var accepted = InputCodec.TryDecode(refusal.Bytes, out var command, out var error);

        Assert.False(accepted, $"{caseName}: TryDecode accepted {Convert.ToHexString(refusal.Bytes)} as {command}");
        Assert.Equal(default(InputCommand), command);
        Assert.Equal("input", error.Format);
        Assert.Equal(refusal.Code, error.Code);
        Assert.Equal(refusal.Offset, error.Offset);
        Assert.Equal(refusal.Field, error.Field);
        Assert.Equal(refusal.Detail, error.Detail);
        Assert.Equal($"input {refusal.Field} at offset {refusal.Offset}: {refusal.Detail}", error.ToString());
        if (refusal.Message is not null)
        {
            Assert.Equal(refusal.Message, error.ToString());
        }
    }

    [Fact]
    public void EncodeThenDecodeIsTheIdentity()
    {
        // Every 17-byte buffer with the identifier and version decodes, and re-encodes to itself.
        var bodies = Gen.Byte.Array[InputCodec.EncodedSize - 2];
        var pcg = new PCG(1, PropertySeed);
        var reencoded = new byte[InputCodec.EncodedSize];
        for (var i = 0; i < PropertyIterations; i++)
        {
            var body = bodies.Generate(pcg, null!, out _);
            byte[] bytes = [InputCodec.FormatId, InputCodec.FormatVersion, .. body];
            var context = $"seed 0x{PropertySeed:X16}, iteration {i}, bytes {Convert.ToHexString(bytes)}";

            var command = DecodeOrFail(bytes, context, out _);
            Array.Clear(reencoded);
            Assert.True(InputCodec.TryEncode(command, reencoded), $"{context}: TryEncode refused a 17-byte destination");
            Assert.True(
                bytes.AsSpan().SequenceEqual(reencoded),
                $"{context}: decoded as {command}, re-encoded as {Convert.ToHexString(reencoded)}");
        }
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/>, failing the test with the error when the decoder refuses them.
    /// The error is formatted only on a refusal: a successful decode's error is the default one.
    /// </summary>
    private static InputCommand DecodeOrFail(byte[] bytes, string context, out FormatError error)
    {
        if (!InputCodec.TryDecode(bytes, out var command, out error))
        {
            Assert.Fail($"{context}: TryDecode refused {Convert.ToHexString(bytes)}: {error}");
        }

        return command;
    }

    /// <summary>One row of contracts/input-command-format.md#decoding, built from the example's bytes.</summary>
    private static InputRefusal Refusal(string caseName)
    {
        var example = Convert.FromHexString(ExampleHex);
        return caseName switch
        {
            // 1: no byte at all.
            "empty" => new([], FormatErrorCode.Truncated, 0, "formatId", "17 bytes expected, got 0"),

            // 2: the identifier is checked before anything else.
            "format-id-0x4A" => new(
                [0x4A, .. example.AsSpan(1)],
                FormatErrorCode.UnknownMagic,
                0,
                "formatId",
                "format identifier 0x4A is not 0x49"),

            // 3: the identifier alone.
            "only-format-id" => new([0x49], FormatErrorCode.Truncated, 1, "version", "17 bytes expected, got 1"),

            // 4: the version comes before the length, so another version is refused as such at any length.
            "version-2" => new(
                WithVersion(example, 2),
                FormatErrorCode.UnsupportedVersion,
                1,
                "version",
                "unsupported version 2; this reader supports version 1",
                Message: "input version at offset 1: unsupported version 2; this reader supports version 1"),
            "version-2-longer" => new(
                [.. WithVersion(example, 2), 0x00, 0x00, 0x00],
                FormatErrorCode.UnsupportedVersion,
                1,
                "version",
                "unsupported version 2; this reader supports version 1",
                Message: "input version at offset 1: unsupported version 2; this reader supports version 1"),
            "version-0" => new(
                WithVersion(example, 0),
                FormatErrorCode.UnsupportedVersion,
                1,
                "version",
                "unsupported version 0; this reader supports version 1",
                Message: "input version at offset 1: unsupported version 0; this reader supports version 1"),

            // 5: shorter than 17 bytes; the offset is the length.
            "truncated-12" => new(example.AsSpan(0, 12).ToArray(), FormatErrorCode.Truncated, 12, "length", "17 bytes expected, got 12"),
            "truncated-16" => new(
                example.AsSpan(0, 16).ToArray(),
                FormatErrorCode.Truncated,
                16,
                "length",
                "17 bytes expected, got 16",
                Message: "input length at offset 16: 17 bytes expected, got 16"),

            // 6: longer than 17 bytes; the offset is 17, the first byte too many.
            "oversized-20" => new([.. example, 0x00, 0x00, 0x00], FormatErrorCode.Oversized, 17, "length", "17 bytes expected, got 20"),

            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    private static byte[] WithVersion(byte[] bytes, byte version)
    {
        var copy = (byte[])bytes.Clone();
        copy[1] = version;
        return copy;
    }

    /// <summary>A refused input and the exact error it must give; <c>Message</c>, when set, is the whole text.</summary>
    private sealed record InputRefusal(
        byte[] Bytes,
        FormatErrorCode Code,
        long Offset,
        string Field,
        string Detail,
        string? Message = null);
}
