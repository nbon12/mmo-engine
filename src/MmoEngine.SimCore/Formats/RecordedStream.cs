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
    /// Decodes a stream. It runs checks 1 to 4 and 7 of contracts/recorded-stream-format.md#decoding in
    /// order (magic, version, header length, file length, checksum); then check 9, the config block, with
    /// format <c>stream</c>, base 64 and the prefix <c>config.</c>; then the byte-only half of check 11, the
    /// initial state, with base <c>64 + configLength</c> and the prefix <c>initialState.</c>; then the step
    /// entries, each command through <see cref="InputCodec.TryDecode"/>, whose refusal is re-reported with
    /// format <c>stream</c>, its file offset and the field <c>steps[i].commands[c].input.&lt;field&gt;</c>.
    /// It throws at the first failure and never returns a partial stream.
    /// </summary>
    /// <remarks>
    /// The header limits, the block lengths against <c>fileLength</c>, <c>stepHz</c>, the header's character
    /// count against the state's, and the rest of the step checks (checks 5, 6, 8, 10, the second half of
    /// 11, and 12's step numbers, command counts, character indices and block fill) are added by Phase 8
    /// (T236). Until then a block that runs past the content is refused as
    /// <see cref="FormatErrorCode.Inconsistent"/> at <c>fileLength</c>, and a step entry that runs past the
    /// steps block as <see cref="FormatErrorCode.Inconsistent"/> at the field being read.
    /// </remarks>
    /// <exception cref="CoreFormatException">The stream is refused.</exception>
    public static RecordedStream Decode(ReadOnlySpan<byte> bytes)
    {
        CheckFrame(bytes);
        var content = bytes[..(bytes.Length - TrailerLength)];

        var rulesRevision = BinaryPrimitives.ReadUInt32LittleEndian(bytes[RulesRevisionOffset..]);
        var stepHz = BinaryPrimitives.ReadUInt16LittleEndian(bytes[StepHzOffset..]);
        var stepCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[StepCountOffset..]);
        var seed = BinaryPrimitives.ReadUInt64LittleEndian(bytes[SeedOffset..]);
        var matchSalt = BinaryPrimitives.ReadUInt64LittleEndian(bytes[MatchSaltOffset..]);
        var collisionChecksum = BinaryPrimitives.ReadUInt64LittleEndian(bytes[CollisionChecksumOffset..]);
        var configLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[ConfigLengthOffset..]);
        var initialStateLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[InitialStateLengthOffset..]);
        var stepsLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[StepsLengthOffset..]);
        var fileLength = (ulong)bytes.Length;
        var blocksNeed = (ulong)HeaderLength + configLength + initialStateLength + stepsLength + TrailerLength;

        // 9: the config block.
        var configStart = (ulong)HeaderLength;
        var configBlock = Block(content, configStart, configLength, blocksNeed, fileLength);
        var config = ConfigBlock.Decode(configBlock, Format, HeaderLength, "config.");

        // 11, byte-only half: the initial state.
        var stateStart = configStart + configLength;
        var stateBytes = Block(content, stateStart, initialStateLength, blocksNeed, fileLength);
        StateSerializer.ReadLoaded(stateBytes, StateErrorContext.StreamInitialState((long)stateStart));

        // 12: the step entries, each command through the input codec.
        var stepsStart = stateStart + initialStateLength;
        var stepsBlock = Block(content, stepsStart, stepsLength, blocksNeed, fileLength);
        var steps = ParseSteps(stepsBlock, (long)stepsStart, stepCount);

        return new RecordedStream(rulesRevision, stepHz, seed, matchSalt, collisionChecksum, config, stateBytes.ToArray(), steps);
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
    /// Checks 1 to 4 and 7 of #decoding: the magic, the version (before any length), the header length, the
    /// actual length against <c>fileLength</c>, and the trailer.
    /// </summary>
    private static void CheckFrame(ReadOnlySpan<byte> bytes)
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

        // 7: the trailer. fileLength equals the length, which is at least the 64-byte header.
        var trailerOffset = bytes.Length - TrailerLength;
        var trailer = BinaryPrimitives.ReadUInt64LittleEndian(bytes[trailerOffset..]);
        var content = XxHash64.HashToUInt64(bytes[..trailerOffset], 0);
        if (trailer != content)
        {
            throw Error(FormatErrorCode.ChecksumMismatch, trailerOffset, "checksum", FormatDetail.ChecksumMismatch, trailer, content);
        }
    }

    /// <summary>A block of the content, refused at <c>fileLength</c> when it runs past the content.</summary>
    private static ReadOnlySpan<byte> Block(ReadOnlySpan<byte> content, ulong start, uint length, ulong blocksNeed, ulong fileLength)
    {
        if (start > (ulong)content.Length || length > (ulong)content.Length - start)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                FileLengthOffset,
                "fileLength",
                string.Create(CultureInfo.InvariantCulture, $"the blocks need {blocksNeed} bytes; fileLength is {fileLength}")));
        }

        return content.Slice((int)start, (int)length);
    }

    /// <summary>
    /// Reads <paramref name="stepCount"/> step entries from the steps block. The list grows with the entries
    /// actually present, so a large <c>stepCount</c> cannot force a large allocation.
    /// </summary>
    private static List<RecordedStep> ParseSteps(ReadOnlySpan<byte> block, long blockOffset, uint stepCount)
    {
        var steps = new List<RecordedStep>();
        var position = 0;
        for (var i = 0u; i < stepCount; i++)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"steps[{i}].");
            Need(block, position, 4, blockOffset, path + "step");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(block[position..]);
            position += 4;
            Need(block, position, 1, blockOffset, path + "commandCount");
            var commandCount = block[position];
            position += 1;

            var commands = new RecordedCommand[commandCount];
            for (var c = 0; c < commandCount; c++)
            {
                var commandPath = string.Create(CultureInfo.InvariantCulture, $"{path}commands[{c}].");
                Need(block, position, 1, blockOffset, commandPath + "characterIndex");
                var characterIndex = block[position];
                position += 1;
                Need(block, position, InputCodec.EncodedSize, blockOffset, commandPath + "input");
                var inputOffset = blockOffset + position;
                if (!InputCodec.TryDecode(block.Slice(position, InputCodec.EncodedSize), out var command, out var error))
                {
                    throw new CoreFormatException(new FormatError(
                        Format,
                        error.Code,
                        inputOffset + error.Offset,
                        string.Concat(commandPath, "input.", error.Field),
                        error.Detail));
                }

                position += InputCodec.EncodedSize;
                commands[c] = new RecordedCommand(characterIndex, command);
            }

            Need(block, position, ExpectedHashLength, blockOffset, path + "expectedHash");
            var expectedHash = BinaryPrimitives.ReadUInt64LittleEndian(block[position..]);
            position += ExpectedHashLength;
            steps.Add(new RecordedStep(step, commands, expectedHash));
        }

        return steps;
    }

    /// <summary>Refuses a field that runs past the steps block as <c>Inconsistent</c> at the field being read.</summary>
    private static void Need(ReadOnlySpan<byte> block, int position, int size, long blockOffset, string field)
    {
        if (block.Length - position < size)
        {
            throw new CoreFormatException(new FormatError(
                Format,
                FormatErrorCode.Inconsistent,
                blockOffset + position,
                field,
                string.Create(CultureInfo.InvariantCulture, $"the step entry runs past the steps block, which ends at offset {blockOffset + block.Length}")));
        }
    }

    private static CoreFormatException Error(FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b = 0) =>
        new(new FormatError(Format, code, offset, field, detail, a, b));
}
