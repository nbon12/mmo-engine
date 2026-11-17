using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Unicode;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Batch;

/// <summary>
/// The batched stepper the browser build calls (FR-025,
/// specs/015-shared-simulation-core/contracts/wasm-api.md#batchstepper): it owns one
/// <see cref="SimWorld"/>, a command buffer of <see cref="CommandCapacity"/> 17-byte input commands and a
/// report buffer, and <see cref="StepQueued"/> steps the world once per queued command, in order, writing
/// each committed step's record to the report. Only bytes cross the JavaScript boundary, so every call
/// returns a code and leaves its detail in the report buffer (#report-buffer-layouts, #return-codes).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LoadConfig"/> is the browser configuration guard (FR-029,
/// contracts/config-rules.md#browser-guard): decode the binary block, run <see cref="ConfigGuard.Check"/>,
/// compile. Every load call discards the world, and a failed one also unloads what it was loading, so
/// <see cref="StepQueued"/> returns <see cref="StepError.NotReady"/> until the next <see cref="Reset"/>
/// returns <see cref="LoadResult.Ok"/>. This fails closed.
/// </para>
/// <para>
/// After construction and the load calls, <see cref="StepQueued"/> allocates nothing and throws nothing on
/// its committed path (FR-032): it decodes commands, steps the world and copies records into buffers it
/// already holds. Only an early stop formats text. Queued commands give the same records, byte for byte, as
/// the same commands stepped one call at a time (US2 scenario 6), because both are this one loop over
/// <see cref="SimWorld.Step"/>.
/// </para>
/// </remarks>
public sealed class BatchStepper
{
    /// <summary>The largest command capacity.</summary>
    public const int MaxCommandCapacity = 256;

    /// <summary>The report buffer a new stepper allocates, and the least <see cref="LoadConfig"/> sizes it to: 64 KiB.</summary>
    private const int MinimumReportLength = 65536;

    /// <summary>The <c>StepQueued</c> report header: <c>u32</c> record count and <c>u32</c> records length.</summary>
    private const int ReportHeaderLength = 8;

    /// <summary>A load call's report: the <c>u32</c> text length before the text.</summary>
    private const int TextLengthLength = 4;

    /// <summary>A stop entry's <c>i32</c> code, <c>u32</c> step and <c>u32</c> text length.</summary>
    private const int StopEntryHeadLength = 12;

    /// <summary>The longest stop-entry text, in bytes.</summary>
    private const int MaxStopTextLength = 1024;

    /// <summary>The command index <see cref="Run"/> is given when no field is injected.</summary>
    private const int NoInjection = -1;

    private readonly byte[] _commands;
    private readonly CharacterCommand[] _command;
    private byte[] _report;
    private CollisionWorld? _collision;
    private CompiledConfig? _config;

    // The world built for the current collision world and configuration, kept across Resets so a Reset
    // after the first allocates none of its buffers; every load call drops it. _world is the same object
    // while a Reset's state is loaded, and null otherwise.
    private SimWorld? _built;
    private SimWorld? _world;

    /// <summary>
    /// A stepper with room for <paramref name="commandCapacity"/> queued commands. It allocates the
    /// <c>commandCapacity × 17</c>-byte command buffer, which is never reallocated, a 65,536-byte report
    /// buffer, and the one-element command it hands the world. Nothing is loaded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="commandCapacity"/> is outside [1, 256].</exception>
    public BatchStepper(int commandCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commandCapacity, MaxCommandCapacity);
        CommandCapacity = commandCapacity;
        _commands = new byte[commandCapacity * InputCodec.EncodedSize];
        _report = new byte[MinimumReportLength];
        _command = new CharacterCommand[1];
    }

    /// <summary>The most commands one <see cref="StepQueued"/> call steps.</summary>
    public int CommandCapacity { get; }

    /// <summary>The command buffer: <see cref="CommandCapacity"/> × 17 bytes, command <c>i</c> at <c>i × 17</c>.</summary>
    public ArraySegment<byte> CommandSegment => new(_commands);

    /// <summary>
    /// The report buffer, valid until the next call on the stepper. <see cref="LoadConfig"/> may replace it
    /// with a larger one, so a caller reads it again after each load call.
    /// </summary>
    public ArraySegment<byte> ReportSegment => new(_report);

    /// <summary>
    /// Decodes an <c>MMCD</c> file (<see cref="CollisionFile.Decode"/>) and keeps its collision world. Returns
    /// <see cref="LoadResult.Ok"/>, or <see cref="LoadResult.FormatRefused"/> with the format error's
    /// message, and then no collision world is loaded. Either way the world is discarded.
    /// </summary>
    public LoadResult LoadCollision(ReadOnlySpan<byte> collisionFile)
    {
        _world = null;
        _built = null;
        _collision = null;
        CollisionWorld collision;
        try
        {
            collision = CollisionFile.Decode(collisionFile);
        }
        catch (CoreFormatException refusal)
        {
            return Reported(LoadResult.FormatRefused, refusal.Message);
        }

        _collision = collision;
        return Accepted();
    }

    /// <summary>
    /// The browser guard (contracts/config-rules.md#browser-guard): decodes the standalone config block
    /// (<see cref="ConfigBlock.Decode(ReadOnlySpan{byte})"/>), runs <see cref="ConfigGuard.Check"/> and
    /// compiles it. Returns <see cref="LoadResult.FormatRefused"/> with the decode error's message,
    /// <see cref="LoadResult.ConfigInvalid"/> with every guard error as a <c>path: message</c> line in
    /// rule-table order joined by <c>\n</c>, or <see cref="LoadResult.Ok"/>; after a refusal no
    /// configuration is loaded. On <see cref="LoadResult.Ok"/> the report buffer grows to
    /// <c>max(65,536, 8 + CommandCapacity × MaxStepRecordLength + 12 + 1,024)</c> when that is larger than
    /// it is. Either way the world is discarded.
    /// </summary>
    public LoadResult LoadConfig(ReadOnlySpan<byte> configBlock)
    {
        _world = null;
        _built = null;
        _config = null;
        SimConfig decoded;
        try
        {
            decoded = ConfigBlock.Decode(configBlock);
        }
        catch (CoreFormatException refusal)
        {
            return Reported(LoadResult.FormatRefused, refusal.Message);
        }

        var errors = ConfigGuard.Check(decoded);
        if (errors.Count != 0)
        {
            return Reported(LoadResult.ConfigInvalid, Lines(errors));
        }

        var config = CompiledConfig.Create(decoded);
        GrowReport(config.MaxStepRecordLength);
        _config = config;
        return Accepted();
    }

    /// <summary>
    /// Creates the world at <paramref name="initialState"/>, a canonical state with no events, with
    /// <paramref name="matchSalt"/>: <see cref="SimWorld.Reset(ReadOnlySpan{byte}, ulong)"/> on the
    /// <see cref="SimWorld"/> over the loaded collision world and configuration. That world is constructed
    /// by the first Reset after the load calls and reused by later ones, because a Reset loads the whole
    /// state and a new world would allocate every buffer again. Returns <see cref="LoadResult.NotReady"/>
    /// with text length 0 unless both are loaded, <see cref="LoadResult.FormatRefused"/> with the reset's
    /// message when the state is malformed or does not fit the configuration, or
    /// <see cref="LoadResult.Ok"/>. Only <see cref="LoadResult.Ok"/> leaves a world to step.
    /// </summary>
    public LoadResult Reset(ReadOnlySpan<byte> initialState, ulong matchSalt)
    {
        _world = null;
        if (_collision is null || _config is null)
        {
            return Reported(LoadResult.NotReady, string.Empty);
        }

        var world = _built ??= new SimWorld(_config, _collision);
        try
        {
            world.Reset(initialState, matchSalt);
        }
        catch (CoreFormatException refusal)
        {
            return Reported(LoadResult.FormatRefused, refusal.Message);
        }

        _world = world;
        return Accepted();
    }

    /// <summary>
    /// Steps the world once for each of the first <paramref name="count"/> queued commands, in order
    /// (contracts/wasm-api.md#batchstepper). A count outside [0, <see cref="CommandCapacity"/>] returns
    /// <see cref="StepError.CountOutOfRange"/>, checked first; no world returns
    /// <see cref="StepError.NotReady"/>; both write a zero header and run no step. Otherwise each command is
    /// decoded (<see cref="InputCodec.TryDecode"/>) and applied to the state's first character, every other
    /// character getting the neutral input, and each committed step's record is written after the 8-byte
    /// header. A command that does not decode stops the batch with <see cref="StepError.CommandRefused"/>, and
    /// a step fault with <see cref="StepError.StepFault"/>; the stop entry follows the records. Returns the
    /// number of steps that committed.
    /// </summary>
    public int StepQueued(int count) => Run(count, NoInjection, 0, 0, 0);

    /// <summary>
    /// <see cref="StepQueued"/> with <paramref name="bits"/> written into one field of the next state when
    /// command <paramref name="commandIndex"/> steps, through <see cref="SimWorld.StepWithInjectedBits"/>:
    /// the tests' way to reach the stop path with a fault no command produces. It shares the same loop.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The field is not a character, weapon or projectile field of a present element.</exception>
    internal int StepQueuedWithInjectedBits(int count, int commandIndex, int fieldOrdinal, int elementIndex, uint bits) =>
        Run(count, commandIndex, fieldOrdinal, elementIndex, bits);

    /// <summary>The <see cref="StepQueued"/> loop; <paramref name="injectAt"/> is the command whose step gets the injected bits, or −1.</summary>
    private int Run(int count, int injectAt, int fieldOrdinal, int elementIndex, uint bits)
    {
        var report = _report.AsSpan();
        if (count < 0 || count > CommandCapacity)
        {
            report[..ReportHeaderLength].Clear();
            return (int)StepError.CountOutOfRange;
        }

        var world = _world;
        if (world is null)
        {
            report[..ReportHeaderLength].Clear();
            return (int)StepError.NotReady;
        }

        // The report buffer holds CommandCapacity records of the configured maximum length after the header,
        // and the world's state never exceeds that maximum, so every committed record fits.
        var offset = ReportHeaderLength;
        for (var i = 0; i < count; i++)
        {
            var source = new ReadOnlySpan<byte>(_commands, i * InputCodec.EncodedSize, InputCodec.EncodedSize);
            if (!InputCodec.TryDecode(source, out var command, out var error))
            {
                return Stopped(i, offset, StepError.CommandRefused, unchecked(world.CurrentStep + 1u), error.ToString());
            }

            _command[0] = new CharacterCommand(0, command);
            var stepped = i == injectAt
                ? world.StepWithInjectedBits(_command, fieldOrdinal, elementIndex, bits)
                : world.Step(_command);
            if (stepped.Fault.Code != StepFaultCode.None)
            {
                return Stopped(i, offset, StepError.StepFault, unchecked(world.CurrentStep + 1u), StepFaultException.Describe(stepped.Fault));
            }

            StepRecord.Write(stepped.Step, stepped.State, stepped.Hash, report[offset..], out var written);
            offset += written;
        }

        WriteHeader(report, count, offset);
        return count;
    }

    /// <summary>
    /// Ends a batch early after <paramref name="committed"/> steps: the header, then the stop entry at
    /// 8 + L (<c>i32</c> code, <c>u32</c> the step that did not commit, <c>u32</c> text length at most 1,024,
    /// the UTF-8 text). Returns <paramref name="committed"/>.
    /// </summary>
    private int Stopped(int committed, int offset, StepError code, uint step, string text)
    {
        var report = _report.AsSpan();
        WriteHeader(report, committed, offset);
        var stop = report[offset..];
        BinaryPrimitives.WriteInt32LittleEndian(stop, (int)code);
        BinaryPrimitives.WriteUInt32LittleEndian(stop[4..], step);
        WriteText(stop[8..], text, MaxStopTextLength);
        return committed;
    }

    private static void WriteHeader(Span<byte> report, int count, int end)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(report, (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(report[4..], (uint)(end - ReportHeaderLength));
    }

    /// <summary>A load call that succeeded: text length 0.</summary>
    private LoadResult Accepted() => Reported(LoadResult.Ok, string.Empty);

    /// <summary>A load call's result with <paramref name="text"/> in the report: its <c>u32</c> byte length at 0, the UTF-8 at 4.</summary>
    private LoadResult Reported(LoadResult result, string text)
    {
        WriteText(_report, text, _report.Length - TextLengthLength);
        return result;
    }

    /// <summary>
    /// Writes <paramref name="text"/> as UTF-8 after a <c>u32</c> byte length at the start of
    /// <paramref name="destination"/>, keeping at most <paramref name="maxBytes"/> bytes of whole characters.
    /// </summary>
    private static void WriteText(Span<byte> destination, string text, int maxBytes)
    {
        var room = destination.Slice(TextLengthLength, System.Math.Min(maxBytes, destination.Length - TextLengthLength));
        Utf8.FromUtf16(text, room, out _, out var written);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)written);
    }

    /// <summary>The guard's errors, one <c>path: message</c> line each, joined by <c>\n</c>.</summary>
    private static string Lines(IReadOnlyList<ConfigError> errors)
    {
        var text = new StringBuilder();
        for (var i = 0; i < errors.Count; i++)
        {
            if (i != 0)
            {
                text.Append('\n');
            }

            text.Append(errors[i].Path).Append(": ").Append(errors[i].Message);
        }

        return text.ToString();
    }

    /// <summary>
    /// Grows the report buffer to <c>max(65,536, 8 + CommandCapacity × maxStepRecordLength + 12 + 1,024)</c>
    /// when that is larger than it is; it never shrinks.
    /// </summary>
    private void GrowReport(int maxStepRecordLength)
    {
        var needed = checked((int)(ReportHeaderLength + ((long)CommandCapacity * maxStepRecordLength) + StopEntryHeadLength + MaxStopTextLength));
        if (needed > _report.Length)
        {
            _report = new byte[needed];
        }
    }
}
