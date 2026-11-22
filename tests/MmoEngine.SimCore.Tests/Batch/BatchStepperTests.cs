using System.Buffers.Binary;
using System.Text;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Batch;

/// <summary>
/// <see cref="BatchStepper"/>, the batched stepper the browser build calls (FR-025,
/// contracts/wasm-api.md#batchstepper, #report-buffer-layouts and #return-codes), and its browser
/// configuration guard (FR-029, contracts/config-rules.md#browser-guard). The tests drive it through its
/// byte buffers exactly as JavaScript does: 17-byte input commands written into
/// <see cref="BatchStepper.CommandSegment"/>, and the report read from <see cref="BatchStepper.ReportSegment"/>
/// with <see cref="BinaryPrimitives"/> at the contract's offsets (load calls: <c>u32</c> text length at 0,
/// UTF-8 text at 4; <c>StepQueued</c>: <c>u32</c> record count at 0, <c>u32</c> records length L at 4, the
/// packed step records at 8, and a stop entry at 8 + L after an early stop).
/// </summary>
public sealed class BatchStepperTests
{
    /// <summary>The host's command capacity (<c>SimCoreExports.CommandCapacity</c>).</summary>
    private const int HostCapacity = 32;

    /// <summary>The batchable stream's step count: three host batches.</summary>
    private const int StreamSteps = 96;

    private const int CommandSize = InputCodec.EncodedSize;

    /// <summary>The report header: <c>u32</c> record count and <c>u32</c> records length.</summary>
    private const int ReportHeaderLength = 8;

    /// <summary>A step record's <c>step</c>, <c>stateLength</c> and <c>hash</c>.</summary>
    private const int RecordOverhead = 16;

    /// <summary>A stop entry's <c>i32</c> code, <c>u32</c> step and <c>u32</c> text length.</summary>
    private const int StopEntryHeadLength = 12;

    /// <summary>The longest stop-entry text.</summary>
    private const int MaxStopText = 1024;

    /// <summary>The report buffer a new stepper allocates, and the least <c>LoadConfig</c> sizes it to.</summary>
    private const int ReportMinimum = 65536;

    /// <summary><c>CompiledConfig.MaxStepRecordLength</c> of <c>simcore/config/default.json</c>.</summary>
    private const int DefaultMaxStepRecordLength = 91688;

    private const ulong Salt = 0x0123456789abcdefUL;

    /// <summary>The field the fault test makes non-finite.</summary>
    private const string VelocityYPath = "characters[0].velocity.y";

    /// <summary>A quiet NaN's bits.</summary>
    private const uint NaNBits = 0x7FC00000u;

    /// <summary>A byte the tests write over report bytes before a call, so a zero the call must write is not the buffer's own.</summary>
    private const byte Scribble = 0xAA;

    /// <summary>Rows (batch size, hz): {1, 2, 7, 32} crossed with 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, int> BatchSizesAndRates => TestRates.WithRateLast(1, 2, 7, 32);

    /// <summary>Rows of <see cref="LoadCallsReturnTheirCodes"/>.</summary>
    public static TheoryData<string> LoadCases =>
    [
        "collision-ok",
        "collision-refused",
        "config-ok",
        "config-malformed",
        "config-invalid",
        "reset-not-ready",
        "reset-refused",
        "load-discards-world",
    ];

    /// <summary>Rows of <see cref="StepQueuedRefusesBadCalls"/>.</summary>
    public static TheoryData<string> BadCallCases =>
    [
        "no-reset",
        "count-negative",
        "count-above-capacity",
        "count-checked-before-world",
        "count-zero",
    ];

    /// <summary>
    /// US2.6: Given a batchable stream (one character on <c>simcore/content/test-world.mmcd</c> with
    /// <c>TestConfigs.At(hz)</c>, 96 steps of one command for character index 0, every 16th command
    /// invalid), When stepper A (<c>new BatchStepper(32)</c>, loaded with the collision file, the stream's
    /// config block and its initial state and match salt) is given the 17-byte commands
    /// <paramref name="batchSize"/> at a time with one <c>StepQueued(batchSize)</c> per batch (the last batch
    /// shorter) and stepper B is driven the same way one command per call, Then every call returns the number
    /// of commands queued, A's concatenated records (read from the report header) equal B's byte for byte,
    /// and both equal <c>StreamRunner.Run(stream, collision)</c> byte for byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(BatchSizesAndRates))]
    public void QueuedCommandsEqualOneAtATime(int batchSize, int hz)
    {
        var stream = BatchableStream.Create(hz);
        var a = Loaded(stream);
        var b = Loaded(stream);
        var fromA = new List<byte>();
        var fromB = new List<byte>();

        for (var start = 0; start < StreamSteps; start += batchSize)
        {
            var count = System.Math.Min(batchSize, StreamSteps - start);
            Queue(a, stream.Commands.AsSpan(start, count));
            var stepped = a.StepQueued(count);
            Assert.True(stepped == count, $"{hz} Hz, batches of {batchSize}: the batch at command {start} returned {stepped}, not {count}");
            fromA.AddRange(ReportRecords(a, count, $"{hz} Hz, batches of {batchSize}, batch at command {start}"));
        }

        for (var k = 0; k < StreamSteps; k++)
        {
            Queue(b, stream.Commands.AsSpan(k, 1));
            var stepped = b.StepQueued(1);
            Assert.True(stepped == 1, $"{hz} Hz, one at a time: command {k} returned {stepped}, not 1");
            fromB.AddRange(ReportRecords(b, 1, $"{hz} Hz, one at a time, command {k}"));
        }

        var runner = StreamRunner.Run(stream.Stream, stream.Collision);
        AssertSameBytes(fromB.ToArray(), fromA.ToArray(), $"{hz} Hz: batches of {batchSize} against one command per call");
        AssertSameBytes(runner, fromA.ToArray(), $"{hz} Hz: batches of {batchSize} against StreamRunner.Run");
    }

    /// <summary>
    /// FR-029 browser guard and the load calls' report (contracts/config-rules.md#browser-guard,
    /// contracts/wasm-api.md#report-buffer-layouts and #return-codes). After each call the report holds a
    /// <c>u32</c> text length at 0 and UTF-8 text at 4:
    /// <c>collision-ok</c> is <c>Ok</c> with length 0; <c>collision-refused</c> (byte 0 changed) is
    /// <c>FormatRefused</c> with <c>CollisionFile.Decode</c>'s message; <c>config-ok</c> is <c>Ok</c> with
    /// length 0; <c>config-malformed</c> (an entry count above the block's length) is <c>FormatRefused</c> with
    /// <c>ConfigBlock.Decode</c>'s message; <c>config-invalid</c> (<c>movement.gravity</c> NaN and
    /// <c>movement.runSpeed</c> −1) is <c>ConfigInvalid</c> with the guard's <c>path: message</c> lines in
    /// rule-table order, joined by <c>\n</c>; <c>reset-not-ready</c> (neither loaded, only the collision, only
    /// the configuration, or after a failed <c>LoadConfig</c> that followed an <c>Ok</c> one) is
    /// <c>NotReady</c> with length 0; <c>reset-refused</c> (<c>characters[0].weaponCount</c> 3) is
    /// <c>FormatRefused</c> with <c>SimWorld.Reset</c>'s message and leaves no world; and
    /// <c>load-discards-world</c>: after an <c>Ok</c> Reset, an <c>Ok</c> load call makes
    /// <c>StepQueued(1)</c> return −1.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoadCases))]
    public void LoadCallsReturnTheirCodes(string caseName)
    {
        var stream = BatchableStream.Create(30);
        var defaultBlock = ConfigBlock.Encode(TestConfigs.Default());

        switch (caseName)
        {
            case "collision-ok":
                {
                    var stepper = new BatchStepper(HostCapacity);
                    ScribbleReport(stepper);
                    AssertLoad(LoadResult.Ok, 0, stepper.LoadCollision(stream.Collision), "LoadCollision of the test world");
                    AssertReportText(stepper, string.Empty, "LoadCollision of the test world");
                    break;
                }

            case "collision-refused":
                {
                    var corrupted = (byte[])stream.Collision.Clone();
                    corrupted[0] ^= 0xFF;
                    var expected = Assert.Throws<CoreFormatException>(() => CollisionFile.Decode(corrupted)).Message;
                    var stepper = new BatchStepper(HostCapacity);
                    AssertLoad(LoadResult.FormatRefused, 1, stepper.LoadCollision(corrupted), "LoadCollision with byte 0 changed");
                    AssertReportText(stepper, expected, "LoadCollision with byte 0 changed");
                    break;
                }

            case "config-ok":
                {
                    var stepper = new BatchStepper(HostCapacity);
                    ScribbleReport(stepper);
                    AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(defaultBlock), "LoadConfig of default.json");
                    AssertReportText(stepper, string.Empty, "LoadConfig of default.json");
                    break;
                }

            case "config-malformed":
                {
                    var malformed = MalformedBlock(defaultBlock);
                    var expected = Assert.Throws<CoreFormatException>(() => ConfigBlock.Decode(malformed)).Message;
                    Assert.StartsWith("config count at offset ", expected, StringComparison.Ordinal);
                    var stepper = new BatchStepper(HostCapacity);
                    AssertLoad(LoadResult.FormatRefused, 1, stepper.LoadConfig(malformed), "LoadConfig of a block whose count exceeds its length");
                    AssertReportText(stepper, expected, "LoadConfig of a block whose count exceeds its length");
                    break;
                }

            case "config-invalid":
                {
                    var invalid = TestConfigs.With(config =>
                    {
                        config.Movement!.Gravity = float.NaN;
                        config.Movement.RunSpeed = -1f;
                    });
                    var expected = string.Join("\n", ConfigGuard.Check(invalid).Select(error => $"{error.Path}: {error.Message}"));
                    Assert.Equal("movement.runSpeed: must be between 0.1 and 20\nmovement.gravity: must be finite", expected);
                    var stepper = new BatchStepper(HostCapacity);
                    AssertLoad(LoadResult.ConfigInvalid, 2, stepper.LoadConfig(ConfigBlock.Encode(invalid)), "LoadConfig with gravity NaN and runSpeed -1");
                    AssertReportText(stepper, expected, "LoadConfig with gravity NaN and runSpeed -1");
                    break;
                }

            case "reset-not-ready":
                {
                    var neither = new BatchStepper(HostCapacity);
                    ScribbleReport(neither);
                    AssertLoad(LoadResult.NotReady, 3, neither.Reset(stream.InitialState, stream.MatchSalt), "Reset with nothing loaded");
                    AssertReportText(neither, string.Empty, "Reset with nothing loaded");

                    var collisionOnly = new BatchStepper(HostCapacity);
                    AssertLoad(LoadResult.Ok, 0, collisionOnly.LoadCollision(stream.Collision), "LoadCollision");
                    ScribbleReport(collisionOnly);
                    AssertLoad(LoadResult.NotReady, 3, collisionOnly.Reset(stream.InitialState, stream.MatchSalt), "Reset with only the collision loaded");
                    AssertReportText(collisionOnly, string.Empty, "Reset with only the collision loaded");

                    var configOnly = new BatchStepper(HostCapacity);
                    AssertLoad(LoadResult.Ok, 0, configOnly.LoadConfig(stream.ConfigBlock), "LoadConfig");
                    ScribbleReport(configOnly);
                    AssertLoad(LoadResult.NotReady, 3, configOnly.Reset(stream.InitialState, stream.MatchSalt), "Reset with only the configuration loaded");
                    AssertReportText(configOnly, string.Empty, "Reset with only the configuration loaded");

                    var unloaded = Loaded(stream);
                    AssertLoad(LoadResult.FormatRefused, 1, unloaded.LoadConfig(MalformedBlock(stream.ConfigBlock)), "a failed LoadConfig after an Ok one");
                    AssertLoad(LoadResult.NotReady, 3, unloaded.Reset(stream.InitialState, stream.MatchSalt), "Reset after a failed LoadConfig that followed an Ok one");
                    AssertReportText(unloaded, string.Empty, "Reset after a failed LoadConfig that followed an Ok one");
                    break;
                }

            case "reset-refused":
                {
                    var start = TestWorldGenerator.Features.FlatRunStart;
                    var threeWeapons = TestStates.Build(
                        0,
                        [
                            TestStates.StandingCharacter(1, start.X, start.Z) with
                        {
                            Position = (start.X, start.Y, start.Z),
                            Weapons = [TestStates.Rifle, TestStates.Launcher, TestStates.Rifle],
                        },
                        ]);
                    var world = new SimWorld(CompiledConfig.Create(TestConfigs.At(30)), CollisionFile.Decode(stream.Collision));
                    var expected = Assert.Throws<CoreFormatException>(() => world.Reset(threeWeapons, stream.MatchSalt)).Message;

                    var stepper = Loaded(stream);
                    AssertLoad(LoadResult.FormatRefused, 1, stepper.Reset(threeWeapons, stream.MatchSalt), "Reset with weaponCount 3");
                    AssertReportText(stepper, expected, "Reset with weaponCount 3");
                    Queue(stepper, stream.Commands.AsSpan(0, 1));
                    var stepped = stepper.StepQueued(1);
                    Assert.True(stepped == (int)StepError.NotReady, $"StepQueued(1) after a refused Reset returned {stepped}, not -1 (NotReady)");
                    break;
                }

            case "load-discards-world":
                {
                    var stepper = Loaded(stream);
                    Queue(stepper, stream.Commands.AsSpan(0, 1));
                    Assert.True(stepper.StepQueued(1) == 1, "StepQueued(1) after an Ok Reset did not step");

                    AssertLoad(LoadResult.Ok, 0, stepper.LoadCollision(stream.Collision), "LoadCollision after an Ok Reset");
                    Queue(stepper, stream.Commands.AsSpan(0, 1));
                    var afterCollision = stepper.StepQueued(1);
                    Assert.True(afterCollision == -1, $"StepQueued(1) after LoadCollision returned {afterCollision}, not -1 (NotReady)");

                    AssertLoad(LoadResult.Ok, 0, stepper.Reset(stream.InitialState, stream.MatchSalt), "Reset after LoadCollision");
                    AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(stream.ConfigBlock), "LoadConfig after an Ok Reset");
                    Queue(stepper, stream.Commands.AsSpan(0, 1));
                    var afterConfig = stepper.StepQueued(1);
                    Assert.True(afterConfig == -1, $"StepQueued(1) after LoadConfig returned {afterConfig}, not -1 (NotReady)");
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of LoadCallsReturnTheirCodes.");
        }
    }

    /// <summary>
    /// contracts/wasm-api.md#return-codes: <c>StepQueued</c> with no Reset returns −1
    /// (<see cref="StepError.NotReady"/>); a count of −1 or <c>CommandCapacity</c> + 1 returns −2
    /// (<see cref="StepError.CountOutOfRange"/>), checked before the world; in each of these the report's bytes
    /// 0 to 7 are all zero and no step runs. A count of 0 returns 0, with record count 0 and length 0.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadCallCases))]
    public void StepQueuedRefusesBadCalls(string caseName)
    {
        var stream = BatchableStream.Create(30);
        BatchStepper stepper;
        int count;
        int expected;
        switch (caseName)
        {
            case "no-reset":
                stepper = new BatchStepper(HostCapacity);
                AssertLoad(LoadResult.Ok, 0, stepper.LoadCollision(stream.Collision), "LoadCollision");
                AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(stream.ConfigBlock), "LoadConfig");
                (count, expected) = (1, -1);
                Assert.True((int)StepError.NotReady == -1, $"StepError.NotReady is {(int)StepError.NotReady}, not -1");
                break;
            case "count-negative":
                stepper = Loaded(stream);
                (count, expected) = (-1, -2);
                break;
            case "count-above-capacity":
                stepper = Loaded(stream);
                (count, expected) = (stepper.CommandCapacity + 1, -2);
                break;
            case "count-checked-before-world":
                stepper = new BatchStepper(HostCapacity);
                (count, expected) = (HostCapacity + 1, -2);
                break;
            case "count-zero":
                stepper = Loaded(stream);
                (count, expected) = (0, 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of StepQueuedRefusesBadCalls.");
        }

        Assert.True((int)StepError.CountOutOfRange == -2, $"StepError.CountOutOfRange is {(int)StepError.CountOutOfRange}, not -2");
        Queue(stepper, stream.Commands.AsSpan(0, 1));
        ScribbleReport(stepper);

        var result = stepper.StepQueued(count);

        Assert.True(result == expected, $"{caseName}: StepQueued({count}) returned {result}, not {expected}");
        var header = stepper.ReportSegment.AsSpan(0, ReportHeaderLength);
        Assert.True(
            header.IndexOfAnyExcept((byte)0) < 0,
            $"{caseName}: report bytes 0 to 7 are {Convert.ToHexString(header)}, not all zero");

        // No step ran: with a world, the next one-command batch is still the first step.
        if (caseName != "no-reset" && caseName != "count-checked-before-world")
        {
            Queue(stepper, stream.Commands.AsSpan(0, 1));
            Assert.True(stepper.StepQueued(1) == 1, $"{caseName}: StepQueued(1) afterwards did not step");
            var record = ReportRecords(stepper, 1, $"{caseName}: the step afterwards");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(record);
            Assert.True(step == 1, $"{caseName}: the step afterwards is {step}, not 1; StepQueued({count}) ran a step");
        }
    }

    /// <summary>
    /// contracts/wasm-api.md#core-api: a command capacity outside [1, 256] throws
    /// <see cref="ArgumentOutOfRangeException"/>; the nearest capacity inside constructs, with a
    /// <c>capacity × 17</c>-byte command buffer.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void ConstructorRefusesACapacityOutsideOneTo256(int capacity)
    {
        BatchStepper? stepper = null;

        Assert.Throws<ArgumentOutOfRangeException>(() => stepper = new BatchStepper(capacity));

        Assert.Null(stepper);
        var inside = capacity < 1 ? 1 : 256;
        var accepted = new BatchStepper(inside);
        Assert.Equal(inside, accepted.CommandCapacity);
        Assert.Equal(inside * CommandSize, accepted.CommandSegment.Count);
    }

    /// <summary>
    /// contracts/wasm-api.md#batchstepper step 3.3 and #report-buffer-layouts (FR-017): Given five queued
    /// commands, When <c>StepQueuedWithInjectedBits(count: 5, commandIndex: 3, …)</c> writes NaN bits
    /// (0x7FC00000) into <c>characters[0].velocity.y</c> at the 4th step, Then it returns 3, the header holds
    /// count 3 and the 3 records' length, the records equal the first 3 of a one-at-a-time run, and the stop
    /// entry at 8 + L holds i32 −4, the 4th step's number and the text
    /// <c>NonFinite at characters[0].velocity.y</c>; and a following <c>StepQueued(1)</c> with the 4th command
    /// returns 1 with the one-at-a-time run's 4th record, so the faulting step committed nothing.
    /// </summary>
    [Fact]
    public void StopsAtTheFirstFaultAndKeepsEarlierRecords()
    {
        var stream = BatchableStream.Create(30);
        var oneAtATime = OneAtATime(stream, 4);
        Assert.True(
            StateSerializer.TryFindField(VelocityYPath, out var ordinal, out var index),
            $"StateSerializer.TryFindField does not know {VelocityYPath}");
        Assert.Equal(0, index);
        var stepper = Loaded(stream);
        Queue(stepper, stream.Commands.AsSpan(0, 5));

        var stepped = stepper.StepQueuedWithInjectedBits(count: 5, commandIndex: 3, fieldOrdinal: ordinal, elementIndex: 0, bits: NaNBits);

        Assert.True(stepped == 3, $"StepQueuedWithInjectedBits returned {stepped}, not 3");
        var report = stepper.ReportSegment.AsSpan();
        var records = ReportRecords(stepper, 3, "the faulted batch");
        var expectedRecords = oneAtATime[0].Concat(oneAtATime[1]).Concat(oneAtATime[2]).ToArray();
        AssertSameBytes(expectedRecords, records, "the faulted batch's 3 records against the one-at-a-time run's first 3");

        var stop = report[(ReportHeaderLength + records.Length)..];
        var code = BinaryPrimitives.ReadInt32LittleEndian(stop);
        var step = BinaryPrimitives.ReadUInt32LittleEndian(stop[4..]);
        Assert.True(code == -4 && code == (int)StepError.StepFault, $"the stop entry's code is {code}, not -4 (StepFault)");
        Assert.True(step == stream.StepNumber(3), $"the stop entry names step {step}, not the 4th step {stream.StepNumber(3)}");
        Assert.Equal("NonFinite at characters[0].velocity.y", StopText(stop));

        Queue(stepper, stream.Commands.AsSpan(3, 1));
        var resumed = stepper.StepQueued(1);
        Assert.True(resumed == 1, $"StepQueued(1) with the 4th command returned {resumed}, not 1");
        AssertSameBytes(oneAtATime[3], ReportRecords(stepper, 1, "the 4th command after the fault"), "the 4th step after the fault against the one-at-a-time run's 4th record");
    }

    /// <summary>
    /// contracts/wasm-api.md#batchstepper step 3.1: Given four queued commands of which command 2 has byte 0
    /// set to 0x4A, When <c>StepQueued(4)</c> runs, Then it returns 2 with the one-at-a-time run's first 2
    /// records, and the stop entry holds code −3, the 3rd step's number and
    /// <c>input formatId at offset 0: format identifier 0x4A is not 0x49</c>; and the world stays at the 2nd
    /// step's state: a following <c>StepQueued(1)</c> with the valid 3rd command gives the one-at-a-time run's
    /// 3rd record.
    /// </summary>
    [Fact]
    public void StopsAtAnUndecodableCommand()
    {
        var stream = BatchableStream.Create(30);
        var oneAtATime = OneAtATime(stream, 3);
        var stepper = Loaded(stream);
        Queue(stepper, stream.Commands.AsSpan(0, 4));
        stepper.CommandSegment.AsSpan()[2 * CommandSize] = 0x4A;

        var stepped = stepper.StepQueued(4);

        Assert.True(stepped == 2, $"StepQueued(4) with command 2 undecodable returned {stepped}, not 2");
        var records = ReportRecords(stepper, 2, "the refused batch");
        AssertSameBytes(oneAtATime[0].Concat(oneAtATime[1]).ToArray(), records, "the refused batch's 2 records against the one-at-a-time run's first 2");
        var stop = stepper.ReportSegment.AsSpan()[(ReportHeaderLength + records.Length)..];
        var code = BinaryPrimitives.ReadInt32LittleEndian(stop);
        var step = BinaryPrimitives.ReadUInt32LittleEndian(stop[4..]);
        Assert.True(code == -3 && code == (int)StepError.CommandRefused, $"the stop entry's code is {code}, not -3 (CommandRefused)");
        Assert.True(step == stream.StepNumber(2), $"the stop entry names step {step}, not the 3rd step {stream.StepNumber(2)}");
        Assert.Equal("input formatId at offset 0: format identifier 0x4A is not 0x49", StopText(stop));

        Queue(stepper, stream.Commands.AsSpan(2, 1));
        var resumed = stepper.StepQueued(1);
        Assert.True(resumed == 1, $"StepQueued(1) with the valid 3rd command returned {resumed}, not 1");
        AssertSameBytes(oneAtATime[2], ReportRecords(stepper, 1, "the 3rd command after the refusal"), "the 3rd step after the refusal against the one-at-a-time run's 3rd record");
    }

    /// <summary>
    /// contracts/wasm-api.md#report-buffer-layouts: After <c>StepQueued(32)</c>, the header's count is 32, the
    /// records walked by their <c>stateLength</c> end exactly at 8 + L, their step numbers are consecutive
    /// from the first step, and each hash is <c>StateHash.Compute(state)</c>.
    /// </summary>
    [Fact]
    public void ReportHeaderMatchesTheRecords()
    {
        var stream = BatchableStream.Create(30);
        var stepper = Loaded(stream);
        Queue(stepper, stream.Commands.AsSpan(0, HostCapacity));

        Assert.True(stepper.StepQueued(HostCapacity) == HostCapacity, "StepQueued(32) did not step 32 commands");

        var report = stepper.ReportSegment.AsSpan();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(report);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(report[4..]);
        Assert.True(count == HostCapacity, $"the header's record count is {count}, not 32");
        Assert.True(ReportHeaderLength + (long)length <= report.Length, $"the header's records length {length} runs past the {report.Length}-byte report");
        var end = ReportHeaderLength + (int)length;
        var offset = ReportHeaderLength;
        for (var i = 0; i < HostCapacity; i++)
        {
            Assert.True(end - offset >= RecordOverhead, $"record {i} starts at {offset}, with only {end - offset} bytes left before 8 + L = {end}");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(report[offset..]);
            var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(report[(offset + 4)..]);
            Assert.True(RecordOverhead + (long)stateLength <= end - offset, $"record {i}'s {stateLength}-byte state runs past 8 + L = {end}");
            var state = report.Slice(offset + 8, (int)stateLength);
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(report[(offset + 8 + (int)stateLength)..]);
            Assert.True(step == stream.StepNumber(i), $"record {i} is step {step}, not {stream.StepNumber(i)}");
            Assert.True(hash == StateHash.Compute(state), $"record {i}'s hash {hash:x16} is not StateHash.Compute of its state ({StateHash.Compute(state):x16})");
            offset += RecordOverhead + (int)stateLength;
        }

        Assert.True(offset == end, $"the 32 records end at {offset}, not at 8 + L = {end}");
    }

    /// <summary>
    /// contracts/wasm-api.md#report-buffer-layouts, Size: a new stepper's report buffer is 65,536 bytes; for
    /// capacity 32, <c>LoadConfig(default.json)</c> sizes it to 2,935,060 (8 + 32 × 91,688 + 12 + 1,024); a
    /// later <c>LoadConfig</c> with a smaller <c>world.maxCharacters</c> keeps the same array and count; a
    /// larger one allocates a bigger buffer, 8 + 32 × its <c>MaxStepRecordLength</c> + 1,036; and for capacity
    /// 1 the default gives 92,732.
    /// </summary>
    [Fact]
    public void ReportBufferGrowsOnlyForLargerMaxima()
    {
        Assert.Equal(DefaultMaxStepRecordLength, CompiledConfig.Create(TestConfigs.Default()).MaxStepRecordLength);
        var defaultBlock = ConfigBlock.Encode(TestConfigs.Default());
        var stepper = new BatchStepper(HostCapacity);
        Assert.Equal(ReportMinimum, stepper.ReportSegment.Count);

        AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(defaultBlock), "LoadConfig of default.json");
        var sized = stepper.ReportSegment;
        Assert.Equal(2_935_060, sized.Count);
        Assert.Equal(ReportHeaderLength + (HostCapacity * DefaultMaxStepRecordLength) + StopEntryHeadLength + MaxStopText, sized.Count);

        var smaller = TestConfigs.With(config => config.World!.MaxCharacters = 8);
        Assert.True(CompiledConfig.Create(smaller).MaxStepRecordLength < DefaultMaxStepRecordLength, "8 characters do not give a shorter maximum record");
        AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(ConfigBlock.Encode(smaller)), "LoadConfig with maxCharacters 8");
        Assert.Same(sized.Array, stepper.ReportSegment.Array);
        Assert.Equal(2_935_060, stepper.ReportSegment.Count);

        var larger = TestConfigs.With(config => config.World!.MaxCharacters = 128);
        var largerRecord = CompiledConfig.Create(larger).MaxStepRecordLength;
        Assert.True(largerRecord > DefaultMaxStepRecordLength, "128 characters do not give a longer maximum record");
        AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(ConfigBlock.Encode(larger)), "LoadConfig with maxCharacters 128");
        Assert.NotSame(sized.Array, stepper.ReportSegment.Array);
        Assert.Equal(ReportHeaderLength + (HostCapacity * largerRecord) + StopEntryHeadLength + MaxStopText, stepper.ReportSegment.Count);

        var single = new BatchStepper(1);
        AssertLoad(LoadResult.Ok, 0, single.LoadConfig(defaultBlock), "LoadConfig of default.json at capacity 1");
        Assert.Equal(92_732, single.ReportSegment.Count);
    }

    /// <summary>A stepper of capacity 32 loaded with the stream's collision file, config block, initial state and salt, each returning <c>Ok</c>.</summary>
    private static BatchStepper Loaded(BatchableStream stream)
    {
        var stepper = new BatchStepper(HostCapacity);
        AssertLoad(LoadResult.Ok, 0, stepper.LoadCollision(stream.Collision), "LoadCollision of the test world");
        AssertLoad(LoadResult.Ok, 0, stepper.LoadConfig(stream.ConfigBlock), "LoadConfig of the stream's config block");
        AssertLoad(LoadResult.Ok, 0, stepper.Reset(stream.InitialState, stream.MatchSalt), "Reset to the stream's initial state");
        return stepper;
    }

    /// <summary>The records of the stream's first <paramref name="count"/> commands, one <c>StepQueued(1)</c> each.</summary>
    private static List<byte[]> OneAtATime(BatchableStream stream, int count)
    {
        var stepper = Loaded(stream);
        var records = new List<byte[]>(count);
        for (var k = 0; k < count; k++)
        {
            Queue(stepper, stream.Commands.AsSpan(k, 1));
            Assert.True(stepper.StepQueued(1) == 1, $"the one-at-a-time run's command {k} did not step");
            records.Add(ReportRecords(stepper, 1, $"the one-at-a-time run's command {k}"));
        }

        return records;
    }

    /// <summary>Encodes <paramref name="commands"/> with <see cref="InputCodec.TryEncode"/> into the command buffer from its start, 17 bytes each.</summary>
    private static void Queue(BatchStepper stepper, ReadOnlySpan<InputCommand> commands)
    {
        var buffer = stepper.CommandSegment.AsSpan();
        Assert.True(buffer.Length >= commands.Length * CommandSize, $"{commands.Length} commands do not fit the {buffer.Length}-byte command buffer");
        for (var i = 0; i < commands.Length; i++)
        {
            Assert.True(InputCodec.TryEncode(commands[i], buffer.Slice(i * CommandSize, CommandSize)), $"command {i} did not encode");
        }
    }

    /// <summary>
    /// The report's records after a <c>StepQueued</c> that returned <paramref name="count"/>: the header's
    /// count must be <paramref name="count"/>, and the bytes are [8, 8 + L).
    /// </summary>
    private static byte[] ReportRecords(BatchStepper stepper, int count, string what)
    {
        var report = stepper.ReportSegment.AsSpan();
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(report);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(report[4..]);
        Assert.True(recordCount == (uint)count, $"{what}: the header's record count is {recordCount}, not {count}");
        Assert.True(ReportHeaderLength + (long)length <= report.Length, $"{what}: the records length {length} runs past the {report.Length}-byte report");
        return report.Slice(ReportHeaderLength, (int)length).ToArray();
    }

    /// <summary>A stop entry's text: <c>u32</c> length at 8 (at most 1,024), UTF-8 at 12.</summary>
    private static string StopText(ReadOnlySpan<byte> stop)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(stop[8..]);
        Assert.True(length <= MaxStopText, $"the stop entry's text is {length} bytes, more than {MaxStopText}");
        return Encoding.UTF8.GetString(stop.Slice(StopEntryHeadLength, (int)length));
    }

    /// <summary>Asserts a load call's result, and its number as the contract's return-code table gives it.</summary>
    private static void AssertLoad(LoadResult expected, int code, LoadResult actual, string what)
    {
        Assert.True(actual == expected, $"{what} returned {actual}, not {expected}");
        Assert.True((int)actual == code, $"{what} returned code {(int)actual}, not {code}");
    }

    /// <summary>Asserts the report after a load call holds <paramref name="expected"/>: its UTF-8 byte length at 0 and its text at 4.</summary>
    private static void AssertReportText(BatchStepper stepper, string expected, string what)
    {
        var report = stepper.ReportSegment.AsSpan();
        var length = BinaryPrimitives.ReadUInt32LittleEndian(report);
        var expectedLength = Encoding.UTF8.GetByteCount(expected);
        Assert.True(length == (uint)expectedLength, $"{what}: the report's text length is {length}, not {expectedLength}");
        Assert.Equal(expected, Encoding.UTF8.GetString(report.Slice(4, expectedLength)));
    }

    /// <summary>Writes <see cref="Scribble"/> over the report's first 16 bytes.</summary>
    private static void ScribbleReport(BatchStepper stepper) => stepper.ReportSegment.AsSpan(0, 16).Fill(Scribble);

    /// <summary>A copy of <paramref name="block"/> whose entry count is one more than its entries.</summary>
    private static byte[] MalformedBlock(byte[] block)
    {
        var copy = (byte[])block.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy, (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(copy) + 1));
        return copy;
    }

    /// <summary>Asserts two byte arrays are equal, naming the first byte that differs.</summary>
    private static void AssertSameBytes(byte[] expected, byte[] actual, string what)
    {
        Assert.True(expected.Length > 0, $"{what}: no expected bytes");
        var common = System.Math.Min(expected.Length, actual.Length);
        var first = expected.AsSpan(0, common).CommonPrefixLength(actual.AsSpan(0, common));
        Assert.True(
            expected.Length == actual.Length && first == common,
            $"{what}: {expected.Length} and {actual.Length} bytes, first difference at byte {first}");
    }

    /// <summary>
    /// A batchable stream: one character at the test world's <c>FlatRunStart</c> (yaw 0, at rest with the
    /// placeholder weapons, step 0), <c>TestConfigs.At(hz)</c>, the committed
    /// <c>simcore/content/test-world.mmcd</c>, and 96 steps, each one command for character index 0, encoded
    /// with <see cref="RecordedStream"/>. Command k has sequence k + 1, client step k + 1, buttons
    /// <c>(k × 37) % 512</c>, yaw <c>(ushort)(k × 2731)</c>, pitch <c>((k × 1543) % 32769) − 16384</c> and slot
    /// <c>k % 2</c>; every 16th (k % 16 == 15) is invalid, cycling a repeated sequence (k, the previous
    /// command's), the reserved button <c>0x0200</c>, pitch 16385 and slot 2.
    /// </summary>
    private sealed record BatchableStream(byte[] Stream, byte[] Collision, byte[] ConfigBlock, byte[] InitialState, ulong MatchSalt, InputCommand[] Commands)
    {
        private const int StreamHeaderLength = 64;
        private const int MatchSaltOffset = 28;
        private const int ConfigLengthOffset = 44;
        private const int InitialStateLengthOffset = 48;

        /// <summary>Step S₀ + k + 1 = k + 1, the step command k runs as.</summary>
        public uint StepNumber(int k) => (uint)k + 1u;

        public static BatchableStream Create(int hz)
        {
            var collision = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
            var world = CollisionFile.Decode(collision);
            var start = TestWorldGenerator.Features.FlatRunStart;
            var initialState = TestStates.Build(
                0,
                [TestStates.StandingCharacter(1, start.X, start.Z) with { Position = (start.X, start.Y, start.Z) }]);

            var commands = new InputCommand[StreamSteps];
            var steps = new RecordedStep[StreamSteps];
            for (var k = 0; k < StreamSteps; k++)
            {
                var sequence = (uint)k + 1u;
                var buttons = (Buttons)((k * 37) % 512);
                var pitch = (short)(((k * 1543) % 32769) - 16384);
                var slot = (byte)(k % 2);
                if (k % 16 == 15)
                {
                    switch ((k / 16) % 4)
                    {
                        case 0:
                            sequence = (uint)k;
                            break;
                        case 1:
                            buttons |= (Buttons)0x0200;
                            break;
                        case 2:
                            pitch = 16385;
                            break;
                        default:
                            slot = 2;
                            break;
                    }
                }

                var step = (uint)k + 1u;
                commands[k] = new InputCommand(sequence, step, buttons, unchecked((ushort)(k * 2731)), pitch, slot);
                steps[k] = new RecordedStep(step, [new RecordedCommand(0, commands[k])], 0UL);
            }

            var encoded = new RecordedStream(
                SimCoreVersion.RulesRevision,
                hz,
                seed: 0,
                matchSalt: Salt,
                collisionChecksum: world.Checksum,
                TestConfigs.At(hz),
                initialState,
                steps).Encode();

            var configLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(ConfigLengthOffset));
            var initialStateLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(InitialStateLengthOffset));
            return new BatchableStream(
                encoded,
                collision,
                encoded.AsSpan(StreamHeaderLength, configLength).ToArray(),
                encoded.AsSpan(StreamHeaderLength + configLength, initialStateLength).ToArray(),
                BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(MatchSaltOffset)),
                commands);
        }
    }
}
