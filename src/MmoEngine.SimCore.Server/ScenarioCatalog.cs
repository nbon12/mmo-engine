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
}

/// <summary>
/// One character of a scenario: its ID, where it starts (at rest, Standing, with full weapons), whether it
/// starts Grounded (on ground whose normal is (0, 1, 0)), its aim yaw for the whole run, and the buttons it
/// holds at each entry. Every entry gives it one command.
/// </summary>
/// <param name="Id">The character's ID; a scenario's IDs are unique.</param>
/// <param name="Position">The feet, at rest height; usually a <see cref="TestWorldFeatures"/> start.</param>
/// <param name="AimYaw">The aim yaw of the initial state and of every command.</param>
/// <param name="Hold">The buttons held at each entry.</param>
public sealed record ScenarioCharacter(uint Id, Vec3 Position, ushort AimYaw, Func<ScenarioTime, Buttons> Hold)
{
    /// <summary>Whether the character starts Grounded; otherwise airborne.</summary>
    public bool Grounded { get; init; } = true;
}

/// <summary>
/// One named scenario stream (contracts/simcore-cli.md#named-scenario-streams): its name, the collision file
/// it runs on, its own step rate, the configuration overrides it applies to a copy of the base
/// configuration, its characters and its length in simulated seconds. <see cref="Build"/> makes the
/// unrecorded stream at any rate, and <see cref="Record"/> runs it through <see cref="StreamRunner"/> for its
/// expected hashes.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>The match salt every scenario stream carries.</summary>
    public const ulong MatchSalt = 0x0015_5CE0_A210_5A17UL;

    /// <summary>The step the initial state is at; the first entry is step 1.</summary>
    private const uint InitialStep = 0;

    private readonly ScenarioCharacter[] _characters;
    private readonly Action<SimConfig>? _configure;

    /// <summary>A definition; the characters are sorted by ID.</summary>
    /// <exception cref="ArgumentNullException">The name or the characters are null.</exception>
    /// <exception cref="ArgumentException">The characters are empty, an ID repeats, or the duration is not positive.</exception>
    public ScenarioDefinition(string name, int stepRateHz, double durationSeconds, IReadOnlyList<ScenarioCharacter> characters, Action<SimConfig>? configure = null)
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
    }

    /// <summary>The stream's name, and its file name without <c>.mmrs</c>.</summary>
    public string Name { get; }

    /// <summary>The collision file under <c>simcore/content/</c> the stream runs on.</summary>
    public string CollisionFileName => "test-world.mmcd";

    /// <summary>The rate the committed stream is recorded at.</summary>
    public int StepRateHz { get; }

    /// <summary>The whole run's simulated seconds: a whole number of steps at 20, 30 and 60 Hz.</summary>
    public double DurationSeconds { get; }

    /// <summary>The characters, in ascending ID.</summary>
    public IReadOnlyList<ScenarioCharacter> Characters => _characters;

    /// <summary>
    /// The unrecorded stream at <paramref name="stepRateHz"/> (the definition's own rate when null) over
    /// <paramref name="world"/>: the base configuration copied, with the overrides applied and
    /// <c>stepRateHz</c> set; an initial state at step 0 holding the characters at rest with full weapons;
    /// and <see cref="DurationSeconds"/> × the rate entries, each with one command per character (sequence
    /// numbers from 1, <c>ClientStep</c> equal to the step), every expected hash 0, seed 0 and
    /// <see cref="MatchSalt"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="ArgumentException">The duration is not a whole number of steps at the rate.</exception>
    public RecordedStream Build(SimConfig baseConfig, CollisionWorld world, int? stepRateHz = null)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(world);
        var rate = stepRateHz ?? StepRateHz;
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
                var input = new InputCommand((uint)k + 1u, step, character.Hold(time), character.AimYaw, 0, 0);
                commands[c] = new RecordedCommand((byte)c, input);
            }

            steps[k] = new RecordedStep(step, commands, 0UL);
        }

        return new RecordedStream(SimCoreVersion.RulesRevision, rate, seed: 0, MatchSalt, world.Checksum, config, InitialState(config), steps);
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
    /// The canonical initial state: step 0, <c>nextProjectileId</c> 1, the characters in ascending ID at
    /// rest (velocity 0, ground normal (0, 1, 0), Standing, aim pitch 0, <c>lastSequence</c> 0, slot 0) with
    /// <c>input.weaponSlots</c> weapons, each new (<see cref="WeaponState.New"/>) from its slot's
    /// configuration, no projectiles and no events.
    /// </summary>
    private byte[] InitialState(SimConfig config)
    {
        var weaponConfigs = config.Weapons ?? throw new ArgumentException("The configuration has no weapons.", nameof(config));
        var weaponSlots = config.Input?.WeaponSlots ?? throw new ArgumentException("The configuration has no input section.", nameof(config));
        if (weaponSlots < 1 || weaponSlots > weaponConfigs.Count)
        {
            throw new ArgumentException($"input.weaponSlots {weaponSlots} does not name 1 to {weaponConfigs.Count} weapons.", nameof(config));
        }

        var characters = new CharacterState[_characters.Length];
        var weapons = new WeaponState[_characters.Length * weaponSlots];
        for (var c = 0; c < _characters.Length; c++)
        {
            var character = _characters[c];
            characters[c] = new CharacterState
            {
                Id = character.Id,
                Position = character.Position,
                Velocity = new Vec3(0f, 0f, 0f),
                GroundNormal = new Vec3(0f, 1f, 0f),
                AimYaw = character.AimYaw,
                AimPitch = 0,
                Stance = Stance.Standing,
                Flags = character.Grounded ? CharacterFlags.Grounded : 0,
                LastSequence = 0,
                ActiveSlot = 0,
                WeaponCount = (byte)weaponSlots,
            };
            for (var s = 0; s < weaponSlots; s++)
            {
                var weapon = weaponConfigs[s] ?? throw new ArgumentException($"weapons[{s}] is missing.", nameof(config));
                weapons[(c * weaponSlots) + s] = WeaponState.New(weapon.MagazineSize, weapon.ReserveRounds);
            }
        }

        var length = StateSerializer.Length(characters.Length, weaponSlots, 0, 0, 0, 0, 0, 0, 0);
        var state = new byte[length];
        var fault = StateSerializer.Write(
            InitialStep,
            1,
            weaponSlots,
            characters,
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
            throw new InvalidOperationException($"{Name}'s initial state does not serialise: {fault.Code} at {fault.Path}.");
        }

        return state;
    }
}
