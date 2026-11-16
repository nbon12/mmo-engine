using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using MmoEngine.SimCore.Collision;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// A collision world's primary data, as the <c>MMCD</c> file holds it
/// (specs/015-shared-simulation-core/contracts/collision-data-format.md#api): the heightfield's origin,
/// sample spacing, grid cell size, sample counts and row-major heights (j along Z outer, i along X inner),
/// and the meshes in directory order.
/// </summary>
public sealed record CollisionData(
    float OriginX,
    float OriginZ,
    float CellSize,
    float GridCellSize,
    int Columns,
    int Rows,
    float[] Heights,
    MeshData[] Meshes);

/// <summary>
/// One mesh of a collision file: its ID, its vertices as x, y, z per vertex, and its triangles as index
/// triples into the vertices.
/// </summary>
public sealed record MeshData(uint MeshId, float[] Vertices, uint[] Indices);

/// <summary>
/// The collision data file <c>MMCD</c>, format version 1 (FR-021,
/// specs/015-shared-simulation-core/contracts/collision-data-format.md): a 48-byte header, the heights,
/// the mesh directory, the mesh bodies in directory order, and the XxHash64 (seed 0) trailer, all
/// little-endian with floats as exact binary32 bits. It is the only input a <see cref="CollisionWorld"/>
/// is built from (FR-004). Decoding is not on the tick path and may allocate.
/// </summary>
public static class CollisionFile
{
    /// <summary>The only version this decoder reads and the encoder writes.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>The header's length in bytes.</summary>
    public const int HeaderLength = 48;

    /// <summary>The largest file, 256 MiB, trailer included.</summary>
    public const long MaxFileLength = 256L * 1024 * 1024;

    private const string Format = "collision";
    private const int TrailerLength = 8;
    private const int DirectoryEntryLength = 12;
    private const int VertexLength = 12;
    private const int TriangleLength = 12;

    private const int MagicLength = 4;
    private const int VersionOffset = 4;
    private const int HeaderLengthOffset = 6;
    private const int FileLengthOffset = 8;
    private const int OriginXOffset = 16;
    private const int OriginZOffset = 20;
    private const int CellSizeOffset = 24;
    private const int GridCellSizeOffset = 28;
    private const int ColumnsOffset = 32;
    private const int RowsOffset = 36;
    private const int MeshCountOffset = 40;

    /// <summary>The file's first four bytes, ASCII <c>MMCD</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "MMCD"u8;

    /// <summary>
    /// Decodes a collision file and builds its world. It runs checks 1 to 4 and 7 of
    /// contracts/collision-data-format.md#check-order in order (magic, version, header length, file length,
    /// checksum), then parses the header, heights, directory, vertices and indices through bounds-checked
    /// slices, and builds the world in file order. It throws at the first failure and never returns a
    /// partial world.
    /// </summary>
    /// <remarks>
    /// The count limits, the length recomputed from the counts, the values and the grid limits (checks 5, 6,
    /// 8 and 9) are added by Phase 8 (T235), between the frame checks and the parse. Until then a block that
    /// runs past the content is refused as <see cref="FormatErrorCode.Inconsistent"/> at <c>fileLength</c>.
    /// </remarks>
    /// <exception cref="CoreFormatException">The file is refused.</exception>
    public static CollisionWorld Decode(ReadOnlySpan<byte> bytes)
    {
        var fileLength = CheckFrame(bytes);
        var content = bytes[..(int)(fileLength - TrailerLength)];
        var checksum = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(int)(fileLength - TrailerLength)..]);
        var data = Parse(content, fileLength);
        return CollisionWorld.Create(data, checksum);
    }

    /// <summary>
    /// Writes <paramref name="data"/> as a collision file: the header, with <c>fileLength</c> computed, the
    /// heights, the directory, the mesh bodies and the trailer. It then decodes its own output, so it throws
    /// exactly what <see cref="Decode"/> would, and a generator cannot write a file the decoder refuses.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The data cannot be written: the heights are not <c>Columns·Rows</c>, a mesh's vertices are not x, y, z
    /// triples or its indices not triangles, or the file would not fit in an array.
    /// </exception>
    /// <exception cref="CoreFormatException">The decoder refuses the written file.</exception>
    public static byte[] Encode(CollisionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Heights);
        ArgumentNullException.ThrowIfNull(data.Meshes);
        ArgumentOutOfRangeException.ThrowIfNegative(data.Columns);
        ArgumentOutOfRangeException.ThrowIfNegative(data.Rows);
        if ((long)data.Columns * data.Rows != data.Heights.Length)
        {
            throw new ArgumentException("The heights must hold exactly Columns × Rows samples.", nameof(data));
        }

        long length = HeaderLength + (4L * data.Heights.Length) + (DirectoryEntryLength * (long)data.Meshes.Length) + TrailerLength;
        foreach (var mesh in data.Meshes)
        {
            if (mesh is null || mesh.Vertices is null || mesh.Indices is null)
            {
                throw new ArgumentException("Every mesh has vertices and indices.", nameof(data));
            }

            if (mesh.Vertices.Length % 3 != 0 || mesh.Indices.Length % 3 != 0)
            {
                throw new ArgumentException("A mesh's vertices are x, y, z triples and its indices are triangles.", nameof(data));
            }

            length += (4L * mesh.Vertices.Length) + (4L * mesh.Indices.Length);
        }

        if (length > Array.MaxLength)
        {
            throw new ArgumentException("The collision file would not fit in an array.", nameof(data));
        }

        var file = new byte[length];
        var span = file.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[VersionOffset..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[HeaderLengthOffset..], HeaderLength);
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

        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], XxHash64.HashToUInt64(span[..offset], 0));
        Decode(file);
        return file;
    }

    /// <summary>
    /// Checks 1 to 4 and 7 of #check-order: the magic, the version (before any length, so another version is
    /// refused as such whatever its length), the header length, the actual length against <c>fileLength</c>,
    /// and the trailer. Returns <c>fileLength</c>, which then equals the input's length.
    /// </summary>
    private static long CheckFrame(ReadOnlySpan<byte> bytes)
    {
        var length = (ulong)bytes.Length;

        // 1: the magic.
        if (bytes.Length < MagicLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "magic", FormatDetail.ExpectedLength, MagicLength, length);
        }

        if (!bytes[..MagicLength].SequenceEqual(Magic))
        {
            throw new CoreFormatException(new FormatError(Format, FormatErrorCode.UnknownMagic, 0, "magic", FrameText.WrongMagic(bytes[..MagicLength], Magic, "MMCD")));
        }

        // 2: the version.
        if (bytes.Length < HeaderLengthOffset)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "version", FormatDetail.ExpectedLength, HeaderLengthOffset, length);
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[VersionOffset..]);
        if (version != FormatVersion)
        {
            throw Error(FormatErrorCode.UnsupportedVersion, VersionOffset, "version", FormatDetail.UnsupportedVersion, version, FormatVersion);
        }

        // 3: the header length, then the whole header.
        if (bytes.Length < FileLengthOffset)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "headerLength", FormatDetail.ExpectedLength, FileLengthOffset, length);
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[HeaderLengthOffset..]);
        if (headerLength != HeaderLength)
        {
            throw new CoreFormatException(new FormatError(Format, FormatErrorCode.Inconsistent, HeaderLengthOffset, "headerLength", FrameText.WrongHeaderLength(headerLength, HeaderLength)));
        }

        if (bytes.Length < HeaderLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "header", FormatDetail.ExpectedLength, HeaderLength, length);
        }

        // 4: the actual length against fileLength.
        var fileLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[FileLengthOffset..]);
        if (length < fileLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "fileLength", FormatDetail.DeclaredLength, fileLength, length);
        }

        if (length > fileLength)
        {
            throw Error(FormatErrorCode.Oversized, (long)fileLength, "fileLength", FormatDetail.DeclaredLength, fileLength, length);
        }

        // 7: the trailer. fileLength equals the length, which is at least the 48-byte header.
        var trailerOffset = bytes.Length - TrailerLength;
        var trailer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[trailerOffset..]);
        var content = XxHash64.HashToUInt64(bytes[..trailerOffset], 0);
        if (trailer != content)
        {
            throw Error(FormatErrorCode.ChecksumMismatch, trailerOffset, "checksum", FormatDetail.ChecksumMismatch, trailer, content);
        }

        return bytes.Length;
    }

    /// <summary>
    /// Reads the header values, the heights, the directory and every mesh body from the content (the file
    /// without its trailer) into new arrays. Every block is sliced only after its length is known to fit.
    /// </summary>
    private static CollisionData Parse(ReadOnlySpan<byte> content, long fileLength)
    {
        var originX = BinaryPrimitives.ReadSingleLittleEndian(Slice(content, OriginXOffset, 4, fileLength));
        var originZ = BinaryPrimitives.ReadSingleLittleEndian(Slice(content, OriginZOffset, 4, fileLength));
        var cellSize = BinaryPrimitives.ReadSingleLittleEndian(Slice(content, CellSizeOffset, 4, fileLength));
        var gridCellSize = BinaryPrimitives.ReadSingleLittleEndian(Slice(content, GridCellSizeOffset, 4, fileLength));
        var columns = BinaryPrimitives.ReadUInt32LittleEndian(Slice(content, ColumnsOffset, 4, fileLength));
        var rows = BinaryPrimitives.ReadUInt32LittleEndian(Slice(content, RowsOffset, 4, fileLength));
        var meshCount = BinaryPrimitives.ReadUInt32LittleEndian(Slice(content, MeshCountOffset, 4, fileLength));

        var sampleCount = (ulong)columns * rows;
        var heightBytes = Slice(content, HeaderLength, sampleCount, 4, fileLength);
        var heights = new float[heightBytes.Length / 4];
        for (var n = 0; n < heights.Length; n++)
        {
            heights[n] = BinaryPrimitives.ReadSingleLittleEndian(heightBytes[(4 * n)..]);
        }

        var directoryOffset = (ulong)HeaderLength + (ulong)heightBytes.Length;
        var directory = Slice(content, directoryOffset, meshCount, DirectoryEntryLength, fileLength);
        var meshes = new MeshData[meshCount];
        var body = directoryOffset + (ulong)directory.Length;
        for (var m = 0; m < meshes.Length; m++)
        {
            var entry = directory[(DirectoryEntryLength * m)..];
            var meshId = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);

            var vertexBytes = Slice(content, body, vertexCount, VertexLength, fileLength);
            var vertices = new float[vertexBytes.Length / 4];
            for (var v = 0; v < vertices.Length; v++)
            {
                vertices[v] = BinaryPrimitives.ReadSingleLittleEndian(vertexBytes[(4 * v)..]);
            }

            body += (ulong)vertexBytes.Length;
            var indexBytes = Slice(content, body, triangleCount, TriangleLength, fileLength);
            var indices = new uint[indexBytes.Length / 4];
            for (var k = 0; k < indices.Length; k++)
            {
                indices[k] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes[(4 * k)..]);
            }

            body += (ulong)indexBytes.Length;
            meshes[m] = new MeshData(meshId, vertices, indices);
        }

        return new CollisionData(originX, originZ, cellSize, gridCellSize, checked((int)columns), checked((int)rows), heights, meshes);
    }

    /// <summary><paramref name="count"/> elements of <paramref name="size"/> bytes from <paramref name="offset"/>.</summary>
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> content, ulong offset, ulong count, int size, long fileLength)
    {
        var available = offset <= (ulong)content.Length ? (ulong)content.Length - offset : 0UL;
        if (offset > (ulong)content.Length || count > available / (ulong)size)
        {
            // Saturating: what the counts read so far need, the trailer included.
            var need = count > (ulong.MaxValue - offset - TrailerLength) / (ulong)size
                ? ulong.MaxValue
                : offset + (count * (ulong)size) + TrailerLength;
            throw Error(FormatErrorCode.Inconsistent, FileLengthOffset, "fileLength", FormatDetail.CountsNeed, need, (ulong)fileLength);
        }

        return content.Slice((int)offset, (int)(count * (ulong)size));
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> content, int offset, int length, long fileLength) =>
        Slice(content, (ulong)offset, (ulong)length, 1, fileLength);

    private static CoreFormatException Error(FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b = 0) =>
        new(new FormatError(Format, code, offset, field, detail, a, b));
}

/// <summary>The preformatted details the file frames (<c>MMCD</c>, <c>MMRS</c>) share.</summary>
internal static class FrameText
{
    /// <summary><c>the magic is 4d4d4358, not 4d4d4344 (MMCD)</c>.</summary>
    public static string WrongMagic(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, string name)
    {
        var found = BinaryPrimitives.ReadUInt32BigEndian(actual);
        var wanted = BinaryPrimitives.ReadUInt32BigEndian(expected);
        return string.Create(CultureInfo.InvariantCulture, $"the magic is {found:x8}, not {wanted:x8} ({name})");
    }

    /// <summary><c>headerLength is 40; version 1 has 48</c>.</summary>
    public static string WrongHeaderLength(ushort actual, int expected) =>
        string.Create(CultureInfo.InvariantCulture, $"headerLength is {actual}; version 1 has {expected}");
}
