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
/// US4.1 and FR-007: fire timing accumulates the configured interval exactly, in integer units, so the shot
/// count over any period never drifts from the configured rate by more than one, even when the interval is
/// not a whole number of steps (data-model.md#weapon-step, the fire loop). One shot costs
/// <c>ShotCost</c> = 60 · stepRateHz units and every step pays <c>Rpm</c> = roundsPerMinute units, so with
/// the trigger held from step 0 the cumulative count after held step k is exactly
/// ceil((k + 1) · rpm / (60 · hz)), and the j-th shot fires on held step floor((j − 1) · 60 · hz / rpm).
/// The tests that go through the world use <see cref="WeaponRuns"/> (one character on the flat world, aim
/// (0, 0), weapon slot 0); the drift sweep calls <c>WeaponStep.Step</c> directly.
/// </summary>
public sealed class FireTimingTests
{
    /// <summary>The rate of US4.1's weapon, rounds per minute.</summary>
    private const int SevenHundredRpm = 700;

    /// <summary>The magazine every world-driven test uses where the placeholder 30 would run dry.</summary>
    private const int LargeMagazine = 100;

    /// <summary>
    /// Rows <c>(hz, rpm)</c>: every rate from 30 to 3600 rounds per minute in steps of 7 (511 values), each
    /// at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<int, int> DriftRows => TestRates.With(DriftRates());

    /// <summary>
    /// US4.1: Given a weapon of 700 rounds per minute at 30 Hz (ShotCost 1800, an interval of 1800 / 700 ≈
    /// 2.57 steps; <c>maxShotsPerStep</c> 1, which R11 allows since 700 ≤ 1800; heat 0) with a 100-round
    /// magazine, Ready in slot 0, When Fire is held for 90 steps (3 s) through the world, Then exactly 35 shot
    /// events fire; after held step k the cumulative count is ceil((k + 1) · 700 / 1800) for every k in
    /// 0..89; the j-th shot fires on held step floor((j − 1) · 1800 / 700) (0, 2, 5, 7, 10, … for j = 1..5),
    /// within one step of its exact time (j − 1) · 1800 / 700; no step fires more than one shot; and with
    /// enough ammunition no step reports a fire block.
    /// </summary>
    [Fact]
    public void SevenHundredRpmAtThirtyHzFiresExactly35ShotsInThreeSeconds()
    {
        const int Hz = 30;
        const int HeldSteps = 3 * Hz;
        var run = WeaponRuns.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = Hz;
            c.Weapons![0]!.RoundsPerMinute = SevenHundredRpm;
            c.Weapons![0]!.MagazineSize = LargeMagazine;
        }));
        var weapon = run.Config.Weapons[0];
        var shotCost = run.Config.ShotCost;
        Assert.Equal(SevenHundredRpm, weapon.Rpm);
        Assert.Equal(1800, shotCost);
        Assert.Equal(1, weapon.MaxShotsPerStep);
        Assert.Equal(LargeMagazine, weapon.MagazineSize);
        Assert.Equal(0u, weapon.HeatPerShotScaled);
        Assert.True(
            weapon.Rpm <= weapon.MaxShotsPerStep * shotCost,
            $"R11 must hold: Rpm {weapon.Rpm} > maxShotsPerStep {weapon.MaxShotsPerStep} × ShotCost {shotCost}");

        var held = run.Hold(Buttons.Fire, HeldSteps, 0);

        var shotSteps = new List<int>();
        for (var k = 0; k < HeldSteps; k++)
        {
            var snapshot = held[k];
            var label = $"700 rpm at 30 Hz, held step {k}";
            Assert.True(snapshot.Step == (uint)(k + 1), $"{label}: the report is for step {snapshot.Step}, expected {k + 1}");
            Assert.True(snapshot.Shots.Count <= 1, $"{label}: {snapshot.Shots.Count} shots in one step, expected at most 1");
            Assert.True(snapshot.FireBlocks.Count == 0, $"{label}: fire blocks [{string.Join(", ", snapshot.FireBlocks)}] with enough ammunition");
            foreach (var shot in snapshot.Shots)
            {
                Assert.True(shot.Slot == 0, $"{label}: a shot from slot {shot.Slot}, expected 0");
                shotSteps.Add(k);
            }

            var expected = CeilDiv((long)(k + 1) * SevenHundredRpm, shotCost);
            Assert.True(
                shotSteps.Count == expected,
                $"{label}: {shotSteps.Count} shots so far, expected ceil(({k} + 1) · 700 / 1800) = {expected}");
        }

        Assert.True(shotSteps.Count == 35, $"700 rpm at 30 Hz: {shotSteps.Count} shots in 3 s, expected exactly 35");
        int[] firstFive = [0, 2, 5, 7, 10];
        Assert.True(
            shotSteps.Take(5).SequenceEqual(firstFive),
            $"700 rpm at 30 Hz: the first shots fired on held steps [{string.Join(", ", shotSteps.Take(5))}], expected [{string.Join(", ", firstFive)}]");
        for (var j = 1; j <= shotSteps.Count; j++)
        {
            var expectedStep = (int)((long)(j - 1) * shotCost / SevenHundredRpm);
            var exactTime = (j - 1) * (double)shotCost / SevenHundredRpm;
            Assert.True(
                shotSteps[j - 1] == expectedStep,
                $"700 rpm at 30 Hz: shot {j} fired on held step {shotSteps[j - 1]}, expected floor(({j} − 1) · 1800 / 700) = {expectedStep}");
            Assert.True(
                System.Math.Abs(shotSteps[j - 1] - exactTime) < 1.0,
                $"700 rpm at 30 Hz: shot {j} fired on held step {shotSteps[j - 1]}, more than one step from its exact time {exactTime:R}");
        }
    }

    /// <summary>
    /// US4.1 at every rate: the placeholder rifle (600 rpm, magazine 30, one shot every hz/10 steps) held for
    /// 3 · hz steps through the world fires exactly 30 shots, on held steps 0, hz/10, 2 · hz/10, …, 29 · hz/10.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void SixHundredRpmFiresThirtyShotsInThreeSecondsAtEveryRate(int hz)
    {
        var run = WeaponRuns.Create(TestConfigs.At(hz));
        var weapon = run.Config.Weapons[0];
        Assert.Equal(600, weapon.Rpm);
        Assert.Equal(60 * hz, run.Config.ShotCost);
        Assert.Equal(1, weapon.MaxShotsPerStep);
        Assert.Equal(30, weapon.MagazineSize);
        var interval = hz / 10;

        var held = run.Hold(Buttons.Fire, 3 * hz, 0);

        var shotSteps = new List<int>();
        for (var k = 0; k < held.Count; k++)
        {
            Assert.True(held[k].Step == (uint)(k + 1), $"{hz} Hz, held step {k}: the report is for step {held[k].Step}, expected {k + 1}");
            Assert.True(held[k].Shots.Count <= 1, $"{hz} Hz, held step {k}: {held[k].Shots.Count} shots in one step, expected at most 1");
            shotSteps.AddRange(Enumerable.Repeat(k, held[k].Shots.Count));
        }

        var expected = Enumerable.Range(0, 30).Select(j => j * interval).ToArray();
        Assert.True(
            shotSteps.SequenceEqual(expected),
            $"{hz} Hz: shots on held steps [{string.Join(", ", shotSteps)}], expected the 30 steps [{string.Join(", ", expected)}]");
    }

    /// <summary>
    /// FR-007: Given <c>TestConfigs.At(hz)</c> with <c>weapons[0].roundsPerMinute</c> = <paramref name="rpm"/>
    /// and <c>maxShotsPerStep</c> = ceil(rpm / (60 · hz)), the smallest R11 allows, When <c>WeaponStep.Step</c>
    /// is called directly for 60 · hz steps (one minute) with Fire held, and before each call Magazine is set
    /// back to <c>magazineSize</c>, the projectile count is 0 and <c>nextProjectileId</c> is 1, so only the
    /// fire accumulator limits shots, Then at every step k the cumulative count is exactly
    /// ceil((k + 1) · rpm / (60 · hz)), 0 ≤ FireWait &lt; ShotCost, and no step reports a fire block; so the
    /// minute fires exactly rpm shots.
    /// </summary>
    [Theory]
    [MemberData(nameof(DriftRows))]
    public void ShotCountNeverDriftsFromTheRateByMoreThanOne(int hz, int rpm)
    {
        var maxShots = (int)CeilDiv(rpm, 60 * hz);
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.Weapons![0]!.RoundsPerMinute = rpm;
            c.Weapons![0]!.MaxShotsPerStep = maxShots;
        }));
        var weapon = config.Weapons[0];
        var shotCost = config.ShotCost;
        Assert.Equal(rpm, weapon.Rpm);
        Assert.Equal(60 * hz, shotCost);
        Assert.Equal(maxShots, weapon.MaxShotsPerStep);
        Assert.Equal(0u, weapon.HeatPerShotScaled);
        Assert.Equal(FireMode.Auto, weapon.FireMode);
        Assert.True(
            rpm <= maxShots * shotCost && rpm > (maxShots - 1) * shotCost,
            $"{rpm} rpm at {hz} Hz: maxShotsPerStep {maxShots} is not the smallest that R11 allows");

        var character = StandingCharacter();
        WeaponState[] weapons =
        [
            WeaponState.New(weapon.MagazineSize, weapon.ReserveRounds),
            WeaponState.New(config.Weapons[1].MagazineSize, config.Weapons[1].ReserveRounds),
        ];
        var spawned = new Projectile[maxShots];
        var shots = new ShotEvent[maxShots];
        var steps = 60 * hz;
        var cumulative = 0L;

        for (var k = 0; k < steps; k++)
        {
            weapons[0] = weapons[0] with { Magazine = (ushort)weapon.MagazineSize };
            var nextProjectileId = 1u;
            var step = (uint)(k + 1);
            var input = new InputCommand(step, step, Buttons.Fire, character.AimYaw, character.AimPitch, 0);

            var count = WeaponStep.Step(
                config,
                WeaponRuns.Salt,
                step,
                ref character,
                weapons,
                in input,
                0,
                ref nextProjectileId,
                spawned,
                shots,
                out var block);

            cumulative += count;
            var expected = CeilDiv((long)(k + 1) * rpm, shotCost);
            var fireWait = weapons[0].FireWait;

            // Messages are built only on failure: this loop runs about 3.4 million times across the rows.
            if (count < 0 || count > maxShots)
            {
                Assert.Fail($"{rpm} rpm at {hz} Hz, step k = {k}: Step returned {count} shots, outside [0, maxShotsPerStep {maxShots}]");
            }

            if (cumulative != expected)
            {
                Assert.Fail($"{rpm} rpm at {hz} Hz, step k = {k}: {cumulative} shots so far, expected ceil(({k} + 1) · {rpm} / {shotCost}) = {expected}");
            }

            if (fireWait < 0 || fireWait >= shotCost)
            {
                Assert.Fail($"{rpm} rpm at {hz} Hz, step k = {k}: FireWait {fireWait} is outside [0, ShotCost {shotCost})");
            }

            if (block != FireBlock.None)
            {
                Assert.Fail($"{rpm} rpm at {hz} Hz, step k = {k}: fire block {block}, although only the accumulator may limit shots");
            }
        }

        Assert.True(cumulative == rpm, $"{rpm} rpm at {hz} Hz: {cumulative} shots in one minute, expected exactly {rpm}");
    }

    /// <summary>
    /// The edge case of a fire interval shorter than one step: Given, through the world at 30 Hz, a weapon of
    /// 2400 rounds per minute (an interval of 0.75 steps) with <c>maxShotsPerStep</c> 2 and a 1000-round
    /// magazine, When Fire is held for 90 steps, Then no step has more than 2 shot events, held step 0 has
    /// exactly 2, the cumulative count after held step k is ceil((k + 1) · 2400 / 1800) for every k, the total
    /// is 120, and the two shots of one step carry consecutive ShotCounter and ProjectileId values and
    /// different direction bits.
    /// </summary>
    [Fact]
    public void WeaponFasterThanOneStepFiresAtMostMaxShotsPerStepWithoutDrift()
    {
        const int Hz = 30;
        const int Rpm = 2400;
        const int MaxShots = 2;
        const int HeldSteps = 90;
        var run = WeaponRuns.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = Hz;
            c.Weapons![0]!.RoundsPerMinute = Rpm;
            c.Weapons![0]!.MaxShotsPerStep = MaxShots;
            c.Weapons![0]!.MagazineSize = 1000;
        }));
        var weapon = run.Config.Weapons[0];
        var shotCost = run.Config.ShotCost;
        Assert.Equal(Rpm, weapon.Rpm);
        Assert.Equal(1800, shotCost);
        Assert.Equal(MaxShots, weapon.MaxShotsPerStep);
        Assert.Equal(1000, weapon.MagazineSize);

        var held = run.Hold(Buttons.Fire, HeldSteps, 0);

        Assert.True(held[0].Shots.Count == MaxShots, $"2400 rpm at 30 Hz: held step 0 fired {held[0].Shots.Count} shots, expected exactly {MaxShots}");
        var cumulative = 0;
        for (var k = 0; k < HeldSteps; k++)
        {
            var snapshot = held[k];
            var label = $"2400 rpm at 30 Hz, held step {k}";
            Assert.True(snapshot.Step == (uint)(k + 1), $"{label}: the report is for step {snapshot.Step}, expected {k + 1}");
            Assert.True(
                snapshot.Shots.Count <= MaxShots,
                $"{label}: {snapshot.Shots.Count} shots in one step, more than maxShotsPerStep {MaxShots}");
            cumulative += snapshot.Shots.Count;
            var expected = CeilDiv((long)(k + 1) * Rpm, shotCost);
            Assert.True(
                cumulative == expected,
                $"{label}: {cumulative} shots so far, expected ceil(({k} + 1) · 2400 / 1800) = {expected}");

            if (snapshot.Shots.Count == MaxShots)
            {
                var first = snapshot.Shots[0];
                var second = snapshot.Shots[1];
                Assert.True(
                    second.ShotCounter == unchecked(first.ShotCounter + 1u),
                    $"{label}: the step's shots have ShotCounter {first.ShotCounter} and {second.ShotCounter}, expected consecutive values");
                Assert.True(
                    second.ProjectileId == unchecked(first.ProjectileId + 1u),
                    $"{label}: the step's shots spawned projectiles {first.ProjectileId} and {second.ProjectileId}, expected consecutive IDs");
                Assert.True(
                    first.DirectionX != second.DirectionX || first.DirectionY != second.DirectionY || first.DirectionZ != second.DirectionZ,
                    $"{label}: the step's two shots (ShotCounter {first.ShotCounter} and {second.ShotCounter}) have identical direction bits ({first.DirectionX:x8}, {first.DirectionY:x8}, {first.DirectionZ:x8})");
            }
        }

        Assert.True(cumulative == 120, $"2400 rpm at 30 Hz: {cumulative} shots in 90 steps, expected 120");
    }

    /// <summary>30, 37, …, 3600 rounds per minute: 511 values.</summary>
    private static int[] DriftRates()
    {
        var rates = new int[511];
        for (var i = 0; i < rates.Length; i++)
        {
            rates[i] = 30 + (7 * i);
        }

        return rates;
    }

    /// <summary>ceil(<paramref name="numerator"/> / <paramref name="denominator"/>) for a non-negative numerator and a positive denominator.</summary>
    private static long CeilDiv(long numerator, long denominator) => (numerator + denominator - 1) / denominator;

    /// <summary>
    /// The post-movement character of the drift sweep: ID 1 at the origin, at rest, standing and grounded on
    /// level ground, aim (0, 0), two weapons, slot 0 active.
    /// </summary>
    private static CharacterState StandingCharacter() => new()
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
        ActiveSlot = 0,
        WeaponCount = 2,
    };
}
