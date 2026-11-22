using System.IO.Hashing;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Hashing;

/// <summary>
/// The state hash is XxHash64 with seed 0 over exactly the canonical state's bytes, one-shot and not
/// chained (FR-016, contracts/state-serialization.md#state-hash). Every caller uses
/// <see cref="StateHash.Compute"/>.
/// </summary>
public sealed class StateHashTests
{
    /// <summary>XXH64 of no bytes with seed 0, the algorithm's published empty-input value.</summary>
    private const ulong EmptyHash = 0xEF46DB3751D8E999UL;

    /// <summary>contracts/state-serialization.md#worked-example.</summary>
    private const ulong WorkedExampleHash = 0x35038da2f2d9459cUL;

    private const int OneMebibyte = 1024 * 1024;

    [Fact]
    public void HashIsXxHash64WithSeedZero()
    {
        var empty = StateHash.Compute(ReadOnlySpan<byte>.Empty);
        Assert.True(empty == EmptyHash, $"Compute of no bytes is {empty:x16}, expected {EmptyHash:x16}");

        var workedExample = TestStates.WorkedExample;
        var worked = StateHash.Compute(workedExample);
        Assert.True(
            worked == WorkedExampleHash,
            $"Compute of the {workedExample.Length}-byte worked example is {worked:x16}, expected {WorkedExampleHash:x16}");

        // Past the 32-byte stripes and every tail path: 1 MiB of (byte)(i * 31) against the library itself.
        var bytes = new byte[OneMebibyte];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(i * 31));
        }

        var large = StateHash.Compute(bytes);
        var reference = XxHash64.HashToUInt64(bytes, 0);
        Assert.True(large == reference, $"Compute of 1 MiB is {large:x16}, XxHash64 with seed 0 gives {reference:x16}");
    }
}
