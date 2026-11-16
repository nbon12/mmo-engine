using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: a warmed-up <c>SimWorld.Step</c> allocates nothing; every buffer it uses is
/// owned by the world and sized from the configured maxima when the world is created
/// (data-model.md#world-simworld). Phases 3, 6 and 7 replace the movement, weapon and projectile stage
/// bodies, so this row is the world-level regression they keep green. Each rate warms the step up twice,
/// then counts <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three batches of 1,000 steps
/// (<see cref="ZeroAllocation"/>), and requires the smallest batch to be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class WorldAllocationTests
{
    private const ulong Salt = 7;

    /// <summary>
    /// A world on <see cref="TestWorlds.Flat"/> with the placeholders at <paramref name="hz"/>, Reset with
    /// <see cref="TestStates.StandingOnFlat"/> and salt 7, steps with an empty command span. Every step
    /// commits.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepAllocatesNothing(int hz)
    {
        var world = new SimWorld(TestConfigs.Compiled(hz), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(TestStates.StandingOnFlat, Salt);
        var faults = 0;

        var counts = ZeroAllocation.MeasureBatches(_ =>
        {
            if (world.Step(ReadOnlySpan<CharacterCommand>.Empty).Fault.Code != StepFaultCode.None)
            {
                faults++;
            }
        });

        ZeroAllocation.AssertABatchAllocatedNothing(counts, $"{hz} Hz: SimWorld.Step with no commands");
        Assert.True(
            faults == 0,
            $"{hz} Hz: {faults} of {ZeroAllocation.TotalCalls} steps reported a fault instead of StepFaultCode.None");
        Assert.Equal((uint)ZeroAllocation.TotalCalls, world.CurrentStep);
    }
}
