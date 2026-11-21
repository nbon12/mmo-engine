using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// US5's flights end to end through <see cref="SimWorld"/> (FR-009, FR-010): a projectile flies the
/// closed-form ballistic path to the bit and ends where the double-precision reference
/// (<see cref="ProjectileReference"/>) says it first meets the terrain. Every expected hit is first
/// predicted by the reference, so no test can pass by comparing two missing hits. <c>System.Math</c> is
/// written in full, because <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
public sealed class ProjectileScenarioTests
{
    private const uint ShooterId = 1;

    private const uint ProjectileId = 1;

    /// <summary>
    /// The arranged state's step, which is also the in-flight projectile's launch step, so the first step the
    /// world runs sweeps k = 1 (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>).
    /// </summary>
    private const uint ArrangedStep = 1;

    /// <summary>The first segment the world sweeps for the arranged projectile: step + 1 − launchStep.</summary>
    private const uint FirstSweptK = 1;

    /// <summary>Yaw 16384 faces +X, the direction of <see cref="HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt"/>'s shot.</summary>
    private const ushort FacingPlusX = 16384;

    /// <summary>How many steps after the projectile leaves the state are run to check it stays gone.</summary>
    private const int StepsAfterLeaving = 2;

    /// <summary>The bound on a stored position's distance from the double closed form, and on a ground hit's height.</summary>
    private const double PathTolerance = 1e-3;

    /// <summary>The shooter's feet: at the origin, so its standing eye is the launch point (0, 1.62, 0).</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, 0f);

    /// <summary>The launch point: the eye height of a standing character above the origin.</summary>
    private static readonly Vec3 LaunchPoint = new(0f, 1.62f, 0f);

    /// <summary>Rows <c>(classIndex, hz)</c>: class 0 (small arms) and class 1 (heavy), each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> Classes => TestRates.WithRateLast(0, 1);

    /// <summary>
    /// US5 scenario 1: Given <see cref="ProjectileWorlds.Flat"/>, its owner (ID 1) standing at the origin as
    /// the only character, so no target is in range, and one in-flight projectile of the row's class launched
    /// at step 1, level from (0, 1.62, 0) along +X at the class's default muzzle speed (600 / 120 m/s). The
    /// double reference first meets the terrain, at a ground point inside the heightfield (|x|, |z| below
    /// <see cref="ProjectileWorlds.GroundHalfExtent"/>) and within the class's <c>maxRangeM</c> of Origin
    /// (about 345 m for class 0 and 69 m for class 1), so the flight cannot end in a Range expiry without a
    /// terrain hit. When it is stepped with <c>SimWorld.Step</c> until it leaves the state, Then at every step
    /// n before the hit the committed state holds it alone with Position exactly the bits of
    /// <c>Ballistics.PositionAt(k + 1)</c> for k = n − 1, within 1e-3 m of the double closed form at k + 1;
    /// the step whose hit list holds it is exactly the first step whose segment p(k) → p(k + 1), evaluated in
    /// double, meets the heightfield (<see cref="ProjectileReference.SegmentHeightfield"/>), with Kind Terrain,
    /// Hitbox None and a point within 1e-3 m of height 0; no earlier step reports a hit; no step reports an
    /// expiry; and the projectile is in no committed state from the hit step on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt(int classIndex, int hz)
    {
        var context = FormattableString.Invariant($"class {classIndex}, {hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var flat = ProjectileWorlds.Flat();
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, FacingPlusX, Stance.Standing);
        var eye = ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);
        Assert.True(
            BitConverter.SingleToUInt32Bits(eye.Y) == BitConverter.SingleToUInt32Bits(LaunchPoint.Y),
            FormattableString.Invariant($"{context}: the shooter's eye {eye} must be the launch point {LaunchPoint}"));
        var speed = ProjectileWorlds.MuzzleSpeed(config, classIndex);
        var record = ProjectileWorlds.InFlight(
            ProjectileId,
            ShooterId,
            classIndex,
            LaunchPoint,
            ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(90.0, 0.0), speed),
            ArrangedStep,
            ArrangedStep,
            config);
        CharacterRecord[] characters = [shooter];

        var predicted = ProjectileReference.PredictFirstHit(flat, config, record, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Terrain },
            FormattableString.Invariant($"{context}: the double path must first meet the terrain, not {predicted}"));
        var range = (double)config.Config.ProjectileClasses![classIndex]!.MaxRangeM;
        var fromOrigin = DVec3.Distance(predicted.Point, LaunchPoint);
        Assert.True(
            System.Math.Abs(predicted.Point.X) < ProjectileWorlds.GroundHalfExtent
                && System.Math.Abs(predicted.Point.Z) < ProjectileWorlds.GroundHalfExtent
                && fromOrigin <= range,
            FormattableString.Invariant(
                $"{context}: the predicted ground point {predicted.Point} must lie inside the heightfield (|x|, |z| < {ProjectileWorlds.GroundHalfExtent:R}) and within maxRangeM {range:R} of Origin, not {fromOrigin:R} m away"));

        var crossingK = FirstHeightfieldSegment(flat, config, record, FirstSweptK, context);
        var hitStep = ArrangedStep + crossingK;
        Assert.True(
            predicted.Step == hitStep,
            FormattableString.Invariant($"{context}: the reference's first hit is at step {predicted.Step}, but the first segment that meets the heightfield is {crossingK} (step {hitStep})"));

        var halfG = config.HalfG[classIndex];
        var launch = ProjectileWorlds.ToProjectile(record);
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(ArrangedStep, characters, [record]));
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[classIndex] + StepsAfterLeaving + 1, context);

        Assert.True(
            outcomes[^1].Step >= hitStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the terrain hit at step {hitStep}"));
        var found = false;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (terrain hit expected at step {hitStep})");
            Assert.True(
                outcome.Expiries.Count == 0,
                $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}]; the shot lands within its range");
            if (outcome.Step < hitStep)
            {
                Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] before the terrain hit");
                Assert.True(
                    outcome.Projectiles.Count == 1 && outcome.Projectiles[0].Id == ProjectileId,
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.Projectiles.Select(p => p.Id))}]; expected [{ProjectileId}] until the hit");
                var k = outcome.Step - launch.LaunchStep;
                var stored = outcome.Projectiles[0].Position;
                var contract = Ballistics.PositionAt(in launch, k + 1u, halfG, hz);
                Assert.True(
                    Bits(stored) == Bits(contract),
                    FormattableString.Invariant($"{at}: the stored Position {stored} (bits {Bits(stored)}) is not PositionAt({k + 1u}) {contract} (bits {Bits(contract)})"));
                var closedForm = ProjectileReference.ClosedForm(record, config, k + 1L);
                var offPath = DVec3.Distance(stored, closedForm);
                Assert.True(
                    offPath <= PathTolerance,
                    FormattableString.Invariant($"{at}: the stored Position {stored} is {offPath:R} m from the double closed form {closedForm} at k + 1 = {k + 1u}"));
            }
            else
            {
                Assert.True(
                    outcome.Projectiles.Count == 0,
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.Projectiles.Select(p => p.Id))}] from the hit step on");
                if (outcome.Step == hitStep)
                {
                    found = true;
                    Assert.True(
                        outcome.Hits.Count == 1,
                        $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}]; expected exactly one, the terrain hit");
                    var hit = outcome.Hits[0];
                    Assert.True(
                        hit.ProjectileId == ProjectileId && hit.Kind == HitKind.Terrain && hit.Hitbox == HitboxPart.None,
                        $"{at}: the hit is {hit}; expected projectile {ProjectileId} on Terrain with Hitbox None");
                    Assert.True(
                        System.Math.Abs(hit.Point.Y) <= PathTolerance,
                        FormattableString.Invariant($"{at}: the terrain hit point {hit.Point} is not within {PathTolerance:R} m of the ground, height 0"));
                }
                else
                {
                    Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] after the projectile left");
                }
            }
        }

        Assert.True(found, FormattableString.Invariant($"{context}: the run has no outcome for step {hitStep}"));
    }

    /// <summary>
    /// The first segment k ≥ <paramref name="firstK"/>, below the class's <c>LifetimeSteps</c>, whose ends
    /// p(k) and p(k + 1), evaluated in double with <see cref="ProjectileReference.ClosedForm(ReferenceProjectile, CompiledConfig, long)"/>,
    /// meet the heightfield of <paramref name="world"/> (<see cref="ProjectileReference.SegmentHeightfield"/>).
    /// A flight that never meets it fails the test.
    /// </summary>
    private static uint FirstHeightfieldSegment(CollisionData world, CompiledConfig config, ReferenceProjectile projectile, uint firstK, string context)
    {
        var lifetime = (uint)config.LifetimeSteps[projectile.Class];
        for (var k = firstK; k < lifetime; k++)
        {
            var a = ProjectileReference.ClosedForm(projectile, config, k);
            var b = ProjectileReference.ClosedForm(projectile, config, k + 1L);
            if (ProjectileReference.SegmentHeightfield(world, a, b) is not null)
            {
                return k;
            }
        }

        Assert.Fail(FormattableString.Invariant($"{context}: no segment from {firstK} to {lifetime - 1u} meets the heightfield in double"));
        return 0;
    }

    /// <summary>
    /// Steps <paramref name="world"/> with no commands (every character gets the neutral input) until the
    /// committed state holds no projectile, then <see cref="StepsAfterLeaving"/> steps more, copying each step's
    /// hits, expiries and committed projectiles before the next step reuses the world's buffers. A fault, or a
    /// projectile still present after <paramref name="maxSteps"/> steps, fails the test.
    /// </summary>
    private static List<StepOutcome> RunUntilTheStateHoldsNoProjectile(SimWorld world, int maxSteps, string context)
    {
        var outcomes = new List<StepOutcome>();
        var leftAt = -1;
        for (var i = 0; i < maxSteps; i++)
        {
            var step = world.CurrentStep + 1u;
            var report = world.Step(ReadOnlySpan<CharacterCommand>.Empty);
            Assert.True(
                report.Fault.Code == StepFaultCode.None,
                FormattableString.Invariant($"{context}: step {step} faulted with {report.Fault.Code} at '{report.Fault.Path}'"));
            var committed = StateSerializer.ReadCommitted(report.State);
            var events = report.Events;
            outcomes.Add(new StepOutcome(report.Step, events.Hits.ToArray(), events.Expiries.ToArray(), committed.Projectiles.ToArray()));
            if (leftAt < 0 && committed.Projectiles.Count == 0)
            {
                leftAt = i;
            }

            if (leftAt >= 0 && i - leftAt >= StepsAfterLeaving)
            {
                return outcomes;
            }
        }

        Assert.Fail(FormattableString.Invariant($"{context}: the projectile is still in the state after {maxSteps} steps"));
        return outcomes;
    }

    private static string Bits(Vec3 vector) => FormattableString.Invariant(
        $"{BitConverter.SingleToUInt32Bits(vector.X):x8} {BitConverter.SingleToUInt32Bits(vector.Y):x8} {BitConverter.SingleToUInt32Bits(vector.Z):x8}");

    /// <summary>One committed step: its number, its hit and expiry events, and the committed projectiles.</summary>
    private sealed record StepOutcome(uint Step, IReadOnlyList<ProjectileHit> Hits, IReadOnlyList<ExpiryEvent> Expiries, IReadOnlyList<Projectile> Projectiles);
}
