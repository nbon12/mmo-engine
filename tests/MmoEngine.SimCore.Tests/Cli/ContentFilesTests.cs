using MmoEngine.SimCore.Cli.Commands;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#content-files and contracts/collision-data-format.md#limits:
/// <c>ContentFiles.LoadAll(contentDir)</c> loads every <c>*.mmcd</c> in the folder in ordinal name order,
/// and refuses a file longer than <c>CollisionFile.MaxFileLength</c> (256 MiB) from its size, before it
/// opens it, with <c>Oversized</c> at offset 8, field <c>fileLength</c>. Each test works in its own
/// <see cref="TempDirectories"/> directory; the committed <c>test-world.mmcd</c> is read through
/// <see cref="RepoPaths"/>, read-only.
/// </summary>
public sealed class ContentFilesTests
{
    /// <summary>One byte over <c>CollisionFile.MaxFileLength</c>.</summary>
    private const long OversizedLength = 268_435_457;

    /// <summary>What <see cref="AnOversizedCollisionFileIsRefusedBeforeItIsRead"/> may allocate: far less than the file.</summary>
    private const long AllocationLimit = 1024 * 1024;

    /// <summary>The <c>tiny-world</c> fixture's checksum (contracts/collision-data-format.md#tiny-world).</summary>
    private const ulong TinyWorldChecksum = 0x9224e3fa3b3fca9cUL;

    /// <summary>
    /// Given a temp directory holding only a sparse <c>big.mmcd</c> of 268,435,457 zero bytes (so byte 0 is
    /// 0, not the <c>M</c> of <c>MMCD</c>), When <c>ContentFiles.LoadAll</c> loads the directory, Then it
    /// throws <see cref="CoreFormatException"/> whose error is format <c>collision</c>, <c>Oversized</c>,
    /// offset 8, field <c>fileLength</c>, reading
    /// <c>collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456</c> (not
    /// <c>UnknownMagic</c>, which would show <c>CollisionFile.Decode</c> saw the bytes), returns nothing,
    /// and the thread allocates less than 1 MiB across the call: the file was never read.
    /// </summary>
    [Fact]
    public void AnOversizedCollisionFileIsRefusedBeforeItIsRead()
    {
        using var directory = TempDirectories.Create("content-files-oversized");
        var big = directory.Combine("big.mmcd");
        using (var stream = new FileStream(big, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(OversizedLength);
        }

        using (var stream = new FileStream(big, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(stream.Length == OversizedLength, $"big.mmcd is {stream.Length} bytes, not {OversizedLength}");
            Assert.True(stream.ReadByte() == 0, "big.mmcd's byte 0 should be 0, not the M of MMCD");
        }

        IReadOnlyList<CollisionWorld>? worlds = null;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var thrown = Assert.Throws<CoreFormatException>(() => worlds = ContentFiles.LoadAll(directory.Path));

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Null(worlds);
        var error = thrown.Error;
        Assert.Equal("collision", error.Format);
        Assert.Equal(FormatErrorCode.Oversized, error.Code);
        Assert.Equal(8L, error.Offset);
        Assert.Equal("fileLength", error.Field);
        Assert.Equal(
            "collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456",
            error.ToString());
        Assert.True(
            allocated < AllocationLimit,
            $"refusing big.mmcd allocated {allocated} bytes on the thread; a refusal from the file's size allocates less than {AllocationLimit}");
    }

    /// <summary>
    /// Given a temp directory holding <c>b.mmcd</c>, a copy of the committed <c>test-world.mmcd</c>, and then
    /// <c>a.mmcd</c>, the <c>tiny-world</c> fixture encoded, When <c>ContentFiles.LoadAll</c> loads it, Then
    /// it returns two worlds in ordinal name order, whatever order the files were written or listed in:
    /// first <c>a.mmcd</c>'s, whose <c>Checksum</c> is the tiny world's <c>9224e3fa3b3fca9c</c>, then
    /// <c>b.mmcd</c>'s, whose <c>Checksum</c> equals the committed file's.
    /// </summary>
    [Fact]
    public void LoadAllDecodesEveryFileInOrdinalOrder()
    {
        using var directory = TempDirectories.Create("content-files-order");
        var committed = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var committedChecksum = CollisionFile.Decode(committed).Checksum;
        Assert.True(committedChecksum != TinyWorldChecksum, "the test world and the tiny world should differ");
        File.WriteAllBytes(directory.Combine("b.mmcd"), committed);
        File.WriteAllBytes(directory.Combine("a.mmcd"), CollisionFile.Encode(TestWorlds.TinyWorld()));

        var worlds = ContentFiles.LoadAll(directory.Path);

        Assert.True(worlds.Count == 2, $"LoadAll returned {worlds.Count} worlds for a.mmcd and b.mmcd");
        Assert.True(
            worlds[0].Checksum == TinyWorldChecksum,
            $"worlds[0] has checksum {worlds[0].Checksum:x16}; a.mmcd (the tiny world, {TinyWorldChecksum:x16}) comes first");
        Assert.True(
            worlds[1].Checksum == committedChecksum,
            $"worlds[1] has checksum {worlds[1].Checksum:x16}; b.mmcd (simcore/content/test-world.mmcd, {committedChecksum:x16}) comes second");
    }
}
