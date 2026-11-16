using System;
using System.IO.Hashing;

namespace MmoEngine.SimCore.Hashing;

/// <summary>
/// The state hash (FR-016, specs/015-shared-simulation-core/contracts/state-serialization.md#state-hash):
/// XxHash64 with seed 0 over exactly the canonical state's bytes, one-shot and not chained. It is the one
/// function every caller uses, and it allocates nothing.
/// </summary>
public static class StateHash
{
    /// <summary>XxHash64 (seed 0) of <paramref name="canonicalState"/>.</summary>
    public static ulong Compute(ReadOnlySpan<byte> canonicalState) => XxHash64.HashToUInt64(canonicalState, 0);
}
