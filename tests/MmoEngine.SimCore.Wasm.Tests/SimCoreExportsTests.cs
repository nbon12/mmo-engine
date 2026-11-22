using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Wasm.Tests.Support;

namespace MmoEngine.SimCore.Wasm.Tests;

/// <summary>
/// The WebAssembly host's exports (FR-024, FR-025, contracts/wasm-api.md#host-exports and #return-codes),
/// called as plain methods on x64 so that coverlet reports <c>SimCoreExports.cs</c> and <c>Program.cs</c>
/// (research R18, the host's coverage gate). Each export is a one-line delegation, so every test checks it
/// against the core call it delegates to: the shared <c>BatchStepper</c>'s return codes, buffers and
/// report, <c>StreamRunner.Run</c> and <c>CrossBuildTables.Evaluate</c>. Reports are read with
/// <see cref="BinaryPrimitives"/> at the contract's offsets (load calls: <c>u32</c> text length at 0 and
/// UTF-8 text at 4; <c>StepQueued</c>: <c>u32</c> record count at 0, <c>u32</c> records length L at 4,
/// the packed records at 8), and records as <c>u32 step | u32 stateLength | state | u64 hash</c>
/// (contracts/state-serialization.md#step-record). Every test that uses the static stepper starts with
/// its own load calls, so no test depends on what an earlier one left loaded.
/// </summary>
[SupportedOSPlatform("browser")]
[Collection(SharedStepperCollection.Name)]
public sealed class SimCoreExportsTests
{
    /// <summary><c>SimCoreExports.CommandCapacity</c>, which is not exported: JavaScript reads it as the command buffer's length / 17.</summary>
    private const int Capacity = 32;

    /// <summary>An input command's encoded size (<c>InputCodec.EncodedSize</c>).</summary>
    private const int CommandSize = 17;

    /// <summary>A step record's <c>step</c>, <c>stateLength</c> and <c>hash</c>.</summary>
    private const int RecordOverhead = 16;

    /// <summary>
    /// The report buffer after <c>LoadConfig</c> of the default block: max(65,536, 8 + 32 × 91,688 + 12 + 1,024),
    /// with 91,688 the placeholders' <c>MaxStepRecordLength</c> (contracts/wasm-api.md#report-buffer-layouts).
    /// </summary>
    private const int DefaultReportLength = 2_935_060;

    /// <summary>The cross-build tables' pose row count (contracts/wasm-api.md#cross-build-tables).</summary>
    private const uint PoseRows = 6192;

    /// <summary>The 64-bit golden-ratio constant, above <see cref="long.MaxValue"/>, so its signed form is negative.</summary>
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    /// <summary>flat-run-30hz's perturbation step, one of its step numbers (the agreement manifest's).</summary>
    private const int PerturbStep = 15;

    /// <summary>Rows of <see cref="RunStreamReturnsTheServerRecords"/>.</summary>
    public static TheoryData<string> RunStreamCases =>
    [
        "flat-run-30hz",
        "fuzz-all-buttons",
        "flat-run-30hz-perturbed",
        "flat-run-30hz-version-2",
    ];

    /// <summary>
    /// FR-025 and contracts/wasm-api.md#return-codes: the load exports return the stepper's
    /// <c>LoadResult</c> as an <c>int</c> and leave its report. Given flat-run-30hz's inputs: When
    /// <c>LoadCollision</c> gets the test world, Then it returns 0 and the report's text length is 0; When it
    /// gets the test world's first 10 bytes, Then it returns 1 and the report text is exactly the
    /// <see cref="CoreFormatException"/> message <c>CollisionFile.Decode</c> throws for them; When
    /// <c>LoadConfig</c> gets a 3-byte block, Then it returns 1 (with <c>ConfigBlock.Decode</c>'s message);
    /// When it gets flat-run-30hz's config block, Then it returns 0 (and the following <c>Reset</c> 0); When
    /// it gets that block re-encoded with <c>movement.gravity</c> NaN, Then it returns 2 with the text exactly
    /// <c>movement.gravity: must be finite</c>, and <c>Reset</c> then returns 3 with text length 0 although
    /// the collision world is loaded (fail closed); and When both loads return 0 again, Then <c>Reset</c>
    /// returns 0 and <c>Reset</c> of the initial state truncated by one byte returns 1.
    /// </summary>
    [Fact]
    public void LoadCallsReturnTheSteppersCodes()
    {
        var flatRun = CommittedStreamInputs.Load("flat-run-30hz");
        var salt = Signed(flatRun.MatchSalt);

        Assert.Equal(0, SimCoreExports.LoadCollision(flatRun.Collision));
        Assert.Equal(0u, ReportTextLength());

        var head = flatRun.Collision[..10];
        var collisionMessage = Assert.Throws<CoreFormatException>(() => CollisionFile.Decode(head)).Message;
        Assert.Equal(1, SimCoreExports.LoadCollision(head));
        Assert.Equal(collisionMessage, ReportText());

        byte[] threeBytes = [0x01, 0x00, 0x00];
        var blockMessage = Assert.Throws<CoreFormatException>(() => ConfigBlock.Decode(threeBytes)).Message;
        Assert.Equal(1, SimCoreExports.LoadConfig(threeBytes));
        Assert.Equal(blockMessage, ReportText());

        Assert.Equal(0, SimCoreExports.LoadCollision(flatRun.Collision));
        Assert.Equal(0, SimCoreExports.LoadConfig(flatRun.ConfigBlock));
        Assert.Equal(0u, ReportTextLength());
        Assert.Equal(0, SimCoreExports.Reset(flatRun.InitialState, salt));

        var config = ConfigBlock.Decode(flatRun.ConfigBlock);
        config.Movement!.Gravity = float.NaN;
        Assert.Equal(2, SimCoreExports.LoadConfig(ConfigBlock.Encode(config)));
        Assert.Equal("movement.gravity: must be finite", ReportText());

        Assert.Equal(3, SimCoreExports.Reset(flatRun.InitialState, salt));
        Assert.Equal(0u, ReportTextLength());

        Assert.Equal(0, SimCoreExports.LoadCollision(flatRun.Collision));
        Assert.Equal(0, SimCoreExports.LoadConfig(flatRun.ConfigBlock));
        Assert.Equal(0, SimCoreExports.Reset(flatRun.InitialState, salt));
        Assert.Equal(0u, ReportTextLength());
        Assert.Equal(1, SimCoreExports.Reset(flatRun.InitialState[..^1], salt));
    }

    /// <summary>
    /// contracts/wasm-api.md#buffer-lifetimes: Given the host, When <c>CommandBuffer()</c> is called twice,
    /// Then each segment is 544 bytes (32 × 17) at offset 0 of the same array; and When <c>LoadConfig</c> of
    /// the default block returns 0, Then <c>ReportBuffer()</c> is 2,935,060 bytes, and a second
    /// <c>LoadConfig</c> of the same block returns 0 and keeps the same array.
    /// </summary>
    [Fact]
    public void BuffersAreTheSteppersSegments()
    {
        var first = SimCoreExports.CommandBuffer();
        var second = SimCoreExports.CommandBuffer();
        Assert.Equal(Capacity * CommandSize, first.Count);
        Assert.Equal(0, first.Offset);
        Assert.NotNull(first.Array);
        Assert.Same(first.Array, second.Array);
        Assert.Equal(first.Count, second.Count);

        var block = DefaultConfig.Block();
        Assert.Equal(0, SimCoreExports.LoadConfig(block));
        var report = SimCoreExports.ReportBuffer();
        Assert.Equal(DefaultReportLength, report.Count);
        Assert.NotNull(report.Array);

        Assert.Equal(0, SimCoreExports.LoadConfig(block));
        var again = SimCoreExports.ReportBuffer();
        Assert.Same(report.Array, again.Array);
        Assert.Equal(DefaultReportLength, again.Count);
    }

    /// <summary>
    /// FR-025 and contracts/wasm-api.md#report-buffer-layouts: <c>StepQueued</c> returns the stepper's
    /// result. Given the host: When <c>StepQueued(-1)</c> or <c>StepQueued(33)</c> is called, Then it returns
    /// −2 (CountOutOfRange); When <c>LoadCollision</c> returned 0 and no <c>Reset</c> followed, Then
    /// <c>StepQueued(1)</c> returns −1 (NotReady); and When flat-run-30hz's config block, initial state and
    /// salt are loaded and its 30 commands are queued as batches of 1, 16 and 13, Then each call returns 1,
    /// 16 and 13, each report header's count equals that number and its length equals the byte length of
    /// the records that follow, and the concatenated records equal
    /// <c>StreamRunner.Run(flat-run-30hz, test-world)</c> byte for byte.
    /// </summary>
    [Fact]
    public void StepQueuedDelegatesToTheStepper()
    {
        Assert.Equal(-2, SimCoreExports.StepQueued(-1));
        Assert.Equal(-2, SimCoreExports.StepQueued(Capacity + 1));

        var flatRun = CommittedStreamInputs.Load("flat-run-30hz");
        Assert.Equal(0, SimCoreExports.LoadCollision(flatRun.Collision));
        Assert.Equal(-1, SimCoreExports.StepQueued(1));

        Assert.Equal(0, SimCoreExports.LoadConfig(flatRun.ConfigBlock));
        Assert.Equal(0, SimCoreExports.Reset(flatRun.InitialState, Signed(flatRun.MatchSalt)));
        var commands = Assert.IsType<byte[]>(flatRun.Commands);
        Assert.Equal(30 * CommandSize, commands.Length);

        var records = new List<byte>();
        var queued = 0;
        foreach (var batch in (int[])[1, 16, 13])
        {
            commands.AsSpan(queued * CommandSize, batch * CommandSize).CopyTo(SimCoreExports.CommandBuffer().AsSpan());
            Assert.Equal(batch, SimCoreExports.StepQueued(batch));
            records.AddRange(ReportRecords(batch, $"the batch of {batch} at command {queued}"));
            queued += batch;
        }

        Assert.Equal(flatRun.StepCount, queued);
        AssertSameRecords(StreamRunner.Run(flatRun.Stream, flatRun.Collision), [.. records], "batches of 1, 16 and 13 against StreamRunner.Run");
    }

    /// <summary>
    /// contracts/wasm-api.md#marshalling-rules: the 64-bit match salt crosses as a signed <c>BigInt</c> and
    /// <c>Reset</c> reinterprets it as <c>ulong</c>. Given rifle-burst's inputs: When <c>Reset</c> gets
    /// <c>unchecked((long)0x9E3779B97F4A7C15)</c>, a negative <c>long</c>, and the stream's 120 commands are
    /// queued 32 at a time, Then the records equal, byte for byte, those of a fresh
    /// <c>new BatchStepper(32)</c> reset with the <c>ulong</c> <c>0x9E3779B97F4A7C15</c>; When it gets the
    /// stream's own salt, Then they equal <c>StreamRunner.Run(rifle-burst)</c>; and When it gets that salt + 1,
    /// Then the first record that differs from the server's is at rifle-burst's first shot step (the first
    /// server record whose <c>events.shots.count</c> is non-zero), where the spread hash first reads the salt.
    /// </summary>
    [Fact]
    public void ResetReinterpretsTheSignedSaltAsUnsigned()
    {
        var rifle = CommittedStreamInputs.Load("rifle-burst");
        var commands = Assert.IsType<byte[]>(rifle.Commands);
        var signedGamma = unchecked((long)GoldenGamma);
        Assert.True(signedGamma < 0, $"unchecked((long)0x{GoldenGamma:X16}) is {signedGamma}, not negative");

        var viaExports = RunThroughExports(rifle, commands, signedGamma);
        var core = new BatchStepper(Capacity);
        Assert.Equal(LoadResult.Ok, core.LoadCollision(rifle.Collision));
        Assert.Equal(LoadResult.Ok, core.LoadConfig(rifle.ConfigBlock));
        Assert.Equal(LoadResult.Ok, core.Reset(rifle.InitialState, GoldenGamma));
        var viaCore = RunThroughStepper(core, commands);
        AssertSameRecords(viaCore, viaExports, "Reset(0x9E3779B97F4A7C15 as a signed long) against a BatchStepper reset with the ulong");

        var server = StreamRunner.Run(rifle.Stream, rifle.Collision);
        AssertSameRecords(server, RunThroughExports(rifle, commands, Signed(rifle.MatchSalt)), "Reset(rifle-burst's own salt) against StreamRunner.Run");

        var firstShot = FirstShotStep(server);
        var plusOne = RunThroughExports(rifle, commands, Signed(unchecked(rifle.MatchSalt + 1)));
        Assert.Equal((uint?)firstShot, FirstDifferingStep(server, plusOne));
    }

    /// <summary>
    /// US2.5 in the host and contracts/wasm-api.md#streamrunner: <c>RunStream</c> returns
    /// <c>StreamRunner.Run</c>'s records. Rows: <c>flat-run-30hz</c> and <c>fuzz-all-buttons</c>, where
    /// <c>RunStream(stream, test-world, -1)</c> equals <c>StreamRunner.Run(stream, test-world)</c>;
    /// <c>flat-run-30hz-perturbed</c>, where <c>RunStream(flat-run-30hz, test-world, 15)</c> equals
    /// <c>StreamRunner.Run(…, perturbStep: 15)</c> and differs from the unperturbed records; and
    /// <c>flat-run-30hz-version-2</c>, a copy with its version byte set to 2, where <c>RunStream</c> throws a
    /// <see cref="CoreFormatException"/> whose message contains
    /// <c>unsupported version 2; this reader supports version 1</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RunStreamCases))]
    public void RunStreamReturnsTheServerRecords(string caseName)
    {
        switch (caseName)
        {
            case "flat-run-30hz":
            case "fuzz-all-buttons":
                {
                    var inputs = CommittedStreamInputs.Load(caseName);
                    AssertSameRecords(StreamRunner.Run(inputs.Stream, inputs.Collision), SimCoreExports.RunStream(inputs.Stream, inputs.Collision, -1), $"{caseName}: RunStream against StreamRunner.Run");
                    break;
                }

            case "flat-run-30hz-perturbed":
                {
                    var flatRun = CommittedStreamInputs.Load("flat-run-30hz");
                    var perturbed = SimCoreExports.RunStream(flatRun.Stream, flatRun.Collision, PerturbStep);
                    AssertSameRecords(StreamRunner.Run(flatRun.Stream, flatRun.Collision, perturbStep: PerturbStep), perturbed, "RunStream(flat-run-30hz, 15) against StreamRunner.Run(perturbStep: 15)");
                    Assert.Equal((uint?)PerturbStep, FirstDifferingStep(StreamRunner.Run(flatRun.Stream, flatRun.Collision), perturbed));
                    break;
                }

            case "flat-run-30hz-version-2":
                {
                    var flatRun = CommittedStreamInputs.Load("flat-run-30hz");
                    var versionTwo = (byte[])flatRun.Stream.Clone();
                    BinaryPrimitives.WriteUInt16LittleEndian(versionTwo.AsSpan(4, 2), 2);
                    var refused = Assert.Throws<CoreFormatException>(() => SimCoreExports.RunStream(versionTwo, flatRun.Collision, -1));
                    Assert.Contains("unsupported version 2; this reader supports version 1", refused.Message, StringComparison.Ordinal);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of RunStreamReturnsTheServerRecords.");
        }
    }

    /// <summary>
    /// US3.8, SC-004 and US5.5 through the host (contracts/wasm-api.md#cross-build-tables): Given
    /// <c>CrossBuildTables.BuildInputs()</c> and the default block, When <c>EvaluateTables</c> is called, Then
    /// it equals <c>CrossBuildTables.Evaluate</c> of the same arguments byte for byte, its <c>MMXR</c> header
    /// carries the inputs' math row count M and P = 6,192 pose rows, and its length is 16 + 8·M + 100·P; and
    /// When the inputs' magic is <c>MMXX</c>, Then it throws a <see cref="CoreFormatException"/> whose
    /// message starts <c>tables magic at offset 0:</c>.
    /// </summary>
    [Fact]
    public void EvaluateTablesReturnsTheServerResults()
    {
        var inputs = CrossBuildTables.BuildInputs();
        var block = DefaultConfig.Block();

        var results = SimCoreExports.EvaluateTables(inputs, block);
        Assert.Equal(CrossBuildTables.Evaluate(inputs, block), results);
        Assert.Equal("MMXR", Encoding.ASCII.GetString(results, 0, 4));
        var mathRows = BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(8, 4));
        var poseRows = BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(12, 4));
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(8, 4)), mathRows);
        Assert.Equal(PoseRows, poseRows);
        Assert.Equal(16L + (8L * mathRows) + (100L * poseRows), results.LongLength);

        var wrongMagic = (byte[])inputs.Clone();
        "MMXX"u8.CopyTo(wrongMagic);
        var refused = Assert.Throws<CoreFormatException>(() => SimCoreExports.EvaluateTables(wrongMagic, block));
        Assert.StartsWith("tables magic at offset 0:", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>contracts/wasm-api.md#host-exports: the host's entry point, which the runners never call, returns 0.</summary>
    [Fact]
    public void EntryPointReturnsZero() => Assert.Equal(0, Program.Main());

    /// <summary>A <c>u64</c> salt as the signed <c>long</c> JavaScript passes (<c>BigInt.asIntN(64, salt)</c>).</summary>
    private static long Signed(ulong salt) => unchecked((long)salt);

    /// <summary>The <c>u32</c> text length at report offset 0 after a load call.</summary>
    private static uint ReportTextLength() =>
        BinaryPrimitives.ReadUInt32LittleEndian(SimCoreExports.ReportBuffer().AsSpan(0, 4));

    /// <summary>The UTF-8 text at report offset 4, of the length at offset 0.</summary>
    private static string ReportText()
    {
        var report = SimCoreExports.ReportBuffer().AsSpan();
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(report);
        return Encoding.UTF8.GetString(report.Slice(4, length));
    }

    /// <summary>
    /// The records of a <c>StepQueued</c> that returned <paramref name="expected"/>: the header's count must
    /// equal it, and its length must equal the byte length of that many records walked from offset 8.
    /// </summary>
    private static byte[] ReportRecords(int expected, string context)
    {
        var report = SimCoreExports.ReportBuffer().AsSpan();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(report);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(report[4..]);
        Assert.True(count == (uint)expected, $"{context}: the report's record count is {count}, not {expected}");

        var walked = 0;
        for (var i = 0; i < expected; i++)
        {
            var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(report[(8 + walked + 4)..]);
            walked += RecordOverhead + (int)stateLength;
        }

        Assert.True(length == (uint)walked, $"{context}: the report's records length is {length}; its {expected} records are {walked} bytes");
        return report.Slice(8, walked).ToArray();
    }

    /// <summary>Loads the host's stepper with the stream's inputs and <paramref name="salt"/>, then queues every command 32 at a time.</summary>
    private static byte[] RunThroughExports(CommittedStreamInputs inputs, byte[] commands, long salt)
    {
        Assert.Equal(0, SimCoreExports.LoadCollision(inputs.Collision));
        Assert.Equal(0, SimCoreExports.LoadConfig(inputs.ConfigBlock));
        Assert.Equal(0, SimCoreExports.Reset(inputs.InitialState, salt));
        var records = new List<byte>();
        for (var start = 0; start < commands.Length / CommandSize; start += Capacity)
        {
            var batch = System.Math.Min(Capacity, (commands.Length / CommandSize) - start);
            commands.AsSpan(start * CommandSize, batch * CommandSize).CopyTo(SimCoreExports.CommandBuffer().AsSpan());
            Assert.Equal(batch, SimCoreExports.StepQueued(batch));
            records.AddRange(ReportRecords(batch, string.Create(CultureInfo.InvariantCulture, $"the exports' batch at command {start}")));
        }

        return [.. records];
    }

    /// <summary>Queues every command on a loaded core <see cref="BatchStepper"/> 32 at a time and returns the records.</summary>
    private static byte[] RunThroughStepper(BatchStepper stepper, byte[] commands)
    {
        var records = new List<byte>();
        for (var start = 0; start < commands.Length / CommandSize; start += Capacity)
        {
            var batch = System.Math.Min(Capacity, (commands.Length / CommandSize) - start);
            commands.AsSpan(start * CommandSize, batch * CommandSize).CopyTo(stepper.CommandSegment.AsSpan());
            Assert.Equal(batch, stepper.StepQueued(batch));
            var report = stepper.ReportSegment.AsSpan();
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(report[4..]);
            records.AddRange(report.Slice(8, length).ToArray());
        }

        return [.. records];
    }

    /// <summary>The packed records, each as its step, state bytes and hash.</summary>
    private static List<(uint Step, byte[] State, ulong Hash)> Records(byte[] records)
    {
        var list = new List<(uint Step, byte[] State, ulong Hash)>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(records.Length - offset >= RecordOverhead, $"a partial record at offset {offset}");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset));
            var stateLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            Assert.True(records.Length - offset >= RecordOverhead + stateLength, $"a partial record at offset {offset}");
            var state = records.AsSpan(offset + 8, stateLength).ToArray();
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset + 8 + stateLength));
            list.Add((step, state, hash));
            offset += RecordOverhead + stateLength;
        }

        return list;
    }

    /// <summary>
    /// The step of the first record whose step number, state bytes or hash differs, or that one side lacks
    /// (named by the step of the record that is there); null when both are equal.
    /// </summary>
    private static uint? FirstDifferingStep(byte[] expected, byte[] actual)
    {
        var left = Records(expected);
        var right = Records(actual);
        for (var i = 0; i < System.Math.Max(left.Count, right.Count); i++)
        {
            if (i >= left.Count || i >= right.Count)
            {
                return i < left.Count ? left[i].Step : right[i].Step;
            }

            if (left[i].Step != right[i].Step || left[i].Hash != right[i].Hash || !left[i].State.AsSpan().SequenceEqual(right[i].State))
            {
                return left[i].Step;
            }
        }

        return null;
    }

    /// <summary>
    /// The step of the first record whose state has a non-zero <c>events.shots.count</c>, the <c>u16</c> after
    /// the header (12 bytes), the characters (52 + 30·<c>weaponCount</c> bytes each) and the projectiles (49
    /// bytes each) (contracts/state-serialization.md#layout).
    /// </summary>
    private static uint FirstShotStep(byte[] records)
    {
        foreach (var (step, state, _) in Records(records))
        {
            var characters = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(4));
            var projectiles = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(6));
            var offset = 12;
            for (var c = 0; c < characters; c++)
            {
                offset += 52 + (30 * state[offset + 51]);
            }

            offset += 49 * projectiles;
            if (BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(offset)) != 0)
            {
                return step;
            }
        }

        Assert.Fail("rifle-burst's server records hold no shot");
        return 0;
    }

    /// <summary>Asserts byte equality, naming the first differing step rather than printing megabytes.</summary>
    private static void AssertSameRecords(byte[] expected, byte[] actual, string context)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{context}: {actual.Length} bytes against {expected.Length}; the first differing record is step {FirstDifferingStep(expected, actual)?.ToString(CultureInfo.InvariantCulture) ?? "none (a partial record)"}"));
        }
    }
}
