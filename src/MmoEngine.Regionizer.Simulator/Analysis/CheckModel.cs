namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>One claim-radius floor input: an instant-interaction range, a closing speed and a tick rate.</summary>
public sealed record ClaimRadiusFloorCase(string Name, double InteractionRangeMetres, double ClosingSpeedMetresPerSecond, double TickRateHz);

/// <summary>Settings for the claim-radius floor check against the core.</summary>
/// <param name="Cases">The floors to check.</param>
/// <param name="SectionSize">The section size the core runs with, in metres.</param>
/// <param name="TrialsPerAnchoring">Approaches stepped per case and anchoring mode, each with random offsets.</param>
public sealed record FloorCheckOptions(IReadOnlyList<ClaimRadiusFloorCase> Cases, int SectionSize, int TrialsPerAnchoring);

/// <summary>Settings for the merge-distance check: two entities stepped apart and back, one section a step.</summary>
/// <param name="SectionSize">The section size, in metres.</param>
/// <param name="RadiusA">The first entity's claim radius, in metres.</param>
/// <param name="RadiusB">The second entity's claim radius, in metres.</param>
/// <param name="Trials">Trials per anchoring mode and merge gap, each with its own random offsets and direction.</param>
public sealed record MergeDistanceCheckOptions(int SectionSize, double RadiusA, double RadiusB, int Trials);

/// <summary>Settings for the lane-bridging check: uniformly random travellers between two endpoint entities.</summary>
/// <param name="LaneLengthMetres">The distance between the two endpoints.</param>
/// <param name="MergeDistanceMetres">d = rA + rB; every entity claims d / 2, position-anchored.</param>
/// <param name="SectionSize">A small section size, so the section grid barely widens d.</param>
/// <param name="Travellers">The traffic steps.</param>
/// <param name="TrialsPerStep">Trials per traffic step; FR-031 requires at least 200.</param>
/// <param name="TolerancePoints">The allowed difference from the estimate, as a probability (0.10 is 10 percentage points).</param>
public sealed record LaneBridgingCheckOptions(
    double LaneLengthMetres,
    double MergeDistanceMetres,
    int SectionSize,
    IReadOnlyList<int> Travellers,
    int TrialsPerStep,
    double TolerancePoints)
{
    /// <summary>The fewest trials per step FR-031 allows.</summary>
    public const int MinimumTrialsPerStep = 200;
}

/// <summary>The claim-radius floor against the core for one case and anchoring mode.</summary>
/// <param name="FloorMetres">The formula's floor.</param>
/// <param name="SimulatedClosingPerTickMetres">The largest per-tick closing measured on the simulator's 60 Hz time base.</param>
/// <param name="SimulatedFloorMetres">The interaction range plus the measured closing.</param>
/// <param name="InteractionTicks">Ticks that ended with the pair within interaction range.</param>
/// <param name="InteractionsWhileSeparated">Of those, ticks where the pair was in different regions at the previous tick end.</param>
public sealed record FloorCheckResult(
    string Name,
    ClaimAnchoring Anchoring,
    double InteractionRangeMetres,
    double ClosingSpeedMetresPerSecond,
    double TickRateHz,
    double FloorMetres,
    double SimulatedClosingPerTickMetres,
    double SimulatedFloorMetres,
    int Trials,
    int InteractionTicks,
    int InteractionsWhileSeparated,
    int InvariantViolations,
    string? FirstFailure)
{
    /// <summary>The floor matches exactly, up to floating-point rounding (1e-9 relative).</summary>
    public bool FloorMatches => Math.Abs(SimulatedFloorMetres - FloorMetres) <= 1e-9 * Math.Max(1, FloorMetres);

    public bool Passed => FloorMatches && InteractionTicks > 0 && InteractionsWhileSeparated == 0 && InvariantViolations == 0;
}

/// <summary>The merge-distance bounds against the core for one anchoring mode and merge gap.</summary>
/// <param name="LowerBoundMetres">rA + rB: every pair this close must share a region.</param>
/// <param name="MergeDistanceMetres">d_max: no pair farther apart may share a region.</param>
/// <param name="ClosestSeparatedMetres">The smallest distance seen with the pair in different regions, or null.</param>
/// <param name="FarthestSharedMetres">The largest distance seen with the pair in one region, or null.</param>
public sealed record MergeDistanceCheckResult(
    ClaimAnchoring Anchoring,
    int MergeGap,
    int SectionSize,
    double RadiusA,
    double RadiusB,
    double LowerBoundMetres,
    double MergeDistanceMetres,
    int Trials,
    int Observations,
    int WithinLowerBound,
    int BeyondMergeDistance,
    int LowerBoundViolations,
    int UpperBoundViolations,
    int InvariantViolations,
    double? ClosestSeparatedMetres,
    double? FarthestSharedMetres,
    string? FirstFailure)
{
    /// <summary>Zero violations, with both bounds exercised.</summary>
    public bool Passed => WithinLowerBound > 0 && BeyondMergeDistance > 0
        && LowerBoundViolations == 0 && UpperBoundViolations == 0 && InvariantViolations == 0;
}

/// <summary>One traffic step of the lane-bridging check.</summary>
/// <param name="Simulated">The share of trials in which the two endpoints shared a region.</param>
/// <param name="Estimate">The analytic estimate for the same step.</param>
public sealed record LaneBridgingStepResult(
    int Travellers,
    int Trials,
    int Bridged,
    double Simulated,
    double Estimate,
    double Difference,
    bool WithinTolerance);

/// <summary>The lane-bridging estimate against the core.</summary>
public sealed record LaneBridgingCheckResult(
    double LaneLengthMetres,
    double MergeDistanceMetres,
    int SectionSize,
    double TolerancePoints,
    IReadOnlyList<LaneBridgingStepResult> Steps,
    int InvariantViolations,
    string? FirstFailure)
{
    public bool Passed => Steps.Count > 0 && InvariantViolations == 0 && Steps.All(s => s.WithinTolerance);
}
