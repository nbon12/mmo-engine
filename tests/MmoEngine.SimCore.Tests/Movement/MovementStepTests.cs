using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// The movement step's sub-steps one at a time (FR-002, data-model.md#movement-step): the wish velocity
/// the buttons and stance give (sub-step 3), the horizontal approach toward it by at most rate·dt
/// (sub-step 4), the sub-move count (sub-step 7), the ground projection that keeps horizontal speed
/// on a slope (sub-step 6), and the ground probe (sub-step 8): it always reaches the contact band, and a
/// steep face touching the capsule never hides the walkable ground under it. Every step here calls
/// <see cref="MovementStep.Step"/> directly, on a <see cref="MovementWorlds"/> world (the side-face test on
/// the generated test world), with a candidate buffer the size a <c>SimWorld</c> uses. The
/// helpers at the end (a character held at an input for a number of steps, rest height, horizontal
/// speed) are shared with <see cref="StepUpTests"/>, <see cref="SlopeTests"/> and
/// <see cref="SlideTests"/>: their "grounded character" is a world's <see cref="MovementWorld.StartCharacter"/>,
/// at rest at its start, stepped through these helpers.
/// </summary>
public sealed class MovementStepTests
{
    /// <summary>The literal sub-step 3 scales a diagonal wish by.</summary>
    private const float Diagonal = 0.70710677f;

    /// <summary>The placeholder <c>movement.runSpeed</c>, m/s.</summary>
    private const float RunSpeed = 7f;

    /// <summary>The placeholder <c>movement.walkSpeed</c>, m/s.</summary>
    private const float WalkSpeed = 3.5f;

    /// <summary>The placeholder <c>movement.crouchSpeed</c>, m/s.</summary>
    private const float CrouchSpeed = 2f;

    /// <summary>
    /// The side-face start on the generated test world: flat ground at rest height, 4 m south of the slope
    /// lane's first row (z −106), beside its 30° ramp, which is 2.89 m high there.
    /// </summary>
    private static readonly Vec3 SideFaceStart = new(-95f, 0.01f, -110f);

    /// <summary>
    /// Every combination of Forward, Back, Left, Right and Walk (32), crossed with Standing and Crouched:
    /// 64 rows. Crouch itself is not a row: <see cref="MovementStep.WishVelocity"/> takes the stance
    /// sub-step 2 settled.
    /// </summary>
    public static TheoryData<Buttons, Stance> ButtonCombinations
    {
        get
        {
            Buttons[] movement = [Buttons.Forward, Buttons.Back, Buttons.Left, Buttons.Right, Buttons.Walk];
            var data = new TheoryData<Buttons, Stance>();
            foreach (var stance in new[] { Stance.Standing, Stance.Crouched })
            {
                for (var mask = 0; mask < 1 << movement.Length; mask++)
                {
                    var buttons = (Buttons)0;
                    for (var bit = 0; bit < movement.Length; bit++)
                    {
                        if ((mask & (1 << bit)) != 0)
                        {
                            buttons |= movement[bit];
                        }
                    }

                    data.Add(buttons, stance);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// The four sub-step 4 rates: grounded with a wish (<c>groundAccel</c>), grounded without
    /// (<c>groundDecel</c>), airborne with a wish (<c>airAccel</c>) and airborne without (0).
    /// </summary>
    public static TheoryData<string, int> HorizontalModes =>
        TestRates.WithRateLast("grounded-wish", "grounded-none", "airborne-wish", "airborne-none");

    /// <summary>
    /// Sub-step 3: with yaw 0 (sinψ 0, cosψ 1) forward is (0, 0, 1) and right is (−1, 0, 0), so the wish
    /// velocity equals, bit for bit, <c>((F − B)·(0, 0, 1)) + ((R − L)·(−1, 0, 0))</c> in float (signed
    /// zeros included), times <c>0.70710677f</c> when both net axes are non-zero, times crouchSpeed 2
    /// when crouched, else walkSpeed 3.5 when Walk is held, else runSpeed 7. Opposite buttons cancel, so
    /// Forward with Back, or Left with Right, adds nothing on that axis, and both pairs give (0, 0, 0).
    /// </summary>
    [Theory]
    [MemberData(nameof(ButtonCombinations))]
    public void WishDirectionFollowsTheButtons(Buttons buttons, Stance stance)
    {
        var config = CompiledConfig.Create(TestConfigs.Default());
        Assert.True(config.RunSpeed == RunSpeed && config.WalkSpeed == WalkSpeed && config.CrouchSpeed == CrouchSpeed,
            $"The placeholder speeds changed: run {config.RunSpeed:R}, walk {config.WalkSpeed:R}, crouch {config.CrouchSpeed:R}.");

        var forward = Held(buttons, Buttons.Forward) - Held(buttons, Buttons.Back);
        var right = Held(buttons, Buttons.Right) - Held(buttons, Buttons.Left);
        var expected = ((float)forward * new Vec3(0f, 0f, 1f)) + ((float)right * new Vec3(-1f, 0f, 0f));
        if (forward != 0 && right != 0)
        {
            expected *= Diagonal;
        }

        var speed = stance == Stance.Crouched ? CrouchSpeed : (buttons & Buttons.Walk) != 0 ? WalkSpeed : RunSpeed;
        expected *= speed;

        var wish = MovementStep.WishVelocity(buttons, stance, 0f, 1f, config);

        Assert.True(expected.Equals(wish), $"{buttons} {stance}: wish velocity {wish}, expected {expected} bit for bit");
        if (forward == 0 && right == 0)
        {
            Assert.True(wish.X == 0f && wish.Y == 0f && wish.Z == 0f, $"{buttons} {stance}: no net direction must give (0, 0, 0), not {wish}");
        }
    }

    /// <summary>
    /// Sub-step 4: on <see cref="MovementWorlds.Flat"/>, a character moving at (1, 0, 0) steps once toward
    /// the run target along +Z (Forward at yaw 0, (0, 0, 7)) or toward rest (no buttons, (0, 0, 0)).
    /// The horizontal velocity moves toward the target by at most rate·Dt: the change has length at most
    /// rate·Dt + 1e-6; it is the target exactly when the target was within rate·Dt (grounded-none at 20
    /// and 30 Hz); otherwise it is rate·Dt along the line to the target (within 1e-5 m/s per axis), and
    /// airborne without a wish it does not change. Grounded rows start at rest height at the start;
    /// airborne rows 1 m above it, so no contact touches the velocity.
    /// </summary>
    [Theory]
    [MemberData(nameof(HorizontalModes))]
    public void HorizontalVelocityMovesTowardTheTargetByAtMostRateTimesDt(string mode, int hz)
    {
        var world = MovementWorlds.Flat();
        var config = TestConfigs.Compiled(hz);
        var (grounded, wish, rate) = mode switch
        {
            "grounded-wish" => (true, true, config.GroundAccelPerStep),
            "grounded-none" => (true, false, config.GroundDecelPerStep),
            "airborne-wish" => (false, true, config.AirAccelPerStep),
            "airborne-none" => (false, false, 0f),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown mode."),
        };
        var start = world.StartCharacter() with { Velocity = new Vec3(1f, 0f, 0f) };
        if (!grounded)
        {
            start = start with { Position = world.Start + new Vec3(0f, 1f, 0f), Flags = default };
        }

        var input = wish ? Command(1, Buttons.Forward, world.Yaw) : Neutral(start);
        var (targetX, targetZ) = wish ? (0.0, (double)RunSpeed) : (0.0, 0.0);

        var step = Step(world, start, input, config);

        var after = step.After.Velocity;
        var (fromX, fromZ) = ((double)start.Velocity.X, (double)start.Velocity.Z);
        var change = Hypot(after.X - fromX, after.Z - fromZ);
        Assert.True(change <= rate + 1e-6, $"{mode} at {hz} Hz: the horizontal velocity changed by {change:R}, more than rate·Dt {rate:R}; velocity {after}");

        var gapX = targetX - fromX;
        var gapZ = targetZ - fromZ;
        var gap = Hypot(gapX, gapZ);
        if (gap <= rate)
        {
            Assert.True(after.X == (float)targetX && after.Z == (float)targetZ,
                $"{mode} at {hz} Hz: the target ({targetX}, {targetZ}) was within rate·Dt {rate:R}, so the velocity must be it exactly, not {after}");
        }
        else
        {
            var expectedX = fromX + (rate * gapX / gap);
            var expectedZ = fromZ + (rate * gapZ / gap);
            Assert.True(System.Math.Abs(after.X - expectedX) <= 1e-5 && System.Math.Abs(after.Z - expectedZ) <= 1e-5,
                $"{mode} at {hz} Hz: the velocity {after} is not rate·Dt {rate:R} toward the target ({targetX}, {targetZ}): expected ({expectedX:R}, {expectedZ:R}) in x and z");
        }
    }

    /// <summary>
    /// Sub-step 7: n = clamp(⌈distance / radius⌉, 1, maxSubMoves). No motion still takes one sub-move,
    /// a whole number of radii takes exactly that many (1.2f / 0.4f rounds to 3), anything over one more,
    /// and 100 m is clamped to 8.
    /// </summary>
    [Theory]
    [InlineData(0f, 1)]
    [InlineData(0.4f, 1)]
    [InlineData(0.41f, 2)]
    [InlineData(1.2f, 3)]
    [InlineData(100f, 8)]
    public void SubMoveCountIsClampedCeilOfDistanceOverRadius(float distance, int expected)
    {
        Assert.Equal(expected, MovementStep.SubMoveCount(distance, 0.4f, 8));
    }

    /// <summary>
    /// Sub-step 6: a character grounded mid-slope on <see cref="MovementWorlds.Slope"/>(30), 2.5 m up the
    /// ramp at rest on it (its bottom sphere r + contactSkin from the ramp plane, ground normal the
    /// ramp's), holding Forward up the slope with its velocity already at the run target (0, 0, 7), steps
    /// once. Its feet move horizontally by runSpeed·Dt within 1e-5 m and rise by that distance·tan 30°
    /// within contactTolerance, Velocity.x and Velocity.z keep the target's bits, and it ends Grounded.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void GroundProjectionKeepsHorizontalSpeed(int hz)
    {
        const float degrees = 30f;
        const float startZ = -9.5f;
        var world = MovementWorlds.Slope(degrees);
        var config = TestConfigs.Compiled(hz);
        Assert.InRange(startZ, world.FrontEdge + 1f, world.BackEdge - 1f);
        var target = MovementStep.WishVelocity(Buttons.Forward, Stance.Standing, 0f, 1f, config);
        Assert.True(target.X == 0f && target.Z == RunSpeed, $"The run target at yaw 0 must be (0, _, 7), not {target}");
        var start = RestingOn(world, world.Plane, startZ, config) with { Velocity = new Vec3(target.X, 0f, target.Z) };

        var step = Step(world, start, Command(1, Buttons.Forward, world.Yaw), config);

        var (before, after) = (step.Before.Position, step.After.Position);
        var horizontal = Hypot((double)after.X - before.X, (double)after.Z - before.Z);
        var run = (double)config.RunSpeed * config.Dt;
        Assert.True(System.Math.Abs(horizontal - run) <= 1e-5, $"At {hz} Hz the feet moved {horizontal:R} m horizontally, expected runSpeed·Dt {run:R} within 1e-5 m; {before} -> {after}");
        var rise = (double)after.Y - before.Y;
        var expectedRise = horizontal * System.Math.Tan(degrees * System.Math.PI / 180.0);
        Assert.True(System.Math.Abs(rise - expectedRise) <= config.ContactTolerance,
            $"At {hz} Hz the feet rose {rise:R} m, expected {expectedRise:R} m within contactTolerance; {before} -> {after}");
        var velocity = step.After.Velocity;
        Assert.True(BitConverter.SingleToUInt32Bits(velocity.X) == BitConverter.SingleToUInt32Bits(target.X)
            && BitConverter.SingleToUInt32Bits(velocity.Z) == BitConverter.SingleToUInt32Bits(target.Z),
            $"At {hz} Hz Velocity.x and Velocity.z must keep the target's bits: velocity {velocity}, target {target}");
        Assert.True(IsGrounded(step.After), $"At {hz} Hz the character must end Grounded on the slope; flags {step.After.Flags}");
    }

    /// <summary>
    /// Sub-step 8 with <c>movement.snapDistance</c> 0, the rule table's minimum (R06 allows it): Given a
    /// character at rest, Grounded, at <see cref="MovementWorlds.Flat"/>'s start, When it is stepped 60
    /// times with the neutral input, Then every step leaves it Grounded with GroundNormal (0, 1, 0),
    /// Velocity (0, 0, 0) and Position bit-identical to the start. The grounded probe sweeps
    /// max(snapDistance, 2·contactSkin), so a zero snap distance never skips it and the character never
    /// alternates between grounded and airborne.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ZeroSnapDistanceKeepsARestingCharacterGrounded(int hz)
    {
        var world = MovementWorlds.Flat();
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Movement!.SnapDistance = 0f;
        }));
        Assert.True(config.SnapDistance == 0f, $"The configuration must have snapDistance 0, not {config.SnapDistance:R}");
        var start = world.StartCharacter();
        Assert.True(IsGrounded(start) && AtRestOn(start.Position.Y, world.FootPoint.Y, config), $"The start must be Grounded at rest on the floor, not {start}");

        var steps = Idle(world, start, 60, config);

        for (var k = 0; k < steps.Length; k++)
        {
            var after = steps[k].After;
            Assert.True(
                IsGrounded(after) && after.GroundNormal.X == 0f && after.GroundNormal.Y == 1f && after.GroundNormal.Z == 0f,
                $"snapDistance 0 at {hz} Hz: step {k + 1} left the character {(IsGrounded(after) ? "Grounded" : "airborne")} with ground normal {after.GroundNormal}; position {after.Position}");
            Assert.True(
                after.Velocity.X == 0f && after.Velocity.Y == 0f && after.Velocity.Z == 0f && after.Position.Equals(start.Position),
                $"snapDistance 0 at {hz} Hz: step {k + 1} moved the resting character: position {after.Position} (start {start.Position}), velocity {after.Velocity}");
        }
    }

    /// <summary>
    /// US1.5 against a steep face (sub-step 8): Given a character that holds Forward from
    /// <see cref="MovementWorlds.Slope"/>(50)'s start for 3 simulated seconds, so that it ends Grounded at
    /// rest height on the flat before the foot, pressed against the 50° face it cannot climb (its bottom
    /// sphere's centre within capsuleRadius + contactSkin + contactTolerance of the face's plane), When
    /// Forward and Jump are held for one step, Then that step clears Grounded, its Velocity.y bits equal
    /// jumpSpeed − <c>GravityPerStep</c> as <see cref="CompiledConfig"/> computes them, and Position.y
    /// rises. The steep face touching the capsule does not hide the ground under the feet, so the character
    /// is Grounded and can jump.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void JumpAtTheFootOfASteepFaceLeavesTheGround(int hz)
    {
        const float degrees = 50f;
        var world = MovementWorlds.Slope(degrees);
        var config = TestConfigs.Compiled(hz);
        Assert.True(config.CosSlopeLimit > world.Plane.Normal.Y, $"The {degrees:R}° face must be steeper than the slope limit: normal {world.Plane.Normal}, CosSlopeLimit {config.CosSlopeLimit:R}");
        var held = Hold(world, world.StartCharacter(), Buttons.Forward, world.Yaw, 3 * hz, config);
        var resting = held[^1].After;
        Assert.True(
            IsGrounded(resting) && resting.Position.Z < world.FrontEdge && AtRestOn(resting.Position.Y, world.FootPoint.Y, config),
            $"{degrees:R}° at {hz} Hz: after holding Forward the character must be Grounded at rest on the flat before the foot (z {world.FrontEdge:R}), not {resting}");
        var bottom = resting.Position + new Vec3(0f, config.CapsuleRadius, 0f);
        var gap = world.Plane.SignedDistance(bottom);
        var touching = (double)config.CapsuleRadius + config.ContactSkin + config.ContactTolerance;
        Assert.True(gap <= touching, $"{degrees:R}° at {hz} Hz: the bottom sphere's centre is {gap:R} m from the face's plane, so it is not pressed against the face (touching is within {touching:R} m)");

        var jump = Step(world, resting, Command(held[^1].After.LastSequence + 1, Buttons.Forward | Buttons.Jump, world.Yaw), config);

        var expected = config.JumpSpeed - config.GravityPerStep;
        Assert.False(IsGrounded(jump.After), $"{degrees:R}° at {hz} Hz: the jump step left the character Grounded; position {jump.After.Position}, velocity {jump.After.Velocity}");
        Assert.True(
            BitConverter.SingleToUInt32Bits(jump.After.Velocity.Y) == BitConverter.SingleToUInt32Bits(expected),
            $"{degrees:R}° at {hz} Hz: the jump step's Velocity.y is {jump.After.Velocity.Y:R}, not jumpSpeed − GravityPerStep {expected:R} bit for bit");
        Assert.True(
            jump.After.Position.Y > resting.Position.Y,
            $"{degrees:R}° at {hz} Hz: the jump step did not rise: Position.y {resting.Position.Y:R} -> {jump.After.Position.Y:R}");
    }

    /// <summary>
    /// Sub-step 8 against a steep side face: Given a character at rest, Grounded, on the generated test
    /// world's flat ground at <see cref="SideFaceStart"/>, facing +Z (yaw 0) toward the slope lane's side,
    /// where the terrain rises from the ground to the 30° ramp within one cell, steeper than the slope
    /// limit, When Forward is held for 2 simulated seconds, Then every step leaves it Grounded at rest
    /// height on the flat (HeightAt under the feet 0), with GroundNormal.y at least <c>CosSlopeLimit</c>.
    /// The run must reach the face, so the clause cannot pass vacuously: at some step the terrain
    /// 2 · capsuleRadius ahead of the feet is more than stepHeight above the flat.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void WalkingIntoASteepSideFaceStaysGrounded(int hz)
    {
        var world = TestWorlds.TestWorld;
        var config = TestConfigs.Compiled(hz);
        Assert.True(world.HeightAt(SideFaceStart.X, SideFaceStart.Z) == 0f, $"The start {SideFaceStart} must be on flat ground at height 0");
        var candidates = Candidates(world, config);
        var character = new CharacterState
        {
            Id = 1,
            Position = SideFaceStart,
            Velocity = new Vec3(0f, 0f, 0f),
            GroundNormal = new Vec3(0f, 1f, 0f),
            AimYaw = 0,
            Stance = Stance.Standing,
            Flags = CharacterFlags.Grounded,
            ActiveSlot = 0,
            WeaponCount = (byte)TestStates.PlaceholderWeapons.Count,
        };

        var lookAhead = 2f * config.CapsuleRadius;
        var reached = false;
        for (var k = 0; k < 2 * hz; k++)
        {
            var input = Command((uint)(k + 1), Buttons.Forward, 0);
            var before = character;
            var result = MovementStep.Step(ref character, in input, config, world, candidates);
            Assert.True(result.Fault == StepFaultCode.None, $"Side face at {hz} Hz: step {k + 1} faulted with {result.Fault}; the character was {before}");
            var feet = character.Position;
            var ground = world.HeightAt(feet.X, feet.Z);
            Assert.True(
                IsGrounded(character) && character.GroundNormal.Y >= config.CosSlopeLimit,
                $"Side face at {hz} Hz: step {k + 1} left the character {(IsGrounded(character) ? "Grounded" : "airborne")} with ground normal {character.GroundNormal}; position {feet}, velocity {character.Velocity}");
            Assert.True(
                ground == 0f && AtRestOn(feet.Y, ground, config),
                $"Side face at {hz} Hz: step {k + 1} left the feet at {feet}, not at rest height on the flat (HeightAt {ground:R})");
            reached |= world.HeightAt(feet.X, feet.Z + lookAhead) > config.StepHeight;
        }

        Assert.True(reached, $"Side face at {hz} Hz: the terrain {lookAhead:R} m ahead never rose more than stepHeight, so the run never reached the side face; it ended at {character.Position}");
    }

    /// <summary>One <see cref="MovementStep.Step"/> call: the character before and after it, and its result.</summary>
    internal readonly record struct Stepped(CharacterState Before, CharacterState After, MovementResult Result);

    /// <summary>
    /// Rows <c>(hz, value, expected)</c>: each row crossed with every step rate of
    /// <see cref="TestRates.Hz"/>, in the order given and 20, 30, 60 within a row.
    /// </summary>
    internal static TheoryData<int, float, bool> AtEveryRate(params (float Value, bool Expected)[] rows)
    {
        var data = new TheoryData<int, float, bool>();
        foreach (var (value, expected) in rows)
        {
            foreach (var hz in TestRates.Hz)
            {
                data.Add(hz, value, expected);
            }
        }

        return data;
    }

    /// <summary>A candidate buffer the size <c>SimWorld</c> uses: <c>MaxQueryCandidates(3·r + 2·contactSkin)</c>.</summary>
    internal static int[] Candidates(CollisionWorld world, CompiledConfig config) =>
        new int[world.MaxQueryCandidates((3f * config.CapsuleRadius) + (2f * config.ContactSkin))];

    /// <summary>A command holding <paramref name="buttons"/> at <paramref name="aimYaw"/>, pitch 0, slot 0.</summary>
    internal static InputCommand Command(uint sequence, Buttons buttons, ushort aimYaw) =>
        new(sequence, sequence, buttons, aimYaw, 0, 0);

    /// <summary>The neutral input: no buttons, and the character's aim, slot and <c>lastSequence</c> unchanged.</summary>
    internal static InputCommand Neutral(in CharacterState character) =>
        new(character.LastSequence, 0, 0, character.AimYaw, character.AimPitch, character.ActiveSlot);

    /// <summary>One <see cref="MovementStep.Step"/> of <paramref name="character"/> with <paramref name="input"/>; it must not fault.</summary>
    internal static Stepped Step(MovementWorld world, CharacterState character, InputCommand input, CompiledConfig config)
    {
        ArgumentNullException.ThrowIfNull(world);
        var before = character;
        var result = MovementStep.Step(ref character, in input, config, world.World, Candidates(world.World, config));
        Assert.True(result.Fault == StepFaultCode.None, $"The step faulted with {result.Fault}; the character was {before}");
        return new Stepped(before, character, result);
    }

    /// <summary>
    /// <paramref name="steps"/> steps from <paramref name="start"/>, each with a command holding
    /// <paramref name="buttons"/> at <paramref name="aimYaw"/> (sequences 1, 2, 3, ...), the character
    /// carried from step to step. No step may fault.
    /// </summary>
    internal static Stepped[] Hold(MovementWorld world, CharacterState start, Buttons buttons, ushort aimYaw, int steps, CompiledConfig config) =>
        Run(world, start, steps, config, (k, _) => Command((uint)(k + 1), buttons, aimYaw));

    /// <summary><paramref name="steps"/> steps from <paramref name="start"/> with the neutral input. No step may fault.</summary>
    internal static Stepped[] Idle(MovementWorld world, CharacterState start, int steps, CompiledConfig config) =>
        Run(world, start, steps, config, (_, character) => Neutral(character));

    /// <summary>Whether the character's Grounded flag is set.</summary>
    internal static bool IsGrounded(in CharacterState character) => (character.Flags & CharacterFlags.Grounded) != 0;

    /// <summary>
    /// Rest height: feet at <paramref name="feetY"/> above a level surface at <paramref name="surfaceY"/>
    /// with feet − surface in [−contactTolerance, contactSkin + contactTolerance].
    /// </summary>
    internal static bool AtRestOn(float feetY, float surfaceY, CompiledConfig config)
    {
        var gap = (double)feetY - surfaceY;
        return gap >= -(double)config.ContactTolerance && gap <= (double)config.ContactSkin + config.ContactTolerance;
    }

    /// <summary>The length of the velocity's x and z, in double.</summary>
    internal static double HorizontalSpeed(Vec3 velocity) => Hypot(velocity.X, velocity.Z);

    private static double Hypot(double x, double z) => System.Math.Sqrt((x * x) + (z * z));

    private static int Held(Buttons buttons, Buttons button) => (buttons & button) != 0 ? 1 : 0;

    /// <summary>
    /// A character grounded at rest on <paramref name="plane"/> on the heading line (x 0) at
    /// <paramref name="z"/>: its bottom sphere's centre r + contactSkin from the plane, so the feet are
    /// (r + contactSkin)/n.y − r above the plane's surface there, with the plane's normal as ground normal.
    /// </summary>
    private static CharacterState RestingOn(MovementWorld world, WorldPlane plane, float z, CompiledConfig config)
    {
        var surface = (plane.Offset - ((double)plane.Normal.Z * z)) / plane.Normal.Y;
        var feet = surface + (((double)config.CapsuleRadius + config.ContactSkin) / plane.Normal.Y) - config.CapsuleRadius;
        return world.StartCharacter() with
        {
            Position = new Vec3(0f, (float)feet, z),
            GroundNormal = plane.Normal,
            Flags = CharacterFlags.Grounded,
        };
    }

    private static Stepped[] Run(MovementWorld world, CharacterState start, int steps, CompiledConfig config, Func<int, CharacterState, InputCommand> inputFor)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);
        var candidates = Candidates(world.World, config);
        var record = new Stepped[steps];
        var character = start;
        for (var k = 0; k < steps; k++)
        {
            var input = inputFor(k, character);
            var before = character;
            var result = MovementStep.Step(ref character, in input, config, world.World, candidates);
            Assert.True(result.Fault == StepFaultCode.None, $"Step {k + 1} of {steps} faulted with {result.Fault}; the character was {before}");
            record[k] = new Stepped(before, character, result);
        }

        return record;
    }
}
