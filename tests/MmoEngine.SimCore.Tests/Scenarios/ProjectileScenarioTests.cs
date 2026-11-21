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

    /// <summary>The shooter's feet: at the origin, so its standing eye is the launch point (0, 1.62, 0).</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, 0f);

    /// <summary>The launch point: the eye height of a standing character above the origin.</summary>
    private static readonly Vec3 LaunchPoint = new(0f, 1.62f, 0f);

    /// <summary><see cref="StruckPartsMatchTheStancesLayout"/>'s target: 25 m in front of the shooter's feet, along +Z.</summary>
    private static readonly Vec3 TargetFeet = new(0f, 0f, 25f);

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
    /// US5 scenario 1: Given <see cref="ProjectileWorlds.Flat"/>, its owner (ID 1) standing at the origin as
    /// the only character, so no target is in range, and one in-flight projectile of the row's class launched
    /// at step 1, level from (0, 1.62, 0) along +X at the class's default muzzle speed (600 / 120 m/s). The
    /// double reference first meets the terrain, at a ground point inside the heightfield (|x|, |z| below
    /// <see cref="ProjectileWorlds.GroundHalfExtent"/>) and within the class's <c>maxRangeM</c> of Origin
    /// (about 345 m for class 0 and 69 m for class 1), so the flight cannot end in a Range expiry without a
    /// terrain hit. When it is stepped with <c>SimWorld.Step</c> until it leaves the state, Then at every step
    /// n before the hit the committed state holds it alone with Position exactly the bits of
    /// <c>Ballistics.PositionAt(k + 1)</c> for k = n − 1, within 1e-3 m of the double closed form at k + 1;
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
                var contract = Ballistics.PositionAt(in launch, k + 1u, halfG, hz);
                Assert.True(
                    Bits(stored) == Bits(contract),
                    FormattableString.Invariant($"{at}: the stored Position {stored} (bits {Bits(stored)}) is not PositionAt({k + 1u}) {contract} (bits {Bits(contract)})"));
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
    /// LaunchVelocity and LaunchStep and with Position exactly the bits of <c>Ballistics.PositionAt(k + 1)</c>
    /// for k = step − LaunchStep; the run's only hit is at the expected step, on that projectile, with Kind
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
                var contract = Ballistics.PositionAt(in stored, k + 1u, halfG, hz);
                Assert.True(
                    Bits(stored.Position) == Bits(contract),
                    FormattableString.Invariant($"{at}: the stored Position {stored.Position} (bits {Bits(stored.Position)}) is not PositionAt({k + 1u}) {contract} (bits {Bits(contract)})"));
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
}
