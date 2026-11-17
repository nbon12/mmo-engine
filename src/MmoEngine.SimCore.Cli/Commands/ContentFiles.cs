using System.Globalization;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// The CLI's one loader of collision content (specs/015-shared-simulation-core/contracts/simcore-cli.md#content-files).
/// <c>streams record</c>, <c>streams verify</c> and <c>agreement prepare</c> load the <c>--content</c>
/// folder only through <see cref="LoadAll(string)"/>, before they run any stream: every <c>*.mmcd</c>, in
/// ordinal name order, each refused from its size when it is longer than
/// <see cref="CollisionFile.MaxFileLength"/> (without being opened) and otherwise read through a read-only
/// <see cref="FileStream"/> and decoded with <see cref="CollisionFile.Decode"/>.
/// </summary>
public static class ContentFiles
{
    /// <summary>The content files' extension.</summary>
    public const string Extension = ".mmcd";

    /// <summary>The <c>fileLength</c> offset in the collision header, where an oversized file is refused.</summary>
    private const long FileLengthOffset = 8;

    /// <summary>
    /// Every <c>*.mmcd</c> in <paramref name="contentDir"/>, decoded, in ordinal name order. An empty folder
    /// gives an empty list.
    /// </summary>
    /// <exception cref="ArgumentException">The folder name is null or empty.</exception>
    /// <exception cref="CoreFormatException">
    /// A file is refused: one longer than <see cref="CollisionFile.MaxFileLength"/> with
    /// <see cref="FormatErrorCode.Oversized"/> at offset 8, field <c>fileLength</c>, before it is opened; any
    /// other with <see cref="CollisionFile.Decode"/>'s error.
    /// </exception>
    /// <exception cref="IOException">The folder does not exist or a file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder or a file cannot be accessed.</exception>
    public static IReadOnlyList<CollisionWorld> LoadAll(string contentDir) => LoadAll(contentDir, opening: null);

    /// <summary>
    /// <see cref="LoadAll(string)"/>, telling <paramref name="opening"/> each file's name before that file
    /// is checked, so a caller knows the name of the file a refusal is about, and that the i-th name it was
    /// told is the i-th world's file.
    /// </summary>
    internal static IReadOnlyList<CollisionWorld> LoadAll(string contentDir, Action<string>? opening)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentDir);
        var paths = Directory.GetFiles(contentDir, "*" + Extension, SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(Extension, StringComparison.Ordinal))
            .ToArray();
        Array.Sort(paths, StringComparer.Ordinal);
        var worlds = new CollisionWorld[paths.Length];
        for (var i = 0; i < paths.Length; i++)
        {
            opening?.Invoke(Path.GetFileName(paths[i]));
            worlds[i] = Load(paths[i]);
        }

        return worlds;
    }

    /// <summary>
    /// Loads <paramref name="contentDir"/> for <paramref name="command"/>. On success it gives the worlds and
    /// their file names, index for index, and returns true. A refused file prints
    /// <c>&lt;command&gt;: content &lt;file name&gt;: &lt;message&gt;</c> on stderr, and a folder that does not
    /// exist or cannot be read prints <c>&lt;command&gt;: content &lt;folder&gt;: &lt;reason&gt;</c>; either
    /// returns false, and the caller runs no stream, writes nothing and exits 2.
    /// </summary>
    internal static bool TryLoadAll(
        string contentDir,
        string command,
        TextWriter stderr,
        out IReadOnlyList<string> names,
        out IReadOnlyList<CollisionWorld> worlds)
    {
        var opened = new List<string>();
        try
        {
            worlds = LoadAll(contentDir, opened.Add);
            names = opened;
            return true;
        }
        catch (CoreFormatException ex)
        {
            stderr.Write($"{command}: content {opened[^1]}: {ex.Message}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var subject = opened.Count > 0 ? opened[^1] : contentDir;
            stderr.Write($"{command}: content {subject}: {ex.Message}\n");
        }

        names = [];
        worlds = [];
        return false;
    }

    /// <summary>One file: refused from its length, or read through a read-only stream and decoded.</summary>
    private static CollisionWorld Load(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > CollisionFile.MaxFileLength)
        {
            throw Oversized(length);
        }

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // The file may have grown since its length was read; the open stream's length is the one read.
            if (stream.Length > CollisionFile.MaxFileLength)
            {
                throw Oversized(stream.Length);
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        return CollisionFile.Decode(bytes);
    }

    private static CoreFormatException Oversized(long length) => new(new FormatError(
        "collision",
        FormatErrorCode.Oversized,
        FileLengthOffset,
        "fileLength",
        string.Create(CultureInfo.InvariantCulture, $"the file is {length} bytes; the limit is {CollisionFile.MaxFileLength}")));
}
