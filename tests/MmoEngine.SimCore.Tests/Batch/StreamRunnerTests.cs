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
/// contract's offsets, independent of <c>StepRecord</c>.
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
