using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Weapons;

/// <summary>
/// FR-006: the weapon step's sub-steps W0 to W6 and its one-shot steps 1 to 8 (data-model.md#weapon-step,
/// data-model.md#weapon-phases), each pinned on its own. Every call goes to <c>WeaponStep.Step</c>
/// directly through <see cref="StepOnce"/>, which passes a post-movement character (ID 1, standing and
/// grounded, two weapons), the character's two <see cref="WeaponState"/>s, an <see cref="InputCommand"/>
/// carrying the character's aim, and spawn and shot spans of <see cref="CompiledConfig.MaxShotsPerStepMax"/>
/// entries. <see cref="Shooter"/> carries that state from call to call, numbering the steps from 1. Where
/// the step length matters the test is a Theory over <see cref="TestRates.All"/>; the other tests run at
/// 30 Hz. The placeholders compile to: rifle (slot 0) 600 rpm, one shot every hz/10 steps, ReloadSteps
/// 2·hz; launcher (slot 1) EquipSteps 15/23/45; recoil recovery 61 units per step at 30 Hz after a delay
/// of 3 steps.
/// </summary>
public sealed class WeaponStepTests
{
    /// <summary>The match salt every call passes: the phase's one weapon salt.</summary>
    private const ulong Salt = WeaponRuns.Salt;

    /// <summary>An input holding no button.</summary>
    private const Buttons NoButtons = 0;

    /// <summary>
    /// Switching and Reloading count from the step after they are entered (data-model.md#weapon-phases: a
    /// duration of D steps entered at step n is Ready at step n + D and can fire in that step).
    /// Switch: with the rifle active, step n changes to slot 1 with Fire held. The launcher is Switching with
    /// PhaseSteps == EquipSteps after step n, Switching (PhaseSteps EquipSteps − k, block Switching, no shot)
    /// after every step n + k for k below EquipSteps, and at step n + EquipSteps it is Ready and fires one
    /// shot in that step. Reload: with Magazine 0, step n holds Reload and Fire. The rifle is Reloading with
    /// PhaseSteps == ReloadSteps (block Reloading, no shot) until step n + ReloadSteps, where it moves 30
    /// rounds (Reserve 180) and fires, leaving Magazine 29.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void SwitchAndReloadCountFromTheNextStep(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var equip = config.Weapons[1].EquipSteps;
        var reload = config.Weapons[0].ReloadSteps;
        Assert.Equal(hz switch { 20 => 15, 30 => 23, 60 => 45, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, equip);
        Assert.Equal(hz switch { 20 => 40, 30 => 60, 60 => 120, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, reload);

        // Switch: the slot changes to 1 at step n with Fire held, and Fire stays held.
        var switcher = new Shooter(config);
        var n = switcher.NextStep;
        for (var k = 0; k < equip; k++)
        {
            var call = switcher.Step(Buttons.Fire, 1);
            var launcher = switcher.Weapons[1];
            var label = $"{hz} Hz switch, step n + {k} (n = {n})";
            Assert.True(switcher.Character.ActiveSlot == 1, $"{label}: ActiveSlot is {switcher.Character.ActiveSlot}, expected 1");
            Assert.True(launcher.Phase == WeaponPhase.Switching, $"{label}: weapons[1] is {launcher.Phase}, expected Switching");
            Assert.True(
                launcher.PhaseSteps == equip - k,
                $"{label}: weapons[1].PhaseSteps is {launcher.PhaseSteps}, expected EquipSteps {equip} − {k}");
            Assert.True(call.ShotCount == 0, $"{label}: {call.ShotCount} shots fired while Switching");
            Assert.True(call.Block == FireBlock.Switching, $"{label}: fire block {call.Block}, expected Switching");
            Assert.True(switcher.Weapons[0].Phase == WeaponPhase.Ready, $"{label}: the holstered rifle is {switcher.Weapons[0].Phase}, expected Ready");
        }

        var ready = switcher.Step(Buttons.Fire, 1);
        var equipped = switcher.Weapons[1];
        var readyLabel = $"{hz} Hz switch, step n + EquipSteps ({n} + {equip})";
        Assert.True(ready.ShotCount == 1, $"{readyLabel}: {ready.ShotCount} shots, expected the launcher's first shot");
        Assert.True(ready.Shots[0].Slot == 1, $"{readyLabel}: the shot came from slot {ready.Shots[0].Slot}, expected 1");
        Assert.True(ready.Block == FireBlock.None, $"{readyLabel}: fire block {ready.Block}, expected None");
        Assert.True(equipped.Phase == WeaponPhase.Firing, $"{readyLabel}: weapons[1] is {equipped.Phase}, expected Firing after its shot");
        Assert.True(equipped.PhaseSteps == 0, $"{readyLabel}: weapons[1].PhaseSteps is {equipped.PhaseSteps}, expected 0");
        Assert.True(equipped.Magazine == 0, $"{readyLabel}: weapons[1].Magazine is {equipped.Magazine}, expected 0");

        // Reload: Reload and Fire held at step n with an empty magazine, then Fire alone.
        var reloader = new Shooter(config);
        reloader.Weapons[0] = reloader.Weapons[0] with { Magazine = 0 };
        n = reloader.NextStep;
        for (var k = 0; k < reload; k++)
        {
            var call = reloader.Step(k == 0 ? Buttons.Fire | Buttons.Reload : Buttons.Fire, 0);
            var rifle = reloader.Weapons[0];
            var label = $"{hz} Hz reload, step n + {k} (n = {n})";
            Assert.True(rifle.Phase == WeaponPhase.Reloading, $"{label}: weapons[0] is {rifle.Phase}, expected Reloading");
            Assert.True(
                rifle.PhaseSteps == reload - k,
                $"{label}: weapons[0].PhaseSteps is {rifle.PhaseSteps}, expected ReloadSteps {reload} − {k}");
            Assert.True(rifle.Magazine == 0 && rifle.Reserve == 210, $"{label}: Magazine {rifle.Magazine}, Reserve {rifle.Reserve}; expected 0 and 210 until the reload completes");
            Assert.True(call.ShotCount == 0, $"{label}: {call.ShotCount} shots fired while Reloading");
            Assert.True(call.Block == FireBlock.Reloading, $"{label}: fire block {call.Block}, expected Reloading");
        }

        var reloaded = reloader.Step(Buttons.Fire, 0);
        var full = reloader.Weapons[0];
        var reloadedLabel = $"{hz} Hz reload, step n + ReloadSteps ({n} + {reload})";
        Assert.True(reloaded.ShotCount == 1, $"{reloadedLabel}: {reloaded.ShotCount} shots, expected one in the step the reload completes");
        Assert.True(reloaded.Block == FireBlock.None, $"{reloadedLabel}: fire block {reloaded.Block}, expected None");
        Assert.True(full.Magazine == 29, $"{reloadedLabel}: Magazine {full.Magazine}, expected 30 moved in, then 29 after the shot");
        Assert.True(full.Reserve == 180, $"{reloadedLabel}: Reserve {full.Reserve}, expected 210 − 30 = 180");
        Assert.True(full.Phase == WeaponPhase.Firing, $"{reloadedLabel}: weapons[0] is {full.Phase}, expected Firing after its shot");
        Assert.True(full.PhaseSteps == 0, $"{reloadedLabel}: weapons[0].PhaseSteps is {full.PhaseSteps}, expected 0");
    }

    /// <summary>
    /// A duration of 0 completes at entry (data-model.md#weapon-phases). With the launcher's <c>equipMs</c>
    /// 0, a change to slot 1 with Fire held fires the launcher in that same step. With the rifle's
    /// <c>reloadMs</c> 0, Reload and Fire held on an empty magazine move 30 rounds and fire in that same
    /// step: Magazine 29, Reserve 180.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ZeroDurationsCompleteInTheEntryStep(int hz)
    {
        var equipConfig = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Weapons![1]!.EquipMs = 0;
        }));
        Assert.Equal(0, equipConfig.Weapons[1].EquipSteps);
        var switcher = new Shooter(equipConfig);

        var switched = switcher.Step(Buttons.Fire, 1);

        var label = $"{hz} Hz, equipMs 0";
        Assert.True(switcher.Character.ActiveSlot == 1, $"{label}: ActiveSlot is {switcher.Character.ActiveSlot}, expected 1");
        Assert.True(switched.ShotCount == 1, $"{label}: {switched.ShotCount} shots in the entry step, expected 1");
        Assert.True(switched.Shots[0].Slot == 1, $"{label}: the shot came from slot {switched.Shots[0].Slot}, expected 1");
        Assert.True(switched.Block == FireBlock.None, $"{label}: fire block {switched.Block}, expected None");
        Assert.True(switcher.Weapons[1].Phase == WeaponPhase.Firing, $"{label}: weapons[1] is {switcher.Weapons[1].Phase}, expected Firing");
        Assert.True(switcher.Weapons[1].Magazine == 0, $"{label}: weapons[1].Magazine is {switcher.Weapons[1].Magazine}, expected 0");

        var reloadConfig = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Weapons![0]!.ReloadMs = 0;
        }));
        Assert.Equal(0, reloadConfig.Weapons[0].ReloadSteps);
        var reloader = new Shooter(reloadConfig);
        reloader.Weapons[0] = reloader.Weapons[0] with { Magazine = 0, Reserve = 210 };

        var reloaded = reloader.Step(Buttons.Fire | Buttons.Reload, 0);

        label = $"{hz} Hz, reloadMs 0";
        var rifle = reloader.Weapons[0];
        Assert.True(reloaded.ShotCount == 1, $"{label}: {reloaded.ShotCount} shots in the entry step, expected 1");
        Assert.True(reloaded.Block == FireBlock.None, $"{label}: fire block {reloaded.Block}, expected None");
        Assert.True(rifle.Magazine == 29, $"{label}: Magazine {rifle.Magazine}, expected 30 moved in, then 29 after the shot");
        Assert.True(rifle.Reserve == 180, $"{label}: Reserve {rifle.Reserve}, expected 180");
        Assert.True(rifle.Phase == WeaponPhase.Firing, $"{label}: weapons[0] is {rifle.Phase}, expected Firing");
        Assert.True(rifle.PhaseSteps == 0, $"{label}: weapons[0].PhaseSteps is {rifle.PhaseSteps}, expected 0");
    }

    /// <summary>
    /// A released trigger counts FireWait down to 0 and never banks shots (data-model.md#weapon-step, W6).
    /// The rifle (600 rpm) fires one shot, then the trigger is released for 10 · hz/10 steps: after every
    /// step 0 ≤ FireWait &lt; ShotCost, FireWait falls by Rpm per step and stops at exactly 0. Re-pressed at
    /// step p, it fires at p and next at exactly p + hz/10, with no shot between and no step firing two.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ReleasedTriggerNeverBanksShots(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var rpm = config.Weapons[0].Rpm;
        Assert.Equal(600, rpm);
        Assert.Equal(60 * hz, config.ShotCost);
        var interval = hz / 10;
        var shooter = new Shooter(config);

        var first = shooter.Step(Buttons.Fire, 0);

        Assert.True(first.ShotCount == 1, $"{hz} Hz: the first held step fired {first.ShotCount} shots, expected 1");
        var expectedWait = config.ShotCost - rpm;
        AssertFireWait($"{hz} Hz, after the first shot", config, expectedWait, shooter.Weapons[0].FireWait);

        var releasedSteps = 10 * (hz / 10);
        for (var r = 1; r <= releasedSteps; r++)
        {
            var call = shooter.Step(NoButtons, 0);
            expectedWait = System.Math.Max(expectedWait - rpm, 0);
            var label = $"{hz} Hz, released step {r} of {releasedSteps}";
            Assert.True(call.ShotCount == 0, $"{label}: {call.ShotCount} shots with the trigger released");
            Assert.True(call.Block == FireBlock.None, $"{label}: fire block {call.Block} with Fire not held");
            AssertFireWait(label, config, expectedWait, shooter.Weapons[0].FireWait);
        }

        Assert.True(
            shooter.Weapons[0].FireWait == 0,
            $"{hz} Hz: FireWait is {shooter.Weapons[0].FireWait} after {releasedSteps} released steps; it must reach exactly 0 and bank nothing");

        var p = shooter.NextStep;
        var shotSteps = new List<uint>();
        for (var k = 0; k < hz; k++)
        {
            var step = shooter.NextStep;
            var call = shooter.Step(Buttons.Fire, 0);
            var label = $"{hz} Hz, re-pressed, held step {k} (step {step}, p = {p})";
            Assert.True(call.ShotCount <= 1, $"{label}: {call.ShotCount} shots in one step");
            var expectedShots = k % interval == 0 ? 1 : 0;
            Assert.True(call.ShotCount == expectedShots, $"{label}: {call.ShotCount} shots, expected {expectedShots} (one every {interval} steps from p)");
            if (call.ShotCount == 1)
            {
                shotSteps.Add(step);
            }
        }

        Assert.True(shotSteps[0] == p, $"{hz} Hz: the first re-pressed shot fired at step {shotSteps[0]}, expected p = {p}");
        Assert.True(
            shotSteps[1] == p + (uint)interval,
            $"{hz} Hz: the second re-pressed shot fired at step {shotSteps[1]}, expected p + hz/10 = {p + (uint)interval}");
    }

    /// <summary>
    /// A semi-automatic weapon fires once per press (data-model.md#weapon-step, W0 and W5). The rifle with
    /// <c>fireMode</c> Semi and Fire held for 10 steps fires exactly one shot, at held step 0; TriggerLatched
    /// is set from then on, and held steps 1 to 9 report the block AwaitingRelease. One released step
    /// reports no block and clears TriggerLatched. Pressing again fires one shot in that step.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void SemiAutomaticWaitsForRelease(int hz)
    {
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Weapons![0]!.FireMode = FireMode.Semi;
        }));
        Assert.Equal(FireMode.Semi, config.Weapons[0].FireMode);
        var shooter = new Shooter(config);
        var totalShots = 0;

        for (var k = 0; k < 10; k++)
        {
            var call = shooter.Step(Buttons.Fire, 0);
            totalShots += call.ShotCount;
            var flags = shooter.Weapons[0].Flags;
            var label = $"{hz} Hz Semi, held step {k}";
            var expectedShots = k == 0 ? 1 : 0;
            var expectedBlock = k == 0 ? FireBlock.None : FireBlock.AwaitingRelease;
            Assert.True(call.ShotCount == expectedShots, $"{label}: {call.ShotCount} shots, expected {expectedShots}");
            Assert.True(call.Block == expectedBlock, $"{label}: fire block {call.Block}, expected {expectedBlock}");
            Assert.True((flags & WeaponFlags.TriggerLatched) != 0, $"{label}: Flags {flags}, expected TriggerLatched set");
        }

        Assert.True(totalShots == 1, $"{hz} Hz Semi: {totalShots} shots in 10 held steps, expected exactly 1");

        var released = shooter.Step(NoButtons, 0);
        Assert.True(released.ShotCount == 0, $"{hz} Hz Semi, released: {released.ShotCount} shots");
        Assert.True(released.Block == FireBlock.None, $"{hz} Hz Semi, released: fire block {released.Block}, expected None");
        Assert.True(
            (shooter.Weapons[0].Flags & WeaponFlags.TriggerLatched) == 0,
            $"{hz} Hz Semi, released: Flags {shooter.Weapons[0].Flags}, expected TriggerLatched cleared");

        var pressed = shooter.Step(Buttons.Fire, 0);
        Assert.True(pressed.ShotCount == 1, $"{hz} Hz Semi, pressed again: {pressed.ShotCount} shots, expected 1");
        Assert.True(pressed.Block == FireBlock.None, $"{hz} Hz Semi, pressed again: fire block {pressed.Block}, expected None");
    }

    /// <summary>
    /// One shot, step 4 (data-model.md#weapon-step): the projectile spawns at the eye of the post-movement
    /// stance with the muzzle velocity along the shot's direction, and does not inherit the shooter's
    /// velocity. At 30 Hz the shooter (ID 1) is at (10.25, 3.5, −7.75) moving at (3, −2, 1) with
    /// <paramref name="slot"/> active; step 9 is computed with <c>nextProjectileId</c> 41 and Fire held.
    /// The projectile is ID 41 (<c>nextProjectileId</c> now 42), owner 1, the weapon's class, launch step 9,
    /// Origin (10.25, 3.5 + EyeHeight, −7.75) with EyeHeight 1.62 standing and 1.05 crouched, launch
    /// velocity = direction × MuzzleSpeed (600 / 120) bit for bit per component, and Position == Origin.
    /// The shot event is character 1, the slot, ShotCounter 0 and projectile 41.
    /// </summary>
    [Theory]
    [InlineData(0, Stance.Standing)]
    [InlineData(0, Stance.Crouched)]
    [InlineData(1, Stance.Standing)]
    [InlineData(1, Stance.Crouched)]
    public void ShotSpawnsAProjectileAtTheEyeWithTheMuzzleVelocity(int slot, Stance stance)
    {
        var config = TestConfigs.Compiled(30);
        var character = StandingCharacter((byte)slot) with
        {
            Position = new Vec3(10.25f, 3.5f, -7.75f),
            Velocity = new Vec3(3f, -2f, 1f),
            Stance = stance,
        };
        var weapons = NewWeapons(config);
        var nextProjectileId = 41u;

        var call = StepOnce(config, 9, ref character, weapons, Buttons.Fire, (byte)slot, 0, ref nextProjectileId);

        var label = $"slot {slot} {stance}";
        Assert.True(call.ShotCount == 1, $"{label}: {call.ShotCount} shots, expected 1");
        var projectile = call.Spawned[0];
        var shot = call.Shots[0];
        Assert.Equal(41u, projectile.Id);
        Assert.Equal(42u, nextProjectileId);
        Assert.Equal(1u, projectile.OwnerId);
        Assert.Equal(slot, config.Weapons[slot].ProjectileClass);
        Assert.Equal((byte)config.Weapons[slot].ProjectileClass, projectile.Class);
        Assert.Equal(9u, projectile.LaunchStep);

        var eye = config.HitboxLayouts[(int)stance].EyeHeight;
        Assert.Equal(stance == Stance.Standing ? 1.62f : 1.05f, eye);
        AssertBits($"{label} Origin", new Vec3(10.25f, 3.5f + eye, -7.75f), projectile.Origin);

        var muzzle = config.Weapons[slot].MuzzleSpeed;
        Assert.Equal(slot == 0 ? 600f : 120f, muzzle);
        AssertBits(
            $"{label} LaunchVelocity (direction × MuzzleSpeed {muzzle}, no inherited velocity)",
            new Vec3(shot.Direction.X * muzzle, shot.Direction.Y * muzzle, shot.Direction.Z * muzzle),
            projectile.LaunchVelocity);
        AssertBits($"{label} Position", projectile.Origin, projectile.Position);

        Assert.Equal(1u, shot.CharacterId);
        Assert.Equal((byte)slot, shot.Slot);
        Assert.Equal(0u, shot.ShotCounter);
        Assert.Equal(41u, shot.ProjectileId);
    }

    /// <summary>
    /// The fire loop stops at the projectile limits (data-model.md#weapon-step, Fire loop), at 30 Hz.
    /// <c>world-full</c>: with the world holding <c>world.maxProjectiles</c> projectiles, a Firing rifle with
    /// Fire held fires nothing, reports ProjectileLimit and becomes Ready. <c>ids-used-up</c>: with
    /// <c>nextProjectileId</c> 0xFFFFFFFF, nothing fires, ProjectileLimit is reported and
    /// <c>nextProjectileId</c> is unchanged. <c>limit-after-one-shot</c>: at 2400 rpm with
    /// <c>maxShotsPerStep</c> 2 and one free projectile slot, exactly one shot fires, no block is reported
    /// (a shot fired) and the rifle is Firing.
    /// </summary>
    [Theory]
    [InlineData("world-full")]
    [InlineData("ids-used-up")]
    [InlineData("limit-after-one-shot")]
    public void ProjectileLimitStopsTheFireLoop(string caseName)
    {
        var defaults = TestConfigs.Compiled(30);
        var (config, startPhase, projectileCount, startId, expectedShots, expectedBlock, expectedPhase) = caseName switch
        {
            "world-full" => (defaults, WeaponPhase.Firing, defaults.MaxProjectiles, 1u, 0, FireBlock.ProjectileLimit, WeaponPhase.Ready),
            "ids-used-up" => (defaults, WeaponPhase.Ready, 0, 0xFFFFFFFFu, 0, FireBlock.ProjectileLimit, WeaponPhase.Ready),
            "limit-after-one-shot" => LimitAfterOneShot(),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
        var character = StandingCharacter(0);
        var weapons = NewWeapons(config);
        weapons[0] = weapons[0] with { Phase = startPhase };
        var nextProjectileId = startId;

        var call = StepOnce(config, 1, ref character, weapons, Buttons.Fire, 0, projectileCount, ref nextProjectileId);

        var rifle = weapons[0];
        Assert.True(call.ShotCount == expectedShots, $"{caseName}: {call.ShotCount} shots, expected {expectedShots}");
        Assert.True(call.Block == expectedBlock, $"{caseName}: fire block {call.Block}, expected {expectedBlock}");
        Assert.True(rifle.Phase == expectedPhase, $"{caseName}: weapons[0] is {rifle.Phase}, expected {expectedPhase}");
        Assert.True(
            nextProjectileId == startId + (uint)expectedShots,
            $"{caseName}: nextProjectileId is {nextProjectileId}, expected {startId + (uint)expectedShots}");
        Assert.True(
            rifle.Magazine == config.Weapons[0].MagazineSize - expectedShots,
            $"{caseName}: Magazine {rifle.Magazine}, expected {config.Weapons[0].MagazineSize - expectedShots}");
        Assert.True(rifle.ShotCounter == (uint)expectedShots, $"{caseName}: ShotCounter {rifle.ShotCounter}, expected {expectedShots}");

        static (CompiledConfig, WeaponPhase, int, uint, int, FireBlock, WeaponPhase) LimitAfterOneShot()
        {
            var fast = CompiledConfig.Create(TestConfigs.With(c =>
            {
                c.StepRateHz = 30;
                c.Weapons![0]!.RoundsPerMinute = 2400;
                c.Weapons![0]!.MaxShotsPerStep = 2;
            }));
            return (fast, WeaponPhase.Ready, fast.MaxProjectiles - 1, 1u, 1, FireBlock.None, WeaponPhase.Firing);
        }
    }

    /// <summary>
    /// One shot, step 7 with no recovery (data-model.md#weapon-step): with <c>recoilRecoveryDegPerSecond</c> 0,
    /// kicks of 1° (182 units) in pitch and yaw, the placeholder maximums 6° and 3° (1092 and 546 units)
    /// and <c>magazineSize</c> 100, Fire is held for 12 shots. After the n-th shot RecoilPitch == min(182·n,
    /// 1092) and RecoilYaw == clamp(previous ± 182, −546, 546), + when bit 16 of that shot's
    /// <c>Spread.Hash(salt, 1, 0, ShotCounter)</c> is 0 and − when it is 1. Steps without a shot leave both
    /// unchanged.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void RecoilAccumulatesToItsMaximumWithoutRecovery(int hz)
    {
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            var rifle = c.Weapons![0]!;
            rifle.RecoilRecoveryDegPerSecond = 0f;
            rifle.RecoilKickPitchDeg = 1f;
            rifle.RecoilKickYawDeg = 1f;
            rifle.MagazineSize = 100;
        }));
        var compiled = config.Weapons[0];
        Assert.Equal(182, compiled.RecoilKickPitchUnits);
        Assert.Equal(182, compiled.RecoilKickYawUnits);
        Assert.Equal(1092, compiled.RecoilMaxPitchUnits);
        Assert.Equal(546, compiled.RecoilMaxYawUnits);
        Assert.Equal(0, compiled.RecoilRecoveryPerStep);
        var shooter = new Shooter(config);
        var interval = hz / 10;
        var shots = 0;
        var previousPitch = 0;
        var previousYaw = 0;

        for (var k = 0; k <= 11 * interval; k++)
        {
            var call = shooter.Step(Buttons.Fire, 0);
            var weapon = shooter.Weapons[0];
            var label = $"{hz} Hz, held step {k}";
            Assert.True(call.ShotCount <= 1, $"{label}: {call.ShotCount} shots in one step");
            if (call.ShotCount == 1)
            {
                shots++;
                var shot = call.Shots[0];
                Assert.True(shot.ShotCounter == (uint)(shots - 1), $"{label}: shot {shots} has ShotCounter {shot.ShotCounter}, expected {shots - 1}");
                var hash = Spread.Hash(Salt, 1, 0, shot.ShotCounter);
                var kick = ((hash >> 16) & 1UL) == 0 ? 182 : -182;
                var expectedPitch = System.Math.Min(182 * shots, 1092);
                var expectedYaw = System.Math.Clamp(previousYaw + kick, -546, 546);
                Assert.True(
                    weapon.RecoilPitch == expectedPitch,
                    $"{label}: after shot {shots} RecoilPitch is {weapon.RecoilPitch}, expected min(182·{shots}, 1092) = {expectedPitch}");
                Assert.True(
                    weapon.RecoilYaw == expectedYaw,
                    $"{label}: after shot {shots} (hash 0x{hash:X16}, kick {kick}) RecoilYaw is {weapon.RecoilYaw}, expected clamp({previousYaw} + {kick}, −546, 546) = {expectedYaw}");
            }
            else
            {
                Assert.True(
                    weapon.RecoilPitch == previousPitch && weapon.RecoilYaw == previousYaw,
                    $"{label}: recoil moved from ({previousPitch}, {previousYaw}) to ({weapon.RecoilPitch}, {weapon.RecoilYaw}) without a shot and with recovery 0");
            }

            previousPitch = weapon.RecoilPitch;
            previousYaw = weapon.RecoilYaw;
        }

        Assert.True(shots == 12, $"{hz} Hz: {shots} shots in {(11 * interval) + 1} held steps, expected 12");
        Assert.True(previousPitch == 1092, $"{hz} Hz: RecoilPitch after 12 shots is {previousPitch}, expected the maximum 1092");
    }

    /// <summary>
    /// One shot, step 1 (data-model.md#weapon-step): the aim with recoil wraps yaw as a <c>u16</c> and clamps
    /// pitch to [−16384, 16384] before the direction is built, using the recoil from before the shot's own
    /// kick. At 30 Hz the rifle holds the row's recoil with StepsSinceShot 0, so W4's recovery (delay 3
    /// steps) does not run first. Aim yaw 65530 with recoil yaw 18 and aim pitch 16384 with recoil pitch 100
    /// give <c>Spread.Direction(hash, 12, 16384, VersMax)</c>; aim yaw 10 with recoil yaw −30 and no pitch
    /// give <c>Spread.Direction(hash, 65516, 0, VersMax)</c>, bit for bit, with
    /// hash = <c>Spread.Hash(salt, 1, 0, 0)</c>.
    /// </summary>
    [Theory]
    [InlineData((ushort)65530, 18, (short)16384, 100, (ushort)12, (short)16384)]
    [InlineData((ushort)10, -30, (short)0, 0, (ushort)65516, (short)0)]
    public void AimWithRecoilWrapsYawAndClampsPitch(
        ushort aimYaw,
        int recoilYaw,
        short aimPitch,
        int recoilPitch,
        ushort expectedYaw,
        short expectedPitch)
    {
        var config = TestConfigs.Compiled(30);
        Assert.True(
            config.Weapons[0].RecoilRecoveryDelaySteps > 1,
            $"the rifle's RecoilRecoveryDelaySteps is {config.Weapons[0].RecoilRecoveryDelaySteps}; it must exceed 1 so the recoil is not recovered before the shot");
        var character = StandingCharacter(0) with { AimYaw = aimYaw, AimPitch = aimPitch };
        var weapons = NewWeapons(config);
        weapons[0] = weapons[0] with { RecoilYaw = recoilYaw, RecoilPitch = recoilPitch, StepsSinceShot = 0 };
        var nextProjectileId = 1u;

        var call = StepOnce(config, 1, ref character, weapons, Buttons.Fire, 0, 0, ref nextProjectileId);

        var label = $"aim ({aimYaw}, {aimPitch}) with recoil ({recoilYaw}, {recoilPitch})";
        Assert.True(call.ShotCount == 1, $"{label}: {call.ShotCount} shots, expected 1");
        var hash = Spread.Hash(Salt, 1, 0, 0);
        var expected = Spread.Direction(hash, expectedYaw, expectedPitch, config.Weapons[0].VersMax);
        AssertBits($"{label}: direction, expected Spread.Direction(hash, {expectedYaw}, {expectedPitch}, VersMax)", expected, call.Shots[0].Direction);
    }

    /// <summary>
    /// W0, W4 and W6 run for every weapon, not only the active one (data-model.md#weapon-step). At 30 Hz
    /// with a heat weapon (<c>heatPerShot</c> 10000) in both slots and slot 0 active, the holstered
    /// launcher starts with Heat 500,000, StepsSinceShot 100, RecoilPitch 100, RecoilYaw −100 and FireWait
    /// 1000. After one neutral step it has Heat 450,000 (cooling 50,000), RecoilPitch 39 and RecoilYaw −39
    /// (recovery 61), FireWait 970 (Rpm 30) and StepsSinceShot 101. The active rifle, at StepsSinceShot
    /// 65535, stays at 65535.
    /// </summary>
    [Fact]
    public void HolsteredWeaponsCoolRecoverAndCountDownTheirWait()
    {
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = 30;
            c.Weapons![0]!.HeatPerShot = 10000;
            c.Weapons![1]!.HeatPerShot = 10000;
        }));
        var launcherConfig = config.Weapons[1];
        Assert.Equal(50_000u, launcherConfig.HeatCooldownPerStep);
        Assert.True(launcherConfig.HeatCooldownDelaySteps <= 101, $"HeatCooldownDelaySteps {launcherConfig.HeatCooldownDelaySteps} must be at most 101");
        Assert.Equal(61, launcherConfig.RecoilRecoveryPerStep);
        Assert.True(launcherConfig.RecoilRecoveryDelaySteps <= 101, $"RecoilRecoveryDelaySteps {launcherConfig.RecoilRecoveryDelaySteps} must be at most 101");
        Assert.Equal(30, launcherConfig.Rpm);
        var shooter = new Shooter(config);
        Assert.Equal(WeaponState.NeverFired, shooter.Weapons[0].StepsSinceShot);
        shooter.Weapons[1] = shooter.Weapons[1] with
        {
            Heat = 500_000,
            StepsSinceShot = 100,
            RecoilPitch = 100,
            RecoilYaw = -100,
            FireWait = 1000,
        };

        var call = shooter.Step(NoButtons, 0);

        var holstered = shooter.Weapons[1];
        Assert.True(call.ShotCount == 0 && call.Block == FireBlock.None, $"a neutral step fired {call.ShotCount} shots with block {call.Block}");
        Assert.True(holstered.Heat == 450_000, $"holstered Heat is {holstered.Heat}, expected 500,000 − 50,000 = 450,000");
        Assert.True(holstered.RecoilPitch == 39, $"holstered RecoilPitch is {holstered.RecoilPitch}, expected 100 − 61 = 39");
        Assert.True(holstered.RecoilYaw == -39, $"holstered RecoilYaw is {holstered.RecoilYaw}, expected −100 + 61 = −39");
        Assert.True(holstered.FireWait == 970, $"holstered FireWait is {holstered.FireWait}, expected 1000 − 30 = 970");
        Assert.True(holstered.StepsSinceShot == 101, $"holstered StepsSinceShot is {holstered.StepsSinceShot}, expected 101");
        Assert.True(holstered.Phase == WeaponPhase.Ready, $"holstered weapon is {holstered.Phase}, expected Ready");
        Assert.True(
            shooter.Weapons[0].StepsSinceShot == WeaponState.NeverFired,
            $"the active rifle's StepsSinceShot is {shooter.Weapons[0].StepsSinceShot}, expected it to stay saturated at 65535");
    }

    /// <summary>
    /// W4's recoil recovery moves toward 0 and never past it (data-model.md#weapon-step). At 30 Hz the rifle
    /// holds RecoilYaw −20 and RecoilPitch 20 with StepsSinceShot at the recovery delay. After one neutral
    /// step both are exactly 0, not +41 or −41, although the recovery is 61 units per step.
    /// </summary>
    [Fact]
    public void RecoilMovesTowardZeroWithoutCrossing()
    {
        var config = TestConfigs.Compiled(30);
        var rifleConfig = config.Weapons[0];
        Assert.Equal(61, rifleConfig.RecoilRecoveryPerStep);
        var shooter = new Shooter(config);
        shooter.Weapons[0] = shooter.Weapons[0] with
        {
            RecoilYaw = -20,
            RecoilPitch = 20,
            StepsSinceShot = (ushort)rifleConfig.RecoilRecoveryDelaySteps,
        };

        var call = shooter.Step(NoButtons, 0);

        var rifle = shooter.Weapons[0];
        Assert.True(call.ShotCount == 0, $"a neutral step fired {call.ShotCount} shots");
        Assert.True(rifle.RecoilYaw == 0, $"RecoilYaw is {rifle.RecoilYaw}, expected −20 recovered to exactly 0 (never +41)");
        Assert.True(rifle.RecoilPitch == 0, $"RecoilPitch is {rifle.RecoilPitch}, expected 20 recovered to exactly 0 (never −41)");
    }

    /// <summary>
    /// Calls <c>WeaponStep.Step</c> once for character <paramref name="character"/>, computing step
    /// <paramref name="step"/> with <see cref="Salt"/>, an input (sequence and client step equal to the step)
    /// holding <paramref name="buttons"/>, the character's aim and weapon slot <paramref name="slot"/>, and
    /// fresh spawn and shot spans of <see cref="CompiledConfig.MaxShotsPerStepMax"/> entries. It returns the
    /// shot count with copies of the spawned projectiles and shot events it reported, and the fire block.
    /// </summary>
    private static Call StepOnce(
        CompiledConfig config,
        uint step,
        ref CharacterState character,
        WeaponState[] weapons,
        Buttons buttons,
        byte slot,
        int projectileCount,
        ref uint nextProjectileId)
    {
        var input = new InputCommand(step, step, buttons, character.AimYaw, character.AimPitch, slot);
        var spawned = new Projectile[config.MaxShotsPerStepMax];
        var shots = new ShotEvent[config.MaxShotsPerStepMax];

        var count = WeaponStep.Step(
            config,
            Salt,
            step,
            ref character,
            weapons,
            in input,
            projectileCount,
            ref nextProjectileId,
            spawned,
            shots,
            out var block);

        Assert.InRange(count, 0, spawned.Length);
        return new Call(count, spawned[..count], shots[..count], block);
    }

    /// <summary>
    /// The post-movement character every test starts from: ID 1 at the origin, at rest, standing and
    /// grounded on level ground, aim (0, 0), sequence 0, two weapons, slot <paramref name="activeSlot"/> active.
    /// </summary>
    private static CharacterState StandingCharacter(byte activeSlot) => new()
    {
        Id = 1,
        Position = new Vec3(0f, 0f, 0f),
        Velocity = new Vec3(0f, 0f, 0f),
        GroundNormal = new Vec3(0f, 1f, 0f),
        AimYaw = 0,
        AimPitch = 0,
        Stance = Stance.Standing,
        Flags = CharacterFlags.Grounded,
        LastSequence = 0,
        ActiveSlot = activeSlot,
        WeaponCount = 2,
    };

    /// <summary>The character's two weapons as a new character has them: Ready, full magazines and reserves.</summary>
    private static WeaponState[] NewWeapons(CompiledConfig config) =>
    [
        WeaponState.New(config.Weapons[0].MagazineSize, config.Weapons[0].ReserveRounds),
        WeaponState.New(config.Weapons[1].MagazineSize, config.Weapons[1].ReserveRounds),
    ];

    private static void AssertFireWait(string label, CompiledConfig config, int expected, int actual)
    {
        Assert.True(
            actual >= 0 && actual < config.ShotCost,
            $"{label}: FireWait {actual} is outside [0, ShotCost {config.ShotCost})");
        Assert.True(actual == expected, $"{label}: FireWait {actual}, expected {expected}");
    }

    private static void AssertBits(string what, Vec3 expected, Vec3 actual)
    {
        Assert.True(
            BitConverter.SingleToUInt32Bits(actual.X) == BitConverter.SingleToUInt32Bits(expected.X)
                && BitConverter.SingleToUInt32Bits(actual.Y) == BitConverter.SingleToUInt32Bits(expected.Y)
                && BitConverter.SingleToUInt32Bits(actual.Z) == BitConverter.SingleToUInt32Bits(expected.Z),
            $"{what}: ({actual.X:R}, {actual.Y:R}, {actual.Z:R}) differs in bits from the expected ({expected.X:R}, {expected.Y:R}, {expected.Z:R})");
    }

    /// <summary>One <c>WeaponStep.Step</c> call's result: the shots, their projectiles and events, and the block.</summary>
    private sealed record Call(int ShotCount, Projectile[] Spawned, ShotEvent[] Shots, FireBlock Block);

    /// <summary>
    /// <see cref="StandingCharacter"/> with slot 0 active and its <see cref="NewWeapons"/>, carried from one
    /// <see cref="StepOnce"/> call to the next, with the steps numbered from 1, <c>nextProjectileId</c>
    /// starting at 1, and no other projectile in the world. Tests may replace a
    /// <see cref="Weapons"/> entry between calls.
    /// </summary>
    private sealed class Shooter
    {
        private CharacterState _character = StandingCharacter(0);
        private uint _nextProjectileId = 1;

        public Shooter(CompiledConfig config)
        {
            Config = config;
            Weapons = NewWeapons(config);
        }

        public CompiledConfig Config { get; }

        public WeaponState[] Weapons { get; }

        public CharacterState Character => _character;

        /// <summary>The step the next <see cref="Step"/> computes.</summary>
        public uint NextStep { get; private set; } = 1;

        public Call Step(Buttons buttons, byte slot)
        {
            var call = StepOnce(Config, NextStep, ref _character, Weapons, buttons, slot, 0, ref _nextProjectileId);
            NextStep++;
            return call;
        }
    }
}
