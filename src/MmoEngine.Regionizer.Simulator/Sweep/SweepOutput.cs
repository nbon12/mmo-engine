using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Output;

namespace MmoEngine.Regionizer.Simulator.Sweep;

/// <summary>
/// <c>sweep.json</c>, the sweep's cell table and the index the memo generator reads
/// (contracts/config-and-cli.md, format version 1): the seeds, and per cell its swept values and its
/// runs with their folders and completed flags.
/// </summary>
internal sealed class SweepIndex(SweepPlan plan, string sweepDirectory)
{
    public const string FileName = "sweep.json";

    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Path { get; } = System.IO.Path.Combine(sweepDirectory, FileName);

    /// <summary>Rewrites the index atomically; a run is completed once its outcome is in and it completed.</summary>
    public void Write(SweepRunOutcome?[] outcomes) =>
        AtomicFile.WriteAllText(Path, Build(plan, outcomes).ToJsonString(Indented) + "\n");

    /// <summary>The index of <paramref name="plan"/>, with each run's completed flag from <paramref name="outcomes"/>.</summary>
    public static JsonObject Build(SweepPlan plan, SweepRunOutcome?[] outcomes)
    {
        var seeds = new JsonArray();
        for (var i = 0; i < plan.Seeds.Count; i++)
        {
            seeds.Add(new JsonObject { ["index"] = i, ["seed"] = plan.Seeds[i] });
        }

        var cells = new JsonArray();
        foreach (var cell in plan.Cells)
        {
            var parameters = new JsonObject();
            foreach (var a in cell.Assignments)
            {
                parameters[a.Path] = a.Value?.DeepClone();
            }

            var runs = new JsonArray();
            foreach (var run in plan.Runs.Where(r => r.Cell.Index == cell.Index))
            {
                var outcome = Volatile.Read(ref outcomes[run.Index]);
                runs.Add(new JsonObject
                {
                    ["seedIndex"] = run.SeedIndex,
                    ["seed"] = run.Seed,
                    ["folder"] = run.Folder,
                    ["completed"] = outcome?.Completed ?? false,
                });
            }

            cells.Add(new JsonObject { ["cellId"] = cell.CellId, ["parameters"] = parameters, ["runs"] = runs });
        }

        return new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["name"] = plan.Spec.Name,
            ["scenario"] = plan.Scenario,
            ["mode"] = plan.Spec.Mode.ToString(),
            ["sweepSeed"] = plan.Spec.SweepSeed,
            ["seeds"] = seeds,
            ["cells"] = cells,
        };
    }
}

/// <summary>
/// <c>cells.csv</c>: one row per cell and seed, with the swept values and the summary's
/// deterministic metrics, then the bookkeeping p99 (contracts/config-and-cli.md). A value the
/// summary lacks, or a run without a summary, leaves its field empty.
/// </summary>
public static class CellsCsv
{
    public const string FileName = "cells.csv";

    /// <summary>Metrics summarized at these percentiles, each a column <c>&lt;metric&gt;.&lt;stat&gt;</c>.</summary>
    public static IReadOnlyList<string> PercentileMetrics { get; } =
        ["regionCount", "largestRegionPlayers", "largestRegionShare", "fusedFightPairs", "sectionChurn"];

    public static IReadOnlyList<string> Stats { get; } = ["p50", "p95", "p99", "max"];

    /// <summary>The summary's <c>totals</c> columns.</summary>
    public static IReadOnlyList<string> Totals { get; } = ["merges", "absorptions", "splits", "fusions", "oversizedFusionTicks"];

    /// <summary>The last column, the only one that depends on wall-clock time.</summary>
    public const string BookkeepingColumn = "bookkeepingP99Micros";

    /// <summary>The header for a sweep over <paramref name="parameterPaths"/>.</summary>
    public static string Header(IEnumerable<string> parameterPaths)
    {
        ArgumentNullException.ThrowIfNull(parameterPaths);
        IEnumerable<string> columns =
        [
            "cellId", "seedIndex", "seed", "runId", "completed",
            .. parameterPaths,
            .. PercentileMetrics.SelectMany(m => Stats.Select(s => $"{m}.{s}")),
            .. Totals,
            BookkeepingColumn,
        ];
        return string.Join(',', columns.Select(RunFolder.Csv));
    }

    /// <summary>Writes the file atomically, one row per run in run order.</summary>
    public static void Write(string path, SweepPlan plan, IReadOnlyList<SweepRunOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcomes);
        var paths = plan.Spec.Parameters.Select(p => p.Path).ToArray();
        var text = new StringBuilder().Append(Header(paths)).Append('\n');
        foreach (var outcome in outcomes)
        {
            text.Append(Row(paths, outcome)).Append('\n');
        }

        AtomicFile.WriteAllText(path, text.ToString());
    }

    private static string Row(string[] paths, SweepRunOutcome outcome)
    {
        var run = outcome.Run;
        var summary = ReadSummary(outcome.FolderPath);
        var fields = new List<string>
        {
            run.Cell.CellId,
            run.SeedIndex.ToString(CultureInfo.InvariantCulture),
            run.Seed.ToString(CultureInfo.InvariantCulture),
            outcome.RunId,
            outcome.Completed ? "true" : "false",
        };
        foreach (var path in paths)
        {
            var assignment = run.Cell.Assignments.FirstOrDefault(a => a.Path == path);
            fields.Add(assignment is null ? string.Empty : Text(assignment.Value));
        }

        foreach (var metric in PercentileMetrics)
        {
            // contracts/run-output.md keeps fused fight pairs beside the other metrics, at the top level.
            var source = metric == "fusedFightPairs" ? Get(summary, metric) : Get(Get(summary, "metrics"), metric);
            fields.AddRange(Stats.Select(s => Text(Get(source, s))));
        }

        fields.AddRange(Totals.Select(t => Text(Get(Get(summary, "totals"), t))));
        fields.Add(Text(Get(Get(summary, "budget"), BookkeepingColumn)));
        return string.Join(',', fields.Select(RunFolder.Csv));
    }

    private static JsonNode? Get(JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    private static JsonNode? ReadSummary(string folder)
    {
        var file = Path.Combine(folder, RunFolder.SummaryFile);
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A JSON value as CSV text: numbers as written, strings as they are, nothing for null.</summary>
    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => value.ToJsonString(),
    };
}

/// <summary>Writes a file through a temporary file and a rename, so readers never see it half written.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
