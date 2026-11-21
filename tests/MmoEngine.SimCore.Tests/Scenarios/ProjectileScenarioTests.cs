using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// US5's flights end to end through <see cref="SimWorld"/> (FR-009, FR-010, FR-011): a projectile flies the
/// closed-form ballistic path to the bit and ends where the double-precision reference
/// (<see cref="ProjectileReference"/>) says it first meets the terrain, a mesh or a hitbox. Every expected
/// hit is first predicted by the reference, so no test can pass by comparing two missing hits. The named
/// projectile definitions (contracts/simcore-cli.md#named-scenario-streams) are built with
/// <c>Build(TestConfigs.At(hz), world, hz)</c>, unrecorded, over the committed
/// <c>simcore/content/test-world.mmcd</c> (opened read-only through <see cref="RepoPaths"/> and decoded with
/// <see cref="CollisionFile.Decode"/>), and stepped with <see cref="ScenarioRuns.Run(SimWorld, RecordedStream)"/>.
/// A shot fired by the weapon step is swept in the step it is fired, segment 0 included; its launch state is
/// read back from the shot event and the shooter's committed pose (<see cref="Launched"/>), because a shot
/// that hits in that step is in no committed state. <c>System.Math</c> is written in full, because
/// <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
public sealed class ProjectileScenarioTests
{
    private const uint ShooterId = 1;

    /// <summary>The target of <see cref="StruckPartsMatchTheStancesLayout"/>.</summary>
    private const uint TargetId = 2;

    /// <summary>The shooter's index in a state of the shooter and the target, which are in ascending ID.</summary>
    private const int ShooterIndex = 0;

    /// <summary>The target's index in a state of the shooter and the target.</summary>
    private const int TargetIndex = 1;

    private const uint ProjectileId = 1;

    /// <summary>The small-arms class, which the rifle in slot 0 fires.</summary>
    private const int SmallArms = 0;

    /// <summary>The heavy class, which the launcher in slot 1 fires.</summary>
    private const int Heavy = 1;

    /// <summary>The rifle's weapon slot.</summary>
    private const byte RifleSlot = 0;

    /// <summary>The launcher's weapon slot.</summary>
    private const byte LauncherSlot = 1;

    /// <summary>The step of the state a fired shot's world is reset to; the shooter fires in the next step.</summary>
    private const uint StateBeforeFire = 0;

    /// <summary>The step in which <see cref="StruckPartsMatchTheStancesLayout"/>'s shooter presses Fire.</summary>
    private const uint FireStep = 1;

    /// <summary>The bound on a hit point's distance from the struck capsule's surface.</summary>
    private const double SurfaceTolerance = 1e-4;

    /// <summary>Binary angle units per radian: 65536 to a turn, so 16384 is 90°.</summary>
    private const double UnitsPerRadian = 32768.0 / System.Math.PI;

    /// <summary>
    /// The arranged state's step, which is also the in-flight projectile's launch step, so the first step the
    /// world runs sweeps k = 1 (<see cref="ProjectileWorlds.InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>).
    /// </summary>
    private const uint ArrangedStep = 1;

    /// <summary>The first segment the world sweeps for the arranged projectile: step + 1 − launchStep.</summary>
    private const uint FirstSweptK = 1;

    /// <summary>Yaw 16384 faces +X, the direction of <see cref="HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt"/>'s shot.</summary>
    private const ushort FacingPlusX = 16384;

    /// <summary>How many steps after the projectile leaves the state are run to check it stays gone.</summary>
    private const int StepsAfterLeaving = 2;

    /// <summary>The bound on a stored position's distance from the double closed form, and on a ground hit's height.</summary>
    private const double PathTolerance = 1e-3;

    /// <summary>The bound on a hit point's distance from the point the double reference predicts.</summary>
    private const double HitPointTolerance = 1e-3;

    /// <summary>Yaw 32768 faces −Z: a target on the +Z side of the shooter faces it.</summary>
    private const ushort FacingMinusZ = 32768;

    /// <summary>The aimed shots of <c>projectile-hits-hitbox</c>: Head, Torso and Legs standing, then the same crouched.</summary>
    private const int AimedShots = 6;

    /// <summary>The index of <c>projectile-hits-hitbox</c>'s point-blank shot and hit, after the aimed ones.</summary>
    private const int PointBlankShot = AimedShots;

    /// <summary>A target 25 m away on <see cref="ProjectileWorlds.Flat"/>, aimed at its torso centre.</summary>
    private const string FlatPlacement = "flat";

    /// <summary>A target 25 m away behind <see cref="ProjectileWorlds.Wall"/>'s quad, aimed at its torso centre.</summary>
    private const string BehindWall = "behind-wall";

    /// <summary>A shot over <see cref="ProjectileWorlds.Ridge"/>'s crest that lands on the ground beyond it.</summary>
    private const string OverRidgeClear = "over-ridge-clear";

    /// <summary>A shot over <see cref="ProjectileWorlds.Ridge"/>'s crest that stays above the ground until its range runs out.</summary>
    private const string OverRidgeExpires = "over-ridge-expires";

    /// <summary>A shot aimed low at <see cref="ProjectileWorlds.Ridge"/>, which meets its front face.</summary>
    private const string OverRidgeBlocked = "over-ridge-blocked";

    /// <summary>A <c>shots-at-known-distances</c> ridge shot aimed 1 m below the crest, which meets the ridge face.</summary>
    private const string RidgeFace = "ridge-face";

    /// <summary>A <c>shots-at-known-distances</c> ridge shot aimed 1.5 m above the crest, which clears it.</summary>
    private const string RidgeClearing = "ridge-clearing";

    /// <summary>The target of the flat shots of <c>shots-at-known-distances</c>.</summary>
    private const uint StreamFlatTargetId = 2;

    /// <summary>The shooter's feet: at the origin, so its standing eye is the launch point (0, 1.62, 0).</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, 0f);

    /// <summary>The launch point: the eye height of a standing character above the origin.</summary>
    private static readonly Vec3 LaunchPoint = new(0f, 1.62f, 0f);

    /// <summary><see cref="StruckPartsMatchTheStancesLayout"/>'s target: 25 m in front of the shooter's feet, along +Z.</summary>
    private static readonly Vec3 TargetFeet = new(0f, 0f, 25f);

    /// <summary>The ridge rows' shooter: 124 m in front of <see cref="ProjectileWorlds.Ridge"/>'s crest, facing +Z.</summary>
    private static readonly Vec3 RidgeShooterFeet = new(0f, 0f, -100f);

    /// <summary>The character IDs of <c>shots-at-known-distances</c>: shooters 1, 3 and 5, and targets 2 and 4.</summary>
    private static readonly uint[] KnownDistanceCharacterIds = [1, 2, 3, 4, 5];

    /// <summary>
    /// The shots of <c>shots-at-known-distances</c>, in each shooter's firing order: shooter 1 (flat) and
    /// shooter 3 (behind the wall) fire one class-0 then one class-1 shot, and shooter 5 (ridge) fires a face
    /// shot then a clearing shot with class 0, then the same with class 1.
    /// </summary>
    private static readonly PlannedShot[] KnownDistanceShots =
    [
        new(1, SmallArms, FlatPlacement),
        new(1, Heavy, FlatPlacement),
        new(3, SmallArms, BehindWall),
        new(3, Heavy, BehindWall),
        new(5, SmallArms, RidgeFace),
        new(5, SmallArms, RidgeClearing),
        new(5, Heavy, RidgeFace),
        new(5, Heavy, RidgeClearing),
    ];

    /// <summary>
    /// The target yaws of <see cref="StruckPartsMatchTheStancesLayout"/>: facing away from the shooter (0, +Z),
    /// an angle off the quadrants (12345), side on (16384 and 49152) and facing it (32768).
    /// </summary>
    private static readonly ushort[] TargetYaws = [0, 12345, 16384, 32768, 49152];

    /// <summary>The three hitbox parts, in hitbox index order.</summary>
    private static readonly HitboxPart[] Parts = [HitboxPart.Head, HitboxPart.Torso, HitboxPart.Legs];

    private static readonly Lazy<CollisionWorld> CommittedTestWorld =
        new(() => CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd")));

    /// <summary>Rows <c>(classIndex, hz)</c>: class 0 (small arms) and class 1 (heavy), each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> Classes => TestRates.WithRateLast(0, 1);

    /// <summary>Rows <c>(stance, hz)</c> of <see cref="StruckPartsMatchTheStancesLayout"/>: Standing and Crouched, each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<Stance, int> StanceRows => TestRates.WithRateLast(Stance.Standing, Stance.Crouched);

    /// <summary>
    /// Rows <c>(classIndex, placement, hz)</c> of <see cref="ShotsAtKnownDistancesHitWhereTheClosedFormPathSays"/>:
    /// both classes on flat terrain and behind the wall, class 1 over the ridge and landing beyond it, class 0
    /// over the ridge until its range runs out, and both classes into the ridge's front face, each at 20, 30
    /// and 60 Hz.
    /// </summary>
    public static TheoryData<int, string, int> KnownDistanceRows => TestRates.WithRateLast<int, string>(
        (SmallArms, FlatPlacement),
        (Heavy, FlatPlacement),
        (SmallArms, BehindWall),
        (Heavy, BehindWall),
        (Heavy, OverRidgeClear),
        (SmallArms, OverRidgeExpires),
        (SmallArms, OverRidgeBlocked),
        (Heavy, OverRidgeBlocked));

    /// <summary>
    /// US5 scenario 1: Given <see cref="ProjectileWorlds.Flat"/>, its owner (ID 1) standing at the origin as
    /// the only character, so no target is in range, and one in-flight projectile of the row's class launched
    /// at step 1, level from (0, 1.62, 0) along +X at the class's default muzzle speed (600 / 120 m/s). The
    /// double reference first meets the terrain, at a ground point inside the heightfield (|x|, |z| below
    /// <see cref="ProjectileWorlds.GroundHalfExtent"/>) and within the class's <c>maxRangeM</c> of Origin
    /// (about 345 m for class 0 and 69 m for class 1), so the flight cannot end in a Range expiry without a
    /// terrain hit. When it is stepped with <c>SimWorld.Step</c> until it leaves the state, Then at every step
    /// n before the hit the committed state holds it alone with Position exactly the bits of the contract's
    /// closed form at k + 1 for k = n − 1, written out in the test
    /// (<see cref="ProjectileWorlds.ContractPosition"/>, not the core's <c>Ballistics.PositionAt</c>), and
    /// within 1e-3 m of the double closed form at k + 1;
    /// the step whose hit list holds it is exactly the first step whose segment p(k) → p(k + 1), evaluated in
    /// double, meets the heightfield (<see cref="ProjectileReference.SegmentHeightfield"/>), with Kind Terrain,
    /// Hitbox None and a point within 1e-3 m of height 0; no earlier step reports a hit; no step reports an
    /// expiry; and the projectile is in no committed state from the hit step on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt(int classIndex, int hz)
    {
        var context = FormattableString.Invariant($"class {classIndex}, {hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var flat = ProjectileWorlds.Flat();
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, FacingPlusX, Stance.Standing);
        var eye = ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);
        Assert.True(
            BitConverter.SingleToUInt32Bits(eye.Y) == BitConverter.SingleToUInt32Bits(LaunchPoint.Y),
            FormattableString.Invariant($"{context}: the shooter's eye {eye} must be the launch point {LaunchPoint}"));
        var speed = ProjectileWorlds.MuzzleSpeed(config, classIndex);
        var record = ProjectileWorlds.InFlight(
            ProjectileId,
            ShooterId,
            classIndex,
            LaunchPoint,
            ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(90.0, 0.0), speed),
            ArrangedStep,
            ArrangedStep,
            config);
        CharacterRecord[] characters = [shooter];

        var predicted = ProjectileReference.PredictFirstHit(flat, config, record, characters, FirstSweptK);
        Assert.True(
            predicted is { Kind: HitKind.Terrain },
            FormattableString.Invariant($"{context}: the double path must first meet the terrain, not {predicted}"));
        var range = (double)config.Config.ProjectileClasses![classIndex]!.MaxRangeM;
        var fromOrigin = DVec3.Distance(predicted.Point, LaunchPoint);
        Assert.True(
            System.Math.Abs(predicted.Point.X) < ProjectileWorlds.GroundHalfExtent
                && System.Math.Abs(predicted.Point.Z) < ProjectileWorlds.GroundHalfExtent
                && fromOrigin <= range,
            FormattableString.Invariant(
                $"{context}: the predicted ground point {predicted.Point} must lie inside the heightfield (|x|, |z| < {ProjectileWorlds.GroundHalfExtent:R}) and within maxRangeM {range:R} of Origin, not {fromOrigin:R} m away"));

        var crossingK = FirstHeightfieldSegment(flat, config, record, FirstSweptK, context).K;
        var hitStep = ArrangedStep + crossingK;
        Assert.True(
            predicted.Step == hitStep,
            FormattableString.Invariant($"{context}: the reference's first hit is at step {predicted.Step}, but the first segment that meets the heightfield is {crossingK} (step {hitStep})"));

        var halfG = config.HalfG[classIndex];
        var launch = ProjectileWorlds.ToProjectile(record);
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(ArrangedStep, characters, [record]));
        var outcomes = RunUntilTheStateHoldsNoProjectile(world, config.LifetimeSteps[classIndex] + StepsAfterLeaving + 1, commands: null, context);

        Assert.True(
            outcomes[^1].Step >= hitStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the terrain hit at step {hitStep}"));
        var found = false;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (terrain hit expected at step {hitStep})");
            Assert.True(
                outcome.Expiries.Count == 0,
                $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}]; the shot lands within its range");
            if (outcome.Step < hitStep)
            {
                Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] before the terrain hit");
                Assert.True(
                    outcome.Projectiles.Count == 1 && outcome.Projectiles[0].Id == ProjectileId,
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.Projectiles.Select(p => p.Id))}]; expected [{ProjectileId}] until the hit");
                var k = outcome.Step - launch.LaunchStep;
                var stored = outcome.Projectiles[0].Position;
                var contract = ProjectileWorlds.ContractPosition(launch.Origin, launch.LaunchVelocity, halfG, hz, k + 1u);
                Assert.True(
                    Bits(stored) == Bits(contract),
                    FormattableString.Invariant($"{at}: the stored Position {stored} (bits {Bits(stored)}) is not the contract's closed form ContractPosition({k + 1u}) {contract} (bits {Bits(contract)})"));
                var closedForm = ProjectileReference.ClosedForm(record, config, k + 1L);
                var offPath = DVec3.Distance(stored, closedForm);
                Assert.True(
                    offPath <= PathTolerance,
                    FormattableString.Invariant($"{at}: the stored Position {stored} is {offPath:R} m from the double closed form {closedForm} at k + 1 = {k + 1u}"));
            }
            else
            {
                Assert.True(
                    outcome.Projectiles.Count == 0,
                    $"{at}: the committed state holds projectiles [{string.Join(", ", outcome.Projectiles.Select(p => p.Id))}] from the hit step on");
                if (outcome.Step == hitStep)
                {
                    found = true;
                    Assert.True(
                        outcome.Hits.Count == 1,
                        $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}]; expected exactly one, the terrain hit");
                    var hit = outcome.Hits[0];
                    Assert.True(
                        hit.ProjectileId == ProjectileId && hit.Kind == HitKind.Terrain && hit.Hitbox == HitboxPart.None,
                        $"{at}: the hit is {hit}; expected projectile {ProjectileId} on Terrain with Hitbox None");
                    Assert.True(
                        System.Math.Abs(hit.Point.Y) <= PathTolerance,
                        FormattableString.Invariant($"{at}: the terrain hit point {hit.Point} is not within {PathTolerance:R} m of the ground, height 0"));
                }
                else
                {
                    Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] after the projectile left");
                }
            }
        }

        Assert.True(found, FormattableString.Invariant($"{context}: the run has no outcome for step {hitStep}"));
    }

    /// <summary>
    /// US5 scenario 1 through the <c>launcher-arc</c> definition: Given
    /// <c>ScenarioCatalog.Get("launcher-arc").Build(TestConfigs.At(hz), testWorld, hz)</c> over the committed
    /// <c>simcore/content/test-world.mmcd</c>, When its steps run on a <see cref="SimWorld"/>, Then the run
    /// fires exactly one shot, from slot 1, whose projectile the shot step's committed state holds with class 1,
    /// the shooter as its owner and the shot step as its LaunchStep. The first segment p(k) → p(k + 1) of the
    /// double closed form from that projectile's stored Origin and LaunchVelocity that meets the heightfield
    /// (<see cref="ProjectileReference.SegmentHeightfield"/>) gives the expected step LaunchStep + k and
    /// triangle ID; the arrange checks that the whole reference (<see cref="ProjectileReference.PredictFirstHit(CollisionData, CompiledConfig, ReferenceProjectile, IReadOnlyList{CharacterState}, long)"/>)
    /// meets that same terrain triangle at that step first, nothing before it, within the class's
    /// <c>maxRangeM</c> of Origin, and that the stream runs to that step. Then at every step from the launch
    /// step to the one before the hit, the committed state holds the projectile with its launch Origin,
    /// LaunchVelocity and LaunchStep and with Position exactly the bits of the contract's closed form at
    /// k + 1 from the launch Origin and LaunchVelocity (<see cref="ProjectileWorlds.ContractPosition"/>, written
    /// out in the test rather than the core's <c>Ballistics.PositionAt</c>) for k = step − LaunchStep; the run's only hit is at the expected step, on that projectile, with Kind
    /// Terrain, that triangle ID as its target ID and Hitbox None; the projectile is in no committed state from
    /// that step on; and no step reports an expiry.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void LauncherArcMeetsTheTerrainAtTheClosedFormStep(int hz)
    {
        var context = FormattableString.Invariant($"launcher-arc, {hz} Hz");
        var testWorld = CommittedTestWorld.Value;
        var collisionData = testWorld.Data;
        var stream = ScenarioCatalog.Get("launcher-arc").Build(TestConfigs.At(hz), testWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        Assert.True(stream.StepHz == hz, FormattableString.Invariant($"{context}: the stream runs at {stream.StepHz} Hz"));

        var run = ScenarioRuns.Run(new SimWorld(config, testWorld), stream);

        AssertNoStepFaulted(run, context);
        var shots = ShotsOf(run);
        Assert.True(
            shots.Length == 1 && shots[0].Shot.Slot == LauncherSlot,
            FormattableString.Invariant($"{context}: the run fired [{string.Join(", ", shots.Select(s => $"step {s.Step.Step}: {s.Shot}"))}]; expected exactly one shot, from slot {LauncherSlot}"));
        var (shotStep, shot) = shots[0];
        var spawned = shotStep.Projectiles.Where(p => p.Id == shot.ProjectileId).ToArray();
        Assert.True(
            spawned.Length == 1 && spawned[0].Class == Heavy && spawned[0].OwnerId == shot.CharacterId && spawned[0].LaunchStep == shotStep.Step,
            FormattableString.Invariant(
                $"{context}: the committed state of the shot step {shotStep.Step} holds [{string.Join(", ", spawned)}]; expected projectile {shot.ProjectileId} of class {Heavy}, owned by {shot.CharacterId}, launched at that step"));
        var launch = spawned[0];

        var (crossingK, triangleId) = FirstHeightfieldSegment(collisionData, config, launch, 0u, context);
        var hitStep = launch.LaunchStep + crossingK;
        var predicted = ProjectileReference.PredictFirstHit(collisionData, config, launch, run.InitialCharacters, 0);
        Assert.True(
            predicted is { Kind: HitKind.Terrain } && predicted.Step == hitStep && predicted.TargetId == (uint)triangleId,
            FormattableString.Invariant(
                $"{context}: the double path must meet the terrain first, at step {hitStep} on triangle {triangleId} where it first meets the heightfield, not {predicted}"));
        var range = (double)config.Config.ProjectileClasses![Heavy]!.MaxRangeM;
        var fromOrigin = DVec3.Distance(predicted.Point, launch.Origin);
        Assert.True(
            fromOrigin <= range,
            FormattableString.Invariant($"{context}: the predicted ground point {predicted.Point} is {fromOrigin:R} m from Origin, beyond maxRangeM {range:R}"));
        Assert.True(
            run.Last.Step >= hitStep,
            FormattableString.Invariant($"{context}: the stream ends at step {run.Last.Step}, before the terrain hit at step {hitStep}"));

        var halfG = config.HalfG[Heavy];
        var allHits = run.Steps.SelectMany(step => step.Hits).ToArray();
        Assert.True(
            allHits.Length == 1,
            FormattableString.Invariant($"{context}: the run reported hits [{string.Join(", ", allHits)}]; expected exactly one, the terrain hit at step {hitStep}"));
        foreach (var step in run.Steps)
        {
            var at = FormattableString.Invariant($"{context}, step {step.Step} (terrain hit expected at step {hitStep})");
            Assert.True(step.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", step.Expiries)}]; the arc lands within its range");
            var held = step.Projectiles.Where(p => p.Id == launch.Id).ToArray();
            if (step.Step < launch.LaunchStep)
            {
                continue;
            }

            if (step.Step < hitStep)
            {
                Assert.True(step.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", step.Hits)}] before the terrain hit");
                Assert.True(held.Length == 1, $"{at}: the committed state holds [{string.Join(", ", held)}]; expected projectile {launch.Id} until the hit");
                var stored = held[0];
                Assert.True(
                    Bits(stored.Origin) == Bits(launch.Origin) && Bits(stored.LaunchVelocity) == Bits(launch.LaunchVelocity) && stored.LaunchStep == launch.LaunchStep,
                    $"{at}: the stored projectile {stored} no longer has the launch Origin, LaunchVelocity and LaunchStep of {launch}");
                var k = step.Step - stored.LaunchStep;
                var contract = ProjectileWorlds.ContractPosition(launch.Origin, launch.LaunchVelocity, halfG, hz, k + 1u);
                Assert.True(
                    Bits(stored.Position) == Bits(contract),
                    FormattableString.Invariant($"{at}: the stored Position {stored.Position} (bits {Bits(stored.Position)}) is not the contract's closed form ContractPosition({k + 1u}) {contract} (bits {Bits(contract)})"));
                continue;
            }

            Assert.True(held.Length == 0, $"{at}: the committed state holds [{string.Join(", ", held)}] from the hit step on");
            if (step.Step == hitStep)
            {
                Assert.True(step.Hits.Count == 1, $"{at}: the step reported hits [{string.Join(", ", step.Hits)}]; expected exactly the terrain hit");
                var hit = step.Hits[0];
                Assert.True(
                    hit.ProjectileId == launch.Id && hit.Kind == HitKind.Terrain && hit.TargetId == (uint)triangleId && hit.Hitbox == HitboxPart.None,
                    FormattableString.Invariant($"{at}: the hit is {hit}; expected projectile {launch.Id} on Terrain, triangle {triangleId}, Hitbox None"));
            }
        }
    }

    /// <summary>
    /// US5 scenario 3 through the <c>mesh-occludes-shot</c> definition: Given
    /// <c>ScenarioCatalog.Get("mesh-occludes-shot").Build(TestConfigs.At(hz), testWorld, hz)</c> over the
    /// committed <c>simcore/content/test-world.mmcd</c>, whose initial state holds two characters, one of which
    /// fires every shot (the shooter) while the other (the target) fires none, When its steps run on a
    /// <see cref="SimWorld"/>, Then the run fires at least one shot, and for each shot the arrange checks the
    /// Given on the shot itself: with the wall mesh (<see cref="TestWorldFeatures.WallMeshId"/>) taken out of the
    /// collision data, the double reference flown from the shot's launch state meets the target first, so the
    /// wall is what stands between the muzzle and the target. Then each shot's projectile is in exactly one hit
    /// event, the run holds no other hit, and every hit has Kind Mesh, a target ID among the wall mesh's
    /// triangle IDs (contracts/collision-data-format.md#triangle-ids) and Hitbox None; no hit names the
    /// target's character ID.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void MeshOccludesShotHitsTheWallAndNeverTheTarget(int hz)
    {
        var context = FormattableString.Invariant($"mesh-occludes-shot, {hz} Hz");
        var testWorld = CommittedTestWorld.Value;
        var collisionData = testWorld.Data;
        var wallMeshId = TestWorldGenerator.Features.WallMeshId;
        var wallTriangles = MeshTriangleIds(collisionData, wallMeshId);
        Assert.True(wallTriangles.Count > 0, FormattableString.Invariant($"{context}: the test world has no triangle in wall mesh {wallMeshId}"));
        var withoutWall = collisionData with { Meshes = collisionData.Meshes.Where(mesh => mesh.MeshId != wallMeshId).ToArray() };
        var stream = ScenarioCatalog.Get("mesh-occludes-shot").Build(TestConfigs.At(hz), testWorld, hz);
        var config = CompiledConfig.Create(stream.Config);

        var run = ScenarioRuns.Run(new SimWorld(config, testWorld), stream);

        AssertNoStepFaulted(run, context);
        var shots = ShotsOf(run);
        Assert.True(shots.Length >= 1, $"{context}: the run fired no shot");
        var shooterIds = shots.Select(s => s.Shot.CharacterId).Distinct().ToArray();
        var others = run.InitialCharacters.Where(c => !shooterIds.Contains(c.Id)).ToArray();
        Assert.True(
            run.InitialCharacters.Count == 2 && shooterIds.Length == 1 && others.Length == 1,
            FormattableString.Invariant(
                $"{context}: the initial state holds characters [{string.Join(", ", run.InitialCharacters.Select(c => c.Id))}] and shots come from [{string.Join(", ", shooterIds)}]; expected two characters, one shooter and one target"));
        var targetId = others[0].Id;

        var allHits = run.Steps.SelectMany(step => step.Hits).ToArray();
        foreach (var (shotStep, shot) in shots)
        {
            var at = FormattableString.Invariant($"{context}, projectile {shot.ProjectileId} fired at step {shotStep.Step}");
            var launch = Launched(shotStep.Characters, shotStep.Step, shot, config, shotStep.Projectiles, at);
            var unoccluded = ProjectileReference.PredictFirstHit(withoutWall, config, launch, shotStep.Characters, 0);
            Assert.True(
                unoccluded is { Kind: HitKind.Character } && unoccluded.TargetId == targetId,
                FormattableString.Invariant($"{at}: without wall mesh {wallMeshId} the double path must meet target {targetId} first, not {unoccluded}, or the wall does not stand between them"));
            var forShot = allHits.Where(hit => hit.ProjectileId == shot.ProjectileId).ToArray();
            Assert.True(
                forShot.Length == 1,
                $"{at}: the run holds hits [{string.Join(", ", forShot)}] for the projectile; expected exactly one");
        }

        Assert.True(
            allHits.Length == shots.Length,
            FormattableString.Invariant($"{context}: the run holds {allHits.Length} hits [{string.Join(", ", allHits)}] for {shots.Length} shots; expected one hit per shot and no other"));
        foreach (var hit in allHits)
        {
            Assert.True(
                hit.Kind == HitKind.Mesh && wallTriangles.Contains(checked((int)hit.TargetId)) && hit.Hitbox == HitboxPart.None,
                FormattableString.Invariant(
                    $"{context}: the hit {hit} is not on the wall; expected Kind Mesh, a target ID among wall mesh {wallMeshId}'s triangles [{string.Join(", ", wallTriangles)}] and Hitbox None"));
            Assert.True(
                hit.TargetId != targetId,
                FormattableString.Invariant($"{context}: the hit {hit} names the target's character ID {targetId}"));
        }
    }

    /// <summary>
    /// US5 scenario 5, the layout half (the bit-for-bit half across builds is Phase 11's): for each of the
    /// target yaws 0, 12345, 16384, 32768 and 49152 and each part, Given <see cref="ProjectileWorlds.Flat"/>, a
    /// configuration whose rifle (<c>weapons[0]</c>) has <c>spreadHalfAngleDeg</c> 0, a shooter (ID 1)
    /// standing at the origin facing +Z with slot 0 Ready, and a target (ID 2) with the row's stance and that
    /// yaw standing 25 m in front of it at (0, 0, 25), holding its stance and yaw every step (Crouch held for
    /// Crouched), from a state at step 0, When the shooter presses Fire in step 1 aimed at the part's configured
    /// centre (the midpoint of the stance's A and B from contracts/config-rules.md rotated by the yaw,
    /// <see cref="ProjectileReference.LayoutCapsules(Stance, DVec3, ushort, CompiledConfig)"/>, with the yaw and
    /// pitch, in binary angle units, nearest the low-root direction of <see cref="ProjectileWorlds.AimWithDrop"/>)
    /// and the world is stepped until the projectile leaves the state, Then the run fires exactly that one
    /// class-0 shot. The weapon step sweeps the shot in step 1, segment 0 included, so the target can be 25 m
    /// away at every rate. The double reference flown from the shot's launch state meets target 2 first (checked
    /// before the outcome), and the part it meets first is the part <c>LayoutCapsules</c> puts first along the
    /// line. At that reference's step, the step reports exactly one hit, on the shot's projectile, with Kind
    /// Character, target 2 and that part as its Hitbox, and the hit point's double distance to that reference
    /// capsule's axis segment equals the configured radius within 1e-4 m. No earlier step reports a hit or an
    /// expiry, the projectile is in every committed state before that step and in none from it on, and the
    /// target holds its arranged pose throughout. Across the yaw rows of the stance, every one of Head, Torso
    /// and Legs is struck at least once.
    /// </summary>
    [Theory]
    [MemberData(nameof(StanceRows))]
    public void StruckPartsMatchTheStancesLayout(Stance stance, int hz)
    {
        var context = FormattableString.Invariant($"{stance} target, {hz} Hz");
        var raw = TestConfigs.At(hz);
        raw.Weapons![RifleSlot]!.SpreadHalfAngleDeg = 0f;
        var config = CompiledConfig.Create(raw);
        var rifle = config.Weapons[RifleSlot];
        Assert.True(
            BitConverter.SingleToUInt32Bits(rifle.VersMax) == 0u && rifle.ProjectileClass == SmallArms,
            FormattableString.Invariant($"{context}: the rifle must fire class {SmallArms} with no spread; it fires class {rifle.ProjectileClass} with VersMax {rifle.VersMax:R}"));
        var flat = ProjectileWorlds.Flat();

        var struck = new HashSet<HitboxPart>();
        foreach (var yaw in TargetYaws)
        {
            foreach (var part in Parts)
            {
                struck.Add(StrikeAimedPart(config, flat, stance, yaw, part, context));
            }
        }

        Assert.True(
            struck.SetEquals(Parts),
            FormattableString.Invariant(
                $"{context}: across the target yaws [{string.Join(", ", TargetYaws)}] the shots struck [{string.Join(", ", struck.Order())}]; every one of Head, Torso and Legs must be struck at least once"));
    }

    /// <summary>
    /// One shot of <see cref="StruckPartsMatchTheStancesLayout"/>: fires at the centre of
    /// <paramref name="part"/> of a <paramref name="stance"/> target 25 m away with yaw
    /// <paramref name="targetYaw"/>, asserts the outcome that test states, and returns the struck part.
    /// </summary>
    private static HitboxPart StrikeAimedPart(CompiledConfig config, CollisionData flat, Stance stance, ushort targetYaw, HitboxPart part, string context)
    {
        var aimed = FormattableString.Invariant($"{context}, target yaw {targetYaw}, aimed at the {part} centre");
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet, yaw: 0, Stance.Standing);
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, targetYaw, stance);
        var centre = ProjectileReference.LayoutCapsules(stance, TargetFeet, targetYaw, config)[(int)part].Centre;
        var eye = ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);
        var speed = ProjectileWorlds.MuzzleSpeed(config, SmallArms);
        var (aimYaw, aimPitch) = BinaryAim(ProjectileWorlds.AimWithDrop(eye, centre, speed, config.HalfG[SmallArms]));
        CharacterRecord[] characters = [shooter, target];
        var world = ProjectileWorlds.Start(ProjectileWorlds.FlatWorld, config, TestStates.Build(StateBeforeFire, characters));

        var outcomes = RunUntilTheStateHoldsNoProjectile(
            world,
            config.LifetimeSteps[SmallArms] + StepsAfterLeaving + 1,
            FireOnceAndHold(aimYaw, aimPitch, stance, targetYaw),
            aimed);

        var shots = outcomes.SelectMany(outcome => outcome.Shots.Select(fired => (outcome.Step, Shot: fired))).ToArray();
        Assert.True(
            shots.Length == 1 && shots[0].Step == FireStep && shots[0].Shot.CharacterId == ShooterId && shots[0].Shot.Slot == RifleSlot,
            FormattableString.Invariant(
                $"{aimed}: the run fired [{string.Join(", ", shots.Select(s => $"step {s.Step}: {s.Shot}"))}]; expected exactly one shot, by character {ShooterId} from slot {RifleSlot} at step {FireStep}"));
        var shot = shots[0].Shot;
        var fireOutcome = outcomes[0];
        Assert.True(fireOutcome.Step == FireStep, FormattableString.Invariant($"{aimed}: the run's first step is {fireOutcome.Step}, not {FireStep}"));
        var launch = Launched(fireOutcome.Characters, FireStep, shot, config, fireOutcome.Projectiles, aimed);
        var predicted = ProjectileReference.PredictFirstHit(flat, config, launch, characters, 0);
        Assert.True(
            predicted is { Kind: HitKind.Character, TargetId: TargetId },
            FormattableString.Invariant($"{aimed}: the double path of the shot (yaw {aimYaw}, pitch {aimPitch}) must meet target {TargetId} first, not {predicted}"));
        var expected = predicted.Hitbox;

        Assert.True(
            outcomes[^1].Step >= predicted.Step,
            FormattableString.Invariant($"{aimed}: the run ended at step {outcomes[^1].Step}, before the predicted hit step {predicted.Step}"));
        ProjectileHit? found = null;
        CharacterState committedTarget = default;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{aimed}, step {outcome.Step} (hit expected at step {predicted.Step})");
            var held = outcome.Projectiles.Where(p => p.Id == shot.ProjectileId).ToArray();
            if (outcome.Step < predicted.Step)
            {
                Assert.True(outcome.Hits.Count == 0, $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}] before the predicted hit");
                Assert.True(outcome.Expiries.Count == 0, $"{at}: the step reported expiries [{string.Join(", ", outcome.Expiries)}] before the predicted hit");
                Assert.True(held.Length == 1, $"{at}: the committed state holds [{string.Join(", ", held)}]; expected projectile {shot.ProjectileId} until the hit");
                AssertHoldsItsPose(outcome, target, at);
                continue;
            }

            Assert.True(held.Length == 0, $"{at}: the committed state holds [{string.Join(", ", held)}] from the hit step on");
            if (outcome.Step == predicted.Step)
            {
                committedTarget = AssertHoldsItsPose(outcome, target, at);
                Assert.True(
                    outcome.Hits.Count == 1,
                    $"{at}: the step reported hits [{string.Join(", ", outcome.Hits)}]; expected exactly one, on target {TargetId}'s {expected}");
                found = outcome.Hits[0];
            }
        }

        Assert.True(found is not null, FormattableString.Invariant($"{aimed}: the run has no outcome for step {predicted.Step}"));
        var hit = found.Value;
        Assert.True(
            hit.ProjectileId == shot.ProjectileId && hit.Kind == HitKind.Character && hit.TargetId == TargetId && hit.Hitbox == expected,
            FormattableString.Invariant(
                $"{aimed}: the hit is {hit}; expected projectile {shot.ProjectileId} on Character {TargetId}, {expected}, the part the configured layout puts first along the line"));
        var capsule = ProjectileReference.LayoutCapsules(stance, committedTarget.Position, committedTarget.AimYaw, config)[(int)expected];
        var distance = ProjectileReference.DistanceToSegment(hit.Point, capsule.A, capsule.B);
        Assert.True(
            System.Math.Abs(distance - capsule.Radius) <= SurfaceTolerance,
            FormattableString.Invariant(
                $"{aimed}: the hit point {hit.Point} is {distance:R} m from the reference {expected} axis {capsule.A} – {capsule.B}; expected the configured radius {capsule.Radius:R} within {SurfaceTolerance:R} m"));
        return hit.Hitbox;
    }

    /// <summary>
    /// The commands of <see cref="StruckPartsMatchTheStancesLayout"/>: in <see cref="FireStep"/> the shooter
    /// presses Fire with slot 0 and the given aim; in every step the target holds <paramref name="stance"/>
    /// (Crouch for Crouched, no button for Standing) and <paramref name="targetYaw"/>, so its post-movement
    /// pose is the arranged one. The shooter has no command after <see cref="FireStep"/>, so the neutral input
    /// keeps its aim and releases the trigger.
    /// </summary>
    private static Func<uint, CharacterCommand[]> FireOnceAndHold(ushort aimYaw, short aimPitch, Stance stance, ushort targetYaw)
    {
        var hold = stance == Stance.Crouched ? Buttons.Crouch : default;
        return step =>
        {
            var targetCommand = new CharacterCommand(TargetIndex, new InputCommand(step, step, hold, targetYaw, 0, 0));
            return step == FireStep
                ? [new CharacterCommand(ShooterIndex, new InputCommand(FireStep, FireStep, Buttons.Fire, aimYaw, aimPitch, RifleSlot)), targetCommand]
                : [targetCommand];
        };
    }

    /// <summary>
    /// US5 scenarios 2 and 6 and the point-blank edge case through the <c>projectile-hits-hitbox</c>
    /// definition: Given <c>ScenarioCatalog.Get("projectile-hits-hitbox").Build(TestConfigs.At(hz), testWorld, hz)</c>
    /// over the committed <c>simcore/content/test-world.mmcd</c>, whose initial state holds two characters,
    /// one of which fires every shot (the shooter) while the other (the target) fires none, When its steps run
    /// on a <see cref="SimWorld"/>, Then the run fires its seven planned shots, reports no expiry, and its hit
    /// events in step order are exactly seven hits, one on each shot's projectile in firing order, every one
    /// with Kind Character and the target's ID, so no hit names the shooter. The first six are the aimed shots:
    /// they strike Head, Torso and Legs while the target stands, then Head, Torso and Legs while it crouches
    /// (its committed stance at the hit step). Each aimed hit has 0 &lt; T &lt; 1, and both ends of its
    /// striking segment p(k) → p(k + 1), for k = hit step − LaunchStep in the contract's float closed form
    /// from the shot's launch state, lie outside the target's bounding sphere at the hit step (its committed
    /// feet plus <c>BoundCenterUp</c>, radius <c>BoundRadius</c>), so the projectile crosses a whole hitbox
    /// within one step. The seventh is the point-blank shot, which starts inside one of the target's hitboxes:
    /// its hit is reported in the step its shot fires, with T bits 0x00000000.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ProjectileHitsHitboxStrikesEveryPlannedPart(int hz)
    {
        var context = FormattableString.Invariant($"projectile-hits-hitbox, {hz} Hz");
        var testWorld = CommittedTestWorld.Value;
        var stream = ScenarioCatalog.Get("projectile-hits-hitbox").Build(TestConfigs.At(hz), testWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        Assert.True(stream.StepHz == hz, FormattableString.Invariant($"{context}: the stream runs at {stream.StepHz} Hz"));

        var run = ScenarioRuns.Run(new SimWorld(config, testWorld), stream);

        AssertNoStepFaulted(run, context);
        var shots = ShotsOf(run);
        var (shooterId, targetId) = ShooterAndTarget(run, shots, context);
        var hits = HitsOf(run);
        var expiries = run.Steps.SelectMany(step => step.Expiries).ToArray();
        Assert.True(
            hits.All(h => h.Hit.Kind != HitKind.Character || h.Hit.TargetId != shooterId),
            FormattableString.Invariant($"{context}: the run reported hits [{string.Join(", ", hits.Select(h => h.Hit))}]; one names the shooter {shooterId}"));
        Assert.True(
            shots.Length == AimedShots + 1,
            FormattableString.Invariant(
                $"{context}: the run fired [{string.Join(", ", shots.Select(s => $"step {s.Step.Step}: {s.Shot}"))}]; expected {AimedShots + 1} shots, six aimed and one point-blank"));
        Assert.True(
            hits.Length == AimedShots + 1,
            FormattableString.Invariant(
                $"{context}: the run reported hits [{string.Join(", ", hits.Select(h => $"step {h.Step.Step}: {h.Hit}"))}]; expected exactly {AimedShots + 1}, one per shot"));
        Assert.True(expiries.Length == 0, $"{context}: the run reported expiries [{string.Join(", ", expiries)}]; every shot hits the target");

        for (var i = 0; i < hits.Length; i++)
        {
            var (shotStep, shot) = shots[i];
            var (hitStep, hit) = hits[i];
            var at = FormattableString.Invariant($"{context}, hit {i} at step {hitStep.Step} (shot fired at step {shotStep.Step})");
            Assert.True(
                hit.ProjectileId == shot.ProjectileId && hit.Kind == HitKind.Character && hit.TargetId == targetId,
                FormattableString.Invariant($"{at}: the hit is {hit}; expected the shot's projectile {shot.ProjectileId} on Character {targetId}"));
            if (i == PointBlankShot)
            {
                Assert.True(
                    hitStep.Step == shotStep.Step && BitConverter.SingleToUInt32Bits(hit.T) == 0u,
                    FormattableString.Invariant(
                        $"{at}: the point-blank hit {hit} (T bits {BitConverter.SingleToUInt32Bits(hit.T):x8}) must be reported in its shot's step {shotStep.Step} with T bits 00000000"));
                continue;
            }

            var stance = i < Parts.Length ? Stance.Standing : Stance.Crouched;
            var part = Parts[i % Parts.Length];
            var committedTarget = CommittedCharacter(hitStep.Characters, targetId, at);
            Assert.True(
                hit.Hitbox == part && committedTarget.Stance == stance,
                FormattableString.Invariant($"{at}: the hit struck {hit.Hitbox} of a {committedTarget.Stance} target; expected {part} while it is {stance}"));
            Assert.True(
                0f < hit.T && hit.T < 1f,
                FormattableString.Invariant($"{at}: the aimed hit's T is {hit.T:R}; expected 0 < T < 1"));

            var launch = Launched(shotStep.Characters, shotStep.Step, shot, config, shotStep.Projectiles, at);
            var k = hitStep.Step - launch.LaunchStep;
            var halfG = config.HalfG[launch.Class];
            var a = ProjectileWorlds.ContractPosition(launch.Origin, launch.LaunchVelocity, halfG, hz, k);
            var b = ProjectileWorlds.ContractPosition(launch.Origin, launch.LaunchVelocity, halfG, hz, k + 1u);
            var layout = config.HitboxLayouts[(int)committedTarget.Stance];
            var feet = committedTarget.Position;
            var boundCentre = new DVec3(feet.X, feet.Y + (double)layout.BoundCenterUp, feet.Z);
            foreach (var (name, end) in new[] { ("start", a), ("end", b) })
            {
                var fromBound = DVec3.Distance(end, boundCentre);
                Assert.True(
                    fromBound > layout.BoundRadius,
                    FormattableString.Invariant(
                        $"{at}: the striking segment {k}'s {name} {end} is {fromBound:R} m from the target's bounding sphere centre {boundCentre}, inside its radius {layout.BoundRadius:R}"));
            }
        }
    }

    /// <summary>
    /// US5's Independent Test, in process: Given the row's world, a configuration whose weapons both have
    /// <c>spreadHalfAngleDeg</c> 0, and a shooter (ID 1) standing at rest with the row's class's slot active
    /// and Ready, from a state at step 0, When the shooter presses Fire in step 1 with the binary-angle yaw
    /// and pitch nearest the row's direction and the world is stepped until the projectile leaves the state,
    /// Then the run fires exactly that one shot, of the row's class at its default muzzle speed, from the
    /// standing shooter's eye (1.62 m above its feet). The weapon step sweeps the shot in step 1, segment 0
    /// included, so a 25 m target is reached at every rate. The rows:
    /// <list type="bullet">
    /// <item><c>flat</c>, both classes: <see cref="ProjectileWorlds.Flat"/>, the shooter at the origin facing
    /// +Z and a target (ID 2) standing 25 m away with yaw 32768, aimed at its torso centre with the class's
    /// drop compensated by the low-root pitch of the double closed form (<see cref="ProjectileWorlds.AimWithDrop"/>);</item>
    /// <item><c>behind-wall</c>, both classes: <see cref="ProjectileWorlds.Wall"/> and the same shooter,
    /// target and aim, so the wall at z = 12.5 m stands in the way;</item>
    /// <item><c>over-ridge-clear</c>, class 1: <see cref="ProjectileWorlds.Ridge"/>, the shooter at
    /// (0, 0, −100) facing +Z, pitch 3.5°, which clears the crest and lands on the flat ground beyond it;</item>
    /// <item><c>over-ridge-expires</c>, class 0: the same, pitch 1.5°, which clears the crest and stays above
    /// the ground until its range runs out;</item>
    /// <item><c>over-ridge-blocked</c>, both classes: the same, aimed low (0.5° for class 0, 2.0° for class 1),
    /// which meets the ridge's front face.</item>
    /// </list>
    /// The arrange asserts that the double reference flown from the shot's launch state predicts the row's
    /// outcome, so no row can pass by comparing two missing hits: Character (target 2, Torso) for <c>flat</c>;
    /// Mesh on one of <see cref="ProjectileWorlds.WallTriangleIds"/> for <c>behind-wall</c>; Terrain at a z in
    /// (<see cref="ProjectileWorlds.RidgeCrestZ"/> + 4, <see cref="ProjectileWorlds.GroundHalfExtent"/>)
    /// within <c>maxRangeM</c> of Origin for <c>over-ridge-clear</c>; Terrain at a z in [20,
    /// <see cref="ProjectileWorlds.RidgeCrestZ"/>] for <c>over-ridge-blocked</c>; and no hit at or before the
    /// Range step for <c>over-ridge-expires</c>. For the two ridge-clearing rows the double closed form is
    /// also above the ridge surface at z = 20, 24 and 28 m. Then, for the hit rows, the hit's kind, target ID
    /// (character or triangle) and hitbox equal the prediction's, its point is within 1e-3 m of the predicted
    /// point and its step is the predicted step; no hit or expiry event holds the projectile at another step,
    /// and no committed state holds it from that step on. For <c>over-ridge-expires</c>, no hit event ever
    /// holds the projectile, and the expiry events hold it exactly once, as {projectile, Range}, at the step
    /// that sweeps p(k) → p(k + 1) for the first k with |p(k + 1) − Origin|² &gt; <c>MaxRangeSq[0]</c> in the
    /// contract's float expressions.
    /// </summary>
    [Theory]
    [MemberData(nameof(KnownDistanceRows))]
    public void ShotsAtKnownDistancesHitWhereTheClosedFormPathSays(int classIndex, string placement, int hz)
    {
        var context = FormattableString.Invariant($"class {classIndex} {placement}, {hz} Hz");
        var raw = TestConfigs.At(hz);
        foreach (var weapon in raw.Weapons!)
        {
            weapon!.SpreadHalfAngleDeg = 0f;
        }

        var config = CompiledConfig.Create(raw);
        var slot = SlotFiring(config, classIndex);
        Assert.True(
            BitConverter.SingleToUInt32Bits(config.Weapons[slot].VersMax) == 0u,
            FormattableString.Invariant($"{context}: slot {slot} must fire with no spread; its VersMax is {config.Weapons[slot].VersMax:R}"));
        var placed = Place(placement, classIndex, config);
        var shooter = ProjectileWorlds.Shooter(ShooterId, placed.ShooterFeet, yaw: 0, Stance.Standing) with { ActiveSlot = slot };
        CharacterRecord[] characters = placed.Target is { } target ? [shooter, target] : [shooter];
        var world = ProjectileWorlds.Start(placed.World, config, TestStates.Build(StateBeforeFire, characters));

        var outcomes = RunUntilTheStateHoldsNoProjectile(
            world,
            config.LifetimeSteps[classIndex] + StepsAfterLeaving + 1,
            FireOnce(placed.AimYaw, placed.AimPitch, slot),
            context);

        var shots = outcomes.SelectMany(outcome => outcome.Shots.Select(fired => (outcome.Step, Shot: fired))).ToArray();
        Assert.True(
            shots.Length == 1 && shots[0].Step == FireStep && shots[0].Shot.CharacterId == ShooterId && shots[0].Shot.Slot == slot,
            FormattableString.Invariant(
                $"{context}: the run fired [{string.Join(", ", shots.Select(s => $"step {s.Step}: {s.Shot}"))}]; expected exactly one shot, by character {ShooterId} from slot {slot} at step {FireStep}"));
        var shot = shots[0].Shot;
        var fireOutcome = outcomes[0];
        Assert.True(fireOutcome.Step == FireStep, FormattableString.Invariant($"{context}: the run's first step is {fireOutcome.Step}, not {FireStep}"));
        var launch = Launched(fireOutcome.Characters, FireStep, shot, config, fireOutcome.Projectiles, context);
        var eye = ProjectileWorlds.Eye(placed.ShooterFeet, config, Stance.Standing);
        var speed = ProjectileWorlds.MuzzleSpeed(config, classIndex);
        Assert.True(
            launch.Class == classIndex && Bits(launch.Origin) == Bits(eye) && config.Weapons[slot].MuzzleSpeed == speed,
            FormattableString.Invariant(
                $"{context}: the shot {launch} must be of class {classIndex} at its default muzzle speed {speed:R} from the standing shooter's eye {eye}"));

        var predicted = ProjectileReference.PredictFirstHit(placed.Data, config, launch, characters, 0);
        var range = (double)config.Config.ProjectileClasses![classIndex]!.MaxRangeM;
        uint? expiryStep = null;
        switch (placement)
        {
            case FlatPlacement:
                Assert.True(
                    predicted is { Kind: HitKind.Character, TargetId: TargetId, Hitbox: HitboxPart.Torso },
                    FormattableString.Invariant($"{context}: the double path must first meet target {TargetId}'s torso, not {predicted}"));
                break;
            case BehindWall:
                Assert.True(
                    predicted is { Kind: HitKind.Mesh } && ProjectileWorlds.WallTriangleIds.Contains(checked((int)predicted.TargetId)),
                    FormattableString.Invariant(
                        $"{context}: the double path must first meet one of the wall triangles [{string.Join(", ", ProjectileWorlds.WallTriangleIds)}], not {predicted}"));
                break;
            case OverRidgeClear:
                Assert.True(
                    predicted is { Kind: HitKind.Terrain }
                        && predicted.Point.Z > ProjectileWorlds.RidgeCrestZ + 4.0
                        && predicted.Point.Z < ProjectileWorlds.GroundHalfExtent
                        && DVec3.Distance(predicted.Point, launch.Origin) <= range,
                    FormattableString.Invariant(
                        $"{context}: the double path must clear the ridge and first meet the terrain at a z in ({ProjectileWorlds.RidgeCrestZ + 4.0:R}, {ProjectileWorlds.GroundHalfExtent:R}) within maxRangeM {range:R} of Origin, not {predicted}"));
                break;
            case OverRidgeBlocked:
                Assert.True(
                    predicted is { Kind: HitKind.Terrain }
                        && predicted.Point.Z >= ProjectileWorlds.RidgeFrontZ
                        && predicted.Point.Z <= ProjectileWorlds.RidgeCrestZ,
                    FormattableString.Invariant(
                        $"{context}: the double path must first meet the ridge's front face, at a z in [{ProjectileWorlds.RidgeFrontZ:R}, {ProjectileWorlds.RidgeCrestZ:R}], not {predicted}"));
                break;
            case OverRidgeExpires:
                var rangeK = FirstRangeK(config, launch);
                Assert.True(rangeK.HasValue, FormattableString.Invariant($"{context}: the contract never passes MaxRangeSq[{classIndex}] within LifetimeSteps[{classIndex}]"));
                expiryStep = launch.LaunchStep + rangeK.Value;
                Assert.True(
                    predicted is null || predicted.Step > expiryStep.Value,
                    FormattableString.Invariant($"{context}: the double path must meet nothing at or before the Range step {expiryStep.Value}, not {predicted}"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown row.");
        }

        if (placement is OverRidgeClear or OverRidgeExpires)
        {
            AssertAboveTheRidge(launch, config, context);
        }

        if (expiryStep is { } rangeStep)
        {
            AssertExpiresWithRangeAt(outcomes, shot.ProjectileId, rangeStep, context);
            return;
        }

        AssertHitWhereTheReferenceSays(outcomes, shot.ProjectileId, predicted!, context);
    }

    /// <summary>
    /// US5's Independent Test, the shots the agreement suite runs (Phase 11 runs them in WebAssembly): Given
    /// <c>ScenarioCatalog.Get("shots-at-known-distances").Build(TestConfigs.At(hz), testWorld, hz)</c> over the
    /// committed <c>simcore/content/test-world.mmcd</c>, whose initial state holds characters 1 to 5, When its
    /// steps run on a <see cref="SimWorld"/>, Then the run fires exactly eight shots: shooter 1 (flat) and
    /// shooter 3 (behind the wall) each fire one class-0 then one class-1 shot, and shooter 5 (ridge) fires a
    /// face shot then a clearing shot with class 0, then the same with class 1. For each shot, its launch state
    /// is taken from the run, and the arrange first asserts that the double reference flown from it over the
    /// test world and the other characters' capsules gives the placement's outcome, so no shot can pass by
    /// comparing two missing hits: Character (target 2, Torso) for the flat shots; Mesh on a triangle of the
    /// wall mesh for the wall shots; Terrain short of <see cref="TestWorldFeatures.RidgeCrest"/>'s line (on the
    /// shooter's side of it) for the face shots; and no hit before the crest line for the clearing shots. Then
    /// the run matches it: for a predicted hit within <c>maxRangeM</c> of Origin, exactly one hit event holds
    /// the projectile, with the predicted kind, target ID and hitbox, a point within 1e-3 m of the predicted
    /// point and the predicted step, and no expiry event holds it; otherwise no hit event holds it and exactly
    /// one expiry event does, {projectile, Range}, at the step that sweeps p(k) → p(k + 1) for the first k with
    /// |p(k + 1) − Origin|² &gt; <c>MaxRangeSq</c> of the shot's class in the contract's float expressions. The
    /// run holds no other hit or expiry, no hit names a shooter, and no projectile remains after the last step.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ShotsAtKnownDistancesStreamHitsWhereTheReferenceSays(int hz)
    {
        var context = FormattableString.Invariant($"shots-at-known-distances, {hz} Hz");
        var testWorld = CommittedTestWorld.Value;
        var collisionData = testWorld.Data;
        var features = TestWorldGenerator.Features;
        var wallTriangles = MeshTriangleIds(collisionData, features.WallMeshId);
        Assert.True(wallTriangles.Count > 0, FormattableString.Invariant($"{context}: the test world has no triangle in wall mesh {features.WallMeshId}"));
        var stream = ScenarioCatalog.Get("shots-at-known-distances").Build(TestConfigs.At(hz), testWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        Assert.True(stream.StepHz == hz, FormattableString.Invariant($"{context}: the stream runs at {stream.StepHz} Hz"));

        var run = ScenarioRuns.Run(new SimWorld(config, testWorld), stream);

        AssertNoStepFaulted(run, context);
        var characterIds = run.InitialCharacters.Select(c => c.Id).ToArray();
        Assert.True(
            characterIds.SequenceEqual(KnownDistanceCharacterIds),
            FormattableString.Invariant($"{context}: the initial state holds characters [{string.Join(", ", characterIds)}]; expected [{string.Join(", ", KnownDistanceCharacterIds)}]"));
        var shots = ShotsOf(run);
        Assert.True(
            shots.Length == KnownDistanceShots.Length,
            FormattableString.Invariant(
                $"{context}: the run fired [{string.Join(", ", shots.Select(s => $"step {s.Step.Step}: {s.Shot}"))}]; expected exactly {KnownDistanceShots.Length} shots"));
        var shooterIds = KnownDistanceShots.Select(p => p.ShooterId).Distinct().ToArray();
        var hits = HitsOf(run);
        var expiries = run.Steps.SelectMany(step => step.Expiries.Select(expiry => (Step: step, Expiry: expiry))).ToArray();
        var expectedHits = 0;
        var expectedExpiries = 0;
        foreach (var shooterId in shooterIds)
        {
            var planned = KnownDistanceShots.Where(p => p.ShooterId == shooterId).ToArray();
            var fired = shots.Where(s => s.Shot.CharacterId == shooterId).ToArray();
            var firedClasses = fired.Select(s => config.Weapons[s.Shot.Slot].ProjectileClass).ToArray();
            Assert.True(
                firedClasses.SequenceEqual(planned.Select(p => p.Class)),
                FormattableString.Invariant(
                    $"{context}: shooter {shooterId} fired classes [{string.Join(", ", firedClasses)}]; expected [{string.Join(", ", planned.Select(p => p.Class))}]"));

            for (var j = 0; j < planned.Length; j++)
            {
                var (shotStep, shot) = fired[j];
                var placement = planned[j].Placement;
                var at = FormattableString.Invariant($"{context}, shooter {shooterId}'s {placement} shot {j} (projectile {shot.ProjectileId}, fired at step {shotStep.Step})");
                var launch = Launched(shotStep.Characters, shotStep.Step, shot, config, shotStep.Projectiles, at);
                var predicted = ProjectileReference.PredictFirstHit(collisionData, config, launch, shotStep.Characters, 0);
                switch (placement)
                {
                    case FlatPlacement:
                        Assert.True(
                            predicted is { Kind: HitKind.Character, TargetId: StreamFlatTargetId, Hitbox: HitboxPart.Torso },
                            FormattableString.Invariant($"{at}: the double path must first meet target {StreamFlatTargetId}'s torso, not {predicted}"));
                        break;
                    case BehindWall:
                        Assert.True(
                            predicted is { Kind: HitKind.Mesh } && wallTriangles.Contains(checked((int)predicted.TargetId)),
                            FormattableString.Invariant(
                                $"{at}: the double path must first meet a triangle of wall mesh {features.WallMeshId} [{string.Join(", ", wallTriangles)}], not {predicted}"));
                        break;
                    case RidgeFace:
                        Assert.True(
                            predicted is { Kind: HitKind.Terrain } && ShortOfTheCrest(predicted.Point, launch.Origin, features.RidgeCrest),
                            FormattableString.Invariant($"{at}: the double path must first meet the terrain short of the crest line {features.RidgeCrest}, not {predicted}"));
                        break;
                    case RidgeClearing:
                        Assert.True(
                            predicted is null || !ShortOfTheCrest(predicted.Point, launch.Origin, features.RidgeCrest),
                            FormattableString.Invariant($"{at}: the double path must meet nothing before the crest line {features.RidgeCrest}, not {predicted}"));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown placement {placement}.");
                }

                var hitsFor = hits.Where(h => h.Hit.ProjectileId == shot.ProjectileId).ToArray();
                var expiriesFor = expiries.Where(e => e.Expiry.ProjectileId == shot.ProjectileId).ToArray();
                var range = (double)config.Config.ProjectileClasses![launch.Class]!.MaxRangeM;
                if (predicted is not null && DVec3.Distance(predicted.Point, launch.Origin) <= range)
                {
                    expectedHits++;
                    Assert.True(
                        hitsFor.Length == 1 && expiriesFor.Length == 0,
                        FormattableString.Invariant(
                            $"{at}: the run holds hits [{string.Join(", ", hitsFor.Select(h => $"step {h.Step.Step}: {h.Hit}"))}] and expiries [{string.Join(", ", expiriesFor.Select(e => $"step {e.Step.Step}: {e.Expiry}"))}] for the projectile; expected exactly one hit, at step {predicted.Step}, and no expiry"));
                    var (hitStep, hit) = hitsFor[0];
                    Assert.True(
                        hitStep.Step == predicted.Step && hit.Kind == predicted.Kind && hit.TargetId == predicted.TargetId && hit.Hitbox == predicted.Hitbox,
                        FormattableString.Invariant($"{at}: the hit at step {hitStep.Step} is {hit}; the double reference predicts {predicted}"));
                    var off = DVec3.Distance(hit.Point, predicted.Point);
                    Assert.True(
                        off <= HitPointTolerance,
                        FormattableString.Invariant($"{at}: the hit point {hit.Point} is {off:R} m from the predicted point {predicted.Point}"));
                    continue;
                }

                expectedExpiries++;
                var rangeK = FirstRangeK(config, launch);
                Assert.True(rangeK.HasValue, FormattableString.Invariant($"{at}: the contract never passes MaxRangeSq[{launch.Class}] within LifetimeSteps[{launch.Class}]"));
                var rangeStep = launch.LaunchStep + rangeK.Value;
                Assert.True(
                    hitsFor.Length == 0
                        && expiriesFor.Length == 1
                        && expiriesFor[0].Step.Step == rangeStep
                        && expiriesFor[0].Expiry.Reason == ExpiryReason.Range,
                    FormattableString.Invariant(
                        $"{at}: the run holds hits [{string.Join(", ", hitsFor.Select(h => $"step {h.Step.Step}: {h.Hit}"))}] and expiries [{string.Join(", ", expiriesFor.Select(e => $"step {e.Step.Step}: {e.Expiry}"))}] for the projectile; expected no hit and one Range expiry at step {rangeStep} (the double reference predicts {predicted})"));
            }
        }

        Assert.True(
            hits.Length == expectedHits && expiries.Length == expectedExpiries,
            FormattableString.Invariant(
                $"{context}: the run holds {hits.Length} hits and {expiries.Length} expiries; the shots account for {expectedHits} and {expectedExpiries}"));
        Assert.True(
            hits.All(h => h.Hit.Kind != HitKind.Character || !shooterIds.Contains(h.Hit.TargetId)),
            FormattableString.Invariant($"{context}: the run reported hits [{string.Join(", ", hits.Select(h => h.Hit))}]; one names a shooter [{string.Join(", ", shooterIds)}]"));
        Assert.True(
            run.Last.Projectiles.Count == 0,
            FormattableString.Invariant($"{context}: projectiles [{string.Join(", ", run.Last.Projectiles.Select(p => p.Id))}] remain after the last step {run.Last.Step}"));
    }

    /// <summary>
    /// The world, shooter, target and aim of a row of <see cref="ShotsAtKnownDistancesHitWhereTheClosedFormPathSays"/>.
    /// The flat and wall rows aim at the torso centre of a standing target 25 m away with
    /// <see cref="ProjectileWorlds.AimWithDrop"/>; the ridge rows aim along +Z at the row's pitch. Both go
    /// through <see cref="BinaryAim"/> to the nearest binary angles.
    /// </summary>
    private static KnownDistancePlacement Place(string placement, int classIndex, CompiledConfig config)
    {
        if (placement is FlatPlacement or BehindWall)
        {
            var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingMinusZ, Stance.Standing);
            var torso = ProjectileReference.LayoutCapsules(Stance.Standing, TargetFeet, FacingMinusZ, config)[(int)HitboxPart.Torso];
            var eye = ProjectileWorlds.Eye(ShooterFeet, config, Stance.Standing);
            var speed = ProjectileWorlds.MuzzleSpeed(config, classIndex);
            var (aimYaw, aimPitch) = BinaryAim(ProjectileWorlds.AimWithDrop(eye, torso.Centre, speed, config.HalfG[classIndex]));
            return placement == FlatPlacement
                ? new KnownDistancePlacement(ProjectileWorlds.Flat(), ProjectileWorlds.FlatWorld, ShooterFeet, target, aimYaw, aimPitch)
                : new KnownDistancePlacement(ProjectileWorlds.Wall(), ProjectileWorlds.WallWorld, ShooterFeet, target, aimYaw, aimPitch);
        }

        var pitchDegrees = (placement, classIndex) switch
        {
            (OverRidgeClear, Heavy) => 3.5,
            (OverRidgeExpires, SmallArms) => 1.5,
            (OverRidgeBlocked, SmallArms) => 0.5,
            (OverRidgeBlocked, Heavy) => 2.0,
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, FormattableString.Invariant($"No ridge row for class {classIndex}.")),
        };
        var (yaw, pitch) = BinaryAim(ProjectileWorlds.Direction(0.0, pitchDegrees));
        return new KnownDistancePlacement(ProjectileWorlds.Ridge(), ProjectileWorlds.RidgeWorld, RidgeShooterFeet, null, yaw, pitch);
    }

    /// <summary>
    /// The commands of <see cref="ShotsAtKnownDistancesHitWhereTheClosedFormPathSays"/>: in
    /// <see cref="FireStep"/> the shooter presses Fire with <paramref name="slot"/> and the given aim. No other
    /// command is given, so the neutral input keeps every character's aim and stance and releases the trigger.
    /// </summary>
    private static Func<uint, CharacterCommand[]> FireOnce(ushort aimYaw, short aimPitch, byte slot) =>
        step => step == FireStep
            ? [new CharacterCommand(ShooterIndex, new InputCommand(FireStep, FireStep, Buttons.Fire, aimYaw, aimPitch, slot))]
            : [];

    /// <summary>The first weapon slot whose weapon fires <paramref name="classIndex"/>.</summary>
    private static byte SlotFiring(CompiledConfig config, int classIndex)
    {
        for (var slot = 0; slot < config.Weapons.Length; slot++)
        {
            if (config.Weapons[slot].ProjectileClass == classIndex)
            {
                return checked((byte)slot);
            }
        }

        Assert.Fail(FormattableString.Invariant($"No weapon slot fires projectile class {classIndex}."));
        return 0;
    }

    /// <summary>
    /// Asserts that the double closed form of <paramref name="launch"/> is above
    /// <see cref="ProjectileWorlds.Ridge"/>'s surface where it crosses z = 20, 24 and 28 m: the band's front
    /// edge, its crest and its back edge.
    /// </summary>
    private static void AssertAboveTheRidge(Projectile launch, CompiledConfig config, string context)
    {
        var origin = (DVec3)launch.Origin;
        var velocity = (DVec3)launch.LaunchVelocity;
        var halfG = (double)config.HalfG[launch.Class];
        Assert.True(velocity.Z > 0.0, FormattableString.Invariant($"{context}: the shot {launch} does not fly toward the ridge, along +Z"));
        foreach (var z in new[] { (double)ProjectileWorlds.RidgeFrontZ, ProjectileWorlds.RidgeCrestZ, ProjectileWorlds.RidgeBackZ })
        {
            var t = (z - origin.Z) / velocity.Z;
            var height = origin.Y + (velocity.Y * t) + (halfG * t * t);
            var surface = ProjectileWorlds.RidgeHeightAt(z);
            Assert.True(
                height > surface,
                FormattableString.Invariant($"{context}: the double path is at height {height:R} at z = {z:R}, not above the ridge surface {surface:R}"));
        }
    }

    /// <summary>
    /// Asserts that the run's only event holding <paramref name="projectileId"/> is one hit, at
    /// <paramref name="predicted"/>'s step, with its kind, target ID and hitbox and a point within
    /// <see cref="HitPointTolerance"/> of its point, and that no committed state holds the projectile from
    /// that step on.
    /// </summary>
    private static void AssertHitWhereTheReferenceSays(List<StepOutcome> outcomes, uint projectileId, PredictedHit predicted, string context)
    {
        Assert.True(
            outcomes[^1].Step >= predicted.Step,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the predicted hit step {predicted.Step}"));
        ProjectileHit? found = null;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (hit expected at step {predicted.Step})");
            var hitsFor = outcome.Hits.Where(h => h.ProjectileId == projectileId).ToArray();
            var expiriesFor = outcome.Expiries.Where(e => e.ProjectileId == projectileId).ToArray();
            Assert.True(expiriesFor.Length == 0, $"{at}: the step reported expiries [{string.Join(", ", expiriesFor)}] for the projectile; it hits");
            if (outcome.Step != predicted.Step)
            {
                Assert.True(hitsFor.Length == 0, $"{at}: the step reported hits [{string.Join(", ", hitsFor)}] for the projectile");
            }
            else
            {
                Assert.True(hitsFor.Length == 1, $"{at}: the step reported hits [{string.Join(", ", hitsFor)}] for the projectile; expected exactly one");
                found = hitsFor[0];
            }

            if (outcome.Step >= predicted.Step)
            {
                Assert.True(
                    outcome.Projectiles.All(p => p.Id != projectileId),
                    $"{at}: the committed state holds [{string.Join(", ", outcome.Projectiles.Select(p => p.Id))}] from the hit step on");
            }
        }

        Assert.True(found is not null, FormattableString.Invariant($"{context}: the run has no outcome for step {predicted.Step}"));
        var hit = found.Value;
        Assert.True(
            hit.Kind == predicted.Kind && hit.TargetId == predicted.TargetId && hit.Hitbox == predicted.Hitbox,
            FormattableString.Invariant($"{context}: the hit is {hit}; the double reference predicts {predicted}"));
        var off = DVec3.Distance(hit.Point, predicted.Point);
        Assert.True(
            off <= HitPointTolerance,
            FormattableString.Invariant($"{context}: the hit point {hit.Point} is {off:R} m from the predicted point {predicted.Point}"));
    }

    /// <summary>
    /// Asserts that no hit event ever holds <paramref name="projectileId"/>, and that the expiry events hold it
    /// exactly once, as {projectile, Range}, at <paramref name="rangeStep"/>.
    /// </summary>
    private static void AssertExpiresWithRangeAt(List<StepOutcome> outcomes, uint projectileId, uint rangeStep, string context)
    {
        Assert.True(
            outcomes[^1].Step >= rangeStep,
            FormattableString.Invariant($"{context}: the run ended at step {outcomes[^1].Step}, before the Range step {rangeStep}"));
        var expired = 0;
        foreach (var outcome in outcomes)
        {
            var at = FormattableString.Invariant($"{context}, step {outcome.Step} (Range expiry expected at step {rangeStep})");
            var hitsFor = outcome.Hits.Where(h => h.ProjectileId == projectileId).ToArray();
            var expiriesFor = outcome.Expiries.Where(e => e.ProjectileId == projectileId).ToArray();
            Assert.True(hitsFor.Length == 0, $"{at}: the step reported hits [{string.Join(", ", hitsFor)}] for the projectile; it expires without a hit");
            if (outcome.Step == rangeStep)
            {
                Assert.True(
                    expiriesFor.Length == 1 && expiriesFor[0].Reason == ExpiryReason.Range,
                    $"{at}: the step reported expiries [{string.Join(", ", expiriesFor)}] for the projectile; expected exactly {{{projectileId}, Range}}");
                expired++;
            }
            else
            {
                Assert.True(expiriesFor.Length == 0, $"{at}: the step reported expiries [{string.Join(", ", expiriesFor)}] for the projectile");
            }
        }

        Assert.True(expired == 1, FormattableString.Invariant($"{context}: the run has no outcome for the Range step {rangeStep}"));
    }

    /// <summary>
    /// The first segment k, below the class's <c>LifetimeSteps</c>, whose end p(k + 1) has
    /// |p(k + 1) − Origin|² &gt; <c>MaxRangeSq</c> in the contract's float expressions
    /// (<see cref="ProjectileWorlds.ContractPosition"/> and <see cref="RangeSquared"/>); null when none does.
    /// </summary>
    private static uint? FirstRangeK(CompiledConfig config, Projectile launch)
    {
        var lifetime = (uint)config.LifetimeSteps[launch.Class];
        for (var k = 0u; k < lifetime; k++)
        {
            var b = ProjectileWorlds.ContractPosition(launch.Origin, launch.LaunchVelocity, config.HalfG[launch.Class], config.StepRateHz, k + 1u);
            if (RangeSquared(b, launch.Origin) > config.MaxRangeSq[launch.Class])
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
    /// Whether <paramref name="point"/> lies strictly on <paramref name="shooter"/>'s side of the crest line,
    /// in the X–Z plane: the 2D cross product of the line's direction with the offset from its start has the
    /// same, non-zero sign for both.
    /// </summary>
    private static bool ShortOfTheCrest(DVec3 point, DVec3 shooter, TestWorldLine crest)
    {
        var dx = (double)crest.To.X - crest.From.X;
        var dz = (double)crest.To.Z - crest.From.Z;
        double Side(DVec3 p) => (dx * (p.Z - crest.From.Z)) - (dz * (p.X - crest.From.X));
        return Side(point) * Side(shooter) > 0.0;
    }

    /// <summary>
    /// The shooter and the target of a run of two characters, one of which fires every shot: the shooter is
    /// the only character that fires, and the target the other.
    /// </summary>
    private static (uint ShooterId, uint TargetId) ShooterAndTarget(ScenarioRun run, (ScenarioStep Step, ShotEvent Shot)[] shots, string context)
    {
        var shooterIds = shots.Select(s => s.Shot.CharacterId).Distinct().ToArray();
        var others = run.InitialCharacters.Where(c => !shooterIds.Contains(c.Id)).ToArray();
        Assert.True(
            run.InitialCharacters.Count == 2 && shooterIds.Length == 1 && others.Length == 1,
            FormattableString.Invariant(
                $"{context}: the initial state holds characters [{string.Join(", ", run.InitialCharacters.Select(c => c.Id))}] and shots come from [{string.Join(", ", shooterIds)}]; expected two characters, one shooter and one target"));
        return (shooterIds[0], others[0].Id);
    }

    /// <summary>The committed character with ID <paramref name="id"/>; anything but exactly one fails the test.</summary>
    private static CharacterState CommittedCharacter(IReadOnlyList<CharacterState> characters, uint id, string context)
    {
        var matching = characters.Where(c => c.Id == id).ToArray();
        Assert.True(matching.Length == 1, FormattableString.Invariant($"{context}: the committed state holds {matching.Length} characters with ID {id}"));
        return matching[0];
    }

    /// <summary>
    /// The aim yaw and pitch, in binary angle units (65536 to a turn), nearest to <paramref name="direction"/>:
    /// yaw = atan2(x, z) and pitch = atan2(y, √(x² + z²)), computed in double and rounded to the nearest unit.
    /// Yaw 0 faces +Z and 16384 faces +X, and positive pitch is up, as the weapon step's forward vector
    /// (cp·sy, sp, cp·cy) has it (data-model.md#weapon-step).
    /// </summary>
    private static (ushort Yaw, short Pitch) BinaryAim(Vec3 direction)
    {
        var x = (double)direction.X;
        var y = (double)direction.Y;
        var z = (double)direction.Z;
        var yaw = (long)System.Math.Round(System.Math.Atan2(x, z) * UnitsPerRadian);
        var pitch = (long)System.Math.Round(System.Math.Atan2(y, System.Math.Sqrt((x * x) + (z * z))) * UnitsPerRadian);
        return ((ushort)(yaw & 0xFFFF), checked((short)pitch));
    }

    /// <summary>
    /// The launch state the weapon step gave <paramref name="shot"/>'s projectile in <paramref name="step"/>
    /// (data-model.md#weapon-step, one shot, step 4): Origin is the shooter's committed, post-movement position
    /// plus its stance's eye height, LaunchVelocity is the shot's direction times the slot's muzzle speed, the
    /// class is the slot's, and LaunchStep is <paramref name="step"/>. A shot that hits in the step it is fired
    /// is in no committed state, so the launch is rebuilt from the shot event; when the step's committed
    /// projectiles do hold it, they must hold exactly this launch.
    /// </summary>
    private static Projectile Launched(
        IReadOnlyList<CharacterState> characters,
        uint step,
        ShotEvent shot,
        CompiledConfig config,
        IReadOnlyList<Projectile> committed,
        string context)
    {
        var shooters = characters.Where(c => c.Id == shot.CharacterId).ToArray();
        Assert.True(shooters.Length == 1, FormattableString.Invariant($"{context}: the committed state holds {shooters.Length} characters with the shooter's ID {shot.CharacterId}"));
        var shooter = shooters[0];
        var weapon = config.Weapons[shot.Slot];
        var origin = ProjectileWorlds.Eye(shooter.Position, config, shooter.Stance);
        var launch = new Projectile
        {
            Id = shot.ProjectileId,
            OwnerId = shot.CharacterId,
            Class = checked((byte)weapon.ProjectileClass),
            LaunchStep = step,
            Origin = origin,
            LaunchVelocity = ProjectileWorlds.LaunchVelocity(shot.Direction, weapon.MuzzleSpeed),
            Position = origin,
        };
        foreach (var stored in committed.Where(p => p.Id == shot.ProjectileId))
        {
            Assert.True(
                stored.OwnerId == launch.OwnerId && stored.Class == launch.Class && stored.LaunchStep == launch.LaunchStep
                    && Bits(stored.Origin) == Bits(launch.Origin) && Bits(stored.LaunchVelocity) == Bits(launch.LaunchVelocity),
                $"{context}: the committed projectile {stored} is not the launch {launch} the shot and the shooter's pose give");
        }

        return launch;
    }

    /// <summary>
    /// Asserts that the committed state of <paramref name="outcome"/> holds <paramref name="arranged"/> with its
    /// arranged position bits, yaw and stance, so the pose the reference posed is the one stage 4 posed;
    /// returns that committed character.
    /// </summary>
    private static CharacterState AssertHoldsItsPose(StepOutcome outcome, CharacterRecord arranged, string context)
    {
        var committed = outcome.Characters.Where(c => c.Id == arranged.Id).ToArray();
        Assert.True(committed.Length == 1, FormattableString.Invariant($"{context}: the committed state holds {committed.Length} characters with ID {arranged.Id}"));
        var character = committed[0];
        var position = new Vec3(arranged.Position.X, arranged.Position.Y, arranged.Position.Z);
        Assert.True(
            Bits(character.Position) == Bits(position) && character.AimYaw == arranged.AimYaw && character.Stance == (Stance)arranged.Stance,
            FormattableString.Invariant(
                $"{context}: character {arranged.Id} is at {character.Position} yaw {character.AimYaw} {character.Stance}; it must hold its arranged pose {position} yaw {arranged.AimYaw} {(Stance)arranged.Stance}"));
        return character;
    }

    /// <summary>Every shot of the run, in step order, with the step that fired it.</summary>
    private static (ScenarioStep Step, ShotEvent Shot)[] ShotsOf(ScenarioRun run) =>
        run.Steps.SelectMany(step => step.Shots.Select(shot => (Step: step, Shot: shot))).ToArray();

    /// <summary>Every hit of the run, in step order and in each step's event order, with the step that reported it.</summary>
    private static (ScenarioStep Step, ProjectileHit Hit)[] HitsOf(ScenarioRun run) =>
        run.Steps.SelectMany(step => step.Hits.Select(hit => (Step: step, Hit: hit))).ToArray();

    /// <summary>Asserts that no step of the run faulted.</summary>
    private static void AssertNoStepFaulted(ScenarioRun run, string context)
    {
        foreach (var step in run.Steps)
        {
            Assert.True(
                step.Fault.Code == StepFaultCode.None,
                FormattableString.Invariant($"{context}: step {step.Step} faulted with {step.Fault.Code} at '{step.Fault.Path}'"));
        }
    }

    /// <summary>
    /// The triangle IDs of mesh <paramref name="meshId"/> in <paramref name="world"/>
    /// (contracts/collision-data-format.md#triangle-ids): the heightfield triangle count
    /// 2·(columns − 1)·(rows − 1) plus each triangle's global index in file order.
    /// </summary>
    private static List<int> MeshTriangleIds(CollisionData world, uint meshId)
    {
        var ids = new List<int>();
        var next = 2 * (world.Columns - 1) * (world.Rows - 1);
        foreach (var mesh in world.Meshes)
        {
            var count = mesh.Indices.Length / 3;
            if (mesh.MeshId == meshId)
            {
                ids.AddRange(Enumerable.Range(next, count));
            }

            next += count;
        }

        return ids;
    }

    /// <summary>
    /// The first segment k ≥ <paramref name="firstK"/>, below the class's <c>LifetimeSteps</c>, whose ends
    /// p(k) and p(k + 1), evaluated in double with <see cref="ProjectileReference.ClosedForm(ReferenceProjectile, CompiledConfig, long)"/>,
    /// meet the heightfield of <paramref name="world"/> (<see cref="ProjectileReference.SegmentHeightfield"/>),
    /// with the triangle ID it meets first there. A flight that never meets it fails the test.
    /// </summary>
    private static (uint K, int TriangleId) FirstHeightfieldSegment(CollisionData world, CompiledConfig config, ReferenceProjectile projectile, uint firstK, string context)
    {
        var lifetime = (uint)config.LifetimeSteps[projectile.Class];
        for (var k = firstK; k < lifetime; k++)
        {
            var a = ProjectileReference.ClosedForm(projectile, config, k);
            var b = ProjectileReference.ClosedForm(projectile, config, k + 1L);
            if (ProjectileReference.SegmentHeightfield(world, a, b) is { } crossing)
            {
                return (k, crossing.TriangleId);
            }
        }

        Assert.Fail(FormattableString.Invariant($"{context}: no segment from {firstK} to {lifetime - 1u} meets the heightfield in double"));
        return default;
    }

    /// <summary>
    /// Steps <paramref name="world"/> until the committed state holds no projectile, then
    /// <see cref="StepsAfterLeaving"/> steps more, giving each step the commands <paramref name="commands"/>
    /// returns for it (none when null: every character gets the neutral input), and copies each step's shots,
    /// hits, expiries, committed projectiles and committed characters before the next step reuses the world's
    /// buffers. A fault, or a projectile still present after <paramref name="maxSteps"/> steps, fails the test.
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
                events.Shots.ToArray(),
                events.Hits.ToArray(),
                events.Expiries.ToArray(),
                committed.Projectiles.ToArray(),
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

    private static string Bits(Vec3 vector) => FormattableString.Invariant(
        $"{BitConverter.SingleToUInt32Bits(vector.X):x8} {BitConverter.SingleToUInt32Bits(vector.Y):x8} {BitConverter.SingleToUInt32Bits(vector.Z):x8}");

    /// <summary>
    /// One committed step: its number, its shot, hit and expiry events, and the committed projectiles and
    /// characters.
    /// </summary>
    private sealed record StepOutcome(
        uint Step,
        IReadOnlyList<ShotEvent> Shots,
        IReadOnlyList<ProjectileHit> Hits,
        IReadOnlyList<ExpiryEvent> Expiries,
        IReadOnlyList<Projectile> Projectiles,
        IReadOnlyList<CharacterState> Characters);

    /// <summary>One planned shot of <c>shots-at-known-distances</c>: its shooter, its projectile class and its placement.</summary>
    private sealed record PlannedShot(uint ShooterId, int Class, string Placement);

    /// <summary>
    /// A row of <see cref="ShotsAtKnownDistancesHitWhereTheClosedFormPathSays"/>: the collision data the
    /// reference reads and the world built from it, the shooter's feet, the target (none for the ridge rows)
    /// and the binary-angle aim of the shot.
    /// </summary>
    private sealed record KnownDistancePlacement(
        CollisionData Data,
        CollisionWorld World,
        Vec3 ShooterFeet,
        CharacterRecord? Target,
        ushort AimYaw,
        short AimPitch);
}
