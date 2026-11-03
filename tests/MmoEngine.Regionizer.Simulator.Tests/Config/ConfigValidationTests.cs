using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Config;

/// <summary>Configuration loading and validation (FR-026, research D12, contracts/config-and-cli.md).</summary>
public sealed class ConfigValidationTests
{
    public static TheoryData<string> Scenarios => [.. ScenarioNames.All];

    /// <summary>
    /// US2.4: Given a configuration with an invalid field, When the simulator loads it, Then it refuses
    /// to run and the error names the field by its JSON path.
    /// </summary>
    [Theory]
    [InlineData("regionizer.sectionSize", "12", null, "regionizer.sectionSize")]
    [InlineData("regionizer.mergeGap", "2", null, "regionizer.mergeGap")]
    [InlineData("regionizer.claimRadius.infantry", "-1", null, "regionizer.claimRadius.infantry")]
    [InlineData("regionizer.split.minDeadPercent", "101", null, "regionizer.split.minDeadPercent")]
    [InlineData("population.total", "10001", null, "population.total")]
    [InlineData("scenario", "\"Skirmish\"", null, "scenario")]
    [InlineData(null, null, "Prod", "DOTNET_ENVIRONMENT")]
    public void InvalidFieldIsRefusedAndNamedByItsJsonPath(string? path, string? json, string? environment, string expectedPath)
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" };
        if (path is not null)
        {
            ConfigJson.SetPath(runConfig, path, JsonNode.Parse(json!));
        }

        var result = RunConfigLoader.Load(runConfig, environmentName: environment);

        Assert.False(result.Succeeded);
        Assert.Null(result.Loaded);
        Assert.Contains(result.Errors, e => e.Path == expectedPath);
    }

    [Fact]
    public void SectionSizeErrorReadsAsTheContractStatesIt()
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" }.With("regionizer.sectionSize", 12);

        var error = Assert.Single(RunConfigLoader.Load(runConfig).Errors);

        Assert.Equal("regionizer.sectionSize: must be a power of two between 1 and 1024", error.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(64)]
    [InlineData(1024)]
    public void PowerOfTwoSectionSizesAreAccepted(int size)
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" }.With("regionizer.sectionSize", size);

        Assert.True(RunConfigLoader.Load(runConfig).Succeeded);
    }

    [Theory]
    [InlineData("regionizer.sectionSize", "0", "regionizer.sectionSize")]
    [InlineData("regionizer.sectionSize", "2048", "regionizer.sectionSize")]
    [InlineData("regionizer.split.minSections", "0", "regionizer.split.minSections")]
    [InlineData("regionizer.split.minAgeSeconds", "-1", "regionizer.split.minAgeSeconds")]
    [InlineData("regionizer.split.minDeadPercent", "-0.5", "regionizer.split.minDeadPercent")]
    [InlineData("movement.speedCeiling.aircraft", "0", "movement.speedCeiling.aircraft")]
    [InlineData("movement.models.speedFactorMax", "1.5", "movement.models.speedFactorMax")]
    [InlineData("map.widthMetres", "499", "map.widthMetres")]
    [InlineData("map.heightMetres", "20001", "map.heightMetres")]
    [InlineData("metrics.metricsEveryTicks", "0", "metrics.metricsEveryTicks")]
    [InlineData("metrics.crossRegionPairsEveryTicks", "0", "metrics.crossRegionPairsEveryTicks")]
    [InlineData("metrics.readOnlyRangeMetres", "200", "metrics.readOnlyRangeMetres")]
    [InlineData("scheduling.dropTo20HzProbability", "1.5", "scheduling.dropTo20HzProbability")]
    [InlineData("scheduling.dropTicksMax", "1", "scheduling.dropTicksMax")]
    [InlineData("scheduling.mode", "\"Sometimes\"", "scheduling.mode")]
    [InlineData("timers.delayTicksMin", "0", "timers.delayTicksMin")]
    [InlineData("durationTicks", "0", "durationTicks")]
    [InlineData("regionizer.anchoring", "\"Corner\"", "regionizer.anchoring")]
    [InlineData("regionizer.tickRate", "30", "regionizer")]
    [InlineData("scenarioParameters.fights[0]", null, "scenarioParameters.fights[0]")]
    [InlineData("scenarioParameters.composition.infantry", "0.9", "scenarioParameters.composition")]
    [InlineData("scenarioParameters.fightBases", "[0, 0, 1, 2, 3, 4, 5]", "scenarioParameters.fightBases[1]")]
    [InlineData("scenarioParameters.fightBases", "[0, 1, 2, 3, 4, 5, 12]", "scenarioParameters.fightBases[6]")]
    [InlineData("scenarioParameters.travellers", "-1", "scenarioParameters.travellers")]
    public void OtherInvalidValuesAreNamedByPath(string path, string? json, string expectedPrefix)
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" };
        if (path == "scenarioParameters.fights[0]")
        {
            runConfig.With("scenarioParameters.fights", new JsonArray(-5, 800, 800, 500, 400, 300, 300));
        }
        else
        {
            ConfigJson.SetPath(runConfig, path, JsonNode.Parse(json!));
        }

        var result = RunConfigLoader.Load(runConfig);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Path.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioThatCreatesMoreEntitiesThanTheLimitNamesThePopulation()
    {
        // 9,990 players plus 36 capture points (3 lanes × 4 bases × 3 objectives) exceed a limit of 10,000.
        var runConfig = new JsonObject { ["scenario"] = "Baseline" }.With("population.total", 9990);

        var result = RunConfigLoader.Load(runConfig);

        var error = Assert.Single(result.Errors);
        Assert.Equal("population.total", error.Path);
        Assert.Contains("10026", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ValidDefaultsLoadForEveryScenario(string scenario)
    {
        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = scenario });

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(scenario, result.Loaded!.Config.Scenario);
        Assert.Matches("^[0-9a-f]{16}$", result.Loaded.ConfigHash);
    }

    [Fact]
    public void RunConfigIsDeepMergedOverTheScenarioDefaults()
    {
        var defaults = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuns.RepositoryRoot(), "simulator", "scenarios", "Baseline.json")))!;
        var runConfig = new JsonObject { ["scenario"] = "Baseline" }
            .With("regionizer.claimRadius.infantry", 24)
            .With("scenarioParameters.fights", new JsonArray(10, 20));

        var config = TestRuns.Load(runConfig.With("scenarioParameters.fightBases", new JsonArray(0, 1))).Config;

        Assert.Equal(24, config.Regionizer.ClaimRadius.Infantry);
        Assert.Equal(defaults["regionizer"]!["claimRadius"]!["groundVehicle"]!.GetValue<double>(), config.Regionizer.ClaimRadius.GroundVehicle);
        Assert.Equal(defaults["regionizer"]!["sectionSize"]!.GetValue<int>(), config.Regionizer.SectionSize);
        Assert.Equal([10, 20], ScenarioParameters.Require<BaselineParameters>(config).Fights);
        Assert.Equal(defaults["scenarioParameters"]!["travellers"]!.GetValue<int>(), ScenarioParameters.Require<BaselineParameters>(config).Travellers);
    }

    [Fact]
    public void SeedAndTickOverridesApplyLast()
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline", ["seed"] = 5, ["durationTicks"] = 100 };

        var result = RunConfigLoader.Load(runConfig, new RunConfigOverrides(Seed: 77, DurationTicks: 12));

        Assert.Equal(77UL, result.Loaded!.Config.Seed);
        Assert.Equal(12, result.Loaded.Config.DurationTicks);
    }

    [Fact]
    public void ConfigHashIgnoresKeyOrderAndNumberSpellingButNotValues()
    {
        var a = RunConfigLoader.Load((JsonObject)JsonNode.Parse("""{ "scenario": "Baseline", "seed": 3, "regionizer": { "mergeGap": 1, "claimRadius": { "infantry": 20 } } }""")!).Loaded!;
        var b = RunConfigLoader.Load((JsonObject)JsonNode.Parse("""{ "regionizer": { "claimRadius": { "infantry": 20.0 }, "mergeGap": 1 }, "seed": 3, "scenario": "Baseline" }""")!).Loaded!;
        var c = RunConfigLoader.Load((JsonObject)JsonNode.Parse("""{ "scenario": "Baseline", "seed": 4, "regionizer": { "mergeGap": 1, "claimRadius": { "infantry": 20 } } }""")!).Loaded!;

        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        Assert.Equal(a.ConfigHash, b.ConfigHash);
        Assert.NotEqual(a.ConfigHash, c.ConfigHash);
        Assert.Equal(ConfigJson.Hash(a.CanonicalJson), a.ConfigHash);
    }

    [Fact]
    public void CanonicalJsonSortsKeysAndRoundTripsTheConfig()
    {
        var loaded = TestRuns.Load(new JsonObject { ["scenario"] = "Migration" });

        var reparsed = RunConfigLoader.Load((JsonObject)JsonNode.Parse(loaded.CanonicalJson)!).Loaded!;

        Assert.Equal(loaded.CanonicalJson, reparsed.CanonicalJson);
        var keys = ((JsonObject)JsonNode.Parse(loaded.CanonicalJson)!).Select(kv => kv.Key).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Theory]
    [InlineData(2.0, 120)]
    [InlineData(0.5, 30)]
    [InlineData(0.0, 0)]
    [InlineData(0.025, 2)]
    public void MinAgeSecondsBecomesBaseStepsAtSixtyHertz(double seconds, long steps)
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }.With("regionizer.split.minAgeSeconds", seconds)).Config;

        var options = config.Regionizer.ToRegionizerOptions();

        Assert.Equal(steps, options.SplitMinAgeSteps);
        Assert.Equal(config.Regionizer.SectionSize, options.SectionSize);
        Assert.Equal(config.Regionizer.ClaimRadius.CapturePointZone, options.ClaimRadius.CapturePointZone);
    }

    [Theory]
    [InlineData(null, "Development")]
    [InlineData("", "Development")]
    [InlineData("Development", "Development")]
    [InlineData("Dev", "Dev")]
    [InlineData("Test", "Test")]
    [InlineData("Staging", "Staging")]
    [InlineData("Production", "Production")]
    public void KnownEnvironmentNamesAreAccepted(string? raw, string expected)
    {
        var loaded = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }, raw);

        Assert.Equal(expected, loaded.EnvironmentName);
    }

    [Fact]
    public void MissingConfigFileIsAnError()
    {
        var result = RunConfigLoader.LoadFile(Path.Combine(TestRuns.TempDir(), "absent.json"));

        Assert.Equal("config", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void ConfigFileIsReadFromDisk()
    {
        var path = TestRuns.WriteConfig(new JsonObject { ["scenario"] = "Baseline", ["seed"] = 9 });

        var result = RunConfigLoader.LoadFile(path);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(9UL, result.Loaded!.Config.Seed);
    }

    [Fact]
    public void TypedConfigIsValidatedBeforeARun()
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }).Config;
        var invalid = config with { Regionizer = config.Regionizer with { MergeGap = 3 } };

        var ex = Assert.Throws<ConfigValidationException>(() => RunConfigLoader.FromConfig(invalid));

        Assert.Equal("regionizer.mergeGap", Assert.Single(ex.Errors).Path);
        Assert.Contains("regionizer.mergeGap: must be 0 or 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedConfigWithABadEnvironmentNameIsRefused()
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }).Config;

        var ex = Assert.Throws<ConfigValidationException>(() => RunConfigLoader.FromConfig(config, "Live"));

        Assert.Equal(EnvironmentNames.VariableName, Assert.Single(ex.Errors).Path);
    }

    /// <summary>Scenario-specific values are validated by the scenario and named by their JSON path.</summary>
    [Theory]
    [InlineData("Baseline", "scenarioParameters=null", "scenarioParameters")]
    [InlineData("Baseline", "scenarioParameters.unknownKnob=1", "scenarioParameters.unknownKnob")]
    [InlineData("Baseline", "scenarioParameters.fights=null", "scenarioParameters.fights")]
    [InlineData("Baseline", "scenarioParameters.fightBases=[0, 1]", "scenarioParameters.fightBases")]
    [InlineData("Baseline", "scenarioParameters.fightBases=null|scenarioParameters.fights=[1,1,1,1,1,1,1,1,1,1,1,1,1]", "scenarioParameters.fights")]
    [InlineData("Baseline", "scenarioParameters.pointToPointAircraft=-1", "scenarioParameters.pointToPointAircraft")]
    [InlineData("Baseline", "scenarioParameters.composition=null", "scenarioParameters.composition")]
    [InlineData("Baseline", "scenarioParameters.composition.aircraft=1.5", "scenarioParameters.composition.aircraft")]
    [InlineData("Baseline", "scenarioParameters.fightRadiusPerSqrtPlayer=0", "scenarioParameters.fightRadiusPerSqrtPlayer")]
    [InlineData("Baseline", "scenarioParameters.objectivesPerBase=17", "scenarioParameters.objectivesPerBase")]
    [InlineData("Baseline", "scenarioParameters.objectiveSpreadMetres=-1", "scenarioParameters.objectiveSpreadMetres")]
    [InlineData("Migration", "scenarioParameters=null", "scenarioParameters")]
    [InlineData("Migration", "scenarioParameters.migrationEverySeconds=0", "scenarioParameters.migrationEverySeconds")]
    [InlineData("Migration", "scenarioParameters.instantActionShare=2", "scenarioParameters.instantActionShare")]
    [InlineData("Migration", "scenarioParameters.fights=[-1, 1, 1, 1, 1, 1, 1]", "scenarioParameters.fights[0]")]
    public void ScenarioParameterErrorsAreNamedByPath(string scenario, string edits, string expectedPath)
    {
        var runConfig = new JsonObject { ["scenario"] = scenario };
        foreach (var edit in edits.Split('|'))
        {
            var at = edit.IndexOf('=', StringComparison.Ordinal);
            ConfigJson.SetPath(runConfig, edit[..at], JsonNode.Parse(edit[(at + 1)..]));
        }

        var result = RunConfigLoader.Load(runConfig);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Path == expectedPath);
    }

    [Theory]
    [InlineData("{ \"scenario\": ", "is not valid JSON")]
    [InlineData("[1, 2]", "must be a JSON object")]
    public void UnreadableConfigFilesAreErrors(string text, string message)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, text);

        var error = Assert.Single(RunConfigLoader.LoadFile(path).Errors);

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-directory", "was not found")]
    [InlineData("missing-file", "has no defaults file")]
    [InlineData("invalid-json", "is not valid JSON")]
    [InlineData("not-an-object", "must hold a JSON object")]
    public void UnusableScenarioDefaultsAreErrorsOnTheScenario(string variant, string message)
    {
        var dir = TestRuns.TempDir();
        if (variant != "missing-directory")
        {
            Directory.CreateDirectory(dir);
        }

        if (variant == "invalid-json")
        {
            File.WriteAllText(Path.Combine(dir, "Baseline.json"), "{ nope");
        }
        else if (variant == "not-an-object")
        {
            File.WriteAllText(Path.Combine(dir, "Baseline.json"), "[]");
        }

        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = "Baseline" }, scenariosDirectory: dir);

        var error = Assert.Single(result.Errors);
        Assert.Equal("scenario", error.Path);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsAreFoundAboveTheWorkingDirectoryWhenNotBesideTheTool()
    {
        var repoRoot = TestRuns.RepositoryRoot();
        var appWithoutDefaults = TestRuns.TempDir();
        Directory.CreateDirectory(appWithoutDefaults);

        var fromRepo = ScenarioDefaults.Locate(null, appWithoutDefaults, Path.Combine(repoRoot, "src"));
        var nowhere = ScenarioDefaults.Locate(null, appWithoutDefaults, appWithoutDefaults);

        Assert.Equal(Path.Combine(repoRoot, "simulator", "scenarios"), fromRepo);
        Assert.Null(nowhere);
    }

    [Fact]
    public void PathOverridesApplyLastAndMayChangeTheScenario()
    {
        var overrides = new RunConfigOverrides(Paths: new Dictionary<string, JsonNode?>
        {
            ["scenario"] = "Migration",
            ["regionizer.mergeGap"] = 1,
        });

        var loaded = RunConfigLoader.Load(new JsonObject { ["scenario"] = "Baseline" }, overrides).Loaded!;

        Assert.Equal("Migration", loaded.Config.Scenario);
        Assert.Equal(1, loaded.Config.Regionizer.MergeGap);
        Assert.Equal(120, ScenarioParameters.Require<MigrationParameters>(loaded.Config).MigrationEverySeconds);
    }

    [Fact]
    public void ANonTextScenarioOverrideIsAnError()
    {
        var overrides = new RunConfigOverrides(Paths: new Dictionary<string, JsonNode?> { ["scenario"] = 3 });

        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = "Baseline" }, overrides);

        Assert.Equal("scenario", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void GetPathAndSetPathAddressDottedPaths()
    {
        var root = new JsonObject();

        ConfigJson.SetPath(root, "regionizer.claimRadius.infantry", 12);

        Assert.Equal(12, ConfigJson.GetPath(root, "regionizer.claimRadius.infantry")!.GetValue<int>());
        Assert.Null(ConfigJson.GetPath(root, "regionizer.claimRadius.aircraft"));
        Assert.Null(ConfigJson.GetPath(root, "regionizer.claimRadius.infantry.deeper"));
    }

    [Fact]
    public void CanonicalJsonNormalizesNumbersAndSortsNestedKeys()
    {
        var node = JsonNode.Parse("""{ "b": [1, 2.0, 2.5, 18446744073709551615, 1e300, true, false, null, "s"], "a": { "y": 1, "x": {} } }""");

        Assert.Equal("""{"a":{"x":{},"y":1},"b":[1,2,2.5,18446744073709551615,1E+300,true,false,null,"s"]}""", ConfigJson.Canonical(node));
    }

    [Theory]
    [InlineData("$.regionizer.sectionSize", "", "regionizer.sectionSize")]
    [InlineData("$", "", "config")]
    [InlineData(null, "", "config")]
    [InlineData("$.fights[2]", "scenarioParameters", "scenarioParameters.fights[2]")]
    [InlineData("$[2]", "scenarioParameters.fights", "scenarioParameters.fights[2]")]
    [InlineData("$", "scenarioParameters", "scenarioParameters")]
    public void JsonPathsBecomeConfigPaths(string? jsonPath, string prefix, string expected)
    {
        Assert.Equal(expected, ConfigPaths.FromJsonPath(jsonPath, prefix));
    }

    [Theory]
    [InlineData("Regionizer.ClaimRadius.Infantry", "regionizer.claimRadius.infantry")]
    [InlineData("Population.Total", "population.total")]
    [InlineData("", "config")]
    public void PropertyChainsBecomeConfigPaths(string chain, string expected)
    {
        Assert.Equal(expected, ConfigPaths.FromPropertyChain(chain));
    }
}
