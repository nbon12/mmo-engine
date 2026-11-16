using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Movement;
using MmoEngine.SimCore.Tests.Properties;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;
using D3 = MmoEngine.SimCore.Tests.Properties.CapsuleClearanceReference.D3;

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
/// forward (sin ψ, 0, cos ψ). The wall, stair and thin-wall faces are vertical (and the ground level), so
/// the distance from the vertical capsule axis to such a face's plane is the feet's horizontal distance to
/// it; the ledge and the corner's walls are measured from the capsule axis to their triangles with
/// <see cref="CapsuleClearanceReference.SegmentTriangle"/>. "Rest height" is
/// <see cref="MovementStepTests.AtRestOn"/>: feet − surface in
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

    /// <summary>How deep the ledge <c>crouch-under-ledge</c> crosses is along the walk line, m.</summary>
    private const double LedgeDepth = 4.0;

    /// <summary>The height of the ledge's underside above the floor: between crouchHeight 1.2 and standHeight 1.8, m.</summary>
    private const double LedgeClearance = 1.5;

    /// <summary>How many of <c>corner-wedge</c>'s last steps must end at bit-identical positions.</summary>
    private const int SettledSteps = 10;

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
    /// US1.6: Given <c>crouch-under-ledge</c> (from rest, Grounded and Standing, 2 m before the near edge of
    /// the 4 m deep ledge whose underside is 1.5 m above the floor; Crouch and Forward held over [0, 1.5 s),
    /// Forward only over [1.5 s, 4.5 s)), Then the release step (the first without Crouch, at 1.5 s) has
    /// stance 1 (Crouched) and a movement outcome record with bit value 1 (HeadroomBlocked), so the release
    /// happens under the ledge; every step from the release until the headroom clears is Crouched with
    /// HeadroomBlocked; a step after the release and before the run ends has room to stand, and the first
    /// such step has stance 0 (Standing); and the capsule never comes nearer the ledge than
    /// capsuleRadius − contactTolerance.
    /// </summary>
    /// <remarks>
    /// A step has room to stand when its standing segment, the axis part the standing capsule adds
    /// (crouchHeight − r to standHeight − r above the feet), is more than r + contactSkin / 2
    /// (<see cref="CompiledConfig.HeadroomClearance"/>) from the ledge at the position the step starts from:
    /// the previous step's committed position, where sub-step 2 tests the headroom before the step moves
    /// (data-model.md#movement-step). The ledge is every triangle of the mesh that holds the level underside
    /// at <see cref="TestWorldFeatures.LedgeUnderside"/> midway between
    /// <see cref="TestWorldFeatures.LedgeNearEdge"/> and <see cref="TestWorldFeatures.LedgeFarEdge"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void CrouchedUnderALedgeStaysCrouchedUntilThereIsRoomToStand(int hz)
    {
        const string Scenario = "crouch-under-ledge";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;
        var features = TestWorldGenerator.Features;

        // Given: 4.5 simulated seconds, Crouch released after the first 1.5, from rest, Grounded and Standing.
        var release = 3 * hz / 2;
        var forward = Heading(HeldInSpans(run, label, (Buttons.Crouch | Buttons.Forward, release), (Buttons.Forward, (9 * hz / 2) - release)));
        Assert.True(
            MovementStepTests.IsGrounded(start) && (byte)start.Stance == 0,
            $"{label}: the character must start Grounded and Standing; flags {start.Flags}, stance {start.Stance}");
        Assert.True(
            start.Velocity.X == 0f && start.Velocity.Y == 0f && start.Velocity.Z == 0f,
            $"{label}: the character must start at rest, not with velocity {start.Velocity}");

        // Given: the ledge's near edge 2 m ahead on the walk line, its far edge 4 m beyond, its underside 1.5 m up.
        var (near, far) = (features.LedgeNearEdge, features.LedgeFarEdge);
        var (toNear, nearAcross) = AlongAndAcross(near, start.Position, forward);
        Assert.True(
            System.Math.Abs(toNear - 2.0) <= GivenTolerance && System.Math.Abs(nearAcross) <= GivenTolerance,
            $"{label}: LedgeNearEdge {near} is {toNear:R} m ahead of and {nearAcross:R} m across the walk line from {start.Position}, not 2 m ahead on it");
        var (depth, farAcross) = AlongAndAcross(far, near, forward);
        Assert.True(
            System.Math.Abs(depth - LedgeDepth) <= GivenTolerance && System.Math.Abs(farAcross) <= GivenTolerance,
            $"{label}: LedgeFarEdge {far} is {depth:R} m beyond and {farAcross:R} m across from LedgeNearEdge {near}, not {LedgeDepth} m beyond it on the walk line");
        var (midX, midZ) = (((double)near.X + far.X) / 2.0, ((double)near.Z + far.Z) / 2.0);
        var rise = (double)features.LedgeUnderside - run.Collision.HeightAt((float)midX, (float)midZ);
        Assert.True(
            System.Math.Abs(rise - LedgeClearance) <= GivenTolerance,
            $"{label}: LedgeUnderside {features.LedgeUnderside:R} is {rise:R} m above the floor midway under the ledge, not {LedgeClearance} m");
        var ledge = LedgeTriangles(run, features, midX, midZ, label);

        var radius = (double)config.CapsuleRadius;
        var (segmentFrom, segmentTo) = ((double)config.CrouchHeight - radius, (double)config.StandHeight - radius);
        var headroom = (double)config.HeadroomClearance;

        var released = run.Steps[release];
        Assert.True(
            (byte)released.Characters[0].Stance == 1 && ReportsHeadroomBlocked(released, start.Id),
            $"{label}: the release step {released.Step} must stay Crouched (stance 1) with HeadroomBlocked (bit value 1) under the ledge, " +
            $"not stance {released.Characters[0].Stance} with outcomes [{string.Join(", ", released.MovementOutcomes)}]; position {released.Characters[0].Position}");

        // The first step after the release that starts with room to stand.
        var clear = -1;
        for (var k = release + 1; k < run.Steps.Count && clear < 0; k++)
        {
            var from = run.Steps[k - 1].Characters[0].Position;
            if (SegmentDistance(ledge, from, segmentFrom, segmentTo) > headroom)
            {
                clear = k;
            }
        }

        Assert.True(
            clear >= 0,
            $"{label}: no step after the release starts with the standing segment more than {headroom:R} m from the ledge, so the character never had room to stand; it ended at {run.Last.Characters[0].Position}");
        for (var k = release; k < clear; k++)
        {
            var step = run.Steps[k];
            Assert.True(
                (byte)step.Characters[0].Stance == 1 && ReportsHeadroomBlocked(step, start.Id),
                $"{label}: step {step.Step} starts under the ledge without room to stand but is stance {step.Characters[0].Stance} with outcomes [{string.Join(", ", step.MovementOutcomes)}], " +
                $"not Crouched (stance 1) with HeadroomBlocked (bit value 1); position {step.Characters[0].Position}");
        }

        var stood = run.Steps[clear];
        Assert.True(
            (byte)stood.Characters[0].Stance == 0,
            $"{label}: step {stood.Step}, the first to start with room to stand (from {run.Steps[clear - 1].Characters[0].Position}), is stance {stood.Characters[0].Stance}, not Standing (stance 0)");

        var nearest = radius - config.ContactTolerance;
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var character = run.Steps[k].Characters[0];
            var distance = AxisDistance(ledge, character, config);
            Assert.True(
                distance >= nearest,
                $"{label}: step {run.Steps[k].Step} left the capsule axis {distance:R} m from the ledge, nearer than capsuleRadius − contactTolerance {nearest:R}; position {character.Position}, stance {character.Stance}");
        }
    }

    /// <summary>
    /// Edge case "a character wedged in a concave corner never tunnels and never oscillates between two
    /// positions; the slide loop stops within its iteration limit": Given <c>corner-wedge</c> (Forward held
    /// diagonally, at 45° to both walls, into the 90° corner at <see cref="TestWorldFeatures.Corner"/>), Then
    /// no step ends with the capsule axis nearer either wall than capsuleRadius − contactTolerance, the
    /// per-step positions (the start included) contain no A, B, A, B alternation with A ≠ B, the last 10
    /// steps' positions are bit-identical, and every step's <c>Counters.Sweeps</c> ≤
    /// <c>Counters.SubMoves</c> · (2 · maxSlideIterations + 4) + 1. The run must end wedged, its axis touching
    /// both walls, and some step must sweep, so neither clause passes vacuously.
    /// </summary>
    /// <remarks>
    /// The walls are the two vertical planes through the vertical line at <see cref="TestWorldFeatures.Corner"/>
    /// that one mesh's vertical triangles touching that line lie in, at 90° to each other (as
    /// <c>TestWorldGeneratorTests</c> finds the corner); each wall is every vertical triangle of that mesh in
    /// its plane.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void CornerWedgeNeverTunnelsOrOscillates(int hz)
    {
        const string Scenario = "corner-wedge";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;
        var forward = Heading(HeldThroughout(run, Buttons.Forward, label));
        var corner = TestWorldGenerator.Features.Corner;
        var walls = CornerWalls(run, corner, start.Position, label);

        // Given: the heading runs diagonally into both walls, toward the corner ahead.
        var diagonal = System.Math.Cos(System.Math.PI / 4.0);
        foreach (var wall in walls)
        {
            var into = -((forward.X * wall.Plane.NormalX) + (forward.Z * wall.Plane.NormalZ));
            Assert.True(
                System.Math.Abs(into - diagonal) <= GivenTolerance,
                $"{label}: the heading meets the wall with normal ({wall.Plane.NormalX:R}, 0, {wall.Plane.NormalZ:R}) at cos {into:R} into it, not 45° (cos {diagonal:R})");
        }

        var (ahead, _) = AlongAndAcross(corner, start.Position, forward);
        Assert.True(ahead > 0.0, $"{label}: the corner {corner} is {ahead:R} m along the heading from {start.Position}, not ahead of it");
        Assert.True(run.Steps.Count >= SettledSteps, $"{label}: the run has {run.Steps.Count} steps, fewer than the {SettledSteps} that must end bit-identical");

        var nearest = (double)config.CapsuleRadius - config.ContactTolerance;
        var sweepsPerSubMove = (2 * config.MaxSlideIterations) + 4;
        var positions = new List<Vec3>(run.Steps.Count + 1) { start.Position };
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var step = run.Steps[k];
            var character = step.Characters[0];
            for (var w = 0; w < walls.Count; w++)
            {
                var distance = AxisDistance(walls[w].Triangles, character, config);
                Assert.True(
                    distance >= nearest,
                    $"{label}: step {step.Step} left the capsule axis {distance:R} m from wall {w}, nearer than capsuleRadius − contactTolerance {nearest:R}; position {character.Position}");
            }

            Assert.True(
                step.Counters.Sweeps <= (step.Counters.SubMoves * sweepsPerSubMove) + 1,
                $"{label}: step {step.Step} ran {step.Counters.Sweeps} sweeps over {step.Counters.SubMoves} sub-moves, more than SubMoves · (2 · maxSlideIterations + 4) + 1 = {(step.Counters.SubMoves * sweepsPerSubMove) + 1}");
            positions.Add(character.Position);
        }

        Assert.True(run.Steps.Any(step => step.Counters.Sweeps > 0), $"{label}: no step ran a sweep, so the loop bound was never exercised");

        for (var i = 0; i + 3 < positions.Count; i++)
        {
            var (a, b) = (positions[i], positions[i + 1]);
            Assert.True(
                a.Equals(b) || !positions[i + 2].Equals(a) || !positions[i + 3].Equals(b),
                $"{label}: the positions after steps {i} to {i + 3} alternate A, B, A, B with A = {a} and B = {b}");
        }

        var settled = run.Last.Characters[0].Position;
        for (var k = run.Steps.Count - SettledSteps; k < run.Steps.Count; k++)
        {
            var position = run.Steps[k].Characters[0].Position;
            Assert.True(
                position.Equals(settled),
                $"{label}: step {run.Steps[k].Step} ended at {position}, not bit-identical to the last step's {settled}, so the last {SettledSteps} steps did not hold still");
        }

        var touching = Touching(config);
        for (var w = 0; w < walls.Count; w++)
        {
            var distance = AxisDistance(walls[w].Triangles, run.Last.Characters[0], config);
            Assert.True(
                distance <= touching,
                $"{label}: the run ended with the axis {distance:R} m from wall {w}, not touching it (within {touching:R} m), so it never wedged into the corner; position {settled}");
        }
    }

    /// <summary>
    /// Edge case "a step that would move a character further than its capsule radius is subdivided, so no
    /// wall thinner than the movement per step is passed through": Given <c>thin-wall-fast</c> (overrides
    /// runSpeed 20, groundAccel 1000, capsuleRadius 0.25 and maxSubMoves 16, so 20 / hz &gt; 0.25 m per step at
    /// every rate; Forward held into the zero-thickness quad <see cref="TestWorldFeatures.ThinWallMeshId"/>
    /// names), Then every step whose start horizontal speed · Dt exceeds capsuleRadius has
    /// <c>Counters.SubMoves</c> ≥ 2, at least one such step occurs, and the signed distance of the feet to the
    /// thin wall's plane never changes sign. The run must reach the wall (its axis touches the plane at some
    /// step), so the never-passes-through clause cannot pass vacuously.
    /// </summary>
    /// <remarks>
    /// A step's start horizontal speed is the horizontal speed of the previous step's committed velocity (the
    /// initial state's for the first step). The thin wall's plane is taken from the quad in double, with its
    /// normal turned toward the start, so the start's signed distance is positive and every step's must be.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void FastMoveIntoAThinWallIsSubdividedAndNeverPassesThrough(int hz)
    {
        const string Scenario = "thin-wall-fast";
        var run = ScenarioRuns.Run(Scenario, hz);
        var label = $"{Scenario} at {hz} Hz";
        var start = RequireOneCharacterAndNoFault(run, label);
        var config = run.Config;

        var movement = run.Stream.Config.Movement!;
        Assert.True(
            movement.RunSpeed == 20f && movement.GroundAccel == 1000f && movement.CapsuleRadius == 0.25f && movement.MaxSubMoves == 16,
            $"{label}: the scenario must override runSpeed 20, groundAccel 1000, capsuleRadius 0.25 and maxSubMoves 16, not " +
            $"{movement.RunSpeed}, {movement.GroundAccel}, {movement.CapsuleRadius} and {movement.MaxSubMoves}");
        var radius = (double)config.CapsuleRadius;
        var dt = (double)config.Dt;
        Assert.True(
            (double)config.RunSpeed * dt > radius,
            $"{label}: runSpeed · Dt = {(double)config.RunSpeed * dt:R} m is not more than capsuleRadius {radius:R}, so no step needs subdividing");
        var forward = Heading(HeldThroughout(run, Buttons.Forward, label));
        var wall = ThinWall(run, start.Position, forward, label);

        var touching = Touching(config);
        var fastSteps = 0;
        var reached = false;
        var before = start;
        for (var k = 0; k < run.Steps.Count; k++)
        {
            var step = run.Steps[k];
            var character = step.Characters[0];
            var startSpeed = MovementStepTests.HorizontalSpeed(before.Velocity);
            if (startSpeed * dt > radius)
            {
                fastSteps++;
                Assert.True(
                    step.Counters.SubMoves >= 2,
                    $"{label}: step {step.Step} starts at {startSpeed:R} m/s, {startSpeed * dt:R} m per step, more than capsuleRadius {radius:R}, but ran {step.Counters.SubMoves} sub-moves, not at least 2");
            }

            var side = wall.Distance(character.Position);
            Assert.True(
                side > 0.0,
                $"{label}: step {step.Step} put the feet {side:R} m from the thin wall's plane, on the far side from the start's {wall.Distance(start.Position):R} m; {before.Position} -> {character.Position}");
            reached |= side <= touching;
            before = character;
        }

        Assert.True(fastSteps >= 1, $"{label}: no step started faster than capsuleRadius / Dt = {radius / dt:R} m/s, so no step had to be subdivided");
        Assert.True(
            reached,
            $"{label}: the axis never came within {touching:R} m of the thin wall's plane, so the run never reached the wall; it ended at {run.Last.Characters[0].Position}");
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

    /// <summary>
    /// Checks that the stream's entries hold, in order, each span's buttons for its number of entries (and
    /// no more entries than the spans cover), every entry carrying one command, for the character, at one aim
    /// yaw, and returns that yaw.
    /// </summary>
    private static ushort HeldInSpans(ScenarioRun run, string label, params (Buttons Buttons, int Entries)[] spans)
    {
        var steps = run.Stream.Steps;
        var total = spans.Sum(span => span.Entries);
        Assert.True(
            steps.Count == total,
            $"{label}: the stream must have {total} entries ([{string.Join(", ", spans.Select(span => $"{span.Entries} × {span.Buttons}"))}]), not {steps.Count}");
        Assert.True(steps[0].Commands.Length == 1, $"{label}: the first entry must carry one command");
        var yaw = steps[0].Commands[0].Command.AimYaw;
        var k = 0;
        foreach (var (buttons, entries) in spans)
        {
            for (var e = 0; e < entries; e++, k++)
            {
                var entry = steps[k];
                Assert.True(
                    entry.Commands.Length == 1
                        && entry.Commands[0].CharacterIndex == 0
                        && entry.Commands[0].Command.Buttons == buttons
                        && entry.Commands[0].Command.AimYaw == yaw,
                    $"{label}: entry {k} (step {entry.Step}) must hold exactly {buttons} at aim yaw {yaw}, not [{string.Join(", ", entry.Commands)}]");
            }
        }

        return yaw;
    }

    /// <summary>Whether the step holds a movement outcome record for the character with bit value 1 (HeadroomBlocked) set.</summary>
    private static bool ReportsHeadroomBlocked(ScenarioStep step, uint characterId) =>
        step.MovementOutcomes.Any(outcome => outcome.CharacterId == characterId && ((byte)outcome.Flags & 1) != 0);

    /// <summary>
    /// Every kept mesh triangle of the run's world with its mesh's ID, in triangle ID order: mesh triangle IDs
    /// follow the heightfield's in file order, mesh by mesh, and dropped degenerate triangles are skipped.
    /// </summary>
    private static List<(uint MeshId, MeshTriangle Triangle)> MeshTriangles(ScenarioRun run)
    {
        var world = run.Collision;
        var triangles = new List<(uint MeshId, MeshTriangle Triangle)>();
        var id = world.Meshes.FirstId;
        foreach (var mesh in world.Data.Meshes)
        {
            for (var t = 0; t < mesh.Indices.Length / 3; t++, id++)
            {
                if (world.Meshes.TryGetMeshTriangle(id, out var triangle))
                {
                    triangles.Add((mesh.MeshId, triangle));
                }
            }
        }

        return triangles;
    }

    /// <summary>A mesh triangle's vertices in double, exactly.</summary>
    private static (D3 A, D3 B, D3 C) InDouble(MeshTriangle triangle) =>
        (D3.From(triangle.A), D3.From(triangle.B), D3.From(triangle.C));

    /// <summary>
    /// The ledge: every kept triangle of the mesh that holds a level triangle (every vertex within 1e-3 m of
    /// <see cref="TestWorldFeatures.LedgeUnderside"/>) over (<paramref name="x"/>, <paramref name="z"/>), the
    /// point midway between the named edges.
    /// </summary>
    private static List<(D3 A, D3 B, D3 C)> LedgeTriangles(ScenarioRun run, TestWorldFeatures features, double x, double z, string label)
    {
        var underside = (double)features.LedgeUnderside;
        var triangles = MeshTriangles(run);
        bool AtUnderside(Vec3 vertex) => System.Math.Abs(vertex.Y - underside) <= GivenTolerance;
        var holder = triangles.FindIndex(entry =>
            AtUnderside(entry.Triangle.A) && AtUnderside(entry.Triangle.B) && AtUnderside(entry.Triangle.C) && ContainsInXz(InDouble(entry.Triangle), x, z));
        Assert.True(holder >= 0, $"{label}: no mesh holds a level underside at {underside:R} m over ({x:R}, {z:R}), midway under the ledge");
        var ledgeId = triangles[holder].MeshId;
        return triangles.Where(entry => entry.MeshId == ledgeId).Select(entry => InDouble(entry.Triangle)).ToList();
    }

    /// <summary>Whether (x, z) lies inside or on the triangle's projection onto the X–Z plane.</summary>
    private static bool ContainsInXz((D3 A, D3 B, D3 C) triangle, double x, double z)
    {
        var (a, b, c) = triangle;
        var e0 = ((b.X - a.X) * (z - a.Z)) - ((b.Z - a.Z) * (x - a.X));
        var e1 = ((c.X - b.X) * (z - b.Z)) - ((c.Z - b.Z) * (x - b.X));
        var e2 = ((a.X - c.X) * (z - c.Z)) - ((a.Z - c.Z) * (x - c.X));
        return (e0 >= 0.0 && e1 >= 0.0 && e2 >= 0.0) || (e0 <= 0.0 && e1 <= 0.0 && e2 <= 0.0);
    }

    /// <summary>
    /// The least distance, in double, from the vertical segment <paramref name="from"/> to
    /// <paramref name="to"/> above <paramref name="feet"/> to <paramref name="triangles"/>.
    /// </summary>
    private static double SegmentDistance(IReadOnlyList<(D3 A, D3 B, D3 C)> triangles, Vec3 feet, double from, double to)
    {
        var p = new D3(feet.X, feet.Y + from, feet.Z);
        var q = new D3(feet.X, feet.Y + to, feet.Z);
        var best = double.PositiveInfinity;
        foreach (var (a, b, c) in triangles)
        {
            best = System.Math.Min(best, CapsuleClearanceReference.SegmentTriangle(p, q, a, b, c));
        }

        return best;
    }

    /// <summary>
    /// The least distance from the character's capsule axis (capsuleRadius to its stance's height −
    /// capsuleRadius above the feet) to <paramref name="triangles"/>.
    /// </summary>
    private static double AxisDistance(IReadOnlyList<(D3 A, D3 B, D3 C)> triangles, in CharacterState character, CompiledConfig config)
    {
        var radius = (double)config.CapsuleRadius;
        var height = character.Stance == Stance.Crouched ? config.CrouchHeight : config.StandHeight;
        return SegmentDistance(triangles, character.Position, radius, height - radius);
    }

    /// <summary>
    /// The vertical plane of <paramref name="triangle"/> in double, with its unit normal turned toward
    /// <paramref name="toward"/>; fails unless the triangle is vertical.
    /// </summary>
    private static WallPlaneInDouble VerticalPlane((D3 A, D3 B, D3 C) triangle, Vec3 toward, string what)
    {
        var (a, b, c) = triangle;
        var normal = D3.Cross(b - a, c - a);
        var length = System.Math.Sqrt(D3.Dot(normal, normal));
        Assert.True(
            length > 0.0 && System.Math.Abs(normal.Y) <= 1e-9 * length,
            $"{what} is not vertical: normal ({normal.X:R}, {normal.Y:R}, {normal.Z:R})");
        var horizontal = System.Math.Sqrt((normal.X * normal.X) + (normal.Z * normal.Z));
        var plane = new WallPlaneInDouble(normal.X / horizontal, normal.Z / horizontal, ((normal.X * a.X) + (normal.Z * a.Z)) / horizontal);
        return plane.Distance(toward) < 0.0 ? new WallPlaneInDouble(-plane.NormalX, -plane.NormalZ, -plane.Offset) : plane;
    }

    /// <summary>Whether every vertex of the triangle is within 1e-3 m of the plane.</summary>
    private static bool LiesIn((D3 A, D3 B, D3 C) triangle, WallPlaneInDouble plane) =>
        System.Math.Abs(plane.Distance(triangle.A)) <= GivenTolerance
        && System.Math.Abs(plane.Distance(triangle.B)) <= GivenTolerance
        && System.Math.Abs(plane.Distance(triangle.C)) <= GivenTolerance;

    /// <summary>Whether the triangle's normal has no vertical component, in double.</summary>
    private static bool IsVertical((D3 A, D3 B, D3 C) triangle)
    {
        var normal = D3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
        var length = System.Math.Sqrt(D3.Dot(normal, normal));
        return length > 0.0 && System.Math.Abs(normal.Y) <= 1e-9 * length;
    }

    /// <summary>
    /// The corner's two walls: the first mesh (in triangle ID order) whose vertical triangles touching the
    /// vertical line through <paramref name="corner"/> (a vertex within 1e-3 m of it horizontally) lie in two
    /// planes through that line at 90° (|n₁·n₂| ≤ 1e-4); each wall is its plane, turned toward
    /// <paramref name="start"/>, and every vertical triangle of that mesh lying in it.
    /// </summary>
    private static List<CornerWall> CornerWalls(ScenarioRun run, Vec3 corner, Vec3 start, string label)
    {
        bool OnCornerLine(D3 vertex) => System.Math.Sqrt(((vertex.X - corner.X) * (vertex.X - corner.X)) + ((vertex.Z - corner.Z) * (vertex.Z - corner.Z))) <= GivenTolerance;
        var vertical = MeshTriangles(run)
            .Select(entry => (entry.MeshId, Triangle: InDouble(entry.Triangle)))
            .Where(entry => IsVertical(entry.Triangle))
            .ToList();
        foreach (var mesh in vertical.Select(entry => entry.MeshId).Distinct())
        {
            var planes = vertical
                .Where(entry => entry.MeshId == mesh && (OnCornerLine(entry.Triangle.A) || OnCornerLine(entry.Triangle.B) || OnCornerLine(entry.Triangle.C)))
                .Select(entry => VerticalPlane(entry.Triangle, start, $"{label}: a corner triangle of mesh {mesh}"))
                .Where(plane => System.Math.Abs(plane.Distance(corner)) <= GivenTolerance)
                .ToList();
            foreach (var first in planes)
            {
                var second = planes.FirstOrDefault(other => System.Math.Abs((first.NormalX * other.NormalX) + (first.NormalZ * other.NormalZ)) <= 1e-4);
                if (second == default)
                {
                    continue;
                }

                return new[] { first, second }
                    .Select(plane => new CornerWall(
                        plane,
                        vertical.Where(entry => entry.MeshId == mesh && LiesIn(entry.Triangle, plane)).Select(entry => entry.Triangle).ToList()))
                    .ToList();
            }
        }

        Assert.Fail($"{label}: no mesh has two vertical walls meeting at 90 degrees along the vertical line through Corner {corner}");
        return [];
    }

    /// <summary>
    /// The plane of the thin wall <see cref="TestWorldFeatures.ThinWallMeshId"/> names, turned toward
    /// <paramref name="start"/>. The Given: the mesh is a single zero-thickness quad (exactly two kept
    /// triangles, vertical and coplanar within 1e-4 m, sharing an edge), the heading runs into it, and the
    /// walk line crosses the plane, ahead of the start, between the quad's vertical edges.
    /// </summary>
    private static WallPlaneInDouble ThinWall(ScenarioRun run, Vec3 start, (double X, double Z) forward, string label)
    {
        var id = TestWorldGenerator.Features.ThinWallMeshId;
        var quad = MeshTriangles(run).Where(entry => entry.MeshId == id).Select(entry => entry.Triangle).ToList();
        var mesh = run.Collision.Data.Meshes.FirstOrDefault(candidate => candidate.MeshId == id);
        Assert.True(
            mesh is not null && mesh.Indices.Length == 6 && quad.Count == 2,
            $"{label}: ThinWallMeshId {id} must name a mesh of exactly two kept triangles, not {mesh?.Indices.Length / 3} triangles with {quad.Count} kept");
        var plane = VerticalPlane(InDouble(quad[0]), start, $"{label}: the thin wall's first triangle");
        var vertices = new[] { quad[0].A, quad[0].B, quad[0].C, quad[1].A, quad[1].B, quad[1].C };
        Assert.True(
            vertices.All(vertex => System.Math.Abs(plane.Distance(vertex)) <= DistanceTolerance),
            $"{label}: the thin wall's triangles are not coplanar: [{string.Join(", ", vertices)}]");
        var shared = new[] { quad[0].A, quad[0].B, quad[0].C }.Count(vertex => vertex.Equals(quad[1].A) || vertex.Equals(quad[1].B) || vertex.Equals(quad[1].C));
        Assert.True(shared == 2, $"{label}: the thin wall's triangles share {shared} vertices, not an edge");

        var into = -((forward.X * plane.NormalX) + (forward.Z * plane.NormalZ));
        Assert.True(into > 0.0, $"{label}: the heading ({forward.X:R}, {forward.Z:R}) does not run into the thin wall (cos {into:R})");
        var reach = plane.Distance(start) / into;
        var (tangentX, tangentZ) = (-plane.NormalZ, plane.NormalX);
        var crossing = ((start.X + (forward.X * reach)) * tangentX) + ((start.Z + (forward.Z * reach)) * tangentZ);
        var along = vertices.Select(vertex => (vertex.X * tangentX) + ((double)vertex.Z * tangentZ)).ToList();
        Assert.True(
            crossing >= along.Min() && crossing <= along.Max(),
            $"{label}: the walk line from {start} crosses the thin wall's plane {reach:R} m ahead at {crossing:R} along it, outside the quad's [{along.Min():R}, {along.Max():R}]");
        return plane;
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

        /// <summary>The signed horizontal distance of a point in double from the plane, positive on the normal's side.</summary>
        public double Distance(D3 point) => ((NormalX * point.X) + (NormalZ * point.Z)) - Offset;
    }

    /// <summary>One wall of the corner: its vertical plane, turned toward the start, and its triangles in double.</summary>
    private sealed record CornerWall(WallPlaneInDouble Plane, List<(D3 A, D3 B, D3 C)> Triangles);
}
