namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// Runs the zero-allocation tests one at a time, after the parallel tests (FR-032). They measure with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, a per-thread counter: a test sharing its thread
/// pool with parallel tests could see their JIT, type loading or continuations land on its thread and be
/// counted. Every allocation test class of this feature is <c>[Collection(ZeroAllocationCollection.Name)]</c>,
/// so each entry point's allocation test can be written in the phase that builds the entry point. Each
/// measures with <see cref="ZeroAllocation"/>: the smallest of three counted batches of 1,000 calls must
/// be 0 bytes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ZeroAllocationCollection
{
    public const string Name = "Zero allocation (per-thread allocation counters)";
}
