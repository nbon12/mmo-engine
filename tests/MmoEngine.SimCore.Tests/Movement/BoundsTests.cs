using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// Edge case "a character pushed out of the heightfield's bounds is clamped to the playable area and
/// reported, not left in undefined terrain" (spec Edge Cases; data-model.md#movement-step: a boundary plane
/// hit and the safety clamp report <c>ClampedToBounds</c>). A <see cref="SimWorld"/> on
/// <see cref="MovementWorlds.Flat"/>, whose playable area is [−32, 32] in X and Z, steps one character
/// that starts at rest 1 m inside the area and runs outward with Forward held for 30 steps.
/// </summary>
public sealed class BoundsTests
{
    private const ulong Salt = 11;

    /// <summary>The steps each run holds Forward.</summary>
    private const int Steps = 30;

    /// <summary>How far inside the playable area the character starts, per axis it runs toward, m.</summary>
    private const double Inset = 1.0;

    /// <summary>The offset of <c>flags</c> in a character record (contracts/state-serialization.md#character-record).</summary>
    private const int FlagsOffset = 45;

    /// <summary>Character <c>flags</c> bit 1, ClampedToBounds.</summary>
    private const byte ClampedToBoundsFlag = 0x02;

    /// <summary>The movement outcome bit value of <c>MovementOutcome.ClampedToBounds</c>.</summary>
    private const byte ClampedToBoundsOutcome = 8;

    /// <summary>Yaw 16384 faces +X; 24576 faces (+X, −Z) at 45°; 32768 faces −Z.</summary>
    private const ushort FacingPlusX = 16384;

    private const ushort FacingPlusXMinusZ = 24576;

    private const ushort FacingMinusZ = 32768;

    /// <summary>
    /// Rows <c>(approach, hz)</c>: run at the +X edge, run at the −Z edge, and run diagonally into the
    /// (+X, −Z) corner, each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<string, int> Approaches =>
        TestRates.WithRateLast("run-at-plus-x-edge", "run-at-minus-z-edge", "run-into-corner");

    /// <summary>
    /// Given the character 1 m inside the playable area, Grounded at rest and facing outward, When Forward
    /// is held for 30 steps, Then at every step <c>Position.x</c> stays within [OriginX, OriginX +
    /// (Columns − 1)·CellSize] and <c>Position.z</c> within [OriginZ, OriginZ + (Rows − 1)·CellSize]; and on
    /// the first step that reaches the edge the state's <c>flags</c> has bit 1 (ClampedToBounds, 0x02) set
    /// and <c>Events.MovementOutcomes</c> holds <c>{CharacterId 1, Flags}</c> with bit value 8
    /// (<c>MovementOutcome.ClampedToBounds</c>) set. The first step that reaches the edge is the first whose
    /// unobstructed run from rest (speed min(k·groundAccel·dt, runSpeed) after step k, so the distance
    /// covered is the discrete sum of those speeds times dt) would take the capsule axis nearer than
    /// capsuleRadius + contactSkin to the boundary plane, so the sweep must stop it there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Approaches))]
    public void CharacterPushedPastTheEdgeIsClampedAndReported(string approach, int hz)
    {
        var flat = MovementWorlds.Flat();
        var heightfield = flat.World.Heightfield;
        var minX = heightfield.OriginX;
        var maxX = heightfield.OriginX + ((float)(heightfield.Columns - 1) * heightfield.CellSize);
        var minZ = heightfield.OriginZ;
        var maxZ = heightfield.OriginZ + ((float)(heightfield.Rows - 1) * heightfield.CellSize);
        var (start, yaw, axisShare) = approach switch
        {
            "run-at-plus-x-edge" => (new Vec3((float)(maxX - Inset), flat.Start.Y, 0f), FacingPlusX, 1.0),
            "run-at-minus-z-edge" => (new Vec3(0f, flat.Start.Y, (float)(minZ + Inset)), FacingMinusZ, 1.0),
            "run-into-corner" => (new Vec3((float)(maxX - Inset), flat.Start.Y, (float)(minZ + Inset)), FacingPlusXMinusZ, System.Math.Sqrt(0.5)),
            _ => throw new ArgumentOutOfRangeException(nameof(approach), approach, "Unknown approach."),
        };

        var config = TestConfigs.Compiled(hz);
        var reachStep = FirstStepReachingTheEdge(hz, axisShare);
        Assert.True(
            reachStep <= Steps,
            $"{approach} at {hz} Hz: the run reaches the edge at step {reachStep}, after the {Steps} steps run");

        var world = new SimWorld(config, flat.World);
        world.Reset(TestStates.Build(0, [new CharacterRecord { Id = 1, Position = (start.X, start.Y, start.Z), AimYaw = yaw }]), Salt);

        for (var k = 1; k <= Steps; k++)
        {
            var report = world.Step([new CharacterCommand(0, new InputCommand((uint)k, (uint)k, Buttons.Forward, yaw, 0, 0))]);
            Assert.True(
                report.Fault.Code == StepFaultCode.None,
                $"{approach} at {hz} Hz: step {k} faulted with {report.Fault.Code} at {report.Fault.Path}");

            var position = StateSerializer.ReadCommitted(report.State).Characters[0].Position;
            Assert.True(
                position.X >= minX && position.X <= maxX && position.Z >= minZ && position.Z <= maxZ,
                $"{approach} at {hz} Hz: after step {k} the position {position} is outside the playable area [{minX}, {maxX}] x [{minZ}, {maxZ}]");

            if (k != reachStep)
            {
                continue;
            }

            var flags = report.State[TestStates.CharacterOffset(0) + FlagsOffset];
            Assert.True(
                (flags & ClampedToBoundsFlag) != 0,
                $"{approach} at {hz} Hz: step {k} reaches the edge, but the state's flags are 0x{flags:X2}, without ClampedToBounds (0x02); position {position}");

            var outcomes = report.Events.MovementOutcomes.ToArray();
            var outcome = Assert.Single(outcomes);
            Assert.Equal(1u, outcome.CharacterId);
            Assert.True(
                ((byte)outcome.Flags & ClampedToBoundsOutcome) != 0,
                $"{approach} at {hz} Hz: step {k} reaches the edge, but its movement outcome flags are {outcome.Flags} ({(byte)outcome.Flags}), without ClampedToBounds (8)");
        }
    }

    /// <summary>
    /// The first step k after which the unobstructed run from rest has covered more than
    /// <see cref="Inset"/> − (capsuleRadius + contactSkin) along each axis it runs toward, computed in double
    /// from the placeholders: after step i the speed is min(i·groundAccel·dt, runSpeed) and the run moves
    /// that speed times dt, and <paramref name="axisShare"/> of it goes along each axis. The run must enter
    /// the contact shell by at least 1e-3 m at that step, so the sweep stops it there whatever its rounding.
    /// </summary>
    private static int FirstStepReachingTheEdge(int hz, double axisShare)
    {
        var movement = TestConfigs.Default().Movement!;
        var dt = 1.0 / hz;
        var gap = Inset - ((double)movement.CapsuleRadius + movement.ContactSkin);
        var covered = 0.0;
        for (var k = 1; k <= Steps; k++)
        {
            var speed = System.Math.Min(k * (double)movement.GroundAccel * dt, movement.RunSpeed);
            covered += speed * dt * axisShare;
            if (covered > gap)
            {
                Assert.True(
                    covered - gap >= 1e-3,
                    $"At {hz} Hz step {k} enters the contact shell by only {covered - gap:R} m; the test needs a clear crossing");
                return k;
            }
        }

        return Steps + 1;
    }
}
