using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
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
/// <see cref="ArrangeWeapon"/> changes them), whether it starts Grounded (on ground whose normal is
/// (0, 1, 0)), its aim yaw and pitch for the whole run, and the buttons it holds and the weapon slot it
/// selects at each entry. Every entry gives it one command.
/// </summary>
/// <param name="Id">The character's ID; a scenario's IDs are unique.</param>
/// <param name="Position">The feet, at rest height; usually a <see cref="TestWorldFeatures"/> start.</param>
/// <param name="AimYaw">The aim yaw of the initial state and of every command.</param>
/// <param name="Hold">The buttons held at each entry.</param>
public sealed record ScenarioCharacter(uint Id, Vec3 Position, ushort AimYaw, Func<ScenarioTime, Buttons> Hold)
{
    /// <summary>Whether the character starts Grounded; otherwise airborne.</summary>
    public bool Grounded { get; init; } = true;

    /// <summary>The aim pitch of the initial state and of every command, a binary angle; 0 (level) unless set.</summary>
    public short AimPitch { get; init; }

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
/// from a generator instead of a character schedule.
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

        _characters = [.. characters];
        Array.Sort(_characters, static (a, b) => a.Id.CompareTo(b.Id));
        for (var i = 1; i < _characters.Length; i++)
        {
            if (_characters[i - 1].Id == _characters[i].Id)
            {
                throw new ArgumentException($"Character ID {_characters[i].Id} repeats.", nameof(characters));
            }
        }

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

    /// <summary>The characters, in ascending ID; empty for a generated definition, which places its own.</summary>
    public IReadOnlyList<ScenarioCharacter> Characters => _characters;

    /// <summary>
    /// The unrecorded stream at <paramref name="stepRateHz"/> (the definition's own rate when null) over
    /// <paramref name="world"/>: the base configuration copied, with the overrides applied and
    /// <c>stepRateHz</c> set; an initial state at step 0 holding the characters at rest with full weapons;
    /// and <see cref="DurationSeconds"/> × the rate entries, each with one command per character (sequence
    /// numbers from 1, <c>ClientStep</c> equal to the step, the character's aim, and the buttons and weapon
    /// slot its schedule gives), every expected hash 0, seed 0 and the definition's match salt. A generated
    /// definition returns its generator's stream at the rate instead.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="ArgumentException">The duration is not a whole number of steps at the rate.</exception>
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
        var steps = new RecordedStep[stepCount];
        for (var k = 0; k < stepCount; k++)
        {
            var step = InitialStep + (uint)k + 1u;
            var time = new ScenarioTime(k, rate);
            var commands = new RecordedCommand[_characters.Length];
            for (var c = 0; c < _characters.Length; c++)
            {
                var character = _characters[c];
                var slot = character.WeaponSlot?.Invoke(time) ?? 0;
                var input = new InputCommand((uint)k + 1u, step, character.Hold(time), character.AimYaw, character.AimPitch, slot);
                commands[c] = new RecordedCommand((byte)c, input);
            }

            steps[k] = new RecordedStep(step, commands, 0UL);
        }

        return new RecordedStream(SimCoreVersion.RulesRevision, rate, seed: 0, _matchSalt, world.Checksum, config, InitialState(Name, config, _characters), steps);
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
    /// <c>lastSequence</c> 0, slot 0) with <c>input.weaponSlots</c> weapons, each new
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
                ActiveSlot = 0,
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
