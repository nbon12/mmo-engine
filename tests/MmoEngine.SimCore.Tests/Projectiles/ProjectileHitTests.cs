using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Projectiles;

/// <summary>
/// US5 scenarios 2, 3 and 6 and the point-blank edge case, with FR-010 and data-model.md#projectile-step
/// steps 3 to 5: a projectile reports the first thing its step's segment meets, a character's hitbox with
/// the part, the point and the step, a mesh in front of that character instead, a hitbox it crosses whole
/// within one step, and a hitbox it starts inside, at t = 0 in its first step.
/// <para>
/// The aimed shots are class-0 projectiles loaded in flight
/// (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>) at
/// the arranged step 1, which is also their launch step, so the first segment the world sweeps is k = 1
/// and <see cref="ProjectileReference.PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{CharacterRecord}, long)"/>
/// is given that <c>firstK</c>. The shooter of the 50 m shots stands at (0, 0, −25), so the target at
/// (0, 0, 25) and the wall at z = 12.5 m both lie beyond segment 0 at every rate (a 600 m/s shot covers
/// 30 m in a 20 Hz step). Every hit row first asserts that the double reference predicts the hit the row
/// is about, so no row can pass by comparing two missing hits. <c>System.Math</c> is written in full,
/// because <c>MmoEngine.SimCore.Math</c> shadows it.
/// </para>
/// </summary>
public sealed class ProjectileHitTests
{
    private const uint ShooterId = 1;

    private const uint TargetId = 2;

    /// <summary>The target's index in the arranged states: characters are in ascending ID, after the shooter.</summary>
    private const int TargetIndex = 1;

    private const uint ProjectileId = 1;

    /// <summary>The arranged state's step, which is also the in-flight projectile's launch step.</summary>
    private const uint ArrangedStep = 1;

    /// <summary>The first segment the world sweeps for the arranged projectile: step + 1 − launchStep.</summary>
    private const uint FirstSweptK = 1;

    private const int SmallArms = 0;

    /// <summary>Yaw 32768 faces −Z: a target at +Z faces the shooter.</summary>
    private const ushort FacingShooter = 32768;

    /// <summary>How many steps after the projectile leaves the state are run to check it stays gone.</summary>
    private const int StepsAfterLeaving = 2;

    /// <summary>The bound on a hit point's distance from its capsule's surface (T193).</summary>
    private const double SurfaceTolerance = 1e-4;

    /// <summary>The bound on a wall hit point's distance from the wall plane (T194).</summary>
    private const double PlaneTolerance = 1e-4;

    /// <summary><see cref="ProjectileCrossingAWholeHitboxInOneStepStillHits"/>'s muzzle speed, the rule table's maximum.</summary>
    private const float FastMuzzleSpeed = 2000f;

    /// <summary>
    /// The step <see cref="ProjectileStartingInsideAHitboxHitsAtItsFirstStep"/>'s projectile is launched and
    /// swept in (k = 0). Not 0 or 1, so a step that used the world step for k would show.
    /// </summary>
    private const uint PointBlankStep = 5;

    /// <summary>
    /// The eye height the point-blank shooter stands with, so that its eye, standing at the target's feet,
    /// is the projectile origin (0, 1.21, 0) on the torso's axis; below <c>movement.standHeight</c> 1.8 m (R18).
    /// </summary>
    private const float PointBlankEyeHeight = 1.21f;

    /// <summary>The 50 m shots' shooter: 25 m on the −Z side of the origin, facing +Z.</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, -25f);

    /// <summary>The 50 m shots' target: 25 m on the +Z side of the origin, behind <see cref="ProjectileWorlds.Wall"/>'s quad.</summary>
    private static readonly Vec3 TargetFeet = new(0f, 0f, 25f);

    /// <summary><see cref="ProjectileCrossingAWholeHitboxInOneStepStillHits"/>'s shooter, at the origin facing +Z.</summary>
    private static readonly Vec3 OriginFeet = new(0f, 0f, 0f);

    /// <summary><see cref="ProjectileStartingInsideAHitboxHitsAtItsFirstStep"/>'s origin: on the torso's axis of a target standing at the origin.</summary>
    private static readonly Vec3 PointBlankOrigin = new(0f, 1.21f, 0f);

    /// <summary>Rows <c>(stance, part, hz)</c> of <see cref="TargetInThePathReportsTheStruckHitboxPointAndStep"/>: both stances, every part, every rate.</summary>
    public static TheoryData<Stance, HitboxPart, int> PartRows => TestRates.WithRateLast<Stance, HitboxPart>(
        (Stance.Standing, HitboxPart.Head),
        (Stance.Standing, HitboxPart.Torso),
        (Stance.Standing, HitboxPart.Legs),
        (Stance.Crouched, HitboxPart.Head),
        (Stance.Crouched, HitboxPart.Torso),
        (Stance.Crouched, HitboxPart.Legs));

    /// <summary>
    /// US5 scenario 2: Given <see cref="ProjectileWorlds.Flat"/>, a shooter (ID 1) standing at (0, 0, −25)
    /// facing +Z, a target (ID 2) at (0, 0, 25) with the row's stance and yaw 32768 (facing the shooter),
    /// holding that stance every step, and one class-0 in-flight projectile owned by 1 from the shooter's eye
    /// at 600 m/s, launched toward the part's centre (the midpoint of the stance's configured A and B,
    /// contracts/config-rules.md, rotated by the target's yaw, <see cref="ProjectileReference.LayoutCapsules(Stance, DVec3, ushort, CompiledConfig)"/>)
    /// with the pitch whose double closed form passes through it, and the double reference predicting that
    /// part of target 2 first. When it is stepped with <c>SimWorld.Step</c>, Then the predicted step's
    /// <c>events.hits</c> is exactly one element, with that projectile ID, Kind Character, target 2, that
    /// hitbox, 0 ≤ T ≤ 1 and a point whose double distance to that capsule's axis segment (posed by
    /// <see cref="HitboxBuilder"/> from the target's committed pose) equals the capsule's radius within
    /// 1e-4 m; no earlier step reports a hit or an expiry, the projectile is in every committed state before
    /// that step, and it is in none from that step on.
    /// </summary>
    [Theory]
    [MemberData(nameof(PartRows))]
    public void TargetInThePathReportsTheStruckHitboxPointAndStep(Stance stance, HitboxPart part, int hz)
    {
        var context = FormattableString.Invariant($"{stance} target, {part}, {hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingShooter, stance);
        var centre = ProjectileReference.LayoutCapsules(stance, TargetFeet, FacingShooter, config)[(int)part].Centre;
        var projectile = AimedInFlight(config, ShooterFeet, centre);
        CharacterRecord[] characters = [shooter, target];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Character, TargetId: TargetId } && predicted.Hitbox == part,
            FormattableString.Invariant($"{context}: the double path aimed at the {part} centre {centre} must meet target {TargetId}'s {part} first, not {predicted}"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1, HoldStance(stance), context);

        var hitStep = AssertNothingBeforeAndGoneFrom(outcomes, predicted.Step, context);
        var at = FormattableString.Invariant($"{context}, step {hitStep.Step}");
        Assert.True(
            hitStep.Hits.Count == 1,
            $"{at}: the step reported hits [{string.Join(", ", hitStep.Hits)}]; expected exactly one, on target {TargetId}'s {part}");
        var hit = hitStep.Hits[0];
        Assert.True(
            hit.ProjectileId == ProjectileId && hit.Kind == HitKind.Character && hit.TargetId == TargetId && hit.Hitbox == part,
            $"{at}: the hit is {hit}; expected projectile {ProjectileId} on Character {TargetId}, {part}");
        Assert.True(hit.T is >= 0f and <= 1f, FormattableString.Invariant($"{at}: the hit's T {hit.T:R} is outside [0, 1]"));

        var committedTarget = AssertTargetHoldsItsPose(hitStep, target, at);
        var hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        HitboxBuilder.Build(config.HitboxLayouts[(int)committedTarget.Stance], committedTarget.Position, committedTarget.AimYaw, hitboxes);
        var capsule = hitboxes[(int)part];
        var distance = ProjectileReference.DistanceToSegment(hit.Point, capsule.A, capsule.B);
        Assert.True(
            System.Math.Abs(distance - capsule.Radius) <= SurfaceTolerance,
            FormattableString.Invariant($"{at}: the hit point {hit.Point} is {distance:R} m from the {part} axis {capsule.A} – {capsule.B}; expected the radius {capsule.Radius:R} within {SurfaceTolerance:R} m"));
    }

    /// <summary>
    /// US5 scenario 3: Given <see cref="ProjectileWorlds.Wall"/>, a shooter (ID 1) standing at (0, 0, −25)
    /// facing +Z, a target (ID 2) standing at (0, 0, 25) with yaw 32768, behind the wall quad at z = 12.5 m,
    /// and one class-0 in-flight projectile owned by 1 from the shooter's eye at 600 m/s aimed at the
    /// target's torso centre, whose double path meets the target's torso on <see cref="ProjectileWorlds.Flat"/>
    /// (so the wall is all that stands between them) and meets a wall triangle first on the wall world.
    /// When it is stepped with <c>SimWorld.Step</c> until it leaves the state, Then the run reports exactly
    /// one hit, at the predicted step, with Kind Mesh, the wall triangle that
    /// <see cref="ProjectileReference.SegmentTriangle"/> meets first along the predicted segment (one of
    /// <see cref="ProjectileWorlds.WallTriangleIds"/>) as its target ID, Hitbox None (255) and a point
    /// within 1e-4 m of z = 12.5; no hit holds Kind Character or target ID 2, no step reports an expiry, and
    /// the projectile is in no committed state from the hit step on.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void MeshBetweenMuzzleAndTargetIsHitInstead(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingShooter, Stance.Standing);
        var torso = ProjectileReference.LayoutCapsules(Stance.Standing, TargetFeet, FacingShooter, config)[(int)HitboxPart.Torso];
        var projectile = AimedInFlight(config, ShooterFeet, torso.Centre);
        CharacterRecord[] characters = [shooter, target];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var unobstructed = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            unobstructed is { Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso },
            FormattableString.Invariant($"{context}: without the wall the double path must meet target {TargetId}'s torso, not {unobstructed}"));
        var wallData = ProjectileWorlds.Wall();
        var predicted = ProjectileReference.PredictFirstHit(wallData, config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Mesh, Hitbox: HitboxPart.None } && predicted.Step <= unobstructed.Step,
            FormattableString.Invariant($"{context}: the double path must meet the wall before the target (at step {unobstructed.Step}), not {predicted}"));

        var a = ProjectileReference.ClosedForm(projectile, config, predicted.K);
        var b = ProjectileReference.ClosedForm(projectile, config, predicted.K + 1L);
        var firstTriangle = FirstWallTriangle(wallData, a, b);
        Assert.True(
            firstTriangle is { } id && ProjectileWorlds.WallTriangleIds.Contains(id) && (uint)id == predicted.TargetId,
            FormattableString.Invariant($"{context}: SegmentTriangle meets wall triangle {firstTriangle?.ToString() ?? "none"} first on segment {predicted.K}; PredictFirstHit names {predicted.TargetId}"));
        var wallTriangle = (uint)firstTriangle.GetValueOrDefault();

        var world = ProjectileWorlds.Start(ProjectileWorlds.WallWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1, commands: null, context);

        var hits = outcomes.SelectMany(o => o.Hits.Select(h => (o.Step, Hit: h))).ToArray();
        Assert.True(
            hits.Length == 1,
            FormattableString.Invariant($"{context}: the run reported hits [{string.Join(", ", hits)}]; expected exactly one, on wall triangle {wallTriangle} at step {predicted.Step}"));
        var (step, hit) = hits[0];
        var at = FormattableString.Invariant($"{context}, step {step}");
        Assert.True(step == predicted.Step, FormattableString.Invariant($"{at}: the wall hit is reported at step {step}; the double path meets it at step {predicted.Step}"));
        Assert.True(
            hit.ProjectileId == ProjectileId && hit.Kind == HitKind.Mesh && hit.TargetId == wallTriangle && hit.Hitbox == HitboxPart.None,
            $"{at}: the hit is {hit}; expected projectile {ProjectileId} on Mesh triangle {wallTriangle} with Hitbox None");
        Assert.True(
            (byte)hit.Hitbox == 255,
            FormattableString.Invariant($"{at}: a world hit's hitbox byte is {(byte)hit.Hitbox}, not 255"));
        Assert.True(
            System.Math.Abs(hit.Point.Z - (double)ProjectileWorlds.WallZ) <= PlaneTolerance,
            FormattableString.Invariant($"{at}: the hit point {hit.Point} is not within {PlaneTolerance:R} m of the wall plane z = {ProjectileWorlds.WallZ:R}"));

        foreach (var outcome in outcomes)
        {
            var during = FormattableString.Invariant($"{context}, step {outcome.Step}");
            Assert.True(
                outcome.Hits.All(h => h.Kind != HitKind.Character && h.TargetId != TargetId),
                $"{during}: the step reported hits [{string.Join(", ", outcome.Hits)}]; the wall hides the target");
            Assert.True(outcome.Expiries.Count == 0, $"{during}: the step reported expiries [{string.Join(", ", outcome.Expiries)}]; the shot hits the wall");
        }

        AssertNothingBeforeAndGoneFrom(outcomes, predicted.Step, context);
    }

    /// <summary>
    /// US5 scenario 6: Given <c>weapons[0].muzzleSpeed</c> 2000, so that one step's segment, 2000 / hz ≥
    /// 33.3 m, is longer than ten torso diameters (4.4 m), a shooter (ID 1) standing at the origin facing
    /// +Z, a standing target (ID 2) with yaw 32768 placed 1.5 segments downrange, so its torso lies in the
    /// middle of segment k = 1 and both of that segment's end points are outside its bounding sphere (the
    /// double distance to <c>BoundCenterUp</c> above its feet against <c>BoundRadius</c>), and one class-0
    /// in-flight projectile owned by 1 from the shooter's eye at 2000 m/s aimed at the torso centre, with the
    /// double reference predicting target 2's torso on that segment. When it is stepped with
    /// <c>SimWorld.Step</c>, Then that segment's step reports exactly one hit, with that projectile ID, Kind
    /// Character, target 2, Torso and 0 &lt; T &lt; 1, no earlier step reports a hit or an expiry, and the
    /// projectile is removed in that step.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ProjectileCrossingAWholeHitboxInOneStepStillHits(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz, {FastMuzzleSpeed:R} m/s");
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Weapons![SmallArms]!.MuzzleSpeed = FastMuzzleSpeed;
        }));
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        Assert.True(speed == FastMuzzleSpeed, FormattableString.Invariant($"{context}: class 0's muzzle speed is {speed:R}, not {FastMuzzleSpeed:R}"));

        var targetFeet = new Vec3(0f, 0f, (float)(1.5 * FastMuzzleSpeed / hz));
        var shooter = ProjectileWorlds.Shooter(ShooterId, OriginFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, targetFeet, FacingShooter, Stance.Standing);
        var torso = ProjectileReference.LayoutCapsules(Stance.Standing, targetFeet, FacingShooter, config)[(int)HitboxPart.Torso];
        var projectile = AimedInFlight(config, OriginFeet, torso.Centre);
        CharacterRecord[] characters = [shooter, target];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var a = ProjectileReference.ClosedForm(projectile, config, FirstSweptK);
        var b = ProjectileReference.ClosedForm(projectile, config, FirstSweptK + 1L);
        var length = DVec3.Distance(a, b);
        var tenDiameters = 10.0 * 2.0 * torso.Radius;
        Assert.True(
            length > tenDiameters,
            FormattableString.Invariant($"{context}: segment {FirstSweptK} is {length:R} m long, not longer than ten torso diameters ({tenDiameters:R} m)"));
        var middle = DVec3.Dot(torso.Centre - a, b - a) / DVec3.LengthSquared(b - a);
        Assert.True(
            middle is > 0.4 and < 0.6,
            FormattableString.Invariant($"{context}: the torso centre {torso.Centre} lies at fraction {middle:R} of segment {FirstSweptK}, not in its middle"));
        var layout = config.HitboxLayouts[(int)Stance.Standing];
        var boundCentre = new DVec3(targetFeet.X, targetFeet.Y + (double)layout.BoundCenterUp, targetFeet.Z);
        foreach (var (name, end) in new[] { ("start", a), ("end", b) })
        {
            var fromBound = DVec3.Distance(end, boundCentre);
            Assert.True(
                fromBound > layout.BoundRadius,
                FormattableString.Invariant($"{context}: the segment's {name} {end} is {fromBound:R} m from the bounding sphere's centre, inside its radius {layout.BoundRadius:R}"));
        }

        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso, K: FirstSweptK },
            FormattableString.Invariant($"{context}: the double path must meet target {TargetId}'s torso on segment {FirstSweptK}, not {predicted}"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1, commands: null, context);

        var hitStep = AssertNothingBeforeAndGoneFrom(outcomes, predicted.Step, context);
        var at = FormattableString.Invariant($"{context}, step {hitStep.Step}");
        Assert.True(
            hitStep.Hits.Count == 1,
            $"{at}: the step reported hits [{string.Join(", ", hitStep.Hits)}]; expected exactly one, on target {TargetId}'s torso");
        var hit = hitStep.Hits[0];
        Assert.True(
            hit.ProjectileId == ProjectileId && hit.Kind == HitKind.Character && hit.TargetId == TargetId && hit.Hitbox == HitboxPart.Torso,
            $"{at}: the hit is {hit}; expected projectile {ProjectileId} on Character {TargetId}, Torso");
        Assert.True(hit.T is > 0f and < 1f, FormattableString.Invariant($"{at}: the hit's T {hit.T:R} is not inside (0, 1)"));
    }

    /// <summary>
    /// The point-blank edge case ("a projectile that starts inside a hitbox reports that hit at its first
    /// step"). Given a target (ID 2) standing at (0, 0, 0) with yaw 0, whose hitboxes and bounding sphere
    /// <see cref="HitboxBuilder"/> builds into spans that a <see cref="ProjectileTargets"/> is made over, and
    /// a class-0 projectile owned by character 1 with LaunchStep n = 5 (so k = 0) and Origin (0, 1.21, 0),
    /// inside the target's torso capsule and outside its head and legs, When
    /// <c>ProjectileStep.Step(ref projectile, n, config, collision, targets, out hit, out expiry)</c> is called
    /// once, Then it returns Hit, and the hit has projectile 1, Kind Character, target 2, Torso, T bits
    /// 0x00000000 and exactly Origin's bits as its point. And through <c>SimWorld.Step</c>, Given that target
    /// and the shooter (ID 1) standing at its feet with an eye height of 1.21 m, so that its eye is that
    /// Origin, from a state at step n − 1, When the shooter presses Fire in step n, Then step n reports its
    /// one shot and exactly one hit, on that shot's projectile, with Kind Character, target 2, Torso, T bits
    /// 0x00000000 and Origin's bits as its point, and the projectile is not in step n's committed state.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ProjectileStartingInsideAHitboxHitsAtItsFirstStep(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Hitboxes!.EyeHeightStanding = PointBlankEyeHeight;
        }));

        var hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        var bounds = HitboxBuilder.Build(config.HitboxLayouts[(int)Stance.Standing], OriginFeet, 0, hitboxes);
        foreach (var capsule in hitboxes)
        {
            var distance = ProjectileReference.DistanceToSegment(PointBlankOrigin, capsule.A, capsule.B);
            var inside = distance < capsule.Radius;
            Assert.True(
                inside == (capsule.Part == HitboxPart.Torso),
                FormattableString.Invariant($"{context}: the origin {PointBlankOrigin} is {distance:R} m from the {capsule.Part} axis (radius {capsule.Radius:R}); it must start inside the torso alone"));
        }

        uint[] targetIds = [TargetId];
        HitboxBounds[] targetBounds = [bounds];
        var targets = new ProjectileTargets(targetIds, hitboxes, targetBounds);
        var projectile = new Projectile
        {
            Id = ProjectileId,
            OwnerId = ShooterId,
            Class = SmallArms,
            LaunchStep = PointBlankStep,
            Origin = PointBlankOrigin,
            LaunchVelocity = ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(0.0, 0.0), ProjectileWorlds.MuzzleSpeed(config, SmallArms)),
            Position = PointBlankOrigin,
        };

        var outcome = ProjectileStep.Step(ref projectile, PointBlankStep, config, ProjectileWorlds.FlatWorld, targets, out var direct, out _);

        var call = FormattableString.Invariant($"{context}, ProjectileStep.Step at step {PointBlankStep} (k 0)");
        Assert.True(outcome == ProjectileOutcome.Hit, $"{call}: returned {outcome}; expected Hit");
        AssertPointBlankHit(direct, ProjectileId, call);

        var shooter = ProjectileWorlds.Shooter(ShooterId, OriginFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, OriginFeet, yaw: 0, Stance.Standing);
        var eye = ProjectileWorlds.Eye(OriginFeet, config, Stance.Standing);
        Assert.True(
            Bits(eye) == Bits(PointBlankOrigin),
            FormattableString.Invariant($"{context}: the shooter's eye {eye} must be the projectile origin {PointBlankOrigin}"));
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(PointBlankStep - 1u, [shooter, target]));
        CharacterCommand[] fire = [new(0, new InputCommand(1u, PointBlankStep, Buttons.Fire, 0, 0, 0))];

        var report = world.Step(fire);

        var stepped = FormattableString.Invariant($"{context}, SimWorld.Step to step {PointBlankStep}");
        Assert.True(
            report.Fault.Code == StepFaultCode.None,
            $"{stepped}: faulted with {report.Fault.Code} at '{report.Fault.Path}'");
        Assert.True(report.Step == PointBlankStep, FormattableString.Invariant($"{stepped}: the report is for step {report.Step}"));
        var shots = report.Events.Shots.ToArray();
        Assert.True(
            shots.Length == 1 && shots[0].CharacterId == ShooterId,
            $"{stepped}: the step reported shots [{string.Join(", ", shots)}]; expected one, by character {ShooterId}");
        var hits = report.Events.Hits.ToArray();
        Assert.True(
            hits.Length == 1,
            $"{stepped}: the step reported hits [{string.Join(", ", hits)}]; expected exactly one, the point-blank torso hit");
        AssertPointBlankHit(hits[0], shots[0].ProjectileId, stepped);
        var committed = StateSerializer.ReadCommitted(report.State);
        Assert.True(
            committed.Projectiles.Count == 0,
            $"{stepped}: the committed state still holds projectiles [{string.Join(", ", committed.Projectiles)}]");
    }

    /// <summary>
    /// A class-0 projectile owned by the shooter, in flight at <see cref="ArrangedStep"/>, launched at that
    /// step from the eye of a shooter standing at <paramref name="shooterFeet"/> at the class's default
    /// muzzle speed, with the low-root pitch whose double closed form passes through
    /// <paramref name="aimPoint"/> (<see cref="ProjectileWorlds.AimWithDrop"/>).
    /// </summary>
    private static ProjectileRecord AimedInFlight(CompiledConfig config, Vec3 shooterFeet, DVec3 aimPoint)
    {
        var eye = ProjectileWorlds.Eye(shooterFeet, config, Stance.Standing);
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var direction = ProjectileWorlds.AimWithDrop(eye, aimPoint, speed, config.HalfG[SmallArms]);
        return ProjectileWorlds.InFlight(
            ProjectileId,
            ShooterId,
            SmallArms,
            eye,
            ProjectileWorlds.LaunchVelocity(direction, speed),
            ArrangedStep,
            ArrangedStep,
            config);
    }

    /// <summary>
    /// The target's command for each step, holding <paramref name="stance"/> (Crouch for Crouched, no
    /// button for Standing) and its yaw, so its post-movement pose is the arranged one every step.
    /// </summary>
    private static Func<uint, CharacterCommand[]> HoldStance(Stance stance)
    {
        var buttons = stance == Stance.Crouched ? Buttons.Crouch : default;
        return step => [new CharacterCommand(TargetIndex, new InputCommand(step, step, buttons, FacingShooter, 0, 0))];
    }

    /// <summary>
    /// Steps <paramref name="world"/> until the committed state holds no projectile, then
    /// <see cref="StepsAfterLeaving"/> steps more, giving each step the commands
    /// <paramref name="commands"/> returns for it (none when null: every character gets the neutral input),
    /// and copies each step's hits, expiries, committed projectile IDs and committed characters before the
    /// next step reuses the world's buffers. A fault, or a projectile still present after
    /// <paramref name="maxSteps"/> steps, fails the test.
    /// </summary>
    private static List<StepOutcome> RunUntilTheStateHoldsNoProjectile(
        SimWorld world,
        int maxSteps,
        Func<uint, CharacterCommand[]>? commands,
        string context)
    {
        var outcomes = new List<StepOutcome>();
        var leftAt = -1;
        for (var i = 0; i < maxSteps; i++)
        {
            var step = world.CurrentStep + 1u;
            var report = world.Step(commands is null ? ReadOnlySpan<CharacterCommand>.Empty : commands(step));
            Assert.True(
                report.Fault.Code == StepFaultCode.None,
                FormattableString.Invariant($"{context}: step {step} faulted with {report.Fault.Code} at '{report.Fault.Path}'"));
            var committed = StateSerializer.ReadCommitted(report.State);
            var events = report.Events;
            outcomes.Add(new StepOutcome(
                report.Step,
                events.Hits.ToArray(),
                events.Expiries.ToArray(),
                committed.Projectiles.Select(p => p.Id).ToArray(),
                committed.Characters.ToArray()));
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

    /// <summary>
    /// Asserts that the run reached <paramref name="hitStep"/>, that every earlier step reported no hit and
    /// no expiry and committed the projectile, and that no committed state from <paramref name="hitStep"/>
    /// on holds it; returns the outcome of <paramref name="hitStep"/>.
    /// </summary>
    private static StepOutcome AssertNothingBeforeAndGoneFrom(IReadOnlyList<StepOutcome> outcomes, uint hitStep, string context)
    {
        Assert.True(
            outcomes[^1].Step >= hitStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the predicted hit step {hitStep}"));
        StepOutcome? found = null;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (hit expected at step {hitStep})");
            if (outcome.Step < hitStep)
            {
                Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] before the predicted hit");
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] before the predicted hit");
                Assert.True(
                    outcome.ProjectileIds.SequenceEqual([ProjectileId]),
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}]; expected [{ProjectileId}] until the hit");
            }
            else
            {
                Assert.True(
                    outcome.ProjectileIds.Count == 0,
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}] from the hit step on");
                if (outcome.Step == hitStep)
                {
                    found = outcome;
                }
            }
        }

        Assert.True(found is not null, FormattableString.Invariant($"{context}: the run has no outcome for step {hitStep}"));
        return found;
    }

    /// <summary>
    /// Asserts that the committed state of <paramref name="outcome"/> holds the target with the arranged
    /// position bits, yaw and stance, so the pose the reference predicted from is the one stage 4 posed;
    /// returns that committed character.
    /// </summary>
    private static CharacterState AssertTargetHoldsItsPose(StepOutcome outcome, CharacterRecord target, string context)
    {
        var committed = outcome.Characters.Where(c => c.Id == target.Id).ToArray();
        Assert.True(committed.Length == 1, FormattableString.Invariant($"{context}: the committed state holds {committed.Length} characters with ID {target.Id}"));
        var character = committed[0];
        var arranged = new Vec3(target.Position.X, target.Position.Y, target.Position.Z);
        Assert.True(
            Bits(character.Position) == Bits(arranged) && character.AimYaw == target.AimYaw && character.Stance == (Stance)target.Stance,
            FormattableString.Invariant(
                $"{context}: the target is at {character.Position} yaw {character.AimYaw} {character.Stance}; it must hold its arranged pose {arranged} yaw {target.AimYaw} {(Stance)target.Stance}"));
        return character;
    }

    /// <summary>
    /// Asserts the point-blank hit: projectile <paramref name="projectileId"/>, Kind Character, target 2,
    /// Torso, T bits 0x00000000 and exactly <see cref="PointBlankOrigin"/>'s bits as its point.
    /// </summary>
    private static void AssertPointBlankHit(ProjectileHit hit, uint projectileId, string context)
    {
        Assert.True(
            hit.ProjectileId == projectileId && hit.Kind == HitKind.Character && hit.TargetId == TargetId && hit.Hitbox == HitboxPart.Torso,
            $"{context}: the hit is {hit}; expected projectile {projectileId} on Character {TargetId}, Torso");
        Assert.True(
            BitConverter.SingleToUInt32Bits(hit.T) == 0u,
            FormattableString.Invariant($"{context}: the hit's T is {hit.T:R} (bits {BitConverter.SingleToUInt32Bits(hit.T):x8}); expected bits 00000000"));
        Assert.True(
            Bits(hit.Point) == Bits(PointBlankOrigin),
            FormattableString.Invariant($"{context}: the hit point {hit.Point} (bits {Bits(hit.Point)}) is not the origin {PointBlankOrigin} (bits {Bits(PointBlankOrigin)})"));
    }

    /// <summary>
    /// The wall triangle of <paramref name="wall"/>'s single mesh that the double segment from
    /// <paramref name="a"/> to <paramref name="b"/> meets first by <see cref="ProjectileReference.SegmentTriangle"/>,
    /// ties within <see cref="ProjectileReference.TieTolerance"/> to the lower ID, or null when it meets none.
    /// </summary>
    private static int? FirstWallTriangle(CollisionData wall, DVec3 a, DVec3 b)
    {
        var mesh = wall.Meshes.Single();
        int? first = null;
        var firstT = double.PositiveInfinity;
        for (var t = 0; t < ProjectileWorlds.WallTriangleIds.Count; t++)
        {
            var hit = ProjectileReference.SegmentTriangle(
                a,
                b,
                Vertex(mesh, mesh.Indices[3 * t]),
                Vertex(mesh, mesh.Indices[(3 * t) + 1]),
                Vertex(mesh, mesh.Indices[(3 * t) + 2]));
            if (hit is { } at && at < firstT - ProjectileReference.TieTolerance)
            {
                first = ProjectileWorlds.WallTriangleIds[t];
                firstT = at;
            }
        }

        return first;
    }

    private static DVec3 Vertex(MeshData mesh, uint index)
    {
        var at = checked(3 * (int)index);
        return new DVec3(mesh.Vertices[at], mesh.Vertices[at + 1], mesh.Vertices[at + 2]);
    }

    private static string Bits(Vec3 vector) => FormattableString.Invariant(
        $"{BitConverter.SingleToUInt32Bits(vector.X):x8} {BitConverter.SingleToUInt32Bits(vector.Y):x8} {BitConverter.SingleToUInt32Bits(vector.Z):x8}");

    /// <summary>One committed step: its number, its hit and expiry events, the committed projectile IDs and characters.</summary>
    private sealed record StepOutcome(
        uint Step,
        IReadOnlyList<ProjectileHit> Hits,
        IReadOnlyList<ExpiryEvent> Expiries,
        IReadOnlyList<uint> ProjectileIds,
        IReadOnlyList<CharacterState> Characters);
}
