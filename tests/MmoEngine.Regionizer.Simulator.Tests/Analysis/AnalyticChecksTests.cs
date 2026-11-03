using System.Text.Json;
using System.Text.Json.Serialization;
using CsCheck;
using MmoEngine.Regionizer.Simulator.Analysis;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>The analytic formulas (US6.1, US6.3) and the report <c>regionizer-sim check</c> writes (FR-031).</summary>
public sealed class AnalyticChecksTests
{
    private const ulong Seed = 20261103;

    // US6.1: Given an aircraft interaction range and two aircraft closing at 240 m/s at 30 Hz, When the
    // claim-radius floor is computed, Then it is the interaction range plus 8 m.
    [Theory]
    [InlineData(0.0)]
    [InlineData(8.0)]
    [InlineData(25.5)]
    [InlineData(150.0)]
    public void ClaimRadiusFloorForClosingAircraftIsRangePlus8m(double interactionRange)
    {
        var floor = AnalyticFormulas.ClaimRadiusFloor(interactionRange, closingSpeedMetresPerSecond: 240, tickRateHz: 30);

        Assert.Equal(interactionRange + 8, floor);
    }

    // US6.3: Given a 600 m lane and a merge distance of 30 m, When the lane-bridging estimate is computed
    // for 30, 60 and 120 travellers, Then it gives about 0.04%, 4% and 74% (to the precision the spec states).
    [Theory]
    [InlineData(30, 0.04, 2)]
    [InlineData(60, 4.0, 0)]
    [InlineData(120, 74.0, 0)]
    public void LaneBridgingEstimateMatchesWorkedExamples(int travellers, double expectedPercent, int decimals)
    {
        var probability = AnalyticFormulas.LaneBridgingProbability(travellers, laneLengthMetres: 600, mergeDistanceMetres: 30);

        Assert.Equal(expectedPercent, Math.Round(probability * 100, decimals));
    }

    // Research D1: k = 3 section diagonals section-anchored, 1 position-anchored, plus one for a merge gap of 1.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0, 3)]
    [InlineData(ClaimAnchoring.Section, 1, 4)]
    [InlineData(ClaimAnchoring.Position, 0, 1)]
    [InlineData(ClaimAnchoring.Position, 1, 2)]
    public void MergeDistanceAddsTheAnchoringAndGapDiagonals(ClaimAnchoring anchoring, int mergeGap, int diagonals)
    {
        var dMax = AnalyticFormulas.MergeDistance(16, 24, 16, anchoring, mergeGap);

        Assert.Equal(40 + (diagonals * 16 * Math.Sqrt(2)), dMax, 1e-9);
    }

    [Fact]
    public void LaneWithNoTravellersNeverBridges()
    {
        Assert.Equal(0, AnalyticFormulas.LaneBridgingProbability(0, 600, 30));
    }

    public static TheoryData<string, Action> InvalidFormulaInputs() => new()
    {
        { "negative range", () => AnalyticFormulas.ClaimRadiusFloor(-1, 240, 30) },
        { "infinite speed", () => AnalyticFormulas.ClaimRadiusFloor(8, double.PositiveInfinity, 30) },
        { "zero tick rate", () => AnalyticFormulas.ClaimRadiusFloor(8, 240, 0) },
        { "negative radius", () => AnalyticFormulas.MergeDistance(-1, 16, 16, ClaimAnchoring.Section, 0) },
        { "NaN radius", () => AnalyticFormulas.MergeDistance(16, double.NaN, 16, ClaimAnchoring.Section, 0) },
        { "zero section size", () => AnalyticFormulas.MergeDistance(16, 16, 0, ClaimAnchoring.Section, 0) },
        { "merge gap 2", () => AnalyticFormulas.MergeDistance(16, 16, 16, ClaimAnchoring.Section, 2) },
        { "unknown anchoring", () => AnalyticFormulas.MergeDistance(16, 16, 16, (ClaimAnchoring)7, 0) },
        { "negative travellers", () => AnalyticFormulas.LaneBridgingProbability(-1, 600, 30) },
        { "zero lane", () => AnalyticFormulas.LaneBridgingProbability(30, 0, 30) },
        { "negative merge distance", () => AnalyticFormulas.LaneBridgingProbability(30, 600, -30) },
    };

    [Theory]
    [MemberData(nameof(InvalidFormulaInputs))]
    public void InvalidFormulaInputIsRejected(string because, Action compute)
    {
        Assert.NotNull(because);
        Assert.Throws<ArgumentOutOfRangeException>(compute);
    }

    [Fact]
    public void LaneBridgingEstimateIsAProbabilityThatGrowsWithTheMergeDistance()
    {
        Gen.Select(Gen.Int[0, 400], Gen.Double[1, 5000], Gen.Double[0, 200], Gen.Double[0, 200])
            .Sample((travellers, lane, d1, d2) =>
            {
                var shorter = AnalyticFormulas.LaneBridgingProbability(travellers, lane, Math.Min(d1, d2));
                var longer = AnalyticFormulas.LaneBridgingProbability(travellers, lane, Math.Max(d1, d2));
                return shorter is >= 0 and <= 1 && longer is >= 0 and <= 1 && shorter <= longer;
            }, seed: "0000001dizO1");
    }

    [Fact]
    public void MergeDistanceBoundsTheSumOfRadiiAndOrdersTheModes()
    {
        Gen.Select(Gen.Double[0, 500], Gen.Double[0, 500], Gen.Int[0, 10])
            .Sample((ra, rb, power) =>
            {
                var s = 1 << power;
                var position = AnalyticFormulas.MergeDistance(ra, rb, s, ClaimAnchoring.Position, 0);
                var section = AnalyticFormulas.MergeDistance(ra, rb, s, ClaimAnchoring.Section, 0);
                var positionGap = AnalyticFormulas.MergeDistance(ra, rb, s, ClaimAnchoring.Position, 1);
                return ra + rb < position && position < section && position < positionGap;
            }, seed: "0000001dizR2");
    }

    [Theory]
    [InlineData(ClaimAnchoring.Section, 3)]
    [InlineData(ClaimAnchoring.Position, 1)]
    public void SectionSlackIsKDiagonals(ClaimAnchoring anchoring, int k)
    {
        Assert.Equal(k * Math.Sqrt(2) * 16, AnalyticFormulas.SectionSlack(16, anchoring), 1e-12);
    }

    [Fact]
    public void RunAllWritesChecksJsonWithEveryCheck()
    {
        var dir = TempDir();
        var options = SmallOptions(tolerance: 0.10);

        var report = AnalyticChecks.RunAll(options, dir);

        Assert.True(report.Passed, $"seed {Seed}: {FirstFailure(report)}");
        var path = Path.Combine(dir, AnalyticChecks.FileName);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        Assert.Equal(AnalyticChecks.FormatVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(Seed, root.GetProperty("seed").GetUInt64());
        Assert.True(root.GetProperty("passed").GetBoolean());

        // One floor result per case and anchoring mode, each with its at-floor and below-floor approaches.
        var floors = root.GetProperty("claimRadiusFloor").EnumerateArray().ToList();
        Assert.Equal(2, floors.Count);
        Assert.Equal(["Section", "Position"], floors.Select(f => f.GetProperty("anchoring").GetString()));
        Assert.All(floors, f =>
        {
            Assert.Equal("Aircraft", f.GetProperty("claimant").GetString());
            Assert.Equal(16, f.GetProperty("floorMetres").GetDouble());
            Assert.Equal(16, f.GetProperty("atFloor").GetProperty("radiusMetres").GetDouble());
            Assert.Equal(0, f.GetProperty("atFloor").GetProperty("separatedBoundaries").GetInt32());
            Assert.True(f.GetProperty("belowFloor").GetProperty("separatedBoundaries").GetInt32() > 0, $"seed {Seed}: no failing boundary below the floor");
            Assert.True(f.GetProperty("passed").GetBoolean());
        });

        // One merge-distance result per mode and gap.
        var merges = root.GetProperty("mergeDistance").EnumerateArray()
            .Select(m => (m.GetProperty("anchoring").GetString(), m.GetProperty("mergeGap").GetInt32()))
            .ToList();
        Assert.Equal([("Section", 0), ("Section", 1), ("Position", 0), ("Position", 1)], merges);

        // The lane-bridging setup FR-031 fixes, recorded with its steps.
        var lane = root.GetProperty("laneBridging");
        Assert.Equal("Position", lane.GetProperty("anchoring").GetString());
        Assert.Equal(0, lane.GetProperty("mergeGap").GetInt32());
        Assert.Equal(1, lane.GetProperty("sectionSize").GetInt32());
        Assert.Equal(15, lane.GetProperty("claimRadiusMetres").GetDouble());
        Assert.Equal(30, lane.GetProperty("mergeDistanceMetres").GetDouble());
        var steps = lane.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([120], steps.Select(s => s.GetProperty("travellers").GetInt32()));
        Assert.All(steps, s => Assert.True(s.GetProperty("withinTolerance").GetBoolean()));
    }

    // FR-031: a disagreement outside tolerance is flagged, in the report and in checks.json.
    [Fact]
    public void DisagreementOutsideToleranceFailsTheReport()
    {
        var dir = TempDir();
        var options = SmallOptions(tolerance: 1e-9);

        var report = AnalyticChecks.RunAll(options, dir);

        Assert.False(report.Passed, $"seed {Seed}: a 1e-9 tolerance should flag the lane step");
        Assert.False(report.LaneBridging.Steps[0].WithinTolerance);
        Assert.NotNull(report.LaneBridging.FirstFailure);
        Assert.Contains($"seed {Seed}", report.LaneBridging.FirstFailure, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, AnalyticChecks.FileName)));
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
    }

    // SC-006 for the defaults `regionizer-sim check` runs: each floor case and merge-distance mode of
    // AnalyticCheckOptions.Default, on its own so each test stays short. The default lane steps are the FR-031
    // setup at seed 20261103, which SimulatorAgreementTests.LaneBridgingSimulationMatchesEstimateWithinTenPoints runs.
    public static TheoryData<string, int> DefaultChecks()
    {
        var data = new TheoryData<string, int>();
        for (var i = 0; i < AnalyticCheckOptions.Default.Floor.Cases.Count; i++)
        {
            data.Add("floor", i);
        }

        for (var i = 0; i < 4; i++)
        {
            data.Add("merge-distance", i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DefaultChecks))]
    public void EveryDefaultCheckIsWithinTolerance(string check, int index)
    {
        var defaults = AnalyticCheckOptions.Default;
        Assert.Equal(Seed, defaults.Seed);
        Assert.Equal(LaneBridgingCheckOptions.Fr031, defaults.LaneBridging);
        if (check == "floor")
        {
            var floorCase = defaults.Floor.Cases[index];

            var results = SimulatorAgreement.ClaimRadiusFloor(defaults.Floor with { Cases = [floorCase] }, defaults.Seed);

            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.NotNull(r.BelowFloor);
                Assert.True(r.Passed, $"seed {defaults.Seed}, {r.Name} {r.Anchoring}: {r.FirstFailure}");
            });
            return;
        }

        var (anchoring, gap) = index switch
        {
            0 => (ClaimAnchoring.Section, 0),
            1 => (ClaimAnchoring.Section, 1),
            2 => (ClaimAnchoring.Position, 0),
            _ => (ClaimAnchoring.Position, 1),
        };

        var result = SimulatorAgreement.MergeDistance(defaults.MergeDistance, anchoring, gap, defaults.Seed);

        Assert.True(result.Passed, $"seed {defaults.Seed}, {anchoring} gap {gap}: {result.FirstFailure}");
    }

    // RunAll runs its cases in parallel; each draws from its own stream, so the report equals the checks run one by one.
    [Fact]
    public void RunAllMatchesTheChecksRunOneByOne()
    {
        var options = SmallOptions(tolerance: 0.10) with
        {
            Floor = new FloorCheckOptions(
                [new ClaimRadiusFloorCase("aircraft", 8, 240, 30), new ClaimRadiusFloorCase("vehicle", 15, 60, 30, EntityClass.GroundVehicle)],
                SectionSize: 1,
                TrialsPerAnchoring: 3),
        };
        options = options with { LaneBridging = options.LaneBridging with { Travellers = [10, 20] } };

        var report = AnalyticChecks.RunAll(options);

        Assert.Equal(SimulatorAgreement.ClaimRadiusFloor(options.Floor, Seed), report.ClaimRadiusFloor);
        Assert.Equal(
            [
                SimulatorAgreement.MergeDistance(options.MergeDistance, ClaimAnchoring.Section, 0, Seed),
                SimulatorAgreement.MergeDistance(options.MergeDistance, ClaimAnchoring.Section, 1, Seed),
                SimulatorAgreement.MergeDistance(options.MergeDistance, ClaimAnchoring.Position, 0, Seed),
                SimulatorAgreement.MergeDistance(options.MergeDistance, ClaimAnchoring.Position, 1, Seed),
            ],
            report.MergeDistance);
        var lane = SimulatorAgreement.LaneBridging(options.LaneBridging, Seed);
        Assert.Equal(lane.Steps, report.LaneBridging.Steps);
        Assert.Equal(lane with { Steps = report.LaneBridging.Steps }, report.LaneBridging);
    }

    // An invalid input fails the whole run with the first error in check order, whatever finishes first.
    [Fact]
    public void RunAllRejectsInvalidInputDeterministically()
    {
        var options = SmallOptions(tolerance: 0.10) with
        {
            Floor = new FloorCheckOptions([new ClaimRadiusFloorCase("odd", 8, 240, 7)], 1, 1),
        };
        options = options with { LaneBridging = options.LaneBridging with { TrialsPerStep = 199 } };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => AnalyticChecks.RunAll(options));

        Assert.Contains("Case 'odd'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunAllWithNoLaneStepsDoesNotPass()
    {
        var options = SmallOptions(tolerance: 0.10);
        options = options with { LaneBridging = options.LaneBridging with { Travellers = [] } };

        var report = AnalyticChecks.RunAll(options);

        Assert.Empty(report.LaneBridging.Steps);
        Assert.False(report.LaneBridging.Passed);
        Assert.False(report.Passed);
    }

    [Fact]
    public void RunAllWithNoFloorCasesDoesNotPass()
    {
        var options = SmallOptions(tolerance: 0.10) with { Floor = new FloorCheckOptions([], 16, 1) };

        var report = AnalyticChecks.RunAll(options);

        // No floor cases means nothing was checked, which is not a pass.
        Assert.Empty(report.ClaimRadiusFloor);
        Assert.False(report.Passed);
    }

    [Fact]
    public void DefaultOptionsAreTheWorkedExamples()
    {
        var options = AnalyticCheckOptions.Default;

        Assert.Contains(options.Floor.Cases, c => c.Name == "aircraft" && c.Claimant == EntityClass.Aircraft && c.ClosingSpeedMetresPerSecond == 240 && c.TickRateHz == 30);
        Assert.Equal([EntityClass.Infantry, EntityClass.GroundVehicle, EntityClass.Aircraft], options.Floor.Cases.Select(c => c.Claimant));

        // Every default case has a radius below the floor by more than the section slack, so both halves of D16 run.
        foreach (var floorCase in options.Floor.Cases)
        {
            foreach (var anchoring in new[] { ClaimAnchoring.Section, ClaimAnchoring.Position })
            {
                Assert.NotNull(SimulatorAgreement.BelowFloorRadius(floorCase, anchoring, options.Floor.SectionSize));
            }
        }

        Assert.Same(LaneBridgingCheckOptions.Fr031, options.LaneBridging);
        Assert.Equal(600, options.LaneBridging.LaneLengthMetres);
        Assert.Equal(30, options.LaneBridging.MergeDistanceMetres);
        Assert.Equal([30, 60, 120], options.LaneBridging.Travellers);
        Assert.True(options.LaneBridging.TrialsPerStep >= 200);
        Assert.Equal(0.10, options.LaneBridging.TolerancePoints);
    }

    // `regionizer-sim check --options` reads the same shape: every field, camelCase, enums as names.
    [Fact]
    public void CheckOptionsRoundTripThroughJson()
    {
        var json = OptionsJson(AnalyticCheckOptions.Default);

        var read = AnalyticCheckOptions.FromJson(json);

        Assert.Equal(AnalyticCheckOptions.Default.Seed, read.Seed);
        Assert.Equal(AnalyticCheckOptions.Default.Floor.Cases, read.Floor.Cases);
        Assert.Equal(AnalyticCheckOptions.Default.Floor.SectionSize, read.Floor.SectionSize);
        Assert.Equal(AnalyticCheckOptions.Default.Floor.TrialsPerAnchoring, read.Floor.TrialsPerAnchoring);
        Assert.Equal(AnalyticCheckOptions.Default.MergeDistance, read.MergeDistance);
        Assert.Equal(AnalyticCheckOptions.Default.LaneBridging.Travellers, read.LaneBridging.Travellers);
        Assert.Equal(AnalyticCheckOptions.Default.LaneBridging with { Travellers = read.LaneBridging.Travellers }, read.LaneBridging);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{ "seed": 1 }""")]
    [InlineData("""{ "seed": 1, "floor": null, "mergeDistance": null, "laneBridging": null }""")]
    [InlineData("""{ "seed": 1, "floor": { "cases": [], "sectionSize": 1, "trialsPerAnchoring": 1 }, "mergeDistance": { "sectionSize": 16, "radiusA": 1, "radiusB": 1, "trials": 1 }, "laneBridging": { "laneLengthMetres": 600, "claimRadiusMetres": 15, "sectionSize": 1, "travellers": [30], "trialsPerStep": 200, "tolerancePoints": 0.1 }, "extra": 1 }""")]
    [InlineData("""{ "seed": 1, "floor": { "cases": [ { "name": "a", "interactionRangeMetres": 8, "closingSpeedMetresPerSecond": 240, "tickRateHz": 30, "claimant": "Tank" } ], "sectionSize": 1, "trialsPerAnchoring": 1 }, "mergeDistance": { "sectionSize": 16, "radiusA": 1, "radiusB": 1, "trials": 1 }, "laneBridging": { "laneLengthMetres": 600, "claimRadiusMetres": 15, "sectionSize": 1, "travellers": [30], "trialsPerStep": 200, "tolerancePoints": 0.1 } }""")]
    public void InvalidCheckOptionsAreRejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => AnalyticCheckOptions.FromJson(json));
    }

    /// <summary>Check options as `regionizer-sim check --options` reads them.</summary>
    internal static string OptionsJson(AnalyticCheckOptions options) => JsonSerializer.Serialize(options, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    });

    /// <summary>Small check options: one aircraft floor at 1 m sections, a few merge-distance trials and one lane step.</summary>
    internal static AnalyticCheckOptions SmallOptions(double tolerance) => new(
        Seed,
        new FloorCheckOptions([new ClaimRadiusFloorCase("aircraft", 8, 240, 30)], SectionSize: 1, TrialsPerAnchoring: 5),
        new MergeDistanceCheckOptions(SectionSize: 16, RadiusA: 16, RadiusB: 24, Trials: 5),
        LaneBridgingCheckOptions.Fr031 with { Travellers = [120], TrialsPerStep = LaneBridgingCheckOptions.MinimumTrialsPerStep, TolerancePoints = tolerance });

    private static string FirstFailure(CheckReport report) =>
        report.ClaimRadiusFloor.Select(f => f.FirstFailure)
            .Concat(report.MergeDistance.Select(m => m.FirstFailure))
            .Append(report.LaneBridging.FirstFailure)
            .FirstOrDefault(f => f is not null) ?? "no failure recorded";

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "mmo-tests", Guid.NewGuid().ToString("N"));
}
