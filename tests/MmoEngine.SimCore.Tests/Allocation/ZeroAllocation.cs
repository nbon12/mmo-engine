namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// The measurement every zero-allocation test of this feature uses (FR-032, US7.3, SC-005): warm the call
/// up twice, then count <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three separate batches
/// of 1,000 calls, and require the smallest batch to be exactly 0 bytes.
/// </summary>
/// <remarks>
/// One counted window of 1,000 calls failed once in five full-suite runs during review, with 4,952 bytes
/// for a <c>SimWorld.Step</c> that allocated 0 in every rerun. A count that appears in one window of a
/// run and in no rerun is not the call's own: a per-call allocation, or a periodic one that recurs within
/// 1,000 calls, shows in every batch, so requiring one clean batch of 1,000 further calls keeps the test
/// strict while a one-off stray count lands in one batch only. The test stays exact: there is no byte
/// tolerance, and the failure message lists every batch's count.
/// </remarks>
internal static class ZeroAllocation
{
    /// <summary>The calls run before any count, so first-call work is not counted.</summary>
    public const int WarmUpCalls = 2;

    /// <summary>The calls in each counted batch.</summary>
    public const int CallsPerBatch = 1_000;

    /// <summary>The counted batches.</summary>
    public const int Batches = 3;

    /// <summary>Every call <see cref="MeasureBatches"/> makes: the warm-up and every batch.</summary>
    public const int TotalCalls = WarmUpCalls + (Batches * CallsPerBatch);

    /// <summary>
    /// Calls <paramref name="call"/> <see cref="WarmUpCalls"/> times with indices 0 and 1, then
    /// <see cref="Batches"/> times <see cref="CallsPerBatch"/> times with indices 0 to 999, and returns each
    /// batch's count of bytes allocated on this thread. The delegate is created by the caller before the
    /// first count, and invoking it allocates nothing, so every byte counted is the call's own.
    /// </summary>
    public static long[] MeasureBatches(Action<int> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var counts = new long[Batches];
        for (var i = 0; i < WarmUpCalls; i++)
        {
            call(i);
        }

        for (var b = 0; b < Batches; b++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < CallsPerBatch; i++)
            {
                call(i);
            }

            counts[b] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return counts;
    }

    /// <summary>
    /// Asserts that the smallest of <paramref name="counts"/> is exactly 0. The message starts with
    /// <paramref name="what"/>, which names the row and the call, and lists every batch's bytes.
    /// </summary>
    public static void AssertABatchAllocatedNothing(long[] counts, string what)
    {
        ArgumentNullException.ThrowIfNull(counts);
        Assert.True(
            counts.Min() == 0,
            $"{what} allocated [{string.Join(", ", counts)}] bytes in {counts.Length} batches of {CallsPerBatch} calls; the smallest batch must be 0");
    }
}
