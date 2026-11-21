using System.Buffers.Binary;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// Hostile headers (FR-020, contracts/collision-data-format.md and contracts/recorded-stream-format.md:
/// "nothing is allocated before every count is bounded, so a hostile header cannot force a large
/// allocation"). Each row is a short file whose header declares counts that would need megabytes, up to
/// the counts' limits, so only the check that bounds the counts against the file's length (collision
/// check 6, stream checks 6 and 12) refuses it. The decoder must refuse it with the exact code, offset and
/// field, and allocate less than 64 KiB doing so; a buffer sized from any of these counts would be at
/// least 8 MiB.
/// </summary>
/// <remarks>
/// <para>
/// The class needs no serial collection, unlike <c>ZeroAllocationCollection</c>'s exact-zero tests. The
/// test method is synchronous, so between its two reads of the per-thread counter
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> only the measured decode runs on its thread: no
/// other test's continuation can land there inside the window. The warm-up decode has already compiled
/// and loaded everything the measured decode touches. And the bound is 64 KiB, not 0, so a stray small
/// allocation cannot decide the outcome, while a count-sized buffer is more than a hundred times over it.
/// </para>
/// <para>
/// The three header-only rows leave their trailers zero: the contracts refuse a file whose counts disagree
/// with its length before its checksum is computed (check 6 comes before check 7), so these rows also show
/// that the decoder never hashes a hostile file. <c>step-count-beyond-block</c> is refused by check 12,
/// after the checksum, so its trailer is rewritten.
/// </para>
/// </remarks>
public sealed class HostileHeaderTests
{
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>What a refusal may allocate on the thread: 64 KiB, against at least 8 MiB for a count-sized buffer.</summary>
    private const long AllocationLimit = 65_536;

    /// <summary>The collision header's length, and its <c>fileLength</c>, <c>columns</c>, <c>rows</c> and <c>meshCount</c> offsets.</summary>
    private const int CollisionHeaderLength = 48;
    private const int CollisionFileLengthOffset = 8;
    private const int ColumnsOffset = 32;
    private const int RowsOffset = 36;
    private const int MeshCountOffset = 40;

    /// <summary>The stream header's length, and its <c>stepCount</c> and <c>fileLength</c> offsets.</summary>
    private const int StreamHeaderLength = 64;
    private const int StepCountOffset = 16;
    private const int StreamFileLengthOffset = 56;

    /// <summary>The largest heightfield side, 4097 samples (contracts/collision-data-format.md#limits).</summary>
    private const uint MaxSamplesPerSide = 4097;

    /// <summary>The most meshes a collision file holds, 4096.</summary>
    private const uint MaxMeshCount = 4096;

    /// <summary>The most vertices a mesh holds, 2²⁰.</summary>
    private const uint MaxVertexCount = 1u << 20;

    /// <summary>The most triangles a mesh holds, 2²¹.</summary>
    private const uint MaxTriangleCount = 1u << 21;

    /// <summary>The most characters a stream holds, 64 (contracts/recorded-stream-format.md#header-64-bytes).</summary>
    private const ushort MaxCharacterCount = 64;

    /// <summary>The most step entries a stream holds, 2²⁰.</summary>
    private const uint MaxStepCount = 1u << 20;

    /// <summary>The longest config block, 65,536 bytes.</summary>
    private const uint MaxConfigLength = 65_536;

    /// <summary>The longest initial state, 1 MiB.</summary>
    private const uint MaxInitialStateLength = 1u << 20;

    /// <summary>
    /// FR-020: given a short file whose header declares counts at or near their limits (a 4097 × 4097
    /// heightfield and 4096 meshes in 56 bytes; a mesh of 2²⁰ vertices and 2²¹ triangles in 84 bytes;
    /// blocks of 64 KiB, 1 MiB and 4 GiB in 72 bytes; 2²⁰ steps in a block that holds two), when it is
    /// decoded once to warm up and then decoded again inside <c>try</c>/<c>catch (CoreFormatException)</c>
    /// with <see cref="GC.GetAllocatedBytesForCurrentThread"/> read around the second decode, then the
    /// decoder refuses it with exactly the row's code, offset and field (and, where the contract pins it,
    /// its detail: the length recomputed from the counts, and the stream's block sum in checked
    /// <c>u64</c>), returns no value, and allocates fewer than 65,536 bytes.
    /// </summary>
    [Theory]
    [InlineData(CollisionFormat, "huge-heightfield")]
    [InlineData(CollisionFormat, "huge-mesh")]
    [InlineData(StreamFormat, "huge-blocks")]
    [InlineData(StreamFormat, "step-count-beyond-block")]
    public void HostileCountsAreRefusedWithoutACountSizedAllocation(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        var row = Row(caseName);
        Assert.True(row.Format == format, $"{label}: the row is a {row.Format} file");

        // Warm up: compile and load the whole refusal path before anything is counted.
        _ = DecodeOnce(format, row.Bytes);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var refusal = DecodeOnce(format, row.Bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        if (refusal is null)
        {
            Assert.Fail($"{label}: the {row.Bytes.Length}-byte file decoded to a value; it must be refused with {row.Code} at offset {row.Offset}, field {row.Field}");
            return;
        }

        var error = refusal.Error;
        Assert.Equal(format, error.Format);
        Assert.True(
            error.Code == row.Code && error.Offset == row.Offset && error.Field == row.Field,
            $"{label}: refused as ({error.Code}, {error.Offset}, {error.Field}), expected ({row.Code}, {row.Offset}, {row.Field}): {error}");
        if (row.Detail is not null)
        {
            Assert.Equal(row.Detail, error.Detail);
        }

        Assert.Equal(error.ToString(), refusal.Message);
        Assert.True(
            allocated < AllocationLimit,
            $"{label}: refusing the {row.Bytes.Length}-byte file allocated {allocated} bytes on the thread; nothing sized from a count may be allocated before the counts are bounded, so the refusal allocates less than {AllocationLimit}");
    }

    /// <summary>
    /// Decodes <paramref name="bytes"/> with the format's decoder and discards the value: the refusal when
    /// the decoder throws <see cref="CoreFormatException"/>, or null when it returns a value. Any other
    /// exception fails the test.
    /// </summary>
    private static CoreFormatException? DecodeOnce(string format, byte[] bytes)
    {
        try
        {
            switch (format)
            {
                case CollisionFormat:
                    _ = CollisionFile.Decode(bytes);
                    break;
                case StreamFormat:
                    _ = RecordedStream.Decode(bytes);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "no hostile-header format");
            }

            return null;
        }
        catch (CoreFormatException refusal)
        {
            return refusal;
        }
    }

    /// <summary>A row's file, built before anything is counted, and the refusal it must get.</summary>
    private static HostileRow Row(string caseName) => caseName switch
    {
        // D + 12M + 8 = 48 + 4·4097·4097 + 12·4096 + 8 is far above fileLength 56: check 6's first clause.
        "huge-heightfield" => new(
            CollisionFormat,
            CollisionHeader(CollisionHeaderLength + 8, MaxSamplesPerSide, MaxSamplesPerSide, MaxMeshCount),
            FormatErrorCode.Inconsistent,
            CollisionFileLengthOffset,
            "fileLength",
            null),

        // The heights and directory fit (48 + 16 + 12 + 8 = 84) and the entry's counts are at their limits,
        // so check 6's last clause refuses it: 84 + 12·2²⁰ + 12·2²¹ = 37,748,820 bytes.
        "huge-mesh" => new(
            CollisionFormat,
            HugeMesh(),
            FormatErrorCode.Inconsistent,
            CollisionFileLengthOffset,
            "fileLength",
            "the counts need 37748820 bytes; fileLength is 84"),

        // 64 + 65,536 + 1,048,576 + 0xFFFFFFFF + 8 = 4,296,081,479 in checked u64; a u32 sum would wrap.
        "huge-blocks" => new(
            StreamFormat,
            HugeBlocks(),
            FormatErrorCode.Inconsistent,
            StreamFileLengthOffset,
            "fileLength",
            "the blocks need 4296081479 bytes; fileLength is 72"),

        // two-steps' two entries fill [898, 942); the third of 2²⁰ entries would start at the block's end.
        "step-count-beyond-block" => new(
            StreamFormat,
            StreamBytes.WithBytes(GoldenFixtures.ReadBin(StreamFormat, "two-steps"), StepCountOffset, U32(MaxStepCount)),
            FormatErrorCode.Inconsistent,
            942,
            "steps[2].step",
            null),

        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "no hostile-header row"),
    };

    /// <summary>
    /// A collision file of <paramref name="length"/> zero bytes with a valid frame (magic, version 1,
    /// header length 48, <c>fileLength</c> equal to the length), origin (0, 0), cell size 1, grid cell size
    /// 4, the given counts, <c>reserved</c> 0 and a zero trailer.
    /// </summary>
    private static byte[] CollisionHeader(int length, uint columns, uint rows, uint meshCount)
    {
        var file = new byte[length];
        var span = file.AsSpan();
        "MMCD"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], CollisionHeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(span[CollisionFileLengthOffset..], (ulong)length);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], 0f);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..], 0f);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(span[28..], 4f);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ColumnsOffset..], columns);
        BinaryPrimitives.WriteUInt32LittleEndian(span[RowsOffset..], rows);
        BinaryPrimitives.WriteUInt32LittleEndian(span[MeshCountOffset..], meshCount);
        return file;
    }

    /// <summary>
    /// 84 bytes: a 2 × 2 heightfield of zeros at [48, 64), one directory entry at [64, 76) with
    /// <c>meshId</c> 1, <c>vertexCount</c> 2²⁰ and <c>triangleCount</c> 2²¹, no mesh body, and a zero
    /// trailer at [76, 84).
    /// </summary>
    private static byte[] HugeMesh()
    {
        const int directoryOffset = CollisionHeaderLength + (4 * 4);
        var file = CollisionHeader(directoryOffset + 12 + 8, 2, 2, 1);
        var entry = file.AsSpan(directoryOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], MaxVertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], MaxTriangleCount);
        return file;
    }

    /// <summary>
    /// 72 bytes: a stream header (magic, version 1, header length 64, rules revision 1, 30 Hz) with
    /// <c>characterCount</c> 64, <c>stepCount</c> 2²⁰, <c>configLength</c> 65,536, <c>initialStateLength</c>
    /// 1,048,576, <c>stepsLength</c> <c>0xFFFFFFFF</c> and <c>fileLength</c> 72, then a zero trailer. Every
    /// header limit holds, so only the block sum refuses it.
    /// </summary>
    private static byte[] HugeBlocks()
    {
        var file = new byte[StreamHeaderLength + 8];
        var span = file.AsSpan();
        "MMRS"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], StreamHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 30);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], MaxCharacterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[StepCountOffset..], MaxStepCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], MaxConfigLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], MaxInitialStateLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(span[StreamFileLengthOffset..], (ulong)file.Length);
        return file;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>One row: the format, the file, and the refusal's code, offset, field and (when the contract pins it) detail.</summary>
    private sealed record HostileRow(string Format, byte[] Bytes, FormatErrorCode Code, long Offset, string Field, string? Detail);
}
