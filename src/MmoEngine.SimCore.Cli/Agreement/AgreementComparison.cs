using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>A build's status in <c>agreement compare</c> (contracts/simcore-cli.md#agreement-compare).</summary>
internal enum BuildStatus
{
    /// <summary>Every stream matched at every step, and the tables matched.</summary>
    Match = 0,

    /// <summary>A stream diverged, failed or is missing, or the tables diverged or are missing.</summary>
    Diverged = 1,

    /// <summary>The build has no folder; an error only when it is required.</summary>
    NotRun = 2,
}

/// <summary>One diverged stream of a build: the comparison and its report's path under <c>report/</c>.</summary>
internal sealed record DivergedStream(string Stream, StreamComparison Comparison, string ReportPath);

/// <summary>
/// One known build's comparison: whether it is required, its status, its diverged streams in manifest
/// order, and its tables result (null when it did not run).
/// </summary>
internal sealed record BuildComparison(string Build, bool Required, BuildStatus Status, IReadOnlyList<DivergedStream> Diverged, TablesResult? Tables);

/// <summary>Every known build's comparison, in contract order, with the manifest's stream and step counts.</summary>
internal sealed record AgreementResult(int Streams, long Steps, IReadOnlyList<BuildComparison> Builds);

/// <summary>
/// Compares every build folder of an agreement work directory with the server's
/// (US2 scenarios 4 and 5, FR-026 server half; specs/015-shared-simulation-core/contracts/simcore-cli.md#agreement-compare).
/// Every known build with a folder is compared, required or not; one without is
/// <see cref="BuildStatus.NotRun"/>, and folders with other names are ignored. For each manifest stream it
/// reads <c>&lt;build&gt;/&lt;stream&gt;.error.txt</c>, reported through <see cref="DivergenceReport.BuildError"/>
/// at the stream's first step, or else <c>&lt;build&gt;/&lt;stream&gt;.records</c> (none when absent), compared
/// through <see cref="DivergenceReport.CompareBuild"/> with <c>server/&lt;stream&gt;.records</c> and the expected
/// hashes of <c>streams/&lt;stream&gt;.mmrs</c>. Then <c>tables.error.txt</c> or <c>tables.bin</c> goes through
/// <see cref="TablesComparison"/>, a build with neither or with the error file being
/// <see cref="TablesResult.Missing"/>.
/// </summary>
internal static class AgreementComparison
{
    /// <summary>
    /// Compares every known build in <paramref name="work"/> under <paramref name="manifest"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The comparison cannot run: a stream file, a server records file, the table inputs or
    /// <c>server/tables.bin</c> is missing, a stream file is refused, or a manifest path leaves the work
    /// directory. The message is the reason.
    /// </exception>
    /// <exception cref="ArgumentException">The server's records or table results are inconsistent with the streams or the inputs.</exception>
    public static AgreementResult Compare(WorkDirectory work, AgreementManifest manifest, IReadOnlyCollection<string> required)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(required);
        var server = new List<ServerStream>(manifest.Streams.Count);
        foreach (var entry in manifest.Streams)
        {
            RecordedStream stream;
            try
            {
                stream = RecordedStream.Decode(ReadRequired(work, entry.File));
            }
            catch (CoreFormatException ex)
            {
                throw new InvalidDataException($"{entry.File}: {ex.Message}", ex);
            }

            server.Add(new ServerStream(entry.Name, stream.Steps[0].Step, [.. stream.Steps.Select(step => step.ExpectedHash)], ReadRequired(work, entry.ServerRecords)));
        }

        var inputs = ReadRequired(work, manifest.Tables.Inputs);
        var serverTables = ReadRequired(work, manifest.Tables.ServerResults);
        var builds = new List<BuildComparison>(BuildNames.Known.Count);
        foreach (var build in BuildNames.Known)
        {
            var isRequired = required.Contains(build, StringComparer.Ordinal);
            builds.Add(Directory.Exists(Path.Combine(work.Root, build))
                ? CompareBuild(work, build, isRequired, server, inputs, serverTables)
                : new BuildComparison(build, isRequired, BuildStatus.NotRun, [], null));
        }

        return new AgreementResult(server.Count, server.Sum(stream => (long)stream.ExpectedHashes.Length), builds);
    }

    /// <summary>One build's streams, then its tables.</summary>
    private static BuildComparison CompareBuild(
        WorkDirectory work, string build, bool required, List<ServerStream> server, byte[] inputs, byte[] serverTables)
    {
        var diverged = new List<DivergedStream>();
        foreach (var stream in server)
        {
            var comparison = ReadText(work, WorkDirectory.ErrorFile(build, stream.Name)) is { } error
                ? DivergenceReport.BuildError(stream.Name, build, stream.FirstStep, error)
                : DivergenceReport.CompareBuild(stream.Name, build, stream.Records, ReadOptional(work, WorkDirectory.RecordsFile(build, stream.Name)), stream.ExpectedHashes);
            if (comparison.Outcome == StreamOutcome.Diverged)
            {
                diverged.Add(new DivergedStream(stream.Name, comparison, AgreementReport.DivergencePath(build, stream.Name)));
            }
        }

        TablesResult tables;
        if (ReadText(work, WorkDirectory.TablesErrorFile(build)) is { } tablesError)
        {
            tables = TablesResult.Missing(tablesError);
        }
        else
        {
            var results = ReadOptional(work, WorkDirectory.TablesFile(build), out var exists);
            tables = exists
                ? TablesComparison.Compare(build, inputs, serverTables, results)
                : TablesResult.Missing($"{WorkDirectory.TablesFile(build)} was not written");
        }

        var status = diverged.Count == 0 && tables.Status == TablesStatus.Match ? BuildStatus.Match : BuildStatus.Diverged;
        return new BuildComparison(build, required, status, diverged, tables);
    }

    /// <summary>A file the comparison needs: missing is a reason it cannot run.</summary>
    private static byte[] ReadRequired(WorkDirectory work, string relative)
    {
        var bytes = ReadOptional(work, relative, out var exists);
        return exists ? bytes : throw new InvalidDataException($"{relative} does not exist");
    }

    /// <summary>A build's file, or no bytes when it is absent: a missing record diverges at its step.</summary>
    private static byte[] ReadOptional(WorkDirectory work, string relative) => ReadOptional(work, relative, out _);

    private static byte[] ReadOptional(WorkDirectory work, string relative, out bool exists)
    {
        var path = work.FullPath(relative);
        exists = File.Exists(path);
        if (!exists)
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>A build's error file without its trailing line breaks, or null when it wrote none.</summary>
    private static string? ReadText(WorkDirectory work, string relative)
    {
        var bytes = ReadOptional(work, relative, out var exists);
        return exists ? System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n') : null;
    }

    /// <summary>The server's side of one stream: its first step, its expected hashes and the server build's records.</summary>
    private sealed record ServerStream(string Name, uint FirstStep, ulong[] ExpectedHashes, byte[] Records);
}
