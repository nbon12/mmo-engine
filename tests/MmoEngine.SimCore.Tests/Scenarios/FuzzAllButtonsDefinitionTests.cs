using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The <c>fuzz-all-buttons</c> definition of <see cref="ScenarioCatalog"/> (US1 scenario 9, US2 scenario 3;
/// the fuzz-all-buttons row of contracts/simcore-cli.md#named-scenario-streams): four characters and one
/// seeded fuzz stream from the fixed seed 1000, covering every button, the full aim range and the four
/// rejection reasons. Its <c>.mmrs</c> file is not written here: it presses Fire, so Phase 7 records it.
/// </summary>
/// <remarks>
/// The definition is built with <see cref="TestConfigs.Default"/> over <see cref="TestWorlds.TestWorld"/>,
/// the generated test world. Its commands are replayed through <see cref="InputValidator.Validate"/> with
/// each character's <c>lastSequence</c> tracked as the world tracks it: it starts at the initial state's
/// value, an accepted command sets it to the command's sequence, and a rejected command leaves it unchanged.
/// Nothing is written to disk.
/// </remarks>
public sealed class FuzzAllButtonsDefinitionTests
{
    private const string Name = "fuzz-all-buttons";

    private const int CharacterCount = 4;

    private const int StepCount = 300;

    private const ulong Seed = 1000UL;

    /// <summary>The rate the committed stream is recorded at, the default 30 Hz.</summary>
    private const int RecordedRateHz = 30;

    /// <summary>The known button bits, 0 to 8.</summary>
    private const int KnownButtonBits = 9;

    /// <summary>The reserved button bits, 9 to 15.</summary>
    private const ushort ReservedButtons = 0xFE00;

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

    /// <summary>
    /// The fuzz-all-buttons row of contracts/simcore-cli.md#named-scenario-streams (US1 scenario 9, US2
    /// scenario 3): Given <c>ScenarioCatalog.Get("fuzz-all-buttons").Build(TestConfigs.Default(), test world, hz)</c>,
    /// Then <see cref="ScenarioCatalog.Names"/> holds <c>fuzz-all-buttons</c> and is in ordinal order; the
    /// stream runs at <paramref name="hz"/> with 4 characters, 300 steps and header seed 1000, while the
    /// definition itself is recorded at 30 Hz, lists no characters (the generator places them) and has
    /// <c>DurationSeconds</c> 10, its 300 steps at 30 Hz; When its
    /// commands are replayed through <see cref="InputValidator.Validate"/>, Then accepted commands set every
    /// button bit 0 to 8, a rejected command sets a reserved bit (<c>0xFE00</c>), accepted commands reach pitch
    /// −16384 and 16384 and every 1/16 turn of yaw, and commands are rejected with all four reasons; two
    /// builds encode to identical bytes; and <see cref="StreamRunner"/> runs it to the end with no fault.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void DefinitionCoversEveryButtonTheAimRangeAndEveryRejection(int hz)
    {
        Assert.Contains(Name, ScenarioCatalog.Names);
        for (var i = 1; i < ScenarioCatalog.Names.Count; i++)
        {
            Assert.True(
                string.CompareOrdinal(ScenarioCatalog.Names[i - 1], ScenarioCatalog.Names[i]) < 0,
                $"ScenarioCatalog.Names is not in ordinal order at [{i - 1}] '{ScenarioCatalog.Names[i - 1]}' and [{i}] '{ScenarioCatalog.Names[i]}'");
        }

        var definition = ScenarioCatalog.Get(Name);
        var stream = definition.Build(TestConfigs.Default(), TestWorlds.TestWorld, hz);
        var label = $"{Name} at {hz} Hz";

        Assert.True(stream.StepHz == hz, $"{label}: stepHz {stream.StepHz}");
        Assert.True(stream.CharacterCount == CharacterCount, $"{label}: {stream.CharacterCount} characters, expected {CharacterCount}");
        Assert.True(stream.Steps.Count == StepCount, $"{label}: {stream.Steps.Count} steps, expected {StepCount}");
        Assert.True(stream.Seed == Seed, $"{label}: header seed {stream.Seed}, expected {Seed}");

        // A generated definition places its own characters and builds the same step count at every rate, so
        // its DurationSeconds is that count at its own rate (300 at 30 Hz), not the built stream's at hz.
        Assert.True(definition.StepRateHz == RecordedRateHz, $"{label}: the definition's rate is {definition.StepRateHz} Hz, expected {RecordedRateHz}");
        Assert.True(definition.Characters.Count == 0, $"{label}: the definition lists {definition.Characters.Count} characters; a generated definition places its own");
        Assert.True(
            definition.DurationSeconds == (double)StepCount / RecordedRateHz,
            $"{label}: DurationSeconds is {definition.DurationSeconds}, expected {StepCount} steps at {RecordedRateHz} Hz");

        // The commands, replayed as stage 1 of the world step validates them.
        var weaponSlots = stream.Config.Input?.WeaponSlots
            ?? throw new InvalidOperationException($"{label}: the stream's configuration has no input section.");
        var initial = StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);
        var lastSequence = new uint[initial.Characters.Count];
        for (var c = 0; c < lastSequence.Length; c++)
        {
            lastSequence[c] = initial.Characters[c].LastSequence;
        }

        var acceptedButtons = 0;
        var rejectedWithReservedButtons = 0;
        var acceptedMinPitch = false;
        var acceptedMaxPitch = false;
        var acceptedYawRanges = new bool[YawRanges];
        var rejections = new int[5];
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
                if (reason == InputRejection.None)
                {
                    lastSequence[index] = command.Sequence;
                    acceptedButtons |= (ushort)command.Buttons;
                    acceptedMinPitch |= command.AimPitch == MinPitch;
                    acceptedMaxPitch |= command.AimPitch == MaxPitch;
                    acceptedYawRanges[command.AimYaw / YawRangeWidth] = true;
                }
                else
                {
                    rejections[(int)reason]++;
                    if (((ushort)command.Buttons & ReservedButtons) != 0)
                    {
                        rejectedWithReservedButtons++;
                    }
                }
            }
        }

        for (var bit = 0; bit < KnownButtonBits; bit++)
        {
            Assert.True(
                (acceptedButtons & (1 << bit)) != 0,
                $"{label}: no accepted command sets button bit {bit} (0x{1 << bit:X4}); accepted bits 0x{acceptedButtons:X4}");
        }

        Assert.True(rejectedWithReservedButtons > 0, $"{label}: no rejected command sets a reserved button bit (0x{ReservedButtons:X4})");
        Assert.True(acceptedMinPitch, $"{label}: no accepted command has pitch {MinPitch}");
        Assert.True(acceptedMaxPitch, $"{label}: no accepted command has pitch {MaxPitch}");
        for (var k = 0; k < YawRanges; k++)
        {
            Assert.True(
                acceptedYawRanges[k],
                $"{label}: no accepted command has a yaw in [{k * YawRangeWidth}, {(k + 1) * YawRangeWidth})");
        }

        foreach (var reason in RejectionReasons)
        {
            Assert.True(
                rejections[(int)reason] > 0,
                $"{label}: no command is rejected with {reason}; rejections [{string.Join(", ", rejections)}]");
        }

        // Deterministic: a second build encodes to the same bytes.
        var encoded = stream.Encode();
        var again = ScenarioCatalog.Get(Name).Build(TestConfigs.Default(), TestWorlds.TestWorld, hz).Encode();
        Assert.True(encoded.AsSpan().SequenceEqual(again), $"{label}: two builds encode to different bytes");

        // Throws on a refusal (CoreFormatException, ConfigValidationException) or a fault (StepFaultException).
        var records = StreamRunner.Run(encoded, new[] { TestWorlds.TestWorld });
        var offset = 0;
        for (var i = 0; i < stream.Steps.Count; i++)
        {
            Assert.True(
                StepRecord.TryRead(records.AsSpan(offset), out var step, out _, out _, out var length),
                $"{label}: the run's records hold no record for steps[{i}] at byte {offset} of {records.Length}");
            Assert.True(step == stream.Steps[i].Step, $"{label}: record {i} is step {step}, expected {stream.Steps[i].Step}");
            offset += length;
        }

        Assert.True(offset == records.Length, $"{label}: the run's records run {records.Length - offset} bytes past the {stream.Steps.Count} steps");
    }
}
