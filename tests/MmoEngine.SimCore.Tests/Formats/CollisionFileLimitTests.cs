using System.Buffers.Binary;
using System.IO.Hashing;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The collision decoder's limit and value checks that no golden fixture pins (FR-020, checks 5, 6, 8 and 9
/// of contracts/collision-data-format.md#check-order and #limits): each row is refused by
/// <see cref="CollisionFile.Decode"/> with exactly its code, offset and field, and no world is returned.
/// </summary>
/// <remarks>
/// <para>
/// Rows are built here from <c>CollisionFile.Encode(TestWorlds.TinyWorld())</c> (188 bytes: header [0, 48),
/// heights [48, 84), directory [84, 96), vertices [96, 144), indices [144, 180), trailer [180, 188)), from a
/// two-mesh variant of it, or, where the encoder would refuse the data, written byte by byte from the
/// contract's layout by <see cref="Raw"/> and the directory-only writers below.
/// </para>
/// <para>
/// Checks 5 and 6 read only the header and the directory, before the checksum (check 7), so their rows keep
/// a stale trailer: refusing them with their own code also shows they run before the checksum. Checks 8 and
/// 9 run after it, so their rows rewrite the trailer as the XxHash64 (seed 0) of every byte before it, and
/// the decoder reaches the value or the world build. The offsets of the two-mesh and hand-written rows are
/// computed from each file's header and directory (<see cref="Layout"/>), not written as literals.
/// </para>
/// </remarks>
public sealed class CollisionFileLimitTests
{
    private const string Format = "collision";
    private const int HeaderLength = 48;
    private const int TrailerLength = 8;
    private const int DirectoryEntryLength = 12;
    private const int VertexLength = 12;
    private const int TriangleLength = 12;

    private const int FileLengthOffset = 8;
    private const int OriginXOffset = 16;
    private const int OriginZOffset = 20;
    private const int CellSizeOffset = 24;
    private const int GridCellSizeOffset = 28;
    private const int ColumnsOffset = 32;
    private const int RowsOffset = 36;
    private const int MeshCountOffset = 40;
    private const int ReservedOffset = 44;

    /// <summary><c>tiny-world</c>'s <c>meshes[0].vertexCount</c>.</summary>
    private const int TinyVertexCountOffset = 88;

    /// <summary><c>tiny-world</c>'s <c>meshes[0].triangleCount</c>.</summary>
    private const int TinyTriangleCountOffset = 92;

    /// <summary><c>tiny-world</c>'s <c>meshes[0].vertices[0].y</c>.</summary>
    private const int TinyVertex0YOffset = 100;

    /// <summary>One more than the largest file, 256 MiB.</summary>
    private const int FileTooLongLength = 268_435_457;

    /// <summary>A quiet NaN's bits.</summary>
    private const uint NaNBits = 0x7FC00000u;

    /// <summary>Positive infinity's bits.</summary>
    private const uint InfinityBits = 0x7F800000u;

    public static TheoryData<string> Rows =>
    [
        "rows-too-few",
        "columns-too-many",
        "rows-too-many",
        "directory-does-not-fit",
        "vertex-count-too-few",
        "vertex-count-too-many",
        "triangle-count-zero",
        "triangle-count-too-many",
        "vertex-sum",
        "triangle-sum",
        "origin-x-non-finite",
        "origin-z-out-of-range",
        "cell-size-too-small",
        "cell-size-too-large",
        "grid-cell-too-large",
        "extent-z",
        "grid-cells-per-axis",
        "vertex-non-finite",
        "mesh-id-not-ascending",
        "reserved-before-origin",
        "mesh-by-mesh",
        "registrations-over-limit",
        "file-too-long",
    ];

    /// <summary>
    /// <b>Given</b> a collision file that breaks one limit or value rule, <b>when</b> it is decoded,
    /// <b>then</b> <see cref="CollisionFile.Decode"/> throws <see cref="CoreFormatException"/> with format
    /// <c>collision</c> and exactly the row's code, offset and field, and returns no world.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void EachLimitIsRefusedAtItsFieldAndOffset(string caseName)
    {
        var row = Row(caseName);

        var exception = Assert.Throws<CoreFormatException>(() => CollisionFile.Decode(row.Bytes));

        Assert.Equal(Format, exception.Error.Format);
        Assert.Equal(row.Code, exception.Error.Code);
        Assert.Equal(row.Offset, exception.Error.Offset);
        Assert.Equal(row.Field, exception.Error.Field);
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"{Format} {row.Field} at offset {row.Offset}: ", exception.Message, StringComparison.Ordinal);
    }

    private static LimitRow Row(string caseName) => caseName switch
    {
        // Check 5, count limits: columns and rows below 2 are OutOfRange, above 4097 Oversized; the trailer stays stale.
        "rows-too-few" => new(WriteU32(Tiny(), RowsOffset, 1), FormatErrorCode.OutOfRange, RowsOffset, "rows"),
        "columns-too-many" => new(WriteU32(Tiny(), ColumnsOffset, 4098), FormatErrorCode.Oversized, ColumnsOffset, "columns"),
        "rows-too-many" => new(WriteU32(Tiny(), RowsOffset, 4098), FormatErrorCode.Oversized, RowsOffset, "rows"),

        // Check 6: the heights and directory must fit before any entry is read, then each entry's counts and
        // the running sums (at most 2²² vertices and 2²² triangles); the trailer stays stale.
        "directory-does-not-fit" => new(DirectoryDoesNotFit(), FormatErrorCode.Inconsistent, FileLengthOffset, "fileLength"),
        "vertex-count-too-few" => new(WriteU32(Tiny(), TinyVertexCountOffset, 2), FormatErrorCode.OutOfRange, TinyVertexCountOffset, "meshes[0].vertexCount"),
        "vertex-count-too-many" => new(WriteU32(Tiny(), TinyVertexCountOffset, 1_048_577), FormatErrorCode.Oversized, TinyVertexCountOffset, "meshes[0].vertexCount"),
        "triangle-count-zero" => new(WriteU32(Tiny(), TinyTriangleCountOffset, 0), FormatErrorCode.OutOfRange, TinyTriangleCountOffset, "meshes[0].triangleCount"),
        "triangle-count-too-many" => new(WriteU32(Tiny(), TinyTriangleCountOffset, 2_097_153), FormatErrorCode.Oversized, TinyTriangleCountOffset, "meshes[0].triangleCount"),
        "vertex-sum" => VertexSum(),
        "triangle-sum" => TriangleSum(),

        // Check 8, values in the contract's order, with the trailer fixed so the decoder reaches them.
        "origin-x-non-finite" => new(FixTrailer(WriteBits(Tiny(), OriginXOffset, InfinityBits)), FormatErrorCode.NonFinite, OriginXOffset, "originX"),
        "origin-z-out-of-range" => new(FixTrailer(WriteF32(Tiny(), OriginZOffset, 9000f)), FormatErrorCode.OutOfRange, OriginZOffset, "originZ"),
        "cell-size-too-small" => new(FixTrailer(WriteF32(Tiny(), CellSizeOffset, 0.125f)), FormatErrorCode.OutOfRange, CellSizeOffset, "cellSize"),
        "cell-size-too-large" => new(FixTrailer(WriteF32(Tiny(), CellSizeOffset, 65f)), FormatErrorCode.OutOfRange, CellSizeOffset, "cellSize"),
        "grid-cell-too-large" => new(FixTrailer(WriteF32(Tiny(), GridCellSizeOffset, 65f)), FormatErrorCode.OutOfRange, GridCellSizeOffset, "gridCellSize"),

        // originZ 8191.5 is within ±8192, but the heightfield then ends at 8193.5 in Z: reported at rows.
        "extent-z" => new(FixTrailer(WriteF32(Tiny(), OriginZOffset, 8191.5f)), FormatErrorCode.OutOfRange, RowsOffset, "rows"),
        "grid-cells-per-axis" => GridCellsPerAxis(),
        "vertex-non-finite" => new(FixTrailer(WriteBits(Tiny(), TinyVertex0YOffset, NaNBits)), FormatErrorCode.NonFinite, TinyVertex0YOffset, "meshes[0].vertices[0].y"),
        "mesh-id-not-ascending" => MeshIdNotAscending(),

        // reserved is checked first, though originX comes first in the file.
        "reserved-before-origin" => new(
            FixTrailer(WriteBits(WriteU32(Tiny(), ReservedOffset, 1), OriginXOffset, NaNBits)),
            FormatErrorCode.OutOfRange,
            ReservedOffset,
            "reserved"),
        "mesh-by-mesh" => MeshByMesh(),

        // Check 9, the world build's counting pass.
        "registrations-over-limit" => RegistrationsOverLimit(),

        // Check 5's fileLength limit: the actual length matches fileLength (check 4), and is one byte over 256 MiB.
        "file-too-long" => FileTooLong(),

        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
    };

    /// <summary>
    /// A 60-byte file whose header (<c>tiny-world</c>'s: 3 × 3 samples, one mesh) declares <c>fileLength</c>
    /// 60, while its heights and directory alone need 48 + 36 + 12 + 8 = 104 bytes.
    /// </summary>
    private static byte[] DirectoryDoesNotFit()
    {
        const int length = 60;
        var file = new byte[length];
        Tiny().AsSpan(0, HeaderLength).CopyTo(file);
        Assert.Equal(3u, ReadU32(file, ColumnsOffset));
        Assert.Equal(3u, ReadU32(file, RowsOffset));
        Assert.Equal(1u, ReadU32(file, MeshCountOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(FileLengthOffset), length);
        return file;
    }

    /// <summary>
    /// A 2 × 2 heightfield with five directory entries, each 2²⁰ vertices and 1 triangle, and no bodies:
    /// <c>fileLength</c> 48 + 16 + 60 + 8 = 132, so the directory fits, and the fifth entry takes the vertex
    /// sum past 2²².
    /// </summary>
    private static LimitRow VertexSum()
    {
        var file = DirectoryOnly(meshCount: 5, vertexCount: 1u << 20, triangleCount: 1);
        Assert.Equal(132, file.Length);
        var entry = DirectoryEntryOffset(file, 4);
        return new(file, FormatErrorCode.Oversized, entry + 4, "meshes[4].vertexCount");
    }

    /// <summary>
    /// A 2 × 2 heightfield with three directory entries, each 3 vertices and 2²¹ triangles, and no bodies:
    /// <c>fileLength</c> 48 + 16 + 36 + 8 = 108, and the third entry takes the triangle sum past 2²².
    /// </summary>
    private static LimitRow TriangleSum()
    {
        var file = DirectoryOnly(meshCount: 3, vertexCount: 3, triangleCount: 1u << 21);
        Assert.Equal(108, file.Length);
        var entry = DirectoryEntryOffset(file, 2);
        return new(file, FormatErrorCode.Oversized, entry + 8, "meshes[2].triangleCount");
    }

    /// <summary>
    /// Origin (−8192, −1), cell size 4, grid cell size 1, 4097 × 2 samples and no meshes: the heightfield
    /// spans exactly to 8192 in X, so the extent check passes, but its 16,384 m width needs 16,384 grid cells
    /// along X, past 4096.
    /// </summary>
    private static LimitRow GridCellsPerAxis()
    {
        const int columns = 4097;
        const int rows = 2;
        var file = Raw(new CollisionData(
            OriginX: -8192f,
            OriginZ: -1f,
            CellSize: 4f,
            GridCellSize: 1f,
            Columns: columns,
            Rows: rows,
            Heights: new float[columns * rows],
            Meshes: []));
        return new(file, FormatErrorCode.OutOfRange, GridCellSizeOffset, "gridCellSize");
    }

    /// <summary>
    /// The two-mesh world with mesh 1's <c>meshId</c> set to mesh 0's, so the IDs are not strictly ascending.
    /// </summary>
    private static LimitRow MeshIdNotAscending()
    {
        var file = CollisionFile.Encode(TwoMeshes());
        var layout = Layout(file);
        var mesh0Id = ReadU32(file, layout[0].Entry);
        Assert.NotEqual(mesh0Id, ReadU32(file, layout[1].Entry));
        return new(FixTrailer(WriteU32(file, layout[1].Entry, mesh0Id)), FormatErrorCode.Inconsistent, layout[1].Entry, "meshes[1].meshId");
    }

    /// <summary>
    /// The two-mesh world with mesh 0's <c>indices[0]</c> set to its vertex count and mesh 1's
    /// <c>vertices[0].x</c> set to NaN: check 8 finishes mesh 0's indices before it reads mesh 1's vertices,
    /// though both are wrong.
    /// </summary>
    private static LimitRow MeshByMesh()
    {
        var file = CollisionFile.Encode(TwoMeshes());
        var layout = Layout(file);
        var changed = WriteU32(file, layout[0].Indices, layout[0].VertexCount);
        changed = WriteBits(changed, layout[1].Vertices, NaNBits);
        return new(FixTrailer(changed), FormatErrorCode.OutOfRange, layout[0].Indices, "meshes[0].indices[0]");
    }

    /// <summary>
    /// Origin (−2048, −2048), cell size 4, grid cell size 1 and 1025 × 1025 samples, so the grid is
    /// 4096 × 4096 = 2²⁴ cells, with one mesh of two triangles, (0, 1, 2) and (1, 3, 2), over the corners
    /// (±2048, 0, ±2048). Each triangle's box overlaps every cell, so the first fills the 2²⁴ registrations
    /// and the second goes past them; the refusal is at the second triangle's first index.
    /// </summary>
    private static LimitRow RegistrationsOverLimit()
    {
        const int samples = 1025;
        var file = Raw(new CollisionData(
            OriginX: -2048f,
            OriginZ: -2048f,
            CellSize: 4f,
            GridCellSize: 1f,
            Columns: samples,
            Rows: samples,
            Heights: new float[samples * samples],
            Meshes:
            [
                new MeshData(
                    MeshId: 1,
                    Vertices:
                    [
                        -2048f, 0f, -2048f,
                        2048f, 0f, -2048f,
                        -2048f, 0f, 2048f,
                        2048f, 0f, 2048f,
                    ],
                    Indices: [0, 1, 2, 1, 3, 2]),
            ]));
        var secondTriangle = Layout(file)[0].Indices + TriangleLength;
        return new(file, FormatErrorCode.OutOfRange, secondTriangle, "grid.registrations");
    }

    /// <summary>
    /// A 268,435,457-byte buffer: <c>tiny-world</c>'s header with <c>fileLength</c> 268,435,457, then zeros.
    /// The actual length equals <c>fileLength</c>, so check 4 passes, and check 5 refuses the length.
    /// </summary>
    private static LimitRow FileTooLong()
    {
        var file = new byte[FileTooLongLength];
        Tiny().AsSpan(0, HeaderLength).CopyTo(file);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(FileLengthOffset), FileTooLongLength);
        return new(file, FormatErrorCode.Oversized, FileLengthOffset, "fileLength");
    }

    /// <summary>The <c>tiny-world</c> fixture's bytes, written by the encoder.</summary>
    private static byte[] Tiny() => CollisionFile.Encode(TestWorlds.TinyWorld());

    /// <summary>
    /// <c>tiny-world</c> with a second mesh after its own: <c>meshId</c> 2, the triangle (−0.5, 0, −0.5),
    /// (0.5, 0, −0.5), (0.5, 1, −0.5), which is not degenerate and lies in the single grid cell. The IDs are
    /// ascending, so the encoder writes it.
    /// </summary>
    private static CollisionData TwoMeshes()
    {
        var tiny = TestWorlds.TinyWorld();
        return tiny with
        {
            Meshes =
            [
                tiny.Meshes[0],
                new MeshData(
                    MeshId: 2,
                    Vertices:
                    [
                        -0.5f, 0f, -0.5f,
                        0.5f, 0f, -0.5f,
                        0.5f, 1f, -0.5f,
                    ],
                    Indices: [0, 1, 2]),
            ],
        };
    }

    /// <summary>
    /// <paramref name="data"/> written as a collision file from the contract's layout, without the encoder's
    /// decode: the header with <c>fileLength</c> computed and <c>reserved</c> 0, the heights, the directory,
    /// the mesh bodies in directory order, and the XxHash64 (seed 0) trailer.
    /// </summary>
    private static byte[] Raw(CollisionData data)
    {
        long length = HeaderLength + (4L * data.Heights.Length) + (DirectoryEntryLength * (long)data.Meshes.Length) + TrailerLength;
        foreach (var mesh in data.Meshes)
        {
            length += (4L * mesh.Vertices.Length) + (4L * mesh.Indices.Length);
        }

        var file = new byte[length];
        var span = file.AsSpan();
        "MMCD"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], HeaderLength);
        BinaryPrimitives.WriteUInt64LittleEndian(span[FileLengthOffset..], (ulong)length);
        BinaryPrimitives.WriteSingleLittleEndian(span[OriginXOffset..], data.OriginX);
        BinaryPrimitives.WriteSingleLittleEndian(span[OriginZOffset..], data.OriginZ);
        BinaryPrimitives.WriteSingleLittleEndian(span[CellSizeOffset..], data.CellSize);
        BinaryPrimitives.WriteSingleLittleEndian(span[GridCellSizeOffset..], data.GridCellSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ColumnsOffset..], (uint)data.Columns);
        BinaryPrimitives.WriteUInt32LittleEndian(span[RowsOffset..], (uint)data.Rows);
        BinaryPrimitives.WriteUInt32LittleEndian(span[MeshCountOffset..], (uint)data.Meshes.Length);

        var offset = HeaderLength;
        foreach (var height in data.Heights)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[offset..], height);
            offset += 4;
        }

        foreach (var mesh in data.Meshes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], mesh.MeshId);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], (uint)(mesh.Vertices.Length / 3));
            BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], (uint)(mesh.Indices.Length / 3));
            offset += DirectoryEntryLength;
        }

        foreach (var mesh in data.Meshes)
        {
            foreach (var coordinate in mesh.Vertices)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[offset..], coordinate);
                offset += 4;
            }

            foreach (var index in mesh.Indices)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], index);
                offset += 4;
            }
        }

        Assert.Equal(length - TrailerLength, (long)offset);
        return FixTrailer(file);
    }

    /// <summary>
    /// A file with <c>tiny-world</c>'s header values but a 2 × 2 heightfield of zeros, then
    /// <paramref name="meshCount"/> directory entries (IDs 1, 2, …, each with the given counts) and no mesh
    /// bodies, then an 8-byte zero trailer; <c>fileLength</c> is that length, 48 + 16 + 12·meshCount + 8.
    /// The bodies the counts declare are absent, which check 6 finds only after the entries and sums.
    /// </summary>
    private static byte[] DirectoryOnly(int meshCount, uint vertexCount, uint triangleCount)
    {
        const int columns = 2;
        const int rows = 2;
        var directory = HeaderLength + (4 * columns * rows);
        var length = directory + (DirectoryEntryLength * meshCount) + TrailerLength;
        var file = new byte[length];
        Tiny().AsSpan(0, HeaderLength).CopyTo(file);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(FileLengthOffset), (ulong)length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(ColumnsOffset), columns);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(RowsOffset), rows);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(MeshCountOffset), (uint)meshCount);
        for (var m = 0; m < meshCount; m++)
        {
            var entry = directory + (DirectoryEntryLength * m);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry), (uint)(m + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 4), vertexCount);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(entry + 8), triangleCount);
        }

        return file;
    }

    /// <summary>Directory entry <paramref name="mesh"/>'s offset, D + 12m with D = 48 + 4·columns·rows, from the header.</summary>
    private static int DirectoryEntryOffset(byte[] file, int mesh)
    {
        var columns = (int)ReadU32(file, ColumnsOffset);
        var rows = (int)ReadU32(file, RowsOffset);
        return HeaderLength + (4 * columns * rows) + (DirectoryEntryLength * mesh);
    }

    /// <summary>
    /// Each mesh's directory entry, vertex block and index block offsets and its vertex count, walked from the
    /// header and directory: mesh bodies start at D + 12M, and mesh m's body is 12·Vₘ + 12·Tₘ bytes.
    /// </summary>
    private static MeshLayout[] Layout(byte[] file)
    {
        var meshCount = (int)ReadU32(file, MeshCountOffset);
        var body = DirectoryEntryOffset(file, meshCount);
        var meshes = new MeshLayout[meshCount];
        for (var m = 0; m < meshCount; m++)
        {
            var entry = DirectoryEntryOffset(file, m);
            var vertexCount = ReadU32(file, entry + 4);
            var triangleCount = ReadU32(file, entry + 8);
            var indices = body + (VertexLength * (int)vertexCount);
            meshes[m] = new MeshLayout(entry, body, indices, vertexCount);
            body = indices + (TriangleLength * (int)triangleCount);
        }

        Assert.Equal(file.Length - TrailerLength, body);
        return meshes;
    }

    private static uint ReadU32(byte[] file, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));

    /// <summary>A copy of <paramref name="file"/> with the u32 <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteU32(byte[] file, int offset, uint value)
    {
        var copy = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <summary>A copy of <paramref name="file"/> with the f32 <paramref name="value"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteF32(byte[] file, int offset, float value) => WriteU32(file, offset, BitConverter.SingleToUInt32Bits(value));

    /// <summary>A copy of <paramref name="file"/> with a float's <paramref name="bits"/> written at <paramref name="offset"/>.</summary>
    private static byte[] WriteBits(byte[] file, int offset, uint bits) => WriteU32(file, offset, bits);

    /// <summary>The <c>fixChecksum</c> mutation, in place: the 8-byte trailer rewritten as the XxHash64 (seed 0) of every byte before it.</summary>
    private static byte[] FixTrailer(byte[] file)
    {
        var trailer = file.Length - TrailerLength;
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(trailer), XxHash64.HashToUInt64(file.AsSpan(0, trailer), 0));
        return file;
    }

    /// <summary>A mesh's directory entry offset, its first vertex's offset, its first index's offset, and its vertex count.</summary>
    private readonly record struct MeshLayout(int Entry, int Vertices, int Indices, uint VertexCount);

    /// <summary>A refused file and the code, offset and field its refusal must carry.</summary>
    private sealed record LimitRow(byte[] Bytes, FormatErrorCode Code, long Offset, string Field);
}
