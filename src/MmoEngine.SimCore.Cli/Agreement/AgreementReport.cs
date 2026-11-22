using System.Globalization;
using System.Text;
using System.Text.Json;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>
/// <c>agreement compare</c>'s report files and summary (specs/015-shared-simulation-core/contracts/simcore-cli.md#agreement-compare):
/// <c>report/agreement.json</c>, every build and stream; <c>report/divergences/&lt;build&gt;/&lt;stream&gt;.txt</c>,
/// each diverged stream's divergence report; <c>report/divergences/&lt;build&gt;/tables.txt</c>, the first
/// mismatching table row or the missing results' message; all written through <see cref="AtomicFile"/>. With
/// <c>--summary</c>, a <c>## SimCore agreement</c> Markdown section is appended to the file in one write.
/// </summary>
internal static class AgreementReport
{
    /// <summary>The report's own format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>The report file, relative to the work directory.</summary>
    public const string ReportFile = ReportFolder + "/agreement.json";

    private const string ReportFolder = "report";

    private const string DivergencesFolder = ReportFolder + "/divergences";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary><c>report/divergences/&lt;build&gt;/&lt;stream&gt;.txt</c>, relative to the work directory.</summary>
    public static string DivergencePath(string build, string stream) => DivergencesFolder + "/" + build + "/" + stream + ".txt";

    /// <summary><c>report/divergences/&lt;build&gt;/tables.txt</c>, relative to the work directory.</summary>
    public static string TablesPath(string build) => DivergencesFolder + "/" + build + "/tables.txt";

    /// <summary>
    /// The one line of a build's tables result for stdout and the summary when it did not match: the first
    /// mismatch line, or <c>tables: build &lt;b&gt;, results missing: &lt;message&gt;</c>.
    /// </summary>
    public static string TablesLine(BuildComparison build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var tables = build.Tables!;
        return tables.Status == TablesStatus.Missing
            ? string.Concat("tables: build ", build.Build, ", results missing: ", tables.FirstMismatch)
            : tables.FirstMismatch!;
    }

    /// <summary>
    /// A diverged build's first report: its first diverged stream's divergence report, or, when only its
    /// tables diverged, its tables line.
    /// </summary>
    public static string FirstReport(BuildComparison build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.Diverged.Count > 0 ? build.Diverged[0].Comparison.Report : TablesLine(build);
    }

    /// <summary>
    /// Writes every diverged stream's report, every unmatched tables result, and then <c>agreement.json</c>
    /// with <paramref name="outcome"/> (<c>Match</c>, <c>Diverged</c> or <c>CannotRun</c>).
    /// </summary>
    public static void Write(WorkDirectory work, AgreementResult result, IReadOnlyList<string> required, string outcome)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(required);
        foreach (var build in result.Builds)
        {
            foreach (var diverged in build.Diverged)
            {
                WriteText(work, diverged.ReportPath, diverged.Comparison.Report + "\n");
            }

            if (build.Tables is { Status: not TablesStatus.Match } tables)
            {
                WriteText(work, TablesPath(build.Build), tables.FirstMismatch + "\n");
            }
        }

        var path = work.FullPath(ReportFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, stream =>
        {
            using var writer = new Utf8JsonWriter(stream, WriterOptions);
            WriteJson(writer, result, required, outcome);
        });
    }

    /// <summary>
    /// The Markdown section for <c>--summary</c>: a leading line break, the <c>## SimCore agreement</c>
    /// heading, the outcome line, a table with one row per known build (build, required, status, streams,
    /// steps, first divergence, tables), and each diverged build's first report in a fenced code block.
    /// </summary>
    public static string Summary(AgreementResult result, string outcomeLine)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        text.Append("\n## SimCore agreement\n\n");
        text.Append(outcomeLine).Append("\n\n");
        text.Append("| Build | Required | Status | Streams | Steps | First divergence | Tables |\n");
        text.Append("| --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var build in result.Builds)
        {
            var required = build.Required ? "yes" : "no";
            text.Append(build.Status == BuildStatus.NotRun
                ? $"| {build.Build} | {required} | NotRun | - | - | - | - |\n"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {build.Build} | {required} | {build.Status} | {result.Streams} | {result.Steps} | {FirstDivergence(build)} | {build.Tables!.Status.ToString().ToLowerInvariant()} |\n"));
        }

        foreach (var build in result.Builds.Where(build => build.Status == BuildStatus.Diverged))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{build.Build}:\n\n```text\n{FirstReport(build)}\n```\n");
        }

        return text.ToString();
    }

    /// <summary>Appends <paramref name="section"/> to <paramref name="file"/> in one write, creating the file when it does not exist.</summary>
    public static void AppendSummary(string file, string section)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(section);
        var bytes = Encoding.UTF8.GetBytes(section);
        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The summary's first divergence: <c>&lt;stream&gt; at step &lt;k&gt;</c>, <c>tables</c> when only the tables diverged, or <c>none</c>.</summary>
    private static string FirstDivergence(BuildComparison build) => build.Diverged.Count > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{build.Diverged[0].Stream} at step {build.Diverged[0].Comparison.FirstDivergentStep}")
        : build.Status == BuildStatus.Diverged ? "tables" : "none";

    private static void WriteText(WorkDirectory work, string relative, string text)
    {
        var path = work.FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, text);
    }

    private static void WriteJson(Utf8JsonWriter writer, AgreementResult result, IReadOnlyList<string> required, string outcome)
    {
        writer.WriteStartObject();
        writer.WriteNumber("formatVersion", FormatVersion);
        writer.WriteString("outcome", outcome);
        writer.WriteNumber("rulesRevision", SimCoreVersion.RulesRevision);
        writer.WriteNumber("streams", result.Streams);
        writer.WriteStartArray("required");
        foreach (var name in required)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("builds");
        foreach (var build in result.Builds)
        {
            writer.WriteStartObject();
            writer.WriteString("build", build.Build);
            writer.WriteBoolean("required", build.Required);
            writer.WriteString("status", build.Status.ToString());
            if (build.Tables is { } tables)
            {
                writer.WriteNumber("streams", result.Streams);
                writer.WriteNumber("steps", result.Steps);
                writer.WriteStartArray("diverged");
                foreach (var diverged in build.Diverged)
                {
                    WriteDiverged(writer, diverged);
                }

                writer.WriteEndArray();
                writer.WriteStartObject("tables");
                writer.WriteString("status", tables.Status.ToString());
                writer.WriteString("firstMismatch", tables.FirstMismatch);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDiverged(Utf8JsonWriter writer, DivergedStream diverged)
    {
        var comparison = diverged.Comparison;
        writer.WriteStartObject();
        writer.WriteString("stream", diverged.Stream);
        writer.WriteNumber("step", comparison.FirstDivergentStep!.Value);
        writer.WriteString("serverHash", Hex(comparison.ServerHash));
        writer.WriteString("buildHash", Hex(comparison.BuildHash));
        writer.WriteNumber("differingFields", comparison.DifferingFields);
        writer.WriteString("error", comparison.Error);
        writer.WriteString("report", diverged.ReportPath);
        writer.WriteEndObject();
    }

    private static string? Hex(ulong? hash) => hash?.ToString("x16", CultureInfo.InvariantCulture);
}
