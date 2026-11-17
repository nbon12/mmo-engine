using System.Buffers.Binary;
using System.IO.Hashing;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Byte-level edits of encoded recorded streams (contracts/recorded-stream-format.md#layout), written from
/// the contract and independent of <c>RecordedStream</c>, so a test can corrupt one field of a committed
/// stream and keep the rest of the file valid. Every member leaves its argument unchanged, and every edit
/// recomputes the trailer as the XxHash64 (seed 0) of every byte before it.
/// </summary>
public static class StreamBytes
{
    private const int TrailerLength = 8;
    private const int HeaderLength = 64;
    private const int StepCountOffset = 16;
    private const int ConfigLengthOffset = 44;
    private const int InitialStateLengthOffset = 48;
    private const int StepsLengthOffset = 52;
    private const int FileLengthOffset = 56;

    /// <summary>A step entry's <c>u32 step</c> and <c>u8 commandCount</c>.</summary>
    private const int EntryHeadLength = 5;

    /// <summary>A command's <c>u8 characterIndex</c> and its 17-byte input command.</summary>
    private const int CommandLength = 18;

    private const int ExpectedHashLength = 8;

    /// <summary>The file's checksum: its last 8 bytes, little-endian.</summary>
    /// <exception cref="ArgumentException">The file is shorter than 8 bytes.</exception>
    public static ulong Trailer(ReadOnlySpan<byte> file)
    {
        if (file.Length < TrailerLength)
        {
            throw new ArgumentException($"A {file.Length}-byte file has no {TrailerLength}-byte trailer.", nameof(file));
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(file[^TrailerLength..]);
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> with <c>steps[stepIndex].expectedHash</c> set to
    /// <paramref name="hash"/> and the trailer recomputed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepIndex"/> is negative or not below <c>stepCount</c>.</exception>
    /// <exception cref="ArgumentException">The header's lengths do not match the file, or the entries run past the steps block.</exception>
    public static byte[] WithExpectedHash(byte[] stream, int stepIndex, ulong hash)
    {
        var (_, hashOffset) = Entry(stream, stepIndex);
        var copy = (byte[])stream.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan((int)hashOffset, ExpectedHashLength), hash);
        return FixTrailer(copy);
    }

    /// <summary>
    /// The file offset of <c>steps[stepIndex].step</c>: the entry's start, walking the steps block entry by
    /// entry (13 + 18·<c>commandCount</c> bytes each) from 64 + <c>configLength</c> + <c>initialStateLength</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepIndex"/> is negative or not below <c>stepCount</c>.</exception>
    /// <exception cref="ArgumentException">The header's lengths do not match the file, or the entries run past the steps block.</exception>
    public static long StepOffset(byte[] stream, int stepIndex) => Entry(stream, stepIndex).Start;

    /// <summary>
    /// A copy of <paramref name="stream"/> with <c>steps[stepIndex].step</c> set to <paramref name="step"/>
    /// and the trailer recomputed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepIndex"/> is negative or not below <c>stepCount</c>.</exception>
    /// <exception cref="ArgumentException">The header's lengths do not match the file, or the entries run past the steps block.</exception>
    public static byte[] WithStepNumber(byte[] stream, int stepIndex, uint step)
    {
        var (start, _) = Entry(stream, stepIndex);
        var copy = (byte[])stream.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan((int)start, 4), step);
        return FixTrailer(copy);
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> with no step entries: the header, config block and initial state
    /// kept, <c>stepCount</c> 0, <c>stepsLength</c> 0, <c>fileLength</c> 64 + <c>configLength</c> +
    /// <c>initialStateLength</c> + 8, and the trailer recomputed. The frame is consistent, so only the
    /// <c>stepCount</c> limit (check 5) refuses it.
    /// </summary>
    /// <exception cref="ArgumentException">The header's lengths do not match the file.</exception>
    public static byte[] WithoutSteps(byte[] stream)
    {
        var (stepsStart, _) = Blocks(stream);
        var copy = new byte[stepsStart + TrailerLength];
        stream.AsSpan(0, (int)stepsStart).CopyTo(copy);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(StepCountOffset), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(StepsLengthOffset), 0u);
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(FileLengthOffset), (ulong)copy.Length);
        return FixTrailer(copy);
    }

    /// <summary>The steps block's start and end, after checking the header's lengths against the file.</summary>
    private static (long Start, long End) Blocks(byte[] stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length < HeaderLength + TrailerLength)
        {
            throw new ArgumentException($"A {stream.Length}-byte file is shorter than a header and a trailer.", nameof(stream));
        }

        var header = stream.AsSpan(0, HeaderLength);
        long configLength = BinaryPrimitives.ReadUInt32LittleEndian(header[ConfigLengthOffset..]);
        long initialStateLength = BinaryPrimitives.ReadUInt32LittleEndian(header[InitialStateLengthOffset..]);
        long stepsLength = BinaryPrimitives.ReadUInt32LittleEndian(header[StepsLengthOffset..]);
        var fileLength = BinaryPrimitives.ReadUInt64LittleEndian(header[FileLengthOffset..]);
        var stepsStart = HeaderLength + configLength + initialStateLength;
        var stepsEnd = stepsStart + stepsLength;
        if (fileLength != (ulong)stream.Length || stepsEnd + TrailerLength != stream.Length)
        {
            throw new ArgumentException(
                $"The header declares fileLength {fileLength} and a steps block ending at {stepsEnd}; the file is {stream.Length} bytes.",
                nameof(stream));
        }

        return (stepsStart, stepsEnd);
    }

    /// <summary>Entry <paramref name="stepIndex"/>'s start (its <c>step</c> field) and its <c>expectedHash</c>'s offset.</summary>
    private static (long Start, long HashOffset) Entry(byte[] stream, int stepIndex)
    {
        var (stepsStart, stepsEnd) = Blocks(stream);
        var stepCount = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(StepCountOffset));
        ArgumentOutOfRangeException.ThrowIfNegative(stepIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)stepIndex, stepCount, nameof(stepIndex));

        var entry = stepsStart;
        for (var i = 0; ; i++)
        {
            if (entry + EntryHeadLength > stepsEnd)
            {
                throw new ArgumentException($"steps[{i}] starts at {entry}, past the steps block's end {stepsEnd}.", nameof(stream));
            }

            var hashOffset = entry + EntryHeadLength + (CommandLength * stream[entry + 4]);
            if (hashOffset + ExpectedHashLength > stepsEnd)
            {
                throw new ArgumentException($"steps[{i}] runs past the steps block's end {stepsEnd}.", nameof(stream));
            }

            if (i == stepIndex)
            {
                return (entry, hashOffset);
            }

            entry = hashOffset + ExpectedHashLength;
        }
    }

    /// <summary><paramref name="copy"/> with its trailer rewritten as the XxHash64 (seed 0) of every byte before it.</summary>
    private static byte[] FixTrailer(byte[] copy)
    {
        var trailer = copy.Length - TrailerLength;
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(trailer), XxHash64.HashToUInt64(copy.AsSpan(0, trailer), 0));
        return copy;
    }
}
