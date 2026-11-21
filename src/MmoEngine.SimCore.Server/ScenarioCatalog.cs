using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The named scenario streams committed under <c>simcore/streams/</c>
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md#named-scenario-streams). Each
/// <see cref="ScenarioDefinition"/> builds its stream over the test world from a base configuration; the
/// scenario tests build the same definitions in process, and <c>simcore streams record</c> records them.
/// </summary>
public static class ScenarioCatalog
{
    /// <summary>The rate a scenario is recorded at when its row names no other.</summary>
    private const int DefaultStepRateHz = 30;

    /// <summary><c>fuzz-all-buttons</c>'s fixed seed, which its header carries.</summary>
    private const ulong FuzzSeed = 1000UL;

    /// <summary><c>fuzz-all-buttons</c>'s characters.</summary>
    private const int FuzzCharacterCount = 4;

    /// <summary><c>fuzz-all-buttons</c>'s steps, at every rate.</summary>
    private const int FuzzStepCount = 300;

    /// <summary><c>rifle-burst</c>'s match salt.</summary>
    private const ulong RifleBurstSalt = 0x0015_5CE0_B025_7001UL;

    /// <summary><c>reload-and-empty</c>'s match salt.</summary>
    private const ulong ReloadAndEmptySalt = 0x0015_5CE0_2E10_AD02UL;

    /// <summary><c>heat-overheat</c>'s match salt.</summary>
    private const ulong HeatOverheatSalt = 0x0015_5CE0_4EA7_0003UL;

    /// <summary><c>rifle-burst</c>'s aim pitch, a binary angle: 11.25° above the horizon.</summary>
    private const short RifleBurstPitch = 2048;

    /// <summary>The aim yaw of the weapon scenarios: 0, along the firing lane (+Z).</summary>
    private const ushort FiringLaneYaw = 0;

    /// <summary><c>heat-overheat</c>'s <c>weapons[0].heatPerShot</c>: the heat test weapon.</summary>
    private const int HeatTestHeatPerShot = 10000;

    /// <summary><c>reload-and-empty</c>'s initial magazine in slot 0.</summary>
    private const ushort ReloadAndEmptyMagazine = 2;

    /// <summary><c>reload-and-empty</c>'s initial reserve in slot 0.</summary>
    private const ushort ReloadAndEmptyReserve = 210;

    /// <summary><c>launcher-arc</c>'s match salt.</summary>
    private const ulong LauncherArcSalt = 0x0015_5CE0_1A0C_0004UL;

    /// <summary><c>projectile-hits-hitbox</c>'s match salt.</summary>
    private const ulong ProjectileHitsHitboxSalt = 0x0015_5CE0_41B0_0005UL;

    /// <summary><c>mesh-occludes-shot</c>'s match salt.</summary>
    private const ulong MeshOccludesShotSalt = 0x0015_5CE0_3E5E_0006UL;

    /// <summary><c>shots-at-known-distances</c>'s match salt.</summary>
    private const ulong ShotsAtKnownDistancesSalt = 0x0015_5CE0_D157_0007UL;

    /// <summary>The rifle's weapon slot, which fires the small-arms class 0.</summary>
    private const byte RifleSlot = 0;

    /// <summary>The launcher's weapon slot, which fires the heavy class 1.</summary>
    private const byte LauncherSlot = 1;

    /// <summary>The shooter's ID in the two-character projectile scenarios.</summary>
    private const uint ShooterId = 1;

    /// <summary>The target's ID in the two-character projectile scenarios.</summary>
    private const uint TargetId = 2;

    /// <summary>How far the targets of <c>projectile-hits-hitbox</c> and <c>shots-at-known-distances</c> stand in front of their shooter, m.</summary>
    private const float TargetDistance = 25f;

    /// <summary>
    /// Binary angle units per radian, 32768 / π rounded to float: an angle in radians times this, through
    /// <see cref="SimMath.RoundToInt"/>, is the nearest binary angle (65,536 to a turn).
    /// </summary>
    private const float UnitsPerRadian = 10430.378f;

    /// <summary>When <c>projectile-hits-hitbox</c>'s target starts holding Crouch, s.</summary>
    private const double CrouchFromSeconds = 1.5;

    /// <summary>When <c>projectile-hits-hitbox</c>'s target releases Crouch and runs toward the shooter, s.</summary>
    private const double RunFromSeconds = 3.5;

    /// <summary>
    /// When <c>projectile-hits-hitbox</c>'s six aimed shots fire, s: Head, Torso and Legs while the target
    /// stands, then the same while it holds Crouch. Each is 0.5 s after the last, more than the rifle's
    /// 0.1 s fire interval.
    /// </summary>
    private static readonly double[] AimedShotSeconds = [0.0, 0.5, 1.0, 2.0, 2.5, 3.0];

    private static readonly ScenarioDefinition[] Definitions = Sorted(
    [
        FlatRun(20),
        FlatRun(30),
        FlatRun(60),
        WallSlide45(),
        StairsClimb(),
        StairsBlocked(),
        SlopeLimit(),
        JumpLand(),
        CrouchUnderLedge(),
        CornerWedge(),
        ThinWallFast(),
        FuzzAllButtons(),
        RifleBurst(),
        ReloadAndEmpty(),
        HeatOverheat(),
        LauncherArc(),
        ProjectileHitsHitbox(),
        MeshOccludesShot(),
        ShotsAtKnownDistances(),
    ]);

    /// <summary>Every scenario's name, in ordinal order.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(Array.ConvertAll(Definitions, definition => definition.Name));

    /// <summary>The definition named <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="KeyNotFoundException">No scenario has that name; the message lists the known names.</exception>
    public static ScenarioDefinition Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var definition in Definitions)
        {
            if (string.Equals(definition.Name, name, StringComparison.Ordinal))
            {
                return definition;
            }
        }

        throw new KeyNotFoundException(string.Create(
            CultureInfo.InvariantCulture,
            $"There is no scenario named '{name}'. The known scenarios are {string.Join(", ", Names)}."));
    }

    /// <summary>
    /// <c>flat-run-&lt;hz&gt;hz</c>: one character, ID 1, at rest, Grounded and Standing at
    /// <see cref="TestWorldFeatures.FlatRunStart"/> on the flat run area, facing yaw 0 (+Z), holding Forward
    /// for one simulated second, recorded at <paramref name="hz"/>.
    /// </summary>
    private static ScenarioDefinition FlatRun(int hz) => new(
        string.Create(CultureInfo.InvariantCulture, $"flat-run-{hz}hz"),
        hz,
        durationSeconds: 1.0,
        [new ScenarioCharacter(1, TestWorldGenerator.Features.FlatRunStart, TestWorldGenerator.Features.FlatRunYaw, static _ => Buttons.Forward)]);

    /// <summary>
    /// <c>wall-slide-45</c>: one character at rest at <see cref="TestWorldFeatures.WallSlideStart"/>, 2.5 m
    /// from the plane of the wall <see cref="TestWorldFeatures.WallMeshId"/> names (about 3 m of run-up along
    /// its heading), holding Forward at <see cref="TestWorldFeatures.WallSlideYaw"/>, 45° into the wall, for
    /// 3 s. The wall runs on for 30 m, far more than the slide covers after contact at any rate (US1.2).
    /// </summary>
    private static ScenarioDefinition WallSlide45() => Holding(
        "wall-slide-45",
        durationSeconds: 3.0,
        TestWorldGenerator.Features.WallSlideStart,
        TestWorldGenerator.Features.WallSlideYaw,
        Buttons.Forward);

    /// <summary>
    /// <c>stairs-climb</c>: one character at rest at <see cref="TestWorldFeatures.StairsClimbStart"/>, 3 m
    /// before the 0.30 m stair (below stepHeight), holding Forward square into its front face for 3 s (US1.3).
    /// </summary>
    private static ScenarioDefinition StairsClimb() => Holding(
        "stairs-climb",
        durationSeconds: 3.0,
        TestWorldGenerator.Features.StairsClimbStart,
        TestWorldGenerator.Features.StairsClimbYaw,
        Buttons.Forward);

    /// <summary>
    /// <c>stairs-blocked</c>: one character at rest at <see cref="TestWorldFeatures.StairsBlockedStart"/>,
    /// 3 m before the 0.50 m stair (above stepHeight), holding Forward square into its front face for 3 s
    /// (US1.3).
    /// </summary>
    private static ScenarioDefinition StairsBlocked() => Holding(
        "stairs-blocked",
        durationSeconds: 3.0,
        TestWorldGenerator.Features.StairsBlockedStart,
        TestWorldGenerator.Features.StairsBlockedYaw,
        Buttons.Forward);

    /// <summary>
    /// <c>slope-limit</c>: one character at rest at <see cref="TestWorldFeatures.SlopeLimitStart"/>, 3 m
    /// before the foot of the 30° ramp, holding Forward up it, across the plateau and on into the 50° ramp
    /// for 6 s (US1.4).
    /// </summary>
    private static ScenarioDefinition SlopeLimit() => Holding(
        "slope-limit",
        durationSeconds: 6.0,
        TestWorldGenerator.Features.SlopeLimitStart,
        TestWorldGenerator.Features.SlopeLimitYaw,
        Buttons.Forward);

    /// <summary>
    /// <c>jump-land</c>: one character at rest, Grounded, at <see cref="TestWorldFeatures.JumpLandStart"/> on
    /// the flat run area, facing <see cref="TestWorldFeatures.FlatRunYaw"/>, holding Jump for the first step
    /// and no buttons after it, for 2 s: 40, 60 and 120 steps at 20, 30 and 60 Hz (US1.5).
    /// </summary>
    private static ScenarioDefinition JumpLand() => new(
        "jump-land",
        DefaultStepRateHz,
        durationSeconds: 2.0,
        [
            new ScenarioCharacter(
                1,
                TestWorldGenerator.Features.JumpLandStart,
                TestWorldGenerator.Features.FlatRunYaw,
                static time => time.Entry == 0 ? Buttons.Jump : 0),
        ]);

    /// <summary>
    /// <c>crouch-under-ledge</c>: one character at rest, Grounded and Standing, at
    /// <see cref="TestWorldFeatures.CrouchUnderLedgeStart"/>, 2 m before the near edge of the 4 m deep ledge
    /// whose underside is 1.5 m above the floor, facing across it at
    /// <see cref="TestWorldFeatures.CrouchUnderLedgeYaw"/>: Crouch and Forward over [0, 1.5 s), Forward only
    /// over [1.5 s, 4.5 s). That is 90, 135 and 270 steps at 20, 30 and 60 Hz, with the release at entries
    /// 30, 45 and 90 (US1.6).
    /// </summary>
    private static ScenarioDefinition CrouchUnderLedge() => new(
        "crouch-under-ledge",
        DefaultStepRateHz,
        durationSeconds: 4.5,
        [
            new ScenarioCharacter(
                1,
                TestWorldGenerator.Features.CrouchUnderLedgeStart,
                TestWorldGenerator.Features.CrouchUnderLedgeYaw,
                static time => time.Before(1.5) ? Buttons.Crouch | Buttons.Forward : Buttons.Forward),
        ]);

    /// <summary>
    /// <c>corner-wedge</c>: one character at rest at <see cref="TestWorldFeatures.CornerWedgeStart"/>,
    /// holding Forward at <see cref="TestWorldFeatures.CornerWedgeYaw"/>, diagonally into the concave corner
    /// at <see cref="TestWorldFeatures.Corner"/> at 45° to both walls, for 4 s (edge case: concave corner).
    /// </summary>
    private static ScenarioDefinition CornerWedge() => Holding(
        "corner-wedge",
        durationSeconds: 4.0,
        TestWorldGenerator.Features.CornerWedgeStart,
        TestWorldGenerator.Features.CornerWedgeYaw,
        Buttons.Forward);

    /// <summary>
    /// <c>thin-wall-fast</c>: one character at rest at <see cref="TestWorldFeatures.ThinWallFastStart"/>,
    /// holding Forward at <see cref="TestWorldFeatures.ThinWallFastYaw"/>, square into the zero-thickness
    /// quad <see cref="TestWorldFeatures.ThinWallMeshId"/> names, for 2 s, with <c>movement.runSpeed</c> 20,
    /// <c>movement.groundAccel</c> 1000, <c>movement.capsuleRadius</c> 0.25 and <c>movement.maxSubMoves</c> 16,
    /// so a step at full speed moves more than the capsule radius at every rate (20 / 60 &gt; 0.25 m) and is
    /// subdivided. R09 accepts it: ((20 / cos 45°) + 50) · 0.05 = 3.914 ≤ 16 · 0.25 (edge case: subdivided
    /// step).
    /// </summary>
    private static ScenarioDefinition ThinWallFast() => Holding(
        "thin-wall-fast",
        durationSeconds: 2.0,
        TestWorldGenerator.Features.ThinWallFastStart,
        TestWorldGenerator.Features.ThinWallFastYaw,
        Buttons.Forward,
        static config =>
        {
            var movement = config.Movement ?? throw new ArgumentException("The configuration has no movement section.", nameof(config));
            movement.RunSpeed = 20f;
            movement.GroundAccel = 1000f;
            movement.CapsuleRadius = 0.25f;
            movement.MaxSubMoves = 16;
        });

    /// <summary>
    /// <c>fuzz-all-buttons</c> (US1.9, US2.3): one seeded fuzz stream of four characters and 300 steps at
    /// every rate, <see cref="StreamGenerator"/>'s stream of the fixed seed 1000 (its header seed), recorded
    /// at 30 Hz. Its commands set every button, reach both pitch limits, cover the whole yaw range and
    /// include all four rejection reasons. It presses Fire, so its <c>.mmrs</c> file is recorded once the
    /// weapon and projectile steps are in place.
    /// </summary>
    private static ScenarioDefinition FuzzAllButtons() => ScenarioDefinition.Generated(
        "fuzz-all-buttons",
        DefaultStepRateHz,
        FuzzStepCount,
        static (config, world, rate) => StreamGenerator.Build(FuzzSeed, config, world, rate, FuzzCharacterCount, FuzzStepCount));

    /// <summary>
    /// <c>rifle-burst</c> (US4.5, US5.4): one character, ID 1, at rest, Grounded and Standing at
    /// <see cref="TestWorldFeatures.FlatRunStart"/>, in front of the clear firing lane, with the placeholder
    /// rifle (600 rpm) in slot 0, aiming at yaw 0 (+Z, along the lane) and pitch 2048 (11.25° above the
    /// horizon) in its initial state and every command: Fire held for 3 s, then released for 1 s. That is
    /// 80, 120 and 240 steps at 20, 30 and 60 Hz: 30 shots, their recoil kick and its recovery. It presses
    /// Fire, so its <c>.mmrs</c> file is recorded once the projectile step is in place.
    /// </summary>
    private static ScenarioDefinition RifleBurst() => new(
        "rifle-burst",
        DefaultStepRateHz,
        durationSeconds: 4.0,
        [
            new ScenarioCharacter(
                1,
                TestWorldGenerator.Features.FlatRunStart,
                FiringLaneYaw,
                static time => time.Before(3.0) ? Buttons.Fire : 0)
            {
                AimPitch = RifleBurstPitch,
            },
        ],
        matchSalt: RifleBurstSalt);

    /// <summary>
    /// <c>reload-and-empty</c> (US4.3, US4.6): one character, ID 1, at rest, Grounded and Standing at
    /// <see cref="TestWorldFeatures.FlatRunStart"/>, facing the firing lane, whose rifle in slot 0 starts
    /// with Magazine 2 and Reserve 210. Fire is held over [0, 1 s), firing the 2 rounds and then reporting
    /// Empty; one step holds Fire and Reload at 1 s, starting the reload; Fire is held over (1 s, 1.5 s],
    /// blocked by Reloading; nothing is held until 3.5 s, so the reload completes; WeaponSlot 1 with Fire is
    /// held over [3.5 s, 4 s), blocked by Switching; and nothing is held until 4.5 s, with slot 1 still
    /// selected. That is 90, 135 and 270 steps at 20, 30 and 60 Hz. It presses Fire, so its <c>.mmrs</c> file
    /// is recorded once the projectile step is in place.
    /// </summary>
    private static ScenarioDefinition ReloadAndEmpty() => new(
        "reload-and-empty",
        DefaultStepRateHz,
        durationSeconds: 4.5,
        [
            new ScenarioCharacter(
                1,
                TestWorldGenerator.Features.FlatRunStart,
                FiringLaneYaw,
                static time => time.Before(1.0) ? Buttons.Fire
                    : time.At(1.0) ? Buttons.Fire | Buttons.Reload
                    : time.NotAfter(1.5) ? Buttons.Fire
                    : time.Before(3.5) ? 0
                    : time.Before(4.0) ? Buttons.Fire
                    : 0)
            {
                WeaponSlot = static time => time.Before(3.5) ? (byte)0 : (byte)1,
                ArrangeWeapon = static (slot, weapon) => slot == 0
                    ? weapon with { Magazine = ReloadAndEmptyMagazine, Reserve = ReloadAndEmptyReserve }
                    : weapon,
            },
        ],
        matchSalt: ReloadAndEmptySalt);

    /// <summary>
    /// <c>heat-overheat</c> (US4.4, US4.6), at its own 30 Hz: one character, ID 1, at rest, Grounded and
    /// Standing at <see cref="TestWorldFeatures.FlatRunStart"/>, facing the firing lane, whose slot 0 is the
    /// heat test weapon: <c>weapons[0].heatPerShot</c> 10000 with every other value the rifle's (600 rpm,
    /// magazine 30, <c>overheatThreshold</c> 100000, <c>heatRecoveryLevel</c> 40000,
    /// <c>heatCooldownPerSecond</c> 50000, <c>heatCooldownDelayMs</c> 250). Fire is held from step 1 for 2.4 s:
    /// 72 steps at 30 Hz (48 and 144 at 20 and 60 Hz), through the overheat at the 10th shot, the cooldown and
    /// the 11th shot at the recovery level. It presses Fire, so its <c>.mmrs</c> file is recorded once the
    /// projectile step is in place.
    /// </summary>
    private static ScenarioDefinition HeatOverheat() => new(
        "heat-overheat",
        DefaultStepRateHz,
        durationSeconds: 2.4,
        [new ScenarioCharacter(1, TestWorldGenerator.Features.FlatRunStart, FiringLaneYaw, static _ => Buttons.Fire)],
        static config =>
        {
            var weapons = config.Weapons ?? throw new ArgumentException("The configuration has no weapons.", nameof(config));
            var heat = weapons[0] ?? throw new ArgumentException("weapons[0] is missing.", nameof(config));
            heat.HeatPerShot = HeatTestHeatPerShot;
        },
        HeatOverheatSalt);

    /// <summary>
    /// <c>launcher-arc</c> (US5.1): one character, ID 1, at rest, Grounded and Standing at
    /// <see cref="TestWorldFeatures.FlatRunStart"/>, aiming at yaw 0 (+Z, along the clear firing lane) and
    /// pitch 0, with ActiveSlot 1 and the launcher Ready in its initial state. It has a command every step
    /// (one character, so the stream is batchable), each carrying WeaponSlot 1 so the launcher stays active
    /// (a slot-0 command would switch to the rifle): Fire in the first step, then no buttons, for 1.5 s in
    /// all (30, 45 and 90 steps at 20, 30 and 60 Hz), so the heavy projectile reaches the terrain about
    /// 0.55 s and 66 m out (57 to 83 m across the launcher's 0.5° spread cone), well before the end.
    /// </summary>
    private static ScenarioDefinition LauncherArc() => new(
        "launcher-arc",
        DefaultStepRateHz,
        durationSeconds: 1.5,
        [
            new ScenarioCharacter(ShooterId, TestWorldGenerator.Features.FlatRunStart, FiringLaneYaw, static time => time.Entry == 0 ? Buttons.Fire : 0)
            {
                ActiveSlot = LauncherSlot,
                WeaponSlot = static _ => LauncherSlot,
            },
        ],
        matchSalt: LauncherArcSalt);

    /// <summary>
    /// <c>projectile-hits-hitbox</c> (US5.2, US5.6, the layout half of US5.5, Edge cases: point-blank), 8 s:
    /// the shooter, ID 1, at <see cref="TestWorldFeatures.FlatRunStart"/> and the target, ID 2, 25 m in front
    /// of it along the firing lane, facing it (yaw 32768), both Standing and Grounded on flat terrain. The
    /// stream's configuration sets the rifle's <c>spreadHalfAngleDeg</c>, <c>recoilKickPitchDeg</c> and
    /// <c>recoilKickYawDeg</c> to 0, so each shot flies where it is aimed. The shooter presses Fire for one
    /// step at 0, 0.5 and 1 s at the target's head, torso and legs centres while it stands, and at 2, 2.5 and
    /// 3 s at the same parts while it holds Crouch over [1.5 s, 3.5 s); each aim is the binary-angle yaw and
    /// low-root pitch to the part's centre (<see cref="AimAt"/>). Every rifle shot crosses the whole target
    /// within one step, whose segment is 10 m or more. From 3.5 s the target releases Crouch and runs toward
    /// the shooter, and the shooter fires once more at the first step at which its eye lies inside one of the
    /// target's post-movement hitboxes, found by stepping a <see cref="SimWorld"/> over the schedule
    /// (<see cref="ScenarioPlan.Run"/>): the point-blank shot. The target holds no button after that step.
    /// </summary>
    /// <exception cref="ScenarioRefusedException">At Build, when an aimed centre is out of the rifle's reach, no step puts the shooter's eye inside the target's hitboxes, or a shot does not strike its planned part.</exception>
    private static ScenarioDefinition ProjectileHitsHitbox()
    {
        const string name = "projectile-hits-hitbox";
        return ScenarioDefinition.Planned(
            name,
            DefaultStepRateHz,
            durationSeconds: 8.0,
            PlanProjectileHitsHitbox,
            ZeroSpreadAndRecoil(name, RifleSlot),
            ProjectileHitsHitboxSalt);
    }

    /// <summary>
    /// <c>mesh-occludes-shot</c> (US5.3), 1 s: the shooter, ID 1, at
    /// <see cref="TestWorldFeatures.WallShooterStart"/> and the target, ID 2, at
    /// <see cref="TestWorldFeatures.WallTargetStart"/>, on opposite sides of the wall mesh
    /// <see cref="TestWorldFeatures.WallMeshId"/>, facing each other, Standing and Grounded. The stream's
    /// configuration sets the rifle's spread and recoil kick to 0, as <c>projectile-hits-hitbox</c>'s does,
    /// and the shooter presses Fire in the first step aimed at the target's torso centre (<see cref="AimAt"/>).
    /// </summary>
    /// <exception cref="ScenarioRefusedException">At Build, when the world has no wall mesh, <see cref="CollisionWorld.SegmentQuery"/> finds no wall triangle first on the line from the shooter's eye to the aimed centre, or the centre is out of the rifle's reach.</exception>
    private static ScenarioDefinition MeshOccludesShot()
    {
        const string name = "mesh-occludes-shot";
        return ScenarioDefinition.Planned(
            name,
            DefaultStepRateHz,
            durationSeconds: 1.0,
            static plan =>
            {
                var features = TestWorldGenerator.Features;
                var shooterStart = features.WallShooterStart;
                var targetStart = features.WallTargetStart;
                var targetYaw = Facing(targetStart, shooterStart);
                var eye = Eye(plan.Config, shooterStart, Stance.Standing);
                var torso = PartCentre(plan.Config, targetStart, targetYaw, Stance.Standing, HitboxPart.Torso);
                var wall = MeshTriangleRange(plan, features.WallMeshId);
                if (!plan.World.SegmentQuery(eye, torso, out var hit) || hit.Kind != HitKind.Mesh || !wall.Contains(hit.TriangleId))
                {
                    throw new ScenarioRefusedException(plan.Name, $"no wall triangle lies first on the line from the shooter's eye {eye} to the target's torso centre {torso}.");
                }

                var aim = AimAt(plan, eye, torso, RifleSlot);
                return
                [
                    new ScenarioCharacter(ShooterId, shooterStart, aim.Yaw, static time => time.Entry == 0 ? Buttons.Fire : 0) { AimPitch = aim.Pitch },
                    new ScenarioCharacter(TargetId, targetStart, targetYaw, static _ => 0),
                ];
            },
            ZeroSpreadAndRecoil(name, RifleSlot),
            MeshOccludesShotSalt);
    }

    /// <summary>
    /// <c>shots-at-known-distances</c> (US5-IT's agreement half), 10.5 s: five characters, Standing and
    /// Grounded, pressing no movement button. Shooter 1 at <see cref="TestWorldFeatures.FlatRunStart"/> faces
    /// yaw 0 with target 2 standing 25 m in front of it along +Z in the clear lane; shooter 3 at
    /// <see cref="TestWorldFeatures.WallShooterStart"/> has target 4 at
    /// <see cref="TestWorldFeatures.WallTargetStart"/>, behind the wall; and shooter 5 at
    /// <see cref="TestWorldFeatures.RidgeShotStart"/> faces <see cref="TestWorldFeatures.RidgeShotYaw"/>. Each
    /// target faces its shooter. The stream's configuration sets <c>spreadHalfAngleDeg</c>,
    /// <c>recoilKickPitchDeg</c> and <c>recoilKickYawDeg</c> to 0 for both weapon slots. Every shooter starts
    /// with ActiveSlot 0 and both slots Ready, fires its class-0 shots, switches to slot 1 and fires its
    /// class-1 shots, each a one-step Fire press. Shooters 1 and 3 fire at the target's torso centre at 0 s
    /// (rifle) and, after selecting slot 1 at 0.5 s and waiting out the 0.75 s switch, at 1.5 s (launcher).
    /// Shooter 5 aims where its heading crosses <see cref="TestWorldFeatures.RidgeCrest"/>'s line, 1 m below
    /// the crest there for a face shot and 1.5 m above it for a clearing shot: rifle face shot at 0 s, rifle
    /// clearing shot at 0.5 s, slot 1 from 1 s, launcher face shot at 2 s, Reload at 2.5 s (the launcher's
    /// magazine holds one round), launcher clearing shot at 6.5 s, after the 3.5 s reload. Every press is
    /// separated from the slot's last shot by more than its fire interval, and the stream ends 4 s after the
    /// last shot, by which time every projectile has hit or passed its range. Each aim is the binary-angle yaw
    /// and the class's low-root pitch to the aim point (<see cref="AimAt"/>). Build steps a
    /// <see cref="SimWorld"/> over the commands and checks every shot's outcome.
    /// </summary>
    /// <exception cref="ScenarioRefusedException">At Build, when the configuration has no launcher slot, the world has no wall mesh or ridge crossing, an aim point is out of its weapon's reach, a shot's outcome is not its placement's (naming the shot: a Character hit on the target's torso for the flat shots, a Mesh hit on a wall triangle for the wall shots, a Terrain hit on the ridge face (within the ridge's half width of the crest line, on the shooter's side, above the surrounding ground) for the face shots, no hit before the crest line for the clearing shots), or a projectile is still flying at the last step.</exception>
    private static ScenarioDefinition ShotsAtKnownDistances()
    {
        const string name = "shots-at-known-distances";
        return ScenarioDefinition.Planned(
            name,
            DefaultStepRateHz,
            durationSeconds: 10.5,
            PlanShotsAtKnownDistances,
            static config =>
            {
                ZeroSpreadAndRecoil(name, RifleSlot)(config);
                ZeroSpreadAndRecoil(name, LauncherSlot)(config);
            },
            ShotsAtKnownDistancesSalt);
    }

    /// <summary>The characters of <c>projectile-hits-hitbox</c> at the plan's rate.</summary>
    private static ScenarioCharacter[] PlanProjectileHitsHitbox(ScenarioPlan plan)
    {
        var shooterStart = TestWorldGenerator.Features.FlatRunStart;
        var targetStart = new Vec3(shooterStart.X, shooterStart.Y, shooterStart.Z + TargetDistance);
        var targetYaw = Facing(targetStart, shooterStart);
        var eye = Eye(plan.Config, shooterStart, Stance.Standing);
        var aims = new ScenarioAim[AimedShotSeconds.Length];
        var parts = new HitboxPart[AimedShotSeconds.Length];
        for (var i = 0; i < aims.Length; i++)
        {
            var stance = i < HitboxLayout.CapsuleCount ? Stance.Standing : Stance.Crouched;
            parts[i] = (HitboxPart)(i % HitboxLayout.CapsuleCount);
            aims[i] = AimAt(plan, eye, PartCentre(plan.Config, targetStart, targetYaw, stance, parts[i]), RifleSlot);
        }

        ScenarioCharacter[] Characters(int pointBlankEntry) =>
        [
            new ScenarioCharacter(
                ShooterId,
                shooterStart,
                aims[0].Yaw,
                time => time.Entry == pointBlankEntry || Array.Exists(AimedShotSeconds, time.At) ? Buttons.Fire : 0)
            {
                AimPitch = aims[0].Pitch,
                Aim = time => aims[LastStarted(AimedShotSeconds, time)],
            },
            new ScenarioCharacter(
                TargetId,
                targetStart,
                targetYaw,
                time => time.Before(CrouchFromSeconds) ? 0
                    : time.Before(RunFromSeconds) ? Buttons.Crouch
                    : time.Entry <= pointBlankEntry ? Buttons.Forward
                    : 0),
        ];

        // Run with the target running to the end and no point-blank shot, and find the first step at
        // which the shooter's eye lies inside one of the target's post-movement hitboxes.
        var probe = plan.Run(Characters(int.MaxValue));
        var runFrom = (int)(RunFromSeconds * plan.StepRateHz);
        var pointBlank = -1;
        Span<Hitbox> hitboxes = stackalloc Hitbox[HitboxLayout.CapsuleCount];
        for (var entry = runFrom; entry < probe.Count && pointBlank < 0; entry++)
        {
            var shooter = probe[entry].Character(ShooterId);
            var target = probe[entry].Character(TargetId);
            var shooterEye = Eye(plan.Config, shooter.Position, shooter.Stance);
            HitboxBuilder.Build(plan.Config.HitboxLayouts[(int)target.Stance], target.Position, target.AimYaw, hitboxes);
            foreach (var hitbox in hitboxes)
            {
                if (SegmentCapsule.Intersect(shooterEye, shooterEye, hitbox.A, hitbox.B, hitbox.Radius, out _))
                {
                    pointBlank = entry;
                    break;
                }
            }
        }

        if (pointBlank < 0)
        {
            throw new ScenarioRefusedException(plan.Name, $"at {plan.StepRateHz} Hz no step puts the shooter's eye inside the running target's hitboxes.");
        }

        var characters = Characters(pointBlank);
        var hits = plan.Run(characters).SelectMany(step => step.Hits).ToArray();
        var struck = Array.ConvertAll(hits, hit => hit.Kind == HitKind.Character && hit.TargetId == TargetId ? hit.Hitbox : HitboxPart.None);
        if (!struck.AsSpan(0, System.Math.Min(struck.Length, parts.Length)).SequenceEqual(parts) || hits.Length != parts.Length + 1 || struck[^1] == HitboxPart.None || hits[^1].T != 0f)
        {
            throw new ScenarioRefusedException(
                plan.Name,
                $"at {plan.StepRateHz} Hz the shots struck [{string.Join(", ", hits)}]; planned Head, Torso and Legs standing, then crouched, then the point-blank hit.");
        }

        return characters;
    }

    /// <summary>The characters of <c>shots-at-known-distances</c> at the plan's rate, with every shot's outcome checked.</summary>
    private static ScenarioCharacter[] PlanShotsAtKnownDistances(ScenarioPlan plan)
    {
        var features = TestWorldGenerator.Features;
        var flatShooter = features.FlatRunStart;
        var flatTarget = new Vec3(flatShooter.X, flatShooter.Y, flatShooter.Z + TargetDistance);
        var crest = features.RidgeCrest;
        var ridgeShooter = features.RidgeShotStart;
        var crossing = Crossing(ridgeShooter, features.RidgeShotYaw, crest, plan.Name);
        var face = new Vec3(crossing.X, crest.From.Y - 1f, crossing.Z);
        var clearing = new Vec3(crossing.X, crest.From.Y + 1.5f, crossing.Z);
        var ridgeEye = Eye(plan.Config, ridgeShooter, Stance.Standing);
        var ridgeAims = new[]
        {
            AimAt(plan, ridgeEye, face, RifleSlot),
            AimAt(plan, ridgeEye, clearing, RifleSlot),
            AimAt(plan, ridgeEye, face, LauncherSlot),
            AimAt(plan, ridgeEye, clearing, LauncherSlot),
        };

        var characters = new List<ScenarioCharacter>();
        var planned = new List<(uint ShooterId, KnownDistanceShot[] Shots)>();
        void AddPair(uint shooterId, Vec3 shooterStart, Vec3 targetStart, KnownDistancePlacement placement)
        {
            var targetYaw = Facing(targetStart, shooterStart);
            var eye = Eye(plan.Config, shooterStart, Stance.Standing);
            var torso = PartCentre(plan.Config, targetStart, targetYaw, Stance.Standing, HitboxPart.Torso);
            var rifle = AimAt(plan, eye, torso, RifleSlot);
            var launcher = AimAt(plan, eye, torso, LauncherSlot);
            characters.Add(new ScenarioCharacter(shooterId, shooterStart, rifle.Yaw, static time => time.At(0.0) || time.At(1.5) ? Buttons.Fire : 0)
            {
                AimPitch = rifle.Pitch,
                Aim = time => time.Before(0.5) ? rifle : launcher,
                WeaponSlot = static time => time.Before(0.5) ? RifleSlot : LauncherSlot,
            });
            characters.Add(new ScenarioCharacter(shooterId + 1, targetStart, targetYaw, static _ => 0));
            planned.Add((shooterId, [new(RifleSlot, placement, shooterId + 1), new(LauncherSlot, placement, shooterId + 1)]));
        }

        AddPair(1, flatShooter, flatTarget, KnownDistancePlacement.Flat);
        AddPair(3, features.WallShooterStart, features.WallTargetStart, KnownDistancePlacement.BehindWall);
        characters.Add(new ScenarioCharacter(
            5,
            ridgeShooter,
            features.RidgeShotYaw,
            static time => time.At(0.0) || time.At(0.5) || time.At(2.0) || time.At(6.5) ? Buttons.Fire
                : time.At(2.5) ? Buttons.Reload
                : 0)
        {
            AimPitch = ridgeAims[0].Pitch,
            Aim = time => ridgeAims[time.Before(0.5) ? 0 : time.Before(1.0) ? 1 : time.Before(6.5) ? 2 : 3],
            WeaponSlot = static time => time.Before(1.0) ? RifleSlot : LauncherSlot,
        });
        planned.Add((5,
        [
            new(RifleSlot, KnownDistancePlacement.RidgeFace, 0),
            new(RifleSlot, KnownDistancePlacement.RidgeClearing, 0),
            new(LauncherSlot, KnownDistancePlacement.RidgeFace, 0),
            new(LauncherSlot, KnownDistancePlacement.RidgeClearing, 0),
        ]));

        var probe = plan.Run(characters);
        var wall = MeshTriangleRange(plan, features.WallMeshId);
        var shots = probe.SelectMany(step => step.Shots).ToArray();
        var hits = probe.SelectMany(step => step.Hits).ToArray();
        foreach (var (shooterId, shooterShots) in planned)
        {
            var fired = Array.FindAll(shots, shot => shot.CharacterId == shooterId);
            for (var j = 0; j < shooterShots.Length; j++)
            {
                var expected = shooterShots[j];
                var shot = j < fired.Length ? fired[j] : (ShotEvent?)null;
                var hit = shot is { } s ? Array.FindAll(hits, h => h.ProjectileId == s.ProjectileId) : [];
                var shortOfCrest = hit.Length == 1 && ShortOfTheLine(hit[0].Point, ridgeShooter, crest);
                var outcome = shot?.Slot == expected.Slot && hit.Length <= 1 && expected.Placement switch
                {
                    KnownDistancePlacement.Flat => hit is [{ Kind: HitKind.Character, Hitbox: HitboxPart.Torso } h0] && h0.TargetId == expected.TargetId,
                    KnownDistancePlacement.BehindWall => hit is [{ Kind: HitKind.Mesh } h1] && wall.Contains((int)h1.TargetId),
                    KnownDistancePlacement.RidgeFace => hit is [{ Kind: HitKind.Terrain } h2] && OnTheRidgeFace(h2.Point, ridgeShooter, features),
                    _ => !shortOfCrest,
                };
                if (!outcome)
                {
                    throw new ScenarioRefusedException(
                        plan.Name,
                        $"at {plan.StepRateHz} Hz shooter {shooterId}'s shot {j} ({expected.Placement}, slot {expected.Slot}) is {(shot is null ? "not fired" : shot.ToString())} with hits [{string.Join(", ", hit)}]; it does not end as its placement says.");
                }
            }
        }

        if (probe[^1].ProjectileCount != 0)
        {
            throw new ScenarioRefusedException(plan.Name, $"at {plan.StepRateHz} Hz {probe[^1].ProjectileCount} projectiles are still flying at the last step.");
        }

        return [.. characters];
    }

    /// <summary>
    /// The configuration override of scenario <paramref name="name"/> that sets <c>weapons[slot]</c>'s spread
    /// and recoil kick to 0, so its shots fly where they are aimed.
    /// </summary>
    /// <exception cref="ScenarioRefusedException">When applied, the configuration has no <c>weapons[slot]</c> (a valid one-weapon configuration has no launcher slot).</exception>
    private static Action<SimConfig> ZeroSpreadAndRecoil(string name, byte slot) => config =>
    {
        var weapons = config.Weapons;
        var weapon = weapons is not null && slot < weapons.Count ? weapons[slot] : null;
        if (weapon is null)
        {
            throw new ScenarioRefusedException(name, $"the configuration has no weapons[{slot}], whose spread and recoil kick the scenario sets to 0.");
        }

        weapon.SpreadHalfAngleDeg = 0f;
        weapon.RecoilKickPitchDeg = 0f;
        weapon.RecoilKickYawDeg = 0f;
    };

    /// <summary>The eye of a character with its feet at <paramref name="feet"/>: the stance's eye height above them, the projectile origin.</summary>
    private static Vec3 Eye(CompiledConfig config, Vec3 feet, Stance stance) =>
        new(feet.X, feet.Y + config.HitboxLayouts[(int)stance].EyeHeight, feet.Z);

    /// <summary>The centre of <paramref name="part"/> (its posed A and B's midpoint) of a character at <paramref name="feet"/> with that yaw and stance.</summary>
    private static Vec3 PartCentre(CompiledConfig config, Vec3 feet, ushort yaw, Stance stance, HitboxPart part)
    {
        Span<Hitbox> hitboxes = stackalloc Hitbox[HitboxLayout.CapsuleCount];
        HitboxBuilder.Build(config.HitboxLayouts[(int)stance], feet, yaw, hitboxes);
        var hitbox = hitboxes[(int)part];
        return (hitbox.A + hitbox.B) * 0.5f;
    }

    /// <summary>The binary-angle yaw that faces <paramref name="to"/> from <paramref name="from"/>, in the X–Z plane.</summary>
    private static ushort Facing(Vec3 from, Vec3 to) => YawUnits(SimMath.Atan2(to.X - from.X, to.Z - from.Z));

    /// <summary>
    /// The binary-angle aim from <paramref name="eye"/> at <paramref name="point"/> for <paramref name="slot"/>'s
    /// weapon, in float with <see cref="SimMath"/>: yaw = Atan2(dx, dz), and pitch the low root of the
    /// class's closed-form launch angle, θ = Atan2(g·d² + 2·h·v², d·(v² + √(v⁴ − g·(g·d² + 2·h·v²)))) for
    /// muzzle speed v, g = −2·HalfG, horizontal distance d and rise h, which is
    /// atan((v² − √(…)) / (g·d)) without its cancellation. Both go through
    /// <see cref="SimMath.RoundToInt"/> to binary angle units.
    /// </summary>
    /// <exception cref="ScenarioRefusedException">The point is out of the weapon's reach.</exception>
    private static ScenarioAim AimAt(ScenarioPlan plan, Vec3 eye, Vec3 point, byte slot)
    {
        var weapon = plan.Config.Weapons[slot];
        var v2 = weapon.MuzzleSpeed * weapon.MuzzleSpeed;
        var g = -2f * plan.Config.HalfG[weapon.ProjectileClass];
        var dx = point.X - eye.X;
        var dz = point.Z - eye.Z;
        var d = SimMath.Sqrt((dx * dx) + (dz * dz));
        var lift = (g * (d * d)) + (2f * ((point.Y - eye.Y) * v2));
        var discriminant = (v2 * v2) - (g * lift);
        if (!(discriminant >= 0f))
        {
            throw new ScenarioRefusedException(plan.Name, $"{point} is out of reach of slot {slot}'s weapon from {eye}.");
        }

        var pitch = SimMath.Atan2(lift, d * (v2 + SimMath.Sqrt(discriminant)));
        return new ScenarioAim(YawUnits(SimMath.Atan2(dx, dz)), checked((short)SimMath.RoundToInt(pitch * UnitsPerRadian)));
    }

    /// <summary>An angle in radians as the nearest binary-angle yaw, wrapped to 0–65535.</summary>
    private static ushort YawUnits(float radians) => unchecked((ushort)SimMath.RoundToInt(radians * UnitsPerRadian));

    /// <summary>The index of the last of <paramref name="seconds"/> (ascending) that <paramref name="time"/> has reached, or 0 before the first.</summary>
    private static int LastStarted(double[] seconds, ScenarioTime time)
    {
        var last = 0;
        for (var i = 0; i < seconds.Length; i++)
        {
            if (!time.Before(seconds[i]))
            {
                last = i;
            }
        }

        return last;
    }

    /// <summary>Where the heading <paramref name="yaw"/> from <paramref name="start"/> crosses <paramref name="line"/>, in the X–Z plane, at the start's height.</summary>
    /// <exception cref="ScenarioRefusedException">The heading is parallel to the line.</exception>
    private static Vec3 Crossing(Vec3 start, ushort yaw, TestWorldLine line, string name)
    {
        SimMath.SinCosBinaryAngle(yaw, out var hx, out var hz);
        var lx = line.To.X - line.From.X;
        var lz = line.To.Z - line.From.Z;
        var across = (hx * lz) - (hz * lx);
        if (across == 0f)
        {
            throw new ScenarioRefusedException(name, $"the heading {yaw} never crosses the line {line}.");
        }

        var fx = line.From.X - start.X;
        var fz = line.From.Z - start.Z;
        var u = ((fx * lz) - (fz * lx)) / across;
        return new Vec3(start.X + (u * hx), start.Y, start.Z + (u * hz));
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies strictly on <paramref name="shooter"/>'s side of
    /// <paramref name="line"/>, in the X–Z plane: the 2D cross product of the line's direction with the
    /// offset from its start has the same non-zero sign for both.
    /// </summary>
    private static bool ShortOfTheLine(Vec3 point, Vec3 shooter, TestWorldLine line)
    {
        var lx = line.To.X - line.From.X;
        var lz = line.To.Z - line.From.Z;
        float Side(Vec3 p) => (lx * (p.Z - line.From.Z)) - (lz * (p.X - line.From.X));
        return Side(point) * Side(shooter) > 0f;
    }

    /// <summary>
    /// Whether <paramref name="point"/> lies on the face of the ridge that <paramref name="shooter"/> sees:
    /// strictly on the shooter's side of <see cref="TestWorldFeatures.RidgeCrest"/>'s line
    /// (<see cref="ShortOfTheLine"/>), within <see cref="TestWorldFeatures.RidgeHalfWidth"/> of that line in
    /// the X–Z plane (the squared 2D cross product of the line's direction with the offset from its start is at
    /// most the half width squared times the direction's squared length), and above
    /// <see cref="TestWorldFeatures.RidgeFootHeight"/>, the ground the ridge rises from.
    /// </summary>
    private static bool OnTheRidgeFace(Vec3 point, Vec3 shooter, TestWorldFeatures features)
    {
        var line = features.RidgeCrest;
        var lx = line.To.X - line.From.X;
        var lz = line.To.Z - line.From.Z;
        var cross = (lx * (point.Z - line.From.Z)) - (lz * (point.X - line.From.X));
        var halfWidth = features.RidgeHalfWidth;
        return ShortOfTheLine(point, shooter, line)
            && cross * cross <= (halfWidth * halfWidth) * ((lx * lx) + (lz * lz))
            && point.Y > features.RidgeFootHeight;
    }

    /// <summary>
    /// The triangle IDs of mesh <paramref name="meshId"/> (contracts/collision-data-format.md#triangle-ids):
    /// the heightfield triangle count plus the mesh triangles' global indices in file order.
    /// </summary>
    /// <exception cref="ScenarioRefusedException">The plan's world has no such mesh.</exception>
    private static TriangleIdRange MeshTriangleRange(ScenarioPlan plan, uint meshId)
    {
        var world = plan.World;
        var first = world.HeightfieldTriangleCount;
        foreach (var mesh in world.Data.Meshes)
        {
            var count = mesh.Indices.Length / 3;
            if (mesh.MeshId == meshId)
            {
                return new TriangleIdRange(first, first + count);
            }

            first += count;
        }

        throw new ScenarioRefusedException(plan.Name, $"the collision world has no mesh {meshId}.");
    }

    /// <summary>
    /// A scenario at the default 30 Hz with one character, ID 1, at rest, Grounded and Standing at
    /// <paramref name="start"/>, holding <paramref name="buttons"/> at <paramref name="yaw"/> at every entry.
    /// </summary>
    private static ScenarioDefinition Holding(
        string name,
        double durationSeconds,
        Vec3 start,
        ushort yaw,
        Buttons buttons,
        Action<SimConfig>? configure = null) => new(
        name,
        DefaultStepRateHz,
        durationSeconds,
        [new ScenarioCharacter(1, start, yaw, _ => buttons)],
        configure);

    private static ScenarioDefinition[] Sorted(ScenarioDefinition[] definitions)
    {
        Array.Sort(definitions, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 1; i < definitions.Length; i++)
        {
            if (string.Equals(definitions[i - 1].Name, definitions[i].Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Two scenarios are named '{definitions[i].Name}'.");
            }
        }

        return definitions;
    }

    /// <summary>Where a <c>shots-at-known-distances</c> shot is aimed, which fixes the outcome Build checks.</summary>
    private enum KnownDistancePlacement
    {
        /// <summary>At the torso of the target 25 m away on flat terrain: a Character hit on its torso.</summary>
        Flat,

        /// <summary>At the torso of the target behind the wall: a Mesh hit on a wall triangle.</summary>
        BehindWall,

        /// <summary>1 m below the crest: a Terrain hit on the ridge face, within the ridge's half width of the crest line on the shooter's side, above the surrounding ground.</summary>
        RidgeFace,

        /// <summary>1.5 m above the crest: no hit before the crest line.</summary>
        RidgeClearing,
    }

    /// <summary>One planned <c>shots-at-known-distances</c> shot: the slot it fires from, its placement and the target it aims at (0 for none).</summary>
    private readonly record struct KnownDistanceShot(byte Slot, KnownDistancePlacement Placement, uint TargetId);

    /// <summary>The triangle IDs from <paramref name="First"/> up to, not including, <paramref name="End"/>.</summary>
    private readonly record struct TriangleIdRange(int First, int End)
    {
        public bool Contains(int id) => id >= First && id < End;
    }
}

/// <summary>A command's aim: the yaw (65,536 to a turn, yaw 0 facing +Z) and the pitch (positive up), in binary angle units.</summary>
/// <param name="Yaw">The aim yaw.</param>
/// <param name="Pitch">The aim pitch, in [−16384, 16384].</param>
public readonly record struct ScenarioAim(ushort Yaw, short Pitch);

/// <summary>A character's committed pose after a step: its ID, its feet, its aim yaw and its stance.</summary>
/// <param name="Id">The character's ID.</param>
/// <param name="Position">The feet.</param>
/// <param name="AimYaw">The aim yaw, a binary angle.</param>
/// <param name="Stance">The stance.</param>
public readonly record struct ScenarioPose(uint Id, Vec3 Position, ushort AimYaw, Stance Stance);

/// <summary>
/// One committed step of a <see cref="ScenarioPlan.Run"/>: its number, its shot, hit and expiry events, the
/// characters' committed poses in ascending ID and the committed projectile count.
/// </summary>
public sealed record ScenarioProbeStep(
    uint Step,
    IReadOnlyList<ShotEvent> Shots,
    IReadOnlyList<ProjectileHit> Hits,
    IReadOnlyList<ExpiryEvent> Expiries,
    IReadOnlyList<ScenarioPose> Characters,
    int ProjectileCount)
{
    /// <summary>The committed pose of the character with ID <paramref name="id"/>.</summary>
    /// <exception cref="KeyNotFoundException">The step holds no such character.</exception>
    public ScenarioPose Character(uint id)
    {
        foreach (var pose in Characters)
        {
            if (pose.Id == id)
            {
                return pose;
            }
        }

        throw new KeyNotFoundException($"Step {Step} holds no character {id}.");
    }

    /// <summary>
    /// The poses and the projectile count of a committed canonical state, read field by field with
    /// <see cref="StateFieldReader"/> (each character's <c>id</c>, <c>position</c>, <c>aimYaw</c> and
    /// <c>stance</c>, which the layout puts in that order).
    /// </summary>
    internal static (ScenarioPose[] Characters, int ProjectileCount) Read(ReadOnlySpan<byte> state)
    {
        var poses = new List<ScenarioPose>();
        var projectiles = 0;
        uint id = 0;
        float x = 0f, y = 0f, z = 0f;
        ushort yaw = 0;
        foreach (var value in StateFieldReader.Read(state).Fields)
        {
            var record = value.Field.Record;
            var name = value.Field.Name;
            if (record == StateRecordKind.Header && name == "projectileCount")
            {
                projectiles = (int)value.Bits;
            }

            if (record != StateRecordKind.Character)
            {
                continue;
            }

            switch (name)
            {
                case "id":
                    id = value.Bits;
                    break;
                case "position.x":
                    x = BitConverter.UInt32BitsToSingle(value.Bits);
                    break;
                case "position.y":
                    y = BitConverter.UInt32BitsToSingle(value.Bits);
                    break;
                case "position.z":
                    z = BitConverter.UInt32BitsToSingle(value.Bits);
                    break;
                case "aimYaw":
                    yaw = (ushort)value.Bits;
                    break;
                case "stance":
                    poses.Add(new ScenarioPose(id, new Vec3(x, y, z), yaw, (Stance)value.Bits));
                    break;
            }
        }

        return ([.. poses], projectiles);
    }
}

/// <summary>
/// What a planned definition (<see cref="ScenarioDefinition.Planned"/>) makes its characters from at Build:
/// the scenario's name, the compiled configuration (the base configuration with the definition's overrides
/// at the build's rate), the collision world, the rate and the step count, and <see cref="Run"/>, which steps
/// a <see cref="SimWorld"/> over a candidate set of characters so a definition can place a shot by what the
/// world does, or check its shots' outcomes, before it returns them.
/// </summary>
public sealed class ScenarioPlan
{
    private readonly ScenarioDefinition _definition;
    private readonly SimConfig _config;
    private readonly Func<CompiledConfig, CollisionWorld, SimWorld> _newWorld;

    /// <summary>
    /// A plan of <paramref name="definition"/> at <paramref name="stepRateHz"/>. <paramref name="newWorld"/>
    /// makes the world <see cref="Run"/> steps, a new <see cref="SimWorld"/> when null: the tests' way to give
    /// it a world whose collision queries overflow and see a probe step fault.
    /// </summary>
    /// <exception cref="ConfigValidationException">The configuration is invalid.</exception>
    internal ScenarioPlan(
        ScenarioDefinition definition,
        SimConfig config,
        CollisionWorld world,
        int stepRateHz,
        int stepCount,
        Func<CompiledConfig, CollisionWorld, SimWorld>? newWorld = null)
    {
        _definition = definition;
        _config = config;
        _newWorld = newWorld ?? (static (compiled, collision) => new SimWorld(compiled, collision));
        Config = CompiledConfig.Create(config);
        World = world;
        StepRateHz = stepRateHz;
        StepCount = stepCount;
    }

    /// <summary>The scenario's name, for messages.</summary>
    public string Name => _definition.Name;

    /// <summary>The compiled configuration the stream carries.</summary>
    public CompiledConfig Config { get; }

    /// <summary>The collision world the stream runs on.</summary>
    public CollisionWorld World { get; }

    /// <summary>The rate the stream is built at.</summary>
    public int StepRateHz { get; }

    /// <summary>The stream's entries.</summary>
    public int StepCount { get; }

    /// <summary>
    /// The stream these characters give, stepped on a new <see cref="SimWorld"/> reset with its initial state
    /// and the definition's match salt: one <see cref="ScenarioProbeStep"/> per entry, in order. A step that
    /// faults stops the run with the <see cref="StepFaultException"/> <see cref="StreamRunner"/> would throw
    /// for it (<c>step &lt;n&gt; faulted: &lt;code&gt; at &lt;path&gt;</c>, n the step that did not commit), so
    /// <c>simcore streams record</c> reports a fault in a plan's probe run as it reports one in the recording.
    /// </summary>
    /// <exception cref="ArgumentException">The characters are empty or an ID repeats.</exception>
    /// <exception cref="StepFaultException">A step faults.</exception>
    public IReadOnlyList<ScenarioProbeStep> Run(IReadOnlyList<ScenarioCharacter> characters)
    {
        var stream = _definition.Assemble(_config, World, StepRateHz, StepCount, ScenarioDefinition.SortedCharacters(characters));
        var world = _newWorld(Config, World);
        world.Reset(stream.InitialState.Span, stream.MatchSalt);
        var steps = new List<ScenarioProbeStep>(stream.Steps.Count);
        foreach (var entry in stream.Steps)
        {
            var commands = Array.ConvertAll(entry.Commands, command => new CharacterCommand(command.CharacterIndex, command.Command));
            var report = world.Step(commands);
            if (report.Fault.Code != StepFaultCode.None)
            {
                throw new StepFaultException(entry.Step, report.Fault);
            }

            var (poses, projectiles) = ScenarioProbeStep.Read(report.State);
            var events = report.Events;
            steps.Add(new ScenarioProbeStep(report.Step, events.Shots.ToArray(), events.Hits.ToArray(), events.Expiries.ToArray(), poses, projectiles));
        }

        return steps;
    }
}

/// <summary>
/// Where a scenario's schedule is: the stream entry (0 for the first step) and the step rate the stream is
/// built at.
/// </summary>
/// <param name="Entry">The entry's index; its step number is <c>Entry + 1</c>.</param>
/// <param name="StepRateHz">The rate the stream is built at.</param>
public readonly record struct ScenarioTime(int Entry, int StepRateHz)
{
    /// <summary>
    /// Whether the entry starts before <paramref name="seconds"/> of simulated time: entry k covers
    /// [k / hz, (k + 1) / hz), so this is k &lt; seconds · hz, exact for a time that is a whole number of
    /// steps.
    /// </summary>
    public bool Before(double seconds) => Entry < seconds * StepRateHz;

    /// <summary>
    /// Whether the entry starts exactly at <paramref name="seconds"/>: k == seconds · hz, true for one entry
    /// when the time is a whole number of steps.
    /// </summary>
    public bool At(double seconds) => Entry == seconds * StepRateHz;

    /// <summary>
    /// Whether the entry starts at or before <paramref name="seconds"/>: k ≤ seconds · hz, so a schedule can
    /// hold a button over a range closed at its end, such as (1 s, 1.5 s].
    /// </summary>
    public bool NotAfter(double seconds) => Entry <= seconds * StepRateHz;
}

/// <summary>
/// One character of a scenario: its ID, where it starts (at rest, Standing, with new weapons unless
/// <see cref="ArrangeWeapon"/> changes them, and slot <see cref="ActiveSlot"/> active), whether it starts
/// Grounded (on ground whose normal is (0, 1, 0)), its initial aim yaw and pitch, and the aim, the buttons it
/// holds and the weapon slot it selects at each entry. Every entry gives it one command.
/// </summary>
/// <param name="Id">The character's ID; a scenario's IDs are unique.</param>
/// <param name="Position">The feet, at rest height; usually a <see cref="TestWorldFeatures"/> start.</param>
/// <param name="AimYaw">The aim yaw of the initial state, and of every command unless <see cref="Aim"/> is set.</param>
/// <param name="Hold">The buttons held at each entry.</param>
public sealed record ScenarioCharacter(uint Id, Vec3 Position, ushort AimYaw, Func<ScenarioTime, Buttons> Hold)
{
    /// <summary>Whether the character starts Grounded; otherwise airborne.</summary>
    public bool Grounded { get; init; } = true;

    /// <summary>
    /// The aim pitch of the initial state, a binary angle; 0 (level) unless set. It is every command's pitch
    /// too unless <see cref="Aim"/> is set.
    /// </summary>
    public short AimPitch { get; init; }

    /// <summary>The aim each entry's command carries; null carries <see cref="AimYaw"/> and <see cref="AimPitch"/> at every entry.</summary>
    public Func<ScenarioTime, ScenarioAim>? Aim { get; init; }

    /// <summary>The weapon slot active in the initial state; slot 0 unless set.</summary>
    public byte ActiveSlot { get; init; }

    /// <summary>The weapon slot each entry's command selects; null selects slot 0 at every entry.</summary>
    public Func<ScenarioTime, byte>? WeaponSlot { get; init; }

    /// <summary>
    /// Called once per slot with the slot and its new weapon's state (<see cref="WeaponState.New"/>); its
    /// result is that slot's initial state, so a definition can start a weapon with any field changed. Null
    /// keeps the new weapons.
    /// </summary>
    public Func<int, WeaponState, WeaponState>? ArrangeWeapon { get; init; }
}

/// <summary>
/// One named scenario stream (contracts/simcore-cli.md#named-scenario-streams): its name, the collision file
/// it runs on, its own step rate, the configuration overrides it applies to a copy of the base
/// configuration, its characters and its length in simulated seconds. <see cref="Build"/> makes the
/// unrecorded stream at any rate, and <see cref="Record"/> runs it through <see cref="StreamRunner"/> for its
/// expected hashes. A <see cref="Generated"/> definition, such as <c>fuzz-all-buttons</c>, takes its stream
/// from a generator instead of a character schedule, and a <see cref="Planned"/> definition, such as
/// <c>projectile-hits-hitbox</c>, makes its characters at Build from the compiled configuration and the
/// world, and may step a <see cref="SimWorld"/> to place or check its shots.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>
    /// The match salt a definition's stream carries unless the definition names its own; the movement
    /// streams carry it.
    /// </summary>
    public const ulong DefaultMatchSalt = 0x0015_5CE0_A210_5A17UL;

    /// <summary>The step the initial state is at; the first entry is step 1.</summary>
    private const uint InitialStep = 0;

    private readonly ScenarioCharacter[] _characters;
    private readonly Action<SimConfig>? _configure;
    private readonly Func<SimConfig, CollisionWorld, int, RecordedStream>? _generate;
    private readonly Func<ScenarioPlan, IReadOnlyList<ScenarioCharacter>>? _plan;
    private readonly ulong _matchSalt;

    /// <summary>
    /// A definition; the characters are sorted by ID. Its stream carries <paramref name="matchSalt"/>,
    /// <see cref="DefaultMatchSalt"/> unless the definition names its own.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or the characters are null.</exception>
    /// <exception cref="ArgumentException">The characters are empty, an ID repeats, or the duration is not positive.</exception>
    public ScenarioDefinition(
        string name,
        int stepRateHz,
        double durationSeconds,
        IReadOnlyList<ScenarioCharacter> characters,
        Action<SimConfig>? configure = null,
        ulong matchSalt = DefaultMatchSalt)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(characters);
        if (characters.Count == 0)
        {
            throw new ArgumentException("A scenario has at least one character.", nameof(characters));
        }

        if (!(durationSeconds > 0.0))
        {
            throw new ArgumentException("A scenario's duration is positive.", nameof(durationSeconds));
        }

        _characters = SortedCharacters(characters);
        Name = name;
        StepRateHz = stepRateHz;
        DurationSeconds = durationSeconds;
        _configure = configure;
        _matchSalt = matchSalt;
    }

    private ScenarioDefinition(string name, int stepRateHz, double durationSeconds, Func<SimConfig, CollisionWorld, int, RecordedStream> generate)
    {
        _characters = [];
        Name = name;
        StepRateHz = stepRateHz;
        DurationSeconds = durationSeconds;
        _generate = generate;
    }

    private ScenarioDefinition(
        string name,
        int stepRateHz,
        double durationSeconds,
        Func<ScenarioPlan, IReadOnlyList<ScenarioCharacter>> plan,
        Action<SimConfig>? configure,
        ulong matchSalt)
    {
        _characters = [];
        Name = name;
        StepRateHz = stepRateHz;
        DurationSeconds = durationSeconds;
        _plan = plan;
        _configure = configure;
        _matchSalt = matchSalt;
    }

    /// <summary>
    /// A generated definition: <see cref="Build"/> returns <paramref name="generate"/>(base configuration,
    /// world, rate), which places its own characters, so <see cref="Characters"/> is empty, and builds
    /// <paramref name="stepCount"/> steps at every rate; <see cref="DurationSeconds"/> is that count at
    /// <paramref name="stepRateHz"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or the generator is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The step rate or the step count is not positive.</exception>
    public static ScenarioDefinition Generated(string name, int stepRateHz, int stepCount, Func<SimConfig, CollisionWorld, int, RecordedStream> generate)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(generate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
        return new ScenarioDefinition(name, stepRateHz, (double)stepCount / stepRateHz, generate);
    }

    /// <summary>
    /// A planned definition: <see cref="Build"/> applies <paramref name="configure"/> to a copy of the base
    /// configuration at the rate, as a scheduled definition does, and takes its characters from
    /// <paramref name="plan"/>, called with a <see cref="ScenarioPlan"/> of that configuration compiled, the
    /// world, the rate and the step count, so their positions, aims and schedules can depend on the
    /// configuration and on what <see cref="ScenarioPlan.Run"/> shows the world doing. Its stream carries
    /// <paramref name="matchSalt"/>; <see cref="Characters"/> is empty, because the plan places them.
    /// </summary>
    /// <exception cref="ArgumentNullException">The name or the plan is null.</exception>
    /// <exception cref="ArgumentException">The duration is not positive.</exception>
    public static ScenarioDefinition Planned(
        string name,
        int stepRateHz,
        double durationSeconds,
        Func<ScenarioPlan, IReadOnlyList<ScenarioCharacter>> plan,
        Action<SimConfig>? configure = null,
        ulong matchSalt = DefaultMatchSalt)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(plan);
        if (!(durationSeconds > 0.0))
        {
            throw new ArgumentException("A scenario's duration is positive.", nameof(durationSeconds));
        }

        return new ScenarioDefinition(name, stepRateHz, durationSeconds, plan, configure, matchSalt);
    }

    /// <summary>The stream's name, and its file name without <c>.mmrs</c>.</summary>
    public string Name { get; }

    /// <summary>The collision file under <c>simcore/content/</c> the stream runs on.</summary>
    public string CollisionFileName => "test-world.mmcd";

    /// <summary>The rate the committed stream is recorded at.</summary>
    public int StepRateHz { get; }

    /// <summary>
    /// The whole run's simulated seconds: a whole number of steps at 20, 30 and 60 Hz. A generated
    /// definition builds the same number of steps at every rate; this is that count at its own rate.
    /// </summary>
    public double DurationSeconds { get; }

    /// <summary>The characters, in ascending ID; empty for a generated or planned definition, which places its own.</summary>
    public IReadOnlyList<ScenarioCharacter> Characters => _characters;

    /// <summary>
    /// The unrecorded stream at <paramref name="stepRateHz"/> (the definition's own rate when null) over
    /// <paramref name="world"/>: the base configuration copied, with the overrides applied and
    /// <c>stepRateHz</c> set; an initial state at step 0 holding the characters at rest with full weapons;
    /// and <see cref="DurationSeconds"/> × the rate entries, each with one command per character (sequence
    /// numbers from 1, <c>ClientStep</c> equal to the step, the character's aim, and the buttons and weapon
    /// slot its schedule gives), every expected hash 0, seed 0 and the definition's match salt. A generated
    /// definition returns its generator's stream at the rate instead, and a planned definition builds the
    /// same from the characters its plan returns.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="ArgumentException">The duration is not a whole number of steps at the rate, or a plan's characters are empty or repeat an ID.</exception>
    /// <exception cref="ConfigValidationException">A planned definition's configuration is invalid.</exception>
    /// <exception cref="ScenarioRefusedException">A planned definition cannot be built on the configuration or the world, such as an aim point out of reach, a weapon slot the configuration lacks or a shot that does not end as planned.</exception>
    /// <exception cref="StepFaultException">A step of a planned definition's probe run (<see cref="ScenarioPlan.Run"/>) faults.</exception>
    public RecordedStream Build(SimConfig baseConfig, CollisionWorld world, int? stepRateHz = null)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(world);
        var rate = stepRateHz ?? StepRateHz;
        if (_generate is not null)
        {
            return _generate(baseConfig, world, rate);
        }

        var config = baseConfig.Clone();
        _configure?.Invoke(config);
        config.StepRateHz = rate;

        var stepCount = StepCount(rate);
        var characters = _plan is null
            ? _characters
            : SortedCharacters(_plan(new ScenarioPlan(this, config, world, rate, stepCount)));
        return Assemble(config, world, rate, stepCount, characters);
    }

    /// <summary>
    /// The characters in ascending ID, copied.
    /// </summary>
    /// <exception cref="ArgumentNullException">The characters are null.</exception>
    /// <exception cref="ArgumentException">The characters are empty or an ID repeats.</exception>
    internal static ScenarioCharacter[] SortedCharacters(IReadOnlyList<ScenarioCharacter> characters)
    {
        ArgumentNullException.ThrowIfNull(characters);
        if (characters.Count == 0)
        {
            throw new ArgumentException("A scenario has at least one character.", nameof(characters));
        }

        ScenarioCharacter[] sorted = [.. characters];
        Array.Sort(sorted, static (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i - 1].Id == sorted[i].Id)
            {
                throw new ArgumentException($"Character ID {sorted[i].Id} repeats.", nameof(characters));
            }
        }

        return sorted;
    }

    /// <summary>
    /// The unrecorded stream of <paramref name="characters"/> (in ascending ID) over <paramref name="world"/>
    /// with <paramref name="config"/>, already overridden and at <paramref name="rate"/>:
    /// <paramref name="stepCount"/> entries of one command per character, as <see cref="Build"/> describes.
    /// </summary>
    internal RecordedStream Assemble(SimConfig config, CollisionWorld world, int rate, int stepCount, ScenarioCharacter[] characters)
    {
        var steps = new RecordedStep[stepCount];
        for (var k = 0; k < stepCount; k++)
        {
            var step = InitialStep + (uint)k + 1u;
            var time = new ScenarioTime(k, rate);
            var commands = new RecordedCommand[characters.Length];
            for (var c = 0; c < characters.Length; c++)
            {
                var character = characters[c];
                var slot = character.WeaponSlot?.Invoke(time) ?? 0;
                var aim = character.Aim?.Invoke(time) ?? new ScenarioAim(character.AimYaw, character.AimPitch);
                var input = new InputCommand((uint)k + 1u, step, character.Hold(time), aim.Yaw, aim.Pitch, slot);
                commands[c] = new RecordedCommand((byte)c, input);
            }

            steps[k] = new RecordedStep(step, commands, 0UL);
        }

        return new RecordedStream(SimCoreVersion.RulesRevision, rate, seed: 0, _matchSalt, world.Checksum, config, InitialState(Name, config, characters), steps);
    }

    /// <summary>
    /// <see cref="Build"/>, run through <see cref="StreamRunner"/> over <paramref name="world"/>, with each
    /// step's expected hash set to the run's hash.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="CoreFormatException">The stream is refused.</exception>
    /// <exception cref="ConfigValidationException">The configuration is invalid.</exception>
    /// <exception cref="StepFaultException">A step faulted.</exception>
    public RecordedStream Record(SimConfig baseConfig, CollisionWorld world, int? stepRateHz = null)
    {
        var built = Build(baseConfig, world, stepRateHz);
        return WithExpectedHashes(built, StreamRunner.Run(built.Encode(), [world]));
    }

    /// <summary>
    /// <paramref name="stream"/> with each step's expected hash taken from <paramref name="records"/>, the
    /// packed step records a run of it returned (contracts/state-serialization.md#step-record).
    /// </summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentException">The records do not parse, or hold another number of steps or other step numbers.</exception>
    public static RecordedStream WithExpectedHashes(RecordedStream stream, ReadOnlySpan<byte> records)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var steps = new RecordedStep[stream.Steps.Count];
        var offset = 0;
        for (var i = 0; i < steps.Length; i++)
        {
            var entry = stream.Steps[i];
            if (!StepRecord.TryRead(records[offset..], out var step, out _, out var hash, out var length) || step != entry.Step)
            {
                throw new ArgumentException($"The records hold no record of step {entry.Step} at byte {offset}.", nameof(records));
            }

            steps[i] = entry with { ExpectedHash = hash };
            offset += length;
        }

        if (offset != records.Length)
        {
            throw new ArgumentException($"The records run {records.Length - offset} bytes past the stream's {steps.Length} steps.", nameof(records));
        }

        return new RecordedStream(
            stream.RulesRevision,
            stream.StepHz,
            stream.Seed,
            stream.MatchSalt,
            stream.CollisionChecksum,
            stream.Config,
            stream.InitialState,
            steps);
    }

    /// <summary><see cref="DurationSeconds"/> × <paramref name="rate"/>, which must be a whole number of at least 1.</summary>
    private int StepCount(int rate)
    {
        var steps = DurationSeconds * rate;
        if (steps < 1.0 || steps != System.Math.Floor(steps) || steps > int.MaxValue)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"{Name} lasts {DurationSeconds} s, which is not a whole number of steps at {rate} Hz."),
                nameof(rate));
        }

        return (int)steps;
    }

    /// <summary>
    /// The canonical initial state of <paramref name="characters"/>, in ascending ID, for the stream
    /// <paramref name="name"/>: step 0, <c>nextProjectileId</c> 1, the characters at rest (velocity 0,
    /// ground normal (0, 1, 0), Grounded or airborne as each says, Standing, each one's aim yaw and pitch,
    /// <c>lastSequence</c> 0, each one's <see cref="ScenarioCharacter.ActiveSlot"/>) with <c>input.weaponSlots</c> weapons, each new
    /// (<see cref="WeaponState.New"/>) from its slot's configuration and then changed by the character's
    /// <see cref="ScenarioCharacter.ArrangeWeapon"/>, no projectiles and no events.
    /// <see cref="StreamGenerator"/> builds its initial states here too.
    /// </summary>
    /// <exception cref="ArgumentException">The configuration has no weapons or input section, or too few weapons for its slots.</exception>
    internal static byte[] InitialState(string name, SimConfig config, IReadOnlyList<ScenarioCharacter> characters)
    {
        var weaponConfigs = config.Weapons ?? throw new ArgumentException("The configuration has no weapons.", nameof(config));
        var weaponSlots = config.Input?.WeaponSlots ?? throw new ArgumentException("The configuration has no input section.", nameof(config));
        if (weaponSlots < 1 || weaponSlots > weaponConfigs.Count)
        {
            throw new ArgumentException($"input.weaponSlots {weaponSlots} does not name 1 to {weaponConfigs.Count} weapons.", nameof(config));
        }

        var states = new CharacterState[characters.Count];
        var weapons = new WeaponState[characters.Count * weaponSlots];
        for (var c = 0; c < characters.Count; c++)
        {
            var character = characters[c];
            states[c] = new CharacterState
            {
                Id = character.Id,
                Position = character.Position,
                Velocity = new Vec3(0f, 0f, 0f),
                GroundNormal = new Vec3(0f, 1f, 0f),
                AimYaw = character.AimYaw,
                AimPitch = character.AimPitch,
                Stance = Stance.Standing,
                Flags = character.Grounded ? CharacterFlags.Grounded : 0,
                LastSequence = 0,
                ActiveSlot = character.ActiveSlot,
                WeaponCount = (byte)weaponSlots,
            };
            for (var s = 0; s < weaponSlots; s++)
            {
                var weapon = weaponConfigs[s] ?? throw new ArgumentException($"weapons[{s}] is missing.", nameof(config));
                var fresh = WeaponState.New(weapon.MagazineSize, weapon.ReserveRounds);
                weapons[(c * weaponSlots) + s] = character.ArrangeWeapon is null ? fresh : character.ArrangeWeapon(s, fresh);
            }
        }

        var length = StateSerializer.Length(states.Length, weaponSlots, 0, 0, 0, 0, 0, 0, 0);
        var state = new byte[length];
        var fault = StateSerializer.Write(
            InitialStep,
            1,
            weaponSlots,
            states,
            weapons,
            Span<Projectile>.Empty,
            Span<ShotEvent>.Empty,
            Span<ProjectileHit>.Empty,
            ReadOnlySpan<ExpiryEvent>.Empty,
            ReadOnlySpan<InputRejectionEvent>.Empty,
            ReadOnlySpan<FireBlockEvent>.Empty,
            ReadOnlySpan<MovementOutcomeEvent>.Empty,
            state,
            out var written);
        if (fault.Code != StepFaultCode.None || written != state.Length)
        {
            throw new InvalidOperationException($"{name}'s initial state does not serialise: {fault.Code} at {fault.Path}.");
        }

        return state;
    }
}
