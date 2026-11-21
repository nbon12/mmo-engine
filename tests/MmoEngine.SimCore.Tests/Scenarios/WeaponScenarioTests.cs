using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The weapon scenarios of US4. Most build a named <see cref="ScenarioCatalog"/> definition
/// (contracts/simcore-cli.md#named-scenario-streams) with <c>Build(TestConfigs.Default(), world, hz)</c>,
/// unrecorded, over the committed <c>simcore/content/test-world.mmcd</c> (opened read-only through
/// <see cref="RepoPaths"/> and decoded with <see cref="CollisionFile.Decode"/>), step it with
/// <see cref="WeaponRuns.Run"/>, and assert the outcome its acceptance scenario states on every step's shot
/// events, fire-block events and committed weapons. Step s is the stream's entry s − 1; the initial state is
/// at step 0. Before asserting an outcome, each test checks its Given on the stream itself (the initial
/// weapons, the buttons held and the compiled configuration), so a definition that does not set the scenario
/// up fails rather than passing vacuously. The fire-block reasons (US4.6) and the Independent Test arrange
/// their world directly with <see cref="WeaponRuns.Create"/> and step it with <see cref="WeaponRuns.Step"/>.
/// </summary>
public sealed class WeaponScenarioTests
{
    /// <summary>A command that holds no button.</summary>
    private const Buttons NoButtons = 0;

    private static readonly Lazy<CollisionWorld> CommittedTestWorld =
        new(() => CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd")));

    /// <summary>
    /// Rows <c>(reason, hz)</c> of <see cref="FireWhenTheWeaponCannotFireReportsTheReason"/>: the six reasons a
    /// held trigger can be blocked for, each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<FireBlock, int> BlockedFireRows => TestRates.WithRateLast(
        FireBlock.Switching,
        FireBlock.Reloading,
        FireBlock.Empty,
        FireBlock.Overheated,
        FireBlock.AwaitingRelease,
        FireBlock.ProjectileLimit);

    /// <summary>
    /// Rows <c>(slot, hz)</c> of <see cref="EveryWeaponFiresOnTheStepsItsConfigurationImplies"/>: slot 0 (the
    /// rifle, Auto) and slot 1 (the launcher, Semi), each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<int, int> WeaponRows => TestRates.WithRateLast(0, 1);

    /// <summary>The committed test world, decoded once and shared: a <see cref="CollisionWorld"/> is immutable.</summary>
    private static CollisionWorld TestWorld => CommittedTestWorld.Value;

    /// <summary>
    /// US4.3: Given a magazine with 2 rounds and the trigger held (<c>reload-and-empty</c> at
    /// <paramref name="hz"/>: the rifle in slot 0 with Magazine 2 and Reserve 210, Fire held for steps 1 to
    /// hz, then Fire and Reload at step hz + 1), When it is stepped, Then 2 shots fire, at steps 1 and
    /// 1 + hz/10, and no other shot fires in the whole run; every step from 2 + hz/10 to hz (Fire held, Reload
    /// not yet pressed) reports exactly <c>[{1, Empty}]</c>; at step hz + 1 the rifle is Reloading with
    /// PhaseSteps == ReloadSteps (40/60/120) and the step reports Reloading; it stays Reloading, with the
    /// magazine still empty, until step hz + ReloadSteps; and at step hz + 1 + ReloadSteps it is Ready with
    /// Magazine 30 (min(30 − 0, 210)) and Reserve 180.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void TwoRoundMagazineFiresTwiceReportsEmptyThenReloadsFromReserve(int hz)
    {
        var stream = ScenarioCatalog.Get("reload-and-empty").Build(TestConfigs.Default(), TestWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        var rifle = config.Weapons[0];
        var reloadSteps = rifle.ReloadSteps;
        var interval = hz / 10;
        var reloadPress = hz + 1;
        var reloaded = reloadPress + reloadSteps;

        // Given: the stream sets the scenario up.
        Assert.Equal(hz, stream.StepHz);
        Assert.Equal(hz switch { 20 => 40, 30 => 60, 60 => 120, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, reloadSteps);
        Assert.Equal(600, rifle.Rpm);
        Assert.Equal(60 * hz, config.ShotCost);
        Assert.Equal(30, rifle.MagazineSize);
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        Assert.True(initial.Characters.Count == 1 && initial.Characters[0].Id == 1, $"{hz} Hz: the initial state must hold one character, ID 1");
        Assert.True(initial.Characters[0].ActiveSlot == 0, $"{hz} Hz: the initial ActiveSlot is {initial.Characters[0].ActiveSlot}, expected 0");
        Assert.True(
            initial.Weapons[0].Phase == WeaponPhase.Ready && initial.Weapons[0].Magazine == 2 && initial.Weapons[0].Reserve == 210,
            $"{hz} Hz: the initial rifle is {initial.Weapons[0]}; expected Ready with Magazine 2 and Reserve 210");
        Assert.True(
            stream.Steps.Count >= reloaded,
            $"{hz} Hz: the stream has {stream.Steps.Count} steps, too few to reach step {reloaded}, where the reload completes");
        for (var s = 1; s <= reloadPress; s++)
        {
            var command = CommandAt(stream, s);
            var expected = s == reloadPress ? Buttons.Fire | Buttons.Reload : Buttons.Fire;
            Assert.True(
                command.Buttons == expected && command.WeaponSlot == 0,
                $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected {expected} with WeaponSlot 0");
        }

        var snapshots = WeaponRuns.Run(stream, TestWorld);

        Assert.Equal(stream.Steps.Count, snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Step == (uint)(i + 1), $"{hz} Hz: entry {i} reported step {snapshots[i].Step}, expected {i + 1}");
        }

        var shotSteps = snapshots.SelectMany(snapshot => snapshot.Shots.Select(_ => (int)snapshot.Step)).ToArray();
        int[] expectedShotSteps = [1, 1 + interval];
        Assert.True(
            shotSteps.SequenceEqual(expectedShotSteps),
            $"{hz} Hz: shots at steps [{string.Join(", ", shotSteps)}] in the whole run, expected exactly [{string.Join(", ", expectedShotSteps)}]");

        for (var s = 2 + interval; s <= hz; s++)
        {
            var snapshot = At(snapshots, s);
            Assert.True(snapshot.Shots.Count == 0, $"{hz} Hz, step {s}: {snapshot.Shots.Count} shots from an empty magazine");
            AssertBlocks($"{hz} Hz, step {s} (Fire held on an empty magazine)", snapshot, FireBlock.Empty);
        }

        var press = At(snapshots, reloadPress);
        var reloading = press.Weapons[0];
        Assert.True(
            reloading.Phase == WeaponPhase.Reloading && reloading.PhaseSteps == reloadSteps,
            $"{hz} Hz, step {reloadPress} (Reload pressed): weapons[0] is {reloading.Phase} with PhaseSteps {reloading.PhaseSteps}; expected Reloading with ReloadSteps {reloadSteps}");
        AssertBlocks($"{hz} Hz, step {reloadPress} (Reload and Fire held)", press, FireBlock.Reloading);

        for (var s = reloadPress; s < reloaded; s++)
        {
            var weapon = At(snapshots, s).Weapons[0];
            var left = reloadSteps - (s - reloadPress);
            Assert.True(
                weapon.Phase == WeaponPhase.Reloading && weapon.PhaseSteps == left && weapon.Magazine == 0 && weapon.Reserve == 210,
                $"{hz} Hz, step {s}: weapons[0] is {weapon.Phase}, PhaseSteps {weapon.PhaseSteps}, Magazine {weapon.Magazine}, Reserve {weapon.Reserve}; expected Reloading, {left}, 0 and 210 until the reload time has passed");
        }

        var done = At(snapshots, reloaded).Weapons[0];
        Assert.True(
            done.Phase == WeaponPhase.Ready && done.PhaseSteps == 0,
            $"{hz} Hz, step {reloaded} (ReloadSteps {reloadSteps} after the press): weapons[0] is {done.Phase} with PhaseSteps {done.PhaseSteps}; expected Ready with 0");
        Assert.True(
            done.Magazine == 30 && done.Reserve == 180,
            $"{hz} Hz, step {reloaded}: Magazine {done.Magazine}, Reserve {done.Reserve}; expected min(30 − 0, 210) = 30 moved in, leaving 180");
    }

    /// <summary>
    /// US4.4 at 30 Hz: Given a heat weapon fired continuously (<c>heat-overheat</c> at its own 30 Hz: the rifle
    /// with <c>heatPerShot</c> 10000 in slot 0, compiled to HeatPerShotScaled 300,000, OverheatThresholdScaled
    /// 3,000,000, HeatRecoveryScaled 1,200,000, HeatCooldownPerStep 50,000 and HeatCooldownDelaySteps 8, with
    /// Fire held at steps 1 to 72), When its heat reaches the overheat threshold, Then it stops firing and
    /// resumes only after heat has fallen to the recovery level: shots at exactly steps 1, 4, …, 28 (10
    /// shots), with Heat 300,000 · k after the k-th; after step 28 Heat is 3,000,000 and the phase Overheated;
    /// every step 29 to 70 fires nothing, stays Overheated and reports exactly <c>[{1, Overheated}]</c>; Heat
    /// stays 3,000,000 after steps 29 to 35 (the cooldown delay), then falls by 50,000 a step, to
    /// 3,000,000 − 50,000 · (s − 35) after each step s in 36 to 70; step 71 cools to 1,200,000 first and fires
    /// exactly one shot (the 11th), committing Heat 1,500,000 and phase Firing; and step 72 fires nothing and
    /// reports no block.
    /// </summary>
    [Fact]
    public void HeatWeaponOverheatsAndResumesAtTheRecoveryLevel()
    {
        const int Hz = 30;
        var definition = ScenarioCatalog.Get("heat-overheat");
        Assert.Equal(Hz, definition.StepRateHz);
        var stream = definition.Build(TestConfigs.Default(), TestWorld);
        var config = CompiledConfig.Create(stream.Config);
        var heat = config.Weapons[0];

        // Given: the heat test weapon at 30 Hz, new, with Fire held at steps 1 to 72.
        Assert.Equal(Hz, stream.StepHz);
        Assert.Equal(300_000u, heat.HeatPerShotScaled);
        Assert.Equal(3_000_000u, heat.OverheatThresholdScaled);
        Assert.Equal(1_200_000u, heat.HeatRecoveryScaled);
        Assert.Equal(50_000u, heat.HeatCooldownPerStep);
        Assert.Equal(8, heat.HeatCooldownDelaySteps);
        Assert.Equal(600, heat.Rpm);
        Assert.Equal(1800, config.ShotCost);
        Assert.Equal(30, heat.MagazineSize);
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        Assert.True(initial.Characters.Count == 1 && initial.Characters[0].Id == 1, "the initial state must hold one character, ID 1");
        Assert.True(initial.Characters[0].ActiveSlot == 0, $"the initial ActiveSlot is {initial.Characters[0].ActiveSlot}, expected 0");
        Assert.True(
            initial.Weapons[0].Phase == WeaponPhase.Ready && initial.Weapons[0].Heat == 0 && initial.Weapons[0].Magazine == 30,
            $"the initial heat weapon is {initial.Weapons[0]}; expected Ready with Heat 0 and Magazine 30");
        Assert.True(stream.Steps.Count >= 72, $"the stream has {stream.Steps.Count} steps; Fire is held at steps 1 to 72");
        for (var s = 1; s <= 72; s++)
        {
            var command = CommandAt(stream, s);
            Assert.True(
                command.Buttons == Buttons.Fire && command.WeaponSlot == 0,
                $"step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected Fire with WeaponSlot 0");
        }

        var snapshots = WeaponRuns.Run(stream, TestWorld);

        Assert.Equal(stream.Steps.Count, snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Step == (uint)(i + 1), $"entry {i} reported step {snapshots[i].Step}, expected {i + 1}");
        }

        var shotSteps = snapshots.Take(72).SelectMany(snapshot => snapshot.Shots.Select(_ => (int)snapshot.Step)).ToArray();
        var expectedShotSteps = Enumerable.Range(0, 10).Select(j => 1 + (3 * j)).Append(71).ToArray();
        Assert.True(
            shotSteps.SequenceEqual(expectedShotSteps),
            $"shots at steps [{string.Join(", ", shotSteps)}] of 1 to 72, expected exactly [{string.Join(", ", expectedShotSteps)}]");

        for (var s = 1; s <= 28; s++)
        {
            Assert.True(At(snapshots, s).FireBlocks.Count == 0, $"step {s}: fire blocks [{string.Join(", ", At(snapshots, s).FireBlocks)}] before the weapon overheated");
        }

        for (var k = 1; k <= 10; k++)
        {
            var s = 1 + (3 * (k - 1));
            var weapon = At(snapshots, s).Weapons[0];
            Assert.True(
                weapon.Heat == 300_000u * (uint)k,
                $"step {s}: Heat {weapon.Heat} after shot {k}, expected 300,000 · {k} = {300_000 * k}");
        }

        var overheated = At(snapshots, 28).Weapons[0];
        Assert.True(
            overheated.Heat == 3_000_000u && overheated.Phase == WeaponPhase.Overheated,
            $"step 28: Heat {overheated.Heat} and {overheated.Phase} after the 10th shot; expected 3,000,000 and Overheated");

        for (var s = 29; s <= 70; s++)
        {
            var snapshot = At(snapshots, s);
            var weapon = snapshot.Weapons[0];
            var expectedHeat = s <= 35 ? 3_000_000u : 3_000_000u - (50_000u * (uint)(s - 35));
            Assert.True(snapshot.Shots.Count == 0, $"step {s}: {snapshot.Shots.Count} shots while Overheated");
            AssertBlocks($"step {s} (Fire held while Overheated)", snapshot, FireBlock.Overheated);
            Assert.True(weapon.Phase == WeaponPhase.Overheated, $"step {s}: weapons[0] is {weapon.Phase}, expected Overheated until Heat reaches the recovery level");
            Assert.True(
                weapon.Heat == expectedHeat,
                s <= 35
                    ? $"step {s}: Heat {weapon.Heat}, expected 3,000,000 until the 8-step cooldown delay has passed"
                    : $"step {s}: Heat {weapon.Heat}, expected 3,000,000 − 50,000 · ({s} − 35) = {expectedHeat}");
        }

        var resume = At(snapshots, 71);
        var resumed = resume.Weapons[0];
        Assert.True(resume.Shots.Count == 1, $"step 71: {resume.Shots.Count} shots, expected exactly one (the 11th) once cooling reached the recovery level");
        Assert.True(resume.FireBlocks.Count == 0, $"step 71: fire blocks [{string.Join(", ", resume.FireBlocks)}], expected none");
        Assert.True(
            resumed.Heat == heat.HeatRecoveryScaled + heat.HeatPerShotScaled && resumed.Heat == 1_500_000u,
            $"step 71: Heat {resumed.Heat}, expected the recovery level 1,200,000 plus one shot's 300,000 = 1,500,000");
        Assert.True(resumed.Phase == WeaponPhase.Firing, $"step 71: weapons[0] is {resumed.Phase}, expected Firing");

        var after = At(snapshots, 72);
        Assert.True(after.Shots.Count == 0, $"step 72: {after.Shots.Count} shots one step after the 11th");
        Assert.True(after.FireBlocks.Count == 0, $"step 72: fire blocks [{string.Join(", ", after.FireBlocks)}], expected none");
    }

    /// <summary>
    /// US4.4 at the other rates: Given a heat weapon fired continuously (<c>heat-overheat</c> built at
    /// <paramref name="hz"/>: the rifle with <c>heatPerShot</c> 10000 in slot 0, compiled to HeatPerShotScaled
    /// 10,000 · hz, OverheatThresholdScaled 100,000 · hz, HeatRecoveryScaled 40,000 · hz and
    /// HeatCooldownPerStep 50,000, one shot every hz/10 steps, with Fire held from step 1 through the step after
    /// <paramref name="resumeStep"/>), When its heat reaches the overheat threshold, Then it stops firing and
    /// resumes only after heat has fallen to the recovery level: the first 10 shots fall on steps
    /// 1 + j · hz/10 (j = 0..9), the last at <paramref name="tenthShotStep"/>, which leaves Heat 100,000 · hz
    /// and the phase Overheated; every step from <paramref name="tenthShotStep"/> + 1 to
    /// <paramref name="resumeStep"/> − 1 fires nothing, stays Overheated and reports exactly
    /// <c>[{1, Overheated}]</c>; the 11th shot fires at <paramref name="resumeStep"/>, committing Heat
    /// 50,000 · hz (the recovery level plus one shot); the step after it fires nothing and reports no block;
    /// and <paramref name="resumeStep"/> == <paramref name="tenthShotStep"/> + HeatCooldownDelaySteps +
    /// ceil(60,000 · hz / 50,000) − 1, from the compiled configuration: cooling starts once StepsSinceShot
    /// reaches the delay, and takes that many steps of 50,000 to fall by 60,000 · hz.
    /// </summary>
    [Theory]
    [InlineData(20, 19, 47)]
    [InlineData(60, 55, 141)]
    public void HeatWeaponStopsAtTheThresholdAndResumesAtTheRecoveryLevel(int hz, int tenthShotStep, int resumeStep)
    {
        var stream = ScenarioCatalog.Get("heat-overheat").Build(TestConfigs.Default(), TestWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        var heat = config.Weapons[0];
        var interval = hz / 10;
        var perHz = (uint)hz;

        // Given: the heat test weapon at hz, new, with Fire held through the step after the 11th shot.
        Assert.Equal(hz, stream.StepHz);
        Assert.Equal(10_000u * perHz, heat.HeatPerShotScaled);
        Assert.Equal(100_000u * perHz, heat.OverheatThresholdScaled);
        Assert.Equal(40_000u * perHz, heat.HeatRecoveryScaled);
        Assert.Equal(50_000u, heat.HeatCooldownPerStep);
        Assert.Equal(600, heat.Rpm);
        Assert.Equal(60 * hz, config.ShotCost);
        Assert.Equal(1, heat.MaxShotsPerStep);
        Assert.Equal(30, heat.MagazineSize);
        Assert.True(
            heat.HeatCooldownDelaySteps > interval,
            $"{hz} Hz: HeatCooldownDelaySteps {heat.HeatCooldownDelaySteps} must exceed the shot interval {interval}, so no shot cools before the 10th");
        var coolingSteps = CeilDiv(heat.OverheatThresholdScaled - heat.HeatRecoveryScaled, heat.HeatCooldownPerStep);
        Assert.Equal(CeilDiv(60_000L * hz, 50_000L), coolingSteps);
        Assert.Equal(1 + (9 * interval), tenthShotStep);
        Assert.True(
            resumeStep == tenthShotStep + heat.HeatCooldownDelaySteps + coolingSteps - 1,
            $"{hz} Hz: resumeStep {resumeStep}, expected tenthShotStep {tenthShotStep} + HeatCooldownDelaySteps {heat.HeatCooldownDelaySteps} + ceil(60,000 · {hz} / 50,000) {coolingSteps} − 1");
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        Assert.True(initial.Characters.Count == 1 && initial.Characters[0].Id == 1, $"{hz} Hz: the initial state must hold one character, ID 1");
        Assert.True(initial.Characters[0].ActiveSlot == 0, $"{hz} Hz: the initial ActiveSlot is {initial.Characters[0].ActiveSlot}, expected 0");
        Assert.True(
            initial.Weapons[0].Phase == WeaponPhase.Ready && initial.Weapons[0].Heat == 0 && initial.Weapons[0].Magazine == 30,
            $"{hz} Hz: the initial heat weapon is {initial.Weapons[0]}; expected Ready with Heat 0 and Magazine 30");
        Assert.True(
            stream.Steps.Count >= resumeStep + 1,
            $"{hz} Hz: the stream has {stream.Steps.Count} steps, too few to reach step {resumeStep + 1}, the step after the 11th shot");
        for (var s = 1; s <= resumeStep + 1; s++)
        {
            var command = CommandAt(stream, s);
            Assert.True(
                command.Buttons == Buttons.Fire && command.WeaponSlot == 0,
                $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected Fire with WeaponSlot 0");
        }

        var snapshots = WeaponRuns.Run(stream, TestWorld);

        Assert.Equal(stream.Steps.Count, snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Step == (uint)(i + 1), $"{hz} Hz: entry {i} reported step {snapshots[i].Step}, expected {i + 1}");
        }

        var shotSteps = snapshots.Take(resumeStep + 1).SelectMany(snapshot => snapshot.Shots.Select(_ => (int)snapshot.Step)).ToArray();
        var expectedShotSteps = Enumerable.Range(0, 10).Select(j => 1 + (j * interval)).Append(resumeStep).ToArray();
        Assert.True(
            shotSteps.SequenceEqual(expectedShotSteps),
            $"{hz} Hz: shots at steps [{string.Join(", ", shotSteps)}] of 1 to {resumeStep + 1}, expected exactly [{string.Join(", ", expectedShotSteps)}]");

        var overheated = At(snapshots, tenthShotStep).Weapons[0];
        Assert.True(
            overheated.Heat == 100_000u * perHz && overheated.Phase == WeaponPhase.Overheated,
            $"{hz} Hz, step {tenthShotStep}: Heat {overheated.Heat} and {overheated.Phase} after the 10th shot; expected 100,000 · {hz} = {100_000 * hz} and Overheated");

        for (var s = tenthShotStep + 1; s < resumeStep; s++)
        {
            var snapshot = At(snapshots, s);
            Assert.True(snapshot.Shots.Count == 0, $"{hz} Hz, step {s}: {snapshot.Shots.Count} shots while Overheated");
            AssertBlocks($"{hz} Hz, step {s} (Fire held while Overheated)", snapshot, FireBlock.Overheated);
            Assert.True(
                snapshot.Weapons[0].Phase == WeaponPhase.Overheated,
                $"{hz} Hz, step {s}: weapons[0] is {snapshot.Weapons[0].Phase} with Heat {snapshot.Weapons[0].Heat}, expected Overheated until Heat reaches the recovery level");
        }

        var resume = At(snapshots, resumeStep);
        var resumed = resume.Weapons[0];
        Assert.True(resume.Shots.Count == 1, $"{hz} Hz, step {resumeStep}: {resume.Shots.Count} shots, expected exactly one (the 11th) once cooling reached the recovery level");
        Assert.True(resume.FireBlocks.Count == 0, $"{hz} Hz, step {resumeStep}: fire blocks [{string.Join(", ", resume.FireBlocks)}], expected none");
        Assert.True(
            resumed.Heat == 50_000u * perHz && resumed.Heat == heat.HeatRecoveryScaled + heat.HeatPerShotScaled,
            $"{hz} Hz, step {resumeStep}: Heat {resumed.Heat}, expected the recovery level {heat.HeatRecoveryScaled} plus one shot's {heat.HeatPerShotScaled} = 50,000 · {hz}");

        var after = At(snapshots, resumeStep + 1);
        Assert.True(after.Shots.Count == 0, $"{hz} Hz, step {resumeStep + 1}: {after.Shots.Count} shots one step after the 11th");
        Assert.True(after.FireBlocks.Count == 0, $"{hz} Hz, step {resumeStep + 1}: fire blocks [{string.Join(", ", after.FireBlocks)}], expected none");
    }

    /// <summary>
    /// US4.5: Given a burst of shots (<c>rifle-burst</c> at <paramref name="hz"/>: the placeholder rifle in slot
    /// 0, Fire held for 3 s at aim yaw 0 and pitch 2048, then released for 1 s; compiled to kicks of 46 pitch
    /// and 18 yaw units, maximums 1092 and 546, recovery 91/61/30 units per step and a recovery delay of 2/3/6
    /// steps, equal to the shot interval hz/10, so W4 runs in each shot's step before its kick), When it is
    /// stepped, Then each shot adds the configured recoil kick to the aim offset, and the offset recovers at the
    /// configured rate once firing stops:
    /// exactly 30 shots fire, at steps 1 + j · hz/10;
    /// after the n-th shot RecoilPitch == min(max(P(n−1) − (StepsSinceShot reached the delay ? 91/61/30 : 0), 0)
    /// + 46, 1092), with P(n−1) the committed RecoilPitch before that step: 46 after every shot at 20 and 30 Hz,
    /// and 46 + 16 · (n − 1) at 60 Hz (510 after the 30th);
    /// RecoilYaw moves, after recovery, by +18 when bit 16 of the shot's
    /// <c>Spread.Hash(stream salt, 1, 0, ShotCounter)</c> is 0 and by −18 when it is 1, clamped to ±546, so
    /// |RecoilYaw| is 18 after every shot at every rate;
    /// every shot's direction bits equal <c>Spread.Direction(hash, (ushort)(0 + yawBeforeKick),
    /// clamp(2048 + pitchBeforeKick), VersMax)</c>, the first shot's with yaw 0 and pitch 2048 unchanged; at 20
    /// and 30 Hz every shot uses the bare aim, and at 60 Hz shot n uses pitch 2048 + 16 · (n − 1);
    /// and after the last shot, at step L = 1 + 29 · hz/10, both offsets stay unchanged at steps L + 1 to
    /// L + delay − 1; from step L + delay each step moves both toward 0 by exactly the recovery per step, never
    /// past 0, until both are 0: at step L + delay at 20 and 30 Hz, and at step L + 22 at 60 Hz.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void EachShotKicksTheAimAndTheOffsetRecoversAfterFiring(int hz)
    {
        const ushort AimYaw = 0;
        const short AimPitch = 2048;
        const int Shots = 30;
        var stream = ScenarioCatalog.Get("rifle-burst").Build(TestConfigs.Default(), TestWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        var rifle = config.Weapons[0];
        var interval = hz / 10;
        var recovery = rifle.RecoilRecoveryPerStep;
        var delay = rifle.RecoilRecoveryDelaySteps;
        var lastShot = 1 + (29 * interval);
        var bothZero = hz == 60 ? lastShot + 22 : lastShot + delay;

        // Given: the rifle's compiled recoil, a new rifle, and Fire held for 3 s at the aim, then released.
        Assert.Equal(hz, stream.StepHz);
        Assert.Equal(46, rifle.RecoilKickPitchUnits);
        Assert.Equal(18, rifle.RecoilKickYawUnits);
        Assert.Equal(1092, rifle.RecoilMaxPitchUnits);
        Assert.Equal(546, rifle.RecoilMaxYawUnits);
        Assert.Equal(hz switch { 20 => 91, 30 => 61, 60 => 30, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, recovery);
        Assert.Equal(hz switch { 20 => 2, 30 => 3, 60 => 6, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, delay);
        Assert.Equal(interval, delay);
        Assert.Equal(600, rifle.Rpm);
        Assert.Equal(60 * hz, config.ShotCost);
        Assert.Equal(1, rifle.MaxShotsPerStep);
        Assert.Equal(FireMode.Auto, rifle.FireMode);
        Assert.Equal(30, rifle.MagazineSize);
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        Assert.True(initial.Characters.Count == 1 && initial.Characters[0].Id == 1, $"{hz} Hz: the initial state must hold one character, ID 1");
        Assert.True(initial.Characters[0].ActiveSlot == 0, $"{hz} Hz: the initial ActiveSlot is {initial.Characters[0].ActiveSlot}, expected 0");
        var start = initial.Weapons[0];
        Assert.True(
            start.Phase == WeaponPhase.Ready && start.Magazine == 30 && start.RecoilPitch == 0 && start.RecoilYaw == 0 && start.ShotCounter == 0,
            $"{hz} Hz: the initial rifle is {start}; expected Ready with Magazine 30, no recoil and ShotCounter 0");
        Assert.True(
            stream.Steps.Count >= bothZero,
            $"{hz} Hz: the stream has {stream.Steps.Count} steps, too few to reach step {bothZero}, where the recoil is back to 0");
        for (var s = 1; s <= stream.Steps.Count; s++)
        {
            var command = CommandAt(stream, s);
            if (s <= 3 * hz)
            {
                Assert.True(
                    command.Buttons == Buttons.Fire && command.WeaponSlot == 0 && command.AimYaw == AimYaw && command.AimPitch == AimPitch,
                    $"{hz} Hz, step {s}: the definition holds {command.Buttons} at aim ({command.AimYaw}, {command.AimPitch}) with WeaponSlot {command.WeaponSlot}; expected Fire at (0, 2048) with WeaponSlot 0");
            }
            else
            {
                Assert.True(
                    command.Buttons == NoButtons && command.WeaponSlot == 0,
                    $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot} after the 3 s burst; expected no buttons with WeaponSlot 0");
            }
        }

        var snapshots = WeaponRuns.Run(stream, TestWorld);

        Assert.Equal(stream.Steps.Count, snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
        {
            Assert.True(snapshots[i].Step == (uint)(i + 1), $"{hz} Hz: entry {i} reported step {snapshots[i].Step}, expected {i + 1}");
        }

        var shotSteps = snapshots.SelectMany(snapshot => snapshot.Shots.Select(_ => (int)snapshot.Step)).ToArray();
        var expectedShotSteps = Enumerable.Range(0, Shots).Select(j => 1 + (j * interval)).ToArray();
        Assert.True(
            shotSteps.SequenceEqual(expectedShotSteps),
            $"{hz} Hz: shots at steps [{string.Join(", ", shotSteps)}] in the whole run, expected exactly the {Shots} steps [{string.Join(", ", expectedShotSteps)}]");

        var previous = start;
        var n = 0;
        for (var s = 1; s <= lastShot; s++)
        {
            var snapshot = At(snapshots, s);
            var weapon = snapshot.Weapons[0];
            if (snapshot.Shots.Count == 1)
            {
                n++;
                var label = $"{hz} Hz, step {s} (shot {n})";
                var shot = snapshot.Shots[0];
                Assert.True(
                    shot.CharacterId == 1 && shot.Slot == 0 && shot.ShotCounter == (uint)(n - 1),
                    $"{label}: the shot is {shot}; expected character 1, slot 0 and ShotCounter {n - 1}");

                // W0 counts the step before W4 compares StepsSinceShot with the delay.
                var reached = SaturatingIncrement(previous.StepsSinceShot) >= delay;
                var pitchBeforeKick = reached ? System.Math.Max(previous.RecoilPitch - recovery, 0) : previous.RecoilPitch;
                var yawBeforeKick = reached ? TowardZero(previous.RecoilYaw, recovery) : previous.RecoilYaw;
                var hash = Spread.Hash(stream.MatchSalt, 1, 0, shot.ShotCounter);
                var kick = ((hash >> 16) & 1UL) == 0 ? 18 : -18;
                var expectedPitch = System.Math.Min(pitchBeforeKick + 46, 1092);
                var expectedYaw = System.Math.Clamp(yawBeforeKick + kick, -546, 546);
                Assert.True(
                    weapon.RecoilPitch == expectedPitch,
                    $"{label}: RecoilPitch {weapon.RecoilPitch}, expected min(max({previous.RecoilPitch} − {(reached ? recovery : 0)}, 0) + 46, 1092) = {expectedPitch}");
                var concretePitch = hz == 60 ? 46 + (16 * (n - 1)) : 46;
                Assert.True(weapon.RecoilPitch == concretePitch, $"{label}: RecoilPitch {weapon.RecoilPitch}, expected {concretePitch}");
                Assert.True(
                    weapon.RecoilYaw == expectedYaw,
                    $"{label}: RecoilYaw {weapon.RecoilYaw}, expected clamp({yawBeforeKick} + {kick}, −546, 546) = {expectedYaw} (hash 0x{hash:X16}, bit 16 {(hash >> 16) & 1UL})");
                Assert.True(System.Math.Abs(weapon.RecoilYaw) == 18, $"{label}: |RecoilYaw| is {System.Math.Abs(weapon.RecoilYaw)}, expected 18");

                var yaw = unchecked((ushort)(AimYaw + yawBeforeKick));
                var pitch = (short)System.Math.Clamp(AimPitch + pitchBeforeKick, -16384, 16384);
                var concreteAimPitch = hz == 60 ? 2048 + (16 * (n - 1)) : 2048;
                Assert.True(
                    yaw == AimYaw && pitch == concreteAimPitch,
                    $"{label}: the aim with recoil is ({yaw}, {pitch}), expected (0, {concreteAimPitch})");
                var direction = Spread.Direction(hash, yaw, pitch, rifle.VersMax);
                var expectedBits = (BitConverter.SingleToUInt32Bits(direction.X), BitConverter.SingleToUInt32Bits(direction.Y), BitConverter.SingleToUInt32Bits(direction.Z));
                var actualBits = (shot.DirectionX, shot.DirectionY, shot.DirectionZ);
                Assert.True(
                    actualBits == expectedBits,
                    $"{label}: direction bits {actualBits}, expected Spread.Direction(hash, {yaw}, {pitch}, VersMax) = {expectedBits}");
            }

            previous = weapon;
        }

        Assert.True(n == Shots, $"{hz} Hz: {n} shots by step {lastShot}, expected {Shots}");
        var lastPitch = previous.RecoilPitch;
        var lastYaw = previous.RecoilYaw;
        Assert.True(
            lastPitch == (hz == 60 ? 510 : 46),
            $"{hz} Hz, step {lastShot}: RecoilPitch {lastPitch} after the 30th shot, expected {(hz == 60 ? 510 : 46)}");

        for (var s = lastShot + 1; s < lastShot + delay; s++)
        {
            var weapon = At(snapshots, s).Weapons[0];
            Assert.True(
                weapon.RecoilPitch == lastPitch && weapon.RecoilYaw == lastYaw,
                $"{hz} Hz, step {s}: recoil ({weapon.RecoilPitch}, {weapon.RecoilYaw}), expected ({lastPitch}, {lastYaw}) unchanged before the {delay}-step recovery delay has passed");
        }

        for (var s = lastShot + delay; s <= stream.Steps.Count; s++)
        {
            var before = At(snapshots, s - 1).Weapons[0];
            var weapon = At(snapshots, s).Weapons[0];
            var expectedPitch = TowardZero(before.RecoilPitch, recovery);
            var expectedYaw = TowardZero(before.RecoilYaw, recovery);
            Assert.True(
                weapon.RecoilPitch == expectedPitch && weapon.RecoilYaw == expectedYaw,
                $"{hz} Hz, step {s}: recoil ({weapon.RecoilPitch}, {weapon.RecoilYaw}), expected ({before.RecoilPitch}, {before.RecoilYaw}) moved toward 0 by {recovery} without passing it = ({expectedPitch}, {expectedYaw})");
            var zero = weapon.RecoilPitch == 0 && weapon.RecoilYaw == 0;
            Assert.True(
                zero == (s >= bothZero),
                $"{hz} Hz, step {s}: recoil ({weapon.RecoilPitch}, {weapon.RecoilYaw}); both offsets must first be 0 at step {bothZero}");
        }
    }

    /// <summary>
    /// US4.6: Given a fire input when the weapon cannot fire, arranged with <see cref="WeaponRuns.Create"/> on
    /// <c>TestConfigs.At(hz)</c> (<see cref="ArrangeBlockedFire"/> gives each reason's arrangement), When it is
    /// stepped, Then no shot fires and the step reports the reason: no shot event, fire blocks exactly
    /// <c>[{1, reason}]</c>, the report's fire-block counter 1, and every weapon's Magazine and ShotCounter
    /// unchanged. The next step, with the same slot and Fire released, reports no fire block and fires no shot.
    /// </summary>
    [Theory]
    [MemberData(nameof(BlockedFireRows))]
    public void FireWhenTheWeaponCannotFireReportsTheReason(FireBlock reason, int hz)
    {
        var (run, initial, buttons, slot) = ArrangeBlockedFire(reason, hz);
        var label = $"{reason} at {hz} Hz";

        var blocked = run.Step(buttons, slot);

        Assert.True(blocked.Step == 1, $"{label}: the report is for step {blocked.Step}, expected 1");
        Assert.True(blocked.Shots.Count == 0, $"{label}: {blocked.Shots.Count} shots when the weapon cannot fire, expected none");
        AssertBlocks($"{label}, step 1 ({buttons} with WeaponSlot {slot})", blocked, reason);
        Assert.True(blocked.FireBlockCounter == 1, $"{label}: the report's fire-block counter is {blocked.FireBlockCounter}, expected 1");
        for (var s = 0; s < initial.Length; s++)
        {
            var weapon = blocked.Weapons[s];
            Assert.True(
                weapon.Magazine == initial[s].Magazine && weapon.ShotCounter == initial[s].ShotCounter,
                $"{label}: weapons[{s}] has Magazine {weapon.Magazine} and ShotCounter {weapon.ShotCounter}, expected {initial[s].Magazine} and {initial[s].ShotCounter} unchanged");
        }

        var released = run.Step(NoButtons, slot);

        Assert.True(released.Shots.Count == 0, $"{label}, step 2 (Fire released): {released.Shots.Count} shots, expected none");
        Assert.True(
            released.FireBlocks.Count == 0 && released.FireBlockCounter == 0,
            $"{label}, step 2 (Fire released): fire blocks [{string.Join(", ", released.FireBlocks)}] and counter {released.FireBlockCounter}, expected none");
    }

    /// <summary>
    /// US4.6 in a stream: in the <c>reload-and-empty</c> definition at <paramref name="hz"/>, Given Reload
    /// pressed with Fire at step hz + 1 and Fire held alone from step hz + 2 to 1.5 · hz + 1, inside the
    /// rifle's ReloadSteps (2 · hz), every one of those steps reports exactly <c>[{1, Reloading}]</c>; and
    /// Given WeaponSlot 1 with Fire held from step 3.5 · hz + 1 to 4 · hz, inside the launcher's EquipSteps
    /// (15/23/45), every one of those steps reports exactly <c>[{1, Switching}]</c>. No shot fires in either
    /// range. The definition's tail is pinned too, since Phase 7 records this stream: it has 4.5 · hz steps,
    /// and every step from 4 · hz + 1 to the end holds no button with WeaponSlot 1 still selected and reports
    /// no fire block.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks(int hz)
    {
        var stream = ScenarioCatalog.Get("reload-and-empty").Build(TestConfigs.Default(), TestWorld, hz);
        var config = CompiledConfig.Create(stream.Config);
        var reloadPress = hz + 1;
        var reloadingFrom = hz + 2;
        var reloadingTo = (3 * hz / 2) + 1;
        var switchingFrom = (7 * hz / 2) + 1;
        var switchingTo = 4 * hz;
        var lastStep = 9 * hz / 2;

        // Given: the rifle still reloads, and the launcher still switches in, throughout each range.
        Assert.Equal(hz, stream.StepHz);
        Assert.Equal(2 * hz, config.Weapons[0].ReloadSteps);
        Assert.Equal(hz switch { 20 => 15, 30 => 23, 60 => 45, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, config.Weapons[1].EquipSteps);
        Assert.True(
            reloadPress + config.Weapons[0].ReloadSteps > reloadingTo,
            $"{hz} Hz: the reload pressed at step {reloadPress} completes by step {reloadingTo}");
        Assert.True(
            switchingFrom + config.Weapons[1].EquipSteps > switchingTo,
            $"{hz} Hz: the switch at step {switchingFrom} completes by step {switchingTo}");
        Assert.True(
            stream.Steps.Count == lastStep,
            $"{hz} Hz: the stream has {stream.Steps.Count} steps, expected 4.5 s = {lastStep}");
        var press = CommandAt(stream, reloadPress);
        Assert.True(
            press.Buttons == (Buttons.Fire | Buttons.Reload) && press.WeaponSlot == 0,
            $"{hz} Hz, step {reloadPress}: the definition holds {press.Buttons} with WeaponSlot {press.WeaponSlot}; expected Fire and Reload with WeaponSlot 0");
        for (var s = reloadingFrom; s <= reloadingTo; s++)
        {
            var command = CommandAt(stream, s);
            Assert.True(
                command.Buttons == Buttons.Fire && command.WeaponSlot == 0,
                $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected Fire with WeaponSlot 0");
        }

        for (var s = switchingFrom; s <= switchingTo; s++)
        {
            var command = CommandAt(stream, s);
            Assert.True(
                command.Buttons == Buttons.Fire && command.WeaponSlot == 1,
                $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected Fire with WeaponSlot 1");
        }

        for (var s = switchingTo + 1; s <= lastStep; s++)
        {
            var command = CommandAt(stream, s);
            Assert.True(
                command.Buttons == NoButtons && command.WeaponSlot == 1,
                $"{hz} Hz, step {s}: the definition holds {command.Buttons} with WeaponSlot {command.WeaponSlot}; expected no button with WeaponSlot 1 still selected");
        }

        var snapshots = WeaponRuns.Run(stream, TestWorld);

        Assert.Equal(stream.Steps.Count, snapshots.Count);
        for (var s = reloadingFrom; s <= reloadingTo; s++)
        {
            var snapshot = At(snapshots, s);
            Assert.True(snapshot.Step == (uint)s, $"{hz} Hz: entry {s - 1} reported step {snapshot.Step}, expected {s}");
            Assert.True(snapshot.Shots.Count == 0, $"{hz} Hz, step {s}: {snapshot.Shots.Count} shots while Reloading");
            AssertBlocks($"{hz} Hz, step {s} (Fire held while Reloading)", snapshot, FireBlock.Reloading);
        }

        for (var s = switchingFrom; s <= switchingTo; s++)
        {
            var snapshot = At(snapshots, s);
            Assert.True(snapshot.Step == (uint)s, $"{hz} Hz: entry {s - 1} reported step {snapshot.Step}, expected {s}");
            Assert.True(snapshot.Shots.Count == 0, $"{hz} Hz, step {s}: {snapshot.Shots.Count} shots while Switching");
            AssertBlocks($"{hz} Hz, step {s} (slot 1 with Fire held while Switching)", snapshot, FireBlock.Switching);
        }

        for (var s = switchingTo + 1; s <= lastStep; s++)
        {
            var snapshot = At(snapshots, s);
            Assert.True(snapshot.Step == (uint)s, $"{hz} Hz: entry {s - 1} reported step {snapshot.Step}, expected {s}");
            Assert.True(snapshot.Shots.Count == 0, $"{hz} Hz, step {s}: {snapshot.Shots.Count} shots with Fire released");
            Assert.True(
                snapshot.FireBlocks.Count == 0,
                $"{hz} Hz, step {s} (Fire released): fire blocks [{string.Join(", ", snapshot.FireBlocks)}], expected none");
        }
    }

    /// <summary>
    /// US4's Independent Test: Given character 1 holding the trigger with each weapon of the test content
    /// (<see cref="WeaponRuns.Create"/> on <c>TestConfigs.At(hz)</c>, ActiveSlot = <paramref name="slot"/>:
    /// slot 0 the rifle, Auto; slot 1 the launcher, Semi), When it is stepped for 20 · hz steps with WeaponSlot
    /// <paramref name="slot"/>, Fire held at every step for slot 0 and at the even held steps only for slot 1
    /// (held step k is step k + 1, so steps 1, 3, 5, …), and Reload held at every step whose previous committed
    /// Magazine is 0, Then each shot's step, direction, recoil and counters equal the values the configuration
    /// implies. After every step the shots (ShotCounter, ProjectileId and direction bits), the fire-block
    /// reason, NextProjectileId and every weapon's state (Phase, PhaseSteps, FireWait, ShotCounter, Magazine,
    /// Reserve, Heat, StepsSinceShot, RecoilPitch and RecoilYaw, and Flags) equal those of
    /// <see cref="ExpectedWeapon"/>, a reference model in plain integer C# that never calls <c>WeaponStep</c>.
    /// Independently of the model: slot 0's first 30 shots fall on held steps 0, hz/10, …, 29 · hz/10; and slot
    /// 1 fires exactly 6 shots in 20 s (magazine 1 + reserve 5), the first on step 1 and each later one on the
    /// first Fire-held step at or after its reload completed, ReloadSteps (70/105/210) after its Reload press
    /// (the step after the previous shot, whose committed Magazine is 0), after which every Fire-held step
    /// reports exactly <c>[{1, Empty}]</c> with Reserve 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(WeaponRows))]
    public void EveryWeaponFiresOnTheStepsItsConfigurationImplies(int slot, int hz)
    {
        var run = WeaponRuns.Create(TestConfigs.At(hz), activeSlot: (byte)slot);
        var config = run.Config;
        var weapon = config.Weapons[slot];
        var steps = 20 * hz;

        // Given: the placeholder weapons at hz.
        Assert.Equal(hz, config.StepRateHz);
        Assert.Equal(60 * hz, config.ShotCost);
        Assert.Equal(2, config.WeaponSlots);
        Assert.Equal(slot == 0 ? FireMode.Auto : FireMode.Semi, weapon.FireMode);
        Assert.Equal(slot == 0 ? 600 : 30, weapon.Rpm);
        Assert.Equal(slot == 0 ? 30 : 1, weapon.MagazineSize);
        Assert.Equal(slot == 0 ? 210 : 5, weapon.ReserveRounds);
        Assert.Equal(1, weapon.MaxShotsPerStep);
        Assert.Equal(0u, weapon.HeatPerShotScaled);
        Assert.Equal(slot == 0 ? 2 * hz : hz switch { 20 => 70, 30 => 105, 60 => 210, _ => throw new ArgumentOutOfRangeException(nameof(hz)) }, weapon.ReloadSteps);

        var model = new ExpectedWeapon(config, run.MatchSalt, (byte)slot);
        var previousMagazine = (int)model.Weapons[slot].Magazine;
        var previousProjectileCount = 0;
        var snapshots = new WeaponSnapshot[steps];
        for (var k = 0; k < steps; k++)
        {
            var step = k + 1;
            var label = $"slot {slot} at {hz} Hz, step {step}";
            var buttons = (FireHeld(slot, step) ? Buttons.Fire : NoButtons) | (previousMagazine == 0 ? Buttons.Reload : NoButtons);

            var expected = model.Step(buttons, (byte)slot, 0, 0, previousProjectileCount);
            var snapshot = run.Step(buttons, (byte)slot);
            snapshots[k] = snapshot;

            Assert.True(snapshot.Step == (uint)step, $"{label}: the report is for step {snapshot.Step}, expected {step}");
            Assert.True(
                snapshot.Shots.Count == expected.Shots.Count,
                $"{label} ({buttons}): {snapshot.Shots.Count} shots, the model fires {expected.Shots.Count}");
            for (var i = 0; i < expected.Shots.Count; i++)
            {
                Assert.True(
                    snapshot.Shots[i] == expected.Shots[i],
                    $"{label}: shot {i} is {snapshot.Shots[i]}, the model's is {expected.Shots[i]}");
            }

            FireBlockSnapshot[] expectedBlocks = expected.Block == FireBlock.None ? [] : [new FireBlockSnapshot(1, expected.Block)];
            Assert.True(
                snapshot.FireBlocks.SequenceEqual(expectedBlocks) && snapshot.FireBlockCounter == expectedBlocks.Length,
                $"{label} ({buttons}): fire blocks [{string.Join(", ", snapshot.FireBlocks)}] with counter {snapshot.FireBlockCounter}, the model's are [{string.Join(", ", expectedBlocks)}]");
            Assert.True(snapshot.ActiveSlot == model.ActiveSlot, $"{label}: ActiveSlot {snapshot.ActiveSlot}, the model's is {model.ActiveSlot}");
            Assert.True(
                snapshot.NextProjectileId == model.NextProjectileId,
                $"{label}: NextProjectileId {snapshot.NextProjectileId}, the model's is {model.NextProjectileId}");
            for (var s = 0; s < config.WeaponSlots; s++)
            {
                Assert.True(
                    snapshot.Weapons[s] == model.Weapons[s],
                    $"{label} ({buttons}): weapons[{s}] is {snapshot.Weapons[s]}, the model's is {model.Weapons[s]}");
            }

            previousMagazine = snapshot.Weapons[slot].Magazine;
            previousProjectileCount = snapshot.ProjectileCount;
        }

        var shotSteps = snapshots.SelectMany(snapshot => snapshot.Shots.Select(_ => (int)snapshot.Step)).ToArray();
        if (slot == 0)
        {
            var interval = hz / 10;
            var expectedFirst = Enumerable.Range(0, 30).Select(j => 1 + (j * interval)).ToArray();
            Assert.True(
                shotSteps.Length >= 30 && shotSteps.Take(30).SequenceEqual(expectedFirst),
                $"slot 0 at {hz} Hz: the first shots fell on steps [{string.Join(", ", shotSteps.Take(30))}], expected held steps 0, {interval}, …, {29 * interval} (steps [{string.Join(", ", expectedFirst)}])");
            return;
        }

        var reloadSteps = weapon.ReloadSteps;
        var expectedShots = new List<int> { 1 };
        var presses = new List<int>();
        while (expectedShots.Count < 6)
        {
            var press = expectedShots[^1] + 1;
            var next = press + reloadSteps;
            while (!FireHeld(slot, next))
            {
                next++;
            }

            presses.Add(press);
            expectedShots.Add(next);
        }

        Assert.True(
            expectedShots[^1] <= steps,
            $"slot 1 at {hz} Hz: the 6th shot is due at step {expectedShots[^1]}, after the {steps} steps of 20 s");
        Assert.True(
            shotSteps.SequenceEqual(expectedShots),
            $"slot 1 at {hz} Hz: shots at steps [{string.Join(", ", shotSteps)}] in 20 s, expected exactly 6, at [{string.Join(", ", expectedShots)}]");
        foreach (var press in presses)
        {
            var reloading = snapshots[press - 1].Weapons[1];
            Assert.True(
                reloading.Phase == WeaponPhase.Reloading && reloading.PhaseSteps == reloadSteps,
                $"slot 1 at {hz} Hz, step {press} (Reload pressed after a shot): weapons[1] is {reloading.Phase} with PhaseSteps {reloading.PhaseSteps}; expected Reloading with ReloadSteps {reloadSteps}");
        }

        for (var step = expectedShots[^1] + 1; step <= steps; step++)
        {
            var snapshot = snapshots[step - 1];
            Assert.True(snapshot.Weapons[1].Reserve == 0, $"slot 1 at {hz} Hz, step {step}: Reserve {snapshot.Weapons[1].Reserve} after the 6th shot, expected 0");
            if (FireHeld(slot, step))
            {
                AssertBlocks($"slot 1 at {hz} Hz, step {step} (Fire held after the 6th shot)", snapshot, FireBlock.Empty);
            }
        }
    }

    /// <summary>
    /// T180's arrangement for <paramref name="reason"/> at <paramref name="hz"/>, on <c>TestConfigs.At(hz)</c>
    /// with character 1 in slot 0, and the buttons and weapon slot of the step that must be blocked:
    /// <list type="bullet">
    /// <item><description><c>Switching</c>: WeaponSlot 1 with Fire held.</description></item>
    /// <item><description><c>Reloading</c>: Magazine 29, Reload and Fire held.</description></item>
    /// <item><description><c>Empty</c>: Magazine 0, Reserve 210, Fire held.</description></item>
    /// <item><description><c>Overheated</c>: the heat weapon (<c>heatPerShot</c> 10000), weapons[0] Overheated
    /// with Heat 100,000 · hz and StepsSinceShot 0, Fire held.</description></item>
    /// <item><description><c>AwaitingRelease</c>: <c>weapons[0].fireMode</c> Semi, Flags TriggerLatched, Fire
    /// held.</description></item>
    /// <item><description><c>ProjectileLimit</c>: <c>world.maxProjectiles</c> 1 with one projectile already in
    /// the state (Id 1, Owner 1, class 0, LaunchStep 0, Origin and Position (0, 100, 0), LaunchVelocity
    /// (0, 50, 0)) and NextProjectileId 2, Fire held.</description></item>
    /// </list>
    /// It also checks the arrangement's Given on the compiled configuration, so a row that no longer sets its
    /// reason up fails rather than passing vacuously.
    /// </summary>
    /// <returns>The world, every weapon's initial state, and the blocked step's buttons and weapon slot.</returns>
    private static (WeaponRuns Run, WeaponState[] Initial, Buttons Buttons, byte Slot) ArrangeBlockedFire(FireBlock reason, int hz)
    {
        var config = TestConfigs.At(hz);
        Func<int, WeaponState, WeaponState> weapon = static (_, fresh) => fresh;
        Projectile[]? projectiles = null;
        uint? nextProjectileId = null;
        var buttons = Buttons.Fire;
        byte slot = 0;
        switch (reason)
        {
            case FireBlock.Switching:
                slot = 1;
                break;
            case FireBlock.Reloading:
                weapon = static (s, fresh) => s == 0 ? fresh with { Magazine = 29 } : fresh;
                buttons = Buttons.Fire | Buttons.Reload;
                break;
            case FireBlock.Empty:
                weapon = static (s, fresh) => s == 0 ? fresh with { Magazine = 0, Reserve = 210 } : fresh;
                break;
            case FireBlock.Overheated:
                config.Weapons![0]!.HeatPerShot = 10000;
                weapon = (s, fresh) => s == 0 ? fresh with { Phase = WeaponPhase.Overheated, Heat = 100_000u * (uint)hz, StepsSinceShot = 0 } : fresh;
                break;
            case FireBlock.AwaitingRelease:
                config.Weapons![0]!.FireMode = FireMode.Semi;
                weapon = static (s, fresh) => s == 0 ? fresh with { Flags = WeaponFlags.TriggerLatched } : fresh;
                break;
            case FireBlock.ProjectileLimit:
                config.World!.MaxProjectiles = 1;
                projectiles =
                [
                    new Projectile
                    {
                        Id = 1,
                        OwnerId = 1,
                        Class = 0,
                        LaunchStep = 0,
                        Origin = new Vec3(0f, 100f, 0f),
                        LaunchVelocity = new Vec3(0f, 50f, 0f),
                        Position = new Vec3(0f, 100f, 0f),
                    },
                ];
                nextProjectileId = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a reason a held trigger can be blocked for.");
        }

        var compiled = CompiledConfig.Create(config);
        var initial = new WeaponState[compiled.WeaponSlots];
        for (var s = 0; s < initial.Length; s++)
        {
            initial[s] = weapon(s, WeaponState.New(compiled.Weapons[s].MagazineSize, compiled.Weapons[s].ReserveRounds));
        }

        var label = $"{reason} at {hz} Hz";
        switch (reason)
        {
            case FireBlock.Switching:
                Assert.True(compiled.Weapons[1].EquipSteps > 0, $"{label}: the launcher's EquipSteps is 0, so it would be Ready at once");
                break;
            case FireBlock.Reloading:
                Assert.True(compiled.Weapons[0].ReloadSteps > 0, $"{label}: the rifle's ReloadSteps is 0, so the reload would complete at entry");
                Assert.True(
                    initial[0].Magazine < compiled.Weapons[0].MagazineSize && initial[0].Reserve > 0,
                    $"{label}: the rifle is {initial[0]}; a reload needs room in the magazine and rounds in reserve");
                break;
            case FireBlock.Overheated:
                Assert.Equal(10_000u * (uint)hz, compiled.Weapons[0].HeatPerShotScaled);
                Assert.Equal(100_000u * (uint)hz, compiled.Weapons[0].OverheatThresholdScaled);
                Assert.True(
                    compiled.Weapons[0].HeatCooldownDelaySteps > 1,
                    $"{label}: HeatCooldownDelaySteps {compiled.Weapons[0].HeatCooldownDelaySteps} must exceed 1, so StepsSinceShot 1 after W0 does not cool");
                break;
            case FireBlock.AwaitingRelease:
                Assert.Equal(FireMode.Semi, compiled.Weapons[0].FireMode);
                break;
            case FireBlock.ProjectileLimit:
                Assert.Equal(1, compiled.MaxProjectiles);
                break;
            default:
                break;
        }

        return (WeaponRuns.Create(config, 0, weapon, projectiles, nextProjectileId), initial, buttons, slot);
    }

    /// <summary>The command character index 0 sends at step <paramref name="step"/> (entry step − 1).</summary>
    private static InputCommand CommandAt(RecordedStream stream, int step)
    {
        var entry = stream.Steps[step - 1];
        Assert.True(entry.Step == (uint)step, $"entry {step - 1} is step {entry.Step}, expected {step}");
        Assert.True(
            entry.Commands.Length == 1 && entry.Commands[0].CharacterIndex == 0,
            $"step {step}: {entry.Commands.Length} commands; expected exactly one, for character index 0");
        return entry.Commands[0].Command;
    }

    /// <summary>The snapshot of step <paramref name="step"/>: entry step − 1.</summary>
    private static WeaponSnapshot At(IReadOnlyList<WeaponSnapshot> snapshots, int step) => snapshots[step - 1];

    /// <summary>Asserts that the step reported exactly one fire block: character 1, <paramref name="reason"/>.</summary>
    private static void AssertBlocks(string label, WeaponSnapshot snapshot, FireBlock reason)
    {
        Assert.True(
            snapshot.FireBlocks.Count == 1 && snapshot.FireBlocks[0] == new FireBlockSnapshot(1, reason),
            $"{label}: fire blocks [{string.Join(", ", snapshot.FireBlocks)}], expected exactly [{new FireBlockSnapshot(1, reason)}]");
    }

    /// <summary>
    /// Whether T181 holds Fire at <paramref name="step"/>: at every step for slot 0, and at the even held steps
    /// k = step − 1 (steps 1, 3, 5, …) for slot 1.
    /// </summary>
    private static bool FireHeld(int slot, int step) => slot == 0 || (step - 1) % 2 == 0;

    /// <summary>ceil(<paramref name="numerator"/> / <paramref name="denominator"/>) for non-negative values.</summary>
    private static int CeilDiv(long numerator, long denominator) => checked((int)((numerator + denominator - 1) / denominator));

    /// <summary>W0's count: <paramref name="stepsSinceShot"/> + 1, saturating at 65535.</summary>
    private static int SaturatingIncrement(ushort stepsSinceShot) => stepsSinceShot == ushort.MaxValue ? stepsSinceShot : stepsSinceShot + 1;

    /// <summary><paramref name="value"/> moved toward 0 by <paramref name="amount"/>, never past it.</summary>
    private static int TowardZero(int value, int amount) =>
        value > 0 ? System.Math.Max(value - amount, 0) : System.Math.Min(value + amount, 0);

    /// <summary>One step of <see cref="ExpectedWeapon"/>: the shots it fires and the fire block it reports.</summary>
    private sealed record ExpectedStep(IReadOnlyList<ShotSnapshot> Shots, FireBlock Block);

    /// <summary>
    /// T181's reference model: character 1's weapons, advanced one step at a time by data-model.md#weapon-step
    /// in plain integer C#. It applies W0 to W6 in order, the slot-change rows of data-model.md#weapon-phases,
    /// the fire block order (Switching, Reloading, Overheated, Empty, AwaitingRelease) and the fire loop with
    /// its projectile limit, and the eight steps of one shot. The only core calls it makes are
    /// <c>Spread.Hash</c> and <c>Spread.Direction</c> for a shot's direction; it never calls
    /// <c>WeaponStep</c>. Each weapon starts as a new character's, as <see cref="WeaponRuns.Create"/> arranges
    /// it, and NextProjectileId starts at 1.
    /// </summary>
    private sealed class ExpectedWeapon
    {
        private const uint CharacterId = 1;
        private const int MinPitch = -16384;
        private const int MaxPitch = 16384;

        private readonly CompiledConfig _config;
        private readonly ulong _salt;
        private readonly WeaponState[] _weapons;

        public ExpectedWeapon(CompiledConfig config, ulong salt, byte activeSlot)
        {
            _config = config;
            _salt = salt;
            _weapons = new WeaponState[config.WeaponSlots];
            for (var s = 0; s < _weapons.Length; s++)
            {
                _weapons[s] = WeaponState.New(config.Weapons[s].MagazineSize, config.Weapons[s].ReserveRounds);
            }

            ActiveSlot = activeSlot;
            NextProjectileId = 1;
        }

        /// <summary>The character's active slot after the last step.</summary>
        public byte ActiveSlot { get; private set; }

        /// <summary>The next projectile ID after the last step.</summary>
        public uint NextProjectileId { get; private set; }

        /// <summary>Every weapon's state after the last step, by slot.</summary>
        public IReadOnlyList<WeaponState> Weapons => _weapons;

        /// <summary>
        /// Advances one step with the accepted input's <paramref name="buttons"/>, <paramref name="weaponSlot"/>
        /// and aim, with <paramref name="projectileCount"/> projectiles in the world before this character fires.
        /// </summary>
        public ExpectedStep Step(Buttons buttons, byte weaponSlot, ushort aimYaw, short aimPitch, int projectileCount)
        {
            var fire = (buttons & Buttons.Fire) != 0;
            var reload = (buttons & Buttons.Reload) != 0;

            // W0: prepare every weapon.
            for (var s = 0; s < _weapons.Length; s++)
            {
                var w = _weapons[s];
                if (w.StepsSinceShot != ushort.MaxValue)
                {
                    w.StepsSinceShot++;
                }

                if (!fire)
                {
                    w.Flags &= ~WeaponFlags.TriggerLatched;
                }

                _weapons[s] = w;
            }

            // W1: slot change, for the old and the new active weapon.
            var switched = weaponSlot != ActiveSlot;
            if (switched)
            {
                var old = _weapons[ActiveSlot];
                if (old.Phase is WeaponPhase.Firing or WeaponPhase.Reloading or WeaponPhase.Switching)
                {
                    old.Phase = WeaponPhase.Ready;
                }

                old.PhaseSteps = 0;
                _weapons[ActiveSlot] = old;

                var incoming = _weapons[weaponSlot];
                var equipSteps = _config.Weapons[weaponSlot].EquipSteps;
                if (incoming.Phase == WeaponPhase.Overheated)
                {
                    incoming.PhaseSteps = (ushort)equipSteps;
                }
                else if (equipSteps > 0)
                {
                    incoming.Phase = WeaponPhase.Switching;
                    incoming.PhaseSteps = (ushort)equipSteps;
                }

                _weapons[weaponSlot] = incoming;
                ActiveSlot = weaponSlot;
            }

            var slot = ActiveSlot;
            var weapon = _config.Weapons[slot];
            var active = _weapons[slot];

            // W2: the active weapon's count-down, from the step after W1 set it.
            if (!switched
                && active.Phase is WeaponPhase.Switching or WeaponPhase.Reloading or WeaponPhase.Overheated
                && active.PhaseSteps > 0)
            {
                active.PhaseSteps--;
                if (active.PhaseSteps == 0 && active.Phase == WeaponPhase.Switching)
                {
                    active.Phase = WeaponPhase.Ready;
                }
                else if (active.PhaseSteps == 0 && active.Phase == WeaponPhase.Reloading)
                {
                    CompleteReload(ref active, weapon);
                }
            }

            // W3: reload start; there is no automatic reload.
            if (reload
                && active.Phase is WeaponPhase.Ready or WeaponPhase.Firing
                && active.Magazine < weapon.MagazineSize
                && active.Reserve > 0)
            {
                if (weapon.ReloadSteps == 0)
                {
                    CompleteReload(ref active, weapon);
                }
                else
                {
                    active.Phase = WeaponPhase.Reloading;
                    active.PhaseSteps = (ushort)weapon.ReloadSteps;
                }
            }

            _weapons[slot] = active;

            // W4: cooling and recoil recovery for every weapon.
            for (var s = 0; s < _weapons.Length; s++)
            {
                var w = _weapons[s];
                var c = _config.Weapons[s];
                if (w.StepsSinceShot >= c.HeatCooldownDelaySteps)
                {
                    w.Heat = w.Heat > c.HeatCooldownPerStep ? w.Heat - c.HeatCooldownPerStep : 0u;
                    if (w.Phase == WeaponPhase.Overheated && w.Heat <= c.HeatRecoveryScaled)
                    {
                        w.Phase = w.PhaseSteps > 0 ? WeaponPhase.Switching : WeaponPhase.Ready;
                    }
                }

                if (w.StepsSinceShot >= c.RecoilRecoveryDelaySteps)
                {
                    w.RecoilPitch = TowardZero(w.RecoilPitch, c.RecoilRecoveryPerStep);
                    w.RecoilYaw = TowardZero(w.RecoilYaw, c.RecoilRecoveryPerStep);
                }

                _weapons[s] = w;
            }

            // W5: the fire block, then the fire loop, for the active weapon.
            active = _weapons[slot];
            var semi = weapon.FireMode == FireMode.Semi;
            var block = FireBlock.None;
            var shots = new List<ShotSnapshot>();
            if (fire)
            {
                block = active.Phase switch
                {
                    WeaponPhase.Switching => FireBlock.Switching,
                    WeaponPhase.Reloading => FireBlock.Reloading,
                    WeaponPhase.Overheated => FireBlock.Overheated,
                    _ when active.Magazine == 0 => FireBlock.Empty,
                    _ when semi && (active.Flags & WeaponFlags.TriggerLatched) != 0 => FireBlock.AwaitingRelease,
                    _ => FireBlock.None,
                };

                if (block != FireBlock.None)
                {
                    if (active.Phase == WeaponPhase.Firing)
                    {
                        active.Phase = WeaponPhase.Ready;
                    }
                }
                else
                {
                    while (active.FireWait < weapon.Rpm
                        && shots.Count < weapon.MaxShotsPerStep
                        && active.Magazine > 0
                        && active.Phase != WeaponPhase.Overheated
                        && !(semi && (active.Flags & WeaponFlags.TriggerLatched) != 0))
                    {
                        if (projectileCount + shots.Count == _config.MaxProjectiles || NextProjectileId == 0xFFFFFFFFu)
                        {
                            if (shots.Count == 0)
                            {
                                block = FireBlock.ProjectileLimit;
                            }

                            break;
                        }

                        shots.Add(FireOne(ref active, slot, weapon, aimYaw, aimPitch));
                    }

                    if (active.Phase != WeaponPhase.Overheated)
                    {
                        active.Phase = block == FireBlock.ProjectileLimit ? WeaponPhase.Ready : WeaponPhase.Firing;
                    }
                }
            }
            else if (active.Phase == WeaponPhase.Firing)
            {
                active.Phase = WeaponPhase.Ready;
            }

            _weapons[slot] = active;

            // W6: the fire wait of every weapon.
            for (var s = 0; s < _weapons.Length; s++)
            {
                var w = _weapons[s];
                w.FireWait = System.Math.Max(w.FireWait - _config.Weapons[s].Rpm, 0);
                _weapons[s] = w;
            }

            return new ExpectedStep(shots, block);
        }

        /// <summary>Moves min(magazineSize − Magazine, Reserve) rounds in and makes the weapon Ready.</summary>
        private static void CompleteReload(ref WeaponState w, CompiledWeapon weapon)
        {
            var moved = System.Math.Min(weapon.MagazineSize - w.Magazine, w.Reserve);
            w.Magazine = (ushort)(w.Magazine + moved);
            w.Reserve = (ushort)(w.Reserve - moved);
            w.Phase = WeaponPhase.Ready;
            w.PhaseSteps = 0;
        }

        /// <summary>One shot, steps 1 to 8 of data-model.md#weapon-step, returning its shot event.</summary>
        private ShotSnapshot FireOne(ref WeaponState w, int slot, CompiledWeapon weapon, ushort aimYaw, short aimPitch)
        {
            // 1. Aim with the recoil from before this shot's kick.
            var yaw = unchecked((ushort)(aimYaw + w.RecoilYaw));
            var pitch = System.Math.Clamp(aimPitch + w.RecoilPitch, MinPitch, MaxPitch);

            // 2 and 3. The spread hash and the direction.
            var hash = Spread.Hash(_salt, CharacterId, slot, w.ShotCounter);
            var direction = Spread.Direction(hash, yaw, (short)pitch, weapon.VersMax);

            // 4 and 5. The projectile's ID and the shot event.
            var projectileId = NextProjectileId;
            NextProjectileId = projectileId + 1;
            var shot = new ShotSnapshot(
                CharacterId,
                (byte)slot,
                w.ShotCounter,
                projectileId,
                BitConverter.SingleToUInt32Bits(direction.X),
                BitConverter.SingleToUInt32Bits(direction.Y),
                BitConverter.SingleToUInt32Bits(direction.Z));

            // 6. Counters and heat.
            w.Magazine--;
            w.ShotCounter = unchecked(w.ShotCounter + 1u);
            w.Heat += weapon.HeatPerShotScaled;
            if (w.Heat >= weapon.OverheatThresholdScaled)
            {
                w.Phase = WeaponPhase.Overheated;
            }

            // 7. The recoil kick.
            w.RecoilPitch = System.Math.Min(w.RecoilPitch + weapon.RecoilKickPitchUnits, weapon.RecoilMaxPitchUnits);
            var kick = ((hash >> 16) & 1UL) == 0 ? weapon.RecoilKickYawUnits : -weapon.RecoilKickYawUnits;
            w.RecoilYaw = System.Math.Clamp(w.RecoilYaw + kick, -weapon.RecoilMaxYawUnits, weapon.RecoilMaxYawUnits);

            // 8. The fire wait and the trigger latch.
            w.StepsSinceShot = 0;
            w.FireWait += _config.ShotCost;
            if (weapon.FireMode == FireMode.Semi)
            {
                w.Flags |= WeaponFlags.TriggerLatched;
            }

            return shot;
        }
    }
}
