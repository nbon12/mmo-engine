using System.Buffers.Binary;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Batch;

/// <summary>
/// <see cref="StreamRunner"/> (contracts/wasm-api.md#streamrunner) runs a whole recorded stream and returns
/// one step record per step, packed back to back (contracts/state-serialization.md#step-record):
/// <c>u32 step | u32 stateLength | state | u64 hash</c>. The tests build a 10-step, one-character stream
/// on <see cref="TestWorlds.Flat"/> whose expected hashes come from stepping a <see cref="SimWorld"/>
/// directly with the same commands, and read the records with <see cref="BinaryPrimitives"/> at the
/// contract's offsets, independent of <c>StepRecord</c>. The perturbation and open-refusal tests run the
/// committed <c>simcore/streams/flat-run-&lt;hz&gt;hz.mmrs</c> over <c>simcore/content/test-world.mmcd</c>,
/// both read-only through <see cref="RepoPaths"/>, and change only in-memory copies.
/// </summary>
public sealed class StreamRunnerTests
{
    /// <summary>The initial state's step S₀; the first entry is step S₀ + 1.</summary>
    private const uint InitialStep = 4;

    private const int StepCount = 10;

    private const ulong Salt = 0x0123456789abcdefUL;

    /// <summary>A step record's <c>step</c>, <c>stateLength</c> and <c>hash</c>.</summary>
    private const int RecordOverhead = 16;

    /// <summary>The field the fault test makes non-finite.</summary>
    private const string VelocityYPath = "characters[0].velocity.y";

    /// <summary>A quiet NaN's bits.</summary>
    private const uint NaNBits = 0x7FC00000u;

    /// <summary><c>characters[0].position.x</c>'s offset in a canonical state: the 12-byte header, then the character's <c>id</c>.</summary>
    private const int PositionXOffset = 16;

    /// <summary>The stream header's <c>version</c>.</summary>
    private const int VersionOffset = 4;

    /// <summary>The stream header's <c>rulesRevision</c>.</summary>
    private const int RulesRevisionOffset = 8;

    /// <summary>The stream header's <c>collisionChecksum</c>.</summary>
    private const int CollisionChecksumOffset = 36;

    /// <summary>
    /// <c>initialState.characters[0].position.x</c> in flat-run-30hz: 64 + its 698-byte config block + 16.
    /// </summary>
    private const int InitialPositionXOffset = 778;

    /// <summary>5000f: inside ±8192, so the byte-only checks accept it, but outside the test world's playable area.</summary>
    private const uint OutsideTheWorldBits = 0x459C4000u;

    /// <summary>
    /// Given a 10-step one-character stream on the flat world at <paramref name="hz"/> (sequences 1 to 10,
    /// Forward on odd step numbers) whose expected hashes come from stepping a <see cref="SimWorld"/>
    /// directly, When <c>StreamRunner.Run(stream.Encode(), CollisionFile.Encode(flat))</c> runs, Then it
    /// returns 10 records back to back: record i has step S₀ + i + 1, a <c>stateLength</c> equal to the
    /// direct state's length, the direct <c>StepReport.State</c> bytes, and a hash equal to
    /// <c>StateHash.Compute(state)</c> and to the expected hash; and the records fill the output exactly,
    /// Σ(16 + stateLength) bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void RecordsCarryTheExpectedHashes(int hz)
    {
        var run = DirectRun.Create(hz);

        var records = StreamRunner.Run(run.Stream.Encode(), CollisionFile.Encode(run.Data));

        var offset = 0;
        var total = 0L;
        for (var i = 0; i < StepCount; i++)
        {
            var label = $"{hz} Hz, record {i}";
            var expected = run.Steps[i];
            Assert.True(records.Length - offset >= RecordOverhead, $"{label}: only {records.Length - offset} bytes are left at byte {offset}");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset));
            var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            Assert.True(step == InitialStep + (uint)i + 1, $"{label}: step {step}, expected {InitialStep + (uint)i + 1}");
            Assert.True(
                stateLength == (uint)expected.State.Length,
                $"{label}: stateLength {stateLength}; the direct step's state is {expected.State.Length} bytes");
            Assert.True(
                RecordOverhead + (long)stateLength <= records.Length - offset,
                $"{label}: a {stateLength}-byte state runs past the {records.Length}-byte output");

            var state = records.AsSpan(offset + 8, (int)stateLength);
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset + 8 + (int)stateLength));
            Assert.True(state.SequenceEqual(expected.State), $"{label}: the state bytes differ from the direct StepReport.State");
            Assert.True(hash == StateHash.Compute(state), $"{label}: hash {hash:x16}; StateHash.Compute(state) is {StateHash.Compute(state):x16}");
            Assert.True(hash == expected.Hash, $"{label}: hash {hash:x16}; the expected hash is {expected.Hash:x16}");
            Assert.True(
                hash == run.Stream.Steps[i].ExpectedHash,
                $"{label}: hash {hash:x16}; the stream's expected hash is {run.Stream.Steps[i].ExpectedHash:x16}");

            offset += RecordOverhead + (int)stateLength;
            total += RecordOverhead + stateLength;
        }

        Assert.True(
            records.Length == total && offset == records.Length,
            $"{hz} Hz: the output is {records.Length} bytes; 10 records of 16 + stateLength bytes are {total}");
    }

    /// <summary>
    /// Given the same stream, When it runs through the byte overload (the collision file) and through the
    /// list overload (<c>new[] { world }</c>, the decoded world), Then both return the same bytes.
    /// </summary>
    [Fact]
    public void BothOverloadsGiveTheSameRecords()
    {
        var run = DirectRun.Create(30);
        var stream = run.Stream.Encode();

        var fromFile = StreamRunner.Run(stream, CollisionFile.Encode(run.Data));
        var fromWorlds = StreamRunner.Run(stream, new[] { run.World });

        Assert.True(fromFile.Length > 0, "the byte overload returned no records");
        Assert.Equal(fromFile, fromWorlds);
    }

    /// <summary>
    /// Given the same stream, whose steps are 5 to 14, When it runs with <c>perturbStep: 999999</c>, a step
    /// the stream does not hold, Then <see cref="ArgumentOutOfRangeException"/> is thrown naming
    /// <c>perturbStep</c> and no records are returned; the same stream without a perturbation runs.
    /// </summary>
    [Fact]
    public void PerturbStepOutsideTheStreamThrows()
    {
        var run = DirectRun.Create(30);
        var stream = run.Stream.Encode();
        var collision = CollisionFile.Encode(run.Data);
        byte[]? records = null;

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => records = StreamRunner.Run(stream, collision, perturbStep: 999999));

        Assert.Equal("perturbStep", thrown.ParamName);
        Assert.Null(records);
        var unperturbed = StreamRunner.Run(stream, collision);
        Assert.True(
            unperturbed.Length == run.Steps.Sum(step => RecordOverhead + step.State.Length),
            $"without a perturbation the stream gives {unperturbed.Length} bytes of records, not its 10 records");
    }

    /// <summary>
    /// SC-002 (contracts/wasm-api.md#streamrunner, perturbation): Given the committed
    /// <c>simcore/streams/flat-run-&lt;hz&gt;hz.mmrs</c> and <c>simcore/content/test-world.mmcd</c>, read-only
    /// through <see cref="RepoPaths"/>, and k the stream's step number at index <c>(stepCount − 1) / 2</c>
    /// (15 for flat-run-30hz), When <c>StreamRunner.Run(stream, collision, perturbStep: k)</c> is compared
    /// record by record with <c>StreamRunner.Run(stream, collision)</c>, Then both hold the same number of
    /// records; every record before step k is byte-identical; record k has the same step number and state
    /// length, its state differs only in the 4 bytes of <c>characters[0].position.x</c> (state offset 16),
    /// whose perturbed bits are the unperturbed bits + 1, and its hash is <c>StateHash.Compute</c> of the
    /// perturbed state and differs from the unperturbed hash; and every later record differs from the
    /// unperturbed one while keeping its step number k + 1, k + 2 and onward. Also <c>perturbStep: -1</c> gives
    /// the unperturbed bytes, and a <c>perturbStep</c> of 0, −2 or the last step number + 1 throws
    /// <see cref="ArgumentOutOfRangeException"/> naming <c>perturbStep</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void PerturbationDivergesAtExactlyThatStep(int hz)
    {
        var name = $"flat-run-{hz}hz";
        var stream = RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs");
        var collision = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var decoded = RecordedStream.Decode(stream);
        var stepCount = decoded.Steps.Count;
        var kIndex = (stepCount - 1) / 2;
        var k = decoded.Steps[kIndex].Step;
        if (hz == 30)
        {
            Assert.True(k == 15, $"{name}: the step at index {kIndex} is {k}, not 15");
        }

        var unperturbed = ReadRecords(StreamRunner.Run(stream, collision), name);
        var perturbed = ReadRecords(StreamRunner.Run(stream, collision, perturbStep: (int)k), $"{name} perturbed at {k}");

        Assert.True(
            perturbed.Count == unperturbed.Count && unperturbed.Count == stepCount,
            $"{name}: {perturbed.Count} perturbed and {unperturbed.Count} unperturbed records; the stream has {stepCount} steps");
        for (var i = 0; i < kIndex; i++)
        {
            Assert.True(
                perturbed[i].Bytes.AsSpan().SequenceEqual(unperturbed[i].Bytes),
                $"{name}: record {i} (step {unperturbed[i].Step}), before the perturbed step {k}, differs");
        }

        var before = unperturbed[kIndex];
        var after = perturbed[kIndex];
        Assert.True(after.Step == k && before.Step == k, $"{name}: record {kIndex} has steps {before.Step} and {after.Step}, not {k}");
        Assert.True(
            after.State.Length == before.State.Length,
            $"{name}: step {k}'s perturbed state is {after.State.Length} bytes, the unperturbed one {before.State.Length}");
        for (var b = 0; b < before.State.Length; b++)
        {
            if (b is < PositionXOffset or >= PositionXOffset + 4)
            {
                Assert.True(
                    after.State[b] == before.State[b],
                    $"{name}: step {k}'s state byte {b} is 0x{after.State[b]:X2} perturbed and 0x{before.State[b]:X2} unperturbed; only bytes 16 to 19 may differ");
            }
        }

        var bitsBefore = BinaryPrimitives.ReadUInt32LittleEndian(before.State.AsSpan(PositionXOffset));
        var bitsAfter = BinaryPrimitives.ReadUInt32LittleEndian(after.State.AsSpan(PositionXOffset));
        Assert.True(
            bitsAfter == bitsBefore + 1u,
            $"{name}: step {k}'s characters[0].position.x is 0x{bitsAfter:X8} perturbed and 0x{bitsBefore:X8} unperturbed; expected the bits + 1");
        Assert.True(
            after.Hash == StateHash.Compute(after.State),
            $"{name}: step {k}'s perturbed hash {after.Hash:x16} is not StateHash.Compute of its state ({StateHash.Compute(after.State):x16})");
        Assert.True(after.Hash != before.Hash, $"{name}: step {k}'s perturbed hash equals the unperturbed {before.Hash:x16}");

        for (var i = kIndex + 1; i < stepCount; i++)
        {
            var expectedStep = k + (uint)(i - kIndex);
            Assert.True(
                perturbed[i].Step == expectedStep && unperturbed[i].Step == expectedStep,
                $"{name}: record {i} has steps {unperturbed[i].Step} and {perturbed[i].Step}, not {expectedStep}");
            Assert.False(
                perturbed[i].Bytes.AsSpan().SequenceEqual(unperturbed[i].Bytes),
                $"{name}: record {i} (step {expectedStep}), after the perturbed step {k}, equals the unperturbed record");
        }

        var noPerturbation = StreamRunner.Run(stream, collision, perturbStep: -1);
        Assert.True(
            noPerturbation.AsSpan().SequenceEqual(StreamRunner.Run(stream, collision)),
            $"{name}: perturbStep -1 does not give the unperturbed bytes");

        var lastStep = decoded.Steps[stepCount - 1].Step;
        foreach (var outside in new[] { 0, -2, (int)lastStep + 1 })
        {
            byte[]? records = null;
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => records = StreamRunner.Run(stream, collision, perturbStep: outside));
            Assert.True(thrown.ParamName == "perturbStep", $"{name}: perturbStep {outside} threw naming {thrown.ParamName}, not perturbStep");
            Assert.Null(records);
        }
    }

    /// <summary>
    /// Rows of <see cref="RefusesBeforeStepZero"/>: each a refusal of contracts/recorded-stream-format.md#opening-a-stream-to-run.
    /// </summary>
    public static TheoryData<string> RefusalCases =>
    [
        "unsupported-version",
        "rules-revision",
        "collision-missing-list",
        "collision-missing-bytes",
        "config-invalid",
        "initial-state-outside-world",
    ];

    /// <summary>
    /// US2.5 and US6.5 support (contracts/recorded-stream-format.md#opening-a-stream-to-run): Given a copy of
    /// the committed flat-run-30hz bytes changed per row, When <see cref="StreamRunner"/> opens it, Then it
    /// throws exactly the row's exception, with the row's code, offset, field and message, and returns no
    /// records:
    /// <c>unsupported-version</c> (u16 at 4 = 2) is <c>UnsupportedVersion</c> at 4, <c>version</c>,
    /// <c>stream version at offset 4: unsupported version 2; this reader supports version 1</c>;
    /// <c>rules-revision</c> (u32 at 8 = <c>RulesRevision</c> + 1, trailer rewritten) is
    /// <c>RulesRevisionMismatch</c> at 8, <c>rulesRevision</c>,
    /// <c>stream rulesRevision at offset 8: rules revision 2 in the stream; this core implements rules revision 1</c>;
    /// <c>collision-missing-list</c> (the list overload, an empty list) and <c>collision-missing-bytes</c>
    /// (the byte overload, the tiny-world file) are <c>CollisionDataMissing</c> at 36,
    /// <c>collisionChecksum</c>, <c>collision data &lt;test-world checksum&gt; was not supplied</c>;
    /// <c>config-invalid</c> (re-encoded with <c>movement.gravity</c> NaN) is a
    /// <see cref="ConfigValidationException"/> whose errors are exactly
    /// [<c>movement.gravity: must be finite</c>]; and <c>initial-state-outside-world</c> (u32 at 778, the
    /// initial state's <c>characters[0].position.x</c>, = 5000f, trailer rewritten) is format <c>stream</c>,
    /// <c>OutOfRange</c> at 778, <c>initialState.characters[0].position.x</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void RefusesBeforeStepZero(string caseName)
    {
        var stream = RepoPaths.ReadAllBytes("simcore", "streams", "flat-run-30hz.mmrs");
        var collision = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var testWorldChecksum = CollisionFile.Decode(collision).Checksum;
        var missingDetail = $"collision data {testWorldChecksum:x16} was not supplied";
        byte[]? records = null;

        switch (caseName)
        {
            case "unsupported-version":
                {
                    var copy = (byte[])stream.Clone();
                    BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(VersionOffset), 2);
                    var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(copy, collision));
                    AssertFormatError(
                        thrown,
                        FormatErrorCode.UnsupportedVersion,
                        VersionOffset,
                        "version",
                        "stream version at offset 4: unsupported version 2; this reader supports version 1");
                    break;
                }

            case "rules-revision":
                {
                    var copy = StreamBytes.WithBytes(stream, RulesRevisionOffset, TestStates.U32(SimCoreVersion.RulesRevision + 1u));
                    var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(copy, collision));
                    AssertFormatError(
                        thrown,
                        FormatErrorCode.RulesRevisionMismatch,
                        RulesRevisionOffset,
                        "rulesRevision",
                        $"stream rulesRevision at offset 8: rules revision {SimCoreVersion.RulesRevision + 1u} in the stream; this core implements rules revision {SimCoreVersion.RulesRevision}");
                    Assert.Equal(
                        $"rules revision {SimCoreVersion.RulesRevision + 1u} in the stream; this core implements rules revision {SimCoreVersion.RulesRevision}",
                        thrown.Error.Detail);
                    break;
                }

            case "collision-missing-list":
                {
                    var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(stream, Array.Empty<CollisionWorld>()));
                    AssertFormatError(thrown, FormatErrorCode.CollisionDataMissing, CollisionChecksumOffset, "collisionChecksum", $"stream collisionChecksum at offset 36: {missingDetail}");
                    Assert.Equal(missingDetail, thrown.Error.Detail);
                    break;
                }

            case "collision-missing-bytes":
                {
                    var tiny = CollisionFile.Encode(TestWorlds.TinyWorld());
                    var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(stream, tiny));
                    AssertFormatError(thrown, FormatErrorCode.CollisionDataMissing, CollisionChecksumOffset, "collisionChecksum", $"stream collisionChecksum at offset 36: {missingDetail}");
                    Assert.Equal(missingDetail, thrown.Error.Detail);
                    break;
                }

            case "config-invalid":
                {
                    var decoded = RecordedStream.Decode(stream);
                    var config = decoded.Config;
                    config.Movement!.Gravity = float.NaN;
                    var copy = new RecordedStream(
                        decoded.RulesRevision,
                        decoded.StepHz,
                        decoded.Seed,
                        decoded.MatchSalt,
                        decoded.CollisionChecksum,
                        config,
                        decoded.InitialState,
                        decoded.Steps).Encode();
                    var thrown = Assert.Throws<ConfigValidationException>(() => records = StreamRunner.Run(copy, collision));
                    Assert.Equal(new[] { "movement.gravity: must be finite" }, thrown.Errors.Select(error => error.ToString()));
                    break;
                }

            case "initial-state-outside-world":
                {
                    var copy = StreamBytes.WithBytes(stream, InitialPositionXOffset, TestStates.U32(OutsideTheWorldBits));
                    var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(copy, collision));
                    Assert.Equal("stream", thrown.Error.Format);
                    Assert.Equal(FormatErrorCode.OutOfRange, thrown.Error.Code);
                    Assert.Equal(InitialPositionXOffset, thrown.Error.Offset);
                    Assert.Equal("initialState.characters[0].position.x", thrown.Error.Field);
                    Assert.StartsWith("stream initialState.characters[0].position.x at offset 778: ", thrown.Message, StringComparison.Ordinal);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of RefusesBeforeStepZero.");
        }

        Assert.Null(records);
    }

    /// <summary>
    /// Given a <see cref="StepFault"/> taken from <c>SimWorld.StepWithInjectedBits</c> with NaN bits
    /// (0x7FC00000) at <c>characters[0].velocity.y</c>, When it is wrapped as
    /// <c>new StepFaultException(7, fault)</c>, Then the exception has <c>Step</c> 7, that <c>Fault</c>, and
    /// the message <c>step 7 faulted: NonFinite at characters[0].velocity.y</c>.
    /// </summary>
    [Fact]
    public void StepFaultExceptionNamesTheStepCodeAndPath()
    {
        var world = new SimWorld(TestConfigs.Compiled(30), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(DirectRun.InitialState(), Salt);
        Assert.True(
            StateSerializer.TryFindField(VelocityYPath, out var ordinal, out var index),
            $"StateSerializer.TryFindField does not know {VelocityYPath}");
        var fault = world.StepWithInjectedBits([], ordinal, index, NaNBits).Fault;
        Assert.Equal(StepFaultCode.NonFinite, fault.Code);
        Assert.Equal(VelocityYPath, fault.Path);

        var exception = new StepFaultException(7, fault);

        Assert.Equal(7u, exception.Step);
        Assert.Equal(fault, exception.Fault);
        Assert.Equal("step 7 faulted: NonFinite at characters[0].velocity.y", exception.Message);
    }

    /// <summary>
    /// Splits packed step records (<c>u32 step | u32 stateLength | state | u64 hash</c>) with
    /// <see cref="BinaryPrimitives"/> at the contract's offsets, requiring them to fill the output exactly.
    /// </summary>
    private static List<PackedRecord> ReadRecords(byte[] records, string what)
    {
        var list = new List<PackedRecord>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(records.Length - offset >= RecordOverhead, $"{what}: only {records.Length - offset} bytes are left at byte {offset}");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset));
            var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            Assert.True(
                RecordOverhead + (long)stateLength <= records.Length - offset,
                $"{what}: the record at byte {offset} has a {stateLength}-byte state, past the {records.Length}-byte output");
            var length = RecordOverhead + (int)stateLength;
            var state = records.AsSpan(offset + 8, (int)stateLength).ToArray();
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset + 8 + (int)stateLength));
            list.Add(new PackedRecord(step, state, hash, records.AsSpan(offset, length).ToArray()));
            offset += length;
        }

        return list;
    }

    /// <summary>Asserts a stream refusal's format, code, offset, field and whole message.</summary>
    private static void AssertFormatError(CoreFormatException thrown, FormatErrorCode code, long offset, string field, string message)
    {
        Assert.Equal("stream", thrown.Error.Format);
        Assert.Equal(code, thrown.Error.Code);
        Assert.Equal(offset, thrown.Error.Offset);
        Assert.Equal(field, thrown.Error.Field);
        Assert.Equal(message, thrown.Message);
    }

    /// <summary>One packed step record: its step, state and hash, and its whole bytes.</summary>
    private sealed record PackedRecord(uint Step, byte[] State, ulong Hash, byte[] Bytes);

    /// <summary>One step of the direct run: the report's step, its state bytes and its hash.</summary>
    private sealed record DirectStep(uint Step, byte[] State, ulong Hash);

    /// <summary>
    /// The test stream and what a <see cref="SimWorld"/> stepped directly with its commands gave: the
    /// flat world's data and decoded world, the stream (with those hashes as its expected hashes) and every
    /// direct step.
    /// </summary>
    private sealed record DirectRun(CollisionData Data, CollisionWorld World, RecordedStream Stream, IReadOnlyList<DirectStep> Steps)
    {
        /// <summary>Character 1 at rest on the flat world, a skin above the floor, Grounded, yaw 0, at step S₀.</summary>
        public static byte[] InitialState() => TestStates.Build(
            InitialStep,
            [TestStates.StandingCharacter(1, 0.5f, 0.5f) with { Position = (0.5f, MovementWorlds.ContactSkin, 0.5f) }]);

        /// <summary>
        /// Steps a world on <see cref="TestWorlds.Flat"/> at <paramref name="hz"/> through 10 one-command
        /// steps (sequence i + 1, client step equal to the step, Forward on odd step numbers, yaw 0, pitch 0,
        /// slot 0), requiring every step to commit, and records the commands with the hashes as a stream.
        /// </summary>
        public static DirectRun Create(int hz)
        {
            var data = TestWorlds.Flat();
            var world = TestWorlds.Load(data);
            var config = TestConfigs.At(hz);
            var initialState = InitialState();
            var simWorld = new SimWorld(CompiledConfig.Create(config), world);
            simWorld.Reset(initialState, Salt);

            var entries = new List<RecordedStep>(StepCount);
            var steps = new List<DirectStep>(StepCount);
            for (var i = 0; i < StepCount; i++)
            {
                var step = InitialStep + (uint)i + 1;
                var command = new InputCommand(
                    Sequence: (uint)i + 1,
                    ClientStep: step,
                    Buttons: step % 2 == 1 ? Buttons.Forward : default,
                    AimYaw: 0,
                    AimPitch: 0,
                    WeaponSlot: 0);
                var report = simWorld.Step([new CharacterCommand(0, command)]);
                Assert.True(report.Fault.Code == StepFaultCode.None, $"{hz} Hz: the direct step {step} faulted: {report.Fault.Code} at {report.Fault.Path}");
                Assert.True(report.Step == step, $"{hz} Hz: the direct step reported step {report.Step}, not {step}");
                steps.Add(new DirectStep(report.Step, report.State.ToArray(), report.Hash));
                entries.Add(new RecordedStep(step, [new RecordedCommand(0, command)], report.Hash));
            }

            var stream = new RecordedStream(
                SimCoreVersion.RulesRevision,
                hz,
                seed: 0,
                matchSalt: Salt,
                collisionChecksum: world.Checksum,
                config,
                initialState,
                entries);
            return new DirectRun(data, world, stream, steps);
        }
    }
}
