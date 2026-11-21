using MmoEngine.SimCore.Benchmarks;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// US7.3, FR-032, SC-005: warmed-up steps allocate nothing, across every step function, driven by the
/// benchmarks' own <see cref="StepCase"/>s: the three movement cases through <c>MovementStep.Step</c>,
/// <c>rifle-hold</c> through <c>WeaponStep.Step</c>, <c>launcher-flight</c> through
/// <c>ProjectileStep.Step</c>, <c>standing</c> through <c>HitboxBuilder</c> and <c>fuzz-all-buttons</c>
/// through <c>SimWorld.Step</c>. It is the regression test over the per-entry-point allocation tests
/// (T036, T076, T115, T172 and T199), and it measures with their rule (<see cref="ZeroAllocation"/>):
/// three counted windows of 1,000 calls, the smallest of which must be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class StepAllocationTests
{
    /// <summary>
    /// Rows <c>(step, hz)</c>: each <c>kind/caseName</c> crossed with 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<string, int> Steps => TestRates.WithRateLast(
        "movement/flat-run",
        "movement/wall-slide",
        "movement/corner-wedge",
        "weapon/rifle-hold",
        "projectile/launcher-flight",
        "hitboxBuild/standing",
        "world/fuzz-all-buttons");

    /// <summary>
    /// Given <c>StepCase.Create(kind, caseName, hz)</c> warmed up by two full passes, with <c>Restart()</c>
    /// between them, When three further windows of 1,000 <c>Step()</c> calls run, a pass that ends inside a
    /// window (or before it) being restarted with <c>Restart()</c>, which is not counted, Then the smallest
    /// window's sum is exactly 0 bytes. Each window's count is the sum, over its runs of <c>Step()</c> calls
    /// between restarts, of <see cref="GC.GetAllocatedBytesForCurrentThread"/> after the run minus before it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Steps))]
    public void WarmedUpStepsAllocateNothing(string step, int hz)
    {
        var separator = step.IndexOf('/', StringComparison.Ordinal);
        var kind = step[..separator];
        var caseName = step[(separator + 1)..];
        var stepCase = StepCase.Create(kind, caseName, hz);
        Assert.True(stepCase.PassLength >= 1, $"{step} at {hz} Hz: PassLength is {stepCase.PassLength}; a pass must have at least one step");

        RunPass(stepCase);
        stepCase.Restart();
        RunPass(stepCase);

        var windows = new long[ZeroAllocation.Batches];
        for (var w = 0; w < windows.Length; w++)
        {
            var left = ZeroAllocation.CallsPerBatch;
            var bytes = 0L;
            while (left > 0)
            {
                if (stepCase.StepsLeftInPass <= 0)
                {
                    stepCase.Restart();
                    if (stepCase.StepsLeftInPass != stepCase.PassLength)
                    {
                        Assert.Fail($"{step} at {hz} Hz: Restart() left {stepCase.StepsLeftInPass} steps in the pass; it must restore PassLength {stepCase.PassLength}");
                    }
                }

                var run = left < stepCase.StepsLeftInPass ? left : stepCase.StepsLeftInPass;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < run; i++)
                {
                    stepCase.Step();
                }

                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                left -= run;
            }

            windows[w] = bytes;
        }

        ZeroAllocation.AssertABatchAllocatedNothing(
            windows,
            $"{step} at {hz} Hz: StepCase.Step() through {EntryPoint(kind)} (Restart() not counted)");
    }

    /// <summary>Runs one whole pass of <paramref name="stepCase"/>: <c>PassLength</c> <c>Step()</c> calls.</summary>
    private static void RunPass(StepCase stepCase)
    {
        for (var i = 0; i < stepCase.PassLength; i++)
        {
            stepCase.Step();
        }
    }

    private static string EntryPoint(string kind) => kind switch
    {
        "movement" => "MovementStep.Step",
        "weapon" => "WeaponStep.Step",
        "projectile" => "ProjectileStep.Step",
        "hitboxBuild" => "HitboxBuilder",
        "world" => "SimWorld.Step",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown step kind."),
    };
}
