using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Metrics;

namespace MmoEngine.Regionizer.Simulator.Output;

/// <summary>
/// Writes one run folder (contracts/run-output.md, format version 1): <c>manifest.json</c>,
/// <c>ticks.csv</c>, <c>events.csv</c>, <c>summary.json</c> and the <c>_incomplete</c> marker, which is
/// present while the run is in progress and removed only when it finishes. Opening a folder clears
/// whatever it held, so a rerun never appends.
/// </summary>
public sealed class RunFolder : IDisposable
{
    public const int FormatVersion = 1;
    public const string IncompleteMarker = "_incomplete";
    public const string ManifestFile = "manifest.json";
    public const string TicksFile = "ticks.csv";
    public const string EventsFile = "events.csv";
    public const string SummaryFile = "summary.json";

    public const string TicksHeader =
        "tick,timeSteps,regionCount,largestRegionPlayers,largestRegionShare,merges,splits,fusions,"
        + "sectionChurn,crossPairsDeferred,crossPairsReadOnly,bookkeepingMicros,serializedMicros,"
        + "oversizedFusion,violations,stateHash";

    public const string EventsHeader = "tick,timeSteps,type,regionA,regionB,causeClass,causeEntity,value,detail";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly StreamWriter _ticks;
    private readonly StreamWriter _events;
    private readonly StringBuilder _line = new();
    private bool _disposed;

    private RunFolder(string path)
    {
        Path = path;
        _ticks = Open(TicksFile);
        _events = Open(EventsFile);
        _ticks.WriteLine(TicksHeader);
        _events.WriteLine(EventsHeader);
    }

    /// <summary>The run folder.</summary>
    public string Path { get; }

    public string SummaryPath => System.IO.Path.Combine(Path, SummaryFile);

    /// <summary>Empties (or creates) <paramref name="path"/>, writes the <c>_incomplete</c> marker and opens the CSV files.</summary>
    public static RunFolder Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            foreach (var file in Directory.EnumerateFiles(full))
            {
                File.Delete(file);
            }

            foreach (var dir in Directory.EnumerateDirectories(full))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        Directory.CreateDirectory(full);
        File.WriteAllText(System.IO.Path.Combine(full, IncompleteMarker), string.Empty);
        return new RunFolder(full);
    }

    public void WriteManifest(JsonObject manifest) => WriteJson(ManifestFile, manifest);

    public void WriteSummary(JsonObject summary) => WriteJson(SummaryFile, summary);

    public void WriteTick(in TickRow row)
    {
        var line = _line.Clear();
        line.Append(Inv(row.Tick)).Append(',')
            .Append(Inv(row.TimeSteps)).Append(',')
            .Append(Inv(row.RegionCount)).Append(',')
            .Append(Inv(row.LargestRegionPlayers)).Append(',')
            .Append(Inv(row.LargestRegionShare)).Append(',')
            .Append(Inv(row.Merges)).Append(',')
            .Append(Inv(row.Splits)).Append(',')
            .Append(Inv(row.Fusions)).Append(',')
            .Append(Inv(row.SectionChurn)).Append(',')
            .Append(row.CrossPairsDeferred is { } d ? Inv(d) : string.Empty).Append(',')
            .Append(row.CrossPairsReadOnly is { } r ? Inv(r) : string.Empty).Append(',')
            .Append(Inv(row.BookkeepingMicros)).Append(',')
            .Append(Inv(row.SerializedMicros)).Append(',')
            .Append(row.OversizedFusion ? '1' : '0').Append(',')
            .Append(Inv(row.Violations)).Append(',')
            .Append(FormatHash(row.StateHash));
        _ticks.WriteLine(line);
    }

    public void WriteEvent(in RunEvent e)
    {
        var line = _line.Clear();
        line.Append(Inv(e.Tick)).Append(',')
            .Append(Inv(e.TimeSteps)).Append(',')
            .Append(e.Type).Append(',')
            .Append(Inv(e.RegionA)).Append(',')
            .Append(e.RegionB is { } b ? Inv(b) : string.Empty).Append(',')
            .Append(e.CauseClass?.ToString() ?? string.Empty).Append(',')
            .Append(e.CauseEntity is { } c ? Inv(c) : string.Empty).Append(',')
            .Append(e.Value is { } v ? Inv(v) : string.Empty).Append(',')
            .Append(Csv(e.Detail));
        _events.WriteLine(line);
    }

    /// <summary>Closes the files and removes the <c>_incomplete</c> marker: the folder now holds a finished run.</summary>
    public void Complete()
    {
        Dispose();
        File.Delete(System.IO.Path.Combine(Path, IncompleteMarker));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticks.Dispose();
        _events.Dispose();
    }

    /// <summary>A state hash as 16 lowercase hex digits.</summary>
    public static string FormatHash(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>A double in round-trip form, invariant culture.</summary>
    public static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Quotes a CSV field when it holds a comma, quote or line break.</summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private StreamWriter Open(string name) =>
        new(System.IO.Path.Combine(Path, name), append: false, new UTF8Encoding(false), bufferSize: 1 << 16) { NewLine = "\n" };

    private void WriteJson(string name, JsonObject value) =>
        File.WriteAllText(System.IO.Path.Combine(Path, name), value.ToJsonString(Indented) + "\n", new UTF8Encoding(false));
}
