using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Store;
using Serilog;

namespace MmoEngine.Harness.Loading;

/// <summary>The outcome of loading one run.</summary>
public sealed record RunLoadResult(string RunId, bool AlreadyLoaded, long TickRows, long EventRows);

/// <summary>The outcome of loading a sweep.</summary>
public sealed record SweepLoadResult(string Sweep, int CellRows, IReadOnlyList<RunLoadResult> Runs, int RunFoldersMissing);

/// <summary>
/// Loads run folders and sweeps into the store (US1, FR-004 to FR-008). A run's <c>runs</c> row is written
/// last, with <c>loaded = 1</c>. That row is the commit marker: a load that finds it does nothing, and a
/// load that doesn't find it clears any partial rows first (research R3).
/// </summary>
public sealed class RunLoader(IHarnessStore store, ILogger logger)
{
    /// <summary>Loads one run folder. Throws <see cref="RunFolderException"/> for a folder it refuses, before writing anything.</summary>
    public async Task<RunLoadResult> LoadRunAsync(string folder, string sweep = "", string cellId = "", CancellationToken cancellationToken = default)
    {
        var reader = RunFolderReader.Open(folder);

        // Parse everything before touching the store, so a malformed file loads nothing.
        var ticks = reader.TickRows().ToList();
        var events = reader.EventRows().ToList();
        var aggregates = reader.AggregateRows().ToList();
        var runId = reader.RunId;

        var existing = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (existing is { Loaded: true })
        {
            logger.Information("Run {RunId} is already loaded; nothing to do", runId);
            return new RunLoadResult(runId, true, 0, 0);
        }

        await store.DeleteRawRowsAsync(runId, cancellationToken).ConfigureAwait(false);
        var tickRows = await store.InsertAsync("sim_ticks", RunFolderReader.TickTableColumns, ticks, cancellationToken).ConfigureAwait(false);
        var eventRows = await store.InsertAsync("sim_events", RunFolderReader.EventTableColumns, events, cancellationToken).ConfigureAwait(false);
        await store.InsertAsync("run_aggregates", RunFolderReader.AggregateTableColumns, aggregates, cancellationToken).ConfigureAwait(false);

        var runnerClass = RunnerClass.FromManifest(reader.Manifest).Id;
        await store.UpsertRunAsync(reader.ToRunRow(runnerClass, sweep, cellId) with { Loaded = true }, cancellationToken).ConfigureAwait(false);
        logger.Information("Loaded run {RunId}: {Ticks} tick rows, {Events} event rows", runId, tickRows, eventRows);
        return new RunLoadResult(runId, false, tickRows, eventRows);
    }

    /// <summary>
    /// Loads a sweep folder: every row of <c>cells.csv</c> into <c>sweep_cells</c>, and every completed run
    /// that <c>sweep.json</c> lists and whose folder exists (FR-007).
    /// </summary>
    public async Task<SweepLoadResult> LoadSweepAsync(string folder, CancellationToken cancellationToken = default)
    {
        var index = SweepIndex.Open(folder);
        var cellRows = index.CellRows().ToList();
        await store.DeleteSweepCellsAsync(index.Name, cancellationToken).ConfigureAwait(false);
        await store.InsertAsync("sweep_cells", SweepIndex.CellTableColumns, cellRows, cancellationToken).ConfigureAwait(false);

        var runs = new List<RunLoadResult>();
        var missing = 0;
        foreach (var run in index.Runs.Where(r => r.Completed))
        {
            var runFolder = Path.Combine(folder, run.Folder);
            if (!Directory.Exists(runFolder))
            {
                missing++;
                continue;
            }

            runs.Add(await LoadRunAsync(runFolder, index.Name, run.CellId, cancellationToken).ConfigureAwait(false));
        }

        logger.Information("Loaded sweep {Sweep}: {Cells} cell rows, {Runs} runs, {Missing} run folders not present",
            index.Name, cellRows.Count, runs.Count, missing);
        return new SweepLoadResult(index.Name, cellRows.Count, runs, missing);
    }
}

/// <summary>A sweep's <c>sweep.json</c> index and <c>cells.csv</c> (specs/013-regionizer-simulator/contracts/config-and-cli.md).</summary>
public sealed class SweepIndex
{
    private static readonly (string Column, string Header)[] SummaryColumns =
    [
        ("region_count_p99", "regionCount.p99"),
        ("largest_region_players_p99", "largestRegionPlayers.p99"),
        ("largest_region_share_p99", "largestRegionShare.p99"),
        ("fused_fight_pairs_p99", "fusedFightPairs.p99"),
        ("section_churn_p99", "sectionChurn.p99"),
        ("merges", "merges"),
        ("absorptions", "absorptions"),
        ("splits", "splits"),
        ("fusions", "fusions"),
        ("oversized_fusion_ticks", "oversizedFusionTicks"),
        ("bookkeeping_p99_micros", "bookkeepingP99Micros"),
    ];

    private readonly Dictionary<string, string> parametersByCell;

    private SweepIndex(string folder, string name, string scenario, IReadOnlyList<SweepRun> runs, Dictionary<string, string> parametersByCell)
    {
        Folder = folder;
        Name = name;
        Scenario = scenario;
        Runs = runs;
        this.parametersByCell = parametersByCell;
    }

    /// <summary>The <c>sweep_cells</c> column names.</summary>
    public static IReadOnlyList<string> CellTableColumns { get; } =
        ["sweep", "scenario", "cell_id", "seed_index", "seed", "run_id", "completed", "parameters", .. SummaryColumns.Select(c => c.Column), "summary"];

    public string Folder { get; }

    public string Name { get; }

    public string Scenario { get; }

    /// <summary>Every run the index lists.</summary>
    public IReadOnlyList<SweepRun> Runs { get; }

    /// <summary>Opens a sweep folder, refusing one without <c>sweep.json</c> or with an unknown format version.</summary>
    public static SweepIndex Open(string folder)
    {
        var path = Path.Combine(folder, "sweep.json");
        if (!File.Exists(path))
        {
            throw new RunFolderException($"Sweep folder {folder} has no sweep.json.");
        }

        JsonObject index;
        try
        {
            index = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new RunFolderException($"{path} is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RunFolderException($"{path} is not valid JSON: {ex.Message}");
        }

        var version = index["formatVersion"]?.GetValue<int>();
        if (version != RunFolderReader.SupportedFormatVersion)
        {
            throw new RunFolderException($"{path} has formatVersion {version?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}; only version 1 is read.");
        }

        var runs = new List<SweepRun>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in index["cells"]?.AsArray() ?? [])
        {
            var cellId = cell!["cellId"]!.GetValue<string>();
            parameters[cellId] = cell["parameters"]?.ToJsonString() ?? "{}";
            foreach (var run in cell["runs"]?.AsArray() ?? [])
            {
                runs.Add(new SweepRun(cellId, run!["seedIndex"]!.GetValue<int>(), run["folder"]!.GetValue<string>(), run["completed"]?.GetValue<bool>() ?? false));
            }
        }

        return new SweepIndex(folder, index["name"]!.GetValue<string>(), index["scenario"]?.GetValue<string>() ?? "", runs, parameters);
    }

    /// <summary>The <c>sweep_cells</c> rows from <c>cells.csv</c>, or none if the file is absent.</summary>
    public IEnumerable<object?[]> CellRows()
    {
        var path = Path.Combine(Folder, "cells.csv");
        if (!File.Exists(path))
        {
            yield break;
        }

        string[] fixedHeaders = ["cellId", "seedIndex", "seed", "runId", "completed"];
        string[]? header = null;
        using (var reader = new StreamReader(path))
        {
            header = RunFolderReader.SplitCsv(reader.ReadLine() ?? "");
        }

        var optional = SummaryColumns.Select(c => Array.IndexOf(header, c.Header)).ToArray();
        foreach (var (line, values, index) in RunFolderReader.Csv(path, fixedHeaders))
        {
            var cellId = values[index[0]];
            var summary = new JsonObject();
            for (var i = 0; i < header.Length; i++)
            {
                summary[header[i]] = values[i];
            }

            var row = new List<object?>
            {
                Name,
                Scenario,
                cellId,
                (uint)RunFolderReader.Convert(values[index[1]], RunFolderReader.ColumnKind.UInt32, "cells.csv", line, "seedIndex")!,
                (ulong)RunFolderReader.Convert(values[index[2]], RunFolderReader.ColumnKind.UInt64, "cells.csv", line, "seed")!,
                values[index[3]],
                (byte)(string.Equals(values[index[4]], "true", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                parametersByCell.GetValueOrDefault(cellId, "{}"),
            };
            for (var i = 0; i < SummaryColumns.Length; i++)
            {
                var text = optional[i] >= 0 ? values[optional[i]] : "";
                var number = text.Length == 0 ? 0d : (double)RunFolderReader.Convert(text, RunFolderReader.ColumnKind.Float64, "cells.csv", line, SummaryColumns[i].Header)!;
                row.Add(SummaryColumns[i].Column is "merges" or "absorptions" or "splits" or "fusions" or "oversized_fusion_ticks" ? (ulong)number : number);
            }

            row.Add(summary.ToJsonString());
            yield return [.. row];
        }
    }
}

/// <summary>One run listed in a sweep index.</summary>
public sealed record SweepRun(string CellId, int SeedIndex, string Folder, bool Completed);
