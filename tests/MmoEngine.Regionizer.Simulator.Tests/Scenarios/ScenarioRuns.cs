using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scenarios;

/// <summary>
/// Short scenario runs and test maps for the scenario tests. Holds no business rules: every value is
/// stated by the caller or copied from a committed map file.
/// </summary>
internal static class ScenarioRuns
{
    /// <summary>A run config of a scenario's defaults with only the seed, duration and population total changed.</summary>
    public static JsonObject Short(string scenario, ulong seed, long ticks, int population) => new()
    {
        ["scenario"] = scenario,
        ["seed"] = seed,
        ["durationTicks"] = ticks,
        ["population"] = new JsonObject { ["total"] = population },
    };

    /// <summary>The committed map file at <c>simulator/maps/&lt;relative&gt;</c>, as an absolute path.</summary>
    public static string MapPath(string relative) => Path.Combine(TestRuns.RepositoryRoot(), "simulator", "maps", relative);

    /// <summary>
    /// Writes a copy of <c>stacked-base.json</c> into a new temp directory with every portal's schedule
    /// set to <paramref name="everySeconds"/> and <paramref name="openSeconds"/>, and returns its path.
    /// </summary>
    public static string StackedBaseWithSchedule(double everySeconds, double openSeconds)
    {
        var map = (JsonObject)JsonNode.Parse(File.ReadAllText(MapPath("stacked-base.json")))!;
        foreach (var entry in map["portalSchedule"]!.AsArray())
        {
            entry!["everySeconds"] = everySeconds;
            entry["openSeconds"] = openSeconds;
        }

        return WriteMap(map);
    }

    /// <summary>Writes map JSON into a new temp directory and returns its path.</summary>
    public static string WriteMap(JsonNode map)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "map.json");
        File.WriteAllText(path, map.ToJsonString());
        return path;
    }

    /// <summary>Counts merges by the class of the entity that caused them, from the run's events.</summary>
    internal sealed class MergeCounter : RunListener
    {
        public Dictionary<EntityClass, int> ByClass { get; } = [];

        public int Fusions { get; private set; }

        public override void OnEvent(in RunEvent runEvent)
        {
            if (runEvent.Type == RunEventType.Fusion)
            {
                Fusions++;
            }

            if (runEvent.Type == RunEventType.Merge && runEvent.CauseClass is { } cls)
            {
                ByClass[cls] = ByClass.GetValueOrDefault(cls) + 1;
            }
        }
    }
}
