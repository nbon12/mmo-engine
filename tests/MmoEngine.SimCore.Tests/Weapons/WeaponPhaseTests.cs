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
/// FR-006: every transition of the weapon phase table (data-model.md#weapon-phases), plus the two reload
/// presses the table ignores (a full magazine, an empty reserve). Each row calls <c>WeaponStep.Step</c>
/// once, directly, at 30 Hz from <c>TestConfigs.At(30)</c> with the row's configuration change, through
/// <see cref="StepOnce"/>. The arranged post-movement character is ID 1, standing and grounded at the
/// origin, ActiveSlot 0, with two weapons; the call passes projectile count 0, <c>nextProjectileId</c> 1
/// and spawn and shot spans of <see cref="CompiledConfig.MaxShotsPerStepMax"/> entries. Every row
/// asserts the exact Phase, PhaseSteps, Magazine and Reserve of the weapon it names after the call, the
/// fire-block reason, the number of shots and the ActiveSlot; the heat rows also assert Heat.
/// <para>
/// The placeholders compile at 30 Hz to: rifle (slot 0) ReloadSteps 60, magazine 30, reserve 210;
/// launcher (slot 1) EquipSteps 23, magazine 1, reserve 5. The heat weapon is the rifle with
/// <c>heatPerShot</c> 10000: HeatPerShotScaled 300,000, OverheatThresholdScaled 3,000,000,
/// HeatRecoveryScaled 1,200,000, HeatCooldownPerStep 50,000 and HeatCooldownDelaySteps 8.
/// </para>
/// </summary>
public sealed class WeaponPhaseTests
{
    private const int Hz = 30;

    /// <summary>An input holding no button.</summary>
    private const Buttons NoButtons = 0;

    /// <summary>The rifle's FireWait one step after a shot at 600 rpm and 30 Hz: 1800 − 600.</summary>
    private const int FireWaitAfterShot = 1200;

    /// <summary>The row names: the phase table's transitions, then the reload presses it ignores.</summary>
    public static TheoryData<string> Rows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in Table)
            {
                data.Add(row.Name);
            }

            return data;
        }
    }

    /// <summary>A new rifle: Ready, 30 in the magazine, 210 in reserve, never fired.</summary>
    private static WeaponState Rifle => WeaponState.New(30, 210);

    /// <summary>A new launcher: Ready, 1 in the magazine, 5 in reserve, never fired.</summary>
    private static WeaponState Launcher => WeaponState.New(1, 5);

    /// <summary>
    /// The rows. Fields a row does not state are the new weapons' (Ready, flags 0, PhaseSteps 0, FireWait
    /// 0, ShotCounter 0, full magazine, Heat 0, StepsSinceShot 65535, no recoil), no button, and input
    /// WeaponSlot equal to the ActiveSlot. A weapon that fired in the previous step has StepsSinceShot 0
    /// and FireWait <see cref="FireWaitAfterShot"/>.
    /// </summary>
    private static IReadOnlyList<PhaseRow> Table =>
    [
        new("ReadyFireHeldToFiring", Rifle, Launcher, Buttons.Fire, 0, WeaponPhase.Firing, 0, 29, 210)
        {
            Shots = 1,
        },
        new(
            "FiringReleasedToReady",
            Rifle with { Phase = WeaponPhase.Firing, Magazine = 29, FireWait = FireWaitAfterShot, StepsSinceShot = 0 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            29,
            210),
        new(
            "FiringBlockedToReady",
            Rifle with { Phase = WeaponPhase.Firing, Magazine = 0, FireWait = FireWaitAfterShot, StepsSinceShot = 0 },
            Launcher,
            Buttons.Fire,
            0,
            WeaponPhase.Ready,
            0,
            0,
            210)
        {
            Block = FireBlock.Empty,
        },
        new("ReadyReloadToReloading", Rifle with { Magazine = 29 }, Launcher, Buttons.Reload, 0, WeaponPhase.Reloading, 60, 29, 210),
        new(
            "FiringReloadToReloading",
            Rifle with { Phase = WeaponPhase.Firing, Magazine = 10, FireWait = FireWaitAfterShot, StepsSinceShot = 0 },
            Launcher,
            Buttons.Reload,
            0,
            WeaponPhase.Reloading,
            60,
            10,
            210),
        new("ReloadFullMagazineIgnored", Rifle, Launcher, Buttons.Reload, 0, WeaponPhase.Ready, 0, 30, 210),
        new(
            "ReloadEmptyReserveIgnored",
            Rifle with { Magazine = 0, Reserve = 0 },
            Launcher,
            Buttons.Reload,
            0,
            WeaponPhase.Ready,
            0,
            0,
            0),
        new(
            "ReloadingCompletesToReady",
            Rifle with { Phase = WeaponPhase.Reloading, PhaseSteps = 1, Magazine = 3, Reserve = 20 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            23,
            0),
        new("ReloadZeroStepsCompletesAtEntry", Rifle with { Magazine = 0 }, Launcher, Buttons.Reload, 0, WeaponPhase.Ready, 0, 30, 180)
        {
            Configure = config => config.Weapons![0]!.ReloadMs = 0,
        },
        new("HolsteredReadySwitchesIn", Rifle, Launcher, NoButtons, 1, WeaponPhase.Switching, 23, 1, 5)
        {
            InputSlot = 1,
        },
        new("ZeroEquipStepsIsReadyAtOnce", Rifle, Launcher, NoButtons, 1, WeaponPhase.Ready, 0, 1, 5)
        {
            Configure = config => config.Weapons![1]!.EquipMs = 0,
            InputSlot = 1,
        },
        new(
            "HolsteredOverheatedSwitchesInOverheated",
            Rifle,
            Launcher with { Phase = WeaponPhase.Overheated, Heat = 3_000_000, StepsSinceShot = 0 },
            NoButtons,
            1,
            WeaponPhase.Overheated,
            23,
            1,
            5)
        {
            Configure = config =>
            {
                config.Weapons![0]!.HeatPerShot = 10000;
                config.Weapons[1]!.HeatPerShot = 10000;
            },
            InputSlot = 1,
            Heat = 3_000_000,
        },
        new(
            "ActiveFiringSwitchedAwayToReady",
            Rifle with { Phase = WeaponPhase.Firing, Magazine = 29, FireWait = FireWaitAfterShot, StepsSinceShot = 0 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            29,
            210)
        {
            InputSlot = 1,
        },
        new(
            "ActiveReloadingSwitchedAwayCancels",
            Rifle with { Phase = WeaponPhase.Reloading, PhaseSteps = 30, Magazine = 5, Reserve = 100 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            5,
            100)
        {
            InputSlot = 1,
        },
        new(
            "ActiveSwitchingSwitchedAwayToReady",
            Rifle with { Phase = WeaponPhase.Switching, PhaseSteps = 10 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            30,
            210)
        {
            InputSlot = 1,
        },
        new(
            "ActiveOverheatedSwitchedAwayStaysOverheated",
            Rifle with { Phase = WeaponPhase.Overheated, PhaseSteps = 4, Magazine = 20, Heat = 3_000_000, StepsSinceShot = 0 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Overheated,
            0,
            20,
            210)
        {
            Configure = HeatWeapon,
            InputSlot = 1,
            Heat = 3_000_000,
        },
        new(
            "SwitchingCompletesToReady",
            Rifle with { Phase = WeaponPhase.Switching, PhaseSteps = 1 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            30,
            210),

        // StepsSinceShot 5 becomes 6 in W0, below the cooldown delay of 8, so W4 does not cool the
        // 2,700,000 before the shot adds 300,000.
        new(
            "ShotReachingThresholdOverheats",
            Rifle with { Magazine = 21, Heat = 2_700_000, StepsSinceShot = 5 },
            Launcher,
            Buttons.Fire,
            0,
            WeaponPhase.Overheated,
            0,
            20,
            210)
        {
            Configure = HeatWeapon,
            Shots = 1,
            Heat = 3_000_000,
        },

        // StepsSinceShot 7 becomes 8 in W0, reaching the cooldown delay, so W4 cools by 50,000 to the
        // recovery level 1,200,000.
        new(
            "OverheatedCoolsToReady",
            Rifle with { Phase = WeaponPhase.Overheated, Magazine = 20, Heat = 1_250_000, StepsSinceShot = 7 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Ready,
            0,
            20,
            210)
        {
            Configure = HeatWeapon,
            Heat = 1_200_000,
        },
        new(
            "OverheatedCoolsToSwitching",
            Rifle with { Phase = WeaponPhase.Overheated, PhaseSteps = 5, Magazine = 20, Heat = 1_250_000, StepsSinceShot = 7 },
            Launcher,
            NoButtons,
            0,
            WeaponPhase.Switching,
            4,
            20,
            210)
        {
            Configure = HeatWeapon,
            Heat = 1_200_000,
        },
    ];

    /// <summary>
    /// FR-006: given the row's weapons, configuration and input, When <c>WeaponStep.Step</c> runs once at
    /// 30 Hz, Then the named weapon has exactly the row's Phase, PhaseSteps, Magazine and Reserve, the
    /// fire block is the row's (None unless stated), the call fired the row's number of shots (0 unless
    /// stated), ActiveSlot is the input's WeaponSlot, and a heat row's Heat is exact.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void TransitionsFollowTheContractTable(string row)
    {
        var placeholders = TestConfigs.Compiled(Hz);
        Assert.True(
            placeholders.Weapons[0].ReloadSteps == 60 && placeholders.Weapons[1].EquipSteps == 23,
            $"{row}: the placeholders compile to rifle ReloadSteps {placeholders.Weapons[0].ReloadSteps} and launcher EquipSteps {placeholders.Weapons[1].EquipSteps}; the row table assumes 60 and 23");

        var arranged = Table.Single(candidate => candidate.Name == row);
        var raw = TestConfigs.At(Hz);
        arranged.Configure?.Invoke(raw);
        var config = CompiledConfig.Create(raw);

        WeaponState[] weapons = [arranged.Weapon0, arranged.Weapon1];
        var input = new InputCommand(1, 1, arranged.Buttons, 0, 0, arranged.InputSlot);
        var (shotCount, character, block) = StepOnce(config, arranged.ActiveSlot, weapons, input);

        var weapon = weapons[arranged.AssertedSlot];
        var expected = (arranged.Phase, arranged.PhaseSteps, arranged.Magazine, arranged.Reserve, arranged.Block, arranged.Shots, arranged.InputSlot);
        var actual = (weapon.Phase, weapon.PhaseSteps, weapon.Magazine, weapon.Reserve, block, shotCount, character.ActiveSlot);
        Assert.True(
            actual == expected,
            $"{row}: weapons[{arranged.AssertedSlot}] (Phase, PhaseSteps, Magazine, Reserve), the fire block, the shot count and ActiveSlot are {actual}, expected {expected}");

        if (arranged.Heat is { } heat)
        {
            Assert.True(weapon.Heat == heat, $"{row}: weapons[{arranged.AssertedSlot}].Heat is {weapon.Heat}, expected {heat}");
        }
    }

    /// <summary>Slot 0 is the heat test weapon: the rifle with <c>heatPerShot</c> 10000.</summary>
    private static void HeatWeapon(SimConfig config) => config.Weapons![0]!.HeatPerShot = 10000;

    /// <summary>
    /// One direct <c>WeaponStep.Step</c> call: step 1, match salt <see cref="WeaponRuns.Salt"/>, the
    /// post-movement character (ID 1, standing and grounded at the origin, yaw 0, pitch 0, LastSequence 1,
    /// <paramref name="activeSlot"/>, two weapons), <paramref name="weapons"/> updated in place, projectile
    /// count 0, <c>nextProjectileId</c> 1, and spawn and shot spans of <c>MaxShotsPerStepMax</c> entries.
    /// </summary>
    private static (int ShotCount, CharacterState Character, FireBlock Block) StepOnce(
        CompiledConfig config,
        byte activeSlot,
        WeaponState[] weapons,
        InputCommand input)
    {
        var character = new CharacterState
        {
            Id = 1,
            Position = new Vec3(0f, 0f, 0f),
            Velocity = new Vec3(0f, 0f, 0f),
            GroundNormal = new Vec3(0f, 1f, 0f),
            AimYaw = input.AimYaw,
            AimPitch = input.AimPitch,
            Stance = Stance.Standing,
            Flags = CharacterFlags.Grounded,
            LastSequence = input.Sequence,
            ActiveSlot = activeSlot,
            WeaponCount = (byte)weapons.Length,
        };
        var nextProjectileId = 1u;
        var spawned = new Projectile[config.MaxShotsPerStepMax];
        var shots = new ShotEvent[config.MaxShotsPerStepMax];

        var shotCount = WeaponStep.Step(
            config,
            WeaponRuns.Salt,
            1u,
            ref character,
            weapons,
            in input,
            0,
            ref nextProjectileId,
            spawned,
            shots,
            out var block);
        return (shotCount, character, block);
    }

    /// <summary>
    /// One row: its name, the two weapons before the call, the buttons held, the slot whose weapon is
    /// asserted, and that weapon's expected Phase, PhaseSteps, Magazine and Reserve.
    /// </summary>
    private sealed record PhaseRow(
        string Name,
        WeaponState Weapon0,
        WeaponState Weapon1,
        Buttons Buttons,
        int AssertedSlot,
        WeaponPhase Phase,
        ushort PhaseSteps,
        ushort Magazine,
        ushort Reserve)
    {
        /// <summary>A change to <c>TestConfigs.At(30)</c> before it is compiled; null for none.</summary>
        public Action<SimConfig>? Configure { get; init; }

        /// <summary>The character's ActiveSlot before the call.</summary>
        public byte ActiveSlot { get; init; }

        /// <summary>The input's WeaponSlot; a value other than <see cref="ActiveSlot"/> is a slot change.</summary>
        public byte InputSlot { get; init; }

        /// <summary>The expected fire block.</summary>
        public FireBlock Block { get; init; } = FireBlock.None;

        /// <summary>The expected number of shots.</summary>
        public int Shots { get; init; }

        /// <summary>The expected Heat of the asserted weapon; null when the row states none.</summary>
        public uint? Heat { get; init; }
    }
}
