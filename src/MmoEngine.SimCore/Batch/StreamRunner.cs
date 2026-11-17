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
/// step: decode the stream (1), refuse another rules revision (2), find the collision world it names by
/// checksum (3), compile its configuration (4) and reset a world from its initial state and match salt (5).
/// Then each entry runs as one <see cref="SimWorld.Step"/> with its commands; the other characters get the
/// neutral input. Expected hashes are not checked here; comparing is the caller's job.
/// </summary>
/// <remarks>
/// <para>
/// This is not tick-path code, so errors are exceptions and it may allocate: a
/// <see cref="CoreFormatException"/> for a format refusal, a <see cref="ConfigValidationException"/> for
/// an invalid configuration, and a <see cref="StepFaultException"/> when a step faults.
/// </para>
/// <para>
/// The one-ulp perturbation (SC-002): with <c>perturbStep</c> a step number of the stream, the step of
/// that number commits, the first character's <c>position.x</c> gets <see cref="MathF.BitIncrement"/>,
/// and that step's record is written from the perturbed, re-serialised and re-hashed state; later steps
/// continue from it. The first divergent step is then exactly <c>perturbStep</c>, naming
/// <c>characters[0].position.x</c>. It is a runtime parameter, never a patched build.
/// </para>
/// </remarks>
public static class StreamRunner
{
    /// <summary>The stream header's <c>collisionChecksum</c> offset.</summary>
    private const int CollisionChecksumOffset = 36;

    /// <summary>The stream header's <c>configLength</c> offset; the initial state starts at 64 + configLength.</summary>
    private const int ConfigLengthOffset = 44;

    /// <summary>The stream header's <c>rulesRevision</c> offset.</summary>
    private const int RulesRevisionOffset = 8;

    /// <summary>The <c>perturbStep</c> that applies no perturbation.</summary>
    private const int NoPerturbation = -1;

    /// <summary>
    /// Runs <paramref name="stream"/> over the collision world <paramref name="collisionFile"/> decodes to.
    /// The stream is decoded and its rules revision checked first, then the collision file is decoded, then
    /// the stream's checksum is matched against the file's.
    /// </summary>
    /// <exception cref="CoreFormatException">The stream or the collision file is refused, or the file is not the collision data the stream names.</exception>
    /// <exception cref="ConfigValidationException">The stream's configuration is invalid.</exception>
    /// <exception cref="StepFaultException">A step faulted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="perturbStep"/> is neither −1 nor one of the stream's step numbers.</exception>
    public static byte[] Run(ReadOnlySpan<byte> stream, ReadOnlySpan<byte> collisionFile, int perturbStep = NoPerturbation)
    {
        var recorded = RecordedStream.Decode(stream);
        CheckRulesRevision(recorded);
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
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="perturbStep"/> is neither −1 nor one of the stream's step numbers.</exception>
    public static byte[] Run(ReadOnlySpan<byte> stream, IReadOnlyList<CollisionWorld> collisionWorlds, int perturbStep = NoPerturbation)
    {
        ArgumentNullException.ThrowIfNull(collisionWorlds);
        var recorded = RecordedStream.Decode(stream);
        CheckRulesRevision(recorded);
        return Open(recorded, InitialStateOffset(stream), collisionWorlds, perturbStep);
    }

    /// <summary>The file offset of a decoded stream's initial state: 64 + <c>configLength</c>.</summary>
    private static long InitialStateOffset(ReadOnlySpan<byte> stream) =>
        RecordedStream.HeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(stream[ConfigLengthOffset..]);

    /// <summary>
    /// Open step 2: a stream recorded under another rules revision is refused with
    /// <see cref="FormatErrorCode.RulesRevisionMismatch"/> at offset 8, naming both revisions, so its
    /// expected hashes are never compared against these rules (reported as NotComparable).
    /// </summary>
    private static void CheckRulesRevision(RecordedStream stream)
    {
        if (stream.RulesRevision != SimCoreVersion.RulesRevision)
        {
            throw new CoreFormatException(new FormatError(
                "stream",
                FormatErrorCode.RulesRevisionMismatch,
                RulesRevisionOffset,
                "rulesRevision",
                string.Create(CultureInfo.InvariantCulture, $"rules revision {stream.RulesRevision} in the stream; this core implements rules revision {SimCoreVersion.RulesRevision}")));
        }
    }

    /// <summary>Open steps 3 to 5, the perturbation check, then every step.</summary>
    private static byte[] Open(RecordedStream stream, long initialStateOffset, IReadOnlyList<CollisionWorld> collisionWorlds, int perturbStep)
    {
        // 3: the collision world the stream names, matched by checksum, never by name.
        var collision = Find(collisionWorlds, stream.CollisionChecksum);

        // 4: the configuration; a guard failure throws ConfigValidationException.
        var config = CompiledConfig.Create(stream.Config);

        // 5: the initial state, with the checks that need the configuration and the world.
        var world = new SimWorld(config, collision);
        world.Reset(stream.InitialState.Span, stream.MatchSalt, StateErrorContext.StreamInitialState(initialStateOffset));

        var perturbIndex = PerturbIndex(stream, perturbStep);
        return RunSteps(world, stream, perturbIndex);
    }

    /// <summary>
    /// The index of the entry whose step number is <paramref name="perturbStep"/>, or −1 for no perturbation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="perturbStep"/> is neither −1 nor one of the stream's step numbers.</exception>
    private static int PerturbIndex(RecordedStream stream, int perturbStep)
    {
        if (perturbStep == NoPerturbation)
        {
            return -1;
        }

        if (perturbStep >= 0)
        {
            var steps = stream.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Step == (uint)perturbStep)
                {
                    return i;
                }
            }
        }

        throw new ArgumentOutOfRangeException(nameof(perturbStep), perturbStep, "perturbStep must be -1 or one of the stream's step numbers.");
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

    /// <summary>
    /// One <see cref="SimWorld.Step"/> per entry, each record appended through <see cref="StepRecord"/>; at
    /// entry <paramref name="perturbIndex"/> the record is the perturbed state's
    /// (<see cref="SimWorld.PerturbFirstCharacterPositionX"/>).
    /// </summary>
    private static byte[] RunSteps(SimWorld world, RecordedStream stream, int perturbIndex)
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

            if (i == perturbIndex)
            {
                // The step committed; its record is written from the perturbed state instead. A refusal here
                // reports the committed step itself, the one whose perturbed state did not commit.
                report = world.PerturbFirstCharacterPositionX();
                if (report.Fault.Code != StepFaultCode.None)
                {
                    throw new StepFaultException(report.Step, report.Fault);
                }
            }

            var state = report.State;
            var destination = records.GetSpan(StepRecord.OverheadLength + state.Length);
            StepRecord.Write(report.Step, state, report.Hash, destination, out var written);
            records.Advance(written);
        }

        return records.WrittenSpan.ToArray();
    }
}
