using System.Globalization;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The generated streams the agreement suite runs beside the committed ones (FR-026 server half, US2
/// scenario 3; specs/015-shared-simulation-core/contracts/simcore-cli.md#generated-streams).
/// <c>seed-&lt;nnn&gt;</c> comes from its seed alone: every draw is <see cref="SplitMix64.At"/> of the seed
/// and a counter that advances in one fixed order (the header, then the initial state, which takes no
/// draw, then per step, per character in ascending index), so a stream is the same on every run and in
/// every build. Each command's fields are drawn uniformly over their valid ranges, and a draw with
/// <c>draw % 64 == 0</c> makes the command invalid on purpose, cycling the four rejection reasons, so the
/// rejection path runs in every build too (US1 scenario 9).
/// </summary>
/// <remarks>
/// The streams are unrecorded (every expected hash 0). No random number generator is used, and every loop is
/// bounded by the step and character counts.
/// </remarks>
public static class StreamGenerator
{
    /// <summary>The steps of every <c>seed-&lt;nnn&gt;</c> stream.</summary>
    public const int SeedStepCount = 300;

    /// <summary>The most characters the spawn row holds: 4 m apart from the flat-run start along +X.</summary>
    public const int MaxCharacterCount = 8;

    /// <summary>The most steps a recorded stream holds, 2²⁰ (contracts/recorded-stream-format.md#decoding).</summary>
    public const int MaxStepCount = 1 << 20;

    /// <summary>The counter of the header's match salt draw.</summary>
    private const ulong MatchSaltCounter = 0;

    /// <summary>The counter of the header's character count draw (<see cref="Build(int, SimConfig, CollisionWorld)"/> only; the counter is reserved either way).</summary>
    private const ulong CharacterCountCounter = 1;

    /// <summary>The first command's first draw; the initial state takes no draw.</summary>
    private const ulong FirstCommandCounter = 2;

    /// <summary>The draws of one command: validity, buttons, yaw, pitch, slot.</summary>
    private const ulong DrawsPerCommand = 5;

    /// <summary>A command is invalid when its validity draw is a multiple of this.</summary>
    private const ulong InvalidEvery = 64;

    /// <summary>The spawn row's spacing along +X, m.</summary>
    private const float Spacing = 4f;

    /// <summary>How far above the terrain each character starts, airborne, m.</summary>
    private const float SpawnHeight = 0.5f;

    private const int KnownButtonValues = (int)Buttons.Known + 1;
    private const int YawValues = 65536;
    private const int MinPitch = -16384;
    private const int MaxPitch = 16384;
    private const int OutOfRangePitchSpan = 16383;
    private const int ReservedButtonBits = 7;
    private const ushort FirstReservedButton = 0x0200;
    private const int SlotValues = 256;

    /// <summary>The step rates <c>seed-&lt;nnn&gt;</c> cycles through, by <c>seed % 3</c>.</summary>
    private static readonly int[] SeedRates = [20, 30, 60];

    /// <summary>The name of the stream of <paramref name="seed"/>: <c>seed-&lt;nnn&gt;</c>, three digits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The seed is outside [0, 999].</exception>
    public static string NameFor(int seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seed, 999);
        return string.Create(CultureInfo.InvariantCulture, $"seed-{seed:D3}");
    }

    /// <summary>
    /// <c>seed-&lt;nnn&gt;</c>: <see cref="Build(ulong, SimConfig, CollisionWorld, int, int, int)"/> of
    /// <paramref name="seed"/> at <c>{20, 30, 60}[seed % 3]</c> Hz with 2 + (the character count draw % 3)
    /// characters and 300 steps.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The seed is negative.</exception>
    /// <exception cref="ArgumentException">The configuration has no input or weapons section, or too few weapons for its slots.</exception>
    public static RecordedStream Build(int seed, SimConfig baseConfig, CollisionWorld world)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seed);
        var unsignedSeed = (ulong)seed;
        var characterCount = 2 + (int)(SplitMix64.At(unsignedSeed, CharacterCountCounter) % 3UL);
        return Build(unsignedSeed, baseConfig, world, SeedRates[seed % SeedRates.Length], characterCount, SeedStepCount);
    }

    /// <summary>
    /// An unrecorded stream from <paramref name="seed"/>: the base configuration copied with
    /// <c>stepRateHz</c> set; header seed <paramref name="seed"/>, the match salt from draw 0 and the world's
    /// checksum; an initial state at step 0 with character IDs 1 to <paramref name="characterCount"/> placed
    /// 4 m apart along +X from <see cref="TestWorldFeatures.FlatRunStart"/>, airborne 0.5 m above
    /// <see cref="CollisionWorld.HeightAt"/>, at rest, aim 0, Standing, with new weapons, no projectiles and
    /// <c>nextProjectileId</c> 1; and <paramref name="stepCount"/> steps, each with one command per character
    /// in ascending index. Valid commands use sequence <c>lastSequence + 1</c> and draw buttons, yaw, pitch
    /// and slot uniformly over their valid ranges; character 0's first two commands are valid with pitch
    /// −16384 and 16384. A validity draw with <c>draw % 64 == 0</c> makes any other command invalid, cycling
    /// <see cref="InputRejection.SequenceNotIncreasing"/>, <see cref="InputRejection.UnknownButtons"/>,
    /// <see cref="InputRejection.PitchOutOfRange"/> and <see cref="InputRejection.UnknownWeaponSlot"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration or the world is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The character count is outside [1, 8], or the step count outside [1, 2²⁰].</exception>
    /// <exception cref="ArgumentException">The configuration has no input or weapons section, or too few weapons for its slots.</exception>
    public static RecordedStream Build(ulong seed, SimConfig baseConfig, CollisionWorld world, int stepRateHz, int characterCount, int stepCount)
    {
        ArgumentNullException.ThrowIfNull(baseConfig);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfLessThan(characterCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(characterCount, MaxCharacterCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(stepCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stepCount, MaxStepCount);
        var config = baseConfig.Clone();
        config.StepRateHz = stepRateHz;
        var weaponSlots = config.Input?.WeaponSlots ?? throw new ArgumentException("The configuration has no input section.", nameof(baseConfig));

        var name = string.Create(CultureInfo.InvariantCulture, $"the stream of seed {seed}");
        var initialState = ScenarioDefinition.InitialState(name, config, SpawnRow(world, characterCount));
        var matchSalt = SplitMix64.At(seed, MatchSaltCounter);

        var lastSequence = new uint[characterCount];
        var steps = new RecordedStep[stepCount];
        var counter = FirstCommandCounter;
        var invalid = 0;
        for (var k = 0; k < stepCount; k++)
        {
            var step = (uint)k + 1u;
            var commands = new RecordedCommand[characterCount];
            for (var c = 0; c < characterCount; c++)
            {
                var validity = SplitMix64.At(seed, counter);
                var buttons = (ushort)(SplitMix64.At(seed, counter + 1) % KnownButtonValues);
                var yaw = (ushort)(SplitMix64.At(seed, counter + 2) % YawValues);
                var pitch = (short)((int)(SplitMix64.At(seed, counter + 3) % (MaxPitch - MinPitch + 1)) + MinPitch);
                var slot = (byte)(SplitMix64.At(seed, counter + 4) % (ulong)weaponSlots);
                counter += DrawsPerCommand;

                var pitchLimit = c == 0 && k < 2;
                if (pitchLimit)
                {
                    pitch = (short)(k == 0 ? MinPitch : MaxPitch);
                }

                var sequence = lastSequence[c] + 1u;
                if (!pitchLimit && validity % InvalidEvery == 0)
                {
                    switch (invalid % 4)
                    {
                        case 0:
                            sequence = lastSequence[c];
                            break;
                        case 1:
                            buttons |= (ushort)(FirstReservedButton << (int)((validity / InvalidEvery) % ReservedButtonBits));
                            break;
                        case 2:
                            var magnitude = MaxPitch + 1 + (int)(validity % OutOfRangePitchSpan);
                            pitch = (short)((validity >> 63) == 0 ? magnitude : -magnitude);
                            break;
                        default:
                            slot = (byte)(weaponSlots + (int)(validity % (ulong)(SlotValues - weaponSlots)));
                            break;
                    }

                    invalid++;
                }
                else
                {
                    lastSequence[c] = sequence;
                }

                commands[c] = new RecordedCommand((byte)c, new InputCommand(sequence, step, (Buttons)buttons, yaw, pitch, slot));
            }

            steps[k] = new RecordedStep(step, commands, 0UL);
        }

        return new RecordedStream(SimCoreVersion.RulesRevision, stepRateHz, seed, matchSalt, world.Checksum, config, initialState, steps);
    }

    /// <summary>
    /// Characters 1 to <paramref name="count"/>, 4 m apart along +X from the flat-run start on the flat
    /// ground in front of the firing lane, each 0.5 m above the terrain there and airborne, facing yaw 0.
    /// </summary>
    private static ScenarioCharacter[] SpawnRow(CollisionWorld world, int count)
    {
        var start = TestWorldGenerator.Features.FlatRunStart;
        var characters = new ScenarioCharacter[count];
        for (var c = 0; c < count; c++)
        {
            var x = start.X + (Spacing * c);
            var position = new Vec3(x, world.HeightAt(x, start.Z) + SpawnHeight, start.Z);
            characters[c] = new ScenarioCharacter((uint)c + 1u, position, 0, static _ => 0) { Grounded = false };
        }

        return characters;
    }
}
