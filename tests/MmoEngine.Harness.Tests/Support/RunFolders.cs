using System.Text.Json.Nodes;

namespace MmoEngine.Harness.Tests.Support;

/// <summary>
/// Copies of the recorded fixtures (a 60-tick Baseline run and a two-cell sweep, made with
/// <c>regionizer-sim</c> from <c>Fixtures/runs/tiny-config.json</c>) into unique temp folders, with unique
/// run IDs, so tests never share files or store rows.
/// </summary>
public static class RunFolders
{
    public static string FixturesRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>A unique ID with a readable prefix.</summary>
    public static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>A unique, empty temp folder.</summary>
    public static string TempFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "mmo-harness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A copy of the clean tiny run with a unique run ID; returns (folder, runId).</summary>
    public static (string Folder, string RunId) CleanRun()
    {
        var folder = Path.Combine(TempFolder(), "run");
        CopyDirectory(Path.Combine(FixturesRoot, "runs", "tiny"), folder);
        var runId = UniqueId("tiny");
        EditManifest(folder, m => m["runId"] = runId);
        return (folder, runId);
    }

    /// <summary>A copy of the tiny sweep with a unique sweep name and unique run IDs; returns (folder, sweep name).</summary>
    public static (string Folder, string Sweep) Sweep()
    {
        var folder = Path.Combine(TempFolder(), "sweep");
        CopyDirectory(Path.Combine(FixturesRoot, "sweeps", "tiny-radius"), folder);
        var sweep = UniqueId("tiny-radius");
        var indexPath = Path.Combine(folder, "sweep.json");
        var index = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();
        index["name"] = sweep;
        File.WriteAllText(indexPath, index.ToJsonString());
        var cellsPath = Path.Combine(folder, "cells.csv");
        var lines = File.ReadAllLines(cellsPath);
        var runIdColumn = Array.IndexOf(lines[0].Split(','), "runId");
        foreach (var manifestPath in Directory.EnumerateFiles(folder, "manifest.json", SearchOption.AllDirectories))
        {
            var runFolder = Path.GetDirectoryName(manifestPath)!;
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var oldId = manifest["runId"]!.GetValue<string>();
            var newId = $"{sweep}-{oldId}";
            manifest["runId"] = newId;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            for (var i = 1; i < lines.Length; i++)
            {
                var fields = lines[i].Split(',');
                if (fields[runIdColumn] == oldId)
                {
                    fields[runIdColumn] = newId;
                    lines[i] = string.Join(',', fields);
                }
            }
        }

        File.WriteAllLines(cellsPath, lines);
        return (folder, sweep);
    }

    /// <summary>Edits a run folder's manifest.json.</summary>
    public static void EditManifest(string folder, Action<JsonObject> edit)
    {
        var path = Path.Combine(folder, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    /// <summary>
    /// Seeds one invariant violation into a run folder, as the simulator records one: the last ticks.csv row
    /// gets violations = 1, and events.csv gets a Violation row at that tick.
    /// </summary>
    public static void SeedViolation(string folder)
    {
        var ticksPath = Path.Combine(folder, "ticks.csv");
        var lines = File.ReadAllLines(ticksPath);
        var header = lines[0].Split(',');
        var violations = Array.IndexOf(header, "violations");
        var fields = lines[^1].Split(',');
        fields[violations] = "1";
        lines[^1] = string.Join(',', fields);
        File.WriteAllLines(ticksPath, lines);
        var tick = fields[Array.IndexOf(header, "tick")];
        var steps = fields[Array.IndexOf(header, "timeSteps")];
        File.AppendAllText(Path.Combine(folder, "events.csv"), $"{tick},{steps},Violation,,,,,,\"Rule1: section 3 owned by two regions\"\n");
    }

    /// <summary>Changes the state hash of every ticks.csv row from <paramref name="fromTick"/> on.</summary>
    public static void CorruptHashesFrom(string folder, long fromTick)
    {
        var ticksPath = Path.Combine(folder, "ticks.csv");
        var lines = File.ReadAllLines(ticksPath);
        var header = lines[0].Split(',');
        var tickColumn = Array.IndexOf(header, "tick");
        var hashColumn = Array.IndexOf(header, "stateHash");
        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split(',');
            if (long.Parse(fields[tickColumn], System.Globalization.CultureInfo.InvariantCulture) >= fromTick)
            {
                fields[hashColumn] = "0123456789abcdef";
                lines[i] = string.Join(',', fields);
            }
        }

        File.WriteAllLines(ticksPath, lines);
    }

    /// <summary>Counts the data rows of a CSV file.</summary>
    public static int CsvRows(string path) => File.ReadAllLines(path).Skip(1).Count(l => l.Length > 0);

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
