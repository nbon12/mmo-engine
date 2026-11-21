namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Unique, empty temp directories under <c>mmo-simcore-tests</c>, one per call, so tests stay
/// parallel-safe and rerun-safe. Tests of the writing commands work on <see cref="CopyOf"/> copies of
/// <c>simcore/</c> folders, never on the repository itself.
/// </summary>
public static class TempDirectories
{
    /// <summary>A new empty directory <c>&lt;temp&gt;/mmo-simcore-tests/&lt;prefix&gt;-&lt;guid&gt;</c>.</summary>
    public static TempDirectory Create(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var path = Path.Combine(Path.GetTempPath(), "mmo-simcore-tests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    /// <summary>
    /// A new directory holding a recursive copy of <paramref name="sourceDirectory"/>, copied in ordinal
    /// order. Each source file is opened for reading only.
    /// </summary>
    public static TempDirectory CopyOf(string sourceDirectory, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        var target = Create(prefix);
        try
        {
            CopyContents(sourceDirectory, target.Path);
        }
        catch
        {
            target.Dispose();
            throw;
        }

        return target;
    }

    private static void CopyContents(string source, string target)
    {
        var files = Directory.GetFiles(source);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        var directories = Directory.GetDirectories(source);
        Array.Sort(directories, StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            var copy = Path.Combine(target, Path.GetFileName(directory));
            Directory.CreateDirectory(copy);
            CopyContents(directory, copy);
        }
    }
}

/// <summary>A temp directory that deletes itself, best effort, when disposed.</summary>
public sealed class TempDirectory : IDisposable
{
    internal TempDirectory(string path) => Path = path;

    /// <summary>The directory's absolute path.</summary>
    public string Path { get; }

    /// <summary>A path inside this directory.</summary>
    public string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var all = new string[parts.Length + 1];
        all[0] = Path;
        parts.CopyTo(all, 1);
        return System.IO.Path.Combine(all);
    }

    /// <summary>Deletes the directory and everything in it; a file still held open leaves it behind.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the directory is unique to this test, so a leftover never affects another run.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
