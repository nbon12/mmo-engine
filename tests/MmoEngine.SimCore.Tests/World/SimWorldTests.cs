using System.Buffers.Binary;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.World;

/// <summary>
/// The Phase 2 <c>SimWorld</c> (data-model.md#world-simworld, #world-step-order): it steps and hashes
/// the canonical state, refuses at <c>Reset</c> a state that does not fit its configuration or collision
/// world (contracts/state-serialization.md#loading-a-state) and keeps what it had, gives every character
/// without a command the neutral input, and sizes every buffer from the configured maxima when it is
/// created. Every world here is on <see cref="TestWorlds.Flat"/>, whose playable area is [−32, 32] in X
/// and Z.
/// </summary>
public sealed class SimWorldTests
{
    private const ulong Salt = 7;

    /// <summary>The cases of the configuration and world table, plus one byte-only row.</summary>
    public static TheoryData<string> RefusedStates =>
    [
        "65-characters",
        "2-projectiles-over-max-1",
        "3-weapons",
        "position-x-40",
        "position-z-minus-40",
        "grounded-normal-y-0.5",
        "airborne-tilted-normal",
        "ready-phase-steps-1",
        "firing-phase-steps-1",
        "switching-phase-steps-0",
        "switching-phase-steps-16",
        "reloading-phase-steps-0",
        "reloading-phase-steps-61",
        "overheated-phase-steps-16",
        "holstered-overheated-phase-steps-1",
        "magazine-31",
        "fire-wait-1800",
        "fire-wait-minus-1",
        "heat-3000000",
        "heat-3300000-heat-per-shot-10000",
        "recoil-pitch-1093",
        "recoil-yaw-minus-547",
        "holstered-weapon-firing",
        "projectile-class-2",
        "position-x-nan",
    ];

    /// <summary>The weapons at the edges of the phaseSteps and heat rules, which Reset accepts.</summary>
    public static TheoryData<string> AcceptedWeapons =>
    [
        "switching-phase-steps-1",
        "switching-phase-steps-15",
        "reloading-phase-steps-1",
        "reloading-phase-steps-60",
        "overheated-phase-steps-0",
        "overheated-phase-steps-15",
        "holstered-overheated-phase-steps-0",
        "heat-2999999",
        "heat-3299999-heat-per-shot-10000",
    ];

    /// <summary>
    /// One step from <see cref="TestStates.StandingOnFlat"/> with no commands commits step 1: the state's
    /// <c>step</c> field is 1, the state is 136 bytes, and the report's hash is the state hash of the
    /// report's state. The next step is step 2 with another hash, because the step is hashed.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepAdvancesTheStepNumberAndHashesTheCanonicalState(int hz)
    {
        var world = NewWorld(TestConfigs.Compiled(hz));
        world.Reset(TestStates.StandingOnFlat, Salt);

        var first = world.Step([]);

        Assert.Equal(StepFaultCode.None, first.Fault.Code);
        Assert.Equal(1u, first.Step);
        Assert.Equal(1u, world.CurrentStep);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(first.State));
        Assert.Equal(136, first.State.Length);
        Assert.Equal(StateHash.Compute(first.State), first.Hash);
        var firstHash = first.Hash;

        var second = world.Step([]);

        Assert.Equal(StepFaultCode.None, second.Fault.Code);
        Assert.Equal(2u, second.Step);
        Assert.Equal(2u, world.CurrentStep);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(second.State));
        Assert.Equal(StateHash.Compute(second.State), second.Hash);
        Assert.NotEqual(firstHash, second.Hash);
    }

    /// <summary>
    /// <c>Reset</c> runs the byte-only checks and then the checks that need the compiled configuration and
    /// the collision world, and refuses with <c>CoreFormatException</c>, format <c>state</c>, the code and
    /// the field, at the field's offset from the state's first byte. The placeholders at 30 Hz allow 64
    /// characters, 2 weapon slots, a rifle magazine of 30, a rifle <c>EquipSteps</c> of 15 and
    /// <c>ReloadSteps</c> of 60, <c>ShotCost</c> 1800, a heat limit of 3,000,000 (threshold 100,000 × 30, no
    /// heat per shot), recoil of at most 1092 pitch and ±546 yaw units, and 2 projectile classes.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedStates))]
    public void ResetRefusesStatesThatDoNotFitTheConfigurationOrWorld(string caseName)
    {
        var refusal = Refused(caseName);
        var config = refusal.Edit is null ? TestConfigs.Compiled(30) : CompiledConfig.Create(TestConfigs.With(refusal.Edit));
        var world = NewWorld(config);

        var exception = Assert.Throws<CoreFormatException>(() => world.Reset(refusal.State, Salt));

        Assert.Equal("state", exception.Error.Format);
        Assert.Equal(refusal.Code, exception.Error.Code);
        Assert.Equal(refusal.Field, exception.Error.Field);
        Assert.Equal(refusal.Offset, exception.Error.Offset);
        Assert.StartsWith($"state {refusal.Field} at offset {refusal.Offset}: ", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The weapon rules are not tighter than the contract: a weapon at either end of its phase's
    /// <c>phaseSteps</c> range, a holstered Overheated weapon with 0, and heat one below the limit (with
    /// and without heat per shot) load, and the next step commits.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedWeapons))]
    public void ResetAcceptsWeaponsAtTheEdgesOfTheirRules(string caseName)
    {
        var (state, edit) = Accepted(caseName);
        var config = edit is null ? TestConfigs.Compiled(30) : CompiledConfig.Create(TestConfigs.With(edit));
        var world = NewWorld(config);

        world.Reset(state, Salt);
        Assert.Equal(0u, world.CurrentStep);
        var report = world.Step([]);

        Assert.True(report.Fault.Code == StepFaultCode.None, $"{caseName}: the step after Reset faulted with {report.Fault.Code} at {report.Fault.Path}");
        Assert.Equal(1u, report.Step);
        Assert.Equal(1u, world.CurrentStep);
    }

    /// <summary>
    /// A refused <c>Reset</c> changes nothing: the world keeps its step, and its next step is the one a
    /// world that never saw the refused state gives after the same two steps.
    /// </summary>
    [Fact]
    public void ResetKeepsThePreviousWorldOnRefusal()
    {
        var world = NewWorld(TestConfigs.Compiled(30));
        world.Reset(TestStates.StandingOnFlat, Salt);
        Assert.Equal(StepFaultCode.None, world.Step([]).Fault.Code);

        Assert.Throws<CoreFormatException>(() => world.Reset(Refused("position-x-40").State, Salt));
        Assert.Equal(1u, world.CurrentStep);

        var fresh = NewWorld(TestConfigs.Compiled(30));
        fresh.Reset(TestStates.StandingOnFlat, Salt);
        Assert.Equal(StepFaultCode.None, fresh.Step([]).Fault.Code);
        var expected = fresh.Step([]);
        var expectedState = expected.State.ToArray();
        var expectedHash = expected.Hash;

        var next = world.Step([]);

        Assert.Equal(StepFaultCode.None, next.Fault.Code);
        Assert.Equal(2u, next.Step);
        Assert.Equal(expectedHash, next.Hash);
        Assert.Equal(expectedState, next.State.ToArray());
    }

    /// <summary>
    /// Stage 1 merges the commands with the characters, by character index. With
    /// <see cref="TestStates.TwoCharacters"/> and a command for index 1 only, index 0 (character 1: yaw
    /// 16384, pitch −512, <c>lastSequence</c> 5, slot 1) gets the neutral input: no buttons, its own aim and
    /// slot, and its <c>lastSequence</c>. Index 1 (character 4) gets the command.
    /// </summary>
    [Fact]
    public void CharactersWithoutACommandGetTheNeutralInput()
    {
        var world = NewWorld(TestConfigs.Compiled(30));
        world.Reset(TestStates.TwoCharacters, Salt);
        var cmd = new InputCommand(3, 42, Buttons.Forward | Buttons.Walk, 8192, 256, 0);
        CharacterCommand[] commands = [new CharacterCommand(1, cmd)];

        var report = world.Step(commands);

        Assert.Equal(StepFaultCode.None, report.Fault.Code);
        var inputs = world.LastStepInputs;
        Assert.Equal(2, inputs.Length);

        var neutral = inputs[0];
        Assert.Equal((Buttons)0, neutral.Buttons);
        Assert.Equal((ushort)16384, neutral.AimYaw);
        Assert.Equal((short)-512, neutral.AimPitch);
        Assert.Equal((byte)1, neutral.WeaponSlot);
        Assert.Equal(5u, neutral.Sequence);

        Assert.Equal(cmd, inputs[1]);
    }

    /// <summary>
    /// Stage 1 reads the commands in order and skips one whose index is below the character being merged.
    /// With <see cref="TestStates.TwoCharacters"/> and two commands for index 0, the first (sequence 6, Forward)
    /// is index 0's input; the second (sequence 7, Back) is skipped when index 1 is merged, so index 1
    /// (character 4) gets the neutral input, no buttons and its own <c>lastSequence</c> 0, and no input is
    /// rejected.
    /// </summary>
    [Fact]
    public void ACommandForAnIndexAlreadyMergedIsSkipped()
    {
        var world = NewWorld(TestConfigs.Compiled(30));
        world.Reset(TestStates.TwoCharacters, Salt);
        var first = new InputCommand(6, 1, Buttons.Forward, 16384, -512, 1);
        var second = new InputCommand(7, 1, Buttons.Back, 0, 0, 0);
        CharacterCommand[] commands = [new CharacterCommand(0, first), new CharacterCommand(0, second)];

        var report = world.Step(commands);

        Assert.Equal(StepFaultCode.None, report.Fault.Code);
        var inputs = world.LastStepInputs;
        Assert.Equal(2, inputs.Length);
        Assert.Equal(first, inputs[0]);
        Assert.Equal((Buttons)0, inputs[1].Buttons);
        Assert.Equal(0u, inputs[1].Sequence);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(report.State[^6..]));
    }

    /// <summary>
    /// Every buffer is allocated once, at construction, from the maxima of the placeholders on
    /// <see cref="TestWorlds.Flat"/>: the candidate buffer for the widest box the 0.4 m capsule with a
    /// 0.01 m skin sweeps (274 candidates), the 91,672-byte state buffer (64 characters, 1024 projectiles,
    /// 2 slots, 1 shot per step), 64 shots, 1024 hits and expiries, and 64 of each other event.
    /// </summary>
    [Fact]
    public void ConstructorSizesEveryBufferFromTheMaxima()
    {
        var config = TestConfigs.Compiled(30);
        var collision = TestWorlds.Load(TestWorlds.Flat());

        var world = new SimWorld(config, collision);

        Assert.Equal(collision.MaxQueryCandidates((3f * 0.4f) + (2f * 0.01f)), world.CandidateCapacity);
        Assert.Equal(274, world.CandidateCapacity);
        Assert.Equal(91_672, world.StateBufferLength);
        Assert.Equal(config.MaxStateLength, world.StateBufferLength);
        Assert.Equal(64, world.ShotCapacity);
        Assert.Equal(1024, world.HitCapacity);
        Assert.Equal(1024, world.ExpiryCapacity);
        Assert.Equal(64, world.InputRejectionCapacity);
        Assert.Equal(64, world.FireBlockCapacity);
        Assert.Equal(64, world.MovementOutcomeCapacity);
    }

    private static SimWorld NewWorld(CompiledConfig config) => new(config, LoadFlat());

    private static CollisionWorld LoadFlat() => TestWorlds.Load(TestWorlds.Flat());

    /// <summary>
    /// A state <c>Reset</c> refuses, the configuration edit it is refused under (none: the placeholders at
    /// 30 Hz), and the expected code, field and offset. Every other value in the state is valid.
    /// </summary>
    private static ResetRefusal Refused(string caseName)
    {
        var standing = TestStates.StandingCharacter(1, 0.5f, 0.5f);
        var weapon0 = TestStates.WeaponOffset(0, 0);
        return caseName switch
        {
            "65-characters" => new(
                TestStates.Build(0, SpreadCharacters(65)),
                null,
                FormatErrorCode.Oversized,
                "characterCount",
                4),
            "2-projectiles-over-max-1" => new(
                TestStates.Build(0, [standing], [TestStates.FlyingProjectile, TestStates.FlyingProjectile with { Id = 2 }]),
                c => c.World!.MaxProjectiles = 1,
                FormatErrorCode.Oversized,
                "projectileCount",
                6),
            "3-weapons" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle, TestStates.Launcher, TestStates.Rifle] }]),
                null,
                FormatErrorCode.Inconsistent,
                "characters[0].weaponCount",
                TestStates.CharacterOffset(0) + 51),
            "position-x-40" => new(
                TestStates.Build(0, [TestStates.StandingCharacter(1, 40f, 0.5f)]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].position.x",
                TestStates.CharacterOffset(0) + 4),
            "position-z-minus-40" => new(
                TestStates.Build(0, [TestStates.StandingCharacter(1, 0.5f, -40f)]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].position.z",
                TestStates.CharacterOffset(0) + 12),

            // A 60° slope's normal while grounded: y = 0.5 is below CosSlopeLimit (cos 45°).
            "grounded-normal-y-0.5" => new(
                TestStates.Build(0, [standing with { GroundNormal = (0.8660254f, 0.5f, 0f) }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].groundNormal.y",
                TestStates.CharacterOffset(0) + 32),

            // Airborne, the normal must be exactly (0, 1, 0).
            "airborne-tilted-normal" => new(
                TestStates.Build(0, [standing with { Flags = 0, GroundNormal = (0.1f, 0.99f, 0f) }]),
                null,
                FormatErrorCode.Inconsistent,
                "characters[0].groundNormal.x",
                TestStates.CharacterOffset(0) + 28),
            // phaseSteps is 0 in Ready and Firing, [1, EquipSteps] in Switching, [1, ReloadSteps] in
            // Reloading, [0, EquipSteps] for the active weapon while Overheated, and 0 for a holstered
            // Overheated weapon.
            "ready-phase-steps-1" => WeaponZero(TestStates.Rifle with { PhaseSteps = 1 }, "phaseSteps", 2),
            "firing-phase-steps-1" => WeaponZero(TestStates.Rifle with { Phase = 1, PhaseSteps = 1 }, "phaseSteps", 2),
            "switching-phase-steps-0" => WeaponZero(TestStates.Rifle with { Phase = 3, PhaseSteps = 0 }, "phaseSteps", 2),
            "switching-phase-steps-16" => WeaponZero(TestStates.Rifle with { Phase = 3, PhaseSteps = 16 }, "phaseSteps", 2),
            "reloading-phase-steps-0" => WeaponZero(TestStates.Rifle with { Phase = 2, PhaseSteps = 0 }, "phaseSteps", 2),
            "reloading-phase-steps-61" => WeaponZero(TestStates.Rifle with { Phase = 2, PhaseSteps = 61 }, "phaseSteps", 2),
            "overheated-phase-steps-16" => WeaponZero(TestStates.Rifle with { Phase = 4, PhaseSteps = 16 }, "phaseSteps", 2),
            "holstered-overheated-phase-steps-1" => new(
                TestStates.Build(0, [standing with { ActiveSlot = 0, Weapons = [TestStates.Rifle, TestStates.Launcher with { Phase = 4, PhaseSteps = 1 }] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[1].phaseSteps",
                TestStates.WeaponOffset(0, 1) + 2),

            // Heat stays below OverheatThresholdScaled + HeatPerShotScaled.
            "heat-3000000" => WeaponZero(TestStates.Rifle with { Heat = 3_000_000 }, "heat", 16),
            "heat-3300000-heat-per-shot-10000" => WeaponZero(TestStates.Rifle with { Heat = 3_300_000 }, "heat", 16) with { Edit = HeatPerShot10000 },
            "magazine-31" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle with { Magazine = 31 }, TestStates.Launcher] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].magazine",
                weapon0 + 12),
            "fire-wait-1800" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle with { FireWait = 1800 }, TestStates.Launcher] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].fireWait",
                weapon0 + 4),
            "fire-wait-minus-1" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle with { FireWait = -1 }, TestStates.Launcher] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].fireWait",
                weapon0 + 4),
            "recoil-pitch-1093" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle with { RecoilPitch = 1093 }, TestStates.Launcher] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].recoilPitch",
                weapon0 + 22),
            "recoil-yaw-minus-547" => new(
                TestStates.Build(0, [standing with { Weapons = [TestStates.Rifle with { RecoilYaw = -547 }, TestStates.Launcher] }]),
                null,
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].recoilYaw",
                weapon0 + 26),

            // Slot 0 is active, so slot 1 is holstered and must be Ready or Overheated, not Firing (1).
            "holstered-weapon-firing" => new(
                TestStates.Build(0, [standing with { ActiveSlot = 0, Weapons = [TestStates.Rifle, TestStates.Launcher with { Phase = 1 }] }]),
                null,
                FormatErrorCode.Inconsistent,
                "characters[0].weapons[1].phase",
                TestStates.WeaponOffset(0, 1)),
            "projectile-class-2" => new(
                TestStates.Build(0, [standing], [TestStates.FlyingProjectile with { Class = 2 }]),
                null,
                FormatErrorCode.OutOfRange,
                "projectiles[0].class",
                TestStates.ProjectileOffset(1, 0) + 8),

            // Byte-only: Reset runs the reader's checks first.
            "position-x-nan" => new(
                TestStates.Mutate(TestStates.StandingOnFlat, TestStates.CharacterOffset(0) + 4, TestStates.U32(0x7FC00000u)),
                null,
                FormatErrorCode.NonFinite,
                "characters[0].position.x",
                TestStates.CharacterOffset(0) + 4),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    /// <summary>
    /// A standing character whose active slot 0 holds <paramref name="rifle"/>, refused under the
    /// placeholders at <c>characters[0].weapons[0].</c><paramref name="field"/>, <c>OutOfRange</c>, at
    /// <paramref name="offsetInRecord"/> into the weapon record.
    /// </summary>
    private static ResetRefusal WeaponZero(WeaponRecord rifle, string field, int offsetInRecord) => new(
        TestStates.Build(0, [TestStates.StandingCharacter(1, 0.5f, 0.5f) with { Weapons = [rifle, TestStates.Launcher] }]),
        null,
        FormatErrorCode.OutOfRange,
        $"characters[0].weapons[0].{field}",
        TestStates.WeaponOffset(0, 0) + offsetInRecord);

    /// <summary>Weapon 0 heats: <c>heatPerShot</c> 10000, so HeatPerShotScaled is 300,000 at 30 Hz.</summary>
    private static void HeatPerShot10000(SimConfig config) => config.Weapons![0]!.HeatPerShot = 10000;

    /// <summary>A state <c>Reset</c> accepts, and the configuration edit it is loaded under (none: the placeholders at 30 Hz).</summary>
    private static (byte[] State, Action<SimConfig>? Edit) Accepted(string caseName)
    {
        var standing = TestStates.StandingCharacter(1, 0.5f, 0.5f);
        byte[] WithRifle(WeaponRecord rifle) => TestStates.Build(0, [standing with { Weapons = [rifle, TestStates.Launcher] }]);
        return caseName switch
        {
            "switching-phase-steps-1" => (WithRifle(TestStates.Rifle with { Phase = 3, PhaseSteps = 1 }), null),
            "switching-phase-steps-15" => (WithRifle(TestStates.Rifle with { Phase = 3, PhaseSteps = 15 }), null),
            "reloading-phase-steps-1" => (WithRifle(TestStates.Rifle with { Phase = 2, PhaseSteps = 1 }), null),
            "reloading-phase-steps-60" => (WithRifle(TestStates.Rifle with { Phase = 2, PhaseSteps = 60 }), null),
            "overheated-phase-steps-0" => (WithRifle(TestStates.Rifle with { Phase = 4, PhaseSteps = 0 }), null),
            "overheated-phase-steps-15" => (WithRifle(TestStates.Rifle with { Phase = 4, PhaseSteps = 15 }), null),
            "holstered-overheated-phase-steps-0" => (
                TestStates.Build(0, [standing with { ActiveSlot = 0, Weapons = [TestStates.Rifle, TestStates.Launcher with { Phase = 4 }] }]),
                null),
            "heat-2999999" => (WithRifle(TestStates.Rifle with { Heat = 2_999_999 }), null),
            "heat-3299999-heat-per-shot-10000" => (WithRifle(TestStates.Rifle with { Heat = 3_299_999 }), HeatPerShot10000),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    /// <summary><paramref name="count"/> standing characters, IDs 1 upward, spread over the playable area.</summary>
    private static CharacterRecord[] SpreadCharacters(int count)
    {
        var characters = new CharacterRecord[count];
        for (var i = 0; i < count; i++)
        {
            characters[i] = TestStates.StandingCharacter((uint)(i + 1), -31.5f + (7f * (i % 8)), -31.5f + (7f * (i / 8)));
        }

        return characters;
    }

    private sealed record ResetRefusal(byte[] State, Action<SimConfig>? Edit, FormatErrorCode Code, string Field, long Offset);
}
