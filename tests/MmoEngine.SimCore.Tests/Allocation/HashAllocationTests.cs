using System.IO.Hashing;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: once warmed up, hashing a canonical state and writing a step record allocate
/// no managed memory (contracts/state-serialization.md#state-hash and #step-record). Every row warms its
/// call up twice, then counts <see cref="GC.GetAllocatedBytesForCurrentThread"/> across ten windows of
/// 1,000 calls into buffers allocated before the count (<see cref="ZeroAllocation"/>), and requires every
/// window to be exactly 0 bytes (all ten of a single rerun when exactly one window of the first
/// measurement is not).
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class HashAllocationTests
{
    /// <summary>contracts/state-serialization.md#worked-example.</summary>
    private const ulong WorkedExampleHash = 0x35038da2f2d9459cUL;

    /// <summary>16 + 136: the worked example's step record.</summary>
    private const int WorkedExampleRecordLength = 152;

    /// <summary>The worked example (136 bytes), and a buffer of the placeholders' MaxStateLength at 30 Hz.</summary>
    public static TheoryData<string> StateCases => ["worked-example", "max-length"];

    [Theory]
    [MemberData(nameof(StateCases))]
    public void StateHashAllocatesNothing(string stateCase)
    {
        var state = State(stateCase);
        var expected = XxHash64.HashToUInt64(state, 0);
        var mismatches = 0;

        var measurement = ZeroAllocation.Measure(_ =>
        {
            if (StateHash.Compute(state) != expected)
            {
                mismatches++;
            }
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(measurement, $"{stateCase}: StateHash.Compute of {state.Length} bytes");
        Assert.True(
            mismatches == 0,
            $"{stateCase}: {mismatches} of {measurement.Calls} calls did not return XxHash64 (seed 0) of the state, {expected:x16}");
    }

    [Fact]
    public void StepRecordWriteAllocatesNothing()
    {
        var state = TestStates.WorkedExample;
        var destination = new byte[WorkedExampleRecordLength];
        var refusals = 0;
        var written = 0;

        var measurement = ZeroAllocation.Measure(_ =>
        {
            if (!StepRecord.Write(0u, state, WorkedExampleHash, destination, out written))
            {
                refusals++;
            }
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(
            measurement,
            $"worked-example record: StepRecord.Write of step 0 and its {state.Length}-byte state into {destination.Length} bytes");
        Assert.True(
            refusals == 0,
            $"worked-example record: StepRecord.Write refused the {WorkedExampleRecordLength}-byte destination on {refusals} of {measurement.Calls} calls");
        Assert.Equal(WorkedExampleRecordLength, written);
        Assert.Equal("00000000" + "88000000" + Convert.ToHexString(state) + "9C45D9F2A28D0335", Convert.ToHexString(destination));
    }

    private static byte[] State(string stateCase) => stateCase switch
    {
        "worked-example" => TestStates.WorkedExample,
        "max-length" => MaxLengthBuffer(),
        _ => throw new ArgumentOutOfRangeException(nameof(stateCase), stateCase, "Unknown state case."),
    };

    /// <summary><c>CompiledConfig.MaxStateLength</c> bytes for the placeholders at 30 Hz, filled with <c>(byte)(i * 31)</c>.</summary>
    private static byte[] MaxLengthBuffer()
    {
        var buffer = new byte[TestConfigs.Compiled(30).MaxStateLength];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = unchecked((byte)(i * 31));
        }

        return buffer;
    }
}
