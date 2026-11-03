using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Sweep;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Sweep;

/// <summary>Sweep specifications: validation keyed by JSON path, cell expansion and seeds (contracts/config-and-cli.md).</summary>
public sealed class SweepSpecTests
{
    public static TheoryData<string, string, string> InvalidSpecs => new()
    {
        { "name-missing", "name", "must be a folder name" },
        { "name-path", "name", "must be a folder name" },
        { "name-dots", "name", "must be a folder name" },
        { "base-missing", "base", "is required" },
        { "seeds-zero", "seeds", "must be at least 1" },
        { "mode-number", "mode", "must be OneAtATime or Grid" },
        { "mode-unknown", "mode", "has an invalid value or is not a known field" },
        { "parameters-empty", "parameters", "must list at least one parameter" },
        { "parameters-null", "parameters", "must list at least one parameter" },
        { "parameter-null", "parameters[0]", "is required" },
        { "path-empty", "parameters[0].path", "must be a dotted JSON path" },
        { "path-double-dot", "parameters[0].path", "must be a dotted JSON path" },
        { "path-seed", "parameters[0].path", "is set by the sweep from sweepSeed" },
        { "path-trace", "parameters[0].path", "traces are not supported in sweeps" },
        { "values-empty", "parameters[0].values", "must list at least one value" },
        { "values-repeated", "parameters[0].values", "must not repeat a value" },
        { "values-not-array", "parameters[0].values", "has an invalid value or is not a known field" },
        { "paths-repeated", "parameters[1].path", "must not repeat or overlap parameters[0].path" },
        { "paths-overlap", "parameters[1].path", "must not repeat or overlap parameters[0].path" },
        { "base-trace-record", "base.trace.record", "must be null in a sweep" },
        { "base-trace-replay", "base.trace.replay", "must be null in a sweep" },
        { "unknown-field", "colour", "has an invalid value or is not a known field" },
        { "sweep-seed-negative", "sweepSeed", "has an invalid value or is not a known field" },
        { "swept-value-invalid", "parameters[0].values[1]", "regionizer.sectionSize = 12: must be a power of two between 1 and 1024" },
        { "swept-path-unknown", "parameters[1].values[0]", "regionizer.claimRadius.infantri = 4: has an invalid value or is not a known field" },
        { "swept-scenario-unknown", "parameters[1].values[0]", "scenario = \"Nowhere\": must be one of" },
        { "base-invalid", "base.regionizer.mergeGap", "must be 0 or 1" },
        { "base-not-object", "base", "has an invalid value or is not a known field" },
        { "base-unknown-field", "base.regionizer.colour", "has an invalid value or is not a known field" },
    };

    [Theory]
    [MemberData(nameof(InvalidSpecs))]
    public void InvalidSpecIsRefusedNamingTheField(string variant, string path, string message)
    {
        var spec = Valid();
        var parameters = spec["parameters"]!.AsArray();
        switch (variant)
        {
            case "name-missing":
                spec.Remove("name");
                break;
            case "name-path":
                spec["name"] = "../escape";
                break;
            case "name-dots":
                spec["name"] = "..";
                break;
            case "base-missing":
                spec.Remove("base");
                break;
            case "seeds-zero":
                spec["seeds"] = 0;
                break;
            case "mode-number":
                spec["mode"] = 9;
                break;
            case "mode-unknown":
                spec["mode"] = "Diagonal";
                break;
            case "parameters-empty":
                parameters.Clear();
                break;
            case "parameters-null":
                spec["parameters"] = null;
                break;
            case "parameter-null":
                parameters[0] = null;
                break;
            case "path-empty":
                parameters[0]!["path"] = string.Empty;
                break;
            case "path-double-dot":
                parameters[0]!["path"] = "regionizer..sectionSize";
                break;
            case "path-seed":
                parameters[0]!["path"] = "seed";
                break;
            case "path-trace":
                parameters[0]!["path"] = "trace.record";
                break;
            case "values-empty":
                parameters[0]!["values"] = new JsonArray();
                break;
            case "values-repeated":
                parameters[0]!["values"] = new JsonArray(8, 16, 8);
                break;
            case "values-not-array":
                parameters[0]!["values"] = 8;
                break;
            case "paths-repeated":
                parameters.Add(new JsonObject { ["path"] = "regionizer.sectionSize", ["values"] = new JsonArray(32) });
                break;
            case "paths-overlap":
                parameters[0]!["path"] = "regionizer.claimRadius";
                parameters[0]!["values"] = new JsonArray(new JsonObject { ["infantry"] = 8 });
                parameters.Add(new JsonObject { ["path"] = "regionizer.claimRadius.infantry", ["values"] = new JsonArray(4) });
                break;
            case "base-trace-record":
                spec["base"]!["trace"] = new JsonObject { ["record"] = "trace.csv" };
                break;
            case "base-trace-replay":
                spec["base"]!["trace"] = new JsonObject { ["replay"] = "trace.csv" };
                break;
            case "unknown-field":
                spec["colour"] = "blue";
                break;
            case "sweep-seed-negative":
                spec["sweepSeed"] = -1;
                break;
            case "swept-value-invalid":
                parameters[0]!["values"] = new JsonArray(8, 12);
                break;
            case "swept-path-unknown":
                parameters.Add(new JsonObject { ["path"] = "regionizer.claimRadius.infantri", ["values"] = new JsonArray(4) });
                break;
            case "swept-scenario-unknown":
                parameters.Add(new JsonObject { ["path"] = "scenario", ["values"] = new JsonArray("Nowhere") });
                break;
            case "base-invalid":
                spec["base"]!["regionizer"] = new JsonObject { ["mergeGap"] = 2 };
                break;
            case "base-not-object":
                spec["base"] = 5;
                break;
            case "base-unknown-field":
                spec["base"]!["regionizer"] = new JsonObject { ["colour"] = 2 };
                break;
        }

        var result = SweepSpecLoader.Load(spec);

        Assert.False(result.Succeeded, $"{variant}: the spec should be refused");
        Assert.Null(result.Plan);
        Assert.True(
            result.Errors.Any(e => e.Path == path && e.Message.Contains(message, StringComparison.Ordinal)),
            $"{variant}: expected '{path}: {message}', got: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void AnErrorInTheBaseIsReportedOnceForAllCells()
    {
        var spec = Valid();
        spec["base"]!["regionizer"] = new JsonObject { ["mergeGap"] = 2 };

        var result = SweepSpecLoader.Load(spec);

        Assert.Single(result.Errors, e => e.Path == "base.regionizer.mergeGap");
    }

    [Fact]
    public void AnInvalidEnvironmentNameIsRefused()
    {
        var result = SweepSpecLoader.Load(Valid(), environmentName: "Moon");

        Assert.Contains(result.Errors, e => e.Path == EnvironmentNames.VariableName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-json")]
    [InlineData("array")]
    public void AnUnreadableFileIsRefused(string variant)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sweep.json");
        if (variant == "not-json")
        {
            File.WriteAllText(path, "{ \"name\": ");
        }
        else if (variant == "array")
        {
            File.WriteAllText(path, "[]");
        }

        var result = SweepSpecLoader.LoadFile(path);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains(variant switch { "missing" => "does not exist", "not-json" => "is not valid JSON", _ => "must be a JSON object" }, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidFileLoadsWithItsCommentsAndTrailingCommas()
    {
        var path = SweepSpecs.Write(Valid());
        File.WriteAllText(path, "// a sweep\n" + File.ReadAllText(path)[..^1] + ",}");

        var result = SweepSpecLoader.LoadFile(path);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal("spec", result.Plan!.Spec.Name);
        Assert.Equal("Baseline", result.Plan.Scenario);
    }

    [Fact]
    public void OneAtATimeStepsEachParameterAloneInOrder()
    {
        var spec = SweepSpecs.Spec(
            "one-at-a-time",
            3,
            1,
            SweepSpecs.Base(10),
            "OneAtATime",
            ("regionizer.split.minSections", [16, 32]),
            ("regionizer.split.minDeadPercent", [10, 25, 50]));

        var plan = SweepSpecs.Plan(spec);

        Assert.Equal(
            ["c000 regionizer.split.minSections=16", "c001 regionizer.split.minSections=32", "c002 regionizer.split.minDeadPercent=10", "c003 regionizer.split.minDeadPercent=25", "c004 regionizer.split.minDeadPercent=50"],
            plan.Cells.Select(c => $"{c.CellId} {string.Join(' ', c.Assignments.Select(a => $"{a.Path}={a.Value}"))}"));
        Assert.Equal([0, 0, 1, 1, 1], plan.Cells.Select(c => c.Assignments[0].ParameterIndex));
        Assert.Equal([0, 1, 0, 1, 2], plan.Cells.Select(c => c.Assignments[0].ValueIndex));

        // Each cell moves only its own parameter; the other keeps the scenario default.
        var defaults = TestRuns.Load(SweepSpecs.Base(10)).Config.Regionizer.Split;
        Assert.Equal([16, 32, defaults.MinSections, defaults.MinSections, defaults.MinSections], plan.Runs.Select(r => r.Loaded.Config.Regionizer.Split.MinSections));
        Assert.Equal([defaults.MinDeadPercent, defaults.MinDeadPercent, 10, 25, 50], plan.Runs.Select(r => r.Loaded.Config.Regionizer.Split.MinDeadPercent));
    }

    [Fact]
    public void SweepingTheScenarioRunsEachCellsOwnScenarioAndListsTheBaseScenario()
    {
        var spec = SweepSpecs.Spec("scenarios", 3, 1, SweepSpecs.Base(10), "Grid", ("scenario", ["Baseline", "Migration"]), ("regionizer.sectionSize", [16, 32]));

        var plan = SweepSpecs.Plan(spec);

        Assert.Equal("Baseline", plan.Scenario);
        Assert.Equal(["Baseline", "Baseline", "Migration", "Migration"], plan.Runs.Select(r => r.Loaded.Config.Scenario));
        Assert.Equal([16, 32, 16, 32], plan.Runs.Select(r => r.Loaded.Config.Regionizer.SectionSize));
    }

    [Fact]
    public void WithoutABaseScenarioTheIndexListsTheFirstCellsScenario()
    {
        var baseConfig = SweepSpecs.Base(10);
        baseConfig.Remove("scenario");
        var spec = SweepSpecs.Spec("scenarios", 3, 1, baseConfig, "OneAtATime", ("scenario", ["Migration", "Baseline"]));

        var plan = SweepSpecs.Plan(spec);

        Assert.Equal("Migration", plan.Scenario);
    }

    [Fact]
    public void SeedsAreDerivedFromTheSweepSeedByIndex()
    {
        var spec = new SweepSpec { Name = "s", Base = new JsonObject(), Seeds = 4, SweepSeed = 20261103, Parameters = [] };

        var seeds = SweepCells.Seeds(spec);

        Assert.Equal(Enumerable.Range(0, 4).Select(i => DeterministicRandom.SweepSeed(20261103, i)), seeds);
        Assert.Equal(4, seeds.Distinct().Count());
    }

    [Theory]
    [InlineData(0, "c000")]
    [InlineData(7, "c007")]
    [InlineData(123, "c123")]
    [InlineData(1000, "c1000")]
    public void CellIdsAreNumberedInOrder(int index, string expected)
    {
        Assert.Equal(expected, SweepCells.CellId(index));
    }

    /// <summary>A valid spec: two cells of section size, two seeds, a short Baseline.</summary>
    private static JsonObject Valid() =>
        SweepSpecs.Spec("spec", 11, 2, SweepSpecs.Base(10), "OneAtATime", ("regionizer.sectionSize", [8, 16]));
}
