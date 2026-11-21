using System.Buffers.Binary;
using System.IO.Hashing;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The stream decoder's structural checks that no golden fixture pins (FR-020, checks 5, 8 and 12 of
/// contracts/recorded-stream-format.md#decoding): each row is refused by <see cref="RecordedStream.Decode"/>
/// with exactly its code, offset and field, and no stream is returned.
/// </summary>
/// <remarks>
/// <para>
/// Rows are built from the committed golden fixtures <c>stream/two-steps.bin</c> and
/// <c>stream/two-characters.bin</c>, read-only through <see cref="GoldenFixtures"/>; every change works on a
/// copy. <c>two-steps</c>' layout is pinned by contracts/recorded-stream-format.md#two-steps: header
/// [0, 64), config [64, 762), initial state [762, 898), step 1 [898, 929) with its command at 904, step 2
/// [929, 942) with its expected hash at 934, trailer [942, 950). <c>two-characters</c>' offsets are computed
/// from its header: the steps block starts at 64 + <c>configLength</c> + <c>initialStateLength</c>, and
/// command c's character index is at the entry's start + 5 + 18c.
/// </para>
/// <para>
/// Check 5 reads only the header, before the checksum (check 7), so its rows keep a stale trailer: refusing
/// them with their own code also shows they run before the checksum. Checks 8 and 12 run after it, so their
/// rows rewrite the trailer as the XxHash64 (seed 0) of every byte before it, and the decoder reaches them.
/// </para>
/// </remarks>
public sealed class RecordedStreamCheckTests
{
    private const string Format = "stream";
    private const int HeaderLength = 64;
    private const int TrailerLength = 8;

    private const int StepHzOffset = 12;
    private const int CharacterCountOffset = 14;
    private const int StepCountOffset = 16;
    private const int ConfigLengthOffset = 44;
    private const int InitialStateLengthOffset = 48;
    private const int StepsLengthOffset = 52;
    private const int FileLengthOffset = 56;

    /// <summary>A step entry's <c>u32 step</c> and <c>u8 commandCount</c>.</summary>
    private const int EntryHeadLength = 5;

    /// <summary>A command's <c>u8 characterIndex</c> and its 17-byte input command.</summary>
    private const int CommandLength = 18;

    /// <summary><c>two-steps</c>' whole length.</summary>
    private const int TwoStepsLength = 950;

    /// <summary><c>two-steps</c>' <c>stepsLength</c>.</summary>
    private const int TwoStepsStepsLength = 44;

    /// <summary><c>two-steps</c>' <c>steps[0].step</c>, the steps block's first byte.</summary>
    private const int TwoStepsStep1 = 898;

    /// <summary><c>two-steps</c>' <c>steps[0].commands[0].input</c>, the command's <c>formatId</c>.</summary>
    private const int TwoStepsStep1Command = 904;

    /// <summary><c>two-steps</c>' <c>steps[1].expectedHash</c>.</summary>
    private const int TwoStepsStep2Hash = 934;

    /// <summary><c>two-steps</c>' trailer, which is also the steps block's end.</summary>
    private const int TwoStepsTrailer = 942;

    /// <summary>One more than the largest stream, 64 MiB.</summary>
    private const int FileTooLongLength = 67_108_865;

    public static TheoryData<string> Rows =>
    [
        "file-too-long",
        "step-count-too-many",
        "zero-characters",
        "step-hz-above",
        "step-hz-below",
        "first-step-number",
        "command-unknown-magic",
        "commands-not-ascending",
        "entry-past-block",
        "bytes-after-entries",
        "step-count-beyond-block",
    ];

    /// <summary>
    /// <b>Given</b> a stream that breaks one structural rule, <b>when</b> it is decoded, <b>then</b>
    /// <see cref="RecordedStream.Decode"/> throws <see cref="CoreFormatException"/> with format <c>stream</c>
    /// and exactly the row's code, offset and field, and returns no stream.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void EachStructuralCheckIsRefusedAtItsFieldAndOffset(string caseName)
    {
        var row = Row(caseName);

        var exception = Assert.Throws<CoreFormatException>(() => RecordedStream.Decode(row.Bytes));

        Assert.Equal(Format, exception.Error.Format);
        Assert.Equal(row.Code, exception.Error.Code);
        Assert.Equal(row.Offset, exception.Error.Offset);
        Assert.Equal(row.Field, exception.Error.Field);
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"{Format} {row.Field} at offset {row.Offset}: ", exception.Message, StringComparison.Ordinal);
    }

    private static CheckRow Row(string caseName) => caseName switch
    {
        // Check 5, header limits, before the checksum: the trailer stays stale.
        "file-too-long" => FileTooLong(),
        "step-count-too-many" => new(WriteU32(TwoSteps(), StepCountOffset, 1_048_577), FormatErrorCode.Oversized, StepCountOffset, "stepCount"),
        "zero-characters" => new(WriteU16(TwoSteps(), CharacterCountOffset, 0), FormatErrorCode.OutOfRange, CharacterCountOffset, "characterCount"),

        // Check 8, stepHz in [20, 60], after the checksum: the trailer is fixed.
        "step-hz-above" => new(FixTrailer(WriteU16(TwoSteps(), StepHzOffset, 61)), FormatErrorCode.OutOfRange, StepHzOffset, "stepHz"),
        "step-hz-below" => new(FixTrailer(WriteU16(TwoSteps(), StepHzOffset, 19)), FormatErrorCode.OutOfRange, StepHzOffset, "stepHz"),

        // Check 12, each step entry in order, with the trailer fixed.
        "first-step-number" => new(FixTrailer(WriteU32(TwoSteps(), TwoStepsStep1, 2)), FormatErrorCode.Inconsistent, TwoStepsStep1, "steps[0].step"),
        "command-unknown-magic" => new(
            FixTrailer(WriteU8(TwoSteps(), TwoStepsStep1Command, 0x4A)),
            FormatErrorCode.UnknownMagic,
            TwoStepsStep1Command,
            "steps[0].commands[0].input.formatId"),
        "commands-not-ascending" => CommandsNotAscending(),
        "entry-past-block" => EntryPastBlock(),
        "bytes-after-entries" => BytesAfterEntries(),

        // stepCount 2²⁰ is within check 5's limit; the block holds two entries, so the third runs past it.
        "step-count-beyond-block" => new(
            FixTrailer(WriteU32(TwoSteps(), StepCountOffset, 1_048_576)),
            FormatErrorCode.Inconsistent,
            TwoStepsTrailer,
            "steps[2].step"),

        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
    };

    /// <summary>
    /// A 67,108,865-byte buffer: <c>two-steps</c>' header with <c>fileLength</c> 67,108,865, then zeros. The
    /// actual length equals <c>fileLength</c>, so check 4 passes, and check 5 refuses the length.
    /// </summary>
    private static CheckRow FileTooLong()
    {
        var file = new byte[FileTooLongLength];
        TwoSteps().AsSpan(0, HeaderLength).CopyTo(file);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(FileLengthOffset), FileTooLongLength);
        return new(file, FormatErrorCode.Oversized, FileLengthOffset, "fileLength");
    }

    /// <summary>
    /// <c>two-characters</c> with step 1's two character indices written as 1 then 0: each is below the
    /// character count 2, but the second is not above the first.
    /// </summary>
    private static CheckRow CommandsNotAscending()
    {
        var file = GoldenFixtures.ReadBin(Format, "two-characters");
        var stepsStart = HeaderLength + (int)ReadU32(file, ConfigLengthOffset) + (int)ReadU32(file, InitialStateLengthOffset);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(CharacterCountOffset)));
        Assert.Equal((byte)2, file[stepsStart + 4]);
        var first = stepsStart + EntryHeadLength;
        var second = stepsStart + EntryHeadLength + CommandLength;
        var changed = WriteU8(WriteU8(file, first, 1), second, 0);
        return new(FixTrailer(changed), FormatErrorCode.Inconsistent, second, "steps[0].commands[1].characterIndex");
    }

    /// <summary>
    /// <c>two-steps</c> with 4 bytes deleted at 934, inside step 2's expected hash, <c>stepsLength</c> 40 and
    /// <c>fileLength</c> 946: the frame is consistent, and step 2's 8-byte hash runs past the block's end at
    /// 938.
    /// </summary>
    private static CheckRow EntryPastBlock()
    {
        var file = Delete(TwoSteps(), TwoStepsStep2Hash, 4);
        file = WriteU32(file, StepsLengthOffset, TwoStepsStepsLength - 4);
        file = WriteU64(file, FileLengthOffset, TwoStepsLength - 4);
        Assert.Equal(TwoStepsLength - 4, file.Length);
        return new(FixTrailer(file), FormatErrorCode.Inconsistent, TwoStepsStep2Hash, "steps[1].expectedHash");
    }

    /// <summary>
    /// <c>two-steps</c> with 4 zero bytes inserted at 942, after step 2, <c>stepsLength</c> 48 and
    /// <c>fileLength</c> 954: the two entries end at 942 and leave 4 bytes of the block unread.
    /// </summary>
    private static CheckRow BytesAfterEntries()
    {
        var file = Insert(TwoSteps(), TwoStepsTrailer, [0, 0, 0, 0]);
        file = WriteU32(file, StepsLengthOffset, TwoStepsStepsLength + 4);
        file = WriteU64(file, FileLengthOffset, TwoStepsLength + 4);
        Assert.Equal(TwoStepsLength + 4, file.Length);
        return new(FixTrailer(file), FormatErrorCode.Inconsistent, TwoStepsTrailer, "steps");
    }

    /// <summary>The committed <c>stream/two-steps.bin</c>, checked against the contract's lengths.</summary>
    private static byte[] TwoSteps()
    {
        var file = GoldenFixtures.ReadBin(Format, "two-steps");
        Assert.Equal(TwoStepsLength, file.Length);
        Assert.Equal((uint)TwoStepsStepsLength, ReadU32(file, StepsLengthOffset));
        Assert.Equal(TwoStepsStep1, HeaderLength + (int)ReadU32(file, ConfigLengthOffset) + (int)ReadU32(file, InitialStateLengthOffset));
        return file;
    }

    private static uint ReadU32(byte[] file, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));

    /// <summary>A copy of <paramref name="file"/> with the byte <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteU8(byte[] file, int offset, byte value)
    {
        var copy = (byte[])file.Clone();
        copy[offset] = value;
        return copy;
    }

    /// <summary>A copy of <paramref name="file"/> with the u16 <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteU16(byte[] file, int offset, ushort value)
    {
        var copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <summary>A copy of <paramref name="file"/> with the u32 <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteU32(byte[] file, int offset, uint value)
    {
        var copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <summary>A copy of <paramref name="file"/> with the u64 <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteU64(byte[] file, int offset, ulong value)
    {
        var copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <summary>The <c>insert</c> mutation: <paramref name="bytes"/> inserted before <paramref name="offset"/>.</summary>
    private static byte[] Insert(byte[] file, int offset, byte[] bytes) => [.. file.AsSpan(0, offset), .. bytes, .. file.AsSpan(offset)];

    /// <summary>The <c>delete</c> mutation: <paramref name="length"/> bytes removed at <paramref name="offset"/>.</summary>
    private static byte[] Delete(byte[] file, int offset, int length) => [.. file.AsSpan(0, offset), .. file.AsSpan(offset + length)];

    /// <summary>The <c>fixChecksum</c> mutation, in place: the 8-byte trailer rewritten as the XxHash64 (seed 0) of every byte before it.</summary>
    private static byte[] FixTrailer(byte[] file)
    {
        var trailer = file.Length - TrailerLength;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(trailer), XxHash64.HashToUInt64(file.AsSpan(0, trailer), 0));
        return file;
    }

    /// <summary>A refused stream and the code, offset and field its refusal must carry.</summary>
    private sealed record CheckRow(byte[] Bytes, FormatErrorCode Code, long Offset, string Field);
}
