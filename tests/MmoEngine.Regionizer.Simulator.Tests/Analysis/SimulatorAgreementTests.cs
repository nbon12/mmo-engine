using MmoEngine.Regionizer.Simulator.Analysis;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// The analytic results against the regionizer core (US6.2, FR-031, SC-006). Every check runs lockstep
/// ticks on the fixed 60 Hz time base with a fixed seed, and the invariant checker after every tick.
/// </summary>
public sealed class SimulatorAgreementTests
{
    private const ulong Seed = 20261103;

    // US6.2: Given two entities stepped apart one section at a time in each anchoring mode, When the
    // merge-distance check runs, Then every pair within the sum of their radii shares a region, and no pair
    // farther apart than the merge distance d_max shares a region because of their claims.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0, 16, 16.0, 24.0)]
    [InlineData(ClaimAnchoring.Section, 1, 16, 16.0, 24.0)]
    [InlineData(ClaimAnchoring.Position, 0, 16, 16.0, 24.0)]
    [InlineData(ClaimAnchoring.Position, 1, 16, 16.0, 24.0)]
    [InlineData(ClaimAnchoring.Section, 0, 8, 0.0, 12.0)]
    [InlineData(ClaimAnchoring.Position, 1, 32, 20.0, 20.0)]
    public void MergeDistanceBoundsHoldAgainstTheCore(ClaimAnchoring anchoring, int mergeGap, int sectionSize, double radiusA, double radiusB)
    {
        var options = new MergeDistanceCheckOptions(sectionSize, radiusA, radiusB, Trials: 40);

        var result = SimulatorAgreement.MergeDistance(options, anchoring, mergeGap, Seed);

        var context = $"seed {Seed}, {anchoring}, gap {mergeGap}, s {sectionSize}, radii {radiusA} and {radiusB}: {result.FirstFailure}";
        Assert.Equal(radiusA + radiusB, result.LowerBoundMetres);
        Assert.Equal(AnalyticFormulas.MergeDistance(radiusA, radiusB, sectionSize, anchoring, mergeGap), result.MergeDistanceMetres);
        Assert.True(result.WithinLowerBound > 0, $"{context}: no pair was within rA + rB");
        Assert.True(result.BeyondMergeDistance > 0, $"{context}: no pair was beyond d_max");
        Assert.True(result.LowerBoundViolations == 0, $"{context}: {result.LowerBoundViolations} pairs within rA + rB were in two regions");
        Assert.True(result.UpperBoundViolations == 0, $"{context}: {result.UpperBoundViolations} pairs beyond d_max shared a region");
        Assert.True(result.InvariantViolations == 0, $"{context}: {result.InvariantViolations} invariant violations");
        Assert.True(result.Passed, context);
    }

    // FR-031: the lane-bridging estimate matches the core within 10 percentage points at each traffic step,
    // with uniformly random travellers and at least 200 trials per step (here 400; 600 m lane, d = 30 m).
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void LaneBridgingSimulationMatchesEstimateWithinTenPoints(int travellers)
    {
        var options = new LaneBridgingCheckOptions(
            LaneLengthMetres: 600,
            MergeDistanceMetres: 30,
            SectionSize: 1,
            Travellers: [travellers],
            TrialsPerStep: 400,
            TolerancePoints: 0.10);

        var result = SimulatorAgreement.LaneBridging(options, Seed);

        var step = Assert.Single(result.Steps);
        var context = $"seed {Seed}, {travellers} travellers: simulated {step.Simulated}, estimate {step.Estimate}";
        Assert.Equal(400, step.Trials);
        Assert.Equal(AnalyticFormulas.LaneBridgingProbability(travellers, 600, 30), step.Estimate);
        Assert.Equal(step.Bridged / 400.0, step.Simulated);
        Assert.True(Math.Abs(step.Simulated - step.Estimate) <= 0.10, context);
        Assert.True(step.WithinTolerance, context);
        Assert.True(result.InvariantViolations == 0, $"{context}: {result.FirstFailure}");
        Assert.True(result.Passed, context);
    }

    // FR-031: the floor matches exactly. A claimant whose radius is the floor is in one region with a target
    // closing at the case's speed at the tick end before the target comes within interaction range.
    [Theory]
    [InlineData("infantry", 10.0, 14.0, 30.0)]
    [InlineData("groundVehicle", 15.0, 60.0, 30.0)]
    [InlineData("aircraft", 8.0, 240.0, 30.0)]
    [InlineData("aircraft-20Hz", 8.0, 240.0, 20.0)]
    [InlineData("static", 5.0, 0.0, 30.0)]
    public void ClaimRadiusFloorHoldsAgainstTheCore(string name, double range, double closingSpeed, double tickRate)
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase(name, range, closingSpeed, tickRate)], SectionSize: 16, TrialsPerAnchoring: 20);

        var results = SimulatorAgreement.ClaimRadiusFloor(options, Seed);

        Assert.Equal([ClaimAnchoring.Section, ClaimAnchoring.Position], results.Select(r => r.Anchoring));
        foreach (var result in results)
        {
            var context = $"seed {Seed}, {name}, {result.Anchoring}: {result.FirstFailure}";
            Assert.Equal(range + (closingSpeed / tickRate), result.FloorMetres);
            Assert.True(result.FloorMatches, $"{context}: simulated floor {result.SimulatedFloorMetres}, formula {result.FloorMetres}");
            Assert.True(result.InteractionTicks > 0, $"{context}: no tick ended within interaction range");
            Assert.True(result.InteractionsWhileSeparated == 0, $"{context}: {result.InteractionsWhileSeparated} interactions across regions");
            Assert.True(result.InvariantViolations == 0, context);
            Assert.True(result.Passed, context);
        }
    }

    [Fact]
    public void SameSeedGivesTheSameMergeDistanceResult()
    {
        var options = new MergeDistanceCheckOptions(16, 16, 24, Trials: 10);

        var first = SimulatorAgreement.MergeDistance(options, ClaimAnchoring.Section, 1, Seed);
        var second = SimulatorAgreement.MergeDistance(options, ClaimAnchoring.Section, 1, Seed);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FewerThan200LaneTrialsPerStepAreRejected()
    {
        var options = new LaneBridgingCheckOptions(600, 30, 1, [60], TrialsPerStep: 199, TolerancePoints: 0.10);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.LaneBridging(options, Seed));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void LaneToleranceOutsideZeroToOneIsRejected(double tolerance)
    {
        var options = new LaneBridgingCheckOptions(600, 30, 1, [60], TrialsPerStep: 200, TolerancePoints: tolerance);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.LaneBridging(options, Seed));
    }

    [Theory]
    [InlineData(7.0)]
    [InlineData(120.0)]
    public void FloorTickRateThatDoesNotDivideTheTimeBaseIsRejected(double tickRate)
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase("odd", 8, 240, tickRate)], 16, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.ClaimRadiusFloor(options, Seed));
    }

    [Fact]
    public void ZeroTrialsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.MergeDistance(new MergeDistanceCheckOptions(16, 16, 16, 0), ClaimAnchoring.Section, 0, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.ClaimRadiusFloor(new FloorCheckOptions([new ClaimRadiusFloorCase("a", 8, 240, 30)], 16, 0), Seed));
    }
}
