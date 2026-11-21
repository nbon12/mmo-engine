using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: a warmed-up <c>WeaponStep.Step</c>, which stage 3 of every world step runs per
/// character, allocates nothing (data-model.md#weapon-step: the spawn and shot spans are the caller's, and
/// the fire loop is bounded by <c>MaxShotsPerStep</c>). Every row warms its call up twice, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three batches of 1,000 calls into spawn and shot
/// buffers allocated before the count (<see cref="ZeroAllocation"/>), and requires the smallest batch to be
/// exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class WeaponAllocationTests
{
    /// <summary>Rows <c>(slot, hz)</c>: slot 0 (the rifle) and slot 1 (the launcher), each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> Slots => TestRates.WithRateLast(0, 1);

    /// <summary>
    /// Given <see cref="TestConfigs.Compiled"/> at <paramref name="hz"/>, the salt <see cref="WeaponRuns.Salt"/>,
    /// a standing grounded character (ID 1, <c>ActiveSlot</c> = <paramref name="slot"/>), its two placeholder
    /// weapons Ready with full magazines and reserves, <c>nextProjectileId</c> 1, and spawn and shot spans of
    /// that weapon's <c>maxShotsPerStep</c> entries. When the warm-up and three counted batches of 1,000 calls
    /// of <c>WeaponStep.Step(config, salt, step, ref character, weapons, in input, 0, ref nextProjectileId,
    /// spawned, shots, out block)</c> run, with <c>step</c> counting up from 1 across all of them and the input
    /// cycling every <c>4 * hz</c> calls (Fire held for <c>3 * hz</c> calls, then Reload held for one call,
    /// then nothing), and with the weapons and <c>nextProjectileId</c> set back to their start values (struct
    /// copies) at the start of each cycle, Then the smallest batch allocated exactly 0 bytes, and at least one
    /// counted call fired a shot, so the counted calls ran the fire loop and the spawn.
    /// </summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void WeaponStepAllocatesNothing(int slot, int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var salt = WeaponRuns.Salt;
        var activeSlot = (byte)slot;
        var character = new CharacterState
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
        var startRifle = WeaponState.New(config.Weapons[0].MagazineSize, config.Weapons[0].ReserveRounds);
        var startLauncher = WeaponState.New(config.Weapons[1].MagazineSize, config.Weapons[1].ReserveRounds);
        var weapons = new[] { startRifle, startLauncher };
        var nextProjectileId = 1u;
        var maxShots = config.Weapons[slot].MaxShotsPerStep;
        var spawned = new Projectile[maxShots];
        var shots = new ShotEvent[maxShots];
        var fire = new InputCommand(1, 0, Buttons.Fire, 0, 0, activeSlot);
        var reload = new InputCommand(1, 0, Buttons.Reload, 0, 0, activeSlot);
        var neutral = new InputCommand(1, 0, 0, 0, 0, activeSlot);
        var cycle = 4 * hz;
        var fireCalls = 3 * hz;
        var calls = 0;
        var countedCallsThatFired = 0;

        // The batch index MeasureBatches passes is not used: calls numbers the calls across the warm-up
        // and every batch. It is not named _, so out _ below stays a discard.
        var counts = ZeroAllocation.MeasureBatches(batchCall =>
        {
            var position = calls % cycle;
            if (position == 0)
            {
                weapons[0] = startRifle;
                weapons[1] = startLauncher;
                nextProjectileId = 1u;
            }

            var input = position < fireCalls ? fire : (position == fireCalls ? reload : neutral);
            var step = (uint)(calls + 1);
            var fired = WeaponStep.Step(
                config,
                salt,
                step,
                ref character,
                weapons,
                in input,
                0,
                ref nextProjectileId,
                spawned,
                shots,
                out _);
            if (calls >= ZeroAllocation.WarmUpCalls && fired >= 1)
            {
                countedCallsThatFired++;
            }

            calls++;
        });

        ZeroAllocation.AssertABatchAllocatedNothing(
            counts,
            $"slot {slot} at {hz} Hz: WeaponStep.Step cycling Fire ({fireCalls} calls), Reload (1) and nothing ({cycle - fireCalls - 1}) into {maxShots}-entry spawn and shot spans");
        Assert.Equal(ZeroAllocation.TotalCalls, calls);
        Assert.True(
            countedCallsThatFired >= 1,
            $"slot {slot} at {hz} Hz: none of the {ZeroAllocation.Batches * ZeroAllocation.CallsPerBatch} counted calls fired a shot, so the fire loop was not measured");
    }
}
