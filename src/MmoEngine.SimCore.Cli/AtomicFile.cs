using System.Text;

namespace MmoEngine.SimCore.Cli;

/// <summary>
/// Writes files atomically (specs/015-shared-simulation-core/contracts/simcore-cli.md#settings-output-and-exit-codes):
/// the content goes to <c>&lt;name&gt;.&lt;guid&gt;.tmp</c> in the target's directory, is flushed, and
/// the temporary file is then renamed over the target, so an interrupted run never leaves a half-written
/// file. On any exception the temporary file is deleted and the exception rethrown, and the target keeps
/// what it held before.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Creates or replaces <paramref name="path"/> with exactly <paramref name="bytes"/>.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(path, stream => stream.Write(bytes));
    }

    /// <summary>Creates or replaces <paramref name="path"/> with <paramref name="text"/> as UTF-8, without a byte order mark.</summary>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// Creates or replaces <paramref name="path"/> with what <paramref name="write"/> writes to the
    /// stream it is given. The stream is a new temporary file beside the target.
    /// </summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(write);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException($"'{path}' names no file in a directory.", nameof(path));
        var temp = Path.Combine(directory, $"{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    /// <summary>Deletes the temporary file if it exists; a failure to delete must not hide the write's exception.</summary>
    private static void DeleteQuietly(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original exception is the one the caller needs; a leftover .tmp file is harmless.
        }
    }
}
