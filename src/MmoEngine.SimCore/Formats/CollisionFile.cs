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
    private const int ReservedOffset = 44;

    /// <summary>The fewest and the most samples along X or Z.</summary>
    private const uint MinSamples = 2;
    private const uint MaxSamples = 4097;

    /// <summary>The most meshes; the vertex and triangle count ranges per mesh; the most vertices or triangles in all.</summary>
    private const uint MaxMeshCount = 4096;
    private const uint MinVertexCount = 3;
    private const uint MaxVertexCount = 1u << 20;
    private const uint MinTriangleCount = 1;
    private const uint MaxTriangleCount = 1u << 21;
    private const ulong MaxTotalCount = 1UL << 22;

    /// <summary>Every origin, height, vertex coordinate and heightfield end is within ±8192 m.</summary>
    private const float MaxCoordinate = 8192f;
    private const float MinCellSize = 0.25f;
    private const float MaxCellSize = 64f;
    private const float MinGridCellSize = 1f;
    private const float MaxGridCellSize = 64f;

    /// <summary>The vertex coordinate names, x, y and z, for error fields.</summary>
    private const string Axes = "xyz";

    /// <summary>The file's first four bytes, ASCII <c>MMCD</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "MMCD"u8;

    /// <summary>
    /// Decodes a collision file and builds its world. It runs the checks of
    /// contracts/collision-data-format.md#check-order in order and throws at the first failure: 1 to 4, the
    /// frame (magic, version before any length, header length, file length); 5, the count limits
    /// (<c>fileLength</c>, <c>columns</c>, <c>rows</c>, <c>meshCount</c>); 6, the heights and directory fit,
    /// each directory entry's counts and the running sums, and the length recomputed from the counts equals
    /// <c>fileLength</c>, in checked <c>u64</c>; 7, the checksum; 8, the values in the contract's order; 9,
    /// the world build's grid limits. Checks 1 to 6 read only the header and the directory and allocate
    /// nothing sized from a count, so a hostile header cannot force a large allocation or make the decoder
    /// hash past the end. No world is constructed before every check passes, so it never returns a partial
    /// world.
    /// </summary>
    /// <exception cref="CoreFormatException">The file is refused.</exception>
    public static CollisionWorld Decode(ReadOnlySpan<byte> bytes)
    {
        var layout = CheckFrame(bytes);
        var trailerOffset = layout.FileLength - TrailerLength;
        var content = bytes[..trailerOffset];
        var checksum = BinaryPrimitives.ReadUInt64LittleEndian(bytes[trailerOffset..]);
        CheckValues(content, layout);
        var data = Parse(content, layout);
        var world = CollisionWorld.TryCreate(data, checksum, out var overflow);
        return world ?? throw GridError(data, overflow);
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
    /// Checks 1 to 7 of #check-order: the magic, the version (before any length, so another version is
    /// refused as such whatever its length), the header length, the actual length against <c>fileLength</c>,
    /// the count limits, the counts against <c>fileLength</c>, and the trailer. Returns the counts, which then
    /// fill the file exactly. Checks 1 to 6 read only the header and the directory, so a file whose counts
    /// disagree with its length is refused before it is hashed.
    /// </summary>
    private static Layout CheckFrame(ReadOnlySpan<byte> bytes)
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

        // 5: the count limits, from the header only.
        if (fileLength > MaxFileLength)
        {
            throw Above(FileLengthOffset, "fileLength", fileLength, MaxFileLength);
        }

        var columns = CheckSamples(bytes, ColumnsOffset, "columns");
        var rows = CheckSamples(bytes, RowsOffset, "rows");
        var meshCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[MeshCountOffset..]);
        if (meshCount > MaxMeshCount)
        {
            throw Above(MeshCountOffset, "meshCount", meshCount, MaxMeshCount);
        }

        // 6: the heights and the directory fit, then each entry's counts and the running sums, then the
        // length recomputed from the counts, all in checked u64.
        var directoryOffset = checked(HeaderLength + (4UL * columns * rows));
        var bodyOffset = checked(directoryOffset + (DirectoryEntryLength * (ulong)meshCount));
        var directoryNeed = checked(bodyOffset + TrailerLength);
        if (directoryNeed > fileLength)
        {
            throw Error(FormatErrorCode.Inconsistent, FileLengthOffset, "fileLength", FormatDetail.CountsNeed, directoryNeed, fileLength);
        }

        var need = bodyOffset;
        var vertexTotal = 0UL;
        var triangleTotal = 0UL;
        for (var m = 0; m < (int)meshCount; m++)
        {
            var entryOffset = (int)directoryOffset + (DirectoryEntryLength * m);
            var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 4)..]);
            var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(entryOffset + 8)..]);
            CheckCount(entryOffset + 4, m, "vertexCount", vertexCount, MinVertexCount, MaxVertexCount);
            CheckCount(entryOffset + 8, m, "triangleCount", triangleCount, MinTriangleCount, MaxTriangleCount);

            vertexTotal = checked(vertexTotal + vertexCount);
            if (vertexTotal > MaxTotalCount)
            {
                throw TotalError(entryOffset + 4, m, "vertexCount", "vertex", vertexTotal);
            }

            triangleTotal = checked(triangleTotal + triangleCount);
            if (triangleTotal > MaxTotalCount)
            {
                throw TotalError(entryOffset + 8, m, "triangleCount", "triangle", triangleTotal);
            }

            need = checked(need + (VertexLength * (ulong)vertexCount) + (TriangleLength * (ulong)triangleCount));
        }

        need = checked(need + TrailerLength);
        if (need != fileLength)
        {
            throw Error(FormatErrorCode.Inconsistent, FileLengthOffset, "fileLength", FormatDetail.CountsNeed, need, fileLength);
        }

        // 7: the trailer. fileLength equals the length, which is at least the 48-byte header.
        var trailerOffset = bytes.Length - TrailerLength;
        var trailer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[trailerOffset..]);
        var content = XxHash64.HashToUInt64(bytes[..trailerOffset], 0);
        if (trailer != content)
        {
            throw Error(FormatErrorCode.ChecksumMismatch, trailerOffset, "checksum", FormatDetail.ChecksumMismatch, trailer, content);
        }

        return new Layout((int)columns, (int)rows, (int)meshCount, (int)directoryOffset, bytes.Length);
    }

    /// <summary>Check 5 for <c>columns</c> or <c>rows</c>: in [2, 4097]; below is <c>OutOfRange</c>, above <c>Oversized</c>.</summary>
    private static uint CheckSamples(ReadOnlySpan<byte> bytes, int offset, string field)
    {
        var samples = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (samples < MinSamples)
        {
            throw Below(offset, field, samples, MinSamples);
        }

        if (samples > MaxSamples)
        {
            throw Above(offset, field, samples, MaxSamples);
        }

        return samples;
    }

    /// <summary>Check 6 for one directory entry's count: below its range is <c>OutOfRange</c>, above it <c>Oversized</c>.</summary>
    private static void CheckCount(int offset, int mesh, string name, uint count, uint min, uint max)
    {
        if (count < min)
        {
            throw Below(offset, MeshField(mesh, name), count, min);
        }

        if (count > max)
        {
            throw Above(offset, MeshField(mesh, name), count, max);
        }
    }

    /// <summary>
    /// Check 8, the values, in the contract's order rather than file order: <c>reserved</c>; the origin;
    /// <c>cellSize</c>; <c>gridCellSize</c>; the heightfield's extent in X then Z (reported at
    /// <c>columns</c> and <c>rows</c>); the grid cells per axis (reported at <c>gridCellSize</c>); the
    /// heights; the mesh IDs ascending; then each mesh in directory order, its vertices and then its indices.
    /// It reads the content in place and allocates only a refusal.
    /// </summary>
    private static void CheckValues(ReadOnlySpan<byte> content, Layout layout)
    {
        var reserved = BinaryPrimitives.ReadUInt32LittleEndian(content[ReservedOffset..]);
        if (reserved != 0)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                ReservedOffset,
                "reserved",
                string.Create(CultureInfo.InvariantCulture, $"{reserved} is not 0")));
        }

        var originX = CheckFloat(content, OriginXOffset, "originX", -MaxCoordinate, MaxCoordinate);
        var originZ = CheckFloat(content, OriginZOffset, "originZ", -MaxCoordinate, MaxCoordinate);
        var cellSize = CheckFloat(content, CellSizeOffset, "cellSize", MinCellSize, MaxCellSize);
        var gridCellSize = CheckFloat(content, GridCellSizeOffset, "gridCellSize", MinGridCellSize, MaxGridCellSize);

        // The playable area ends at origin + (float)(samples − 1)·cellSize, the expression every sample uses.
        var extentX = (float)(layout.Columns - 1) * cellSize;
        var extentZ = (float)(layout.Rows - 1) * cellSize;
        CheckExtent(originX + extentX, ColumnsOffset, "columns", 'x');
        CheckExtent(originZ + extentZ, RowsOffset, "rows", 'z');
        CheckGridCells(extentX, gridCellSize, 'X');
        CheckGridCells(extentZ, gridCellSize, 'Z');

        var sampleCount = layout.Columns * layout.Rows;
        for (var n = 0; n < sampleCount; n++)
        {
            var offset = HeaderLength + (4 * n);
            var height = BinaryPrimitives.ReadSingleLittleEndian(content[offset..]);
            if (!IsCoordinate(height))
            {
                throw ValueError(offset, string.Create(CultureInfo.InvariantCulture, $"heights[{n}]"), height, -MaxCoordinate, MaxCoordinate);
            }
        }

        for (var m = 1; m < layout.MeshCount; m++)
        {
            var entryOffset = layout.DirectoryOffset + (DirectoryEntryLength * m);
            var meshId = BinaryPrimitives.ReadUInt32LittleEndian(content[entryOffset..]);
            var previous = BinaryPrimitives.ReadUInt32LittleEndian(content[(entryOffset - DirectoryEntryLength)..]);
            if (meshId <= previous)
            {
                throw new CoreFormatException(new FormatError(
                    Format,
                    FormatErrorCode.Inconsistent,
                    entryOffset,
                    MeshField(m, "meshId"),
                    string.Create(CultureInfo.InvariantCulture, $"{meshId} is not above {previous}")));
            }
        }

        var body = layout.BodyOffset;
        for (var m = 0; m < layout.MeshCount; m++)
        {
            var entryOffset = layout.DirectoryOffset + (DirectoryEntryLength * m);
            var vertexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(content[(entryOffset + 4)..]);
            var triangleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(content[(entryOffset + 8)..]);
            for (var c = 0; c < 3 * vertexCount; c++)
            {
                var offset = body + (4 * c);
                var coordinate = BinaryPrimitives.ReadSingleLittleEndian(content[offset..]);
                if (!IsCoordinate(coordinate))
                {
                    var field = string.Create(CultureInfo.InvariantCulture, $"meshes[{m}].vertices[{c / 3}].{Axes[c % 3]}");
                    throw ValueError(offset, field, coordinate, -MaxCoordinate, MaxCoordinate);
                }
            }

            var indices = body + (VertexLength * vertexCount);
            for (var k = 0; k < 3 * triangleCount; k++)
            {
                var offset = indices + (4 * k);
                var index = BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
                if (index >= (uint)vertexCount)
                {
                    throw new CoreFormatException(new FormatError(
                        Format,
                        FormatErrorCode.OutOfRange,
                        offset,
                        string.Create(CultureInfo.InvariantCulture, $"meshes[{m}].indices[{k}]"),
                        string.Create(CultureInfo.InvariantCulture, $"vertex index {index} is not below vertexCount {vertexCount}")));
                }
            }

            body = indices + (TriangleLength * triangleCount);
        }
    }

    /// <summary>A header float that must be finite and within [<paramref name="min"/>, <paramref name="max"/>]: <c>NonFinite</c>, else <c>OutOfRange</c>.</summary>
    private static float CheckFloat(ReadOnlySpan<byte> content, int offset, string field, float min, float max)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(content[offset..]);
        if (!float.IsFinite(value) || value < min || value > max)
        {
            throw ValueError(offset, field, value, min, max);
        }

        return value;
    }

    /// <summary>The heightfield's end along one axis must be at most 8192 m; it is reported at that axis's sample count.</summary>
    private static void CheckExtent(float end, int offset, string field, char axis)
    {
        if (end > MaxCoordinate)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                offset,
                field,
                string.Create(CultureInfo.InvariantCulture, $"the heightfield ends at {axis} = {end}; the limit is {MaxCoordinate}")));
        }
    }

    /// <summary>⌈extent / gridCellSize⌉, the grid's cells along one axis, must be at most 4096; it is reported at <c>gridCellSize</c>.</summary>
    private static void CheckGridCells(float extent, float gridCellSize, char axis)
    {
        var cells = System.MathF.Ceiling(extent / gridCellSize);
        if (cells > UniformGrid.MaxCellsPerAxis)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                GridCellSizeOffset,
                "gridCellSize",
                string.Create(CultureInfo.InvariantCulture, $"{cells} grid cells along {axis}; the limit is {UniformGrid.MaxCellsPerAxis}")));
        }
    }

    /// <summary>True when <paramref name="value"/> is finite and within ±8192 m.</summary>
    private static bool IsCoordinate(float value) => float.IsFinite(value) && System.MathF.Abs(value) <= MaxCoordinate;

    /// <summary>A refused float: <c>NonFinite</c> (<c>NaN is not finite</c>), or <c>OutOfRange</c> (<c>9000 is outside [-8192, 8192]</c>).</summary>
    private static CoreFormatException ValueError(int offset, string field, float value, float min, float max)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        return float.IsFinite(value)
            ? Error(FormatErrorCode.OutOfRange, offset, field, FormatDetail.OutOfRangeFloat, bits, BitConverter.SingleToUInt32Bits(min), BitConverter.SingleToUInt32Bits(max))
            : Error(FormatErrorCode.NonFinite, offset, field, FormatDetail.NotFinite, bits);
    }

    /// <summary>
    /// Check 9's refusal, at the file offset of the first index of the triangle whose registration broke a
    /// grid limit: <c>grid.cells[cx,cz]</c> (<c>65 triangles; the limit is 64</c>) or
    /// <c>grid.registrations</c>.
    /// </summary>
    private static CoreFormatException GridError(CollisionData data, GridOverflow overflow)
    {
        var offset = TriangleOffset(data, overflow.TriangleIndex);
        var error = overflow.Registrations
            ? new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                offset,
                "grid.registrations",
                string.Create(CultureInfo.InvariantCulture, $"{overflow.Count} registrations; the limit is {UniformGrid.MaxRegistrations}"))
            : new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                offset,
                string.Create(CultureInfo.InvariantCulture, $"grid.cells[{overflow.CellX},{overflow.CellZ}]"),
                string.Create(CultureInfo.InvariantCulture, $"{overflow.Count} triangles; the limit is {CollisionWorld.MaxTrianglesPerCell}"));
        return new CoreFormatException(error);
    }

    /// <summary>The file offset of global mesh triangle <paramref name="index"/>'s first index, walked from the layout.</summary>
    private static long TriangleOffset(CollisionData data, int index)
    {
        var body = HeaderLength + (4L * data.Heights.Length) + (DirectoryEntryLength * (long)data.Meshes.Length);
        var remaining = index;
        foreach (var mesh in data.Meshes)
        {
            var triangles = mesh.Indices.Length / 3;
            var indices = body + (4L * mesh.Vertices.Length);
            if (remaining < triangles)
            {
                return indices + (TriangleLength * (long)remaining);
            }

            remaining -= triangles;
            body = indices + (4L * mesh.Indices.Length);
        }

        throw new ArgumentOutOfRangeException(nameof(index), index, "The triangle index is past the meshes.");
    }

    /// <summary>
    /// Reads the header values, the heights, the directory and every mesh body from the content (the file
    /// without its trailer) into new arrays. Checks 1 to 8 have passed, so every block lies where the counts
    /// put it and every value is in range.
    /// </summary>
    private static CollisionData Parse(ReadOnlySpan<byte> content, Layout layout)
    {
        var originX = BinaryPrimitives.ReadSingleLittleEndian(content[OriginXOffset..]);
        var originZ = BinaryPrimitives.ReadSingleLittleEndian(content[OriginZOffset..]);
        var cellSize = BinaryPrimitives.ReadSingleLittleEndian(content[CellSizeOffset..]);
        var gridCellSize = BinaryPrimitives.ReadSingleLittleEndian(content[GridCellSizeOffset..]);

        var heights = new float[layout.Columns * layout.Rows];
        for (var n = 0; n < heights.Length; n++)
        {
            heights[n] = BinaryPrimitives.ReadSingleLittleEndian(content[(HeaderLength + (4 * n))..]);
        }

        var meshes = new MeshData[layout.MeshCount];
        var body = layout.BodyOffset;
        for (var m = 0; m < meshes.Length; m++)
        {
            var entry = content[(layout.DirectoryOffset + (DirectoryEntryLength * m))..];
            var meshId = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var vertexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var triangleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);

            var vertices = new float[3 * vertexCount];
            for (var v = 0; v < vertices.Length; v++)
            {
                vertices[v] = BinaryPrimitives.ReadSingleLittleEndian(content[(body + (4 * v))..]);
            }

            body += VertexLength * vertexCount;
            var indices = new uint[3 * triangleCount];
            for (var k = 0; k < indices.Length; k++)
            {
                indices[k] = BinaryPrimitives.ReadUInt32LittleEndian(content[(body + (4 * k))..]);
            }

            body += TriangleLength * triangleCount;
            meshes[m] = new MeshData(meshId, vertices, indices);
        }

        return new CollisionData(originX, originZ, cellSize, gridCellSize, layout.Columns, layout.Rows, heights, meshes);
    }

    private static string MeshField(int mesh, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"meshes[{mesh}].{name}");

    /// <summary>A count below its range: <c>OutOfRange</c>, such as <c>1 is below 2</c>.</summary>
    private static CoreFormatException Below(int offset, string field, ulong value, ulong min) =>
        new(new FormatError(Format, FormatErrorCode.OutOfRange, offset, field, string.Create(CultureInfo.InvariantCulture, $"{value} is below {min}")));

    /// <summary>A count or length above its limit: <c>Oversized</c>, such as <c>4098 is above 4097</c>.</summary>
    private static CoreFormatException Above(int offset, string field, ulong value, ulong max) =>
        new(new FormatError(Format, FormatErrorCode.Oversized, offset, field, string.Create(CultureInfo.InvariantCulture, $"{value} is above {max}")));

    /// <summary>A running sum of directory counts past 2²²: <c>Oversized</c> at the entry that took it there.</summary>
    private static CoreFormatException TotalError(int offset, int mesh, string name, string what, ulong total) =>
        new(new FormatError(
            Format,
            FormatErrorCode.Oversized,
            offset,
            MeshField(mesh, name),
            string.Create(CultureInfo.InvariantCulture, $"the {what} counts reach {total}; the limit is {MaxTotalCount}")));

    private static CoreFormatException Error(FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b = 0, ulong c = 0) =>
        new(new FormatError(Format, code, offset, field, detail, a, b, c));

    /// <summary>
    /// The counts checks 5 and 6 have bounded: the samples along X and Z, the meshes, the directory's offset
    /// D = 48 + 4·columns·rows, and the file's length, which the counts then fill exactly.
    /// </summary>
    private readonly record struct Layout(int Columns, int Rows, int MeshCount, int DirectoryOffset, int FileLength)
    {
        /// <summary>The first mesh body, after the directory: D + 12M.</summary>
        public int BodyOffset => DirectoryOffset + (DirectoryEntryLength * MeshCount);
    }
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
