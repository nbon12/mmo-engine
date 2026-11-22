using System.Globalization;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>
/// The agreement work directory (specs/015-shared-simulation-core/contracts/simcore-cli.md#agreement-work-directory):
/// its layout, as the manifest-relative paths <c>agreement prepare</c> writes and <c>agreement compare</c>
/// reads, and its writer. Every file is written through <see cref="AtomicFile"/>, under the work directory
/// only: a relative path that is rooted or climbs out with <c>..</c> is refused. This and the record
/// handler of <c>streams record</c> are the only code that encodes and writes stream files
/// (contracts/recorded-stream-format.md#regeneration), and the committed streams are never written here.
/// </summary>
internal sealed class WorkDirectory
{
    /// <summary>The manifest, written last, so a work directory without one is incomplete.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The cross-build table inputs (<c>MMXT</c>).</summary>
    public const string TablesInputs = "tables/inputs.bin";

    /// <summary>The config block the pose rows use.</summary>
    public const string TablesConfig = "tables/config.bin";

    /// <summary>The server build's table results (<c>MMXR</c>).</summary>
    public const string ServerTables = BuildNames.Server + "/" + TablesFileName;

    /// <summary>The batch inputs of a batchable stream: its config block, initial state and commands.</summary>
    public const string BatchConfigFile = "config.bin";

    /// <inheritdoc cref="BatchConfigFile"/>
    public const string BatchInitialStateFile = "initial-state.bin";

    /// <inheritdoc cref="BatchConfigFile"/>
    public const string BatchCommandsFile = "commands.bin";

    private const string StreamExtension = ".mmrs";

    private const string RecordsExtension = ".records";

    private const string ErrorExtension = ".error.txt";

    private const string TablesFileName = "tables.bin";

    private const string TablesErrorFileName = "tables.error.txt";

    /// <summary>A work directory rooted at <paramref name="root"/>, the <c>--work</c> argument.</summary>
    /// <exception cref="ArgumentException">The root is null or empty.</exception>
    public WorkDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The work directory's absolute path.</summary>
    public string Root { get; }

    /// <summary><c>streams/&lt;stream&gt;.mmrs</c>, a committed or generated stream.</summary>
    public static string StreamFile(string stream) => "streams/" + stream + StreamExtension;

    /// <summary><c>refusals/&lt;name&gt;.mmrs</c>, a stream every build must refuse.</summary>
    public static string RefusalFile(string name) => "refusals/" + name + StreamExtension;

    /// <summary><c>collision/&lt;checksum as x16&gt;.mmcd</c>.</summary>
    public static string CollisionPath(ulong checksum) =>
        string.Create(CultureInfo.InvariantCulture, $"collision/{checksum:x16}.mmcd");

    /// <summary><c>&lt;build&gt;/&lt;stream&gt;.records</c>, a build's step records; <c>server/…</c> for the server's.</summary>
    public static string RecordsFile(string build, string stream) => build + "/" + stream + RecordsExtension;

    /// <summary><c>&lt;build&gt;/&lt;stream&gt;.error.txt</c>, written instead of records when the build threw.</summary>
    public static string ErrorFile(string build, string stream) => build + "/" + stream + ErrorExtension;

    /// <summary><c>&lt;build&gt;/tables.bin</c>, a build's table results.</summary>
    public static string TablesFile(string build) => build + "/" + TablesFileName;

    /// <summary><c>&lt;build&gt;/tables.error.txt</c>, written instead of table results when the build threw.</summary>
    public static string TablesErrorFile(string build) => build + "/" + TablesErrorFileName;

    /// <summary><c>batch/&lt;stream&gt;/&lt;file&gt;</c>, one of a batchable stream's batch inputs.</summary>
    public static string BatchFile(string stream, string file) => "batch/" + stream + "/" + file;

    /// <summary>
    /// The absolute path of <paramref name="relative"/>, a manifest-relative path with <c>/</c> separators.
    /// </summary>
    /// <exception cref="InvalidDataException">The path is empty, rooted, or has an empty, <c>.</c> or <c>..</c> segment, so it would leave the work directory.</exception>
    public string FullPath(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var segments = relative.Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains('\\', StringComparison.Ordinal) || segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException($"{relative} is not a relative path inside the work directory");
        }

        return Path.Combine([Root, .. segments]);
    }

    /// <summary>Creates or replaces <paramref name="relative"/> with <paramref name="bytes"/> atomically, creating its folders.</summary>
    public void Write(string relative, byte[] bytes)
    {
        var path = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, bytes);
    }

    /// <summary>Writes <c>streams/&lt;stream&gt;.mmrs</c> as <paramref name="recorded"/>, encoded: a generated stream with its expected hashes.</summary>
    public void WriteStream(string stream, RecordedStream recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        Write(StreamFile(stream), recorded.Encode());
    }
}
