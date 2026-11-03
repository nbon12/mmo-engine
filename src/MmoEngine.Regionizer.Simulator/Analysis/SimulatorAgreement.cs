using System.Globalization;
using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>
/// Checks the analytic results against the regionizer core itself (FR-031, SC-006). Each check drives
/// the core in lockstep on the 60 Hz time base (research D7), moves an entity only during its region's
/// tick, runs the invariant checker after every tick and draws its random offsets from
/// <see cref="DeterministicRandom"/>, so a seed reproduces it exactly. Split damping is off (any island
/// splits at once), so a region always matches the islands of its claims.
/// </summary>
public static class SimulatorAgreement
{
    private const int BaseStepsPerSecond = 60;
    private const int LockstepStepsPerTick = 2;
    private const int Claimant = 0;
    private const int Target = 1;

    /// <summary>
    /// The claim-radius floor, per case and anchoring mode (research D16): a claimant closes head-on on an
    /// unoccupied capture point, which claims only its own section, at the case's speed, one tick at a time
    /// on the time base. With the claim radius at the floor, every tick boundary within reach
    /// (d_interact + v_rel · Δt) must find them in one region. With the radius below the floor by more than
    /// the section slack k·√2·s, at least one such boundary must find them apart, which shows the floor is
    /// needed. The closing measured per tick must give the formula's floor.
    /// </summary>
    public static IReadOnlyList<FloorCheckResult> ClaimRadiusFloor(FloorCheckOptions options, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Cases);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TrialsPerAnchoring, 1);
        var results = new List<FloorCheckResult>();
        foreach (var floorCase in options.Cases)
        {
            foreach (var anchoring in new[] { ClaimAnchoring.Section, ClaimAnchoring.Position })
            {
                results.Add(FloorCase(floorCase, anchoring, options.SectionSize, options.TrialsPerAnchoring, seed));
            }
        }

        return results;
    }

    /// <summary>
    /// The radius the floor check runs below the floor: r_min − k·√2·s − one tick's closing. The claims
    /// cannot meet beyond r + k·√2·s, and the boundaries of an approach are one tick's closing apart, so
    /// every approach has a boundary within reach where the pair must be in two regions. Null when the
    /// radius would be negative: the floor is then within the section slack plus one tick at this size.
    /// </summary>
    public static double? BelowFloorRadius(ClaimRadiusFloorCase floorCase, ClaimAnchoring anchoring, int sectionSize)
    {
        ArgumentNullException.ThrowIfNull(floorCase);
        var radius = floorCase.FloorMetres - AnalyticFormulas.SectionSlack(sectionSize, anchoring) - ClosingPerTick(floorCase);
        return radius >= 0 ? radius : null;
    }

    /// <summary>
    /// Head-on approaches at a given claim radius (research D16). Each approach places the target at a random
    /// offset inside its section, sends the claimant in on a random heading at the case's closing speed, and
    /// ends at a random distance under one tick's closing, so the boundaries fall at random offsets. Every
    /// tick boundary within reach (the floor) is checked for a shared region.
    /// </summary>
    public static FloorApproachResult FloorApproaches(
        ClaimRadiusFloorCase floorCase,
        ClaimAnchoring anchoring,
        int sectionSize,
        double radius,
        int approaches,
        ulong seed)
    {
        ArgumentNullException.ThrowIfNull(floorCase);
        ArgumentOutOfRangeException.ThrowIfLessThan(approaches, 1);
        if (!double.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The claim radius must be finite and non-negative.");
        }

        var stepsPerTick = StepsPerTick(floorCase);
        var closing = ClosingPerTick(floorCase);
        var reach = floorCase.FloorMetres;
        var s = sectionSize;
        var slack = AnalyticFormulas.SectionSlack(s, anchoring);

        // The first boundary is beyond both the reach and d_max at this radius: the pair starts in two regions.
        var ticks = (int)Math.Ceiling((Math.Max(reach, radius + slack) + closing) / closing) + 1;
        var half = ((ticks + 1) * closing) + radius + (8 * s);
        var topology = SectionTopology.OpenWorld(s, 2 * half, 2 * half);
        var settings = NoDamping(s, anchoring, 0, RadiiFor(floorCase.Claimant, radius));
        var stream = string.Create(CultureInfo.InvariantCulture, $"analysis.floor.{floorCase.Name}.{anchoring}.s{s}.r{radius:R}");
        var rng = DeterministicRandom.ForStream(seed, stream);
        var centre = Math.Floor(half / s) * s;
        var violations = new List<Violation>();
        var separatedLog = new FailureLog(seed);
        var invariantLog = new FailureLog(seed);
        var tally = new FloorTally(reach, separatedLog);

        for (var trial = 0; trial < approaches; trial++)
        {
            var regionizer = new Regionizer(topology, settings);

            // The target sits at a random offset inside its section; the claimant comes in on a random heading.
            var bx = centre + (rng.NextDouble() * s);
            var by = centre + (rng.NextDouble() * s);
            var (dx, dy) = rng.UnitVector();
            var final = closing * rng.NextDouble();
            var start = final + (ticks * closing);
            var (ax, ay) = (bx + (dx * start), by + (dy * start));
            regionizer.AddEntity(Claimant, floorCase.Claimant, 0, ax, ay);
            regionizer.AddEntity(Target, EntityClass.CapturePoint, 0, bx, by);
            tally.StartApproach();
            var now = 0L;

            for (var t = 0; t <= ticks; t++)
            {
                if (t > 0)
                {
                    var along = final + ((ticks - t) * closing);
                    (ax, ay) = (bx + (dx * along), by + (dy * along));
                }

                LockstepTick(regionizer, stepsPerTick, ref now, t > 0 ? Claimant : -1, ax, ay);
                invariantLog.Invariants(trial, t, Check(regionizer, violations));
                tally.Observe(trial, t, ax, ay, bx, by, SameRegion(regionizer, Claimant, Target));
            }
        }

        return new FloorApproachResult(
            radius, approaches, tally.Boundaries, tally.SeparatedBoundaries, tally.MaxClosingPerTick,
            invariantLog.InvariantViolations, separatedLog.First, invariantLog.First);
    }

    /// <summary>
    /// The merge-distance bounds for one anchoring mode and merge gap: two entities are stepped apart one
    /// section at a time and back again, at random offsets inside sections. After each tick end, every pair
    /// within rA + rB must share a region and no pair farther apart than d_max may.
    /// </summary>
    public static MergeDistanceCheckResult MergeDistance(MergeDistanceCheckOptions options, ClaimAnchoring anchoring, int mergeGap, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Trials, 1);
        var s = options.SectionSize;
        var lower = options.RadiusA + options.RadiusB;
        var dMax = AnalyticFormulas.MergeDistance(options.RadiusA, options.RadiusB, s, anchoring, mergeGap);

        // Two sections past d_max, so the far end of every trial is beyond the bound.
        var steps = (int)Math.Ceiling(dMax / s) + 2;
        var half = ((steps + 6) * s) + Math.Max(options.RadiusA, options.RadiusB);
        var topology = SectionTopology.OpenWorld(s, 2 * half, 2 * half);
        var radii = new ClaimRadii(options.RadiusA, options.RadiusB, 0, 0, 0);
        var settings = NoDamping(s, anchoring, mergeGap, radii);
        var rng = DeterministicRandom.ForStream(seed, $"analysis.merge-distance.{anchoring}.gap{mergeGap}");
        var centre = Math.Floor(half / s) * s;
        var violations = new List<Violation>();
        var log = new FailureLog(seed);
        var tally = new MergeDistanceTally(lower, dMax, log);

        for (var trial = 0; trial < options.Trials; trial++)
        {
            var regionizer = new Regionizer(topology, settings);
            var ax = centre + (rng.NextDouble() * s);
            var ay = centre + (rng.NextDouble() * s);
            var (dx, dy) = rng.UnitVector();
            var (bx, by) = PointInSectionAlong(rng, ax, ay, dx, dy, 0, s);
            regionizer.AddEntity(Claimant, EntityClass.Infantry, 0, ax, ay);
            regionizer.AddEntity(Target, EntityClass.GroundVehicle, 0, bx, by);
            var now = 0L;

            for (var i = 0; i <= 2 * steps; i++)
            {
                var k = i <= steps ? i : (2 * steps) - i;
                if (i > 0)
                {
                    (bx, by) = PointInSectionAlong(rng, ax, ay, dx, dy, k, s);
                }

                LockstepTick(regionizer, LockstepStepsPerTick, ref now, i > 0 ? Target : -1, bx, by);
                log.Invariants(trial, i, Check(regionizer, violations));
                tally.Observe(trial, i, ax, ay, bx, by, SameRegion(regionizer, Claimant, Target));
            }
        }

        return new MergeDistanceCheckResult(
            anchoring, mergeGap, s, options.RadiusA, options.RadiusB, lower, dMax, options.Trials, tally.Observations,
            tally.WithinLowerBound, tally.BeyondMergeDistance, tally.LowerBoundViolations, tally.UpperBoundViolations,
            log.InvariantViolations, tally.ClosestSeparated, tally.FarthestShared, log.First);
    }

    /// <summary>
    /// The lane-bridging estimate in the setup FR-031 fixes: position anchoring and merge gap 0, so two claims
    /// merge between d = rA + rB and d + √2·s. For each traffic step, trials place two endpoint entities a
    /// lane's length apart with uniformly random travellers on the straight lane between them, every entity
    /// claiming the same radius, and count how often the endpoints share a region after the tick end. Each
    /// step must match the estimate at d within the tolerance.
    /// </summary>
    public static LaneBridgingCheckResult LaneBridging(LaneBridgingCheckOptions options, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Travellers);
        if (options.TrialsPerStep < LaneBridgingCheckOptions.MinimumTrialsPerStep)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TrialsPerStep,
                $"TrialsPerStep must be at least {LaneBridgingCheckOptions.MinimumTrialsPerStep} (FR-031).");
        }

        if (!double.IsFinite(options.TolerancePoints) || options.TolerancePoints <= 0 || options.TolerancePoints > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.TolerancePoints, "TolerancePoints must be in (0, 1].");
        }

        if (!double.IsFinite(options.ClaimRadiusMetres) || options.ClaimRadiusMetres <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ClaimRadiusMetres, "ClaimRadiusMetres must be finite and positive.");
        }

        const ClaimAnchoring Anchoring = ClaimAnchoring.Position;
        const int MergeGap = 0;
        var s = options.SectionSize;
        var length = options.LaneLengthMetres;
        var radius = options.ClaimRadiusMetres;
        var d = options.MergeDistanceMetres;
        var margin = Math.Ceiling((radius + (4 * s)) / s) * s;

        // The lane is a corridor one section tall. Every entity sits on the lane's row, so a claim's
        // sections in any other row would be a subset of its sections in that row: the corridor decides
        // bridging exactly as an open map would, at a fraction of the cost.
        var topology = SectionTopology.OpenWorld(s, length + (2 * margin), s);
        var radii = new ClaimRadii(radius, radius, radius, radius, radius);
        var settings = NoDamping(s, Anchoring, MergeGap, radii);
        var laneY = s / 2.0;
        var violations = new List<Violation>();
        var log = new FailureLog(seed);
        var steps = new List<LaneBridgingStepResult>();

        foreach (var travellers in options.Travellers)
        {
            // Validates the step's inputs before any trial runs.
            var estimate = AnalyticFormulas.LaneBridgingProbability(travellers, length, d);
            var rng = DeterministicRandom.ForStream(seed, $"analysis.lane-bridging.{travellers}");
            var bridged = 0;
            for (var trial = 0; trial < options.TrialsPerStep; trial++)
            {
                var regionizer = new Regionizer(topology, settings);

                // One entity with the same radius at each end of the lane, each in its own fight.
                regionizer.AddEntity(Claimant, EntityClass.Infantry, 0, margin, laneY, fightTag: 0);
                regionizer.AddEntity(Target, EntityClass.Infantry, 0, margin + length, laneY, fightTag: 1);
                for (var i = 0; i < travellers; i++)
                {
                    regionizer.AddEntity(2 + i, EntityClass.Traveller, 0, margin + (rng.NextDouble() * length), laneY);
                }

                var now = 0L;
                LockstepTick(regionizer, LockstepStepsPerTick, ref now, -1, 0, 0);
                log.Invariants(trial, travellers, Check(regionizer, violations));
                if (SameRegion(regionizer, Claimant, Target))
                {
                    bridged++;
                }
            }

            var simulated = bridged / (double)options.TrialsPerStep;
            var difference = simulated - estimate;
            var withinTolerance = Math.Abs(difference) <= options.TolerancePoints;
            if (!withinTolerance)
            {
                log.Record(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{travellers} travellers: bridged in {simulated:0.####} of trials against an estimate of {estimate:0.####}, outside {options.TolerancePoints:0.##}"));
            }

            steps.Add(new LaneBridgingStepResult(travellers, options.TrialsPerStep, bridged, simulated, estimate, difference, withinTolerance));
        }

        var mergesUpTo = d + AnalyticFormulas.SectionSlack(s, Anchoring);
        return new LaneBridgingCheckResult(
            length, radius, d, mergesUpTo, s, Anchoring, MergeGap, options.TolerancePoints, steps, log.InvariantViolations, log.First);
    }

    internal static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = bx - ax;
        var dy = by - ay;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static FloorCheckResult FloorCase(ClaimRadiusFloorCase floorCase, ClaimAnchoring anchoring, int s, int trials, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(floorCase);
        var floor = floorCase.FloorMetres;
        var slack = AnalyticFormulas.SectionSlack(s, anchoring);
        var atFloor = FloorApproaches(floorCase, anchoring, s, floor, trials, seed);
        var belowRadius = BelowFloorRadius(floorCase, anchoring, s);
        var below = belowRadius is { } r ? FloorApproaches(floorCase, anchoring, s, r, trials, seed) : null;
        var simulatedFloor = floorCase.InteractionRangeMetres + atFloor.MaxClosingPerTickMetres;

        var result = new FloorCheckResult(
            floorCase.Name, floorCase.Claimant, anchoring, s, floorCase.InteractionRangeMetres,
            floorCase.ClosingSpeedMetresPerSecond, floorCase.TickRateHz, floor, slack, simulatedFloor, atFloor, below, null);
        return result with { FirstFailure = FirstFloorFailure(result, seed) };
    }

    /// <summary>The first reason a floor result fails, with its seed, or null when it passes.</summary>
    internal static string? FirstFloorFailure(FloorCheckResult result, ulong seed)
    {
        string Seeded(string message) => string.Create(CultureInfo.InvariantCulture, $"seed {seed}, {message}");
        var atFloor = result.AtFloor;
        var below = result.BelowFloor;
        if (!result.FloorMatches)
        {
            return Seeded($"the closing measured per tick gives a floor of {result.SimulatedFloorMetres:R} m against the formula's {result.FloorMetres:R} m");
        }

        if (atFloor.FirstInvariantViolation is not null)
        {
            return atFloor.FirstInvariantViolation;
        }

        if (below?.FirstInvariantViolation is not null)
        {
            return below.FirstInvariantViolation;
        }

        if (atFloor.Boundaries == 0)
        {
            return Seeded($"no tick boundary came within reach ({result.FloorMetres:R} m) in {atFloor.Approaches} approaches");
        }

        if (atFloor.SeparatedBoundaries > 0)
        {
            return $"at the floor, radius {atFloor.RadiusMetres.ToString("R", CultureInfo.InvariantCulture)} m: {atFloor.FirstSeparated}";
        }

        return below is not null && below.SeparatedBoundaries == 0
            ? Seeded($"a radius of {below.RadiusMetres:R} m, below the floor by more than the section slack ({result.SectionSlackMetres:0.###} m), found the pair in one region at all {below.Boundaries} boundaries within reach in {below.Approaches} approaches")
            : null;
    }

    private static int StepsPerTick(ClaimRadiusFloorCase floorCase)
    {
        var stepsPerTick = BaseStepsPerSecond / floorCase.TickRateHz;
        if (!double.IsFinite(stepsPerTick) || stepsPerTick < 1 || stepsPerTick != Math.Floor(stepsPerTick))
        {
            throw new ArgumentOutOfRangeException(
                nameof(floorCase),
                floorCase.TickRateHz,
                $"Case '{floorCase.Name}': the tick rate must divide the {BaseStepsPerSecond} Hz time base.");
        }

        return (int)stepsPerTick;
    }

    /// <summary>The closing per tick on the simulator's time base: v_rel · (steps per tick / 60 s).</summary>
    private static double ClosingPerTick(ClaimRadiusFloorCase floorCase)
    {
        if (!double.IsFinite(floorCase.ClosingSpeedMetresPerSecond) || floorCase.ClosingSpeedMetresPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floorCase),
                floorCase.ClosingSpeedMetresPerSecond,
                $"Case '{floorCase.Name}': the floor check needs a positive closing speed; the claimant closes head-on.");
        }

        return floorCase.ClosingSpeedMetresPerSecond * StepsPerTick(floorCase) / BaseStepsPerSecond;
    }

    /// <summary>Only the claimant's class claims; the capture point is unoccupied, so it claims only its own section.</summary>
    private static ClaimRadii RadiiFor(EntityClass claimant, double radius) => claimant switch
    {
        EntityClass.Infantry => new ClaimRadii(radius, 0, 0, 0, 0),
        EntityClass.GroundVehicle => new ClaimRadii(0, radius, 0, 0, 0),
        EntityClass.Aircraft => new ClaimRadii(0, 0, radius, 0, 0),
        EntityClass.Traveller => new ClaimRadii(0, 0, 0, radius, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(claimant), claimant, "The claimant must be infantry, a ground vehicle, an aircraft or a traveller."),
    };

    /// <summary>Split damping off: any region whose claims form two islands splits at its tick end.</summary>
    private static RegionizerOptions NoDamping(int sectionSize, ClaimAnchoring anchoring, int mergeGap, ClaimRadii radii) =>
        new(sectionSize, anchoring, mergeGap, radii, SplitMinSections: 1, SplitMinDeadPercent: 0, SplitMinAgeSteps: 0, TimerMode.TickCounter);

    /// <summary>
    /// One lockstep tick (research D7): every region starts in ID order, the mover (if any) moves while its
    /// region ticks, then every region ends in ID order.
    /// </summary>
    private static void LockstepTick(Regionizer regionizer, long stepsPerTick, ref long now, int moverId, double x, double y)
    {
        now += stepsPerTick;
        regionizer.AdvanceTo(now);
        var ticking = new List<long>();
        foreach (var id in regionizer.RegionIds.ToArray())
        {
            if (regionizer.TryMarkTicking(id))
            {
                ticking.Add(id);
            }
        }

        if (moverId >= 0)
        {
            regionizer.MoveEntity(moverId, 0, x, y);
        }

        foreach (var id in ticking)
        {
            regionizer.MarkNotTicking(id);
        }
    }

    /// <summary>The invariant checker at the tick boundary; returns the violations it found.</summary>
    private static List<Violation> Check(Regionizer regionizer, List<Violation> violations)
    {
        violations.Clear();
        InvariantChecker.Check(regionizer, violations);
        return violations;
    }

    /// <summary>A random point inside the section that lies <paramref name="k"/> section lengths from (ax, ay) along (dx, dy).</summary>
    private static (double X, double Y) PointInSectionAlong(DeterministicRandom rng, double ax, double ay, double dx, double dy, int k, int s)
    {
        var sx = Math.Floor((ax + (dx * k * s)) / s);
        var sy = Math.Floor((ay + (dy * k * s)) / s);
        return ((sx + rng.NextDouble()) * s, (sy + rng.NextDouble()) * s);
    }

    private static bool SameRegion(Regionizer regionizer, int a, int b) =>
        regionizer.GetEntity(a).RegionId == regionizer.GetEntity(b).RegionId;
}
