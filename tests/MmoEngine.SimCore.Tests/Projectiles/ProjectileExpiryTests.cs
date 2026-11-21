using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Projectiles;

/// <summary>
/// US5 scenario 4 and data-model.md#projectile-step step 6: a projectile that hits nothing expires, and
/// leaves the state, in the step whose segment end b = p(k + 1) first passes one of its limits, checked in
/// the contract's order: Range when |b − Origin|² &gt; <c>MaxRangeSq[class]</c>, Lifetime when
/// k + 1 ≥ <c>LifetimeSteps[class]</c>, OutOfBounds when any coordinate of b is beyond ±8192 m. The step
/// reports it as one <c>{projectileId, reason}</c> element of <c>events.expiries</c> and no hit. The
/// expected step is computed here with the contract's float expressions
/// (<see cref="ProjectileWorlds.ContractPosition"/> and the squared length written out as
/// <c>((dx·dx) + (dy·dy)) + (dz·dz)</c>), and every row first asserts, with
/// <see cref="ProjectileReference.PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{CharacterRecord}, long)"/>,
/// that the double path meets nothing at or before that step, so no row can pass by never flying far
/// enough to expire.
/// </summary>
public sealed class ProjectileExpiryTests
{
    private const uint ShooterId = 1;

    private const uint ProjectileId = 1;

    /// <summary>
    /// The arranged state's step, which is also the in-flight projectile's launch step, so the first step the
    /// world runs sweeps k = 1 (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>).
    /// </summary>
    private const uint ArrangedStep = 1;

    /// <summary>The first segment the world sweeps for the arranged projectile: step + 1 − launchStep.</summary>
    private const uint FirstSweptK = 1;

    /// <summary>The <c>maxRangeM</c> of the Lifetime and OutOfBounds rows: the rule table's maximum, 10 km.</summary>
    private const float ExtendedRange = 10000f;

    /// <summary>The coordinate bound of data-model.md#projectile-step step 6.</summary>
    private const float CoordinateLimit = 8192f;

    /// <summary>How many steps after the projectile leaves the state are run to check it stays gone.</summary>
    private const int StepsAfterLeaving = 2;

    private const int SmallArms = 0;

    /// <summary><see cref="LimitsApplyInContractOrder"/>'s row where Range and Lifetime are first passed by one segment.</summary>
    private const string RangeAndLifetime = "range-and-lifetime-same-step";

    /// <summary><see cref="LimitsApplyInContractOrder"/>'s row where Lifetime and OutOfBounds are first passed by one segment.</summary>
    private const string LifetimeAndOutOfBounds = "lifetime-and-out-of-bounds-same-step";

    /// <summary><see cref="LimitsApplyInContractOrder"/>'s row where Range and OutOfBounds are first passed by one segment.</summary>
    private const string RangeAndOutOfBounds = "range-and-out-of-bounds-same-step";

    /// <summary><see cref="HitBeyondTheRangeExpiresWithRange"/>'s target.</summary>
    private const uint FarTargetId = 2;

    /// <summary>Yaw 32768 faces −Z: <see cref="HitBeyondTheRangeExpiresWithRange"/>'s target faces its shooter.</summary>
    private const ushort FacingMinusZ = 32768;

    private static readonly Vec3 ShooterFeet = new(0f, 0f, 0f);

    /// <summary><see cref="HitBeyondTheRangeExpiresWithRange"/>'s shooter: 300 m behind the origin, facing +Z.</summary>
    private static readonly Vec3 FarShooterFeet = new(0f, 0f, -300f);

    /// <summary>
    /// <see cref="HitBeyondTheRangeExpiresWithRange"/>'s target: 600.5 m downrange of <see cref="FarShooterFeet"/>,
    /// so its torso's near surface (radius 0.22 m) is about 600.28 m from the shooter's eye, beyond the 600 m range.
    /// </summary>
    private static readonly Vec3 FarTargetFeet = new(0f, 0f, 300.5f);

    /// <summary>The OutOfBounds row's origin: 92 m inside the +X coordinate bound, 50 m up.</summary>
    private static readonly Vec3 OutOfBoundsOrigin = new(8100f, 50f, 0f);

    /// <summary>The OutOfBounds row's launch velocity: 600 m/s along +X.</summary>
    private static readonly Vec3 OutOfBoundsVelocity = new(600f, 0f, 0f);

    private static readonly Lazy<CollisionWorld> CommittedTestWorld =
        new(() => CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd")));

    /// <summary>
    /// Rows <c>(reason, classIndex, hz)</c> of <see cref="ProjectilePastItsLimitExpiresAtThatStepWithoutAHit"/>:
    /// Range and Lifetime for both classes and OutOfBounds for class 0, each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<ExpiryReason, int, int> LimitRows => TestRates.WithRateLast<ExpiryReason, int>(
        (ExpiryReason.Range, 0),
        (ExpiryReason.Range, 1),
        (ExpiryReason.Lifetime, 0),
        (ExpiryReason.Lifetime, 1),
        (ExpiryReason.OutOfBounds, 0));

    /// <summary>
    /// The rows of <see cref="LimitsApplyInContractOrder"/>: each names two limits that one segment passes
    /// first, together.
    /// </summary>
    public static TheoryData<string> OrderCases => [RangeAndLifetime, LifetimeAndOutOfBounds, RangeAndOutOfBounds];

    /// <summary>
    /// US5 scenario 4: Given <see cref="ProjectileWorlds.Flat"/>, its owner (character 1, standing at the
    /// origin) as the only character, and one in-flight projectile of the class from the owner's eye
    /// (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>,
    /// launched at the arranged step 1) at the class's default muzzle speed (600 / 120 m/s): for Range,
    /// heading +Z 10° above the horizon with the default <c>maxRangeM</c> (600 / 400); for Lifetime, 45° up
    /// with <c>maxRangeM</c> 10000; for OutOfBounds (class 0), from (8100, 50, 0) at (600, 0, 0) m/s with
    /// <c>maxRangeM</c> 10000. The contract's float expressions put the first limit passed at segment k and
    /// name the row's reason for it, and the double path meets nothing at or before world step 1 + k. When
    /// the world is stepped with <c>SimWorld.Step</c> until the projectile leaves the state, Then step 1 + k
    /// reports exactly <c>[{1, reason}]</c> in <c>events.expiries</c>, no earlier step reports an expiry, no
    /// step reports a hit, the projectile is in every committed state before step 1 + k, and the committed
    /// state holds no projectile from step 1 + k on.
    /// </summary>
    [Theory]
    [MemberData(nameof(LimitRows))]
    public void ProjectilePastItsLimitExpiresAtThatStepWithoutAHit(ExpiryReason reason, int classIndex, int hz)
    {
        var context = FormattableString.Invariant($"{reason} row, class {classIndex}, {hz} Hz");
        var raw = TestConfigs.At(hz);
        if (reason != ExpiryReason.Range)
        {
            raw.ProjectileClasses![classIndex]!.MaxRangeM = ExtendedRange;
        }

        var config = CompiledConfig.Create(raw);
        var maxRange = reason != ExpiryReason.Range ? ExtendedRange : classIndex == SmallArms ? 600f : 400f;
        Assert.True(
            config.MaxRangeSq[classIndex] == maxRange * maxRange,
            FormattableString.Invariant($"{context}: MaxRangeSq[{classIndex}] is {config.MaxRangeSq[classIndex]:R}, expected {maxRange:R}²"));

        var (origin, velocity) = reason switch
        {
            ExpiryReason.Range => (Eye(config), Launch(config, classIndex, 10.0)),
            ExpiryReason.Lifetime => (Eye(config), Launch(config, classIndex, 45.0)),
            ExpiryReason.OutOfBounds => (OutOfBoundsOrigin, OutOfBoundsVelocity),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a row of LimitRows."),
        };
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var projectile = ProjectileWorlds.InFlight(ProjectileId, ShooterId, classIndex, origin, velocity, ArrangedStep, ArrangedStep, config);
        CharacterRecord[] characters = [shooter];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var (k, contractReason) = ContractExpiry(config, classIndex, AsVector(projectile.Origin), AsVector(projectile.LaunchVelocity), FirstSweptK);
        var expiryStep = ArrangedStep + k;
        Assert.True(
            contractReason == reason,
            FormattableString.Invariant($"{context}: the contract's first limit is {contractReason} at k {k} (step {expiryStep}); the row is set up for {reason}"));
        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is null || predicted.Step > expiryStep,
            FormattableString.Invariant($"{context}: the double path meets {predicted?.Kind} {predicted?.TargetId} at step {predicted?.Step}, not after the expiry step {expiryStep}"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[classIndex] + StepsAfterLeaving + 1, context);

        Assert.True(
            outcomes[^1].Step >= expiryStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the expiry step {expiryStep}"));
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (expiry expected at step {expiryStep})");
            Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}]; expected none");
            if (outcome.Step < expiryStep)
            {
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] before the limit is passed");
                Assert.True(
                    outcome.ProjectileIds.SequenceEqual([ProjectileId]),
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}]; expected [{ProjectileId}] until the expiry step");
            }
            else if (outcome.Step == expiryStep)
            {
                Assert.True(
                    outcome.Expiries.SequenceEqual([new ExpiryEvent { ProjectileId = ProjectileId, Reason = reason }]),
                    $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}]; expected exactly [{{ ProjectileId = {ProjectileId}, Reason = {reason} }}]");
                Assert.True(outcome.ProjectileIds.Count == 0, $"{at}: the committed state still holds projectiles [{string.Join(", ", outcome.ProjectileIds)}]");
            }
            else
            {
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] after the projectile left");
                Assert.True(outcome.ProjectileIds.Count == 0, $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}] after the expiry step");
            }
        }
    }

    /// <summary>
    /// US5 scenario 4 through Phase 6's <c>rifle-burst</c> definition: Given
    /// <c>ScenarioCatalog.Get("rifle-burst").Build(TestConfigs.At(hz), testWorld, hz)</c> over the committed
    /// <c>simcore/content/test-world.mmcd</c> (opened read-only through <see cref="RepoPaths"/>), whose rifle
    /// fires class-0 shots above the horizon (asserted on each shot's direction) along the clear firing lane,
    /// and for each shot's projectile, taken from the committed state of the step that fired it, the first k
    /// whose |p(k + 1) − Origin|² &gt; <c>MaxRangeSq[0]</c> in the contract's float expressions, with the
    /// double path meeting nothing at or before step LaunchStep + k, When its steps run on a
    /// <see cref="SimWorld"/>, Then every shot's projectile ID appears in exactly one expiry event, with reason
    /// <see cref="ExpiryReason.Range"/>, at step LaunchStep + k, and in no hit event.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void RifleBurstShotsAboveTheHorizonExpireWithRange(int hz)
    {
        var testWorld = CommittedTestWorld.Value;
        var stream = ScenarioCatalog.Get("rifle-burst").Build(TestConfigs.At(hz), testWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        var collisionData = testWorld.Data;

        var run = ScenarioRuns.Run(new SimWorld(config, testWorld), stream);

        foreach (var step in run.Steps)
        {
            Assert.True(
                step.Fault.Code == StepFaultCode.None,
                FormattableString.Invariant($"{hz} Hz: step {step.Step} faulted with {step.Fault.Code} at '{step.Fault.Path}'"));
        }

        var shots = run.Steps.SelectMany(step => step.Shots.Select(shot => (step.Step, Shot: shot, step.Projectiles))).ToList();
        Assert.True(shots.Count > 0, FormattableString.Invariant($"{hz} Hz: rifle-burst fired no shot"));
        foreach (var (shotStep, shot, committed) in shots)
        {
            var id = shot.ProjectileId;
            var at = FormattableString.Invariant($"{hz} Hz, projectile {id} fired at step {shotStep}");
            Assert.True(shot.Direction.Y > 0f, FormattableString.Invariant($"{at}: the shot's direction {shot.Direction} is not above the horizon"));
            var spawned = committed.Where(p => p.Id == id).ToArray();
            Assert.True(
                spawned.Length == 1 && spawned[0].Class == SmallArms && spawned[0].LaunchStep == shotStep,
                FormattableString.Invariant($"{at}: the committed state of that step holds [{string.Join(", ", spawned)}]; expected one class-0 projectile launched at that step"));
            var projectile = spawned[0];
            var k = FirstRangeK(config, SmallArms, projectile.Origin, projectile.LaunchVelocity, 0u);
            Assert.True(k.HasValue, $"{at}: the contract never passes MaxRangeSq[0] within LifetimeSteps[0]");
            var expiryStep = projectile.LaunchStep + k.Value;
            var predicted = ProjectileReference.PredictFirstHit(collisionData, config, projectile, run.InitialCharacters, 0);
            Assert.True(
                predicted is null || predicted.Step > expiryStep,
                FormattableString.Invariant($"{at}: the double path meets {predicted?.Kind} {predicted?.TargetId} at step {predicted?.Step}, not after the Range step {expiryStep}"));

            var expiries = run.Steps
                .SelectMany(step => step.Expiries.Where(e => e.ProjectileId == id).Select(e => (step.Step, e.Reason)))
                .ToArray();
            var hits = run.Steps
                .SelectMany(step => step.Hits.Where(h => h.ProjectileId == id).Select(h => (step.Step, Hit: h)))
                .ToArray();

            Assert.True(
                expiries.Length == 1 && expiries[0].Reason == ExpiryReason.Range && expiries[0].Step == expiryStep,
                FormattableString.Invariant(
                    $"{at}: expiry events [{string.Join(", ", expiries)}] over {run.Steps.Count} steps; expected exactly one, Range at step {expiryStep} (LaunchStep {projectile.LaunchStep} + k {k.Value})"));
            Assert.True(hits.Length == 0, FormattableString.Invariant($"{at}: hit events [{string.Join(", ", hits)}]; a shot above the horizon hits nothing"));
        }
    }

    /// <summary>
    /// data-model.md#projectile-step step 6: with no hit, the first of Range, Lifetime and OutOfBounds that
    /// applies expires the projectile, in that order. Given <see cref="ProjectileWorlds.Flat"/>, the owner
    /// (character 1, standing at the origin) as the only character, the default configuration's step rate
    /// and one class-0 in-flight projectile launched at the arranged step 1 whose limits are set so that the
    /// row's two limits are first passed by the same segment k and the third is not passed by then:
    /// <list type="bullet">
    /// <item><c>range-and-lifetime-same-step</c>: from the owner's eye, heading +Z 10° up at 600 m/s with the
    /// default 600 m range, and <c>lifetimeMs</c> set so that <c>LifetimeSteps</c> is the first Range k + 1;</item>
    /// <item><c>lifetime-and-out-of-bounds-same-step</c>: from (8100, 50, 0) at (600, 0, 0) m/s with
    /// <c>maxRangeM</c> 10000, and <c>LifetimeSteps</c> set to the first OutOfBounds k + 1;</item>
    /// <item><c>range-and-out-of-bounds-same-step</c>: the same flight with the default lifetime and
    /// <c>maxRangeM</c> set between |p(k) − Origin| and |p(k + 1) − Origin| for the first OutOfBounds k.</item>
    /// </list>
    /// The arrange checks the coincidence with the contract's float expressions, and that the double path
    /// meets nothing at or before world step 1 + k. When the world is stepped with <c>SimWorld.Step</c> until
    /// the projectile leaves the state, Then step 1 + k reports exactly one expiry,
    /// <c>{1, Range}</c>, <c>{1, Lifetime}</c> and <c>{1, Range}</c> respectively, no other step reports an
    /// expiry, no step reports a hit, and the projectile leaves the state at step 1 + k.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderCases))]
    public void LimitsApplyInContractOrder(string caseName)
    {
        var (raw, origin, velocity, limitA, limitB, expected) = ArrangeTwoLimitsInOneSegment(caseName);
        var config = CompiledConfig.Create(raw);
        var context = FormattableString.Invariant($"{caseName}, {config.StepRateHz} Hz");
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var projectile = ProjectileWorlds.InFlight(ProjectileId, ShooterId, SmallArms, origin, velocity, ArrangedStep, ArrangedStep, config);
        CharacterRecord[] characters = [shooter];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var limits = FirstLimits(config, SmallArms, AsVector(projectile.Origin), AsVector(projectile.LaunchVelocity), FirstSweptK);
        var described = FormattableString.Invariant(
            $"Range at k {limits[ExpiryReason.Range]?.ToString() ?? "never"}, Lifetime at k {limits[ExpiryReason.Lifetime]?.ToString() ?? "never"}, OutOfBounds at k {limits[ExpiryReason.OutOfBounds]?.ToString() ?? "never"}");
        Assert.True(
            limits[limitA] is { } k && limits[limitB] == k,
            $"{context}: the arranged limits must put {limitA} and {limitB} in one segment; the contract gives {described}");
        var segment = limits[limitA].GetValueOrDefault();
        foreach (var (reason, at) in limits)
        {
            Assert.True(
                reason == limitA || reason == limitB || at is null || at > segment,
                $"{context}: {reason} must not be passed by segment {segment}; the contract gives {described}");
        }

        var expiryStep = ArrangedStep + segment;
        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is null || predicted.Step > expiryStep,
            FormattableString.Invariant($"{context}: the double path meets {predicted?.Kind} {predicted?.TargetId} at step {predicted?.Step}, not after the expiry step {expiryStep}"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1, context);

        AssertExpiresOnceAt(outcomes, expiryStep, expected, context);
    }

    /// <summary>
    /// data-model.md#projectile-step step 5's hit-beyond-range branch: a hit whose point is further than the
    /// class's range from Origin is not reported, and the projectile expires with Range in that step. Given
    /// <see cref="ProjectileWorlds.Flat"/> with the default <c>maxRangeM</c> 600, a shooter (ID 1) standing
    /// at (0, 0, −300) facing +Z, a standing target (ID 2) at (0, 0, 300.5) with yaw 32768, so its feet are
    /// 600.5 m downrange and its torso's near surface about 600.28 m from the shooter's eye, and one class-0
    /// in-flight projectile owned by 1 from the eye (0, 1.62, −300) at 600 m/s, launched at step 1 with the
    /// pitch the double closed form gives to meet the torso centre. The arrange finds the segment k that
    /// holds the predicted torso entry (k = hz at every rate: p(k) is about 599.98 m from Origin) and checks
    /// that the float |p(j) − Origin|² ≤ <c>MaxRangeSq[0]</c> for every swept end p(j) up to p(k), so no
    /// earlier no-hit Range expiry applies; that <see cref="ProjectileReference.PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{CharacterRecord}, long)"/>
    /// names step 1 + k with Kind Character, target 2 and Torso; and that the predicted point has
    /// |point − Origin|² &gt; <c>MaxRangeSq[0]</c>. When the world is stepped with <c>SimWorld.Step</c> until
    /// the projectile leaves the state, Then step 1 + k reports <c>events.expiries == [{1, Range}]</c>, no
    /// step reports a hit, no earlier step reports an expiry, and the projectile is in every committed
    /// state before step 1 + k and in none from it on.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void HitBeyondTheRangeExpiresWithRange(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = TestConfigs.Compiled(hz);
        Assert.True(
            config.MaxRangeSq[SmallArms] == 600f * 600f,
            FormattableString.Invariant($"{context}: MaxRangeSq[0] is {config.MaxRangeSq[SmallArms]:R}, expected the default 600²"));

        var shooter = ProjectileWorlds.Shooter(ShooterId, FarShooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(FarTargetId, FarTargetFeet, FacingMinusZ, Stance.Standing);
        var eye = ProjectileWorlds.Eye(FarShooterFeet, config, Stance.Standing);
        var torso = ProjectileReference.LayoutCapsules(Stance.Standing, FarTargetFeet, FacingMinusZ, config)[(int)HitboxPart.Torso];
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var direction = ProjectileWorlds.AimWithDrop(eye, torso.Centre, speed, config.HalfG[SmallArms]);
        var projectile = ProjectileWorlds.InFlight(
            ProjectileId,
            ShooterId,
            SmallArms,
            eye,
            ProjectileWorlds.LaunchVelocity(direction, speed),
            ArrangedStep,
            ArrangedStep,
            config);
        CharacterRecord[] characters = [shooter, target];
        var state = TestStates.Build(ArrangedStep, characters, [projectile]);

        var predicted = ProjectileReference.PredictFirstHit(ProjectileWorlds.Flat(), config, projectile, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Character, TargetId: FarTargetId, Hitbox: HitboxPart.Torso },
            FormattableString.Invariant($"{context}: the double path must meet target {FarTargetId}'s torso first, not {predicted}"));
        var k = predicted.K;
        Assert.True(
            k == (uint)hz,
            FormattableString.Invariant($"{context}: the torso entry is predicted in segment {k}; expected the segment one second after launch, k = {hz}"));

        var origin = AsVector(projectile.Origin);
        var launch = AsVector(projectile.LaunchVelocity);
        var maxRangeSq = config.MaxRangeSq[SmallArms];
        for (var j = FirstSweptK + 1u; j <= k; j++)
        {
            var end = ProjectileWorlds.ContractPosition(origin, launch, config.HalfG[SmallArms], hz, j);
            var rangeSq = RangeSquared(end, origin);
            Assert.True(
                rangeSq <= maxRangeSq,
                FormattableString.Invariant($"{context}: the float |p({j}) − Origin|² is {rangeSq:R}, over MaxRangeSq[0] {maxRangeSq:R}, so segment {j - 1} would expire with Range before the hit"));
        }

        var pointRangeSq = DVec3.LengthSquared(predicted.Point - origin);
        Assert.True(
            pointRangeSq > maxRangeSq,
            FormattableString.Invariant($"{context}: the predicted torso hit {predicted.Point} is {System.Math.Sqrt(pointRangeSq):R} m from Origin, not beyond the 600 m range"));

        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, state);
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1, context);

        AssertExpiresOnceAt(outcomes, predicted.Step, ExpiryReason.Range, context);
    }

    /// <summary>
    /// The configuration, launch and limits of a <see cref="LimitsApplyInContractOrder"/> row, all from
    /// <see cref="TestConfigs.Default"/> at its own step rate: the raw configuration with the row's
    /// override, the projectile's origin and launch velocity, the two limits one segment passes, and the
    /// reason the contract's order gives.
    /// </summary>
    private static (SimConfig Raw, Vec3 Origin, Vec3 Velocity, ExpiryReason LimitA, ExpiryReason LimitB, ExpiryReason Expected)
        ArrangeTwoLimitsInOneSegment(string caseName)
    {
        var defaults = CompiledConfig.Create(TestConfigs.Default());
        var hz = defaults.StepRateHz;
        switch (caseName)
        {
            case RangeAndLifetime:
                {
                    var origin = Eye(defaults);
                    var velocity = Launch(defaults, SmallArms, 10.0);
                    var k = FirstLimits(defaults, SmallArms, origin, velocity, FirstSweptK)[ExpiryReason.Range]
                        ?? throw new InvalidOperationException($"{caseName}: the default range is never passed within the default lifetime.");
                    var raw = TestConfigs.With(c => c.ProjectileClasses![SmallArms]!.LifetimeMs = LifetimeMsFor(k + 1u, hz));
                    return (raw, origin, velocity, ExpiryReason.Range, ExpiryReason.Lifetime, ExpiryReason.Range);
                }

            case LifetimeAndOutOfBounds:
                {
                    var k = FirstLimits(defaults, SmallArms, OutOfBoundsOrigin, OutOfBoundsVelocity, FirstSweptK)[ExpiryReason.OutOfBounds]
                        ?? throw new InvalidOperationException($"{caseName}: the flight never leaves ±8192 m within the default lifetime.");
                    var raw = TestConfigs.With(c =>
                    {
                        c.ProjectileClasses![SmallArms]!.MaxRangeM = ExtendedRange;
                        c.ProjectileClasses[SmallArms]!.LifetimeMs = LifetimeMsFor(k + 1u, hz);
                    });
                    return (raw, OutOfBoundsOrigin, OutOfBoundsVelocity, ExpiryReason.Lifetime, ExpiryReason.OutOfBounds, ExpiryReason.Lifetime);
                }

            case RangeAndOutOfBounds:
                {
                    var k = FirstLimits(defaults, SmallArms, OutOfBoundsOrigin, OutOfBoundsVelocity, FirstSweptK)[ExpiryReason.OutOfBounds]
                        ?? throw new InvalidOperationException($"{caseName}: the flight never leaves ±8192 m within the default lifetime.");
                    var halfG = defaults.HalfG[SmallArms];
                    var inside = ProjectileWorlds.ContractPosition(OutOfBoundsOrigin, OutOfBoundsVelocity, halfG, hz, k);
                    var outside = ProjectileWorlds.ContractPosition(OutOfBoundsOrigin, OutOfBoundsVelocity, halfG, hz, k + 1u);
                    var range = (float)System.Math.Round(
                        (System.Math.Sqrt(RangeSquared(inside, OutOfBoundsOrigin)) + System.Math.Sqrt(RangeSquared(outside, OutOfBoundsOrigin))) / 2.0);
                    var raw = TestConfigs.With(c => c.ProjectileClasses![SmallArms]!.MaxRangeM = range);
                    return (raw, OutOfBoundsOrigin, OutOfBoundsVelocity, ExpiryReason.Range, ExpiryReason.OutOfBounds, ExpiryReason.Range);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of OrderCases.");
        }
    }

    /// <summary>
    /// The <c>lifetimeMs</c> whose <c>ceilSteps</c> at <paramref name="hz"/> is exactly
    /// <paramref name="steps"/> (contracts/config-rules.md: <c>(ms·Hz + 999) / 1000</c>): ⌊steps·1000 / Hz⌋.
    /// </summary>
    private static int LifetimeMsFor(uint steps, int hz) => checked((int)(steps * 1000u / (uint)hz));

    /// <summary>
    /// For each limit, the first segment k from <paramref name="firstK"/> to <c>LifetimeSteps</c> − 1 that
    /// passes it on its own, in the contract's float expressions, or null when none does: Range when
    /// |p(k + 1) − Origin|² &gt; <c>MaxRangeSq</c>, Lifetime at k = <c>LifetimeSteps</c> − 1, and
    /// OutOfBounds when a coordinate of p(k + 1) is beyond ±8192 m.
    /// </summary>
    private static Dictionary<ExpiryReason, uint?> FirstLimits(CompiledConfig config, int classIndex, Vec3 origin, Vec3 velocity, uint firstK)
    {
        var lifetime = (uint)config.LifetimeSteps[classIndex];
        var limits = new Dictionary<ExpiryReason, uint?>
        {
            [ExpiryReason.Range] = null,
            [ExpiryReason.Lifetime] = lifetime >= firstK + 1u ? lifetime - 1u : null,
            [ExpiryReason.OutOfBounds] = null,
        };
        for (var k = firstK; k < lifetime; k++)
        {
            var b = ProjectileWorlds.ContractPosition(origin, velocity, config.HalfG[classIndex], config.StepRateHz, k + 1u);
            if (limits[ExpiryReason.Range] is null && RangeSquared(b, origin) > config.MaxRangeSq[classIndex])
            {
                limits[ExpiryReason.Range] = k;
            }

            if (limits[ExpiryReason.OutOfBounds] is null
                && (System.Math.Abs(b.X) > CoordinateLimit || System.Math.Abs(b.Y) > CoordinateLimit || System.Math.Abs(b.Z) > CoordinateLimit))
            {
                limits[ExpiryReason.OutOfBounds] = k;
            }
        }

        return limits;
    }

    /// <summary>
    /// Asserts that the run's only expiry is <c>{ProjectileId, reason}</c> at <paramref name="expiryStep"/>,
    /// that no step reports a hit, and that the projectile is in every committed state before that step and
    /// in none from it on.
    /// </summary>
    private static void AssertExpiresOnceAt(IReadOnlyList<StepOutcome> outcomes, uint expiryStep, ExpiryReason reason, string context)
    {
        Assert.True(
            outcomes[^1].Step >= expiryStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the expiry step {expiryStep}"));
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (expiry expected at step {expiryStep})");
            Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}]; expected none");
            if (outcome.Step < expiryStep)
            {
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] before the limit is passed");
                Assert.True(
                    outcome.ProjectileIds.SequenceEqual([ProjectileId]),
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}]; expected [{ProjectileId}] until the expiry step");
            }
            else if (outcome.Step == expiryStep)
            {
                Assert.True(
                    outcome.Expiries.SequenceEqual([new ExpiryEvent { ProjectileId = ProjectileId, Reason = reason }]),
                    $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}]; expected exactly [{{ ProjectileId = {ProjectileId}, Reason = {reason} }}]");
                Assert.True(outcome.ProjectileIds.Count == 0, $"{at}: the committed state still holds projectiles [{string.Join(", ", outcome.ProjectileIds)}]");
            }
            else
            {
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] after the projectile left");
                Assert.True(outcome.ProjectileIds.Count == 0, $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.ProjectileIds)}] after the expiry step");
            }
        }
    }

    /// <summary>
    /// The first segment k ≥ <paramref name="firstK"/> whose end b = p(k + 1) passes a limit, and the reason
    /// data-model.md#projectile-step step 6's order gives: Range when |b − Origin|² &gt; <c>MaxRangeSq</c>,
    /// else Lifetime when k + 1 ≥ <c>LifetimeSteps</c>, else OutOfBounds when a coordinate of b is beyond
    /// ±8192 m. Every k up to <c>LifetimeSteps</c> − 1 is checked, and the Lifetime limit applies there.
    /// </summary>
    private static (uint K, ExpiryReason Reason) ContractExpiry(CompiledConfig config, int classIndex, Vec3 origin, Vec3 velocity, uint firstK)
    {
        var lifetime = (uint)config.LifetimeSteps[classIndex];
        for (var k = firstK; k < lifetime; k++)
        {
            var b = ProjectileWorlds.ContractPosition(origin, velocity, config.HalfG[classIndex], config.StepRateHz, k + 1u);
            if (RangeSquared(b, origin) > config.MaxRangeSq[classIndex])
            {
                return (k, ExpiryReason.Range);
            }

            if (k + 1u >= lifetime)
            {
                return (k, ExpiryReason.Lifetime);
            }

            if (System.Math.Abs(b.X) > CoordinateLimit || System.Math.Abs(b.Y) > CoordinateLimit || System.Math.Abs(b.Z) > CoordinateLimit)
            {
                return (k, ExpiryReason.OutOfBounds);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(firstK), firstK, "The first swept segment is past the class's lifetime.");
    }

    /// <summary>
    /// The first segment k ≥ <paramref name="firstK"/>, below <c>LifetimeSteps</c>, whose end p(k + 1) has
    /// |p(k + 1) − Origin|² &gt; <c>MaxRangeSq</c> in the contract's float expressions; null when none does.
    /// </summary>
    private static uint? FirstRangeK(CompiledConfig config, int classIndex, Vec3 origin, Vec3 velocity, uint firstK)
    {
        var lifetime = (uint)config.LifetimeSteps[classIndex];
        for (var k = firstK; k < lifetime; k++)
        {
            var b = ProjectileWorlds.ContractPosition(origin, velocity, config.HalfG[classIndex], config.StepRateHz, k + 1u);
            if (RangeSquared(b, origin) > config.MaxRangeSq[classIndex])
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>|b − origin|² in float, as <c>((dx·dx) + (dy·dy)) + (dz·dz)</c>.</summary>
    private static float RangeSquared(Vec3 b, Vec3 origin)
    {
        var dx = b.X - origin.X;
        var dy = b.Y - origin.Y;
        var dz = b.Z - origin.Z;
        return ((dx * dx) + (dy * dy)) + (dz * dz);
    }

    /// <summary>
    /// Steps <paramref name="world"/> with no commands (every character gets the neutral input) until the
    /// committed state holds no projectile, then <see cref="StepsAfterLeaving"/> steps more, copying each
    /// step's hits, expiries and committed projectile IDs before the next step reuses the world's buffers.
    /// A fault, or a projectile still present after <paramref name="maxSteps"/> steps, fails the test.
    /// </summary>
    private static List<StepOutcome> RunUntilTheStateHoldsNoProjectile(SimWorld world, int maxSteps, string context)
    {
        var outcomes = new List<StepOutcome>();
        var leftAt = -1;
        for (var i = 0; i < maxSteps; i++)
        {
            var report = world.Step(ReadOnlySpan<CharacterCommand>.Empty);
            Assert.True(
                report.Fault.Code == StepFaultCode.None,
                FormattableString.Invariant($"{context}: the step after step {report.Step} faulted with {report.Fault.Code} at '{report.Fault.Path}'"));
            var committed = StateSerializer.ReadCommitted(report.State);
            var events = report.Events;
            outcomes.Add(new StepOutcome(
                report.Step,
                events.Hits.ToArray(),
                events.Expiries.ToArray(),
                committed.Projectiles.Select(p => p.Id).ToArray()));
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

    /// <summary>The owner's eye, standing at <see cref="ShooterFeet"/>: where its shots start.</summary>
    private static Vec3 Eye(CompiledConfig config) => ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);

    /// <summary>The class's default muzzle speed along heading +Z at <paramref name="pitchDegrees"/> above the horizon.</summary>
    private static Vec3 Launch(CompiledConfig config, int classIndex, double pitchDegrees) =>
        ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(0.0, pitchDegrees), ProjectileWorlds.MuzzleSpeed(config, classIndex));

    private static Vec3 AsVector((float X, float Y, float Z) vector) => new(vector.X, vector.Y, vector.Z);

    /// <summary>One committed step: its number, its hit and expiry events, and the committed projectile IDs.</summary>
    private sealed record StepOutcome(uint Step, IReadOnlyList<ProjectileHit> Hits, IReadOnlyList<ExpiryEvent> Expiries, IReadOnlyList<uint> ProjectileIds);
}
