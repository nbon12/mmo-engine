using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Agreement;

/// <summary>
/// The generated streams the agreement suite runs beside the committed ones (FR-026 server half, US2
/// scenario 3; contracts/simcore-cli.md#generated-streams). <c>StreamGenerator.Build(seed, config, world)</c>
/// makes <c>seed-&lt;nnn&gt;</c> from its seed alone, through the core's counter-based SplitMix64: the rate
/// cycles 20, 30 and 60 Hz by seed, each stream has 300 steps and two to four characters on the test world,
/// every command field is drawn over its valid range, and about one command in 64 is made invalid on
/// purpose, so every build runs the rejection path too (US1 scenario 9).
/// </summary>
/// <remarks>
/// Streams are built with <see cref="TestConfigs.Default"/>, the configuration of
/// <c>simcore/config/default.json</c>, over <see cref="TestWorlds.TestWorld"/>, the generated test world.
/// The coverage test replays each stream's commands through <see cref="InputValidator.Validate"/> with each
/// character's <c>lastSequence</c> tracked as the world tracks it: it starts at the initial state's value,
/// an accepted command sets it to the command's sequence, and a rejected command leaves it unchanged.
/// Nothing is written to disk.
/// </remarks>
public sealed class StreamGeneratorTests
{
    /// <summary>The first and last seeds the agreement suite generates.</summary>
    private const int FirstSeed = 1;

    private const int LastSeed = 50;

    /// <summary>Every generated stream's step count.</summary>
    private const int StepCount = 300;

    /// <summary>The known button bits, 0 to 8.</summary>
    private const int KnownButtonBits = 9;

    private const short MinPitch = -16384;

    private const short MaxPitch = 16384;

    /// <summary>A 1/16 turn of yaw as a binary angle: 65,536 / 16.</summary>
    private const int YawRangeWidth = 4096;

    private const int YawRanges = 16;

    /// <summary>The four rejection reasons, in contract order.</summary>
    private static readonly InputRejection[] RejectionReasons =
    [
        InputRejection.SequenceNotIncreasing,
        InputRejection.UnknownButtons,
        InputRejection.PitchOutOfRange,
        InputRejection.UnknownWeaponSlot,
    ];

    /// <summary>The seeds whose shape is checked: the first three and the last.</summary>
    public static TheoryData<int> ShapeSeeds => [1, 2, 3, 50];

    /// <summary>
    /// FR-026 and US2 scenario 3: Given <c>StreamGenerator.Build(seed, default.json, test world)</c> for
    /// seeds 1 to 50, When each stream's commands are replayed through <see cref="InputValidator.Validate"/>
    /// with each character's <c>lastSequence</c> tracked as the world does, Then together the seeds include
    /// accepted commands setting each button bit 0 to 8; accepted commands with pitch −16384 and with pitch
    /// 16384; accepted commands whose yaw falls in each of the 16 ranges [k × 4096, (k + 1) × 4096); at least
    /// one command rejected with each of <c>SequenceNotIncreasing</c>, <c>UnknownButtons</c>,
    /// <c>PitchOutOfRange</c> and <c>UnknownWeaponSlot</c>; streams at 20, 30 and 60 Hz; and between 1/128 and
    /// 1/32 of all commands invalid.
    /// </summary>
    [Fact]
    public void GeneratedStreamsCoverEveryButtonAndTheAimRange()
    {
        var coverage = new Coverage();
        var rates = new SortedSet<int>();
        for (var seed = FirstSeed; seed <= LastSeed; seed++)
        {
            var stream = StreamGenerator.Build(seed, TestConfigs.Default(), TestWorlds.TestWorld);
            rates.Add(stream.StepHz);
            Replay(stream, $"seed {seed}", coverage);
        }

        for (var bit = 0; bit < KnownButtonBits; bit++)
        {
            Assert.True(
                (coverage.AcceptedButtons & (1 << bit)) != 0,
                $"no accepted command of seeds {FirstSeed}-{LastSeed} sets button bit {bit} (0x{1 << bit:X4}); accepted bits 0x{coverage.AcceptedButtons:X4}");
        }

        Assert.True(coverage.AcceptedMinPitch, $"no accepted command of seeds {FirstSeed}-{LastSeed} has pitch {MinPitch}");
        Assert.True(coverage.AcceptedMaxPitch, $"no accepted command of seeds {FirstSeed}-{LastSeed} has pitch {MaxPitch}");
        for (var k = 0; k < YawRanges; k++)
        {
            Assert.True(
                coverage.AcceptedYawRanges[k],
                $"no accepted command of seeds {FirstSeed}-{LastSeed} has a yaw in [{k * YawRangeWidth}, {(k + 1) * YawRangeWidth})");
        }

        foreach (var reason in RejectionReasons)
        {
            Assert.True(
                coverage.Rejections[(int)reason] > 0,
                $"no command of seeds {FirstSeed}-{LastSeed} is rejected with {reason}; rejections [{string.Join(", ", coverage.Rejections)}]");
        }

        Assert.True(rates.SetEquals(new[] { 20, 30, 60 }), $"seeds {FirstSeed}-{LastSeed} run at [{string.Join(", ", rates)}] Hz, not 20, 30 and 60");
        Assert.True(coverage.Commands > 0, $"seeds {FirstSeed}-{LastSeed} hold no commands");
        Assert.True(
            coverage.Invalid * 128L >= coverage.Commands && coverage.Invalid * 32L <= coverage.Commands,
            $"{coverage.Invalid} of {coverage.Commands} commands are invalid; between 1/128 and 1/32 should be");
    }

    /// <summary>
    /// contracts/simcore-cli.md#generated-streams: Given seed <paramref name="seed"/>, When
    /// <c>StreamGenerator.Build(seed, default.json, test world)</c> runs, Then <c>NameFor(seed)</c> is
    /// <c>seed-001</c>, <c>seed-002</c>, <c>seed-003</c> or <c>seed-050</c>; <c>StepHz</c> is
    /// <c>{20, 30, 60}[seed % 3]</c>; it has 300 steps and 2 to 4 characters; its header seed is the seed and
    /// its collision checksum the test world's; every step carries one command per character, in ascending
    /// index order; and <see cref="StreamRunner"/> runs it with no refusal and no fault, one record per step.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShapeSeeds))]
    public void EachGeneratedStreamHasTheContractShape(int seed)
    {
        var expectedName = seed switch
        {
            1 => "seed-001",
            2 => "seed-002",
            3 => "seed-003",
            50 => "seed-050",
            _ => throw new ArgumentOutOfRangeException(nameof(seed), seed, "Unknown row."),
        };
        Assert.Equal(expectedName, StreamGenerator.NameFor(seed));

        var stream = StreamGenerator.Build(seed, TestConfigs.Default(), TestWorlds.TestWorld);
        var label = $"seed {seed}";

        var expectedHz = new[] { 20, 30, 60 }[seed % 3];
        Assert.True(stream.StepHz == expectedHz, $"{label}: stepHz {stream.StepHz}, expected {expectedHz}");
        Assert.True(stream.Steps.Count == StepCount, $"{label}: {stream.Steps.Count} steps, expected {StepCount}");
        Assert.True(
            stream.CharacterCount >= 2 && stream.CharacterCount <= 4,
            $"{label}: {stream.CharacterCount} characters, expected 2 to 4");
        Assert.True(stream.Seed == (ulong)seed, $"{label}: header seed {stream.Seed}, expected {seed}");
        Assert.True(
            stream.CollisionChecksum == TestWorlds.TestWorld.Checksum,
            $"{label}: collisionChecksum {stream.CollisionChecksum:x16}, expected the test world's {TestWorlds.TestWorld.Checksum:x16}");

        for (var i = 0; i < stream.Steps.Count; i++)
        {
            var commands = stream.Steps[i].Commands;
            Assert.True(
                commands.Length == stream.CharacterCount,
                $"{label}: steps[{i}] has {commands.Length} commands for {stream.CharacterCount} characters");
            for (var c = 0; c < commands.Length; c++)
            {
                Assert.True(
                    commands[c].CharacterIndex == c,
                    $"{label}: steps[{i}].commands[{c}] is for character index {commands[c].CharacterIndex}, expected {c}");
            }
        }

        // Throws on a refusal (CoreFormatException, ConfigValidationException) or a fault (StepFaultException).
        var records = StreamRunner.Run(stream.Encode(), new[] { TestWorlds.TestWorld });
        var offset = 0;
        for (var i = 0; i < stream.Steps.Count; i++)
        {
            Assert.True(
                StepRecord.TryRead(records.AsSpan(offset), out var step, out _, out _, out var length),
                $"{label}: the run's records hold no record for steps[{i}] at byte {offset} of {records.Length}");
            Assert.True(
                step == stream.Steps[i].Step,
                $"{label}: record {i} is step {step}, expected {stream.Steps[i].Step}");
            offset += length;
        }

        Assert.True(offset == records.Length, $"{label}: the run's records run {records.Length - offset} bytes past the {stream.Steps.Count} steps");
    }

    /// <summary>
    /// US2 scenario 3 (identical on every run): Given seed 17, When it is built twice and each build is
    /// encoded, Then the two encodings are byte-identical; and the encoding of seed 18 differs from them.
    /// </summary>
    [Fact]
    public void SameSeedGivesTheSameStreamBytes()
    {
        var first = StreamGenerator.Build(17, TestConfigs.Default(), TestWorlds.TestWorld).Encode();
        var second = StreamGenerator.Build(17, TestConfigs.Default(), TestWorlds.TestWorld).Encode();
        var other = StreamGenerator.Build(18, TestConfigs.Default(), TestWorlds.TestWorld).Encode();

        Assert.True(first.AsSpan().SequenceEqual(second), "two builds of seed 17 encode to different bytes");
        Assert.False(first.AsSpan().SequenceEqual(other), "seeds 17 and 18 encode to the same bytes");
    }

    /// <summary>
    /// Replays <paramref name="stream"/>'s commands through <see cref="InputValidator.Validate"/> in entry
    /// order, as stage 1 of the world step does, and adds what they cover to <paramref name="coverage"/>.
    /// Each character's <c>lastSequence</c> starts at the initial state's; an accepted command sets it.
    /// </summary>
    private static void Replay(RecordedStream stream, string label, Coverage coverage)
    {
        var weaponSlots = stream.Config.Input?.WeaponSlots
            ?? throw new InvalidOperationException($"{label}: the stream's configuration has no input section.");
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        var lastSequence = new uint[initial.Characters.Count];
        for (var c = 0; c < lastSequence.Length; c++)
        {
            lastSequence[c] = initial.Characters[c].LastSequence;
        }

        foreach (var entry in stream.Steps)
        {
            var previousIndex = -1;
            foreach (var recorded in entry.Commands)
            {
                int index = recorded.CharacterIndex;
                Assert.True(
                    index > previousIndex && index < lastSequence.Length,
                    $"{label}: step {entry.Step} has a command for character index {index} after index {previousIndex}, of {lastSequence.Length} characters");
                previousIndex = index;

                var command = recorded.Command;
                var reason = InputValidator.Validate(in command, lastSequence[index], weaponSlots);
                coverage.Commands++;
                if (reason == InputRejection.None)
                {
                    lastSequence[index] = command.Sequence;
                    coverage.AcceptedButtons |= (ushort)command.Buttons;
                    coverage.AcceptedMinPitch |= command.AimPitch == MinPitch;
                    coverage.AcceptedMaxPitch |= command.AimPitch == MaxPitch;
                    coverage.AcceptedYawRanges[command.AimYaw / YawRangeWidth] = true;
                }
                else
                {
                    coverage.Invalid++;
                    coverage.Rejections[(int)reason]++;
                }
            }
        }
    }

    /// <summary>What a set of replayed commands covers.</summary>
    private sealed class Coverage
    {
        /// <summary>The union of the accepted commands' button bits.</summary>
        public int AcceptedButtons { get; set; }

        public bool AcceptedMinPitch { get; set; }

        public bool AcceptedMaxPitch { get; set; }

        /// <summary>Whether an accepted command has a yaw in [k × 4096, (k + 1) × 4096), by k.</summary>
        public bool[] AcceptedYawRanges { get; } = new bool[YawRanges];

        /// <summary>The rejected commands, by <see cref="InputRejection"/> value.</summary>
        public int[] Rejections { get; } = new int[5];

        public long Commands { get; set; }

        public long Invalid { get; set; }
    }
}
