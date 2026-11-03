using System.Globalization;
using System.Text.Json;

namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>A sweep result that cannot be read; its row is marked and the memo does not succeed.</summary>
internal sealed class UnreadableException(string message) : Exception(message);

/// <summary>One run of a sweep cell: its seed, run ID, summary and what its manifest and the sweep's cells.csv record.</summary>
/// <param name="Population">The manifest's <c>parameters.population.total</c>, or null when it records none.</param>
/// <param name="Scheduling">The manifest's <c>scheduling</c> mode, or null when it records none.</param>
/// <param name="StoppedBy">The kind of the violation or timer fault the run stopped at, or null for a clean run.</param>
/// <param name="CellsRow">The run's row of the sweep's <c>cells.csv</c> (column to text), or null when there is none.</param>
internal sealed record RunResult(
    int SeedIndex,
    ulong Seed,
    string RunId,
    JsonElement Summary,
    int? Population,
    string? Scheduling,
    string? StoppedBy,
    IReadOnlyDictionary<string, string>? CellsRow);

/// <summary>One sweep cell: its swept parameters (JSON path to value) and its runs.</summary>
internal sealed record CellResult(string CellId, IReadOnlyList<KeyValuePair<string, JsonElement>> Parameters, IReadOnlyList<RunResult> Runs);

/// <summary>One sweep folder, read from <c>sweep.json</c> and each run's manifest and summary.</summary>
internal sealed record SweepResult(string Name, string Scenario, IReadOnlyList<CellResult> Cells);

/// <summary>
/// Reads sweep folders per contracts/config-and-cli.md (<c>sweep.json</c>, <c>cells.csv</c>) and
/// contracts/run-output.md (each run's <c>manifest.json</c> and <c>summary.json</c>). Strict: a run that
/// is missing, incomplete or inconsistent with its manifest makes the sweep unreadable, and so does a run
/// that stopped at a violation or timer fault unless the caller counts those (measured options).
/// </summary>
internal static class SweepResults
{
    /// <summary>The sweep's cell table, one row per cell and seed (contracts/config-and-cli.md).</summary>
    public const string CellsFile = "cells.csv";

    private const int FormatVersion = 1;

    /// <summary>Reads one sweep folder.</summary>
    /// <param name="allowStoppedRuns">
    /// Keep runs that stopped at a violation or timer fault (with <see cref="RunResult.StoppedBy"/> set)
    /// instead of making the sweep unreadable. An interrupted run is unreadable either way.
    /// </param>
    public static SweepResult Read(string resultsDirectory, string sweepName, bool allowStoppedRuns = false)
    {
        var sweepDir = Path.GetFullPath(Path.Combine(resultsDirectory, sweepName));
        using var sweep = ParseFile(Path.Combine(sweepDir, "sweep.json"), $"{sweepName}/sweep.json");
        var root = sweep.RootElement;
        var where = $"{sweepName}/sweep.json";
        RequireVersion(root, where);
        var scenario = String(root, "scenario", where);

        var seeds = new Dictionary<int, ulong>();
        foreach (var seed in Array(root, "seeds", where))
        {
            seeds[Int(seed, "index", where)] = UInt64(seed, "seed", where);
        }

        var cellsCsv = ReadCellsCsv(Path.Combine(sweepDir, CellsFile));
        var cells = new List<CellResult>();
        foreach (var cell in Array(root, "cells", where))
        {
            var cellId = String(cell, "cellId", where);
            var parameters = Property(cell, "parameters", where, JsonValueKind.Object)
                .EnumerateObject()
                .Select(p => new KeyValuePair<string, JsonElement>(p.Name, p.Value.Clone()))
                .ToList();
            var runs = new List<RunResult>();
            foreach (var run in Array(cell, "runs", $"{where} cell {cellId}"))
            {
                runs.Add(ReadRun(sweepDir, sweepName, cellId, run, seeds, cellsCsv, allowStoppedRuns));
            }

            if (runs.Count == 0)
            {
                throw new UnreadableException($"{where}: cell {cellId} has no runs");
            }

            cells.Add(new CellResult(cellId, parameters, [.. runs.OrderBy(r => r.SeedIndex)]));
        }

        if (cells.Count == 0)
        {
            throw new UnreadableException($"{where}: no cells");
        }

        return new SweepResult(sweepName, scenario, cells);
    }

    /// <summary>
    /// A parameter value as text: numbers in the invariant culture, strings as they are, booleans as true or
    /// false, anything else as compact JSON.
    /// </summary>
    public static string Label(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => Number(value.GetDouble()),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => JsonSerializer.Serialize(value),
    };

    public static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static RunResult ReadRun(
        string sweepDir,
        string sweepName,
        string cellId,
        JsonElement run,
        Dictionary<int, ulong> seeds,
        Dictionary<(string CellId, int SeedIndex), IReadOnlyDictionary<string, string>> cellsCsv,
        bool allowStoppedRuns)
    {
        var where = $"{sweepName}/sweep.json cell {cellId}";
        var folder = String(run, "folder", where);
        var seedIndex = Int(run, "seedIndex", where);
        var seed = UInt64(run, "seed", where);
        var runName = $"{sweepName}/{folder}";
        if (seeds.TryGetValue(seedIndex, out var listed) && listed != seed)
        {
            throw new UnreadableException($"{runName}: seed {seed} differs from the sweep's seed {listed} for index {seedIndex}");
        }

        var listedCompleted = Bool(run, "completed", where);
        if (!listedCompleted && !allowStoppedRuns)
        {
            throw new UnreadableException($"{runName}: the sweep lists the run as not completed");
        }

        var runDir = Path.GetFullPath(Path.Combine(sweepDir, folder));
        if (!runDir.StartsWith(sweepDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnreadableException($"{runName}: the run folder is outside the sweep folder");
        }

        if (File.Exists(Path.Combine(runDir, "_incomplete")))
        {
            throw new UnreadableException($"{runName}: the run folder is still marked _incomplete");
        }

        var manifestName = $"{runName}/manifest.json";
        using var manifest = ParseFile(Path.Combine(runDir, "manifest.json"), manifestName);
        RequireVersion(manifest.RootElement, manifestName);
        var runId = String(manifest.RootElement, "runId", manifestName);
        var manifestSeed = UInt64(manifest.RootElement, "seed", manifestName);
        if (manifestSeed != seed)
        {
            throw new UnreadableException($"{manifestName}: seed {manifestSeed} differs from the sweep's {seed}");
        }

        var summaryName = $"{runName}/summary.json";
        using var summary = ParseFile(Path.Combine(runDir, "summary.json"), summaryName);
        RequireVersion(summary.RootElement, summaryName);
        var summaryRunId = String(summary.RootElement, "runId", summaryName);
        if (summaryRunId != runId)
        {
            throw new UnreadableException($"{summaryName}: run ID {summaryRunId} differs from the manifest's {runId}");
        }

        var completed = Bool(summary.RootElement, "completed", summaryName);
        string? stoppedBy = null;
        if (summary.RootElement.TryGetProperty("firstViolation", out var violation) && violation.ValueKind != JsonValueKind.Null)
        {
            if (!allowStoppedRuns)
            {
                throw new UnreadableException($"{summaryName}: the run stopped at a violation: {violation.GetRawText()}");
            }

            stoppedBy = violation.ValueKind == JsonValueKind.Object
                && violation.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                ? kind.GetString()
                : "Violation";
        }
        else if (!completed || !listedCompleted)
        {
            // A run that did not finish and recorded no violation was interrupted: its numbers are partial.
            throw new UnreadableException($"{summaryName}: the run did not complete");
        }

        return new RunResult(
            seedIndex,
            seed,
            runId,
            summary.RootElement.Clone(),
            ManifestPopulation(manifest.RootElement, manifestName),
            manifest.RootElement.TryGetProperty("scheduling", out var scheduling) && scheduling.ValueKind == JsonValueKind.String ? scheduling.GetString() : null,
            stoppedBy,
            cellsCsv.GetValueOrDefault((cellId, seedIndex)));
    }

    /// <summary>The manifest's <c>parameters.population.total</c>, or null when it records none.</summary>
    private static int? ManifestPopulation(JsonElement manifest, string manifestName)
    {
        if (!manifest.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("population", out var population) || population.ValueKind != JsonValueKind.Object
            || !population.TryGetProperty("total", out var total))
        {
            return null;
        }

        return total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var value)
            ? value
            : throw new UnreadableException($"{manifestName}: 'parameters.population.total' is not a whole number");
    }

    /// <summary>
    /// The sweep's <c>cells.csv</c> as rows keyed by cell ID and seed index. The file is optional evidence
    /// (absorptions): a missing or unparseable file, or one without <c>cellId</c> and <c>seedIndex</c>
    /// columns, gives no rows.
    /// </summary>
    private static Dictionary<(string CellId, int SeedIndex), IReadOnlyDictionary<string, string>> ReadCellsCsv(string path)
    {
        var rows = new Dictionary<(string, int), IReadOnlyDictionary<string, string>>();
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return rows;
        }

        if (lines.Length == 0)
        {
            return rows;
        }

        var header = lines[0].Split(',');
        var cellColumn = System.Array.IndexOf(header, "cellId");
        var seedColumn = System.Array.IndexOf(header, "seedIndex");
        if (cellColumn < 0 || seedColumn < 0)
        {
            return rows;
        }

        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(',');
            if (fields.Length != header.Length
                || !int.TryParse(fields[seedColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seedIndex))
            {
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++)
            {
                row[header[i]] = fields[i];
            }

            rows[(fields[cellColumn], seedIndex)] = row;
        }

        return rows;
    }

    private static JsonDocument ParseFile(string path, string name)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UnreadableException($"{name}: cannot be read ({e.GetType().Name})");
        }
        catch (JsonException e)
        {
            throw new UnreadableException($"{name}: not valid JSON ({e.Message})");
        }
    }

    private static void RequireVersion(JsonElement root, string where)
    {
        var version = Int(root, "formatVersion", where);
        if (version != FormatVersion)
        {
            throw new UnreadableException($"{where}: formatVersion {version} is not supported (expected {FormatVersion})");
        }
    }

    private static JsonElement Property(JsonElement element, string name, string where, JsonValueKind? kind = null)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            throw new UnreadableException($"{where}: '{name}' is missing");
        }

        return kind is null || value.ValueKind == kind
            ? value
            : throw new UnreadableException($"{where}: '{name}' has the wrong type ({value.ValueKind})");
    }

    private static bool Bool(JsonElement element, string name, string where) => Property(element, name, where).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        var other => throw new UnreadableException($"{where}: '{name}' has the wrong type ({other})"),
    };

    private static JsonElement.ArrayEnumerator Array(JsonElement element, string name, string where) =>
        Property(element, name, where, JsonValueKind.Array).EnumerateArray();

    private static string String(JsonElement element, string name, string where) =>
        Property(element, name, where, JsonValueKind.String).GetString()!;

    private static int Int(JsonElement element, string name, string where) =>
        Property(element, name, where, JsonValueKind.Number).TryGetInt32(out var value)
            ? value
            : throw new UnreadableException($"{where}: '{name}' is not a whole number");

    private static ulong UInt64(JsonElement element, string name, string where) =>
        Property(element, name, where, JsonValueKind.Number).TryGetUInt64(out var value)
            ? value
            : throw new UnreadableException($"{where}: '{name}' is not a 64-bit unsigned seed");
}
