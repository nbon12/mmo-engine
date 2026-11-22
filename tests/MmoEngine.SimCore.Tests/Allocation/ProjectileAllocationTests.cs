using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: <c>ProjectileStep.Step</c>, which stage 5 of every world step runs per projectile,
/// allocates nothing (data-model.md#projectile-step: the hitboxes and bounding spheres are the caller's spans,
/// and every loop is bounded by the targets' length). Every row warms its call up twice, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across ten windows of 1,000 calls over hitbox, bounds
/// and ID buffers allocated before the count (<see cref="ZeroAllocation"/>), and requires every window to be
/// exactly 0 bytes (all ten of a single rerun when exactly one window of the first measurement is not). The
/// flights alternate between a level launch, which strikes the target and so runs
/// the capsule tests and the character hit, and a launch 2° above it, which passes over the target's
/// bounding sphere and flies on until the terrain or its range ends it.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class ProjectileAllocationTests
{
    private const uint ProjectileId = 1;

    private const uint TargetId = 2;

    /// <summary>
    /// The projectile's owner: not the target, so the target's bounding sphere is tested every call and its
    /// capsules on every level flight.
    /// </summary>
    private const uint OwnerId = 3;

    /// <summary>The launch step, where every flight starts again (k = 0).</summary>
    private const uint LaunchStep = 1;

    /// <summary>
    /// The lifted launch's pitch above +X, in degrees. Its path passes at least 1.4 m from the target's
    /// bounding-sphere centre (radius about 0.95 m), so it never reaches the capsules: class 0 expires at its
    /// range and class 1 meets the terrain about 137 m out.
    /// </summary>
    private const double LiftedPitchDegrees = 2.0;

    /// <summary>
    /// The level launch's pitch: along +X, it strikes the standing target 30 m away, class 0 in the head
    /// (about 1.608 m up there, 0.03 m from the head's axis) and class 1 in the torso (about 1.313 m up).
    /// </summary>
    private const double LevelPitchDegrees = 0.0;

    /// <summary>The launch point and the projectile's position at launch: the eye of a character standing at the origin.</summary>
    private static readonly Vec3 Origin = new(0f, 1.62f, 0f);

    /// <summary>The standing target's feet: 30 m along +X.</summary>
    private static readonly Vec3 TargetFeet = new(30f, 0f, 0f);

    /// <summary>Rows <c>(classIndex, hz)</c>: class 0 (small arms) and class 1 (heavy), each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> Classes => TestRates.WithRateLast(0, 1);

    /// <summary>
    /// Given <see cref="ProjectileWorlds.Flat"/>, <see cref="TestConfigs.Compiled"/> at <paramref name="hz"/>, one
    /// standing target (ID 2) 30 m along +X whose three hitboxes and bounding sphere <see cref="HitboxBuilder"/>
    /// built once into arrays allocated before the count, a <see cref="ProjectileTargets"/> over them, and two
    /// launch projectiles built before the count (ID 1, owner 3, the row's class, LaunchStep 1, Origin and
    /// Position (0, 1.62, 0), the class's default muzzle speed): a level one along +X and a lifted one 2° above
    /// it. When the warm-up and ten counted windows of 1,000 calls (and ten more when the rerun runs,
    /// <see cref="ZeroAllocation"/>) of <c>ProjectileStep.Step(ref projectile, step, config, collision,
    /// targets, out hit, out expiry)</c> run, starting with the level launch, with <c>step</c> counting up from
    /// LaunchStep, and with the projectile set to the other launch struct and <c>step</c> to LaunchStep
    /// (struct, integer and bool assignments) whenever a call returns Hit or Expired, Then every window
    /// allocated exactly 0 bytes, the test made exactly the calls the measurement counts, and the counted
    /// calls of each measurement that ran hold at least one Flying return, at least one Hit on target 2 with
    /// Kind Character, and at least one end that is not a character hit (an Expired return, or a Hit of
    /// another kind), so they ran the flying path, the capsule tests with the character hit, and the other
    /// endings.
    /// </summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void ProjectileStepAllocatesNothing(int classIndex, int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var collision = ProjectileWorlds.FlatWorld;
        var hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        var bounds = new[] { HitboxBuilder.Build(config.HitboxLayouts[(int)Stance.Standing], TargetFeet, 0, hitboxes) };
        var targetIds = new[] { TargetId };
        var speed = ProjectileWorlds.MuzzleSpeed(config, classIndex);
        var level = Launch(classIndex, LevelPitchDegrees, speed);
        var lifted = Launch(classIndex, LiftedPitchDegrees, speed);
        var useLevel = true;
        var projectile = level;
        var step = LaunchStep;
        var calls = 0;
        var countedFlying = new int[ZeroAllocation.MaxMeasurements];
        var countedCharacterHits = new int[ZeroAllocation.MaxMeasurements];
        var countedOtherEnds = new int[ZeroAllocation.MaxMeasurements];

        // The index Measure passes within a window is not used: calls numbers the calls across the warm-up
        // and every window of both measurements. It is not named _, so out _ below stays a discard. The
        // ProjectileTargets is a ref struct, which a lambda cannot capture, so each call makes it over the
        // same arrays, which allocates nothing.
        var measurement = ZeroAllocation.Measure(windowCall =>
        {
            var targets = new ProjectileTargets(targetIds, hitboxes, bounds);
            var outcome = ProjectileStep.Step(ref projectile, step, config, collision, targets, out var hit, out _);
            var counted = calls >= ZeroAllocation.WarmUpCalls;
            var measured = counted ? (calls - ZeroAllocation.WarmUpCalls) / ZeroAllocation.CallsPerMeasurement : 0;
            if (outcome == ProjectileOutcome.Flying)
            {
                step++;
                if (counted)
                {
                    countedFlying[measured]++;
                }
            }
            else
            {
                if (counted)
                {
                    if (outcome == ProjectileOutcome.Hit && hit.Kind == HitKind.Character && hit.TargetId == TargetId)
                    {
                        countedCharacterHits[measured]++;
                    }
                    else
                    {
                        countedOtherEnds[measured]++;
                    }
                }

                useLevel = !useLevel;
                projectile = useLevel ? level : lifted;
                step = LaunchStep;
            }

            calls++;
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(
            measurement,
            $"class {classIndex} at {hz} Hz: ProjectileStep.Step over one target's 3 hitboxes and bounding sphere, alternating a level and a 2° flight after each Hit or Expired");
        Assert.Equal(measurement.Calls, calls);
        for (var m = 0; m < measurement.Measurements; m++)
        {
            Assert.True(
                countedFlying[m] >= 1 && countedCharacterHits[m] >= 1 && countedOtherEnds[m] >= 1,
                $"class {classIndex} at {hz} Hz: of the {ZeroAllocation.CallsPerMeasurement} counted calls of measurement {m + 1} of {measurement.Measurements}, {countedFlying[m]} returned Flying, {countedCharacterHits[m]} hit target {TargetId} (Kind Character) and {countedOtherEnds[m]} ended otherwise (Expired, or a Hit of another kind); each must happen at least once");
        }
    }

    /// <summary>The launch struct of the row's class at <paramref name="pitchDegrees"/> above +X.</summary>
    private static Projectile Launch(int classIndex, double pitchDegrees, float speed) => new()
    {
        Id = ProjectileId,
        OwnerId = OwnerId,
        Class = (byte)classIndex,
        LaunchStep = LaunchStep,
        Origin = Origin,
        LaunchVelocity = ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(90.0, pitchDegrees), speed),
        Position = Origin,
    };
}
