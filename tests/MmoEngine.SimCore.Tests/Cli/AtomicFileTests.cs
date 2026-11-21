using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#settings-output-and-exit-codes: every file the CLI creates or replaces is
/// written to a temporary file in the same directory and then renamed, so an interrupted run never leaves
/// a half-written file. <c>AtomicFile</c> writes <c>&lt;name&gt;.&lt;guid&gt;.tmp</c> beside the target,
/// renames it over the target, and deletes it if the write fails. Each test works in its own
/// <see cref="TempDirectories"/> folder.
/// </summary>
public sealed class AtomicFileTests
{
    private const string FileName = "report.bin";

    [Fact]
    public void WriteReplacesTheFileThroughATempFileInTheSameDirectory()
    {
        using var directory = TempDirectories.Create("atomic-file");
        var path = directory.Combine(FileName);
        byte[] first = [0x01, 0x02, 0x03];
        byte[] second = [0x0A, 0x0B, 0x0C, 0x0D, 0x0E];
        byte[] third = [0x7F];

        AtomicFile.WriteAllBytes(path, first);

        Assert.Equal(first, File.ReadAllBytes(path));
        Assert.Equal(FileName, Assert.Single(EntryNames(directory)));

        AtomicFile.WriteAllBytes(path, second);

        Assert.Equal(second, File.ReadAllBytes(path));
        Assert.Equal(FileName, Assert.Single(EntryNames(directory)));

        // While a write is in progress the new bytes go to <name>.<guid>.tmp in the same folder, and the
        // target still holds the previous bytes; the rename then replaces it.
        string[]? namesDuringWrite = null;
        byte[]? targetDuringWrite = null;
        AtomicFile.Write(path, stream =>
        {
            stream.Write(third);
            namesDuringWrite = EntryNames(directory);
            targetDuringWrite = File.ReadAllBytes(path);
        });

        Assert.NotNull(namesDuringWrite);
        Assert.Contains(FileName, namesDuringWrite);
        var temp = Assert.Single(namesDuringWrite, name => name != FileName);
        Assert.StartsWith(FileName + ".", temp, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", temp, StringComparison.Ordinal);
        Assert.NotNull(targetDuringWrite);
        Assert.Equal(second, targetDuringWrite);

        Assert.Equal(third, File.ReadAllBytes(path));
        Assert.Equal(FileName, Assert.Single(EntryNames(directory)));
    }

    [Fact]
    public void FailedWriteLeavesTheOriginalAndNoTempFile()
    {
        using var directory = TempDirectories.Create("atomic-file");
        var path = directory.Combine(FileName);
        byte[] original = [0x01, 0x02, 0x03];
        byte[] partial = [0xFF, 0xFF];
        File.WriteAllBytes(path, original);
        var injected = new IOException("injected write failure");

        // The callback writes part of the new content, then fails: the write is interrupted mid-file.
        var thrown = Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write(partial);
            throw injected;
        }));

        Assert.Same(injected, thrown);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(FileName, Assert.Single(EntryNames(directory)));
    }

    /// <summary>The names of every file and folder in <paramref name="directory"/>, in ordinal order.</summary>
    private static string[] EntryNames(TempDirectory directory)
    {
        var entries = Directory.GetFileSystemEntries(directory.Path);
        var names = new string[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            names[i] = Path.GetFileName(entries[i]);
        }

        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
