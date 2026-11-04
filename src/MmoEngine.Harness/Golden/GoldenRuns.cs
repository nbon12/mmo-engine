using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Loading;

namespace MmoEngine.Harness.Golden;

/// <summary>The outcome of comparing a run with a golden run.</summary>
public enum GoldenOutcome
{
    Match,
    Diverged,
    NotComparable,
}

/// <summary>
/// A comparison's report (FR-019). On divergence it names the first divergent tick, both hashes, and the
/// deterministic metrics row from each side.
/// </summary>
public sealed record GoldenComparison(
    string Name,
    GoldenOutcome Outcome,
    string Message,
    long? FirstDivergentTick = null,
    string? GoldenHash = null,
    string? RunHash = null,
    IReadOnlyDictionary<string, string>? GoldenRow = null,
    IReadOnlyDictionary<string, string>? RunRow = null)
{
    /// <summary>The exit code for this outcome.</summary>
    public int ExitCode => Outcome switch
    {
        GoldenOutcome.Match => HarnessExitCodes.Ok,
        GoldenOutcome.Diverged => HarnessExitCodes.Diverged,
        _ => HarnessExitCodes.NotComparable,
    };

    /// <summary>The report as JSON.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["golden"] = Name,
            ["outcome"] = Outcome.ToString(),
            ["message"] = Message,
        };
        if (FirstDivergentTick is { } tick)
        {
            json["firstDivergentTick"] = tick;
            json["goldenHash"] = GoldenHash;
            json["runHash"] = RunHash;
            json["goldenRow"] = Row(GoldenRow);
            json["runRow"] = Row(RunRow);
        }

        return json;

        static JsonObject? Row(IReadOnlyDictionary<string, string>? row) =>
            row is null ? null : new JsonObject([.. row.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))]);
    }
}

/// <summary>
/// Golden runs (contracts/golden-run.md, research R6). A golden run is a folder holding <c>config.json</c>,
/// <c>golden.json</c> and the deterministic columns of the recorded <c>ticks.csv</c>. Recording is an
/// explicit command whose output is committed. CI only checks (FR-020).
/// </summary>
public static class GoldenRuns
{
    /// <summary>The golden-run format version.</summary>
    public const int FormatVersion = 1;

    /// <summary><c>ticks.csv</c> columns that are wall-clock measurements, so not part of a golden run.</summary>
    public static IReadOnlyList<string> WallClockColumns { get; } = ["bookkeepingMicros", "serializedMicros"];

    /// <summary>Records a golden run from a completed run folder and the config it was run with.</summary>
    public static JsonObject Record(string runFolder, string configPath, string goldenFolder, string name)
    {
        var run = RunFolderReader.Open(runFolder);
        var runnerClass = RunnerClass.FromManifest(run.Manifest);
        Directory.CreateDirectory(goldenFolder);
        File.Copy(configPath, Path.Combine(goldenFolder, "config.json"), overwrite: true);

        var (header, rows) = DeterministicTicks(Path.Combine(runFolder, "ticks.csv"));
        var csv = new StringBuilder();
        csv.Append(string.Join(',', header)).Append('\n');
        foreach (var row in rows)
        {
            csv.Append(string.Join(',', row)).Append('\n');
        }

        File.WriteAllText(Path.Combine(goldenFolder, "ticks.csv"), csv.ToString());
        var golden = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["name"] = name,
            ["runFormatVersion"] = RunFolderReader.SupportedFormatVersion,
            ["sourceRunId"] = run.RunId,
            ["configHash"] = run.Manifest["configHash"]?.GetValue<string>(),
            ["gitSha"] = run.Manifest["gitSha"]?.GetValue<string>(),
            ["ticks"] = rows.Count,
            ["runnerClass"] = new JsonObject { ["id"] = runnerClass.Id, ["fields"] = runnerClass.Fields.DeepClone() },
        };
        File.WriteAllText(Path.Combine(goldenFolder, "golden.json"), golden.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return golden;
    }

    /// <summary>Compares a run folder against a golden run, tick by tick.</summary>
    public static GoldenComparison Compare(string goldenFolder, string runFolder)
    {
        var goldenPath = Path.Combine(goldenFolder, "golden.json");
        if (!File.Exists(goldenPath))
        {
            throw new RunFolderException($"Golden folder {goldenFolder} has no golden.json.");
        }

        JsonObject golden;
        try
        {
            golden = JsonNode.Parse(File.ReadAllText(goldenPath)) as JsonObject ?? throw new RunFolderException($"{goldenPath} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RunFolderException($"{goldenPath} is not valid JSON: {ex.Message}");
        }

        var name = golden["name"]?.GetValue<string>() ?? Path.GetFileName(goldenFolder);
        var version = golden["formatVersion"]?.GetValue<int>();
        if (version != FormatVersion)
        {
            return new GoldenComparison(name, GoldenOutcome.NotComparable,
                $"golden format version {version?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}; this checker reads version {FormatVersion}");
        }

        var runVersion = RunFormatVersion(runFolder);
        if (runVersion is { } v && v != RunFolderReader.SupportedFormatVersion)
        {
            return new GoldenComparison(name, GoldenOutcome.NotComparable,
                string.Create(CultureInfo.InvariantCulture, $"the run's format version is {v}; golden runs are recorded from version {RunFolderReader.SupportedFormatVersion}"));
        }

        var run = RunFolderReader.Open(runFolder);

        var goldenClass = golden["runnerClass"]?["id"]?.GetValue<string>() ?? "(missing)";
        var runClass = RunnerClass.FromManifest(run.Manifest).Id;
        if (!string.Equals(goldenClass, runClass, StringComparison.Ordinal))
        {
            return new GoldenComparison(name, GoldenOutcome.NotComparable,
                $"recorded on runner class {goldenClass}, replayed on {runClass}; hashes are only compared within one class");
        }

        var (goldenHeader, goldenRows) = ReadCsv(Path.Combine(goldenFolder, "ticks.csv"));
        var (runHeader, runRows) = DeterministicTicks(Path.Combine(runFolder, "ticks.csv"));
        var goldenTick = Array.IndexOf(goldenHeader, "tick");
        var goldenHash = Array.IndexOf(goldenHeader, "stateHash");
        var runTick = Array.IndexOf(runHeader, "tick");
        var runHash = Array.IndexOf(runHeader, "stateHash");
        if (goldenTick < 0 || goldenHash < 0)
        {
            throw new RunFolderException($"{goldenFolder}/ticks.csv has no tick or stateHash column.");
        }

        var count = Math.Max(goldenRows.Count, runRows.Count);
        for (var i = 0; i < count; i++)
        {
            var g = i < goldenRows.Count ? goldenRows[i] : null;
            var r = i < runRows.Count ? runRows[i] : null;
            var gHash = g?[goldenHash];
            var rHash = r?[runHash];
            if (g is not null && r is not null && gHash == rHash && g[goldenTick] == r[runTick])
            {
                continue;
            }

            var tick = long.Parse((g ?? r)![g is not null ? goldenTick : runTick], CultureInfo.InvariantCulture);
            var reason = g is null ? "the run has more ticks than the golden run"
                : r is null ? "the run ended before the golden run"
                : $"state hash differs at tick {tick}";
            return new GoldenComparison(name, GoldenOutcome.Diverged, $"first divergent tick {tick}: {reason}", tick, gHash, rHash,
                g is null ? null : Zip(goldenHeader, g), r is null ? null : Zip(runHeader, r));
        }

        return new GoldenComparison(name, GoldenOutcome.Match,
            string.Create(CultureInfo.InvariantCulture, $"all {goldenRows.Count} ticks match on runner class {runClass}"));
    }

    private static int? RunFormatVersion(string runFolder)
    {
        var path = Path.Combine(runFolder, "manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["formatVersion"]?.GetValue<int>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Zip(string[] header, string[] row) =>
        header.Zip(row).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);

    private static (string[] Header, List<string[]> Rows) DeterministicTicks(string path)
    {
        var (header, rows) = ReadCsv(path);
        var keep = header.Select((h, i) => (h, i)).Where(p => !WallClockColumns.Contains(p.h)).Select(p => p.i).ToArray();
        return ([.. keep.Select(i => header[i])], [.. rows.Select(r => keep.Select(i => r[i]).ToArray())]);
    }

    private static (string[] Header, List<string[]> Rows) ReadCsv(string path)
    {
        if (!File.Exists(path))
        {
            throw new RunFolderException($"{path} not found.");
        }

        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            throw new RunFolderException($"{path} is empty.");
        }

        return (RunFolderReader.SplitCsv(lines[0]), [.. lines.Skip(1).Select(RunFolderReader.SplitCsv)]);
    }
}
