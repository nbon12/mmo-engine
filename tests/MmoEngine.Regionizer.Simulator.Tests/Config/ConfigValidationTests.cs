using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Config;

/// <summary>Configuration loading and validation (FR-026, research D12, contracts/config-and-cli.md).</summary>
public sealed class ConfigValidationTests
{
    public static TheoryData<string> Scenarios => [.. ScenarioNames.All];

    /// <summary>
    /// US2.4: Given a configuration with an invalid field, When the simulator starts, Then it refuses
    /// to run, exits with a non-zero code (2) and names the invalid field by its JSON path. The cases
    /// include tick rates the base step rate is not a multiple of, a degraded rate not below the tick
    /// rate, and a map file the portal validator rejects (on Stacked areas, a scenario that reads map
    /// files, so the refusal is the validator's and not "must be null").
    /// </summary>
    [Theory]
    [InlineData("regionizer.sectionSize", "12", null, "regionizer.sectionSize")]
    [InlineData("regionizer.mergeGap", "2", null, "regionizer.mergeGap")]
    [InlineData("regionizer.claimRadius.infantry", "-1", null, "regionizer.claimRadius.infantry")]
    [InlineData("regionizer.split.minDeadPercent", "101", null, "regionizer.split.minDeadPercent")]
    [InlineData("population.total", "10001", null, "population.total")]
    [InlineData("scenario", "\"Skirmish\"", null, "scenario")]
    [InlineData(null, null, "Prod", "DOTNET_ENVIRONMENT")]
    [InlineData("scheduling.baseStepHz", "50", null, "scheduling.baseStepHz")]
    [InlineData("scheduling.baseStepHz", "90", null, "scheduling.baseStepHz")]
    [InlineData("scheduling.tickHz", "25", null, "scheduling.baseStepHz")]
    [InlineData("scheduling.degradedTickHz", "7", null, "scheduling.baseStepHz")]
    [InlineData("scheduling.degradedTickHz", "30", null, "scheduling.degradedTickHz")]
    [InlineData("scheduling.tickHz", "0", null, "scheduling.tickHz")]
    [InlineData("scheduling.baseStepHz", "1200", null, "scheduling.baseStepHz")]
    [InlineData("map.file", "\"simulator/maps/test/seeded-leaks.json\"", null, "map.file", "StackedAreas", "portal validator")]
    public void InvalidConfigIsRefusedNamingTheField(string? path, string? json, string? environment, string expectedPath, string scenario = "Baseline", string? expectedMessage = null)
    {
        var runConfig = TestRuns.Small(scenario, 3, 10);
        if (path is not null)
        {
            ConfigJson.SetPath(runConfig, path, JsonNode.Parse(json!));
        }

        var result = RunConfigLoader.Load(runConfig, environmentName: environment);
        var output = TestRuns.TempDir();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(
            ["run", TestRuns.WriteConfig(runConfig), "--out", output],
            stdout,
            stderr,
            new RunOptions { GitSha = "unknown", EnvironmentName = environment });

        Assert.False(result.Succeeded);
        Assert.Null(result.Loaded);
        Assert.Contains(result.Errors, e => e.Path == expectedPath);
        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Contains($"\"Path\":\"{expectedPath}\"", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.False(Directory.Exists(output));
        if (expectedMessage is not null)
        {
            Assert.Contains(result.Errors, e => e.Path == expectedPath && e.Message.Contains(expectedMessage, StringComparison.Ordinal));
            Assert.Contains(expectedMessage, stderr.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// FR-021 and FR-023: with nothing but the scenario named, the speed ceilings are 7, 30 and 120 m/s for
    /// infantry, ground vehicles and aircraft, and the map is 8 × 8 km with three lanes and no map file.
    /// </summary>
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }).Config;

        var ceiling = config.Movement.SpeedCeiling;
        Assert.Equal((7.0, 30.0, 120.0), (ceiling.Infantry, ceiling.GroundVehicle, ceiling.Aircraft));
        Assert.Equal((8000.0, 8000.0), (config.Map.WidthMetres, config.Map.HeightMetres));
        Assert.Equal(3, config.Map.Lanes);
        Assert.Null(config.Map.File);
    }

    /// <summary>
    /// A scenario that reads a map file accepts one that loads and passes the portal validator. Refusing a
    /// map with findings, or one that can't be read, is the scenario's job (Scenarios/ScenarioTests.cs and
    /// ScenarioParameterTests.cs cover it), so the check runs once, where the map is used.
    /// </summary>
    [Fact]
    public void AMapFileThatPassesTheValidatorIsAccepted()
    {
        var result = RunConfigLoader.Load(new JsonObject { ["scenario"] = "StackedAreas" }.With("map.file", "simulator/maps/stacked-base.json"));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("simulator/maps/stacked-base.json", result.Loaded!.Config.Map.File);
    }

    /// <summary>The tick rates (FR-018, research D7): valid combinations load, with 60, 30 and 20 Hz when the config leaves them out.</summary>
    [Theory]
    [InlineData(null, null, null, 60, 30, 20)]
    [InlineData(120, null, null, 120, 30, 20)]
    [InlineData(60, 60, 20, 60, 60, 20)]
    [InlineData(90, 30, 10, 90, 30, 10)]
    [InlineData(30, 30, 10, 30, 30, 10)]
    public void ValidTickRatesLoadAndDefaultToSixtyThirtyAndTwenty(int? baseStepHz, int? tickHz, int? degradedTickHz, int expectedBase, int expectedTick, int expectedDegraded)
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" };
        if (baseStepHz is { } b)
        {
            runConfig.With("scheduling.baseStepHz", b);
        }

        if (tickHz is { } t)
        {
            runConfig.With("scheduling.tickHz", t);
        }

        if (degradedTickHz is { } d)
        {
            runConfig.With("scheduling.degradedTickHz", d);
        }

        var scheduling = TestRuns.Load(runConfig).Config.Scheduling;

        Assert.Equal((expectedBase, expectedTick, expectedDegraded), (scheduling.BaseStepHz, scheduling.TickHz, scheduling.DegradedTickHz));
    }

    [Theory]
    [InlineData("scheduling.baseStepHz", "50", "scheduling.baseStepHz: must be a multiple of scheduling.tickHz (30) and scheduling.degradedTickHz (20), was 50")]
    [InlineData("scheduling.degradedTickHz", "30", "scheduling.degradedTickHz: must be lower than scheduling.tickHz (30), was 30")]
    public void TickRateErrorsReadAsTheContractStatesThem(string path, string json, string expected)
    {
        var runConfig = new JsonObject { ["scenario"] = "Baseline" }.With(path, JsonNode.Parse(json));

        var error = Assert.Single(RunConfigLoader.Load(runConfig).Errors);

        Assert.Equal(expected, error.ToString());
    }

    public static TheoryData<string, string> TuningPaths => new()
    {
        { "regionizer.sectionSize", "32" },
        { "regionizer.anchoring", "\"Position\"" },
        { "regionizer.mergeGap", "1" },
        { "regionizer.claimRadius.infantry", "24" },
        { "regionizer.claimRadius.groundVehicle", "32" },
        { "regionizer.claimRadius.aircraft", "40" },
        { "regionizer.claimRadius.traveller", "8" },
        { "regionizer.claimRadius.capturePointZone", "12" },
        { "regionizer.split.minSections", "16" },
        { "regionizer.split.minDeadPercent", "40" },
        { "regionizer.split.minAgeSeconds", "5" },
        { "regionizer.timerMode", "\"Absolute\"" },
        { "scheduling.baseStepHz", "120" },
        { "scheduling.tickHz", "60" },
        { "scheduling.degradedTickHz", "15" },
        { "metrics.targetFightSize", "900" },
    };

    /// <summary>
    /// FR-026 and Principle VI: every tuning value comes from configuration. Overriding each tuning
    /// path changes the run's effective options (the core's options, the time base or the metrics
    /// settings) and is recorded in the manifest's parameters.
    /// </summary>
    [Theory]
    [MemberData(nameof(TuningPaths))]
    public void TuningPathOverrideReachesEffectiveOptionsAndManifest(string path, string json)
    {
        const ulong Seed = 5;
        var value = JsonNode.Parse(json)!;
        var defaults = TestRuns.Load(TestRuns.Small("Baseline", Seed, 2, population: 40));
        var overridden = TestRuns.Load(TestRuns.Small("Baseline", Seed, 2, population: 40).With(path, value));
        var dir = TestRuns.TempDir();

        var result = SimulationRunner.Run(overridden, dir, new RunOptions { GitSha = "unknown" });

        var before = Effective(defaults.Config, path);
        var after = Effective(overridden.Config, path);
        Assert.NotEqual(before, after);
        Assert.Equal(Expected(path, value, overridden.Config), after);
        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunFolder.ManifestFile)))!.AsObject();
        Assert.Equal(ConfigJson.Canonical(value), ConfigJson.Canonical(ConfigJson.GetPath(manifest["parameters"]!.AsObject(), path)));
        Assert.NotEqual(defaults.ConfigHash, manifest["configHash"]!.GetValue<string>());
        Assert.Equal(overridden.ConfigHash, manifest["configHash"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(2.0, 60, 30, 20, 120)]
    [InlineData(0.5, 60, 30, 20, 30)]
    [InlineData(0.0, 60, 30, 20, 0)]
    [InlineData(0.025, 60, 30, 20, 2)]
    [InlineData(2.0, 120, 30, 20, 240)]
    [InlineData(0.5, 90, 30, 10, 45)]
    [InlineData(0.025, 30, 30, 10, 1)]
    public void MinAgeSecondsBecomesBaseStepsOfTheRunsTimeBase(double seconds, int baseStepHz, int tickHz, int degradedTickHz, long steps)
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "Baseline" }
            .With("regionizer.split.minAgeSeconds", seconds)
            .With("scheduling.baseStepHz", baseStepHz)
            .With("scheduling.tickHz", tickHz)
            .With("scheduling.degradedTickHz", degradedTickHz)).Config;

        var options = config.ToRegionizerOptions();

        Assert.Equal(steps, options.SplitMinAgeSteps);
        Assert.Equal(config.Regionizer.SectionSize, options.SectionSize);
        Assert.Equal(config.Regionizer.ClaimRadius.CapturePointZone, options.ClaimRadius.CapturePointZone);
        Assert.Equal(TimeBase.Default.ToSteps(seconds), config.Regionizer.ToRegionizerOptions().SplitMinAgeSteps);
    }

    /// <summary>The run's effective value of a tuning path, as the run uses it.</summary>
    private static object Effective(RunConfig config, string path)
    {
        var options = config.ToRegionizerOptions();
        var time = TimeBase.From(config.Scheduling);
        return path switch
        {
            "regionizer.sectionSize" => options.SectionSize,
            "regionizer.anchoring" => options.Anchoring,
            "regionizer.mergeGap" => options.MergeGap,
            "regionizer.claimRadius.infantry" => options.ClaimRadius.Infantry,
            "regionizer.claimRadius.groundVehicle" => options.ClaimRadius.GroundVehicle,
            "regionizer.claimRadius.aircraft" => options.ClaimRadius.Aircraft,
            "regionizer.claimRadius.traveller" => options.ClaimRadius.Traveller,
            "regionizer.claimRadius.capturePointZone" => options.ClaimRadius.CapturePointZone,
            "regionizer.split.minSections" => options.SplitMinSections,
            "regionizer.split.minDeadPercent" => options.SplitMinDeadPercent,
            "regionizer.split.minAgeSeconds" => options.SplitMinAgeSteps,
            "regionizer.timerMode" => options.TimerMode,
            "scheduling.baseStepHz" => (time.BaseStepHz, options.SplitMinAgeSteps),
            "scheduling.tickHz" => (time.TickHz, time.TickPeriodSteps),
            "scheduling.degradedTickHz" => (time.DegradedTickHz, time.DegradedPeriodSteps),
            "metrics.targetFightSize" => config.Metrics.TargetFightSize,
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "not a tuning path of this test"),
        };
    }

    /// <summary>What the effective value must be once <paramref name="value"/> is set, stated from the value itself.</summary>
    private static object Expected(string path, JsonNode value, RunConfig config) => path switch
    {
        "regionizer.anchoring" => Enum.Parse<ClaimAnchoring>(value.GetValue<string>()),
        "regionizer.timerMode" => Enum.Parse<TimerMode>(value.GetValue<string>()),
        "regionizer.claimRadius.infantry" or "regionizer.claimRadius.groundVehicle" or "regionizer.claimRadius.aircraft"
            or "regionizer.claimRadius.traveller" or "regionizer.claimRadius.capturePointZone"
            or "regionizer.split.minDeadPercent" => value.GetValue<double>(),
        "regionizer.split.minAgeSeconds" => (long)(value.GetValue<double>() * 60),
        "scheduling.baseStepHz" => (value.GetValue<int>(), (long)(config.Regionizer.Split.MinAgeSeconds * value.GetValue<int>())),
        "scheduling.tickHz" => (value.GetValue<int>(), 60 / value.GetValue<int>()),
        "scheduling.degradedTickHz" => (value.GetValue<int>(), 60 / value.GetValue<int>()),
        _ => value.GetValue<int>(),
    };

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
