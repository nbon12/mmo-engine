using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Agreement work directories for the <c>simcore agreement</c> tests
/// (contracts/simcore-cli.md#agreement-work-directory). <see cref="Prepare"/> runs
/// <c>simcore agreement prepare</c> in process into a new <c>work</c> folder of a unique
/// <see cref="TempDirectories"/> directory, with <c>--streams</c>, <c>--content</c> and <c>--config</c> on the
/// committed <c>simcore/</c> files, which the CLI opens read-only. <see cref="WriteBuild"/> then stands in for
/// a WebAssembly build by writing a <c>&lt;work&gt;/&lt;build&gt;/</c> folder from the server's own results,
/// optionally with one stream perturbed by one ulp, so <c>agreement compare</c> can be tested before any
/// WebAssembly build exists. Nothing is written outside the temp directory, so the tests are parallel-safe and
/// rerun-safe.
/// </summary>
public static class WorkDirectories
{
    /// <summary>The seeds <see cref="Prepare"/> generates unless told otherwise: two generated streams.</summary>
    public const string DefaultSeeds = "1-2";

    /// <summary>The extension of a step records file (contracts/simcore-cli.md#agreement-work-directory).</summary>
    public const string RecordsExtension = ".records";

    /// <summary>The server build's folder in a work directory.</summary>
    public const string ServerFolder = "server";

    /// <summary>The table results file of a build's folder.</summary>
    public const string TablesFile = "tables.bin";

    /// <summary>
    /// Runs <c>simcore agreement prepare --work &lt;unique temp&gt;/work --seeds &lt;seeds&gt;</c> with
    /// <c>--streams</c>, <c>--content</c> and <c>--config</c> on <c>&lt;root&gt;/simcore/streams</c>,
    /// <c>&lt;root&gt;/simcore/content</c> and <c>&lt;root&gt;/simcore/config/default.json</c>, asserts it
    /// exits 0, and returns the prepared work directory, which deletes its temp directory when disposed.
    /// </summary>
    public static PreparedWork Prepare(string seeds = DefaultSeeds)
    {
        ArgumentException.ThrowIfNullOrEmpty(seeds);
        var root = TempDirectories.Create("agreement-work");
        try
        {
            var work = root.Combine("work");
            var (exit, stdout, stderr) = SimcoreCli.Run(PrepareArgs(work, seeds));
            Assert.True(
                exit == 0,
                $"simcore agreement prepare --seeds {seeds} --work {work} exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
            return new PreparedWork(root, work);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The arguments of <c>simcore agreement prepare</c>: <c>--work</c>, <c>--seeds</c>, then <c>--streams</c>,
    /// <c>--content</c> and <c>--config</c>, each the committed <c>simcore/</c> path unless given.
    /// </summary>
    public static string[] PrepareArgs(string work, string seeds, string? streams = null, string? content = null, string? config = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(work);
        ArgumentNullException.ThrowIfNull(seeds);
        return
        [
            "agreement", "prepare",
            "--work", work,
            "--seeds", seeds,
            "--streams", streams ?? RepoPaths.Combine("simcore", "streams"),
            "--content", content ?? RepoPaths.Combine("simcore", "content"),
            "--config", config ?? RepoPaths.Combine("simcore", "config", "default.json"),
        ];
    }

    /// <summary>
    /// Writes the folder <c>&lt;work&gt;/&lt;build&gt;/</c> as a build equal to the server would: a copy of every
    /// <c>server/&lt;stream&gt;.records</c>, except that <paramref name="perturbStream"/>, when given, gets
    /// <c>StreamRunner.Run(stream, collision, perturbStep)</c> of <c>streams/&lt;stream&gt;.mmrs</c> over the
    /// <c>collision/&lt;x16&gt;.mmcd</c> its header names; then a copy of <c>server/tables.bin</c> as
    /// <c>tables.bin</c>.
    /// </summary>
    public static void WriteBuild(string work, string build, string? perturbStream = null, uint perturbStep = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(work);
        ArgumentException.ThrowIfNullOrEmpty(build);
        var server = Path.Combine(work, ServerFolder);
        var folder = Directory.CreateDirectory(Path.Combine(work, build)).FullName;
        var records = Directory.GetFiles(server, "*" + RecordsExtension);
        Array.Sort(records, StringComparer.Ordinal);
        Assert.True(records.Length > 0, $"{server} holds no {RecordsExtension} file to copy into {folder}");

        var perturbed = false;
        foreach (var source in records)
        {
            var name = Path.GetFileName(source)[..^RecordsExtension.Length];
            var target = Path.Combine(folder, name + RecordsExtension);
            if (name == perturbStream)
            {
                File.WriteAllBytes(target, PerturbedRecords(work, name, perturbStep));
                perturbed = true;
            }
            else
            {
                File.Copy(source, target);
            }
        }

        Assert.True(
            perturbStream is null || perturbed,
            $"{server} holds no {perturbStream}{RecordsExtension}, so {build} cannot perturb it");
        File.Copy(Path.Combine(server, TablesFile), Path.Combine(folder, TablesFile));
    }

    /// <summary>
    /// <c>StreamRunner.Run(stream, collision, perturbStep)</c> of the work directory's <c>streams/&lt;name&gt;.mmrs</c>
    /// over the collision file its header's <c>collisionChecksum</c> names.
    /// </summary>
    private static byte[] PerturbedRecords(string work, string name, uint perturbStep)
    {
        var stream = File.ReadAllBytes(Path.Combine(work, "streams", name + ".mmrs"));
        var checksum = RecordedStream.Decode(stream).CollisionChecksum;
        var collisionPath = Path.Combine(work, "collision", checksum.ToString("x16", CultureInfo.InvariantCulture) + ".mmcd");
        Assert.True(File.Exists(collisionPath), $"{name} names collision data {checksum:x16}, but {collisionPath} does not exist");
        return StreamRunner.Run(stream, File.ReadAllBytes(collisionPath), perturbStep: checked((int)perturbStep));
    }
}

/// <summary>
/// A work directory <see cref="WorkDirectories.Prepare"/> wrote: <see cref="Path"/> is the <c>work</c> folder
/// of a unique temp directory, which <see cref="Dispose"/> deletes with everything in it.
/// </summary>
public sealed class PreparedWork : IDisposable
{
    private readonly TempDirectory _root;

    internal PreparedWork(TempDirectory root, string path)
    {
        _root = root;
        Path = path;
    }

    /// <summary>The work directory's absolute path, passed as <c>--work</c>.</summary>
    public string Path { get; }

    /// <summary>A path inside the work directory.</summary>
    public string Combine(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var all = new string[parts.Length + 1];
        all[0] = Path;
        parts.CopyTo(all, 1);
        return System.IO.Path.Combine(all);
    }

    /// <summary>A path beside the work directory, in the same unique temp directory, such as a <c>--summary</c> file.</summary>
    public string Beside(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _root.Combine(name);
    }

    /// <summary>Deletes the temp directory and the work directory in it, best effort.</summary>
    public void Dispose() => _root.Dispose();
}
