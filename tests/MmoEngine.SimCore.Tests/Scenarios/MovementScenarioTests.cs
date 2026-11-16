using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Movement;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The named movement scenarios of US1 (spec acceptance scenarios 1 to 6 and the movement edge cases,
/// contracts/simcore-cli.md#named-scenario-streams). Each test builds its scenario in process with
/// <see cref="ScenarioRuns.Run(string, int)"/>: <see cref="ScenarioCatalog"/>'s definition over the
/// generated test world (never the committed <c>.mmcd</c>), at 20, 30 and 60 Hz, stepped through a
/// <see cref="SimWorld"/>, and asserts the outcome its acceptance scenario states on the committed
/// characters of every step.
/// </summary>
/// <remarks>
/// <para>
/// Before asserting an outcome, each test checks its Given on the run itself: no step faulted, one
/// character, the buttons the definition holds, and the named geometry of
/// <see cref="TestWorldGenerator.Features"/> on the character's walk line. A scenario that never reaches
/// its feature fails rather than passing vacuously.
/// </para>
/// <para>
/// Distances are taken in double from the float state. A yaw is a binary angle, 65,536 per turn, with
/// forward (sin ψ, 0, cos ψ). Every geometry these scenarios touch is a vertical face (or level ground),
/// so the distance from the vertical capsule axis to a face's plane is the feet's horizontal distance to
/// it. "Rest height" is <see cref="MovementStepTests.AtRestOn"/>: feet − surface in
/// [−contactTolerance, contactSkin + contactTolerance]. The character "touches" a face when its axis is
/// within capsuleRadius + contactSkin + contactTolerance of the face's plane: a sweep stops the capsule
/// between r + contactSkin and r + 1.1·contactSkin from what it hits (its tolerance is 0.1·contactSkin),
/// so the rest band's upper edge is the bound a touch can be read against.
/// </para>
/// </remarks>
public sealed class MovementScenarioTests
{
    /// <summary>How close a distance must be to its stated value, m.</summary>
    private const double DistanceTolerance = 1e-4;

    /// <summary>How close a speed must be to its stated value, m/s.</summary>
    private const double SpeedTolerance = 1e-4;

    /// <summary>How close a named feature must lie to the geometry the scenario's Given states, m.</summary>
    private const double GivenTolerance = 1e-3;

    /// <summary>The speed along the wall that US1.2 keeps: runSpeed · cos 45° = 7 · cos 45°, m/s.</summary>
    private const double ParallelSpeed = 4.9497476;

    /// <summary>The rise of the stair <c>stairs-climb</c> walks into, below stepHeight (0.35 m), m.</summary>
    private const double ClimbRise = 0.30;

    /// <summary>The rise of the stair <c>stairs-blocked</c> walks into, above stepHeight, m.</summary>
    private const double BlockedRise = 0.50;

    /// <summary>US1.1's rows: the step rate and the distance one second of Forward from rest covers.</summary>
    public static TheoryData<int, double> FlatRuns => new()
    {
        { 20, 6.825 },
        { 30, 6.766667 },
        { 60, 6.708333 },
    };

    /// <summary>US1.3's rows: <c>stairs-climb</c> and <c>stairs-blocked</c>, each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<string, int> Stairs => TestRates.WithRateLast("stairs-climb", "stairs-blocked");

    /// <summary>
    /// US1.1: Given one character at rest, Grounded, on flat terrain, aim yaw 0 (runSpeed 7, groundAccel
    /// 70), When it is stepped hz steps (one simulated second) with Forward held, Then its displacement
    /// along the aim heading is <paramref name="expectedMetres"/> within 1e-4 m and equals
    /// Σₖ₌₁ᴺ min(k·a·dt, v)·dt computed in double within 1e-4 m, its lateral displacement is within 1e-4 m
    /// of 0, every step is Grounded, and the final state's flags have bit 0 (Grounded) set.
    /// </summary>
    [Theory]
    [MemberData(nameof(FlatRuns))]
    public void FlatRunFromRestCoversTheDiscreteSum(int hz, double expectedMetres)
    {
        var scenario = $"flat-run-{hz}hz";
        var run = ScenarioRuns.Run(scenario, hz);
        var label = $"{scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        Assert.True(hz == run.Steps.Count, $"{label}: one simulated second is {hz} steps, but the stream has {run.Steps.Count}");
        var yaw = HeldThroughout(run, Buttons.Forward, label);
        Assert.True(yaw == 0, $"{label}: the aim yaw must be 0, not {yaw}");
        Assert.True(MovementStepTests.IsGrounded(start), $"{label}: the character must start Grounded; flags {start.Flags}");
        Assert.True(
            start.Velocity.X == 0f && start.Velocity.Y == 0f && start.Velocity.Z == 0f,
            $"{label}: the character must start at rest, not with velocity {start.Velocity}");

        var movement = run.Stream.Config.Movement!;
        var discreteSum = DiscreteSum(hz, movement.GroundAccel, movement.RunSpeed);
        var forward = Heading(yaw);
        var last = run.Last.Characters[0];
        var (along, across) = AlongAndAcross(last.Position, start.Position, forward);

        Assert.True(
            System.Math.Abs(along - expectedMetres) <= DistanceTolerance,
            $"{label}: the character moved {along:R} m along its aim heading, not {expectedMetres:R} m within {DistanceTolerance} m; {start.Position} -> {last.Position}");
        Assert.True(
            System.Math.Abs(along - discreteSum) <= DistanceTolerance,
            $"{label}: the character moved {along:R} m along its aim heading, not the discrete sum {discreteSum:R} m within {DistanceTolerance} m");
        Assert.True(
            System.Math.Abs(across) <= DistanceTolerance,
            $"{label}: the character moved {across:R} m across its aim heading, not 0 within {DistanceTolerance} m; {start.Position} -> {last.Position}");
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            Assert.True(
                MovementStepTests.IsGrounded(character),
                $"{label}: step {run.Steps[k].Step} left the character airborne; flags {character.Flags}, position {character.Position}");
        }

        Assert.True(
            ((byte)last.Flags & 1) != 0,
            $"{label}: the final state's flags {(byte)last.Flags} do not have bit 0 (Grounded) set");
    }

    /// <summary>
    /// US1.2: Given <c>wall-slide-45</c> (the character reaches run speed 3 m before the test world's wall,
    /// the mesh <see cref="TestWorldFeatures.WallMeshId"/> names, heading 45° into it, Forward held), Then
    /// at every step the capsule axis is at least capsuleRadius − contactTolerance from the wall plane on
    /// the start's side; first contact (the first step whose axis touches the plane) occurs with at least
    /// hz steps (one simulated second) of the run after it; and from the step after first contact the
    /// horizontal velocity's component along the wall normal is within 1e-4 m/s of 0, its magnitude is
    /// 7 · cos 45° = 4.9497476 within 1e-4 m/s, and the position along the wall increases at every step.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void WallSlideNeverEntersTheWallAndKeepsTheParallelSpeed(int hz)
    {
        const string Scenario = "wall-slide-45";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;
        var forward = Heading(HeldThroughout(run, Buttons.Forward, label));
        var wall = WallPlane(run, start.Position, label);

        var into = -((forward.X * wall.NormalX) + (forward.Z * wall.NormalZ));
        Assert.True(
            System.Math.Abs(into - System.Math.Cos(System.Math.PI / 4.0)) <= DistanceTolerance,
            $"{label}: the heading meets the wall at cos {into:R} into it, not 45° (cos {System.Math.Cos(System.Math.PI / 4.0):R})");

        // The direction along the wall the heading carries the character: the wall's horizontal tangent,
        // turned to point the way the heading goes.
        var (tangentX, tangentZ) = (-wall.NormalZ, wall.NormalX);
        if ((tangentX * forward.X) + (tangentZ * forward.Z) < 0.0)
        {
            (tangentX, tangentZ) = (-tangentX, -tangentZ);
        }

        var nearest = (double)config.CapsuleRadius - config.ContactTolerance;
        var touching = Touching(config);
        var contact = -1;
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var feet = run.Steps[k].Characters[0].Position;
            var distance = wall.Distance(feet);
            Assert.True(
                distance >= nearest,
                $"{label}: step {run.Steps[k].Step} left the capsule axis {distance:R} m from the wall plane, nearer than capsuleRadius − contactTolerance {nearest:R}; feet {feet}");
            if (contact < 0 && distance <= touching)
            {
                contact = k;
            }
        }

        Assert.True(contact >= 0, $"{label}: the character never touched the wall (axis within {touching:R} m of its plane)");
        var after = run.Steps.Count - 1 - contact;
        Assert.True(
            after >= hz,
            $"{label}: first contact is at step {run.Steps[contact].Step}, leaving {after} steps of the run after it, fewer than one simulated second ({hz})");

        for (var k = contact + 1; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            var velocity = character.Velocity;
            var normal = ((double)velocity.X * wall.NormalX) + ((double)velocity.Z * wall.NormalZ);
            Assert.True(
                System.Math.Abs(normal) <= SpeedTolerance,
                $"{label}: step {run.Steps[k].Step}'s horizontal velocity has {normal:R} m/s along the wall normal, not 0 within {SpeedTolerance}; velocity {velocity}");
            var speed = MovementStepTests.HorizontalSpeed(velocity);
            Assert.True(
                System.Math.Abs(speed - ParallelSpeed) <= SpeedTolerance,
                $"{label}: step {run.Steps[k].Step}'s horizontal speed is {speed:R} m/s, not 7 · cos 45° = {ParallelSpeed:R} within {SpeedTolerance}; velocity {velocity}");
            var previous = run.Steps[k - 1].Characters[0].Position;
            var moved = (((double)character.Position.X - previous.X) * tangentX) + (((double)character.Position.Z - previous.Z) * tangentZ);
            Assert.True(
                moved > 0.0,
                $"{label}: step {run.Steps[k].Step} moved {moved:R} m along the wall, not forward along it; {previous} -> {character.Position}");
        }
    }

    /// <summary>
    /// US1.3: Given <c>stairs-climb</c> (Forward held into the 0.30 m stair, below stepHeight 0.35 m),
    /// Then the last step is Grounded with Position.y at rest height on the stair top and the feet past
    /// its front edge by more than capsuleRadius; Given <c>stairs-blocked</c> (the 0.50 m stair), Then
    /// whenever Grounded Position.y is at rest height on the floor, the axis never comes nearer the stair
    /// face than capsuleRadius − contactTolerance, and it does reach the face (touches it at some step),
    /// so the block cannot pass vacuously. The front edge is <see cref="TestWorldFeatures.StairsClimbFrontEdge"/>
    /// or <see cref="TestWorldFeatures.StairsBlockedFrontEdge"/>: the point where the walk line crosses
    /// the stair's front face, at the stair's top height, the face perpendicular to the heading.
    /// </summary>
    [Theory]
    [MemberData(nameof(Stairs))]
    public void StairBelowTheStepHeightIsClimbedAndAboveItIsBlocked(string scenario, int hz)
    {
        var run = ScenarioRuns.Run(scenario, hz);
        var label = $"{scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;
        var climbs = scenario == "stairs-climb";
        var features = TestWorldGenerator.Features;
        var edge = climbs ? features.StairsClimbFrontEdge : features.StairsBlockedFrontEdge;
        var rise = climbs ? ClimbRise : BlockedRise;
        var forward = Heading(HeldThroughout(run, Buttons.Forward, label));

        var (ahead, across) = AlongAndAcross(edge, start.Position, forward);
        Assert.True(
            ahead > 0.0 && System.Math.Abs(across) <= GivenTolerance,
            $"{label}: the front edge {edge} is {ahead:R} m ahead of and {across:R} m across the walk line from {start.Position}; it must lie on it, ahead");
        var floorAtEdge = (double)run.Collision.HeightAt(edge.X, edge.Z);
        Assert.True(
            System.Math.Abs(edge.Y - floorAtEdge - rise) <= GivenTolerance,
            $"{label}: the front edge {edge} is {edge.Y - floorAtEdge:R} m above the floor at {floorAtEdge:R}, not the stair's rise {rise}");

        if (climbs)
        {
            var last = run.Last.Characters[0];
            Assert.True(MovementStepTests.IsGrounded(last), $"{label}: the last step must be Grounded; flags {last.Flags}, position {last.Position}");
            Assert.True(
                MovementStepTests.AtRestOn(last.Position.Y, edge.Y, config),
                $"{label}: the last Position.y {last.Position.Y:R} is not at rest height on the stair top {edge.Y:R}");
            var (past, _) = AlongAndAcross(last.Position, edge, forward);
            Assert.True(
                past > config.CapsuleRadius,
                $"{label}: the feet ended {past:R} m past the stair's front edge {edge}, not more than capsuleRadius {config.CapsuleRadius:R}; position {last.Position}");
            return;
        }

        var nearest = (double)config.CapsuleRadius - config.ContactTolerance;
        var touching = Touching(config);
        var closest = double.PositiveInfinity;
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            var feet = character.Position;
            if (MovementStepTests.IsGrounded(character))
            {
                var floor = run.Collision.HeightAt(feet.X, feet.Z);
                Assert.True(
                    MovementStepTests.AtRestOn(feet.Y, floor, config),
                    $"{label}: step {run.Steps[k].Step} is Grounded with Position.y {feet.Y:R}, not at rest height on the floor {floor:R}; position {feet}");
            }

            var (before, _) = AlongAndAcross(edge, feet, forward);
            Assert.True(
                before >= nearest,
                $"{label}: step {run.Steps[k].Step} left the capsule axis {before:R} m from the stair face, nearer than capsuleRadius − contactTolerance {nearest:R}; position {feet}");
            closest = System.Math.Min(closest, before);
        }

        Assert.True(
            closest <= touching,
            $"{label}: the axis came no nearer the stair face than {closest:R} m, so the character never reached it (touching is within {touching:R} m)");
    }

    /// <summary>
    /// US1.4: Given <c>slope-limit</c> (Forward held up the 30° slope onto the plateau and on into the 50°
    /// slope), Then every step up to the one that reaches the plateau, which covers the walkable slope, is
    /// Grounded, and the character does reach the plateau at rest height on
    /// <see cref="TestWorldFeatures.PlateauHeight"/>; afterwards it is never Grounded with
    /// GroundNormal.y &lt; <c>CosSlopeLimit</c>, and Position.y never exceeds the plateau height +
    /// stepHeight (0.35 m). The run reaches the 50° slope, so the refusal cannot pass vacuously: at some
    /// step after the plateau the terrain 2 · capsuleRadius ahead of the feet along the heading is more than
    /// stepHeight above the plateau.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void SlopeBelowTheLimitIsClimbedAndAboveItIsRefused(int hz)
    {
        const string Scenario = "slope-limit";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;
        var forward = Heading(HeldThroughout(run, Buttons.Forward, label));
        var plateau = TestWorldGenerator.Features.PlateauHeight;

        var reached = -1;
        for (var k = 0; k < run.Steps.Count && reached < 0; k++)
        {
            var character = run.Steps[k].Characters[0];
            Assert.True(
                MovementStepTests.IsGrounded(character),
                $"{label}: step {run.Steps[k].Step} left the character airborne on the way up the walkable slope; position {character.Position}, velocity {character.Velocity}");
            if (MovementStepTests.AtRestOn(character.Position.Y, plateau, config))
            {
                reached = k;
            }
        }

        Assert.True(reached >= 0, $"{label}: the character never reached the plateau at rest height on {plateau:R}; it ended at {run.Last.Characters[0].Position}");

        var ceiling = (double)plateau + config.StepHeight;
        var lookAhead = 2.0 * config.CapsuleRadius;
        var atSteepSlope = false;
        for (var k = reached + 1; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            var feet = character.Position;
            if (MovementStepTests.IsGrounded(character))
            {
                Assert.True(
                    character.GroundNormal.Y >= config.CosSlopeLimit,
                    $"{label}: step {run.Steps[k].Step} left the character Grounded on normal {character.GroundNormal}, steeper than CosSlopeLimit {config.CosSlopeLimit:R}; position {feet}");
            }

            Assert.True(
                feet.Y <= ceiling,
                $"{label}: step {run.Steps[k].Step} put Position.y at {feet.Y:R}, above the plateau height + stepHeight {ceiling:R}; position {feet}");
            var ahead = run.Collision.HeightAt((float)(feet.X + (lookAhead * forward.X)), (float)(feet.Z + (lookAhead * forward.Z)));
            atSteepSlope |= ahead > ceiling;
        }

        Assert.True(
            atSteepSlope,
            $"{label}: after the plateau the terrain {lookAhead:R} m ahead never rose more than stepHeight above it, so the run never reached the 50° slope; it ended at {run.Last.Characters[0].Position}");
    }

    /// <summary>
    /// US1.5: Given <c>jump-land</c> (Jump pressed once while Grounded on flat terrain, then no buttons),
    /// Then the jump step clears Grounded and its velocity.y bits equal jumpSpeed − <c>GravityPerStep</c>
    /// evaluated in float (4.5f − 9.81f / hz as <see cref="CompiledConfig"/> computes it); every later
    /// airborne step's velocity.y equals the previous one minus <c>GravityPerStep</c> exactly, clamped at
    /// −maxFallSpeed; Position.y is never below HeightAt(x, z) − contactTolerance; and the landing step
    /// sets Grounded with Position.y at rest height and velocity.y == 0f.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void JumpLeavesWithJumpSpeedFollowsGravityAndLandsGrounded(int hz)
    {
        const string Scenario = "jump-land";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;

        var jumps = new List<int>();
        for (var k = 0; k < run.Stream.Steps.Count; k++)
        {
            if (run.Stream.Steps[k].Commands.Any(command => (command.Command.Buttons & Buttons.Jump) != 0))
            {
                jumps.Add(k);
            }
        }

        Assert.True(jumps.Count == 1, $"{label}: Jump must be pressed at exactly one step, not at entries [{string.Join(", ", jumps)}]");
        var jump = jumps[0];
        var before = jump == 0 ? run.InitialCharacters[0] : run.Steps[jump - 1].Characters[0];
        Assert.True(MovementStepTests.IsGrounded(before), $"{label}: the character must be Grounded when Jump is pressed; flags {before.Flags}");

        for (var k = 0; k < run.Steps.Count; k++)
        {
            var feet = run.Steps[k].Characters[0].Position;
            var ground = run.Collision.HeightAt(feet.X, feet.Z);
            Assert.True(
                (double)feet.Y >= (double)ground - config.ContactTolerance,
                $"{label}: step {run.Steps[k].Step} put Position.y at {feet.Y:R}, below the terrain {ground:R} by more than contactTolerance; position {feet}");
        }

        var leaving = run.Steps[jump].Characters[0];
        Assert.True(!MovementStepTests.IsGrounded(leaving), $"{label}: the jump step {run.Steps[jump].Step} must clear Grounded; flags {leaving.Flags}");
        var jumpVelocity = config.JumpSpeed - config.GravityPerStep;
        AssertSameBits(jumpVelocity, leaving.Velocity.Y, $"{label}: the jump step {run.Steps[jump].Step}'s velocity.y (jumpSpeed − GravityPerStep)");

        var landing = -1;
        var previous = leaving.Velocity.Y;
        for (var k = jump + 1; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            if (MovementStepTests.IsGrounded(character))
            {
                landing = k;
                break;
            }

            var expected = previous - config.GravityPerStep;
            if (expected < -config.MaxFallSpeed)
            {
                expected = -config.MaxFallSpeed;
            }

            AssertSameBits(expected, character.Velocity.Y, $"{label}: airborne step {run.Steps[k].Step}'s velocity.y (the previous {previous:R} − GravityPerStep, clamped at −maxFallSpeed)");
            previous = character.Velocity.Y;
        }

        Assert.True(landing > jump + 1, $"{label}: the character never landed after an airborne step (landing entry {landing}, jump entry {jump})");
        var landed = run.Steps[landing].Characters[0];
        var floor = run.Collision.HeightAt(landed.Position.X, landed.Position.Z);
        Assert.True(
            MovementStepTests.AtRestOn(landed.Position.Y, floor, config),
            $"{label}: the landing step {run.Steps[landing].Step} left Position.y at {landed.Position.Y:R}, not at rest height on the terrain {floor:R}");
        Assert.True(
            landed.Velocity.Y == 0f,
            $"{label}: the landing step {run.Steps[landing].Step} left velocity.y at {landed.Velocity.Y:R}, not 0");
    }

    /// <summary>
    /// The run's only character, as loaded; fails if the stream holds another number of characters or any
    /// step faulted.
    /// </summary>
    private static CharacterState RequireOneCharacterAndNoFault(ScenarioRun run, string label)
    {
        Assert.True(run.InitialCharacters.Count == 1, $"{label}: the scenario must hold one character, not {run.InitialCharacters.Count}");
        Assert.True(run.Steps.Count > 0, $"{label}: the stream has no steps");
        foreach (var step in run.Steps)
        {
            Assert.True(step.Fault.Code == StepFaultCode.None, $"{label}: step {step.Step} faulted: {step.Fault}");
        }

        return run.InitialCharacters[0];
    }

    /// <summary>
    /// Checks that every stream entry carries one command, for the character, holding exactly
    /// <paramref name="buttons"/> at one aim yaw, and returns that yaw.
    /// </summary>
    private static ushort HeldThroughout(ScenarioRun run, Buttons buttons, string label)
    {
        var steps = run.Stream.Steps;
        Assert.True(steps.Count > 0 && steps[0].Commands.Length == 1, $"{label}: the first entry must carry one command");
        var yaw = steps[0].Commands[0].Command.AimYaw;
        foreach (var entry in steps)
        {
            Assert.True(
                entry.Commands.Length == 1
                    && entry.Commands[0].CharacterIndex == 0
                    && entry.Commands[0].Command.Buttons == buttons
                    && entry.Commands[0].Command.AimYaw == yaw,
                $"{label}: entry {entry.Step} must hold exactly {buttons} at aim yaw {yaw}, not [{string.Join(", ", entry.Commands)}]");
        }

        return yaw;
    }

    /// <summary>A yaw's forward direction in X–Z, in double: (sin ψ, cos ψ) with ψ = yaw · 2π / 65,536.</summary>
    private static (double X, double Z) Heading(ushort yaw)
    {
        var angle = yaw * 2.0 * System.Math.PI / 65536.0;
        return (System.Math.Sin(angle), System.Math.Cos(angle));
    }

    /// <summary>
    /// The horizontal displacement from <paramref name="from"/> to <paramref name="to"/>, in double, split
    /// into its component along <paramref name="forward"/> and its component across it.
    /// </summary>
    private static (double Along, double Across) AlongAndAcross(Vec3 to, Vec3 from, (double X, double Z) forward)
    {
        var (dx, dz) = ((double)to.X - from.X, (double)to.Z - from.Z);
        return ((dx * forward.X) + (dz * forward.Z), (dx * forward.Z) - (dz * forward.X));
    }

    /// <summary>US1.1's discrete sum Σₖ₌₁ᴺ min(k·a·dt, v)·dt over one second, N = hz, in double.</summary>
    private static double DiscreteSum(int hz, double accel, double speed)
    {
        var dt = 1.0 / hz;
        var sum = 0.0;
        for (var k = 1; k <= hz; k++)
        {
            sum += System.Math.Min(k * accel * dt, speed) * dt;
        }

        return sum;
    }

    /// <summary>The distance within which the capsule axis touches a face's plane: capsuleRadius + contactSkin + contactTolerance.</summary>
    private static double Touching(CompiledConfig config) =>
        (double)config.CapsuleRadius + config.ContactSkin + config.ContactTolerance;

    /// <summary>
    /// The plane of the wall <see cref="TestWorldFeatures.WallMeshId"/> names, from its first triangle in
    /// double, with its unit normal turned toward <paramref name="start"/>. The wall must be vertical.
    /// </summary>
    private static WallPlaneInDouble WallPlane(ScenarioRun run, Vec3 start, string label)
    {
        var wallId = TestWorldGenerator.Features.WallMeshId;
        var mesh = run.Collision.Data.Meshes.FirstOrDefault(candidate => candidate.MeshId == wallId);
        Assert.True(mesh is not null && mesh.Indices.Length >= 3, $"{label}: WallMeshId {wallId} names no mesh with a triangle");
        var a = Vertex(mesh!, mesh!.Indices[0]);
        var b = Vertex(mesh, mesh.Indices[1]);
        var c = Vertex(mesh, mesh.Indices[2]);
        var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var (nx, ny, nz) = ((uy * vz) - (uz * vy), (uz * vx) - (ux * vz), (ux * vy) - (uy * vx));
        var length = System.Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        Assert.True(length > 0.0 && System.Math.Abs(ny) <= 1e-9 * length, $"{label}: the wall's first triangle is not vertical: normal ({nx}, {ny}, {nz})");
        var plane = new WallPlaneInDouble(nx / length, nz / length, ((nx * a.X) + (nz * a.Z)) / length);
        return plane.Distance(start) < 0.0 ? new WallPlaneInDouble(-plane.NormalX, -plane.NormalZ, -plane.Offset) : plane;
    }

    private static (double X, double Y, double Z) Vertex(MeshData mesh, uint index) =>
        (mesh.Vertices[index * 3], mesh.Vertices[(index * 3) + 1], mesh.Vertices[(index * 3) + 2]);

    /// <summary>Fails unless <paramref name="actual"/> has exactly <paramref name="expected"/>'s bits.</summary>
    private static void AssertSameBits(float expected, float actual, string what)
    {
        var (expectedBits, actualBits) = (BitConverter.SingleToUInt32Bits(expected), BitConverter.SingleToUInt32Bits(actual));
        Assert.True(
            expectedBits == actualBits,
            $"{what} is {actual:R} (0x{actualBits:x8}), not {expected:R} (0x{expectedBits:x8})");
    }

    /// <summary>A vertical plane n·p = offset with horizontal unit normal (NormalX, 0, NormalZ), in double.</summary>
    private readonly record struct WallPlaneInDouble(double NormalX, double NormalZ, double Offset)
    {
        /// <summary>The signed horizontal distance of <paramref name="point"/> from the plane, positive on the normal's side.</summary>
        public double Distance(Vec3 point) => ((NormalX * point.X) + (NormalZ * point.Z)) - Offset;
    }
}
