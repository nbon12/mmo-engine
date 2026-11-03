using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Tests.Support;

/// <summary>
/// Run configurations for tests: a scenario's defaults with explicit overrides, shrunk to a small map
/// and population so runs take seconds. Holds no business rules; every value is stated by the caller.
/// </summary>
internal static class TestRuns
{
    /// <summary>The fixed CsCheck seed for property tests, so they are deterministic.</summary>
    public const string PropertySeed = "0N0XIzNsQ0O2";

    /// <summary>The repository root: the nearest directory above the tests holding <c>MmoEngine.sln</c>.</summary>
    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MmoEngine.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("MmoEngine.sln was not found above the test directory.");
    }

    /// <summary>A unique temp directory per call: parallel- and rerun-safe.</summary>
    public static string TempDir() => Path.Combine(Path.GetTempPath(), "mmo-tests", Guid.NewGuid().ToString("N"));

    /// <summary>A run config naming a scenario, its seed and duration, on a 2 km map with a small population.</summary>
    public static JsonObject Small(string scenario, ulong seed, long ticks, int population = 160)
    {
        var config = new JsonObject
        {
            ["scenario"] = scenario,
            ["seed"] = seed,
            ["durationTicks"] = ticks,
            ["map"] = new JsonObject { ["widthMetres"] = 2000, ["heightMetres"] = 2000 },
            ["population"] = new JsonObject { ["total"] = population },
        };
        return config;
    }

    /// <summary>Sets a dotted path in a run config and returns it.</summary>
    public static JsonObject With(this JsonObject config, string path, JsonNode? value)
    {
        ConfigJson.SetPath(config, path, value);
        return config;
    }

    /// <summary>Loads a run config over its scenario defaults, failing the test on any error.</summary>
    public static LoadedRunConfig Load(JsonObject runConfig, string? environment = null)
    {
        var result = RunConfigLoader.Load(runConfig, environmentName: environment);
        Assert.True(result.Succeeded, "config should load: " + string.Join("; ", result.Errors));
        return result.Loaded!;
    }

    /// <summary>Writes a run config to a JSON file in a new temp directory and returns its path.</summary>
    public static string WriteConfig(JsonObject runConfig)
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, runConfig.ToJsonString());
        return path;
    }

    /// <summary>Runs a loaded config into a new temp folder with a fixed git SHA (no git process per test).</summary>
    public static RunResult Run(LoadedRunConfig loaded, RunOptions? options = null) =>
        SimulationRunner.Run(loaded, TempDir(), (options ?? new RunOptions()) with { GitSha = options?.GitSha ?? "unknown" });

    /// <summary>The rows of a CSV file without its header, split on commas (the files under test quote nothing in these columns).</summary>
    public static IReadOnlyList<string[]> CsvRows(string path) =>
        [.. File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','))];

    /// <summary>A column of a CSV file by header name.</summary>
    public static IReadOnlyList<string> CsvColumn(string path, string column)
    {
        var lines = File.ReadAllLines(path);
        var index = Array.IndexOf(lines[0].Split(','), column);
        Assert.True(index >= 0, $"{path} has no column {column}");
        return [.. lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')[index])];
    }

    public static double ParseDouble(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
}
