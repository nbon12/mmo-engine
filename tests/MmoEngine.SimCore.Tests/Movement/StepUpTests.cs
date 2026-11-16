using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// Step-up (US1.3 supporting, FR-002, data-model.md#movement-step sub-move 3): a grounded character
/// running into a box step climbs a rise up to the step height (placeholder 0.35 m) and is blocked by a
/// higher one, at 20, 30 and 60 Hz. Each run starts from <see cref="MovementStepTests"/>' grounded
/// character, <see cref="MovementWorlds.Stair"/>'s <see cref="MovementWorld.StartCharacter"/> at rest on
/// the floor 2 m before the step's front face, facing it, and holds Forward for 2 simulated seconds
/// through <see cref="MovementStep.Step"/>; the step's top runs 20 m deep, so a climb ends on it.
/// </summary>
public sealed class StepUpTests
{
    /// <summary>Rises of 0.10 and 0.34 m climb; 0.36 and 0.50 m block. Each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, float, bool> Rises =>
        MovementStepTests.AtEveryRate((0.10f, true), (0.34f, true), (0.36f, false), (0.50f, false));

    /// <summary>
    /// Climbing rows: the last step is Grounded with Position.y at rest height on the stair top and
    /// Position past the front edge by more than capsuleRadius, and no single step raises Position.y by
    /// more than stepHeight + contactSkin. Blocking rows: at every step Position.y stays at rest height on
    /// the floor, the feet stay before the front face, and the capsule axis never comes nearer the
    /// stair's front face than capsuleRadius − contactTolerance.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rises))]
    public void RisesUpToTheStepHeightClimbAndHigherRisesBlock(int hz, float rise, bool climbs)
    {
        var world = MovementWorlds.Stair(rise);
        var config = TestConfigs.Compiled(hz);
        var start = world.StartCharacter();
        Assert.True(MovementStepTests.IsGrounded(start) && MovementStepTests.AtRestOn(start.Position.Y, world.FootPoint.Y, config),
            $"The start must be grounded at rest height on the floor, not {start}");

        var steps = MovementStepTests.Hold(world, start, Buttons.Forward, world.Yaw, 2 * hz, config);

        if (climbs)
        {
            var maxRaise = (double)config.StepHeight + config.ContactSkin;
            for (var k = 0; k < steps.Length; k++)
            {
                var raised = (double)steps[k].After.Position.Y - steps[k].Before.Position.Y;
                Assert.True(raised <= maxRaise,
                    $"rise {rise:R} at {hz} Hz: step {k + 1} raised Position.y by {raised:R} m, more than stepHeight + contactSkin {maxRaise:R}; {steps[k].Before.Position} -> {steps[k].After.Position}");
            }

            var last = steps[^1].After;
            Assert.True(MovementStepTests.IsGrounded(last), $"rise {rise:R} at {hz} Hz: the last step must be Grounded; flags {last.Flags}, position {last.Position}");
            Assert.True(MovementStepTests.AtRestOn(last.Position.Y, world.TopHeight, config),
                $"rise {rise:R} at {hz} Hz: the last Position.y {last.Position.Y:R} is not at rest height on the stair top {world.TopHeight:R}");
            var past = (double)last.Position.Z - world.FrontEdge;
            Assert.True(past > config.CapsuleRadius,
                $"rise {rise:R} at {hz} Hz: the last Position is {past:R} m past the front edge {world.FrontEdge:R}, not more than capsuleRadius; position {last.Position}");
            return;
        }

        var nearest = (double)config.CapsuleRadius - config.ContactTolerance;
        for (var k = 0; k < steps.Length; k++)
        {
            var after = steps[k].After;
            Assert.True(MovementStepTests.AtRestOn(after.Position.Y, world.FootPoint.Y, config),
                $"rise {rise:R} at {hz} Hz: step {k + 1} left Position.y at {after.Position.Y:R}, not at rest height on the floor; position {after.Position}");
            Assert.True(after.Position.Z < world.FrontEdge,
                $"rise {rise:R} at {hz} Hz: step {k + 1} put the feet at z {after.Position.Z:R}, past the front face at {world.FrontEdge:R}");
            var distance = AxisToFrontFace(after, world, config, rise);
            Assert.True(distance >= nearest,
                $"rise {rise:R} at {hz} Hz: step {k + 1} left the axis {distance:R} m from the stair face, nearer than capsuleRadius − contactTolerance {nearest:R}; position {after.Position}");
        }
    }

    /// <summary>
    /// The distance, in double, from the character's capsule axis (feet + r to feet + the stance's axis
    /// top, vertical) to the stair's front face, the rectangle x in ±<see cref="MovementWorlds.HalfWidth"/>,
    /// y in [0, rise] on the plane z = <see cref="MovementWorld.FrontEdge"/>. Both are axis-aligned
    /// boxes (the axis a degenerate one), so the distance is the length of the per-axis gaps.
    /// </summary>
    private static double AxisToFrontFace(CharacterState character, MovementWorld world, CompiledConfig config, float rise)
    {
        var feet = character.Position;
        var stance = character.Stance == Stance.Crouched ? CompiledConfig.Crouched : CompiledConfig.Standing;
        var axisBottom = (double)feet.Y + config.CapsuleRadius;
        var axisTop = (double)feet.Y + config.AxisTop[stance];
        var dx = System.Math.Max(0.0, System.Math.Abs((double)feet.X) - MovementWorlds.HalfWidth);
        var dy = System.Math.Max(0.0, System.Math.Max(axisBottom - rise, world.FootPoint.Y - axisTop));
        var dz = world.Plane.SignedDistance(feet);
        return System.Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
