using MmoEngine.Regionizer.Simulator.Analysis;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// How the agreement checks classify what they observe (FR-031). A correct core never produces a
/// violation, so these feed the tallies observations directly to show each violation is counted and the
/// first one is reported with its seed.
/// </summary>
public sealed class CheckTalliesTests
{
    private const ulong Seed = 7;
    private const double LowerBound = 40;
    private const double MergeDistance = 107.9;

    [Theory]
    [InlineData(10.0, false, 1, 0)]
    [InlineData(10.0, true, 0, 0)]
    [InlineData(80.0, false, 0, 0)]
    [InlineData(80.0, true, 0, 0)]
    [InlineData(200.0, false, 0, 0)]
    [InlineData(200.0, true, 0, 1)]
    public void MergeDistanceTallyCountsOnlyBreachesOfTheBounds(double distance, bool shared, int lowerViolations, int upperViolations)
    {
        var log = new FailureLog(Seed);
        var tally = new MergeDistanceTally(LowerBound, MergeDistance, log);

        tally.Observe(trial: 3, step: 5, 0, 0, distance, 0, shared);

        Assert.Equal(1, tally.Observations);
        Assert.Equal(distance <= LowerBound ? 1 : 0, tally.WithinLowerBound);
        Assert.Equal(distance > MergeDistance ? 1 : 0, tally.BeyondMergeDistance);
        Assert.Equal(lowerViolations, tally.LowerBoundViolations);
        Assert.Equal(upperViolations, tally.UpperBoundViolations);
        Assert.Equal(shared ? distance : null, tally.FarthestShared);
        Assert.Equal(shared ? null : distance, tally.ClosestSeparated);
        if (lowerViolations + upperViolations == 0)
        {
            Assert.Null(log.First);
        }
        else
        {
            Assert.StartsWith($"seed {Seed}, trial 3, step 5: ", log.First, StringComparison.Ordinal);
        }
    }

    // D16: every boundary within reach (here 16 m) must find the pair in one region; boundaries beyond it are not counted.
    [Fact]
    public void FloorTallyFlagsABoundaryWithinReachThatFindsThePairApart()
    {
        var log = new FailureLog(Seed);
        var tally = new FloorTally(reach: 16, log);
        tally.StartApproach();

        tally.Observe(0, 0, 0, 0, 30, 0, shared: false);
        tally.Observe(0, 1, 0, 0, 22, 0, shared: false);
        tally.Observe(0, 2, 0, 0, 14, 0, shared: false);
        tally.Observe(0, 3, 0, 0, 6, 0, shared: true);

        Assert.Equal(2, tally.Boundaries);
        Assert.Equal(1, tally.SeparatedBoundaries);
        Assert.Equal(8, tally.MaxClosingPerTick);
        Assert.StartsWith($"seed {Seed}, trial 0, step 2: ", log.First, StringComparison.Ordinal);
        Assert.Contains("two regions within reach", log.First, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(16.0, true, 1, 0)]
    [InlineData(16.0, false, 1, 1)]
    [InlineData(16.000001, false, 0, 0)]
    [InlineData(0.0, false, 1, 1)]
    public void FloorTallyCountsOnlyBoundariesWithinReach(double distance, bool shared, int boundaries, int separated)
    {
        var log = new FailureLog(Seed);
        var tally = new FloorTally(reach: 16, log);
        tally.StartApproach();

        tally.Observe(0, 0, 0, 0, distance, 0, shared);

        Assert.Equal(boundaries, tally.Boundaries);
        Assert.Equal(separated, tally.SeparatedBoundaries);
        Assert.Equal(0, tally.MaxClosingPerTick);
        Assert.Equal(separated > 0, log.First is not null);
    }

    // A new approach starts from its own placement: the jump from the last approach's end is not a closing.
    [Fact]
    public void FloorTallyMeasuresClosingWithinAnApproachOnly()
    {
        var log = new FailureLog(Seed);
        var tally = new FloorTally(reach: 16, log);

        tally.StartApproach();
        tally.Observe(0, 0, 0, 0, 40, 0, shared: false);
        tally.Observe(0, 1, 0, 0, 37, 0, shared: false);
        tally.StartApproach();
        tally.Observe(1, 0, 0, 0, 20, 0, shared: false);
        tally.Observe(1, 1, 0, 0, 18, 0, shared: false);

        Assert.Equal(3, tally.MaxClosingPerTick);
        Assert.Equal(0, tally.Boundaries);
    }

    [Fact]
    public void FailureLogCountsEveryInvariantViolationAndKeepsTheFirst()
    {
        var log = new FailureLog(Seed);

        log.Invariants(1, 2, []);
        log.Invariants(1, 3, [new Violation(InvariantRule.SectionSingleOwner, "first"), new Violation(InvariantRule.ClaimsInRegion, "second")]);
        log.Invariants(1, 4, [new Violation(InvariantRule.LegalState, "later")]);

        Assert.Equal(3, log.InvariantViolations);
        Assert.Equal($"seed {Seed}, trial 1, step 3: invariant SectionSingleOwner broken: first", log.First);
    }

    // A correct core never fails the floor check, so these build results directly to show that each way it can
    // fail is reported first, in this order, with the seed.
    [Theory]
    [InlineData("passes", null)]
    [InlineData("floor-mismatch", "seed 7, the closing measured per tick gives a floor of 17 m against the formula's 16 m")]
    [InlineData("invariant-at-floor", "seed 7, trial 0, step 1: invariant at the floor")]
    [InlineData("invariant-below-floor", "seed 7, trial 0, step 1: invariant below the floor")]
    [InlineData("no-boundary", "seed 7, no tick boundary came within reach (16 m) in 5 approaches")]
    [InlineData("separated-at-floor", "at the floor, radius 16 m: seed 7, trial 2, step 3: two regions within reach")]
    [InlineData("below-never-separated", "seed 7, a radius of 6.5 m, below the floor by more than the section slack (1.414 m), found the pair in one region at all 9 boundaries within reach in 5 approaches")]
    public void FloorResultReportsItsFirstFailureWithTheSeed(string variant, string? expected)
    {
        var atFloor = new FloorApproachResult(16, 5, 10, 0, 8, 0, null, null);
        var below = new FloorApproachResult(6.5, 5, 9, 4, 8, 0, "seed 7, trial 1, step 2: apart", null);
        var simulatedFloor = 16.0;
        switch (variant)
        {
            case "floor-mismatch":
                simulatedFloor = 17;
                break;
            case "invariant-at-floor":
                atFloor = atFloor with { InvariantViolations = 1, FirstInvariantViolation = "seed 7, trial 0, step 1: invariant at the floor" };
                break;
            case "invariant-below-floor":
                below = below with { InvariantViolations = 1, FirstInvariantViolation = "seed 7, trial 0, step 1: invariant below the floor" };
                break;
            case "no-boundary":
                atFloor = atFloor with { Boundaries = 0 };
                break;
            case "separated-at-floor":
                atFloor = atFloor with { SeparatedBoundaries = 1, FirstSeparated = "seed 7, trial 2, step 3: two regions within reach" };
                break;
            case "below-never-separated":
                below = below with { SeparatedBoundaries = 0, FirstSeparated = null };
                break;
        }

        var result = new FloorCheckResult(
            "aircraft", EntityClass.Aircraft, ClaimAnchoring.Position, 1, 8, 240, 30, 16, Math.Sqrt(2), simulatedFloor, atFloor, below, null);

        Assert.Equal(expected, SimulatorAgreement.FirstFloorFailure(result, Seed));
        Assert.Equal(expected is null, result.Passed);
    }

    [Fact]
    public void ReportListsEveryCheckOutsideTolerance()
    {
        var floor = new FloorCheckResult(
            "aircraft", EntityClass.Aircraft, ClaimAnchoring.Section, 1, 8, 240, 30, 16, 3 * Math.Sqrt(2), 17,
            new FloorApproachResult(16, 5, 10, 0, 9, 0, null, null), null, "seed 7, the floor differs");
        var merge = new MergeDistanceCheckResult(
            ClaimAnchoring.Position, 1, 16, 0, 0, 0, 45, 5, 50, WithinLowerBound: 0, BeyondMergeDistance: 10, 0, 0, 0, null, null, null);
        var lane = new LaneBridgingCheckResult(600, 15, 30, 31.4, 1, ClaimAnchoring.Position, 0, 0.1, [], 0, null);

        var failing = new CheckReport(1, Seed, [floor], [merge], lane);
        var empty = new CheckReport(1, Seed, [], [], lane);

        Assert.False(failing.Passed);
        Assert.Equal(
            [
                "claim-radius floor aircraft Section: seed 7, the floor differs",
                "merge distance Position gap 1: a bound was not exercised",
                "lane bridging: no traffic steps",
            ],
            failing.Failures());
        Assert.Equal(["claim-radius floor: no cases", "merge distance: no modes", "lane bridging: no traffic steps"], empty.Failures());
    }
}
