using System.Text;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Configuration inputs for the bounded-read and unreadable-file rows of <c>SimConfigJsonTests</c> and
/// <c>ConfigCommandTests</c> (contracts/simcore-cli.md#config-check): <c>simcore/config/default.json</c>,
/// read-only through <see cref="RepoPaths"/>, padded to an exact length, a file in a test's own temp
/// directory that grows after its length is checked, and a copy there that another handle holds so it
/// cannot be read.
/// </summary>
public static class ConfigFiles
{
    /// <summary>
    /// <c>simcore/config/default.json</c> as UTF-8 bytes, padded with trailing spaces to exactly
    /// <paramref name="length"/> bytes, which is still the same valid configuration.
    /// </summary>
    public static byte[] DefaultPaddedTo(long length)
    {
        var json = Encoding.UTF8.GetBytes(RepoPaths.ReadAllText("simcore", "config", "default.json"));
        Assert.True(json.Length <= length, $"default.json is {json.Length} bytes, longer than the {length} asked for");
        var bytes = new byte[length];
        json.CopyTo(bytes, 0);
        bytes.AsSpan(json.Length).Fill((byte)' ');
        return bytes;
    }

    /// <summary>
    /// Pads the file at <paramref name="path"/> with trailing spaces to <paramref name="length"/> bytes, as a
    /// file that grows after its length is checked and before it is read, then opens it read-only.
    /// </summary>
    public static Stream GrowThenOpen(string path, long length)
    {
        using (var grow = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var padding = new byte[length - grow.Length];
            padding.AsSpan().Fill((byte)' ');
            grow.Write(padding);
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Writes a copy of <c>simcore/config/default.json</c> to <paramref name="path"/>, in a test's own temp
    /// directory, and returns a handle that holds it open sharing nothing (<see cref="FileShare.None"/>), so
    /// the file exists and is short enough but cannot be read until the handle is disposed. On Unix that rests
    /// on .NET's advisory <c>flock</c> emulation of the share mode, which <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>
    /// or the <c>System.IO.DisableFileLocking</c> switch turns off. So before the handle is returned, the lock
    /// is checked with the open the commands make (read-only, shared for reading): when that open does not
    /// fail with an <see cref="IOException"/>, the handle is disposed and the test fails naming the cause,
    /// rather than looking like a command that read a file it should refuse.
    /// </summary>
    public static FileStream WriteHeldCopy(string path)
    {
        File.WriteAllText(path, RepoPaths.ReadAllText("simcore", "config", "default.json"));
        var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var blocked = Record.Exception(() =>
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            probe.ReadByte();
        });
        if (blocked is not IOException)
        {
            hold.Dispose();
            Assert.Fail(
                $"the FileShare.None handle did not block a read-only open of {path} ({blocked?.GetType().Name ?? "the open succeeded"}): " +
                ".NET file locking is off (DOTNET_SYSTEM_IO_DISABLEFILELOCKING or System.IO.DisableFileLocking) or the temp file system does not support flock");
        }

        return hold;
    }
}
