using System.Globalization;
using System.Text.Json;

namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>A sweep result that cannot be read; its row is marked and the memo does not succeed.</summary>
internal sealed class UnreadableException(string message) : Exception(message);

/// <summary>One completed, clean run of a sweep cell: its seed, run ID and summary.</summary>
internal sealed record RunResult(int SeedIndex, ulong Seed, string RunId, JsonElement Summary);

/// <summary>One sweep cell: its swept parameters (JSON path to value) and its runs.</summary>
internal sealed record CellResult(string CellId, IReadOnlyList<KeyValuePair<string, JsonElement>> Parameters, IReadOnlyList<RunResult> Runs);

/// <summary>One sweep folder, read from <c>sweep.json</c> and each run's manifest and summary.</summary>
internal sealed record SweepResult(string Name, string Scenario, IReadOnlyList<CellResult> Cells);

/// <summary>
/// Reads sweep folders per contracts/config-and-cli.md (<c>sweep.json</c>) and contracts/run-output.md
/// (each run's <c>manifest.json</c> and <c>summary.json</c>). Strict: a run that is missing, incomplete,
/// stopped at a violation, or inconsistent with its manifest makes the sweep unreadable.
/// </summary>
internal static class SweepResults
{
    private const int FormatVersion = 1;

    public static SweepResult Read(string resultsDirectory, string sweepName)
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
                runs.Add(ReadRun(sweepDir, sweepName, cellId, run, seeds));
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

    private static RunResult ReadRun(string sweepDir, string sweepName, string cellId, JsonElement run, Dictionary<int, ulong> seeds)
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

        if (!Bool(run, "completed", where))
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

        using var manifest = ParseFile(Path.Combine(runDir, "manifest.json"), $"{runName}/manifest.json");
        RequireVersion(manifest.RootElement, $"{runName}/manifest.json");
        var runId = String(manifest.RootElement, "runId", $"{runName}/manifest.json");
        var manifestSeed = UInt64(manifest.RootElement, "seed", $"{runName}/manifest.json");
        if (manifestSeed != seed)
        {
            throw new UnreadableException($"{runName}/manifest.json: seed {manifestSeed} differs from the sweep's {seed}");
        }

        var summaryName = $"{runName}/summary.json";
        using var summary = ParseFile(Path.Combine(runDir, "summary.json"), summaryName);
        RequireVersion(summary.RootElement, summaryName);
        var summaryRunId = String(summary.RootElement, "runId", summaryName);
        if (summaryRunId != runId)
        {
            throw new UnreadableException($"{summaryName}: run ID {summaryRunId} differs from the manifest's {runId}");
        }

        if (!Bool(summary.RootElement, "completed", summaryName))
        {
            throw new UnreadableException($"{summaryName}: the run did not complete");
        }

        if (summary.RootElement.TryGetProperty("firstViolation", out var violation) && violation.ValueKind != JsonValueKind.Null)
        {
            throw new UnreadableException($"{summaryName}: the run stopped at a violation: {violation.GetRawText()}");
        }

        return new RunResult(seedIndex, seed, runId, summary.RootElement.Clone());
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
