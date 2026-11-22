using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: a warmed-up <c>MovementStep.Step</c>, which stage 2 of every world step runs per
/// character, allocates nothing (data-model.md#movement-step: the candidate buffer is the caller's, and
/// every loop is bounded). Every row warms its call up twice, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across ten windows of 1,000 calls into a candidate
/// buffer allocated before the count (<see cref="ZeroAllocation"/>), and requires every window to be
/// exactly 0 bytes (all ten of a single rerun when exactly one window of the first measurement is not).
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class MovementAllocationTests
{
    /// <summary>
    /// Rows <c>(setup, hz)</c>: <c>flat-run</c> on <see cref="MovementWorlds.Flat"/>, <c>wall-slide</c> on
    /// <see cref="MovementWorlds.Wall"/>(45) and <c>corner-wedge</c> on <see cref="MovementWorlds.Corner"/>,
    /// each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<string, int> Setups => TestRates.WithRateLast("flat-run", "wall-slide", "corner-wedge");

    /// <summary>
    /// A character grounded at rest at the world's <see cref="MovementWorld.Start"/> facing its
    /// <see cref="MovementWorld.Yaw"/> holds Forward: on the flat it runs, into the 45° wall it slides, and
    /// into the corner it wedges. The call is <c>MovementStep.Step(ref character, in input, config, world,
    /// candidates)</c> with <see cref="TestConfigs.Compiled"/> at <paramref name="hz"/> and a candidate span of
    /// <c>world.MaxQueryCandidates((3f * 0.4f) + (2f * 0.01f))</c> entries, the size <c>SimWorld</c> uses. The
    /// character carries over from call to call and is set back to its start state every <c>2 * hz</c> calls
    /// (a struct assignment), so the run stays in the world. Every call must return <c>Fault</c> None.
    /// </summary>
    [Theory]
    [MemberData(nameof(Setups))]
    public void MovementStepAllocatesNothing(string setup, int hz)
    {
        var movementWorld = World(setup);
        Assert.True(movementWorld.StartGrounded, $"{setup}: the start must be grounded at rest");
        var config = TestConfigs.Compiled(hz);
        var world = movementWorld.World;
        var candidates = new int[world.MaxQueryCandidates((3f * 0.4f) + (2f * 0.01f))];
        var start = movementWorld.StartCharacter();
        var character = start;
        var input = new InputCommand(1, 0, Buttons.Forward, movementWorld.Yaw, 0, 0);
        var period = 2 * hz;
        var faults = 0;
        var lastFault = StepFaultCode.None;

        var measurement = ZeroAllocation.Measure(call =>
        {
            if (call % period == 0)
            {
                character = start;
            }

            var result = MovementStep.Step(ref character, in input, config, world, candidates);
            if (result.Fault != StepFaultCode.None)
            {
                faults++;
                lastFault = result.Fault;
            }
        });

        ZeroAllocation.AssertEveryWindowAllocatedNothing(
            measurement,
            $"{setup} at {hz} Hz: MovementStep.Step with Forward held into a {candidates.Length}-entry candidate span");
        Assert.True(
            faults == 0,
            $"{setup} at {hz} Hz: {faults} of {measurement.Calls} calls returned a fault ({lastFault}) instead of StepFaultCode.None");
    }

    private static MovementWorld World(string setup) => setup switch
    {
        "flat-run" => MovementWorlds.Flat(),
        "wall-slide" => MovementWorlds.Wall(45f),
        "corner-wedge" => MovementWorlds.Corner(),
        _ => throw new ArgumentOutOfRangeException(nameof(setup), setup, "Unknown setup."),
    };
}
