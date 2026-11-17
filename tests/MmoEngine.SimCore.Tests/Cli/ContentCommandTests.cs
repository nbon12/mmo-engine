using System.Globalization;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#content-generate: <c>simcore content generate --out &lt;file&gt;</c> writes the
/// deterministic test world as <c>MMCD</c> v1 and prints <c>&lt;path&gt;: &lt;bytes&gt; bytes, checksum
/// &lt;x16&gt;</c>. Its output equals the committed <c>simcore/content/test-world.mmcd</c> byte for byte, and
/// when the file cannot be written it exits 2 and writes nothing. Each test writes only into its own
/// <see cref="TempDirectories"/> directory; the committed file is read through <see cref="RepoPaths"/>,
/// read-only.
/// </summary>
public sealed class ContentCommandTests
{
    /// <summary>
    /// Given a unique temp directory, When <c>simcore content generate --out &lt;tmp&gt;/test-world.mmcd</c>
    /// runs, Then it exits 0, prints exactly <c>&lt;tmp&gt;/test-world.mmcd: &lt;n&gt; bytes, checksum
    /// &lt;x16&gt;</c>, where n is the committed <c>simcore/content/test-world.mmcd</c>'s length and x16 its
    /// trailer, and the written bytes equal the committed file's.
    /// </summary>
    [Fact]
    public void ContentGenerateReproducesTheCommittedWorld()
    {
        var committed = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var checksum = StreamBytes.Trailer(committed).ToString("x16", CultureInfo.InvariantCulture);
        using var directory = TempDirectories.Create("content-generate");
        var output = directory.Combine("test-world.mmcd");

        var (exit, stdout, stderr) = SimcoreCli.Run("content", "generate", "--out", output);

        Assert.True(exit == 0, $"simcore content generate --out {output} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{output}: {committed.Length} bytes, checksum {checksum}\n"),
            stdout);
        Assert.True(File.Exists(output), $"{output} was not written");
        var written = File.ReadAllBytes(output);
        Assert.True(
            written.AsSpan().SequenceEqual(committed),
            $"the generated world ({written.Length} bytes) differs from the committed simcore/content/test-world.mmcd ({committed.Length} bytes, checksum {checksum})");
    }

    /// <summary>
    /// Given an <c>--out</c> path whose parent is a regular file, When <c>simcore content generate</c>
    /// runs, Then it exits 2 with the reason on stderr, prints no result line, and writes nothing: the temp
    /// directory still holds only that file, with its bytes unchanged.
    /// </summary>
    [Fact]
    public void ContentGenerateExitsTwoWhenTheOutputCannotBeWritten()
    {
        using var directory = TempDirectories.Create("content-generate-unwritable");
        var blocker = directory.Combine("not-a-directory");
        var blockerBytes = "a regular file, not a directory\n"u8.ToArray();
        File.WriteAllBytes(blocker, blockerBytes);
        var output = Path.Combine(blocker, "test-world.mmcd");

        var (exit, stdout, stderr) = SimcoreCli.Run("content", "generate", "--out", output);

        Assert.True(exit == 2, $"simcore content generate --out {output} exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(string.Empty, stdout);
        Assert.False(string.IsNullOrWhiteSpace(stderr), "the refusal gave no reason on stderr");
        var entries = Directory.GetFileSystemEntries(directory.Path);
        Assert.True(
            entries.Length == 1 && entries[0] == blocker,
            $"the temp directory should hold only {blocker}, but holds: {string.Join(", ", entries)}");
        Assert.Equal(blockerBytes, File.ReadAllBytes(blocker));
    }

    /// <summary>
    /// contracts/simcore-cli.md#settings-output-and-exit-codes: Given <c>--out</c> given as
    /// <paramref name="value"/> (empty, or whitespace only), When <c>simcore content generate</c> runs, Then
    /// it exits 2, a usage error refused when the command line is parsed: stderr has
    /// <c>--out: the path must not be empty or whitespace</c> and no exception, and stdout has no result line.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ContentGenerateRefusesAnEmptyOrWhitespaceOutPath(string value)
    {
        var (exit, stdout, stderr) = SimcoreCli.Run("content", "generate", "--out", value);

        Assert.True(exit == 2, $"simcore content generate --out \"{value}\" exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("--out: the path must not be empty or whitespace", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);
    }
}
