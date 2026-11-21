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
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three batches of 1,000 calls over hitbox, bounds
/// and ID buffers allocated before the count (<see cref="ZeroAllocation"/>), and requires the smallest batch
/// to be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class ProjectileAllocationTests
{
    private const uint ProjectileId = 1;

    private const uint TargetId = 2;

    /// <summary>The projectile's owner: not the target, so the target's sphere and capsules are tested every call.</summary>
    private const uint OwnerId = 3;

    /// <summary>The launch step, where every flight starts again (k = 0).</summary>
    private const uint LaunchStep = 1;

    /// <summary>The launch pitch above +X, in degrees.</summary>
    private const double LaunchPitchDegrees = 2.0;

    /// <summary>The launch point and the projectile's position at launch: the eye of a character standing at the origin.</summary>
    private static readonly Vec3 Origin = new(0f, 1.62f, 0f);

    /// <summary>The standing target's feet: 30 m along +X.</summary>
    private static readonly Vec3 TargetFeet = new(30f, 0f, 0f);

    /// <summary>Rows <c>(classIndex, hz)</c>: class 0 (small arms) and class 1 (heavy), each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> Classes => TestRates.WithRateLast(0, 1);

    /// <summary>
    /// Given <see cref="ProjectileWorlds.Flat"/>, <see cref="TestConfigs.Compiled"/> at <paramref name="hz"/>, one
    /// standing target (ID 2) 30 m along +X whose three hitboxes and bounding sphere <see cref="HitboxBuilder"/>
    /// built once into arrays allocated before the count, a <see cref="ProjectileTargets"/> over them, and a
    /// launch projectile (ID 1, owner 3, the row's class, LaunchStep 1) whose Origin and Position are
    /// (0, 1.62, 0) and whose launch velocity is the class's default muzzle speed 2° above +X. When the warm-up
    /// and three counted batches of 1,000 calls of <c>ProjectileStep.Step(ref projectile, step, config,
    /// collision, targets, out hit, out expiry)</c> run, with <c>step</c> counting up from LaunchStep, and with
    /// the projectile set back to its launch struct and <c>step</c> to LaunchStep (struct and integer
    /// assignments) whenever a call returns Hit or Expired, Then the smallest batch allocated exactly 0 bytes,
    /// and the counted calls hold at least one Flying return and at least one Hit or Expired return, so they ran
    /// both the flying and the ending paths.
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
        var launch = new Projectile
        {
            Id = ProjectileId,
            OwnerId = OwnerId,
            Class = (byte)classIndex,
            LaunchStep = LaunchStep,
            Origin = Origin,
            LaunchVelocity = ProjectileWorlds.LaunchVelocity(
                ProjectileWorlds.Direction(90.0, LaunchPitchDegrees),
                ProjectileWorlds.MuzzleSpeed(config, classIndex)),
            Position = Origin,
        };
        var projectile = launch;
        var step = LaunchStep;
        var calls = 0;
        var countedFlying = 0;
        var countedEnded = 0;

        // The batch index MeasureBatches passes is not used: calls numbers the calls across the warm-up and
        // every batch. It is not named _, so out _ below stays a discard. The ProjectileTargets is a ref struct,
        // which a lambda cannot capture, so each call makes it over the same arrays, which allocates nothing.
        var counts = ZeroAllocation.MeasureBatches(batchCall =>
        {
            var targets = new ProjectileTargets(targetIds, hitboxes, bounds);
            var outcome = ProjectileStep.Step(ref projectile, step, config, collision, targets, out _, out _);
            var counted = calls >= ZeroAllocation.WarmUpCalls;
            if (outcome == ProjectileOutcome.Flying)
            {
                step++;
                if (counted)
                {
                    countedFlying++;
                }
            }
            else
            {
                projectile = launch;
                step = LaunchStep;
                if (counted)
                {
                    countedEnded++;
                }
            }

            calls++;
        });

        ZeroAllocation.AssertABatchAllocatedNothing(
            counts,
            $"class {classIndex} at {hz} Hz: ProjectileStep.Step over one target's 3 hitboxes and bounding sphere, restarting the flight after each Hit or Expired");
        Assert.Equal(ZeroAllocation.TotalCalls, calls);
        Assert.True(
            countedFlying >= 1 && countedEnded >= 1,
            $"class {classIndex} at {hz} Hz: of the {ZeroAllocation.Batches * ZeroAllocation.CallsPerBatch} counted calls, {countedFlying} returned Flying and {countedEnded} Hit or Expired; each must happen at least once");
    }
}
