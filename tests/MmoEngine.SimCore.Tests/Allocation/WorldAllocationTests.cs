using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: a warmed-up <c>SimWorld.Step</c> allocates nothing; every buffer it uses is
/// owned by the world and sized from the configured maxima when the world is created
/// (data-model.md#world-simworld). Each row warms the step up twice, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across ten windows of 1,000 steps
/// (<see cref="ZeroAllocation"/>), and requires every window to be exactly 0 bytes (all ten of a single
/// rerun when exactly one window of the first measurement is not).
/// <para>
/// <see cref="StepAllocatesNothing"/> runs no commands, so it covers the fixed per-step path of every
/// stage: the neutral input, the movement of a character at rest, the weapon step with nothing held, the
/// hitbox build, the <c>ProjectileTargets</c> over the stage-4 scratch with no projectile, and the commit
/// and hash. <see cref="StepWithProjectilesAllocatesNothing"/> drives the live weapon and projectile path
/// that Phases 6 and 7 built: shots, projectiles carried forward in flight, character hits, expiries and
/// the event writers that record them, and it requires every counted window, of the rerun too when it
/// runs, to hold all of them, so every window that must be clean is one that ran them.
/// </para>
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class WorldAllocationTests
{
    private const ulong Salt = 7;

    /// <summary>The shooter's ID; it is character index 0, the only one with a command.</summary>
    private const uint ShooterId = 1;

    /// <summary>The target's ID; it is character index 1 and gets the neutral input every step.</summary>
    private const uint TargetId = 2;

    /// <summary>The target's yaw: it faces −Z, toward the shooter.</summary>
    private const ushort FacingShooter = 32768;

    /// <summary>The shooter's feet: the origin of the flat projectile world, aiming along +Z (yaw 0, pitch 0).</summary>
    private static readonly Vec3 ShooterFeet = new(0f, 0f, 0f);

    /// <summary>
    /// The target's feet: 10 m along +Z. There the rifle's 1.5° spread cone is about 0.26 m in radius, so a
    /// fair share of every rate's shots strike the target. At 50 m it is about 1.3 m, and one counted 60 Hz
    /// window of 1,000 steps held no character hit at all.
    /// </summary>
    private static readonly Vec3 TargetFeet = new(0f, 0f, 10f);

    /// <summary>
    /// A world on <see cref="TestWorlds.Flat"/> with the placeholders at <paramref name="hz"/>, Reset with
    /// <see cref="TestStates.StandingOnFlat"/> and salt 7, steps with an empty command span. Every step
    /// commits.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepAllocatesNothing(int hz)
    {
        var world = new SimWorld(TestConfigs.Compiled(hz), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(TestStates.StandingOnFlat, Salt);
        var faults = 0;

        var measurement = ZeroAllocation.Measure(_ =>
        {
            if (world.Step(ReadOnlySpan<CharacterCommand>.Empty).Fault.Code != StepFaultCode.None)
            {
                faults++;
            }
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(measurement, $"{hz} Hz: SimWorld.Step with no commands");
        Assert.True(
            faults == 0,
            $"{hz} Hz: {faults} of {measurement.Calls} steps reported a fault instead of StepFaultCode.None");
        Assert.Equal((uint)measurement.Calls, world.CurrentStep);
    }

    /// <summary>
    /// Given a world on <see cref="ProjectileWorlds.FlatWorld"/> with the placeholders at <paramref name="hz"/>,
    /// Reset with salt 7 and a state at step 0 holding a shooter (ID 1) at the origin facing +Z and a standing
    /// target (ID 2) 10 m along +Z facing the shooter, both grounded at rest, the shooter's rifle Ready with a
    /// full magazine and a reserve of 65,535 rounds (a u16 the configuration does not bound,
    /// contracts/state-serialization.md#weapon-record), so the rifle never runs dry across the 10,002 steps, or
    /// 20,002 with the rerun, and a <see cref="CharacterCommand"/> array of one entry allocated before the
    /// count. When the warm-up and ten counted windows of 1,000 <c>SimWorld.Step(commands)</c> calls run
    /// (and ten more when the rerun runs, <see cref="ZeroAllocation"/>), with the entry set in place
    /// before each step to <c>CharacterCommand(0, command)</c>, the command holding the next Sequence (1 for
    /// the first step), ClientStep equal to the step being computed, aim yaw 0 and pitch 0, slot 0, and
    /// buttons cycling every <c>4 * hz</c> steps as T172's do (Fire held for <c>3 * hz</c> steps, then Reload
    /// for one step, then nothing), Then every window allocated exactly 0 bytes, no step reported a fault,
    /// the world is at the step that counts every call made, and every counted window that ran holds at
    /// least one shot event, at least one hit event on target 2 with Kind Character, and at least one expiry
    /// event. The spread and the recoil carry some shots of each burst past the target, and those that rise
    /// clear of the ground fly on for many steps and expire at their range, so every window runs the spawn,
    /// the flights carried forward, the capsule tests with the character hit, the expiries and their
    /// writers.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepWithProjectilesAllocatesNothing(int hz)
    {
        var world = new SimWorld(TestConfigs.Compiled(hz), ProjectileWorlds.FlatWorld);
        world.Reset(ShooterAndTarget(), Salt);
        var commands = new CharacterCommand[1];
        var cycle = 4 * hz;
        var fireSteps = 3 * hz;
        var shots = new int[ZeroAllocation.MaxCountedWindows];
        var characterHits = new int[ZeroAllocation.MaxCountedWindows];
        var expiries = new int[ZeroAllocation.MaxCountedWindows];
        var calls = 0;
        var faults = 0;

        // The index Measure passes within a window is not used: calls numbers the calls across the warm-up
        // and every window of both measurements. The StepReport is a ref struct, so it is read inside the
        // call and never captured.
        var measurement = ZeroAllocation.Measure(windowCall =>
        {
            var position = calls % cycle;
            var buttons = position < fireSteps ? Buttons.Fire : (position == fireSteps ? Buttons.Reload : 0);
            var sequence = (uint)(calls + 1);
            commands[0] = new CharacterCommand(0, new InputCommand(sequence, world.CurrentStep + 1u, buttons, 0, 0, 0));
            var report = world.Step(commands);
            if (report.Fault.Code != StepFaultCode.None)
            {
                faults++;
            }
            else if (calls >= ZeroAllocation.WarmUpCalls)
            {
                var window = (calls - ZeroAllocation.WarmUpCalls) / ZeroAllocation.CallsPerWindow;
                var events = report.Events;
                shots[window] += events.Shots.Length;
                expiries[window] += events.Expiries.Length;
                foreach (var hit in events.Hits)
                {
                    if (hit.Kind == HitKind.Character && hit.TargetId == TargetId)
                    {
                        characterHits[window]++;
                    }
                }
            }

            calls++;
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(
            measurement,
            $"{hz} Hz: SimWorld.Step with a shooter firing rifle bursts at a target 10 m away");
        Assert.True(
            faults == 0,
            $"{hz} Hz: {faults} of {measurement.Calls} steps reported a fault instead of StepFaultCode.None");
        Assert.Equal(measurement.Calls, calls);
        Assert.Equal((uint)measurement.Calls, world.CurrentStep);
        for (var w = 0; w < measurement.CountedWindows; w++)
        {
            Assert.True(
                shots[w] >= 1 && characterHits[w] >= 1 && expiries[w] >= 1,
                $"{hz} Hz: counted window {w} of {measurement.CountedWindows} held {shots[w]} shots, {characterHits[w]} hits on target {TargetId} (Kind Character) and {expiries[w]} expiries; every window must hold at least one of each");
        }
    }

    /// <summary>
    /// The canonical state at step 0: the shooter and the target of
    /// <see cref="StepWithProjectilesAllocatesNothing"/>, no projectiles, <c>nextProjectileId</c> 1. The
    /// shooter's rifle holds a full magazine and 65,535 reserve rounds; its launcher and the target's weapons
    /// are the placeholders'.
    /// </summary>
    private static byte[] ShooterAndTarget()
    {
        var shooter = ProjectileWorlds.Shooter(ShooterId, ShooterFeet) with
        {
            Weapons = [TestStates.Rifle with { Reserve = ushort.MaxValue }, TestStates.Launcher],
        };
        var target = ProjectileWorlds.Target(TargetId, TargetFeet, FacingShooter);
        return TestStates.Build(0, [shooter, target]);
    }
}
