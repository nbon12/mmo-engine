using System.Text;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Absolute paths into the repository, found from the test binaries rather than the current
/// directory. Tests open everything under <c>simcore/</c> read-only through <see cref="OpenRead"/>,
/// <see cref="ReadAllBytes"/> and <see cref="ReadAllText"/>, never write under it (tests of the
/// writing commands work on <see cref="TempDirectories.CopyOf"/> copies), and never rely on the
/// current directory.
/// </summary>
public static class RepoPaths
{
    private const string SolutionFile = "MmoEngine.sln";

    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The first directory above <see cref="AppContext.BaseDirectory"/> that holds MmoEngine.sln.</summary>
    public static string Root => RootPath.Value;

    /// <summary>The committed content of the shared simulation core: <c>simcore/</c>.</summary>
    public static string SimCore => Combine("simcore");

    /// <summary>An absolute path under <see cref="Root"/>.</summary>
    public static string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var all = new string[parts.Length + 1];
        all[0] = Root;
        parts.CopyTo(all, 1);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(all)));
        if (path != Root && !path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{path} is not under the repository root {Root}.", nameof(parts));
        }

        return path;
    }

    /// <summary>Opens a repository file for reading only, sharing it with other readers.</summary>
    public static FileStream OpenRead(params string[] parts) =>
        new(Combine(parts), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Reads a repository file's bytes through <see cref="OpenRead"/>.</summary>
    public static byte[] ReadAllBytes(params string[] parts)
    {
        using var stream = OpenRead(parts);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Reads a repository file as UTF-8 text through <see cref="OpenRead"/>.</summary>
    public static string ReadAllText(params string[] parts)
    {
        using var reader = new StreamReader(OpenRead(parts), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string FindRoot()
    {
        var start = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        throw new DirectoryNotFoundException($"No directory holding {SolutionFile} above {start}.");
    }
}
