using System.Text.Json.Serialization;

namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>
/// One claim-radius floor input: an instant-interaction range, a closing speed and a tick rate, and the
/// class of the entity that closes head-on (research D16). Its claim radius is the one under test.
/// </summary>
/// <param name="Claimant">The closing entity's class: infantry, ground vehicle, aircraft or traveller.</param>
public sealed record ClaimRadiusFloorCase(
    string Name,
    double InteractionRangeMetres,
    double ClosingSpeedMetresPerSecond,
    double TickRateHz,
    EntityClass Claimant = EntityClass.Aircraft)
{
    /// <summary>r_min = d_interact + v_rel · Δt, with Δt = 1 / tick rate.</summary>
    [JsonIgnore]
    public double FloorMetres => AnalyticFormulas.ClaimRadiusFloor(InteractionRangeMetres, ClosingSpeedMetresPerSecond, TickRateHz);
}

/// <summary>Settings for the claim-radius floor check against the core.</summary>
/// <param name="Cases">The floors to check.</param>
/// <param name="SectionSize">The section size the core runs with, in metres.</param>
/// <param name="TrialsPerAnchoring">
/// Head-on approaches per case, anchoring mode and radius, each at a random offset inside the target's
/// section, a random heading and a random phase.
/// </param>
public sealed record FloorCheckOptions(IReadOnlyList<ClaimRadiusFloorCase> Cases, int SectionSize, int TrialsPerAnchoring);

/// <summary>Settings for the merge-distance check: two entities stepped apart and back, one section a step.</summary>
/// <param name="SectionSize">The section size, in metres.</param>
/// <param name="RadiusA">The first entity's claim radius, in metres.</param>
/// <param name="RadiusB">The second entity's claim radius, in metres.</param>
/// <param name="Trials">Trials per anchoring mode and merge gap, each with its own random offsets and direction.</param>
public sealed record MergeDistanceCheckOptions(int SectionSize, double RadiusA, double RadiusB, int Trials);

/// <summary>
/// Settings for the lane-bridging check (FR-031): two endpoint entities a lane's length apart, uniformly
/// random travellers between them, every entity claiming the same radius, position-anchored with merge gap 0.
/// </summary>
/// <param name="LaneLengthMetres">The distance between the two endpoints.</param>
/// <param name="ClaimRadiusMetres">Every entity's claim radius; the estimate is compared at d = rA + rB, twice this.</param>
/// <param name="SectionSize">A small section size, so the section grid barely widens d (claims merge between d and d + √2·s).</param>
/// <param name="Travellers">The traffic steps.</param>
/// <param name="TrialsPerStep">Trials per traffic step; FR-031 requires at least 200.</param>
/// <param name="TolerancePoints">The allowed difference from the estimate, as a probability (0.10 is 10 percentage points).</param>
public sealed record LaneBridgingCheckOptions(
    double LaneLengthMetres,
    double ClaimRadiusMetres,
    int SectionSize,
    IReadOnlyList<int> Travellers,
    int TrialsPerStep,
    double TolerancePoints)
{
    /// <summary>The fewest trials per step FR-031 allows.</summary>
    public const int MinimumTrialsPerStep = 200;

    /// <summary>
    /// The setup FR-031 and research D16 fix: a 600 m lane, 15 m radii (so d = 30 m), 1 m sections, the
    /// issue's 30, 60 and 120 travellers, 400 trials per step and a 10-point tolerance.
    /// </summary>
    public static LaneBridgingCheckOptions Fr031 { get; } = new(
        LaneLengthMetres: 600,
        ClaimRadiusMetres: 15,
        SectionSize: 1,
        Travellers: [30, 60, 120],
        TrialsPerStep: 400,
        TolerancePoints: 0.10);

    /// <summary>The merge distance the estimate is compared at: d = rA + rB.</summary>
    [JsonIgnore]
    public double MergeDistanceMetres => 2 * ClaimRadiusMetres;
}

/// <summary>Head-on approaches at one claim radius: the tick boundaries within reach, and how many found the pair apart.</summary>
/// <param name="RadiusMetres">The claimant's claim radius.</param>
/// <param name="Approaches">Approaches run.</param>
/// <param name="Boundaries">Tick boundaries at which the pair was within reach, d_interact + v_rel · Δt.</param>
/// <param name="SeparatedBoundaries">Of those, boundaries at which the pair was in different regions.</param>
/// <param name="MaxClosingPerTickMetres">The largest closing over one tick, measured on the 60 Hz time base.</param>
/// <param name="InvariantViolations">Violations the invariant checker found after any tick.</param>
/// <param name="FirstSeparated">The first boundary within reach that found the pair apart, with its seed, or null.</param>
/// <param name="FirstInvariantViolation">The first invariant violation, with its seed, or null.</param>
public sealed record FloorApproachResult(
    double RadiusMetres,
    int Approaches,
    int Boundaries,
    int SeparatedBoundaries,
    double MaxClosingPerTickMetres,
    int InvariantViolations,
    string? FirstSeparated,
    string? FirstInvariantViolation);

/// <summary>The claim-radius floor against the core for one case and anchoring mode (research D16).</summary>
/// <param name="FloorMetres">The formula's floor, r_min, which is also the reach the check uses.</param>
/// <param name="SectionSlackMetres">k·√2·s: how far the section grid can widen a claim in this anchoring mode.</param>
/// <param name="SimulatedFloorMetres">The interaction range plus the closing per tick measured at the floor.</param>
/// <param name="AtFloor">Approaches with the claim radius at the floor: no boundary within reach may find the pair apart.</param>
/// <param name="BelowFloor">
/// Approaches with the radius below the floor by the section slack plus one tick's closing, so every
/// approach has a boundary within reach that the claims cannot cover: at least one must find the pair
/// apart. Null when that radius would be negative (the floor is within the slack at this section size).
/// </param>
public sealed record FloorCheckResult(
    string Name,
    EntityClass Claimant,
    ClaimAnchoring Anchoring,
    int SectionSize,
    double InteractionRangeMetres,
    double ClosingSpeedMetresPerSecond,
    double TickRateHz,
    double FloorMetres,
    double SectionSlackMetres,
    double SimulatedFloorMetres,
    FloorApproachResult AtFloor,
    FloorApproachResult? BelowFloor,
    string? FirstFailure)
{
    /// <summary>The floor matches exactly, up to floating-point rounding (1e-9 relative).</summary>
    public bool FloorMatches => Math.Abs(SimulatedFloorMetres - FloorMetres) <= 1e-9 * Math.Max(1, FloorMetres);

    /// <summary>Violations the invariant checker found in either set of approaches.</summary>
    public int InvariantViolations => AtFloor.InvariantViolations + (BelowFloor?.InvariantViolations ?? 0);

    /// <summary>The floor matches; at the floor, boundaries were reached and none found the pair apart; below it, one did.</summary>
    public bool Passed => FloorMatches
        && AtFloor.Boundaries > 0 && AtFloor.SeparatedBoundaries == 0
        && (BelowFloor is null || BelowFloor.SeparatedBoundaries > 0)
        && InvariantViolations == 0;
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

/// <summary>The lane-bridging estimate against the core, with the setup it ran (FR-031).</summary>
/// <param name="MergeDistanceMetres">d = rA + rB, the distance the estimate is compared at.</param>
/// <param name="MergesUpToMetres">d + √2·s: two claims merge somewhere between d and this distance.</param>
public sealed record LaneBridgingCheckResult(
    double LaneLengthMetres,
    double ClaimRadiusMetres,
    double MergeDistanceMetres,
    double MergesUpToMetres,
    int SectionSize,
    ClaimAnchoring Anchoring,
    int MergeGap,
    double TolerancePoints,
    IReadOnlyList<LaneBridgingStepResult> Steps,
    int InvariantViolations,
    string? FirstFailure)
{
    public bool Passed => Steps.Count > 0 && InvariantViolations == 0 && Steps.All(s => s.WithinTolerance);
}
