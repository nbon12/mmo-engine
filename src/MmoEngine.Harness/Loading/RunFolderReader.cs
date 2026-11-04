using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Loading;

/// <summary>A run folder the loader refuses (FR-005): incomplete, an unknown format version, or malformed files.</summary>
public sealed class RunFolderException(string message) : Exception(message);

/// <summary>
/// Reads a simulator run folder written to sub-spec 1's run-output contract, format version 1
/// (specs/013-regionizer-simulator/contracts/run-output.md). Columns are matched by header name, so a
/// column added later doesn't shift the others.
/// </summary>
public sealed class RunFolderReader
{
    /// <summary>The only run-output format version this loader reads.</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>The <c>sim_ticks</c> columns, in insert order, and the <c>ticks.csv</c> header each comes from.</summary>
    internal static readonly (string Column, string Header, ColumnKind Kind)[] TickColumns =
    [
        ("tick", "tick", ColumnKind.UInt64),
        ("time_steps", "timeSteps", ColumnKind.UInt64),
        ("region_count", "regionCount", ColumnKind.UInt32),
        ("largest_region_players", "largestRegionPlayers", ColumnKind.UInt32),
        ("largest_region_share", "largestRegionShare", ColumnKind.Float64),
        ("merges", "merges", ColumnKind.UInt32),
        ("absorptions", "absorptions", ColumnKind.UInt32),
        ("splits", "splits", ColumnKind.UInt32),
        ("fusions", "fusions", ColumnKind.UInt32),
        ("fused_fight_pairs", "fusedFightPairs", ColumnKind.UInt32),
        ("section_churn", "sectionChurn", ColumnKind.UInt64),
        ("cross_pairs_deferred", "crossPairsDeferred", ColumnKind.NullableUInt64),
        ("cross_pairs_read_only", "crossPairsReadOnly", ColumnKind.NullableUInt64),
        ("bookkeeping_micros", "bookkeepingMicros", ColumnKind.Float64),
        ("serialized_micros", "serializedMicros", ColumnKind.Float64),
        ("oversized_fusion", "oversizedFusion", ColumnKind.UInt8),
        ("violations", "violations", ColumnKind.UInt32),
        ("state_hash", "stateHash", ColumnKind.String),
    ];

    /// <summary>The <c>sim_events</c> columns after <c>run_id</c> and <c>seq</c>, and their <c>events.csv</c> headers.</summary>
    internal static readonly (string Column, string Header, ColumnKind Kind)[] EventColumns =
    [
        ("tick", "tick", ColumnKind.UInt64),
        ("time_steps", "timeSteps", ColumnKind.UInt64),
        ("type", "type", ColumnKind.String),
        ("region_a", "regionA", ColumnKind.NullableInt64),
        ("region_b", "regionB", ColumnKind.NullableInt64),
        ("cause_class", "causeClass", ColumnKind.String),
        ("cause_entity", "causeEntity", ColumnKind.NullableInt64),
        ("value", "value", ColumnKind.NullableFloat64),
        ("detail", "detail", ColumnKind.String),
    ];

    private RunFolderReader(string folder, JsonObject manifest, JsonObject? summary)
    {
        Folder = folder;
        Manifest = manifest;
        Summary = summary;
    }

    /// <summary>The run folder.</summary>
    public string Folder { get; }

    /// <summary>The parsed <c>manifest.json</c>.</summary>
    public JsonObject Manifest { get; }

    /// <summary>The parsed <c>summary.json</c>, if present.</summary>
    public JsonObject? Summary { get; }

    /// <summary>The run ID from the manifest.</summary>
    public string RunId => Manifest["runId"]!.GetValue<string>();

    /// <summary>Opens a run folder, refusing an incomplete one or an unknown format version.</summary>
    public static RunFolderReader Open(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
        {
            throw new RunFolderException($"Run folder not found: {folder}");
        }

        if (File.Exists(Path.Combine(folder, "_incomplete")))
        {
            throw new RunFolderException($"Run folder {folder} is incomplete (it still has its _incomplete marker); it was not loaded.");
        }

        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new RunFolderException($"Run folder {folder} has no manifest.json.");
        }

        var manifest = ParseObject(manifestPath);
        var version = manifest["formatVersion"]?.GetValue<int>();
        if (version != SupportedFormatVersion)
        {
            throw new RunFolderException(
                $"Run folder {folder} has formatVersion {version?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}; this loader reads only version {SupportedFormatVersion}.");
        }

        if (manifest["runId"] is not JsonValue)
        {
            throw new RunFolderException($"Run folder {folder}: manifest.json has no runId.");
        }

        foreach (var file in new[] { "ticks.csv", "events.csv" })
        {
            if (!File.Exists(Path.Combine(folder, file)))
            {
                throw new RunFolderException($"Run folder {folder} has no {file}.");
            }
        }

        var summaryPath = Path.Combine(folder, "summary.json");
        var summary = File.Exists(summaryPath) ? ParseObject(summaryPath) : null;
        return new RunFolderReader(folder, manifest, summary);
    }

    /// <summary>The <c>runs</c> row for this run (not yet marked loaded).</summary>
    public RunRow ToRunRow(string runnerClass, string sweep = "", string cellId = "") => new()
    {
        RunId = RunId,
        Kind = sweep.Length > 0 ? "sweep-cell" : Text(Manifest, "kind", "simulator"),
        GitSha = Text(Manifest, "gitSha", "unknown"),
        ConfigHash = Text(Manifest, "configHash", ""),
        Scenario = Text(Manifest, "scenario", ""),
        Seed = Manifest["seed"] is JsonValue s ? s.GetValue<ulong>() : 0,
        Scheduling = Text(Manifest, "scheduling", ""),
        Profile = "none",
        RunnerClass = runnerClass,
        Sweep = sweep,
        CellId = cellId,
        Started = Time(Manifest, "startedUtc") ?? DateTime.UnixEpoch,
        Finished = Time(Manifest, "finishedUtc"),
        Ticks = Manifest["ticks"] is JsonValue t ? t.GetValue<ulong>() : 0,
        Manifest = Manifest.ToJsonString(),
    };

    /// <summary>The <c>sim_ticks</c> column names, <c>run_id</c> first.</summary>
    public static IReadOnlyList<string> TickTableColumns { get; } = ["run_id", .. TickColumns.Select(c => c.Column)];

    /// <summary>The <c>sim_events</c> column names, <c>run_id</c> and <c>seq</c> first.</summary>
    public static IReadOnlyList<string> EventTableColumns { get; } = ["run_id", "seq", .. EventColumns.Select(c => c.Column)];

    /// <summary>The rows of <c>ticks.csv</c>, as <c>sim_ticks</c> rows.</summary>
    public IEnumerable<object?[]> TickRows()
    {
        var runId = RunId;
        foreach (var (line, values, index) in Csv(Path.Combine(Folder, "ticks.csv"), TickColumns.Select(c => c.Header)))
        {
            var row = new object?[TickColumns.Length + 1];
            row[0] = runId;
            for (var i = 0; i < TickColumns.Length; i++)
            {
                row[i + 1] = Convert(values[index[i]], TickColumns[i].Kind, "ticks.csv", line, TickColumns[i].Header);
            }

            yield return row;
        }
    }

    /// <summary>The rows of <c>events.csv</c>, as <c>sim_events</c> rows numbered in file order.</summary>
    public IEnumerable<object?[]> EventRows()
    {
        var runId = RunId;
        ulong seq = 0;
        foreach (var (line, values, index) in Csv(Path.Combine(Folder, "events.csv"), EventColumns.Select(c => c.Header)))
        {
            var row = new object?[EventColumns.Length + 2];
            row[0] = runId;
            row[1] = seq++;
            for (var i = 0; i < EventColumns.Length; i++)
            {
                row[i + 2] = Convert(values[index[i]], EventColumns[i].Kind, "events.csv", line, EventColumns[i].Header);
            }

            yield return row;
        }
    }

    /// <summary>The <c>run_aggregates</c> rows from <c>summary.json</c>'s <c>metrics</c>.</summary>
    public IEnumerable<object?[]> AggregateRows()
    {
        if (Summary?["metrics"] is not JsonObject metrics)
        {
            yield break;
        }

        foreach (var (name, node) in metrics.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (node is not JsonObject stats)
            {
                continue;
            }

            yield return [RunId, name, Number(stats, "p50"), Number(stats, "p95"), Number(stats, "p99"), Number(stats, "max"), Number(stats, "mean")];
        }
    }

    /// <summary>The <c>run_aggregates</c> column names.</summary>
    public static IReadOnlyList<string> AggregateTableColumns { get; } = ["run_id", "metric", "p50", "p95", "p99", "max", "mean"];

    internal enum ColumnKind
    {
        String,
        UInt8,
        UInt32,
        UInt64,
        Float64,
        NullableUInt64,
        NullableInt64,
        NullableFloat64,
    }

    internal static object? Convert(string text, ColumnKind kind, string file, long line, string header)
    {
        try
        {
            return kind switch
            {
                ColumnKind.String => text,
                ColumnKind.UInt8 => byte.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                ColumnKind.UInt32 => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                ColumnKind.UInt64 => ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                ColumnKind.Float64 => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                ColumnKind.NullableUInt64 => text.Length == 0 ? null : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
                ColumnKind.NullableInt64 => text.Length == 0 ? null : long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                ColumnKind.NullableFloat64 => text.Length == 0 ? null : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new RunFolderException($"{file} line {line}: column {header} has invalid value '{text}'.");
        }
    }

    /// <summary>Reads a CSV with a header, yielding (line number, fields, index of each wanted header).</summary>
    internal static IEnumerable<(long Line, string[] Values, int[] Index)> Csv(string path, IEnumerable<string> wanted)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine() ?? throw new RunFolderException($"{Path.GetFileName(path)} is empty (no header).");
        var names = SplitCsv(header);
        var index = wanted.Select(w =>
        {
            var i = Array.IndexOf(names, w);
            return i >= 0 ? i : throw new RunFolderException($"{Path.GetFileName(path)} has no column '{w}'.");
        }).ToArray();
        long line = 1;
        while (reader.ReadLine() is { } text)
        {
            line++;
            if (text.Length == 0)
            {
                continue;
            }

            var values = SplitCsv(text);
            if (values.Length != names.Length)
            {
                throw new RunFolderException($"{Path.GetFileName(path)} line {line}: {values.Length} fields, header has {names.Length}.");
            }

            yield return (line, values, index);
        }
    }

    /// <summary>Splits one CSV line, honouring double-quoted fields with doubled quotes inside.</summary>
    internal static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    private static JsonObject ParseObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new RunFolderException($"{path} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RunFolderException($"{path} is not valid JSON: {ex.Message}");
        }
    }

    private static string Text(JsonObject o, string name, string fallback) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    private static DateTime? Time(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) &&
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    private static double? Number(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
