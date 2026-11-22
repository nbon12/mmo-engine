using System.Text;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// The writer of the committed streams folder (FR-028, specs/015-shared-simulation-core/contracts/recorded-stream-format.md#regeneration,
/// contracts/simcore-cli.md#streams-record). Only <c>simcore streams record</c> calls it, which
/// <c>StreamWriterTests.OnlyTheRecordCommandWritesStreamFiles</c> checks: it writes a stream file
/// atomically, removes a stream whose scenario left the catalog, and appends one entry to the folder's
/// regeneration log in a single write.
/// </summary>
internal static class StreamFiles
{
    /// <summary>The stream files' extension.</summary>
    public const string Extension = ".mmrs";

    /// <summary>The regeneration log's file name inside a streams folder.</summary>
    public const string RegenerationLogName = "REGENERATIONS.md";

    /// <summary>What a new regeneration log starts with, before its first entry.</summary>
    private const string LogPreamble =
        "# Stream regenerations\n" +
        "\n" +
        "This file is appended by `simcore streams record`; each entry gives the reason and the old and new hashes.\n";

    /// <summary>The path of the regeneration log in <paramref name="outDir"/>.</summary>
    public static string RegenerationLogPath(string outDir) => Path.Combine(outDir, RegenerationLogName);

    /// <summary>The path of stream <paramref name="name"/> in <paramref name="outDir"/>.</summary>
    public static string StreamPath(string outDir, string name) => Path.Combine(outDir, name + Extension);

    /// <summary>
    /// Creates or replaces <c>&lt;outDir&gt;/&lt;name&gt;.mmrs</c> with <paramref name="bytes"/> through
    /// <see cref="AtomicFile"/>, creating <paramref name="outDir"/> when it does not exist.
    /// </summary>
    public static void WriteCommitted(string outDir, string name, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);
        Directory.CreateDirectory(outDir);
        AtomicFile.WriteAllBytes(StreamPath(outDir, name), bytes);
    }

    /// <summary>Deletes <c>&lt;outDir&gt;/&lt;name&gt;.mmrs</c>, a stream whose scenario is no longer in the catalog.</summary>
    public static void RemoveCommitted(string outDir, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentException.ThrowIfNullOrEmpty(name);
        File.Delete(StreamPath(outDir, name));
    }

    /// <summary>
    /// Appends <paramref name="entry"/> to <c>&lt;outDir&gt;/REGENERATIONS.md</c> in one write. When the log
    /// does not exist or is empty, that write starts it with the <c># Stream regenerations</c> heading and the
    /// sentence saying the file is appended by <c>simcore streams record</c>, then the entry, so the log always
    /// starts with its heading (contracts/simcore-cli.md#regenerationsmd). The entry starts on a line of its
    /// own, after a blank line, and ends with its own line feed, as every entry <c>streams record</c> builds
    /// does.
    /// </summary>
    public static void AppendRegenerations(string outDir, string entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(outDir);
        var path = RegenerationLogPath(outDir);
        var text = new StringBuilder();
        var last = LastByte(path);
        if (last is null)
        {
            text.Append(LogPreamble);
        }
        else if (last != '\n')
        {
            text.Append('\n');
        }

        text.Append('\n').Append(entry);

        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The file's last byte, or null when the file does not exist or is empty.</summary>
    private static int? LastByte(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return null;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte();
    }
}
