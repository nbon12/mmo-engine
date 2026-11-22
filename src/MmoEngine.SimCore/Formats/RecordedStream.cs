using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// One step of a recorded stream: its step number, its commands (character index ascending, at most one
/// per character) and the state hash expected after it (FR-022).
/// </summary>
public sealed record RecordedStep(uint Step, RecordedCommand[] Commands, ulong ExpectedHash);

/// <summary>
/// One recorded input command: the character's index in the world's character list (ascending character
/// ID) and the command as decoded, not validated.
/// </summary>
public readonly record struct RecordedCommand(byte CharacterIndex, InputCommand Command);

/// <summary>
/// A recorded stream, <c>MMRS</c> format version 1 (FR-022,
/// specs/015-shared-simulation-core/contracts/recorded-stream-format.md): one case of the agreement suite,
/// with the rules revision, the step rate, the seed, the match salt, the collision data's checksum, the
/// configuration, the initial state, and every step's commands and expected hash. Decoding and encoding are
/// not on the tick path and may allocate. A stream is immutable once built.
/// </summary>
public sealed class RecordedStream
{
    /// <summary>The only version this decoder reads and the encoder writes.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>The header's length in bytes.</summary>
    public const int HeaderLength = 64;

    /// <summary>The largest stream, 64 MiB, trailer included.</summary>
    public const long MaxFileLength = 64L * 1024 * 1024;

    /// <summary>The most step entries a stream holds, 2²⁰.</summary>
    private const uint MaxStepCount = 1u << 20;

    /// <summary>The longest config block, 64 KiB.</summary>
    private const uint MaxConfigLength = 65_536;

    /// <summary>The longest initial state, 1 MiB.</summary>
    private const uint MaxInitialStateLength = 1u << 20;

    /// <summary>The most characters a stream holds.</summary>
    private const ushort MaxCharacterCount = 64;

    /// <summary>The step rate's range, in Hz.</summary>
    private const ushort MinStepHz = 20;
    private const ushort MaxStepHz = 60;

    private const string Format = "stream";
    private const int TrailerLength = 8;
    private const int MagicLength = 4;
    private const int VersionOffset = 4;
    private const int HeaderLengthOffset = 6;
    private const int RulesRevisionOffset = 8;
    private const int StepHzOffset = 12;
    private const int CharacterCountOffset = 14;
    private const int StepCountOffset = 16;
    private const int SeedOffset = 20;
    private const int MatchSaltOffset = 28;
    private const int CollisionChecksumOffset = 36;
    private const int ConfigLengthOffset = 44;
    private const int InitialStateLengthOffset = 48;
    private const int StepsLengthOffset = 52;
    private const int FileLengthOffset = 56;

    /// <summary>A step entry's <c>u32 step</c> and <c>u8 commandCount</c>.</summary>
    private const int StepEntryHeadLength = 5;

    /// <summary>A command's <c>u8 characterIndex</c> and its 17-byte input command.</summary>
    private const int CommandLength = 1 + InputCodec.EncodedSize;

    /// <summary>A step entry's <c>u64 expectedHash</c>.</summary>
    private const int ExpectedHashLength = 8;

    private const int StateCharacterCountOffset = 4;

    private readonly SimConfig _config;
    private readonly byte[] _initialState;

    /// <summary>
    /// A stream from its parts. <see cref="CharacterCount"/> comes from the initial state's header (0 when
    /// the state is shorter than its header, which <see cref="Encode"/> then refuses). The configuration and
    /// the initial state are copied, so later changes to the arguments do not reach the stream.
    /// </summary>
    /// <exception cref="ArgumentNullException">The configuration, the steps, or a step is null.</exception>
    public RecordedStream(
        uint rulesRevision,
        int stepHz,
        ulong seed,
        ulong matchSalt,
        ulong collisionChecksum,
        SimConfig config,
        ReadOnlyMemory<byte> initialState,
        IReadOnlyList<RecordedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(steps);
        var copied = ImmutableArray.CreateBuilder<RecordedStep>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i] ?? throw new ArgumentNullException(nameof(steps), "A recorded stream's steps are not null.");
            ArgumentNullException.ThrowIfNull(step.Commands, nameof(steps));
            copied.Add(step);
        }

        RulesRevision = rulesRevision;
        StepHz = stepHz;
        Seed = seed;
        MatchSalt = matchSalt;
        CollisionChecksum = collisionChecksum;
        _config = config.Clone();
        _initialState = initialState.ToArray();
        CharacterCount = _initialState.Length >= StateCharacterCountOffset + 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(_initialState.AsSpan(StateCharacterCountOffset))
            : 0;
        Steps = copied.MoveToImmutable();
    }

    /// <summary>The file's first four bytes, ASCII <c>MMRS</c>.</summary>
    public static ReadOnlySpan<byte> Magic => "MMRS"u8;

    /// <summary>The <c>SimCoreVersion.RulesRevision</c> the stream was recorded with.</summary>
    public uint RulesRevision { get; }

    /// <summary>The step rate, equal to the configuration's <c>stepRateHz</c>.</summary>
    public int StepHz { get; }

    /// <summary>The number of characters, from the initial state's header.</summary>
    public int CharacterCount { get; }

    /// <summary>The seed the stream's commands were generated from; 0 for a scenario with no seed.</summary>
    public ulong Seed { get; }

    /// <summary>The match salt given to the world with the initial state.</summary>
    public ulong MatchSalt { get; }

    /// <summary>The trailer checksum of the collision data the stream was recorded against.</summary>
    public ulong CollisionChecksum { get; }

    /// <summary>The configuration. Each read returns a new deep copy, so no caller can change the stream's.</summary>
    public SimConfig Config => _config.Clone();

    /// <summary>The initial state's canonical bytes, with no events.</summary>
    public ReadOnlyMemory<byte> InitialState => _initialState;

    /// <summary>The steps, in order.</summary>
    public IReadOnlyList<RecordedStep> Steps { get; }

    /// <summary>
    /// Decodes a stream. It runs the checks of contracts/recorded-stream-format.md#decoding in order and
    /// throws at the first failure: 1 to 4, the frame (magic, version before any length, header length, the
    /// actual length against <c>fileLength</c>); 5, the header limits (<c>fileLength</c>,
    /// <c>configLength</c>, <c>initialStateLength</c>, <c>characterCount</c>, <c>stepCount</c>); 6, the
    /// block lengths against <c>fileLength</c> in checked <c>u64</c>; 7, the checksum; 8, <c>stepHz</c> in
    /// [20, 60]; 9, the config block, with format <c>stream</c>, base 64 and the prefix <c>config.</c>; 10,
    /// <c>stepHz</c> against the config's <c>stepRateHz</c>; 11, the initial state's byte-only checks with
    /// base <c>64 + configLength</c> and the prefix <c>initialState.</c>, then its <c>characterCount</c>
    /// against the header's; 12, each step entry in order: its number against the initial state's step S₀
    /// (S₀ + i + 1), its <c>commandCount</c> against <c>characterCount</c>, each command's
    /// <c>characterIndex</c> (below <c>characterCount</c>, strictly ascending) and then its input through
    /// <see cref="InputCodec.TryDecode"/>, whose refusal is re-reported with format <c>stream</c>, its file
    /// offset and the field <c>steps[i].commands[c].input.&lt;field&gt;</c>; and the entries fill the block
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Checks 1 to 6 read only the header, with <see cref="BinaryPrimitives"/>, and allocate nothing, so a
    /// hostile header cannot force a large allocation or make the decoder hash past the end. After check 6
    /// every block lies inside the content, so the later checks slice it directly. The step list grows with
    /// the entries actually present and is never sized from <c>stepCount</c>, and no stream is constructed
    /// before every check passes, so the decoder never returns a partial stream.
    /// </remarks>
    /// <exception cref="CoreFormatException">The stream is refused.</exception>
    public static RecordedStream Decode(ReadOnlySpan<byte> bytes)
    {
        var header = CheckFrame(bytes);
        var content = bytes[..(bytes.Length - TrailerLength)];

        // 8: the step rate.
        if (header.StepHz is < MinStepHz or > MaxStepHz)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.OutOfRange,
                StepHzOffset,
                "stepHz",
                string.Create(CultureInfo.InvariantCulture, $"{header.StepHz} is outside [{MinStepHz}, {MaxStepHz}]")));
        }

        // 9: the config block.
        var configStart = HeaderLength;
        var config = ConfigBlock.Decode(content.Slice(configStart, header.ConfigLength), Format, configStart, "config.");

        // 10: the step rate against the config's.
        if (config.StepRateHz != header.StepHz)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                StepHzOffset,
                "stepHz",
                string.Create(CultureInfo.InvariantCulture, $"{header.StepHz} is not the config's stepRateHz {config.StepRateHz}")));
        }

        // 11: the initial state's byte-only checks, then its character count against the header's.
        var stateStart = configStart + header.ConfigLength;
        var stateBytes = content.Slice(stateStart, header.InitialStateLength);
        var initialState = StateSerializer.ReadLoaded(stateBytes, StateErrorContext.StreamInitialState(stateStart));
        var stateCharacterCount = BinaryPrimitives.ReadUInt16LittleEndian(stateBytes[StateCharacterCountOffset..]);
        if (stateCharacterCount != header.CharacterCount)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                stateStart + StateCharacterCountOffset,
                "initialState.characterCount",
                string.Create(CultureInfo.InvariantCulture, $"{stateCharacterCount} is not the header's characterCount {header.CharacterCount}")));
        }

        // 12: the step entries.
        var stepsStart = stateStart + header.InitialStateLength;
        var steps = ParseSteps(content.Slice(stepsStart, header.StepsLength), stepsStart, header.StepCount, initialState.Step, header.CharacterCount);

        return new RecordedStream(header.RulesRevision, header.StepHz, header.Seed, header.MatchSalt, header.CollisionChecksum, config, stateBytes.ToArray(), steps);
    }

    /// <summary>
    /// Writes the stream: the header, with the block lengths and <c>fileLength</c> computed, the config
    /// block, the initial state, the step entries and the trailer. It then decodes its own output, so it
    /// throws exactly what <see cref="Decode"/> would.
    /// </summary>
    /// <exception cref="ConfigValidationException">The configuration breaks a shape rule, so its block cannot be written.</exception>
    /// <exception cref="InvalidOperationException">A value does not fit its field: the step rate, a step's command count, or the file's length.</exception>
    /// <exception cref="CoreFormatException">The decoder refuses the written stream.</exception>
    public byte[] Encode()
    {
        if (StepHz is < 0 or > ushort.MaxValue)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The step rate {StepHz} does not fit the u16 stepHz field."));
        }

        var configBlock = ConfigBlock.Encode(_config);
        long stepsLength = 0;
        foreach (var step in Steps)
        {
            if (step.Commands.Length > byte.MaxValue)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Step {step.Step} has {step.Commands.Length} commands; the u8 commandCount holds at most 255."));
            }

            stepsLength += StepEntryHeadLength + (CommandLength * (long)step.Commands.Length) + ExpectedHashLength;
        }

        var fileLength = HeaderLength + (long)configBlock.Length + _initialState.Length + stepsLength + TrailerLength;
        if (fileLength > Array.MaxLength)
        {
            throw new InvalidOperationException("The stream would not fit in an array.");
        }

        var file = new byte[fileLength];
        var span = file.AsSpan();
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[VersionOffset..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span[HeaderLengthOffset..], HeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[RulesRevisionOffset..], RulesRevision);
        BinaryPrimitives.WriteUInt16LittleEndian(span[StepHzOffset..], (ushort)StepHz);
        BinaryPrimitives.WriteUInt16LittleEndian(span[CharacterCountOffset..], (ushort)CharacterCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[StepCountOffset..], (uint)Steps.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(span[SeedOffset..], Seed);
        BinaryPrimitives.WriteUInt64LittleEndian(span[MatchSaltOffset..], MatchSalt);
        BinaryPrimitives.WriteUInt64LittleEndian(span[CollisionChecksumOffset..], CollisionChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ConfigLengthOffset..], (uint)configBlock.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[InitialStateLengthOffset..], (uint)_initialState.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[StepsLengthOffset..], (uint)stepsLength);
        BinaryPrimitives.WriteUInt64LittleEndian(span[FileLengthOffset..], (ulong)fileLength);

        var offset = HeaderLength;
        configBlock.CopyTo(span[offset..]);
        offset += configBlock.Length;
        _initialState.CopyTo(span[offset..]);
        offset += _initialState.Length;

        foreach (var step in Steps)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], step.Step);
            span[offset + 4] = (byte)step.Commands.Length;
            offset += StepEntryHeadLength;
            foreach (var command in step.Commands)
            {
                span[offset] = command.CharacterIndex;
                InputCodec.TryEncode(command.Command, span[(offset + 1)..]);
                offset += CommandLength;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], step.ExpectedHash);
            offset += ExpectedHashLength;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(span[offset..], XxHash64.HashToUInt64(span[..offset], 0));
        Decode(file);
        return file;
    }

    /// <summary>
    /// Checks 1 to 7 of #decoding: the magic, the version (before any length, so another version is refused
    /// as such whatever its length), the header length, the actual length against <c>fileLength</c>, the
    /// header limits, the block lengths against <c>fileLength</c>, and the trailer. Checks 1 to 6 read only
    /// the header, so a file whose blocks disagree with its length is refused before it is hashed. Returns the
    /// header's values; the blocks then fill the file exactly.
    /// </summary>
    private static Header CheckFrame(ReadOnlySpan<byte> bytes)
    {
        var length = (ulong)bytes.Length;

        // 1: the magic.
        if (bytes.Length < MagicLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "magic", FormatDetail.ExpectedLength, MagicLength, length);
        }

        if (!bytes[..MagicLength].SequenceEqual(Magic))
        {
            throw new CoreFormatException(new FormatError(Format, FormatErrorCode.UnknownMagic, 0, "magic", FrameText.WrongMagic(bytes[..MagicLength], Magic, "MMRS")));
        }

        // 2: the version.
        if (bytes.Length < HeaderLengthOffset)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "version", FormatDetail.ExpectedLength, HeaderLengthOffset, length);
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[VersionOffset..]);
        if (version != FormatVersion)
        {
            throw Error(FormatErrorCode.UnsupportedVersion, VersionOffset, "version", FormatDetail.UnsupportedVersion, version, FormatVersion);
        }

        // 3: the header length, then the whole header.
        if (bytes.Length < RulesRevisionOffset)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "headerLength", FormatDetail.ExpectedLength, RulesRevisionOffset, length);
        }

        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[HeaderLengthOffset..]);
        if (headerLength != HeaderLength)
        {
            throw new CoreFormatException(new FormatError(Format, FormatErrorCode.Inconsistent, HeaderLengthOffset, "headerLength", FrameText.WrongHeaderLength(headerLength, HeaderLength)));
        }

        if (bytes.Length < HeaderLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "header", FormatDetail.ExpectedLength, HeaderLength, length);
        }

        // 4: the actual length against fileLength.
        var fileLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes[FileLengthOffset..]);
        if (length < fileLength)
        {
            throw Error(FormatErrorCode.Truncated, bytes.Length, "fileLength", FormatDetail.DeclaredLength, fileLength, length);
        }

        if (length > fileLength)
        {
            throw Error(FormatErrorCode.Oversized, (long)fileLength, "fileLength", FormatDetail.DeclaredLength, fileLength, length);
        }

        // 5: the header limits, in the contract's order: fileLength, configLength, initialStateLength,
        // characterCount, stepCount.
        if (fileLength > MaxFileLength)
        {
            throw Above(FileLengthOffset, "fileLength", fileLength, MaxFileLength);
        }

        var configLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[ConfigLengthOffset..]);
        if (configLength > MaxConfigLength)
        {
            throw Above(ConfigLengthOffset, "configLength", configLength, MaxConfigLength);
        }

        var initialStateLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[InitialStateLengthOffset..]);
        if (initialStateLength > MaxInitialStateLength)
        {
            throw Above(InitialStateLengthOffset, "initialStateLength", initialStateLength, MaxInitialStateLength);
        }

        var characterCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[CharacterCountOffset..]);
        CheckCount(CharacterCountOffset, "characterCount", characterCount, MaxCharacterCount);
        var stepCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[StepCountOffset..]);
        CheckCount(StepCountOffset, "stepCount", stepCount, MaxStepCount);

        // 6: the blocks fill the file exactly, summed in checked u64.
        var stepsLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[StepsLengthOffset..]);
        var blocksNeed = checked((ulong)HeaderLength + configLength + initialStateLength + stepsLength + TrailerLength);
        if (blocksNeed != fileLength)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                FileLengthOffset,
                "fileLength",
                string.Create(CultureInfo.InvariantCulture, $"the blocks need {blocksNeed} bytes; fileLength is {fileLength}")));
        }

        // 7: the trailer. fileLength equals the length, which is at least the 64-byte header.
        var trailerOffset = bytes.Length - TrailerLength;
        var trailer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[trailerOffset..]);
        var content = XxHash64.HashToUInt64(bytes[..trailerOffset], 0);
        if (trailer != content)
        {
            throw Error(FormatErrorCode.ChecksumMismatch, trailerOffset, "checksum", FormatDetail.ChecksumMismatch, trailer, content);
        }

        // Check 5 bounds the block lengths and check 6 makes them fill the file, so each fits an int.
        return new Header(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[RulesRevisionOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[StepHzOffset..]),
            characterCount,
            stepCount,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[SeedOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[MatchSaltOffset..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[CollisionChecksumOffset..]),
            (int)configLength,
            (int)initialStateLength,
            (int)stepsLength);
    }

    /// <summary>Check 5 for <c>characterCount</c> or <c>stepCount</c>: 0 is <c>OutOfRange</c>, above the limit <c>Oversized</c>.</summary>
    private static void CheckCount(int offset, string field, uint count, uint max)
    {
        if (count == 0)
        {
            throw new CoreFormatException(new FormatError(Format, FormatErrorCode.OutOfRange, offset, field, "0 is below 1"));
        }

        if (count > max)
        {
            throw Above(offset, field, count, max);
        }
    }

    /// <summary>
    /// Check 12: reads <paramref name="stepCount"/> step entries from the steps block, each in order.
    /// <list type="bullet">
    /// <item><description>Entry i's step number must be <paramref name="initialStep"/> + i + 1, compared in
    /// <c>u64</c> so no sum wraps (<see cref="FormatErrorCode.Inconsistent"/> at <c>steps[i].step</c>).</description></item>
    /// <item><description>Its <c>commandCount</c> is at most <paramref name="characterCount"/>
    /// (<see cref="FormatErrorCode.Inconsistent"/>).</description></item>
    /// <item><description>Each command's <c>characterIndex</c> is below <paramref name="characterCount"/>
    /// (<see cref="FormatErrorCode.OutOfRange"/>) and above the one before it
    /// (<see cref="FormatErrorCode.Inconsistent"/>), checked before its input is decoded.</description></item>
    /// <item><description>A field that runs past the block is <see cref="FormatErrorCode.Inconsistent"/> at the
    /// field being read, and bytes left after the last entry are <see cref="FormatErrorCode.Inconsistent"/> at
    /// the first of them, field <c>steps</c>.</description></item>
    /// </list>
    /// The list grows with the entries actually present, so a large <c>stepCount</c> cannot force a large
    /// allocation, and a field's path is formatted only for a refusal.
    /// </summary>
    private static List<RecordedStep> ParseSteps(ReadOnlySpan<byte> block, long blockOffset, uint stepCount, uint initialStep, int characterCount)
    {
        var steps = new List<RecordedStep>();
        var position = 0;
        for (var i = 0u; i < stepCount; i++)
        {
            Need(block, position, 4, blockOffset, i, -1, "step");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
            var expectedStep = (ulong)initialStep + i + 1UL;
            if (step != expectedStep)
            {
                throw StepError(
                    FormatErrorCode.Inconsistent,
                    blockOffset + position,
                    i,
                    -1,
                    "step",
                    string.Create(CultureInfo.InvariantCulture, $"{step} is not {expectedStep}, the initial state's step {initialStep} plus {i + 1UL}"));
            }

            position += 4;
            Need(block, position, 1, blockOffset, i, -1, "commandCount");
            var commandCount = block[position];
            if (commandCount > characterCount)
            {
                throw StepError(
                    FormatErrorCode.Inconsistent,
                    blockOffset + position,
                    i,
                    -1,
                    "commandCount",
                    string.Create(CultureInfo.InvariantCulture, $"{commandCount} is above characterCount {characterCount}"));
            }

            position += 1;
            var commands = new RecordedCommand[commandCount];
            for (var c = 0; c < commandCount; c++)
            {
                Need(block, position, 1, blockOffset, i, c, "characterIndex");
                var characterIndex = block[position];
                if (characterIndex >= characterCount)
                {
                    throw StepError(
                        FormatErrorCode.OutOfRange,
                        blockOffset + position,
                        i,
                        c,
                        "characterIndex",
                        string.Create(CultureInfo.InvariantCulture, $"character index {characterIndex} is not below characterCount {characterCount}"));
                }

                if (c > 0 && characterIndex <= commands[c - 1].CharacterIndex)
                {
                    throw StepError(
                        FormatErrorCode.Inconsistent,
                        blockOffset + position,
                        i,
                        c,
                        "characterIndex",
                        string.Create(CultureInfo.InvariantCulture, $"character index {characterIndex} is not above {commands[c - 1].CharacterIndex}, the index before it"));
                }

                position += 1;
                Need(block, position, InputCodec.EncodedSize, blockOffset, i, c, "input");
                var inputOffset = blockOffset + position;
                if (!InputCodec.TryDecode(block.Slice(position, InputCodec.EncodedSize), out var command, out var error))
                {
                    throw StepError(error.Code, inputOffset + error.Offset, i, c, "input." + error.Field, error.Detail);
                }

                position += InputCodec.EncodedSize;
                commands[c] = new RecordedCommand(characterIndex, command);
            }

            Need(block, position, ExpectedHashLength, blockOffset, i, -1, "expectedHash");
            var expectedHash = BinaryPrimitives.ReadUInt64LittleEndian(block[position..]);
            position += ExpectedHashLength;
            steps.Add(new RecordedStep(step, commands, expectedHash));
        }

        if (position != block.Length)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                blockOffset + position,
                "steps",
                string.Create(CultureInfo.InvariantCulture, $"{block.Length - position} bytes are left after the {stepCount} step entries")));
        }

        return steps;
    }

    /// <summary>
    /// Refuses a field that runs past the steps block as <c>Inconsistent</c> at the field being read: field
    /// <paramref name="leaf"/> of step <paramref name="step"/>, or of its command <paramref name="command"/>
    /// when that is not negative.
    /// </summary>
    private static void Need(ReadOnlySpan<byte> block, int position, int size, long blockOffset, uint step, int command, string leaf)
    {
        if (block.Length - position < size)
        {
            throw StepError(
                FormatErrorCode.Inconsistent,
                blockOffset + position,
                step,
                command,
                leaf,
                string.Create(CultureInfo.InvariantCulture, $"the step entry runs past the steps block, which ends at offset {blockOffset + block.Length}"));
        }
    }

    /// <summary>
    /// A refusal inside the steps block, at <c>steps[i].&lt;leaf&gt;</c>, or at
    /// <c>steps[i].commands[c].&lt;leaf&gt;</c> when <paramref name="command"/> is not negative.
    /// </summary>
    private static CoreFormatException StepError(FormatErrorCode code, long offset, uint step, int command, string leaf, string detail)
    {
        var field = command < 0
            ? string.Create(CultureInfo.InvariantCulture, $"steps[{step}].{leaf}")
            : string.Create(CultureInfo.InvariantCulture, $"steps[{step}].commands[{command}].{leaf}");
        return new CoreFormatException(new FormatError(Format, code, offset, field, detail));
    }

    /// <summary>A header count or length above its limit: <c>Oversized</c>, such as <c>65 is above 64</c>.</summary>
    private static CoreFormatException Above(int offset, string field, ulong value, ulong max) =>
        new(new FormatError(Format, FormatErrorCode.Oversized, offset, field, string.Create(CultureInfo.InvariantCulture, $"{value} is above {max}")));

    private static CoreFormatException Error(FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b = 0) =>
        new(new FormatError(Format, code, offset, field, detail, a, b));

    /// <summary>
    /// The header's values once checks 1 to 7 have passed: the block lengths are within check 5's limits
    /// and, with the header and trailer, fill the file exactly.
    /// </summary>
    private readonly record struct Header(
        uint RulesRevision,
        ushort StepHz,
        ushort CharacterCount,
        uint StepCount,
        ulong Seed,
        ulong MatchSalt,
        ulong CollisionChecksum,
        int ConfigLength,
        int InitialStateLength,
        int StepsLength);
}
