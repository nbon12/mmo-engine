using System.Text;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Configuration inputs for the bounded-read rows of <c>SimConfigJsonTests</c> and <c>ConfigCommandTests</c>
/// (contracts/simcore-cli.md#config-check): <c>simcore/config/default.json</c>, read-only through
/// <see cref="RepoPaths"/>, padded to an exact length, and a file in a test's own temp directory that grows
/// after its length is checked.
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
}
