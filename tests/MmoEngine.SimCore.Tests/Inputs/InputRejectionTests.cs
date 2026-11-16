using System.Buffers.Binary;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Inputs;

/// <summary>
/// Validation inside <c>SimWorld.Step</c> (stage 1, US1 scenario 9, contracts/input-command-format.md#validation):
/// a rejected command is reported in the step's events as <c>{characterId, reason}</c> and changes nothing
/// else, because the character steps with the neutral input (no buttons, aim, slot and <c>lastSequence</c>
/// unchanged); an accepted command sets <c>aimYaw</c>, <c>aimPitch</c> and <c>lastSequence</c>, so the same
/// command replayed is rejected every time. Every world is on <see cref="MovementWorlds.Flat"/>, with one
/// character (ID 1) grounded at rest height, yaw 1000, pitch 0, <c>lastSequence</c> 100 and slot 0 active.
/// </summary>
public sealed class InputRejectionTests
{
    private const ulong Salt = 7;

    private const ushort StartYaw = 1000;

    private const uint StartSequence = 100;

    /// <summary>Every field of the faulty commands is valid except the one the row breaks.</summary>
    private const ushort CommandYaw = 12345;

    /// <summary>The four rejection codes, each crossed with 20, 30 and 60 Hz.</summary>
    public static TheoryData<InputRejection, int> Reasons => TestRates.WithRateLast(
        InputRejection.SequenceNotIncreasing,
        InputRejection.UnknownButtons,
        InputRejection.PitchOutOfRange,
        InputRejection.UnknownWeaponSlot);

    /// <summary>
    /// US1.9: given worlds A and B reset to the same state, when A steps with one command for character
    /// index 0 that holds Forward and Jump and breaks one rule (sequence 100, not above
    /// <c>lastSequence</c> 100; reserved button bit 9, buttons 0x0211; pitch 16385; slot 2 of 2) and B steps
    /// with no command, then A reports exactly one input rejection, <c>{1, reason}</c>, with the code's
    /// fixed value; A's state bytes before the events block equal B's byte for byte; A's events block is
    /// B's with <c>events.inputRejections.count</c> 1 and the one record <c>01 00 00 00 &lt;reason&gt;</c>;
    /// and A's character kept yaw 1000, <c>lastSequence</c> 100, slot 0, its X and Z, and Grounded
    /// (Forward and Jump were not applied).
    /// </summary>
    [Theory]
    [MemberData(nameof(Reasons))]
    public void InvalidCommandIsRejectedWithItsReasonAndLeavesTheStateUnchanged(InputRejection reason, int hz)
    {
        var flat = MovementWorlds.Flat();
        var a = NewWorld(flat, hz);
        var b = NewWorld(flat, hz);
        var command = FaultyCommand(reason);

        var reportA = a.Step([new CharacterCommand(0, command)]);
        Assert.Equal(StepFaultCode.None, reportA.Fault.Code);
        var rejections = reportA.Events.InputRejections.ToArray();
        var stateA = reportA.State.ToArray();

        var reportB = b.Step([]);
        Assert.Equal(StepFaultCode.None, reportB.Fault.Code);
        Assert.True(reportB.Events.InputRejections.IsEmpty, "B had no command, so it has no input rejection");
        var stateB = reportB.State.ToArray();

        Assert.Equal(new[] { new InputRejectionEvent { CharacterId = 1, Reason = reason } }, rejections);
        Assert.Equal(ReasonValue(reason), (byte)reason);

        var eventsA = EventsOffset(stateA);
        var eventsB = EventsOffset(stateB);
        Assert.Equal(eventsB, eventsA);
        Assert.Equal(stateB.AsSpan(0, eventsB).ToArray(), stateA.AsSpan(0, eventsA).ToArray());

        var blockB = stateB[eventsB..];
        var countB = InputRejectionCountOffset(blockB);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(blockB.AsSpan(countB)));
        byte[] expectedBlockA =
        [
            .. blockB[..countB],
            0x01, 0x00,
            0x01, 0x00, 0x00, 0x00, (byte)reason,
            .. blockB[(countB + 2)..],
        ];
        Assert.Equal(expectedBlockA, stateA[eventsA..]);

        var start = flat.Start;
        var character = Character(stateA);
        Assert.Equal(StartYaw, character.AimYaw);
        Assert.Equal(StartSequence, character.LastSequence);
        Assert.Equal(0, character.ActiveSlot);
        AssertSameBits(start.X, character.Position.X, "position.x");
        AssertSameBits(start.Z, character.Position.Z, "position.z");
        Assert.True(
            (character.Flags & CharacterFlags.Grounded) != 0,
            $"{reason} at {hz} Hz: the rejected command's Jump was applied; the character is airborne at {character.Position}");
    }

    /// <summary>
    /// contracts/input-command-format.md#validation: an accepted command sets <c>aimYaw</c>,
    /// <c>aimPitch</c> and <c>lastSequence</c>, and a replayed command is rejected every time. From the
    /// reset state, the command (sequence 101, yaw 12345, pitch −200, slot 0, no buttons) commits a
    /// character with yaw 12345, pitch −200 and <c>lastSequence</c> 101 and no rejection
    /// (<c>events.inputRejections.count</c> 0); the identical command again is rejected with
    /// SequenceNotIncreasing and leaves yaw, pitch and <c>lastSequence</c> as the first step set them.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void AcceptedCommandSetsAimAndLastSequenceAndAReplayIsRejected(int hz)
    {
        var world = NewWorld(MovementWorlds.Flat(), hz);
        var command = new InputCommand(StartSequence + 1, 0, 0, CommandYaw, -200, 0);

        var accepted = world.Step([new CharacterCommand(0, command)]);

        Assert.Equal(StepFaultCode.None, accepted.Fault.Code);
        Assert.True(accepted.Events.InputRejections.IsEmpty, $"the accepted command was rejected at {hz} Hz");
        var acceptedState = accepted.State.ToArray();
        Assert.Equal(0, InputRejectionCount(acceptedState));
        var first = Character(acceptedState);
        Assert.Equal(CommandYaw, first.AimYaw);
        Assert.Equal((short)-200, first.AimPitch);
        Assert.Equal(StartSequence + 1, first.LastSequence);

        var replayed = world.Step([new CharacterCommand(0, command)]);

        Assert.Equal(StepFaultCode.None, replayed.Fault.Code);
        Assert.Equal(
            new[] { new InputRejectionEvent { CharacterId = 1, Reason = InputRejection.SequenceNotIncreasing } },
            replayed.Events.InputRejections.ToArray());
        var replayedState = replayed.State.ToArray();
        Assert.Equal(1, InputRejectionCount(replayedState));
        var second = Character(replayedState);
        Assert.Equal(CommandYaw, second.AimYaw);
        Assert.Equal((short)-200, second.AimPitch);
        Assert.Equal(StartSequence + 1, second.LastSequence);
    }

    /// <summary>A world over <paramref name="flat"/> at <paramref name="hz"/>, reset to the one-character state.</summary>
    private static SimWorld NewWorld(MovementWorld flat, int hz)
    {
        var world = new SimWorld(CompiledConfig.Create(TestConfigs.At(hz)), flat.World);
        var character = flat.StartRecord() with { AimYaw = StartYaw, AimPitch = 0, LastSequence = StartSequence, ActiveSlot = 0 };
        world.Reset(TestStates.Build(0, [character]), Salt);
        return world;
    }

    /// <summary>
    /// Forward and Jump with yaw 12345, pitch 0, slot 0 and sequence 101, which <c>Validate</c> accepts,
    /// with the one field that makes it fail with <paramref name="reason"/>.
    /// </summary>
    private static InputCommand FaultyCommand(InputRejection reason)
    {
        var valid = new InputCommand(StartSequence + 1, 0, Buttons.Forward | Buttons.Jump, CommandYaw, 0, 0);
        return reason switch
        {
            InputRejection.SequenceNotIncreasing => valid with { Sequence = StartSequence },
            InputRejection.UnknownButtons => valid with { Buttons = (Buttons)0x0211 },
            InputRejection.PitchOutOfRange => valid with { AimPitch = 16385 },
            InputRejection.UnknownWeaponSlot => valid with { WeaponSlot = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a rejection reason."),
        };
    }

    /// <summary>The code values of data-model.md#reason-and-outcome-codes, which are part of the hashed state.</summary>
    private static byte ReasonValue(InputRejection reason) => reason switch
    {
        InputRejection.SequenceNotIncreasing => 1,
        InputRejection.UnknownButtons => 2,
        InputRejection.PitchOutOfRange => 3,
        InputRejection.UnknownWeaponSlot => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a rejection reason."),
    };

    /// <summary>The committed character of a one-character state.</summary>
    private static CharacterState Character(byte[] state)
    {
        var characters = StateSerializer.ReadCommitted(state).Characters;
        Assert.Single(characters);
        return characters[0];
    }

    /// <summary>
    /// The offset of the events block, <c>12 + R·c + 49·p</c> with R = 52 + 30·W and the placeholder W of 2
    /// (contracts/state-serialization.md#layout), from the header's counts.
    /// </summary>
    private static int EventsOffset(byte[] state)
    {
        var characters = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(4));
        var projectiles = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(6));
        return TestStates.HeaderLength
            + (TestStates.CharacterLength(TestStates.PlaceholderWeaponSlots) * characters)
            + (TestStates.ProjectileLength * projectiles);
    }

    /// <summary>
    /// The offset, inside an events block, of <c>events.inputRejections.count</c>: after the shots
    /// (2 + 25 each), the hits (2 + 26 each) and the expiries (2 + 5 each).
    /// </summary>
    private static int InputRejectionCountOffset(byte[] events)
    {
        var offset = 0;
        offset += 2 + (TestStates.ShotLength * BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(offset)));
        offset += 2 + (TestStates.HitLength * BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(offset)));
        offset += 2 + (TestStates.SmallEventLength * BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(offset)));
        return offset;
    }

    /// <summary><c>events.inputRejections.count</c> as the state's bytes hold it.</summary>
    private static int InputRejectionCount(byte[] state)
    {
        var events = state[EventsOffset(state)..];
        return BinaryPrimitives.ReadUInt16LittleEndian(events.AsSpan(InputRejectionCountOffset(events)));
    }

    private static void AssertSameBits(float expected, float actual, string field) =>
        Assert.True(
            BitConverter.SingleToUInt32Bits(expected) == BitConverter.SingleToUInt32Bits(actual),
            $"{field}: expected {expected:R} (0x{BitConverter.SingleToUInt32Bits(expected):X8}), got {actual:R} (0x{BitConverter.SingleToUInt32Bits(actual):X8})");
}
