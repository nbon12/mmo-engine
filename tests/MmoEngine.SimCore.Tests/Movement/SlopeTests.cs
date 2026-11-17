using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// The slope limit (US1.4 supporting, FR-002, data-model.md#movement-step): with
/// <c>slopeLimitDegrees</c> 45, a grounded character holding Forward up a slope climbs one below the
/// limit and stays grounded, and is refused by a steeper one, at 20, 30 and 60 Hz. Each run starts from
/// <see cref="MovementStepTests"/>' grounded character, <see cref="MovementWorlds.Slope"/>'s
/// <see cref="MovementWorld.StartCharacter"/> at rest on the flat at the slope's foot (4 m before the
/// ramp, facing up it), and holds Forward for 3 simulated seconds through <see cref="MovementStep.Step"/>:
/// enough to cross the 6 m ramp and end well onto the upper flat when it climbs.
/// </summary>
public sealed class SlopeTests
{
    /// <summary>30° and 44° climb; 46°, 50° and 70° are refused. Each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, float, bool> Slopes =>
        MovementStepTests.AtEveryRate((30f, true), (44f, true), (46f, false), (50f, false), (70f, false));

    /// <summary>
    /// Climbing rows: the character is Grounded at every step and ends on the top flat (past the ramp's
    /// top edge by more than capsuleRadius) at rest height on it. Refused rows: whenever it is Grounded
    /// its GroundNormal.y is at least <c>CosSlopeLimit</c>, Position.y never exceeds the foot height
    /// plus stepHeight, and every step that ends with the feet at rest height on the flat before the foot
    /// is Grounded there, so the steep face it is refused by never hides the walkable ground under it. So
    /// the refusal cannot pass for a character that never reached the ramp, at some step the terrain
    /// 2 · capsuleRadius ahead of the feet along the heading is more than stepHeight above the foot (as
    /// <c>MovementScenarioTests.SlopeBelowTheLimitIsClimbedAndAboveItIsRefused</c> checks; a capsule
    /// against a steep ramp touches it while its feet are still before the foot line, so the feet crossing
    /// that line is not required).
    /// </summary>
    [Theory]
    [MemberData(nameof(Slopes))]
    public void SlopesBelowTheLimitClimbAndSteeperAreRefused(int hz, float degrees, bool climbs)
    {
        var world = MovementWorlds.Slope(degrees);
        var config = TestConfigs.Compiled(hz);
        var start = world.StartCharacter();
        Assert.True(MovementStepTests.IsGrounded(start) && MovementStepTests.AtRestOn(start.Position.Y, world.FootPoint.Y, config),
            $"The start must be grounded at rest height on the flat before the foot, not {start}");

        var steps = MovementStepTests.Hold(world, start, Buttons.Forward, world.Yaw, 3 * hz, config);

        if (climbs)
        {
            for (var k = 0; k < steps.Length; k++)
            {
                var after = steps[k].After;
                Assert.True(MovementStepTests.IsGrounded(after),
                    $"{degrees:R}° at {hz} Hz: step {k + 1} left the character airborne while climbing; position {after.Position}, velocity {after.Velocity}");
            }

            var last = steps[^1].After;
            var past = (double)last.Position.Z - world.BackEdge;
            Assert.True(past > config.CapsuleRadius,
                $"{degrees:R}° at {hz} Hz: the character ended {past:R} m past the ramp's top edge {world.BackEdge:R}, not on the top flat; position {last.Position}");
            Assert.True(MovementStepTests.AtRestOn(last.Position.Y, world.TopHeight, config),
                $"{degrees:R}° at {hz} Hz: the last Position.y {last.Position.Y:R} is not at rest height on the top flat {world.TopHeight:R}");
            return;
        }

        var ceiling = (double)world.FootPoint.Y + config.StepHeight;
        var angle = world.Yaw * 2.0 * System.Math.PI / 65536.0;
        var (forwardX, forwardZ) = (System.Math.Sin(angle), System.Math.Cos(angle));
        var lookAhead = 2.0 * config.CapsuleRadius;
        var atSteepSlope = false;
        for (var k = 0; k < steps.Length; k++)
        {
            var after = steps[k].After;
            var feet = after.Position;
            var ahead = world.World.HeightAt((float)(feet.X + (lookAhead * forwardX)), (float)(feet.Z + (lookAhead * forwardZ)));
            atSteepSlope |= ahead > ceiling;
            if (MovementStepTests.IsGrounded(after))
            {
                Assert.True(after.GroundNormal.Y >= config.CosSlopeLimit,
                    $"{degrees:R}° at {hz} Hz: step {k + 1} left the character Grounded on normal {after.GroundNormal}, steeper than CosSlopeLimit {config.CosSlopeLimit:R}");
            }

            Assert.True(after.Position.Y <= ceiling,
                $"{degrees:R}° at {hz} Hz: step {k + 1} put Position.y at {after.Position.Y:R}, above the foot height + stepHeight {ceiling:R}; position {after.Position}");
            if (after.Position.Z < world.FrontEdge && MovementStepTests.AtRestOn(after.Position.Y, world.FootPoint.Y, config))
            {
                Assert.True(MovementStepTests.IsGrounded(after),
                    $"{degrees:R}° at {hz} Hz: step {k + 1} left the character airborne at rest height on the flat before the foot; position {after.Position}, velocity {after.Velocity}");
            }
        }

        Assert.True(atSteepSlope,
            $"{degrees:R}° at {hz} Hz: the terrain {lookAhead:R} m ahead of the feet never rose more than stepHeight above the foot, so the character never reached the steep slope; it ended at {steps[^1].After.Position}");
    }
}
