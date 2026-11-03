using MmoEngine.Regionizer.Simulator.Analysis;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// The analytic results against the regionizer core (US6.1, US6.2, FR-031, SC-006). Every check runs
/// lockstep ticks on the fixed 60 Hz time base with a fixed seed, and the invariant checker after every tick.
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

    // FR-031: the lane-bridging estimate matches the core within 10 percentage points at each traffic step, in
    // the setup FR-031 fixes: position anchoring, merge gap 0, 1 m sections, 15 m radii, a 600 m lane with a
    // 15 m-radius entity at each end, uniformly placed travellers, 400 trials per step, compared at d = 30 m.
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void LaneBridgingSimulationMatchesEstimateWithinTenPoints(int travellers)
    {
        var options = LaneBridgingCheckOptions.Fr031 with { Travellers = [travellers] };

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

    // FR-031: the check runs exactly the setup the requirement fixes, and records it in its result.
    [Fact]
    public void LaneBridgingCheckRunsTheFr031Setup()
    {
        var setup = LaneBridgingCheckOptions.Fr031;

        var result = SimulatorAgreement.LaneBridging(setup with { Travellers = [30], TrialsPerStep = LaneBridgingCheckOptions.MinimumTrialsPerStep }, Seed);

        Assert.Equal(600, setup.LaneLengthMetres);
        Assert.Equal(15, setup.ClaimRadiusMetres);
        Assert.Equal(1, setup.SectionSize);
        Assert.Equal([30, 60, 120], setup.Travellers);
        Assert.True(setup.TrialsPerStep >= 200, $"{setup.TrialsPerStep} trials per step");
        Assert.Equal(0.10, setup.TolerancePoints);
        Assert.Equal(ClaimAnchoring.Position, result.Anchoring);
        Assert.Equal(0, result.MergeGap);
        Assert.Equal(1, result.SectionSize);
        Assert.Equal(15, result.ClaimRadiusMetres);
        Assert.Equal(600, result.LaneLengthMetres);
        Assert.Equal(30, result.MergeDistanceMetres);
        Assert.Equal(30 + Math.Sqrt(2), result.MergesUpToMetres, 1e-12);
        Assert.Equal(200, Assert.Single(result.Steps).Trials);
        Assert.Same(setup, AnalyticCheckOptions.Default.LaneBridging);
    }

    // US6.1 (second part): Given an aircraft (or another class) with a claim radius at the floor closing head-on
    // on an unoccupied capture point, which claims only its own section, When ticks run, Then at every tick
    // boundary within reach (d_interact + v_rel·Δt) they share a region. When the radius is below the floor by
    // more than the section slack k·√2·s, Then at least one such boundary finds them in different regions.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 1, EntityClass.Aircraft, 8.0, 240.0, 30.0)]
    [InlineData(ClaimAnchoring.Position, 1, EntityClass.Aircraft, 8.0, 240.0, 30.0)]
    [InlineData(ClaimAnchoring.Section, 1, EntityClass.Aircraft, 8.0, 240.0, 20.0)]
    [InlineData(ClaimAnchoring.Section, 2, EntityClass.GroundVehicle, 15.0, 60.0, 30.0)]
    [InlineData(ClaimAnchoring.Position, 4, EntityClass.Infantry, 10.0, 14.0, 30.0)]
    [InlineData(ClaimAnchoring.Position, 2, EntityClass.Traveller, 10.0, 30.0, 30.0)]
    [InlineData(ClaimAnchoring.Section, 16, EntityClass.Aircraft, 100.0, 240.0, 30.0)]
    [InlineData(ClaimAnchoring.Position, 16, EntityClass.Aircraft, 100.0, 240.0, 30.0)]
    public void ClaimRadiusFloorHoldsInTheCore_BelowFloorFails(
        ClaimAnchoring anchoring, int sectionSize, EntityClass claimant, double range, double closingSpeed, double tickRate)
    {
        var floorCase = new ClaimRadiusFloorCase("case", range, closingSpeed, tickRate, claimant);
        var options = new FloorCheckOptions([floorCase], sectionSize, TrialsPerAnchoring: 20);
        var floor = range + (closingSpeed / tickRate);
        var slack = AnalyticFormulas.MergeDistance(0, 0, sectionSize, anchoring, 0);

        var result = Assert.Single(SimulatorAgreement.ClaimRadiusFloor(options, Seed), r => r.Anchoring == anchoring);

        var context = $"seed {Seed}, {claimant} {anchoring}, s {sectionSize}: {result.FirstFailure}";
        Assert.Equal(floor, result.FloorMetres);
        Assert.Equal(slack, result.SectionSlackMetres);
        Assert.True(result.FloorMatches, $"{context}: simulated floor {result.SimulatedFloorMetres}, formula {result.FloorMetres}");

        // At the floor: boundaries within reach were reached, and none found the pair apart.
        Assert.Equal(floor, result.AtFloor.RadiusMetres);
        Assert.Equal(20, result.AtFloor.Approaches);
        Assert.True(result.AtFloor.Boundaries > 0, $"{context}: no boundary came within reach at the floor");
        Assert.True(result.AtFloor.SeparatedBoundaries == 0, $"{context}: {result.AtFloor.SeparatedBoundaries} boundaries within reach found the pair apart at the floor");

        // Below the floor by more than the section slack: at least one boundary within reach found the pair apart.
        var below = result.BelowFloor;
        Assert.NotNull(below);
        Assert.True(below.RadiusMetres < floor - slack, $"{context}: radius {below.RadiusMetres} is not below r_min − k·√2·s = {floor - slack}");
        Assert.True(below.SeparatedBoundaries > 0, $"{context}: radius {below.RadiusMetres} never found the pair apart within reach");
        Assert.StartsWith($"seed {Seed}, ", below.FirstSeparated, StringComparison.Ordinal);

        Assert.True(result.InvariantViolations == 0, $"{context}: {result.InvariantViolations} invariant violations");
        Assert.True(result.Passed, context);
    }

    // D16: any radius below the floor by more than the section slack (down to 0) leaves a boundary within reach
    // with the pair apart, over random offsets inside sections; the floor itself never does.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 1, EntityClass.Aircraft, 8.0, 240.0)]
    [InlineData(ClaimAnchoring.Position, 2, EntityClass.GroundVehicle, 15.0, 60.0)]
    public void EveryRadiusBelowTheFloorBySlackFailsAndTheFloorHolds(ClaimAnchoring anchoring, int sectionSize, EntityClass claimant, double range, double closingSpeed)
    {
        var floorCase = new ClaimRadiusFloorCase("case", range, closingSpeed, 30, claimant);
        var floor = floorCase.FloorMetres;
        var slack = AnalyticFormulas.SectionSlack(sectionSize, anchoring);
        var closing = closingSpeed / 30;

        foreach (var radius in new[] { floor - slack - closing, (floor - slack - closing) / 2, 0 })
        {
            var below = SimulatorAgreement.FloorApproaches(floorCase, anchoring, sectionSize, radius, approaches: 15, Seed);

            Assert.True(below.Boundaries > 0, $"seed {Seed}, radius {radius}: no boundary within reach");
            Assert.True(below.SeparatedBoundaries > 0, $"seed {Seed}, {claimant} {anchoring}, radius {radius} below r_min − k·√2·s = {floor - slack}: no boundary found the pair apart");
            Assert.True(below.InvariantViolations == 0, $"seed {Seed}, radius {radius}: {below.FirstInvariantViolation}");
        }

        foreach (var radius in new[] { floor, floor + sectionSize })
        {
            var atOrAbove = SimulatorAgreement.FloorApproaches(floorCase, anchoring, sectionSize, radius, approaches: 15, Seed);

            Assert.True(atOrAbove.Boundaries > 0, $"seed {Seed}, radius {radius}: no boundary within reach");
            Assert.True(atOrAbove.SeparatedBoundaries == 0, $"seed {Seed}, {claimant} {anchoring}, radius {radius} at or above the floor {floor}: {atOrAbove.FirstSeparated}");
            Assert.True(atOrAbove.InvariantViolations == 0, $"seed {Seed}, radius {radius}: {atOrAbove.FirstInvariantViolation}");
        }
    }

    // At the default 16 m sections the placeholder floors are within the section slack, so only the floor half of
    // the check applies: the floor holds, and no below-floor radius exists to test.
    [Theory]
    [InlineData(EntityClass.Infantry, 10.0, 14.0)]
    [InlineData(EntityClass.GroundVehicle, 15.0, 60.0)]
    [InlineData(EntityClass.Aircraft, 8.0, 240.0)]
    public void ClaimRadiusFloorHoldsAtTheDefaultSectionSize(EntityClass claimant, double range, double closingSpeed)
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase("default", range, closingSpeed, 30, claimant)], SectionSize: 16, TrialsPerAnchoring: 10);

        var results = SimulatorAgreement.ClaimRadiusFloor(options, Seed);

        Assert.Equal([ClaimAnchoring.Section, ClaimAnchoring.Position], results.Select(r => r.Anchoring));
        foreach (var result in results)
        {
            var context = $"seed {Seed}, {claimant} {result.Anchoring}: {result.FirstFailure}";
            Assert.True(result.AtFloor.Boundaries > 0, context);
            Assert.True(result.AtFloor.SeparatedBoundaries == 0, context);
            Assert.Null(result.BelowFloor);
            Assert.True(result.Passed, context);
        }
    }

    // A radius below the floor that the core still covers at every boundary would mean the floor is not needed:
    // the check flags it with its seed. Here the "below" radius is the floor itself, so no boundary fails.
    [Fact]
    public void BelowFloorRadiusThatNeverFailsIsReported()
    {
        var floorCase = new ClaimRadiusFloorCase("aircraft", 8, 240, 30);

        var atFloor = SimulatorAgreement.FloorApproaches(floorCase, ClaimAnchoring.Position, 1, floorCase.FloorMetres, approaches: 5, Seed);
        var result = new FloorCheckResult(
            "aircraft", EntityClass.Aircraft, ClaimAnchoring.Position, 1, 8, 240, 30, 16, Math.Sqrt(2), 16, atFloor, atFloor, null);

        Assert.True(atFloor.SeparatedBoundaries == 0, $"seed {Seed}: {atFloor.FirstSeparated}");
        Assert.False(result.Passed);
    }

    // Research D1: k = 3 section-anchored and 1 position-anchored. The aircraft floor is 16 m and closes 8 m a tick.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 1, 3)]
    [InlineData(ClaimAnchoring.Position, 1, 1)]
    [InlineData(ClaimAnchoring.Position, 4, 1)]
    public void BelowFloorRadiusIsTheSlackAndOneTickUnderTheFloor(ClaimAnchoring anchoring, int sectionSize, int k)
    {
        var radius = SimulatorAgreement.BelowFloorRadius(new ClaimRadiusFloorCase("aircraft", 8, 240, 30), anchoring, sectionSize);

        Assert.NotNull(radius);
        Assert.Equal(16 - (k * Math.Sqrt(2) * sectionSize) - 8, radius.Value, 1e-12);
    }

    [Fact]
    public void NoBelowFloorRadiusWhenTheFloorIsWithinTheSlackAndOneTick()
    {
        Assert.Null(SimulatorAgreement.BelowFloorRadius(new ClaimRadiusFloorCase("aircraft", 8, 240, 30), ClaimAnchoring.Section, 16));
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
    public void SameSeedGivesTheSameFloorResult()
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase("aircraft", 8, 240, 30)], SectionSize: 1, TrialsPerAnchoring: 5);

        var first = SimulatorAgreement.ClaimRadiusFloor(options, Seed);
        var second = SimulatorAgreement.ClaimRadiusFloor(options, Seed);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FewerThan200LaneTrialsPerStepAreRejected()
    {
        var options = LaneBridgingCheckOptions.Fr031 with { Travellers = [60], TrialsPerStep = 199 };

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.LaneBridging(options, Seed));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void LaneToleranceOutsideZeroToOneIsRejected(double tolerance)
    {
        var options = LaneBridgingCheckOptions.Fr031 with { Travellers = [60], TrialsPerStep = 200, TolerancePoints = tolerance };

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.LaneBridging(options, Seed));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-15.0)]
    [InlineData(double.PositiveInfinity)]
    public void LaneRadiusThatIsNotPositiveIsRejected(double radius)
    {
        var options = LaneBridgingCheckOptions.Fr031 with { Travellers = [60], TrialsPerStep = 200, ClaimRadiusMetres = radius };

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

    // The claimant closes head-on, so a closing speed of 0 has nothing to check.
    [Fact]
    public void FloorWithoutAClosingSpeedIsRejected()
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase("static", 5, 0, 30)], 16, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.ClaimRadiusFloor(options, Seed));
    }

    [Theory]
    [InlineData(EntityClass.CapturePoint)]
    [InlineData(EntityClass.HeavyProjectile)]
    public void FloorClaimantMustBeAClassWithAClaimRadius(EntityClass claimant)
    {
        var options = new FloorCheckOptions([new ClaimRadiusFloorCase("odd", 8, 240, 30, claimant)], 16, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.ClaimRadiusFloor(options, Seed));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void FloorApproachRadiusMustBeNonNegative(double radius)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulatorAgreement.FloorApproaches(new ClaimRadiusFloorCase("a", 8, 240, 30), ClaimAnchoring.Section, 16, radius, 1, Seed));
    }

    [Fact]
    public void ZeroTrialsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.MergeDistance(new MergeDistanceCheckOptions(16, 16, 16, 0), ClaimAnchoring.Section, 0, Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorAgreement.ClaimRadiusFloor(new FloorCheckOptions([new ClaimRadiusFloorCase("a", 8, 240, 30)], 16, 0), Seed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SimulatorAgreement.FloorApproaches(new ClaimRadiusFloorCase("a", 8, 240, 30), ClaimAnchoring.Section, 16, 16, 0, Seed));
    }
}
