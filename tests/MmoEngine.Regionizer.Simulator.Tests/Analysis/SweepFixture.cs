using System.Text.Json;
using System.Text.Json.Nodes;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// Writes a synthetic sweep folder that follows contracts/config-and-cli.md (<c>sweep.json</c>,
/// <c>cells.csv</c>) and contracts/run-output.md (each run's <c>manifest.json</c>, <c>ticks.csv</c>,
/// <c>events.csv</c> and <c>summary.json</c>). Every value is explicit or a contract-shaped zero; the
/// fixture holds no memo rule.
/// </summary>
internal sealed class SweepFixture(string name, string scenario, string mode = "OneAtATime")
{
    /// <summary>The seeds every cell runs, by seed index (common random numbers across cells).</summary>
    public static readonly ulong[] Seeds = [11, 22];

    public const int Ticks = 1000;

    private const string TicksHeader = "tick,timeSteps,regionCount,largestRegionPlayers,largestRegionShare,merges,splits,fusions,"
        + "sectionChurn,crossPairsDeferred,crossPairsReadOnly,bookkeepingMicros,serializedMicros,oversizedFusion,violations,stateHash";

    private const string EventsHeader = "tick,timeSteps,type,regionA,regionB,causeClass,causeEntity,value,detail";

    private static readonly string[] Classes = ["Infantry", "GroundVehicle", "Aircraft", "Traveller", "CapturePoint", "HeavyProjectile"];

    private readonly List<(string CellId, JsonObject Parameters, Action<JsonObject>[] Seeds)> _cells = [];

    public string Name { get; } = name;

    /// <summary>The run ID of a cell's seed: the folder's names, so tests can list them.</summary>
    public static string RunId(string sweep, string cellId, int seedIndex) => $"{sweep}-{cellId}-seed{seedIndex}";

    /// <summary>Summary fields for one seed: each path is set (created as needed), or removed when the value is null.</summary>
    public static Action<JsonObject> S(params (string Path, JsonNode? Value)[] fields) => summary =>
    {
        foreach (var (path, value) in fields)
        {
            Set(summary, path, value);
        }
    };

    /// <summary>A cell's swept parameters, JSON path to value.</summary>
    public static JsonObject P(params (string Path, JsonNode? Value)[] parameters)
    {
        var json = new JsonObject();
        foreach (var (path, value) in parameters)
        {
            json[path] = value;
        }

        return json;
    }

    /// <summary>Adds a cell; one summary setter per seed index.</summary>
    public SweepFixture Cell(JsonObject parameters, params Action<JsonObject>[] seeds)
    {
        _cells.Add(($"c{_cells.Count:000}", parameters, seeds));
        return this;
    }

    /// <summary>The run IDs of every cell and seed, in cell then seed order.</summary>
    public IEnumerable<string> RunIds() =>
        _cells.SelectMany(c => Enumerable.Range(0, c.Seeds.Length).Select(i => RunId(Name, c.CellId, i)));

    /// <summary>Writes <c>&lt;results&gt;/&lt;name&gt;/</c> with sweep.json, cells.csv and every run folder.</summary>
    public void Write(string results)
    {
        var sweepDir = Path.Combine(results, Name);
        Directory.CreateDirectory(sweepDir);
        var cells = new JsonArray();
        var csv = new List<string> { "cellId,seedIndex,seed,runId,bookkeepingP99Micros" };
        foreach (var (cellId, parameters, seeds) in _cells)
        {
            // A sweep that steps the scenario itself (a grid over scenarios) runs each cell's own scenario.
            var cellScenario = parameters["scenario"] is JsonValue swept ? swept.GetValue<string>() : scenario;
            var runs = new JsonArray();
            for (var i = 0; i < seeds.Length; i++)
            {
                var folder = $"{cellId}/seed-{i}";
                var runId = RunId(Name, cellId, i);
                var summary = Summary(runId);
                seeds[i](summary);
                WriteRun(Path.Combine(sweepDir, cellId, $"seed-{i}"), runId, cellScenario, Seeds[i], summary);
                runs.Add(new JsonObject { ["seedIndex"] = i, ["seed"] = Seeds[i], ["folder"] = folder, ["completed"] = true });
                csv.Add($"{cellId},{i},{Seeds[i]},{runId},{summary["budget"]!["bookkeepingP99Micros"]}");
            }

            cells.Add(new JsonObject { ["cellId"] = cellId, ["parameters"] = parameters.DeepClone(), ["runs"] = runs });
        }

        var sweep = new JsonObject
        {
            ["formatVersion"] = 1,
            ["name"] = Name,
            ["scenario"] = scenario,
            ["mode"] = mode,
            ["sweepSeed"] = 20261103,
            ["seeds"] = new JsonArray([.. Seeds.Select((seed, i) => (JsonNode)new JsonObject { ["index"] = i, ["seed"] = seed })]),
            ["cells"] = cells,
        };
        File.WriteAllText(Path.Combine(sweepDir, "sweep.json"), sweep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(sweepDir, "cells.csv"), csv);
    }

    /// <summary>Sets a dotted path in a JSON object, creating objects on the way; a null value removes the last property.</summary>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        foreach (var segment in segments[..^1])
        {
            if (current[segment] is not JsonObject next)
            {
                next = [];
                current[segment] = next;
            }

            current = next;
        }

        if (value is null)
        {
            current.Remove(segments[^1]);
        }
        else
        {
            current[segments[^1]] = value;
        }
    }

    private static void WriteRun(string runDir, string runId, string scenario, ulong seed, JsonObject summary)
    {
        Directory.CreateDirectory(runDir);
        var manifest = new JsonObject
        {
            ["formatVersion"] = 1,
            ["runId"] = runId,
            ["kind"] = "simulator",
            ["gitSha"] = "unknown",
            ["configHash"] = "0123456789abcdef",
            ["scenario"] = scenario,
            ["seed"] = seed,
            ["scheduling"] = "Lockstep",
            ["parameters"] = new JsonObject(),
            ["runtime"] = new JsonObject
            {
                ["framework"] = ".NET 9.0.0",
                ["os"] = "test",
                ["architecture"] = "Arm64",
                ["processorCount"] = 1,
                ["machineName"] = "test",
                ["hardwareIntrinsics"] = new JsonObject(),
                ["vectorIsHardwareAccelerated"] = true,
            },
            ["startedUtc"] = "2026-11-03T13:00:00Z",
            ["finishedUtc"] = "2026-11-03T13:01:00Z",
            ["ticks"] = Ticks,
            ["metricsEveryTicks"] = 1,
            ["islandCheckEveryTicks"] = 1,
            ["crossRegionPairsEveryTicks"] = 30,
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(runDir, "manifest.json"), manifest.ToJsonString(options));
        File.WriteAllText(Path.Combine(runDir, "summary.json"), summary.ToJsonString(options));
        File.WriteAllLines(Path.Combine(runDir, "ticks.csv"), [TicksHeader]);
        File.WriteAllLines(Path.Combine(runDir, "events.csv"), [EventsHeader]);
    }

    /// <summary>A summary.json with every field of the contract, all zero, for a clean completed run.</summary>
    private static JsonObject Summary(string runId)
    {
        static JsonObject Percentiles() => new() { ["p50"] = 0, ["p95"] = 0, ["p99"] = 0, ["max"] = 0, ["mean"] = 0 };
        static JsonObject ByClass() => new(Classes.Select(c => new KeyValuePair<string, JsonNode?>(c, 0.0)));
        var metrics = new JsonObject();
        foreach (var metric in new[]
        {
            "regionCount", "largestRegionPlayers", "largestRegionShare", "sectionChurn", "crossPairsDeferred",
            "crossPairsReadOnly", "bookkeepingMicros", "serializedMicros", "regionLifetimeSteps", "splitLatencyTicks",
        })
        {
            metrics[metric] = Percentiles();
        }

        return new JsonObject
        {
            ["formatVersion"] = 1,
            ["runId"] = runId,
            ["completed"] = true,
            ["ticks"] = Ticks,
            ["firstViolation"] = null,
            ["metrics"] = metrics,
            ["rates"] = new JsonObject
            {
                ["mergesPerSecondByClass"] = ByClass(),
                ["splitsPerSecond"] = 0.0,
                ["fusionsPerSecondByClass"] = ByClass(),
            },
            ["totals"] = new JsonObject
            {
                ["merges"] = 0,
                ["splits"] = 0,
                ["fusions"] = 0,
                ["oversizedFusionTicks"] = 0,
                ["timersSet"] = 0,
                ["timersFired"] = 0,
            },
            ["fusedFightPairs"] = new JsonObject { ["p50"] = 0, ["p95"] = 0, ["max"] = 0 },
            ["budget"] = new JsonObject { ["bookkeepingP99Micros"] = 0.0, ["budgetMicros"] = 1000.0, ["withinBudget"] = true },
            ["wallClock"] = new JsonObject { ["seconds"] = 0.0 },
        };
    }
}
