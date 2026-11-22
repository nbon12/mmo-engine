using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.World;

/// <summary>
/// Stage 5 of the world step (data-model.md#world-step-order): after stage 4 has posed the hitboxes from the
/// post-movement characters, every projectile, the committed ones carried forward and then the step's new
/// ones, is stepped in ascending ID. A projectile that hits or expires leaves the state in that step with
/// one hit or expiry event, the rest stay in ascending ID with Position = p(k + 1), a projectile fired in
/// the step is swept from its origin in that same step, and <c>nextProjectileId</c> is stage 3's alone.
/// Every world here is on <see cref="ProjectileWorlds.Flat"/>, every test runs at 20, 30 and 60 Hz, and
/// every hit a test expects is first predicted by <see cref="ProjectileReference"/>, so no test can pass by
/// comparing two missing hits. <c>System.Math</c> is written in full, because <c>MmoEngine.SimCore.Math</c>
/// shadows it.
/// </summary>
public sealed class ProjectileStageTests
{
    private const uint ShooterId = 1;

    private const uint TargetId = 2;

    /// <summary>The target's index in the arranged states: characters are in ascending ID, after the shooter.</summary>
    private const int TargetIndex = 1;

    private const int SmallArms = 0;

    /// <summary>Yaw 32768 faces −Z: a target at +Z faces a shooter on its −Z side.</summary>
    private const ushort FacingShooter = 32768;

    /// <summary>
    /// The <c>maxRangeM</c> of <see cref="HitAndExpiredProjectilesAreRemovedAndTheRestKeepTheirOrder"/>: the
    /// rule table's maximum, so the 45° shot reaches its Lifetime limit before its range (as T191's Lifetime
    /// rows do).
    /// </summary>
    private const float ExtendedRange = 10000f;

    /// <summary>The coordinate bound of data-model.md#projectile-step step 6.</summary>
    private const float CoordinateLimit = 8192f;

    /// <summary>
    /// <see cref="HitAndExpiredProjectilesAreRemovedAndTheRestKeepTheirOrder"/>'s arranged step: late enough
    /// that a projectile launched <c>LifetimeSteps − 1</c> steps before the next step (179 at 60 Hz) was
    /// launched at step 0 or later.
    /// </summary>
    private const uint LateStep = 200;

    /// <summary>The projectile that flies on through the step.</summary>
    private const uint FlyingId = 4;

    /// <summary>The projectile that hits the target in the step.</summary>
    private const uint HittingId = 7;

    /// <summary>The projectile that passes its lifetime in the step.</summary>
    private const uint ExpiringId = 9;

    /// <summary>The arranged <c>nextProjectileId</c>, one more than the largest live ID.</summary>
    private const uint NextId = 10;

    /// <summary><see cref="NewProjectilesAreSweptInTheStepTheyAreFired"/>'s projectile already in flight.</summary>
    private const uint OlderId = 3;

    /// <summary>The arranged step of the tests whose projectiles are launched at it, so the next step sweeps k = 1.</summary>
    private const uint ArrangedStep = 1;

    /// <summary>The first segment the world sweeps for a projectile launched at the arranged step.</summary>
    private const uint FirstSweptK = 1;

    /// <summary>The bound on a hit point's distance from the capsule surface it is reported on.</summary>
    private const double SurfaceTolerance = 1e-4;

    /// <summary>The bound on a hit point's distance from the point the reference predicts.</summary>
    private const double PointTolerance = 1e-3;

    /// <summary>
    /// How far the hit point must be from the pre-movement torso's surface, and how far the pre-movement
    /// prediction's point from the post-movement one, so the two poses are told apart.
    /// </summary>
    private const double PoseSeparation = 1e-2;

    /// <summary>The least distance the running target covers in its step (7 m/s for 1/60 s is 0.117 m).</summary>
    private const double LeastRunDistance = 0.1;

    /// <summary><see cref="HitAndExpiredProjectilesAreRemovedAndTheRestKeepTheirOrder"/>'s shooter: 50 m from the target.</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, -25f);

    /// <summary>The target of every test that has one, before it moves.</summary>
    private static readonly Vec3 TargetFeet = new(0f, 0f, 25f);

    /// <summary><see cref="NewProjectilesAreSweptInTheStepTheyAreFired"/>'s shooter, at the origin facing +Z.</summary>
    private static readonly Vec3 OriginFeet = new(0f, 0f, 0f);

    /// <summary>
    /// Given a state at step 200 (with class 0's <c>maxRangeM</c> 10000) holding a shooter (ID 1) standing at
    /// (0, 0, −25), a target (ID 2) standing at (0, 0, 25) facing it, <c>nextProjectileId</c> 10, and three
    /// class-0 projectiles owned by the shooter, from its eye at 600 m/s:
    /// <list type="bullet">
    /// <item>4, launched at step 200 heading −Z 10° up, which meets nothing and passes no limit on segment 1;</item>
    /// <item>7, aimed at the target's torso centre (<see cref="ProjectileWorlds.AimWithDrop"/>) and launched so
    /// that step 201 sweeps the segment the reference meets the torso on;</item>
    /// <item>9, heading +X 45° up and launched <c>LifetimeSteps − 1</c> steps before step 201, so step 201 sweeps
    /// its last segment, on which it meets nothing and passes neither its range nor ±8192 m.</item>
    /// </list>
    /// When <c>SimWorld.Step</c> runs step 201, Then the committed projectiles are exactly [4], with its launch
    /// fields unchanged and Position = <c>Ballistics.PositionAt(2)</c> to the bit; <c>events.hits</c> holds
    /// projectile 7 alone, on the target's torso; <c>events.expiries</c> is exactly [{9, Lifetime}];
    /// <c>nextProjectileId</c> is still 10; reading the state bytes back gives those projectile and event lists;
    /// and the bytes from the projectile records to the end, with the header's <c>projectileCount</c> and
    /// <c>nextProjectileId</c>, are exactly the ones <see cref="TestStates.Build"/> writes for that projectile
    /// and those events.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void HitAndExpiredProjectilesAreRemovedAndTheRestKeepTheirOrder(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.ProjectileClasses![SmallArms]!.MaxRangeM = ExtendedRange;
        }));
        var step = LateStep + 1u;
        var halfG = config.HalfG[SmallArms];
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var lifetime = (uint)config.LifetimeSteps[SmallArms];
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingShooter, Stance.Standing);
        CharacterRecord[] characters = [shooter, target];
        var eye = ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);

        var flying = ProjectileWorlds.InFlight(
            FlyingId, ShooterId, SmallArms, eye, ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(180.0, 10.0), speed), LateStep, LateStep, config);
        var flyingK = step - flying.LaunchStep;
        var flyingPrediction = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, flying, characters, flyingK);
        Assert.True(
            flyingPrediction is null || flyingPrediction.Step > step,
            FormattableString.Invariant($"{context}: projectile {FlyingId} must meet nothing in step {step}, but the reference meets {flyingPrediction}"));
        AssertLimit(config, flying, flyingK, null, $"{context}, projectile {FlyingId}");

        var torso = ProjectileReference.LayoutCapsules(Stance.Standing, TargetFeet, FacingShooter, config)[(int)HitboxPart.Torso];
        var aimed = ProjectileWorlds.LaunchVelocity(ProjectileWorlds.AimWithDrop(eye, torso.Centre, speed, halfG), speed);
        var entry = ProjectileReference.PredictFirstHit(
            ProjectileWorlds.Flat(), config, ProjectileWorlds.InFlight(HittingId, ShooterId, SmallArms, eye, aimed, 0u, 0u, config), characters);
        Assert.True(
            entry is { Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso } && entry.K >= 1u,
            FormattableString.Invariant($"{context}: the aimed shot must meet target {TargetId}'s torso on a segment after the first, not {entry}"));
        var hitting = ProjectileWorlds.InFlight(HittingId, ShooterId, SmallArms, eye, aimed, step - entry.K, LateStep, config);
        var hittingPrediction = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, hitting, characters, entry.K);
        Assert.True(
            hittingPrediction is { Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso } && hittingPrediction.Step == step,
            FormattableString.Invariant($"{context}: projectile {HittingId} must meet target {TargetId}'s torso in step {step}, not {hittingPrediction}"));

        var expiring = ProjectileWorlds.InFlight(
            ExpiringId, ShooterId, SmallArms, eye, ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(90.0, 45.0), speed), step - (lifetime - 1u), LateStep, config);
        var expiringK = step - expiring.LaunchStep;
        var expiringPrediction = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, expiring, characters, expiringK);
        Assert.True(
            expiringPrediction is null,
            FormattableString.Invariant($"{context}: projectile {ExpiringId} must meet nothing on its last segment {expiringK}, but the reference meets {expiringPrediction}"));
        AssertLimit(config, expiring, expiringK, ExpiryReason.Lifetime, $"{context}, projectile {ExpiringId}");

        var state = TestStates.Build(LateStep, characters, [flying, hitting, expiring], NextId);
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);

        var report = world.Step(ReadOnlySpan<CharacterCommand>.Empty);

        var at = FormattableString.Invariant($"{context}, step {step}");
        Assert.True(report.Fault.Code == StepFaultCode.None, $"{at}: faulted with {report.Fault.Code} at '{report.Fault.Path}'");
        Assert.True(report.Step == step, FormattableString.Invariant($"{at}: the report is for step {report.Step}"));
        Assert.True(report.Events.Shots.IsEmpty, $"{at}: the step reported shots [{string.Join(", ", report.Events.Shots.ToArray())}]; nobody fired");
        var committed = StateSerializer.ReadCommitted(report.State);
        var hits = report.Events.Hits.ToArray();
        var expiries = report.Events.Expiries.ToArray();

        var launch = ProjectileWorlds.ToProjectile(flying);
        var expectedFlying = launch with { Position = Ballistics.PositionAt(in launch, flyingK + 1u, halfG, hz) };
        Assert.True(
            committed.Projectiles.Select(p => p.Id).SequenceEqual([FlyingId]),
            $"{at}: the committed projectiles are [{string.Join(", ", committed.Projectiles.Select(p => p.Id))}]; expected [{FlyingId}]");
        Assert.True(
            committed.Projectiles[0] == expectedFlying,
            FormattableString.Invariant(
                $"{at}: projectile {FlyingId} is {committed.Projectiles[0]} (position bits {Bits(committed.Projectiles[0].Position)}); expected {expectedFlying} (position bits {Bits(expectedFlying.Position)}, PositionAt({flyingK + 1u}))"));
        Assert.True(
            hits.Length == 1 && hits[0].ProjectileId == HittingId && hits[0].Kind == HitKind.Character && hits[0].TargetId == TargetId && hits[0].Hitbox == HitboxPart.Torso,
            $"{at}: the step reported hits [{string.Join(", ", hits)}]; expected exactly projectile {HittingId} on Character {TargetId}, Torso");
        Assert.True(
            expiries.SequenceEqual([new ExpiryEvent { ProjectileId = ExpiringId, Reason = ExpiryReason.Lifetime }]),
            $"{at}: the step reported expiries [{string.Join(", ", expiries)}]; expected exactly [{{ ProjectileId = {ExpiringId}, Reason = Lifetime }}]");
        Assert.True(
            committed.NextProjectileId == NextId,
            FormattableString.Invariant($"{at}: nextProjectileId is {committed.NextProjectileId}; stage 5 must leave it at {NextId}"));
        Assert.True(
            committed.Hits.SequenceEqual(hits) && committed.Expiries.SequenceEqual(expiries),
            $"{at}: the state bytes hold hits [{string.Join(", ", committed.Hits)}] and expiries [{string.Join(", ", committed.Expiries)}]; the report's events are [{string.Join(", ", hits)}] and [{string.Join(", ", expiries)}]");

        var hit = hits[0];
        var expected = TestStates.Build(
            step,
            [],
            [ProjectileWorlds.ToRecord(expectedFlying)],
            NextId,
            new EventRecords
            {
                Hits = [new HitRecord(hit.ProjectileId, (byte)hit.Kind, hit.TargetId, (byte)hit.Hitbox, (hit.Point.X, hit.Point.Y, hit.Point.Z), hit.T)],
                Expiries = [new SmallEventRecord(ExpiringId, (byte)ExpiryReason.Lifetime)],
            });
        var bytes = report.State.ToArray();
        var tail = bytes.AsSpan(TestStates.ProjectileOffset(characters.Length, 0));
        Assert.True(
            bytes.AsSpan(6, 6).SequenceEqual(expected.AsSpan(6, 6)) && tail.SequenceEqual(expected.AsSpan(TestStates.HeaderLength)),
            $"{at}: the state's projectileCount and nextProjectileId bytes [{Convert.ToHexString(bytes, 6, 6)}] and its bytes from the projectiles on [{Convert.ToHexString(tail)}] must be [{Convert.ToHexString(expected, 6, 6)}] and [{Convert.ToHexString(expected.AsSpan(TestStates.HeaderLength))}]");
    }

    /// <summary>
    /// Given a shooter (ID 1) standing at the origin facing +Z with the rifle (slot 0) Ready, in a state at
    /// step 1 that already holds projectile 3 (class 0, owned by the shooter, launched at step 1 heading −Z 10°
    /// up, meeting nothing on segment 1) and so <c>nextProjectileId</c> 4, When the shooter presses Fire in
    /// step 2, Then step 2 reports one shot by the shooter from slot 0 with projectile 4, whose launch state
    /// meets nothing on segment 0; the committed projectiles are [3, 4] in ascending ID; projectile 3 is at
    /// <c>Ballistics.PositionAt(2)</c>; projectile 4 has LaunchStep 2, OwnerId 1, class 0 and the shooter's eye
    /// as Origin, and was swept in the step it was fired, so its Position is <c>PositionAt(1)</c> from its own
    /// Origin and LaunchVelocity, to the bit; there is no hit or expiry; and <c>nextProjectileId</c> is 5.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void NewProjectilesAreSweptInTheStepTheyAreFired(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var halfG = config.HalfG[SmallArms];
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var shooter = ProjectileWorlds.Shooter(ShooterId, OriginFeet, yaw: 0, Stance.Standing);
        CharacterRecord[] characters = [shooter];
        var eye = ProjectileWorlds.Eye(OriginFeet, config, Stance.Standing);
        var older = ProjectileWorlds.InFlight(
            OlderId, ShooterId, SmallArms, eye, ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(180.0, 10.0), speed), ArrangedStep, ArrangedStep, config);
        var olderPrediction = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, older, characters, FirstSweptK);
        Assert.True(
            olderPrediction is null || olderPrediction.Step > ArrangedStep + FirstSweptK,
            FormattableString.Invariant($"{context}: projectile {OlderId} must meet nothing on segment {FirstSweptK}, but the reference meets {olderPrediction}"));
        var state = TestStates.Build(ArrangedStep, characters, [older]);
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var step = ArrangedStep + 1u;
        CharacterCommand[] fire = [new(0, new InputCommand(1u, step, Buttons.Fire, 0, 0, 0))];

        var report = world.Step(fire);

        var at = FormattableString.Invariant($"{context}, step {step}");
        Assert.True(report.Fault.Code == StepFaultCode.None, $"{at}: faulted with {report.Fault.Code} at '{report.Fault.Path}'");
        var shots = report.Events.Shots.ToArray();
        Assert.True(
            shots.Length == 1 && shots[0].CharacterId == ShooterId && shots[0].Slot == 0 && shots[0].ProjectileId == OlderId + 1u,
            $"{at}: the step reported shots [{string.Join(", ", shots)}]; expected one, by character {ShooterId} from slot 0, with projectile {OlderId + 1u}");
        var newId = shots[0].ProjectileId;
        var launched = new ReferenceProjectile(ShooterId, SmallArms, eye, ProjectileWorlds.LaunchVelocity(shots[0].Direction, speed), step);
        var newPrediction = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, launched, characters);
        Assert.True(
            newPrediction is null || newPrediction.Step > step,
            FormattableString.Invariant($"{at}: the new projectile must meet nothing on segment 0, but the reference meets {newPrediction}"));

        var committed = StateSerializer.ReadCommitted(report.State);
        Assert.True(
            committed.Projectiles.Select(p => p.Id).SequenceEqual([OlderId, newId]),
            $"{at}: the committed projectiles are [{string.Join(", ", committed.Projectiles.Select(p => p.Id))}]; expected [{OlderId}, {newId}] in ascending ID");
        var olderLaunch = ProjectileWorlds.ToProjectile(older);
        var olderExpected = olderLaunch with { Position = Ballistics.PositionAt(in olderLaunch, (step - ArrangedStep) + 1u, halfG, hz) };
        Assert.True(
            committed.Projectiles[0] == olderExpected,
            $"{at}: projectile {OlderId} is {committed.Projectiles[0]} (position bits {Bits(committed.Projectiles[0].Position)}); expected {olderExpected} (position bits {Bits(olderExpected.Position)})");
        var spawned = committed.Projectiles[1];
        Assert.True(
            spawned.LaunchStep == step && spawned.OwnerId == ShooterId && spawned.Class == SmallArms && Bits(spawned.Origin) == Bits(eye),
            $"{at}: the new projectile is {spawned}; expected LaunchStep {step}, OwnerId {ShooterId}, class {SmallArms} and Origin {eye} (bits {Bits(eye)})");
        var sweptOnce = Ballistics.PositionAt(in spawned, 1u, halfG, hz);
        Assert.True(
            Bits(spawned.Position) == Bits(sweptOnce),
            $"{at}: the new projectile's Position is {spawned.Position} (bits {Bits(spawned.Position)}); swept in the step it was fired it must be PositionAt(1) {sweptOnce} (bits {Bits(sweptOnce)})");
        Assert.True(report.Events.Hits.IsEmpty, $"{at}: the step reported hits [{string.Join(", ", report.Events.Hits.ToArray())}]; expected none");
        Assert.True(report.Events.Expiries.IsEmpty, $"{at}: the step reported expiries [{string.Join(", ", report.Events.Expiries.ToArray())}]; expected none");
        Assert.True(
            committed.NextProjectileId == newId + 1u,
            FormattableString.Invariant($"{at}: nextProjectileId is {committed.NextProjectileId}; expected {newId + 1u}, one past the shot's projectile"));
    }

    /// <summary>
    /// Given a target (ID 2) standing at (0, 0, 25) facing −Z and already running across the line along +X at
    /// <c>runSpeed</c> (7 m/s), holding Right, and a shooter (ID 1) standing 1.5 segments (900/hz m) down the
    /// line, the target's post-movement pose is first found by stepping a world with the same characters and
    /// no projectile once: the target moves at least 0.1 m. A class-0 projectile in flight at step 1 from the
    /// shooter's eye is aimed at the post-movement torso centre, and the reference, posing the target where it
    /// ends the step, meets that torso on segment 1, while posing it where it starts the step it meets the
    /// torso at a point at least 1 cm away, or nowhere in that step. When <c>SimWorld.Step</c> runs step 2,
    /// Then the target ends the step where the projectile-free world put it, the step reports exactly one hit,
    /// on the target's torso, within 1e-3 m of the post-movement prediction, the hit point lies on the torso
    /// <see cref="HitboxBuilder"/> poses from the post-movement pose (its distance to that axis equals the radius
    /// within 1e-4 m) and at least 1 cm off the torso it poses from the pre-movement pose, and the projectile is
    /// removed.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void HitboxesComeFromThePostMovementPose(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var shooterFeet = new Vec3(0f, 0f, TargetFeet.Z - (1.5f * speed / hz));
        var shooter = ProjectileWorlds.Shooter(ShooterId, shooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingShooter, Stance.Standing) with { Velocity = (config.RunSpeed, 0f, 0f) };
        CharacterRecord[] characters = [shooter, target];
        var step = ArrangedStep + 1u;
        CharacterCommand[] runRight = [new(TargetIndex, new InputCommand(1u, step, Buttons.Right, FacingShooter, 0, 0))];

        var probe = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(ArrangedStep, characters));
        var probeReport = probe.Step(runRight);
        Assert.True(
            probeReport.Fault.Code == StepFaultCode.None,
            $"{context}: the projectile-free step faulted with {probeReport.Fault.Code} at '{probeReport.Fault.Path}'");
        var afterMoving = StateSerializer.ReadCommitted(probeReport.State).Characters.ToArray();
        var moved = afterMoving.Single(c => c.Id == TargetId);
        var ran = System.Math.Sqrt(
            System.Math.Pow((double)moved.Position.X - TargetFeet.X, 2.0) + System.Math.Pow((double)moved.Position.Z - TargetFeet.Z, 2.0));
        Assert.True(
            ran >= LeastRunDistance && moved.AimYaw == FacingShooter && moved.Stance == Stance.Standing,
            FormattableString.Invariant(
                $"{context}: the running target ends the step at {moved.Position} yaw {moved.AimYaw} {moved.Stance}, {ran:R} m from where it started; it must run at least {LeastRunDistance:R} m and keep yaw {FacingShooter}, standing"));

        var layout = config.HitboxLayouts[(int)Stance.Standing];
        var postTorso = ProjectileReference.LayoutCapsules(Stance.Standing, moved.Position, moved.AimYaw, config)[(int)HitboxPart.Torso];
        var eye = ProjectileWorlds.Eye(shooterFeet, config, Stance.Standing);
        var direction = ProjectileWorlds.AimWithDrop(eye, postTorso.Centre, speed, config.HalfG[SmallArms]);
        var projectile = ProjectileWorlds.InFlight(1u, ShooterId, SmallArms, eye, ProjectileWorlds.LaunchVelocity(direction, speed), ArrangedStep, ArrangedStep, config);
        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, afterMoving, FirstSweptK);
        Assert.True(
            predicted is { K: FirstSweptK, Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso } && predicted.Step == step,
            FormattableString.Invariant($"{context}: posed where it ends the step, the target's torso must be met on segment {FirstSweptK} (step {step}), not {predicted}"));
        var unmoved = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            unmoved is null || unmoved.Step != step || unmoved.Hitbox != HitboxPart.Torso || DVec3.Distance(unmoved.Point, predicted.Point) >= PoseSeparation,
            FormattableString.Invariant($"{context}: posed where it starts the step, the target must be met elsewhere, not at {unmoved?.Point} against the post-movement point {predicted.Point}"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(ArrangedStep, characters, [projectile]));

        var report = world.Step(runRight);

        var at = FormattableString.Invariant($"{context}, step {step}");
        Assert.True(report.Fault.Code == StepFaultCode.None, $"{at}: faulted with {report.Fault.Code} at '{report.Fault.Path}'");
        var committed = StateSerializer.ReadCommitted(report.State);
        var committedTarget = committed.Characters.Single(c => c.Id == TargetId);
        Assert.True(
            Bits(committedTarget.Position) == Bits(moved.Position),
            $"{at}: the target ends the step at {committedTarget.Position} (bits {Bits(committedTarget.Position)}); the projectile-free step put it at {moved.Position} (bits {Bits(moved.Position)})");
        var hits = report.Events.Hits.ToArray();
        Assert.True(
            hits.Length == 1 && hits[0].ProjectileId == projectile.Id && hits[0].Kind == HitKind.Character && hits[0].TargetId == TargetId && hits[0].Hitbox == HitboxPart.Torso,
            $"{at}: the step reported hits [{string.Join(", ", hits)}]; expected exactly projectile {projectile.Id} on Character {TargetId}, Torso");
        var hit = hits[0];
        var fromPrediction = DVec3.Distance(hit.Point, predicted.Point);
        Assert.True(
            fromPrediction <= PointTolerance,
            FormattableString.Invariant($"{at}: the hit point {hit.Point} is {fromPrediction:R} m from the post-movement prediction {predicted.Point}"));

        var post = new Hitbox[HitboxLayout.CapsuleCount];
        HitboxBuilder.Build(layout, moved.Position, moved.AimYaw, post);
        var pre = new Hitbox[HitboxLayout.CapsuleCount];
        HitboxBuilder.Build(layout, TargetFeet, FacingShooter, pre);
        var postTorsoBox = post[(int)HitboxPart.Torso];
        var preTorsoBox = pre[(int)HitboxPart.Torso];
        var offPost = System.Math.Abs(ProjectileReference.DistanceToSegment(hit.Point, postTorsoBox.A, postTorsoBox.B) - postTorsoBox.Radius);
        var offPre = System.Math.Abs(ProjectileReference.DistanceToSegment(hit.Point, preTorsoBox.A, preTorsoBox.B) - preTorsoBox.Radius);
        Assert.True(
            offPost <= SurfaceTolerance,
            FormattableString.Invariant($"{at}: the hit point {hit.Point} is {offPost:R} m off the post-movement torso's surface (axis {postTorsoBox.A} – {postTorsoBox.B})"));
        Assert.True(
            offPre >= PoseSeparation,
            FormattableString.Invariant($"{at}: the hit point {hit.Point} is only {offPre:R} m off the pre-movement torso's surface (axis {preTorsoBox.A} – {preTorsoBox.B}); the hitboxes must come from the post-movement pose"));
        Assert.True(
            committed.Projectiles.Count == 0,
            $"{at}: the committed state still holds projectiles [{string.Join(", ", committed.Projectiles)}]");
    }

    /// <summary>
    /// Asserts that on segment <paramref name="k"/> of <paramref name="projectile"/>'s flight the first limit of
    /// data-model.md#projectile-step step 6 that its end b = p(k + 1) passes, in the contract's float
    /// expressions, is <paramref name="expected"/> (null: none): Range when |b − Origin|² &gt; <c>MaxRangeSq</c>,
    /// else Lifetime when k + 1 ≥ <c>LifetimeSteps</c>, else OutOfBounds when a coordinate of b is beyond ±8192 m.
    /// </summary>
    private static void AssertLimit(CompiledConfig config, ProjectileRecord projectile, uint k, ExpiryReason? expected, string context)
    {
        var origin = new Vec3(projectile.Origin.X, projectile.Origin.Y, projectile.Origin.Z);
        var velocity = new Vec3(projectile.LaunchVelocity.X, projectile.LaunchVelocity.Y, projectile.LaunchVelocity.Z);
        var b = ProjectileWorlds.ContractPosition(origin, velocity, config.HalfG[projectile.Class], config.StepRateHz, k + 1u);
        var dx = b.X - origin.X;
        var dy = b.Y - origin.Y;
        var dz = b.Z - origin.Z;
        var rangeSq = ((dx * dx) + (dy * dy)) + (dz * dz);
        ExpiryReason? limit = rangeSq > config.MaxRangeSq[projectile.Class]
            ? ExpiryReason.Range
            : k + 1u >= (uint)config.LifetimeSteps[projectile.Class]
                ? ExpiryReason.Lifetime
                : System.Math.Abs(b.X) > CoordinateLimit || System.Math.Abs(b.Y) > CoordinateLimit || System.Math.Abs(b.Z) > CoordinateLimit
                    ? ExpiryReason.OutOfBounds
                    : null;
        Assert.True(
            limit == expected,
            FormattableString.Invariant(
                $"{context}: segment {k} ends at {b} ({System.Math.Sqrt(rangeSq):R} m from Origin), which passes {limit?.ToString() ?? "no limit"}; expected {expected?.ToString() ?? "no limit"}"));
    }

    private static string Bits(Vec3 vector) => FormattableString.Invariant(
        $"{BitConverter.SingleToUInt32Bits(vector.X):x8} {BitConverter.SingleToUInt32Bits(vector.Y):x8} {BitConverter.SingleToUInt32Bits(vector.Z):x8}");
}
