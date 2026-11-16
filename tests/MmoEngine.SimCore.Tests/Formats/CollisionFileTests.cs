using System.Buffers.Binary;
using System.IO.Hashing;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The collision data file <c>MMCD</c>, format version 1 (FR-021, contracts/collision-data-format.md):
/// <c>tiny-world</c>'s exact bytes and trailer checksum, the round trip that keeps every bit (negative
/// zero included), and the frame checks 1–4 and 7 of #check-order, which refuse a file before any of
/// its values is read. Checks 5, 6, 8 and 9 are Phase 8's.
/// </summary>
public sealed class CollisionFileTests
{
    /// <summary><c>tiny-world</c>'s 188 bytes, one row of contracts/collision-data-format.md#tiny-world's dump per line.</summary>
    private const string TinyWorldHex =
        "4d4d434401003000bc00000000000000" +
        "000080bf000080bf0000803f00008040" +
        "03000000030000000100000000000000" +
        "00000000000000000000000000000000" +
        "0000803e000000000000000000000000" +
        "00000080010000000400000003000000" +
        "000000bf000000000000003f0000003f" +
        "000000000000003f0000003f0000803f" +
        "0000003f000000bf0000803f0000003f" +
        "00000000010000000200000000000000" +
        "02000000030000000000000000000000" +
        "010000009cca3f3bfae32492";

    private const int TinyWorldLength = 188;

    /// <summary>The trailer: XxHash64 (seed 0) of bytes [0, 180).</summary>
    private const ulong TinyWorldChecksum = 0x9224e3fa3b3fca9cUL;

    private const int TinyWorldTrailerOffset = 180;

    /// <summary>48 + 4·9 + 8: tiny-world's heightfield with no meshes.</summary>
    private const int HeightfieldOnlyLength = 92;

    private const uint NegativeZeroBits = 0x80000000u;

    public static TheoryData<string> Worlds => ["tiny-world", "heightfield-only", "flat"];

    public static TheoryData<string> FrameRefusals =>
    [
        "3-bytes",
        "magic-MMCX",
        "5-bytes",
        "version-2",
        "version-2-6-bytes",
        "7-bytes",
        "header-length-40",
        "truncated-20",
        "truncated-100",
        "4-bytes-appended",
        "checksum-mismatch",
    ];

    [Fact]
    public void TinyWorldEncodesToItsContractBytesAndChecksum()
    {
        var bytes = CollisionFile.Encode(TestWorlds.TinyWorld());

        Assert.Equal(TinyWorldLength, bytes.Length);
        Assert.Equal(TinyWorldHex.ToUpperInvariant(), Convert.ToHexString(bytes));
        Assert.Equal("9CCA3F3BFAE32492", Convert.ToHexString(bytes, TinyWorldTrailerOffset, 8));

        var reference = XxHash64.HashToUInt64(bytes.AsSpan(0, TinyWorldTrailerOffset), 0);
        Assert.True(reference == TinyWorldChecksum, $"XxHash64 of [0, 180) is {reference:x16}, the contract pins {TinyWorldChecksum:x16}");

        var world = CollisionFile.Decode(bytes);
        Assert.True(world.Checksum == TinyWorldChecksum, $"the decoded Checksum is {world.Checksum:x16}, expected {TinyWorldChecksum:x16}");

        Assert.Equal(HeightfieldOnlyLength, CollisionFile.Encode(TestWorlds.HeightfieldOnly()).Length);
    }

    [Theory]
    [MemberData(nameof(Worlds))]
    public void DecodeThenEncodeIsTheIdentity(string world)
    {
        var bytes = CollisionFile.Encode(Data(world));

        var decoded = CollisionFile.Decode(bytes);
        var reencoded = CollisionFile.Encode(decoded.Data);

        Assert.True(
            bytes.AsSpan().SequenceEqual(reencoded),
            $"{world}: {bytes.Length} bytes re-encode as {reencoded.Length} bytes, first difference at {FirstDifference(bytes, reencoded)}");
        var trailer = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - 8));
        Assert.True(decoded.Checksum == trailer, $"{world}: Checksum {decoded.Checksum:x16}, the trailer is {trailer:x16}");

        if (world == "tiny-world")
        {
            // heights[8] is negative zero, kept bit for bit through the file and the decoded data.
            var bits = BitConverter.SingleToUInt32Bits(decoded.Data.Heights[8]);
            Assert.True(bits == NegativeZeroBits, $"tiny-world heights[8] decodes as 0x{bits:X8}, expected 0x{NegativeZeroBits:X8}");
        }
    }

    [Theory]
    [MemberData(nameof(FrameRefusals))]
    public void FrameChecksRefuseInContractOrder(string caseName)
    {
        var refusal = Refusal(caseName);

        var exception = Assert.Throws<CoreFormatException>(() => CollisionFile.Decode(refusal.Bytes));

        Assert.Equal("collision", exception.Error.Format);
        Assert.Equal(refusal.Code, exception.Error.Code);
        Assert.Equal(refusal.Offset, exception.Error.Offset);
        Assert.Equal(refusal.Field, exception.Error.Field);
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"collision {refusal.Field} at offset {refusal.Offset}: ", exception.Message, StringComparison.Ordinal);
        if (refusal.Message is not null)
        {
            Assert.Equal(refusal.Message, exception.Message);
        }
    }

    private static CollisionData Data(string world) => world switch
    {
        "tiny-world" => TestWorlds.TinyWorld(),
        "heightfield-only" => TestWorlds.HeightfieldOnly(),
        "flat" => TestWorlds.Flat(),
        _ => throw new ArgumentOutOfRangeException(nameof(world), world, "Unknown world."),
    };

    /// <summary>One row of checks 1–4 and 7 of contracts/collision-data-format.md#check-order, from tiny-world's bytes.</summary>
    private static CollisionRefusal Refusal(string caseName)
    {
        var tiny = CollisionFile.Encode(TestWorlds.TinyWorld());
        return caseName switch
        {
            // 1: magic.
            "3-bytes" => new(Truncate(tiny, 3), FormatErrorCode.Truncated, 3, "magic"),
            "magic-MMCX" => new(Write(tiny, 0, "4d4d4358"), FormatErrorCode.UnknownMagic, 0, "magic"),

            // 2: version, before the header length and the file length.
            "5-bytes" => new(Truncate(tiny, 5), FormatErrorCode.Truncated, 5, "version"),
            "version-2" => new(
                Write(tiny, 4, "0200"),
                FormatErrorCode.UnsupportedVersion,
                4,
                "version",
                "collision version at offset 4: unsupported version 2; this reader supports version 1"),
            "version-2-6-bytes" => new(Truncate(Write(tiny, 4, "0200"), 6), FormatErrorCode.UnsupportedVersion, 4, "version"),

            // 3: header length, then the whole header.
            "7-bytes" => new(Truncate(tiny, 7), FormatErrorCode.Truncated, 7, "headerLength"),
            "header-length-40" => new(Write(tiny, 6, "2800"), FormatErrorCode.Inconsistent, 6, "headerLength"),
            "truncated-20" => new(Truncate(tiny, 20), FormatErrorCode.Truncated, 20, "header"),

            // 4: the actual length against fileLength, fewer bytes or more.
            "truncated-100" => new(
                Truncate(tiny, 100),
                FormatErrorCode.Truncated,
                100,
                "fileLength",
                "collision fileLength at offset 100: the file declares 188 bytes, 100 are present"),
            "4-bytes-appended" => new([.. tiny, 0x00, 0x00, 0x00, 0x00], FormatErrorCode.Oversized, TinyWorldLength, "fileLength"),

            // 7: heights[4] = 1 without fixing the trailer; the checksum is checked before any value.
            "checksum-mismatch" => ChecksumMismatch(Write(tiny, 64, "0000803f")),

            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    private static CollisionRefusal ChecksumMismatch(byte[] mutated)
    {
        var content = XxHash64.HashToUInt64(mutated.AsSpan(0, TinyWorldTrailerOffset), 0);
        return new(
            mutated,
            FormatErrorCode.ChecksumMismatch,
            TinyWorldTrailerOffset,
            "checksum",
            $"collision checksum at offset 180: the trailer is 9224e3fa3b3fca9c, the content hashes to {content:x16}");
    }

    /// <summary>The fixture mutation <c>truncate</c>: the first <paramref name="length"/> bytes.</summary>
    private static byte[] Truncate(byte[] bytes, int length) => bytes.AsSpan(0, length).ToArray();

    /// <summary>The fixture mutation <c>write</c>: a copy with <paramref name="hex"/> written at <paramref name="offset"/>.</summary>
    private static byte[] Write(byte[] bytes, int offset, string hex)
    {
        var copy = (byte[])bytes.Clone();
        Convert.FromHexString(hex).CopyTo(copy.AsSpan(offset));
        return copy;
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var length = a.Length < b.Length ? a.Length : b.Length;
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return length;
    }

    /// <summary>A refused file and the error it must give; <c>Message</c>, when set, is the whole text.</summary>
    private sealed record CollisionRefusal(
        byte[] Bytes,
        FormatErrorCode Code,
        long Offset,
        string Field,
        string? Message = null);
}
