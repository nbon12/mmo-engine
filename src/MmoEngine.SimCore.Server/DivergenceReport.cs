using System.Globalization;
using System.Text;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The divergence report (FR-026, US2 scenarios 4 and 5, SC-002;
/// specs/015-shared-simulation-core/contracts/state-serialization.md#divergence-report and
/// contracts/simcore-cli.md#divergence-report). <see cref="AgainstExpected"/> is the server build's report
/// against a committed stream's expected hashes, which <c>simcore streams verify</c> prints: a committed
/// stream stores hashes, not states, so it names the step and both hashes but no fields.
/// <see cref="CompareBuild"/> compares a build's step records with the server's and lists the first
/// divergent step's differing fields, decoded through <see cref="StateFieldReader"/>;
/// <see cref="BuildError"/> reports a build that wrote an error file; <see cref="NotComparable"/> is the
/// line for a stream in another format version or rules revision.
/// </summary>
/// <remarks>
/// Every report's lines are joined with <c>\n</c>, with no line break after the last. Values are bits as
/// <c>0x%08X</c> (zero-extended to 32) and a decimal (unsigned or signed by type; floats in round-trip
/// <c>"R"</c> form in the invariant culture); hashes are 16 lowercase hex digits.
/// </remarks>
public static class DivergenceReport
{
    private const string Absent = "absent";

    /// <summary>
    /// The server build's run of <paramref name="stream"/> against its committed expected hashes, first
    /// differing at <paramref name="step"/>: two lines joined with <c>\n</c>, with no line break after the
    /// second,
    /// <c>DIVERGED stream &lt;name&gt;, build server, first divergent step &lt;n&gt;, against the expected hashes</c>
    /// and <c>  hash  expected &lt;x16&gt;  server &lt;x16&gt;</c>, each hash as 16 lowercase hex digits.
    /// </summary>
    /// <exception cref="ArgumentNullException">The stream name is null.</exception>
    public static string AgainstExpected(string stream, uint step, ulong expected, ulong actual)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return AgainstExpected(stream, "server", step, expected, actual);
    }

    /// <summary>
    /// Compares <paramref name="build"/>'s packed step records of <paramref name="stream"/> with the
    /// server's (contracts/state-serialization.md#step-record), step by step in the server's order. The
    /// first divergent step is the first where, in this order: the build's record is missing or not whole;
    /// the two records' step numbers differ; their hashes or state bytes differ (the report then lists the
    /// differing fields); or the build's hash differs from the stream's expected hash (the report then
    /// names the step and both hashes, <c>against the expected hashes</c>). Records the build has past the
    /// server's last are a divergence at the first of them. Equal records give
    /// <see cref="StreamOutcome.Match"/> and an empty report.
    /// </summary>
    /// <exception cref="ArgumentNullException">A name or the expected hashes are null.</exception>
    /// <exception cref="ArgumentException">
    /// The server's records are not whole records back to back, or their number is not the number of
    /// expected hashes.
    /// </exception>
    public static StreamComparison CompareBuild(
        string stream,
        string build,
        ReadOnlySpan<byte> serverRecords,
        ReadOnlySpan<byte> buildRecords,
        IReadOnlyList<ulong> expectedHashes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(expectedHashes);
        var (serverCount, serverRemaining) = CountRecords(serverRecords);
        if (serverRemaining != 0 || serverCount != expectedHashes.Count)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The server records of {stream} hold {serverCount} whole records and {serverRemaining} more bytes; the stream has {expectedHashes.Count} expected hashes."),
                nameof(serverRecords));
        }

        var serverOffset = 0;
        var buildOffset = 0;
        var nextStep = 0u;
        for (var i = 0; i < serverCount; i++)
        {
            // Every server record is whole: the count above walked them.
            _ = StepRecord.TryRead(serverRecords[serverOffset..], out var step, out var serverState, out var serverHash, out var serverLength);
            nextStep = unchecked(step + 1u);
            if (!StepRecord.TryRead(buildRecords[buildOffset..], out var buildStep, out var buildState, out var buildHash, out var buildLength))
            {
                return Diverged(Header(stream, build, step), HashLine("server", Hex(serverHash), build, Absent), step, serverHash, null);
            }

            if (buildStep != step || buildHash != serverHash || !buildState.SequenceEqual(serverState))
            {
                return FieldDiff(stream, build, step, buildStep, serverState, buildState, serverHash, buildHash);
            }

            if (buildHash != expectedHashes[i])
            {
                return new StreamComparison(
                    StreamOutcome.Diverged,
                    step,
                    serverHash,
                    buildHash,
                    0,
                    null,
                    AgainstExpected(stream, build, step, expectedHashes[i], buildHash));
            }

            serverOffset += serverLength;
            buildOffset += buildLength;
        }

        if (buildOffset < buildRecords.Length)
        {
            var whole = StepRecord.TryRead(buildRecords[buildOffset..], out var extraStep, out _, out var extraHash, out _);
            var step = whole ? extraStep : nextStep;
            var buildHash = whole ? extraHash : (ulong?)null;
            return Diverged(Header(stream, build, step), HashLine("server", Absent, build, whole ? Hex(extraHash) : Absent), step, null, buildHash);
        }

        return new StreamComparison(StreamOutcome.Match, null, null, null, 0, null, string.Empty);
    }

    /// <summary>
    /// A build that wrote <c>&lt;stream&gt;.error.txt</c> instead of records: Diverged at the stream's first
    /// step <paramref name="firstStep"/>, with <paramref name="message"/> as the error and the report
    /// <c>DIVERGED stream &lt;s&gt;, build &lt;b&gt;, first divergent step &lt;n&gt;</c> and
    /// <c>  error  &lt;message&gt;</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">A name or the message is null.</exception>
    public static StreamComparison BuildError(string stream, string build, uint firstStep, string message)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(message);
        return new StreamComparison(
            StreamOutcome.Diverged,
            firstStep,
            null,
            null,
            0,
            message,
            string.Concat(Header(stream, build, firstStep), "\n  error  ", message));
    }

    /// <summary>
    /// The line for a stream in another format version or rules revision (US2 scenario 5):
    /// <c>NOT COMPARABLE stream &lt;name&gt;: &lt;detail&gt;</c>, the refusal's detail naming both values,
    /// such as <c>unsupported version 2; this reader supports version 1</c> or
    /// <c>rules revision 2 in the stream; this core implements rules revision 1</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The stream name is null.</exception>
    public static string NotComparable(string stream, FormatError error)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Concat("NOT COMPARABLE stream ", stream, ": ", error.Detail);
    }

    /// <summary>The expected-hash report of <paramref name="build"/>: two lines, no field.</summary>
    private static string AgainstExpected(string stream, string build, uint step, ulong expected, ulong actual) => string.Create(
        CultureInfo.InvariantCulture,
        $"DIVERGED stream {stream}, build {build}, first divergent step {step}, against the expected hashes\n  hash  expected {expected:x16}  {build} {actual:x16}");

    /// <summary>
    /// The field-diff report of the first divergent step: the header, the hash line, the record step line
    /// when the two step numbers differ, the count of field lines, each differing field in layout order
    /// (with <c>absent</c> for an element on one side only), and <c>unreadable from offset &lt;n&gt;</c> when
    /// a state cannot be read to its end. Fields at or past the place where either state stops being
    /// readable are not compared.
    /// </summary>
    private static StreamComparison FieldDiff(
        string stream,
        string build,
        uint step,
        uint buildStep,
        ReadOnlySpan<byte> serverState,
        ReadOnlySpan<byte> buildState,
        ulong serverHash,
        ulong buildHash)
    {
        var server = StateFieldReader.Read(serverState);
        var other = StateFieldReader.Read(buildState);
        var stop = server.Stop.CompareTo(other.Stop) <= 0 ? server.Stop : other.Stop;
        var fields = new List<string>();
        var (i, j) = (0, 0);
        while (true)
        {
            var hasServer = i < server.Fields.Length && server.Fields[i].Key.CompareTo(stop) < 0;
            var hasBuild = j < other.Fields.Length && other.Fields[j].Key.CompareTo(stop) < 0;
            if (!hasServer && !hasBuild)
            {
                break;
            }

            var order = !hasBuild ? -1 : !hasServer ? 1 : server.Fields[i].Key.CompareTo(other.Fields[j].Key);
            if (order == 0)
            {
                if (server.Fields[i].Bits != other.Fields[j].Bits)
                {
                    fields.Add(FieldLine(server.Fields[i].Path, Value(server.Fields[i]), build, Value(other.Fields[j])));
                }

                i++;
                j++;
            }
            else if (order < 0)
            {
                fields.Add(FieldLine(server.Fields[i].Path, Value(server.Fields[i]), build, Absent));
                i++;
            }
            else
            {
                fields.Add(FieldLine(other.Fields[j].Path, Absent, build, Value(other.Fields[j])));
                j++;
            }
        }

        var report = new StringBuilder();
        report.Append(Header(stream, build, step)).Append('\n');
        report.Append(HashLine("server", Hex(serverHash), build, Hex(buildHash)));
        if (buildStep != step)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n  record step  server {step}  {build} {buildStep}");
        }

        report.Append(CultureInfo.InvariantCulture, $"\n  {fields.Count} {(fields.Count == 1 ? "field differs" : "fields differ")}");
        foreach (var line in fields)
        {
            report.Append('\n').Append(line);
        }

        if ((other.UnreadableOffset ?? server.UnreadableOffset) is { } unreadable)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n  unreadable from offset {unreadable}");
        }

        return new StreamComparison(StreamOutcome.Diverged, step, serverHash, buildHash, fields.Count, null, report.ToString());
    }

    private static StreamComparison Diverged(string header, string hashLine, uint step, ulong? serverHash, ulong? buildHash) =>
        new(StreamOutcome.Diverged, step, serverHash, buildHash, 0, null, string.Concat(header, "\n", hashLine));

    /// <summary>The whole records back to back at the start of <paramref name="records"/>, and the bytes after them.</summary>
    private static (int Count, int Remaining) CountRecords(ReadOnlySpan<byte> records)
    {
        var count = 0;
        var offset = 0;
        while (StepRecord.TryRead(records[offset..], out _, out _, out _, out var length))
        {
            count++;
            offset += length;
        }

        return (count, records.Length - offset);
    }

    private static string Header(string stream, string build, uint step) =>
        string.Create(CultureInfo.InvariantCulture, $"DIVERGED stream {stream}, build {build}, first divergent step {step}");

    private static string HashLine(string reference, string referenceHash, string build, string buildHash) =>
        string.Concat("  hash  ", reference, " ", referenceHash, string.Concat("  ", build, " ", buildHash));

    private static string FieldLine(string path, string server, string build, string buildValue) =>
        string.Concat(string.Concat("  ", path, "  server ", server), string.Concat("  ", build, " ", buildValue));

    /// <summary>A field's value: its bits as <c>0x%08X</c>, then its decimal by type.</summary>
    private static string Value(StateFieldValue field)
    {
        var bits = field.Bits;
        var decimalText = field.Type switch
        {
            StateFieldType.I16 => ((short)bits).ToString(CultureInfo.InvariantCulture),
            StateFieldType.I32 => ((int)bits).ToString(CultureInfo.InvariantCulture),
            StateFieldType.F32 => BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture),
            _ => bits.ToString(CultureInfo.InvariantCulture),
        };
        return string.Create(CultureInfo.InvariantCulture, $"0x{bits:X8} {decimalText}");
    }

    private static string Hex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);
}
