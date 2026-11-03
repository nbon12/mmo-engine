using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scenarios;

/// <summary>Each scenario validates its own parameters and its map file before a run, naming the field (FR-026).</summary>
public sealed class ScenarioParameterTests
{
    /// <summary>An invalid scenario value is refused with the JSON path of the field at fault.</summary>
    [Theory]
    [InlineData("Spacing", """{"scenarioParameters.spacingMetres": 0}""", "scenarioParameters.spacingMetres")]
    [InlineData("Spacing", """{"scenarioParameters.spacingMetres": 8000}""", "scenarioParameters.spacingMetres")]
    [InlineData("Spacing", """{"scenarioParameters.fights": [10, 10, 10], "scenarioParameters.fightBases": null}""", "scenarioParameters.fights")]
    [InlineData("Spacing", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("Spacing", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("LaneTraffic", """{"scenarioParameters.laneLengthMetres": 0}""", "scenarioParameters.laneLengthMetres")]
    [InlineData("LaneTraffic", """{"scenarioParameters.laneLengthMetres": 7500}""", "scenarioParameters.laneLengthMetres")]
    [InlineData("LaneTraffic", """{"scenarioParameters.clumpSize": 0}""", "scenarioParameters.clumpSize")]
    [InlineData("LaneTraffic", """{"scenarioParameters.clumpSpreadMetres": -1}""", "scenarioParameters.clumpSpreadMetres")]
    [InlineData("LaneTraffic", """{"scenarioParameters.travellerPlacement": "Scattered"}""", "scenarioParameters")]
    [InlineData("LaneTraffic", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("LaneTraffic", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("VehicleColumns", """{"scenarioParameters.columns": -1}""", "scenarioParameters.columns")]
    [InlineData("VehicleColumns", """{"scenarioParameters.vehiclesPerColumn": -1}""", "scenarioParameters.vehiclesPerColumn")]
    [InlineData("VehicleColumns", """{"scenarioParameters.columns": 3, "scenarioParameters.vehiclesPerColumn": 1000000000}""", "scenarioParameters.vehiclesPerColumn")]
    [InlineData("VehicleColumns", """{"scenarioParameters.columnSpacingMetres": -1}""", "scenarioParameters.columnSpacingMetres")]
    [InlineData("VehicleColumns", """{"scenarioParameters.columnSpeedMetresPerSecond": 31}""", "scenarioParameters.columnSpeedMetresPerSecond")]
    [InlineData("VehicleColumns", """{"scenarioParameters.columnSpeedMetresPerSecond": 0}""", "scenarioParameters.columnSpeedMetresPerSecond")]
    [InlineData("VehicleColumns", """{"scenarioParameters.fights": [100], "scenarioParameters.fightBases": [0]}""", "scenarioParameters.fights")]
    [InlineData("VehicleColumns", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("VehicleColumns", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("OneBase", """{"map.basesPerLane": 2, "scenarioParameters.fights": [10, 10], "scenarioParameters.fightBases": null}""", "scenarioParameters.fights")]
    [InlineData("OneBase", """{"scenarioParameters.fights": [], "scenarioParameters.fightBases": []}""", "scenarioParameters.fights")]
    [InlineData("OneBase", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("AirTraffic", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("AirTraffic", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("Baseline", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("Migration", """{"map.file": "simulator/maps/stacked-base.json"}""", "map.file")]
    [InlineData("Migration", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("StackedAreas", """{"scenarioParameters.surfaceFight": -1}""", "scenarioParameters.surfaceFight")]
    [InlineData("StackedAreas", """{"scenarioParameters.undergroundFight": -1}""", "scenarioParameters.undergroundFight")]
    [InlineData("StackedAreas", """{"scenarioParameters.undergroundLayer": 0}""", "scenarioParameters.undergroundLayer")]
    [InlineData("StackedAreas", """{"scenarioParameters.undergroundLayer": 7}""", "scenarioParameters.undergroundLayer")]
    [InlineData("StackedAreas", """{"scenarioParameters.composition.aircraft": 0.5}""", "scenarioParameters.composition")]
    [InlineData("StackedAreas", """{"regionizer.sectionSize": 32}""", "regionizer.sectionSize")]
    [InlineData("StackedAreas", """{"map.file": null}""", "map.file")]
    [InlineData("StackedAreas", """{"map.file": "simulator/maps/no-such-map.json"}""", "map.file")]
    [InlineData("StackedAreas", """{"map.widthMetres": 1000}""", "map.widthMetres")]
    [InlineData("StackedAreas", """{"map.heightMetres": 1000}""", "map.heightMetres")]
    [InlineData("StackedAreas", """{"population.total": 9999}""", "population.total")]
    [InlineData("StackedAreas", """{"scenarioParameters.doorCycleSeconds": 60}""", "scenarioParameters")]
    [InlineData("StackedAreas", """{"scenarioParameters": null}""", "scenarioParameters")]
    [InlineData("Soak", """{"scenarioParameters.surfaceFight": -1}""", "scenarioParameters.surfaceFight")]
    [InlineData("Soak", """{"scenarioParameters.migrationEverySeconds": 0}""", "scenarioParameters.migrationEverySeconds")]
    [InlineData("Soak", """{"map.file": null}""", "map.file")]
    [InlineData("Soak", """{"map.widthMetres": 1000}""", "map.widthMetres")]
    [InlineData("Soak", """{"scenarioParameters": null}""", "scenarioParameters")]
    public void InvalidScenarioValueIsRefusedNamingTheField(string scenario, string overrides, string expectedPath)
    {
        var runConfig = new JsonObject { ["scenario"] = scenario };
        foreach (var (path, value) in JsonNode.Parse(overrides)!.AsObject())
        {
            runConfig.With(path, value?.DeepClone());
        }

        var result = RunConfigLoader.Load(runConfig);

        Assert.False(result.Succeeded, $"{scenario} with {overrides} should be refused");
        Assert.Contains(result.Errors, e => e.Path.StartsWith(expectedPath, StringComparison.Ordinal));
    }

    /// <summary>A map file that loads but cannot be used is refused, naming <c>map.file</c>.</summary>
    [Theory]
    [InlineData("not json", "could not be read")]
    [InlineData("no-surface", "has no layer 0")]
    [InlineData("instant-schedule", "shorter than one base step")]
    public void UnusableMapFileIsRefusedNamingMapFile(string kind, string expectedMessage)
    {
        var map = (JsonObject)JsonNode.Parse(File.ReadAllText(ScenarioRuns.MapPath("stacked-base.json")))!;
        string path;
        switch (kind)
        {
            case "not json":
                path = Path.Combine(TestRuns.TempDir(), "map.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "{ not json");
                break;
            case "no-surface":
                // The bunker alone, renumbered: a valid map with no surface layer 0.
                var bunker = map["layers"]![1]!.DeepClone();
                bunker["id"] = 3;
                path = ScenarioRuns.WriteMap(new JsonObject
                {
                    ["formatVersion"] = 1,
                    ["name"] = "bunker-only",
                    ["sectionSize"] = 16,
                    ["layers"] = new JsonArray(bunker),
                });
                break;
            default:
                path = ScenarioRuns.StackedBaseWithSchedule(everySeconds: 0.001, openSeconds: 0);
                break;
        }

        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = "StackedAreas" }.With("map.file", path));

        Assert.False(result.Succeeded);
        Assert.All(result.Errors, e => Assert.Equal("map.file", e.Path));
        Assert.Contains(result.Errors, e => e.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    /// <summary>More than five validator findings are summarised rather than listed in full.</summary>
    [Fact]
    public void ManyPortalFindingsAreSummarised()
    {
        var map = (JsonObject)JsonNode.Parse(File.ReadAllText(ScenarioRuns.MapPath("stacked-base.json")))!;
        var portals = map["portals"]!.AsArray();
        for (var i = 0; i < 6; i++)
        {
            var portal = portals[0]!.DeepClone();
            portal["id"] = 10 + i;
            portal["closedPolicy"] = new JsonObject();
            portals.Add(portal);
        }

        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = "StackedAreas" }.With("map.file", ScenarioRuns.WriteMap(map)));

        var error = Assert.Single(result.Errors);
        Assert.Equal("map.file", error.Path);
        Assert.Contains("6 finding(s)", error.Message, StringComparison.Ordinal);
        Assert.Contains("and 1 more", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every scenario's committed defaults load and pass validation, including both map scenarios' map file.</summary>
    [Theory]
    [MemberData(nameof(ScenarioTests.AllScenarios), MemberType = typeof(ScenarioTests))]
    public void DefaultsOfEveryScenarioAreValid(string scenario)
    {
        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = scenario });

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }
}
