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
    /// The claim-radius floor, per case and anchoring mode: a claimant with the floor as its radius and a
    /// target that claims only its own section close at the case's speed, one tick at a time on the time
    /// base. Every tick that ends with them within interaction range must follow a tick end at which they
    /// already shared a region, and the measured closing per tick must give the formula's floor.
    /// </summary>
    public static IReadOnlyList<FloorCheckResult> ClaimRadiusFloor(FloorCheckOptions options, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(options);
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
    /// The lane-bridging estimate: for each traffic step, trials place two endpoint entities a lane's
    /// length apart with uniformly random travellers on the straight lane between them, every entity
    /// claiming d / 2 position-anchored, and count how often the endpoints share a region after the tick
    /// end. Each step must match the estimate within the tolerance.
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

        var s = options.SectionSize;
        var length = options.LaneLengthMetres;
        var radius = options.MergeDistanceMetres / 2;
        var margin = Math.Ceiling((radius + (4 * s)) / s) * s;

        // The lane is a corridor one section tall. Every entity sits on the lane's row, so a claim's
        // sections in any other row would be a subset of its sections in that row: the corridor decides
        // bridging exactly as an open map would, at a fraction of the cost.
        var topology = SectionTopology.OpenWorld(s, length + (2 * margin), s);
        var radii = new ClaimRadii(radius, radius, radius, radius, radius);
        var settings = NoDamping(s, ClaimAnchoring.Position, 0, radii);
        var laneY = s / 2.0;
        var violations = new List<Violation>();
        var log = new FailureLog(seed);
        var steps = new List<LaneBridgingStepResult>();

        foreach (var travellers in options.Travellers)
        {
            // Validates the step's inputs before any trial runs.
            var estimate = AnalyticFormulas.LaneBridgingProbability(travellers, length, options.MergeDistanceMetres);
            var rng = DeterministicRandom.ForStream(seed, $"analysis.lane-bridging.{travellers}");
            var bridged = 0;
            for (var trial = 0; trial < options.TrialsPerStep; trial++)
            {
                var regionizer = new Regionizer(topology, settings);
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

        return new LaneBridgingCheckResult(length, options.MergeDistanceMetres, s, options.TolerancePoints, steps, log.InvariantViolations, log.First);
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
        var range = floorCase.InteractionRangeMetres;
        var floor = AnalyticFormulas.ClaimRadiusFloor(range, floorCase.ClosingSpeedMetresPerSecond, floorCase.TickRateHz);
        var stepsPerTick = BaseStepsPerSecond / floorCase.TickRateHz;
        if (stepsPerTick < 1 || stepsPerTick != Math.Floor(stepsPerTick))
        {
            throw new ArgumentOutOfRangeException(
                nameof(floorCase),
                floorCase.TickRateHz,
                $"Case '{floorCase.Name}': the tick rate must divide the {BaseStepsPerSecond} Hz time base.");
        }

        // The closing per tick on the simulator's time base: dt = steps per tick / 60 s.
        var closing = floorCase.ClosingSpeedMetresPerSecond * (stepsPerTick / BaseStepsPerSecond);
        var dMax = AnalyticFormulas.MergeDistance(floor, 0, s, anchoring, 0);
        var approachTicks = closing > 0 ? (int)Math.Ceiling((dMax + s) / closing) + 1 : 0;
        var half = range + (approachTicks * closing) + floor + (8 * s);
        var topology = SectionTopology.OpenWorld(s, 2 * half, 2 * half);
        var radii = new ClaimRadii(floor, 0, 0, 0, 0);
        var settings = NoDamping(s, anchoring, 0, radii);
        var rng = DeterministicRandom.ForStream(seed, $"analysis.floor.{floorCase.Name}.{anchoring}");
        var centre = Math.Floor(half / s) * s;
        var violations = new List<Violation>();
        var log = new FailureLog(seed);
        var tally = new FloorTally(range, log);

        for (var trial = 0; trial < trials; trial++)
        {
            var regionizer = new Regionizer(topology, settings);
            var ax = centre + (rng.NextDouble() * s);
            var ay = centre + (rng.NextDouble() * s);
            var (dx, dy) = rng.UnitVector();

            // The approach ends at a random distance in (0, range], so its last tick is an interaction.
            var final = range * (1 - rng.NextDouble());
            var start = final + (approachTicks * closing);
            var (bx, by) = (ax + (dx * start), ay + (dy * start));
            regionizer.AddEntity(Claimant, EntityClass.Infantry, 0, ax, ay);
            regionizer.AddEntity(Target, EntityClass.GroundVehicle, 0, bx, by);
            var now = 0L;

            for (var t = 0; t <= approachTicks; t++)
            {
                if (t > 0)
                {
                    var along = final + ((approachTicks - t) * closing);
                    (bx, by) = (ax + (dx * along), ay + (dy * along));
                }

                LockstepTick(regionizer, (long)stepsPerTick, ref now, t > 0 ? Target : -1, bx, by);
                log.Invariants(trial, t, Check(regionizer, violations));
                tally.Observe(trial, t, ax, ay, bx, by, SameRegion(regionizer, Claimant, Target));
            }
        }

        return new FloorCheckResult(
            floorCase.Name, anchoring, range, floorCase.ClosingSpeedMetresPerSecond, floorCase.TickRateHz, floor,
            tally.MaxClosingPerTick, range + tally.MaxClosingPerTick, trials, tally.InteractionTicks,
            tally.InteractionsWhileSeparated, log.InvariantViolations, log.First);
    }

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
