using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Allocation;

/// <summary>
/// FR-032, US7.3, SC-005: after warm-up, <see cref="BatchStepper.StepQueued"/>, the batched entry point the
/// browser build calls (contracts/wasm-api.md#batchstepper: "After construction and the load calls,
/// <c>StepQueued</c> allocates nothing"), allocates zero bytes. The stepper owns its world, its command
/// buffer and its report buffer, and the report buffer is sized by <c>LoadConfig</c> from the configured
/// maxima, so a committed batch only decodes commands, steps the world and copies records into buffers it
/// already holds. Each row warms up with two batches, then counts
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> across three windows of at least 1,000 steps each
/// (T036's rule, <see cref="ZeroAllocation"/>), and requires the smallest window to be exactly 0 bytes.
/// </summary>
[Collection(ZeroAllocationCollection.Name)]
public sealed class BatchAllocationTests
{
    /// <summary>The host's command capacity (<c>SimCoreExports.CommandCapacity</c>).</summary>
    private const int HostCapacity = 32;

    /// <summary>Rows <c>(batchSize, hz)</c>: batch sizes 1 and 16, each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> BatchSizesAndRates => TestRates.WithRateLast(1, 16);

    /// <summary>
    /// Given a <c>new BatchStepper(32)</c> loaded with the encoded test world
    /// (<c>CollisionFile.Encode(TestWorldGenerator.Generate())</c>), the config block of
    /// <c>TestConfigs.At(hz)</c> and the initial state and match salt of the <c>flat-run-&lt;hz&gt;hz</c>
    /// definition (<c>ScenarioCatalog</c>: one character holding Forward along the flat run for one second,
    /// so <paramref name="hz"/> commands), each load call returning <c>Ok</c>; When the definition's
    /// commands are encoded into the stepper's command buffer with <see cref="InputCodec.TryEncode"/>,
    /// <paramref name="batchSize"/> at a time in order, and stepped with one
    /// <c>StepQueued(batchSize)</c> per batch, Reset to the definition's initial state (with the counter
    /// paused) whenever fewer than <paramref name="batchSize"/> of its commands remain; with two warm-up
    /// batches, then three counted windows, each running <c>StepQueued(batchSize)</c> calls until at least
    /// 1,000 steps have run (1,000 calls of 1, or 63 calls of 16); Then every call returns
    /// <paramref name="batchSize"/>, every Reset returns <c>Ok</c>, and the smallest window allocated
    /// exactly 0 bytes. The failure message names the batch size, the rate and every window's byte count.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizesAndRates))]
    public void StepQueuedAllocatesNothingAfterWarmUp(int batchSize, int hz)
    {
        var what = string.Create(CultureInfo.InvariantCulture, $"batches of {batchSize} at {hz} Hz");
        var definition = ScenarioCatalog.Get(string.Create(CultureInfo.InvariantCulture, $"flat-run-{hz}hz"));
        var stream = definition.Build(TestConfigs.Default(), TestWorlds.TestWorld, hz);
        Assert.True(
            stream.Steps.Count == hz,
            $"{what}: the flat-run-{hz}hz definition has {stream.Steps.Count} steps, not {hz}");
        var commands = new InputCommand[stream.Steps.Count];
        for (var k = 0; k < commands.Length; k++)
        {
            var entry = stream.Steps[k].Commands;
            Assert.True(entry.Length == 1, $"{what}: entry {k} has {entry.Length} commands, not 1");
            commands[k] = entry[0].Command;
        }

        var stepper = new BatchStepper(HostCapacity);
        AssertOk(stepper.LoadCollision(CollisionFile.Encode(TestWorldGenerator.Generate())), $"{what}: LoadCollision of the encoded test world");
        AssertOk(stepper.LoadConfig(ConfigBlock.Encode(TestConfigs.At(hz))), $"{what}: LoadConfig of TestConfigs.At({hz})");
        var driver = new Driver(stepper, commands, stream.InitialState.ToArray(), stream.MatchSalt, batchSize);
        driver.Reset();
        Assert.True(driver.LastReset == LoadResult.Ok, $"{what}: Reset to the flat-run-{hz}hz initial state returned {driver.LastReset}, not Ok");

        // Warm-up: two batches, so first-call work is not counted.
        for (var call = 0; call < ZeroAllocation.WarmUpCalls; call++)
        {
            if (driver.NeedsReset)
            {
                driver.Reset();
            }

            driver.Step();
        }

        // Three windows of at least 1,000 steps. Reset runs with the counter paused: it is a load call,
        // which may allocate; only StepQueued and the encoding of its commands are counted.
        var windows = new long[ZeroAllocation.Batches];
        var callsPerWindow = 0;
        for (var window = 0; window < windows.Length; window++)
        {
            long counted = 0;
            var steps = 0;
            var calls = 0;
            var mark = GC.GetAllocatedBytesForCurrentThread();
            while (steps < ZeroAllocation.CallsPerBatch)
            {
                if (driver.NeedsReset)
                {
                    counted += GC.GetAllocatedBytesForCurrentThread() - mark;
                    driver.Reset();
                    mark = GC.GetAllocatedBytesForCurrentThread();
                }

                driver.Step();
                steps += batchSize;
                calls++;
            }

            counted += GC.GetAllocatedBytesForCurrentThread() - mark;
            windows[window] = counted;
            callsPerWindow = calls;
        }

        Assert.True(
            driver.EncodeFailures == 0,
            $"{what}: InputCodec.TryEncode refused {driver.EncodeFailures} commands for the {stepper.CommandSegment.Count}-byte command buffer");
        Assert.True(
            driver.FailedResets == 0,
            $"{what}: {driver.FailedResets} of {driver.Resets} Resets did not return Ok; the last returned {driver.LastReset}");
        Assert.True(
            driver.WrongReturns == 0,
            $"{what}: {driver.WrongReturns} of {driver.Calls} StepQueued({batchSize}) calls did not return {batchSize}; the last such call returned {driver.LastWrongReturn}");
        Assert.True(
            windows.Min() == 0,
            $"{what}: StepQueued({batchSize}) allocated [{string.Join(", ", windows)}] bytes in {windows.Length} windows of {callsPerWindow} calls ({callsPerWindow * batchSize} steps) each, Resets not counted; the smallest window must be 0");
    }

    private static void AssertOk(LoadResult result, string what) =>
        Assert.True(result == LoadResult.Ok, $"{what} returned {result}, not Ok");

    /// <summary>
    /// Feeds the definition's commands to the stepper in order, <c>batchSize</c> per <c>StepQueued</c>, and
    /// records anything unexpected in counters rather than asserting, so the counted calls allocate nothing
    /// of their own.
    /// </summary>
    private sealed class Driver(BatchStepper stepper, InputCommand[] commands, byte[] initialState, ulong matchSalt, int batchSize)
    {
        private int _next;

        /// <summary>True when fewer than <c>batchSize</c> of the definition's commands remain.</summary>
        public bool NeedsReset => commands.Length - _next < batchSize;

        public int Calls { get; private set; }

        public int WrongReturns { get; private set; }

        public int LastWrongReturn { get; private set; }

        public int EncodeFailures { get; private set; }

        public int Resets { get; private set; }

        public int FailedResets { get; private set; }

        public LoadResult LastReset { get; private set; }

        /// <summary>Resets the stepper to the definition's initial state and match salt, and restarts at its first command.</summary>
        public void Reset()
        {
            LastReset = stepper.Reset(initialState, matchSalt);
            Resets++;
            if (LastReset != LoadResult.Ok)
            {
                FailedResets++;
            }

            _next = 0;
        }

        /// <summary>Encodes the next <c>batchSize</c> commands into the command buffer and steps them with one <c>StepQueued</c>.</summary>
        public void Step()
        {
            var buffer = stepper.CommandSegment.AsSpan();
            for (var i = 0; i < batchSize; i++)
            {
                if (!InputCodec.TryEncode(in commands[_next + i], buffer.Slice(i * InputCodec.EncodedSize, InputCodec.EncodedSize)))
                {
                    EncodeFailures++;
                }
            }

            var stepped = stepper.StepQueued(batchSize);
            Calls++;
            _next += batchSize;
            if (stepped != batchSize)
            {
                WrongReturns++;
                LastWrongReturn = stepped;
            }
        }
    }
}
