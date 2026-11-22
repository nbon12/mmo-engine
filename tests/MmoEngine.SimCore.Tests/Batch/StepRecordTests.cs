using System.Buffers.Binary;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Batch;

/// <summary>
/// A step record is <c>u32 step | u32 stateLength | state | u64 hash</c>, little-endian, 16 bytes plus
/// the state (contracts/state-serialization.md#step-record). Packed records follow each other with no
/// header and no padding, and a reader walks them by <c>stateLength</c>.
/// </summary>
public sealed class StepRecordTests
{
    /// <summary>16 + 136: the worked example's record.</summary>
    private const int WorkedExampleRecordLength = 152;

    private const ulong WorkedExampleHash = 0x35038da2f2d9459cUL;

    /// <summary>The second record's step: <see cref="TestStates.TwoCharacters"/> with its <c>step</c> set to 5.</summary>
    private const uint SecondStep = 5;

    [Fact]
    public void RecordIsStepLengthStateAndHash()
    {
        var workedExample = TestStates.WorkedExample;

        // The worked example's record: step 0, length 0x88 (136), the state, then the hash, all little-endian.
        var record = new byte[WorkedExampleRecordLength];
        Assert.True(
            StepRecord.Write(0, workedExample, WorkedExampleHash, record, out var written),
            "Write refused a 152-byte destination for a 136-byte state");
        Assert.Equal(WorkedExampleRecordLength, written);
        var expected = "00000000" + "88000000" + Convert.ToHexString(workedExample) + "9C45D9F2A28D0335";
        Assert.Equal(expected, Convert.ToHexString(record));

        // One byte short: refused, and no partial record is left in the destination.
        var tooShort = new byte[WorkedExampleRecordLength - 1];
        Assert.False(StepRecord.Write(0, workedExample, WorkedExampleHash, tooShort, out _), "Write accepted a 151-byte destination");
        Assert.Equal(new byte[WorkedExampleRecordLength - 1], tooShort);

        // Two records of different lengths back to back, as in a .records file.
        var second = TestStates.Mutate(TestStates.TwoCharacters, 0, TestStates.U32(SecondStep));
        var secondHash = StateHash.Compute(second);
        var packed = new byte[WorkedExampleRecordLength + 16 + second.Length];
        Assert.True(StepRecord.Write(0, workedExample, WorkedExampleHash, packed, out var firstLength), "Write refused the first record");
        Assert.True(StepRecord.Write(SecondStep, second, secondHash, packed.AsSpan(firstLength), out var secondLength), "Write refused the second record");
        Assert.Equal(WorkedExampleRecordLength, firstLength);
        Assert.Equal(16 + second.Length, secondLength);
        Assert.Equal((uint)second.Length, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(firstLength + 4)));

        var offset = 0;
        AssertReads(packed, ref offset, "record 0", 0, workedExample, WorkedExampleHash);
        AssertReads(packed, ref offset, "record 1", SecondStep, second, secondHash);
        Assert.Equal(packed.Length, offset);

        // A record cut off in the middle of its state is refused.
        var midState = packed.AsSpan(0, 8 + 100).ToArray();
        Assert.False(StepRecord.TryRead(midState, out _, out _, out _, out _), "TryRead accepted a record truncated 100 bytes into its 136-byte state");
    }

    /// <summary>Reads the record at <paramref name="offset"/> and moves past it.</summary>
    private static void AssertReads(byte[] packed, ref int offset, string label, uint step, byte[] state, ulong hash)
    {
        Assert.True(
            StepRecord.TryRead(packed.AsSpan(offset), out var readStep, out var readState, out var readHash, out var length),
            $"{label}: TryRead refused the record at offset {offset}");
        Assert.True(readStep == step, $"{label}: step {readStep}, expected {step}");
        Assert.True(readState.SequenceEqual(state), $"{label}: state {Convert.ToHexString(readState)}, expected {Convert.ToHexString(state)}");
        Assert.True(readHash == hash, $"{label}: hash {readHash:x16}, expected {hash:x16}");
        Assert.Equal(16 + state.Length, length);
        offset += length;
    }
}
