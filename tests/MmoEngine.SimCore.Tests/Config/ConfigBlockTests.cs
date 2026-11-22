using System.Buffers.Binary;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// The binary config block of recorded streams, in its standalone form: format <c>config</c>, offsets
/// from the block's first byte (contracts/recorded-stream-format.md#config-block).
/// </summary>
public sealed class ConfigBlockTests
{
    /// <summary>Placeholder entries: 3 general, 20 movement, 2 × 21 weapon, 2 × 3 class, 42 + 2 hitbox, 1 input.</summary>
    private const int DefaultCount = 116;

    /// <summary>2 + 6 · 116.</summary>
    private const int DefaultLength = 698;

    /// <summary>Entry 115, the last: <c>input.weaponSlots</c> (0x5001).</summary>
    private const int LastEntryOffset = 692;

    /// <summary>Entry 23, the first of weapon slot 0's 21 entries: <c>weapons[0].roundsPerMinute</c> (0x2001).</summary>
    private const int WeaponSlot0Offset = 140;

    /// <summary>Entry 25: <c>weapons[0].fireMode</c> (0x2003).</summary>
    private const int FireMode0Offset = 152;

    public static TheoryData<string> MalformedCases =>
    [
        "empty",
        "count-117",
        "count-115",
        "first-two-swapped",
        "repeated-field-id",
        "unknown-field-id",
        "last-entry-missing",
        "weapon-slot-0-missing",
    ];

    [Fact]
    public void BlockRoundTripsToTheSameBitsAndGuardResult()
    {
        var block = ConfigBlock.Encode(TestConfigs.Default());

        Assert.Equal(DefaultLength, block.Length);
        Assert.Equal(DefaultCount, BinaryPrimitives.ReadUInt16LittleEndian(block));
        var fieldIds = Enumerable.Range(0, DefaultCount).Select(k => FieldIdAt(block, k)).ToArray();
        Assert.Equal(fieldIds.Distinct().Order().ToArray(), fieldIds);

        Assert.Equal(0x0001, FieldIdAt(block, 0));
        Assert.Equal(30u, ValueBitsAt(block, 0));
        Assert.Equal(0x1007, FieldIdAt(block, 9));
        Assert.Equal(0x411CF5C3u, BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(58)));
        Assert.Equal(0x5001, FieldIdAt(block, DefaultCount - 1));
        Assert.Equal(2u, ValueBitsAt(block, DefaultCount - 1));

        var decoded = ConfigBlock.Decode(block);
        Assert.Equal(block, ConfigBlock.Encode(decoded));

        // A NaN decodes with its bits; the guard refuses it the same way before and after the round trip.
        const uint nanWithPayload = 0x7FC01234u;
        var nanConfig = TestConfigs.With(c => c.Movement!.Gravity = BitConverter.UInt32BitsToSingle(nanWithPayload));
        var nanBlock = ConfigBlock.Encode(nanConfig);
        Assert.Equal(nanWithPayload, BinaryPrimitives.ReadUInt32LittleEndian(nanBlock.AsSpan(58)));
        var nanDecoded = ConfigBlock.Decode(nanBlock);
        Assert.Equal(nanWithPayload, BitConverter.SingleToUInt32Bits(nanDecoded.Movement!.Gravity));
        Assert.Equal(nanBlock, ConfigBlock.Encode(nanDecoded));
        ConfigError[] notFinite = [new("movement.gravity", "must be finite")];
        Assert.Equal(notFinite, ConfigGuard.Check(nanConfig));
        Assert.Equal(notFinite, ConfigGuard.Check(nanDecoded));

        // An enum value that is no member decodes with all 32 bits, and only the guard refuses it.
        var oddFireMode = (byte[])block.Clone();
        Assert.Equal(0x2003, BinaryPrimitives.ReadUInt16LittleEndian(oddFireMode.AsSpan(FireMode0Offset)));
        BinaryPrimitives.WriteUInt32LittleEndian(oddFireMode.AsSpan(FireMode0Offset + 2), 0x00000100u);
        var oddDecoded = ConfigBlock.Decode(oddFireMode);
        Assert.Equal(0x00000100u, (uint)oddDecoded.Weapons![0]!.FireMode);
        Assert.Equal(oddFireMode, ConfigBlock.Encode(oddDecoded));
        ConfigError[] notAMember = [new("weapons[0].fireMode", "must be Auto or Semi")];
        Assert.Equal(notAMember, ConfigGuard.Check(oddDecoded));
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void MalformedBlockIsRefusedNamingTheField(string caseName)
    {
        var refusal = Malformed(caseName);

        var exception = Assert.Throws<CoreFormatException>(() => ConfigBlock.Decode(refusal.Bytes));

        Assert.Equal("config", exception.Error.Format);
        Assert.Equal(refusal.Code, exception.Error.Code);
        Assert.Equal(refusal.Offset, exception.Error.Offset);
        Assert.Equal(refusal.Field, exception.Error.Field);
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"config {refusal.Field} at offset {refusal.Offset}: ", exception.Message, StringComparison.Ordinal);
        if (refusal.Message is not null)
        {
            Assert.Equal(refusal.Message, exception.Message);
        }

        if (refusal.Names is not null)
        {
            Assert.Contains(refusal.Names, exception.Message, StringComparison.Ordinal);
        }
    }

    private static Refusal Malformed(string caseName)
    {
        var block = ConfigBlock.Encode(TestConfigs.Default());
        return caseName switch
        {
            "empty" => new([], FormatErrorCode.Truncated, 0, "count"),
            "count-117" => new(WithCount(block, 117), FormatErrorCode.Truncated, DefaultLength, "count"),
            "count-115" => new(WithCount(block, 115), FormatErrorCode.Oversized, LastEntryOffset, "count"),
            "first-two-swapped" => new(
                [.. block.AsSpan(0, 2), .. block.AsSpan(8, 6), .. block.AsSpan(2, 6), .. block.AsSpan(14)],
                FormatErrorCode.Inconsistent,
                8,
                "entries[1].fieldId",
                Message: "config entries[1].fieldId at offset 8: field 0x0001 is not above 0x0002"),
            "repeated-field-id" => new(
                WithFieldId(block, 8, 0x0001),
                FormatErrorCode.Inconsistent,
                8,
                "entries[1].fieldId",
                Message: "config entries[1].fieldId at offset 8: field 0x0001 is not above 0x0001"),
            "unknown-field-id" => new(
                WithFieldId(block, LastEntryOffset, 0x6001),
                FormatErrorCode.Inconsistent,
                LastEntryOffset,
                "entries[115].fieldId",
                Names: "0x6001"),
            "last-entry-missing" => new(
                WithCount(block.AsSpan(0, LastEntryOffset).ToArray(), 115),
                FormatErrorCode.Inconsistent,
                LastEntryOffset,
                "input.weaponSlots",
                Message: "config input.weaponSlots at offset 692: field 0x5001 is missing"),
            "weapon-slot-0-missing" => new(
                WithCount([.. block.AsSpan(0, WeaponSlot0Offset), .. block.AsSpan(WeaponSlot0Offset + (21 * 6))], 95),
                FormatErrorCode.Inconsistent,
                WeaponSlot0Offset,
                "weapons[0].roundsPerMinute",
                Names: "0x2001"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    private static int FieldIdAt(byte[] block, int k) => BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2 + (6 * k)));

    private static uint ValueBitsAt(byte[] block, int k) => BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4 + (6 * k)));

    private static byte[] WithCount(byte[] block, ushort count)
    {
        var copy = (byte[])block.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy, count);
        return copy;
    }

    private static byte[] WithFieldId(byte[] block, int offset, ushort fieldId)
    {
        var copy = (byte[])block.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), fieldId);
        return copy;
    }

    /// <summary>A malformed block and the refusal it must give; <c>Message</c> is exact, <c>Names</c> must appear.</summary>
    private sealed record Refusal(
        byte[] Bytes,
        FormatErrorCode Code,
        long Offset,
        string Field,
        string? Message = null,
        string? Names = null);
}
