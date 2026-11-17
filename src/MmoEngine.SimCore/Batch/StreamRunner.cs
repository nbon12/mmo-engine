using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Batch;

/// <summary>
/// Runs a whole recorded stream and returns every step's record, packed back to back exactly as in a
/// <c>.records</c> file (<c>u32 step | u32 stateLength | state | u64 hash</c>;
/// specs/015-shared-simulation-core/contracts/wasm-api.md#streamrunner,
/// contracts/state-serialization.md#step-record). It opens the stream in the order of
/// contracts/recorded-stream-format.md#opening-a-stream-to-run, so every refusal happens before the first
/// step: decode the stream (1), find the collision world it names by checksum (3), compile its
/// configuration (4) and reset a world from its initial state and match salt (5). Then each entry runs as
/// one <see cref="SimWorld.Step"/> with its commands; the other characters get the neutral input. Expected
/// hashes are not checked here; comparing is the caller's job.
/// </summary>
/// <remarks>
/// This is not tick-path code, so errors are exceptions and it may allocate: a
/// <see cref="CoreFormatException"/> for a format refusal, a <see cref="ConfigValidationException"/> for
/// an invalid configuration, and a <see cref="StepFaultException"/> when a step faults. The rules-revision
/// check (open step 2) and the one-ulp perturbation are Phase 4's; until then any <c>perturbStep</c> other
/// than −1 is refused.
/// </remarks>
public static class StreamRunner
{
    /// <summary>The stream header's <c>collisionChecksum</c> offset.</summary>
    private const int CollisionChecksumOffset = 36;

    /// <summary>The stream header's <c>configLength</c> offset; the initial state starts at 64 + configLength.</summary>
    private const int ConfigLengthOffset = 44;

    /// <summary>
    /// Runs <paramref name="stream"/> over the collision world <paramref name="collisionFile"/> decodes to.
    /// The stream is decoded first, then the collision file, then the stream's checksum is matched against
    /// the file's.
    /// </summary>
    /// <exception cref="CoreFormatException">The stream or the collision file is refused, or the file is not the collision data the stream names.</exception>
    /// <exception cref="ConfigValidationException">The stream's configuration is invalid.</exception>
    /// <exception cref="StepFaultException">A step faulted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="perturbStep"/> is not −1.</exception>
    public static byte[] Run(ReadOnlySpan<byte> stream, ReadOnlySpan<byte> collisionFile, int perturbStep = -1)
    {
        var recorded = RecordedStream.Decode(stream);
        var collision = CollisionFile.Decode(collisionFile);
        return Open(recorded, InitialStateOffset(stream), [collision], perturbStep);
    }

    /// <summary>
    /// Runs <paramref name="stream"/> over the first of <paramref name="collisionWorlds"/> whose
    /// <see cref="CollisionWorld.Checksum"/> is the stream's <c>collisionChecksum</c>; an empty list refuses
    /// every stream.
    /// </summary>
    /// <exception cref="ArgumentNullException">The list is null.</exception>
    /// <exception cref="CoreFormatException">The stream is refused, or no supplied world is the collision data it names.</exception>
    /// <exception cref="ConfigValidationException">The stream's configuration is invalid.</exception>
    /// <exception cref="StepFaultException">A step faulted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="perturbStep"/> is not −1.</exception>
    public static byte[] Run(ReadOnlySpan<byte> stream, IReadOnlyList<CollisionWorld> collisionWorlds, int perturbStep = -1)
    {
        ArgumentNullException.ThrowIfNull(collisionWorlds);
        var recorded = RecordedStream.Decode(stream);
        return Open(recorded, InitialStateOffset(stream), collisionWorlds, perturbStep);
    }

    /// <summary>The file offset of a decoded stream's initial state: 64 + <c>configLength</c>.</summary>
    private static long InitialStateOffset(ReadOnlySpan<byte> stream) =>
        RecordedStream.HeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(stream[ConfigLengthOffset..]);

    /// <summary>Open steps 3 to 5, then every step.</summary>
    private static byte[] Open(RecordedStream stream, long initialStateOffset, IReadOnlyList<CollisionWorld> collisionWorlds, int perturbStep)
    {
        // 3: the collision world the stream names, matched by checksum, never by name.
        var collision = Find(collisionWorlds, stream.CollisionChecksum);

        // 4: the configuration; a guard failure throws ConfigValidationException.
        var config = CompiledConfig.Create(stream.Config);

        // 5: the initial state, with the checks that need the configuration and the world.
        var world = new SimWorld(config, collision);
        world.Reset(stream.InitialState.Span, stream.MatchSalt, StateErrorContext.StreamInitialState(initialStateOffset));

        if (perturbStep != -1)
        {
            throw new ArgumentOutOfRangeException(nameof(perturbStep), perturbStep, "This runner applies no perturbation, so perturbStep must be -1.");
        }

        return RunSteps(world, stream);
    }

    private static CollisionWorld Find(IReadOnlyList<CollisionWorld> collisionWorlds, ulong checksum)
    {
        for (var i = 0; i < collisionWorlds.Count; i++)
        {
            var candidate = collisionWorlds[i];
            if (candidate is not null && candidate.Checksum == checksum)
            {
                return candidate;
            }
        }

        throw new CoreFormatException(new FormatError(
            "stream",
            FormatErrorCode.CollisionDataMissing,
            CollisionChecksumOffset,
            "collisionChecksum",
            string.Create(CultureInfo.InvariantCulture, $"collision data {checksum:x16} was not supplied")));
    }

    /// <summary>One <see cref="SimWorld.Step"/> per entry, each record appended through <see cref="StepRecord"/>.</summary>
    private static byte[] RunSteps(SimWorld world, RecordedStream stream)
    {
        var steps = stream.Steps;
        var widest = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            widest = System.Math.Max(widest, steps[i].Commands.Length);
        }

        var commands = new CharacterCommand[widest];
        var records = new ArrayBufferWriter<byte>();
        for (var i = 0; i < steps.Count; i++)
        {
            var entry = steps[i].Commands;
            for (var c = 0; c < entry.Length; c++)
            {
                commands[c] = new CharacterCommand(entry[c].CharacterIndex, entry[c].Command);
            }

            var report = world.Step(commands.AsSpan(0, entry.Length));
            if (report.Fault.Code != StepFaultCode.None)
            {
                throw new StepFaultException(unchecked(report.Step + 1u), report.Fault);
            }

            var state = report.State;
            var destination = records.GetSpan(StepRecord.OverheadLength + state.Length);
            StepRecord.Write(report.Step, state, report.Hash, destination, out var written);
            records.Advance(written);
        }

        return records.WrittenSpan.ToArray();
    }
}
