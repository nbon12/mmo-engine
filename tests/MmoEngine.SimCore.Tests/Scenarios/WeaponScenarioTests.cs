using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The named weapon scenarios of US4 (contracts/simcore-cli.md#named-scenario-streams): each test builds a
/// <see cref="ScenarioCatalog"/> definition with <c>Build(TestConfigs.Default(), world, hz)</c>, unrecorded,
/// over the committed <c>simcore/content/test-world.mmcd</c> (opened read-only through
/// <see cref="RepoPaths"/> and decoded with <see cref="CollisionFile.Decode"/>), steps it with
/// <see cref="WeaponRuns.Run"/>, and asserts the outcome its acceptance scenario states on every step's shot
/// events, fire-block events and committed weapons. Step s is the stream's entry s − 1; the initial state is
/// at step 0. Before asserting an outcome, each test checks its Given on the stream itself (the initial
/// weapons, the buttons held and the compiled configuration), so a definition that does not set the scenario
/// up fails rather than passing vacuously.
/// </summary>
public sealed class WeaponScenarioTests
{
    private static readonly Lazy<CollisionWorld> CommittedTestWorld =
        new(() => CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd")));

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
}
