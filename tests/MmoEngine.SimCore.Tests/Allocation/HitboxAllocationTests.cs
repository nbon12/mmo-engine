using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: <c>HitboxBuilder.Build</c>, which stage 4 of every world step runs per
/// character, allocates nothing (data-model.md#hitbox-layout-and-hitboxes). Every row warms its call up
/// twice, then counts <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three batches of 1,000
/// calls into a three-element <see cref="Hitbox"/> buffer allocated before the count
/// (<see cref="ZeroAllocation"/>), and requires the smallest batch to be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class HitboxAllocationTests
{
    /// <summary>
    /// The stance's layout from the placeholders at 30 Hz, the feet at (0.5, 0, 0.5) and aim yaw
    /// <c>(ushort)(64 * i)</c> for call i of a batch, so each batch sweeps yaws 0 to 63,936.
    /// </summary>
    [Theory]
    [InlineData(Stance.Standing)]
    [InlineData(Stance.Crouched)]
    public void BuildAllocatesNothing(Stance stance)
    {
        var layout = TestConfigs.Compiled(30).HitboxLayouts[(int)stance];
        var position = new Vec3(0.5f, 0f, 0.5f);
        var hitboxes = new Hitbox[3];
        var bounds = default(HitboxBounds);

        var counts = ZeroAllocation.MeasureBatches(i => bounds = HitboxBuilder.Build(layout, position, (ushort)(64 * i), hitboxes));

        ZeroAllocation.AssertABatchAllocatedNothing(counts, $"{stance}: HitboxBuilder.Build into a 3-element span");

        // The calls built the stance's three capsules and its bounding sphere.
        Assert.Equal(HitboxPart.Head, hitboxes[0].Part);
        Assert.Equal(HitboxPart.Torso, hitboxes[1].Part);
        Assert.Equal(HitboxPart.Legs, hitboxes[2].Part);
        Assert.True(
            BitConverter.SingleToUInt32Bits(bounds.Radius) == BitConverter.SingleToUInt32Bits(layout.BoundRadius),
            $"{stance}: the last call's bounding radius is {bounds.Radius:R}, the layout's BoundRadius is {layout.BoundRadius:R}");
    }
}
