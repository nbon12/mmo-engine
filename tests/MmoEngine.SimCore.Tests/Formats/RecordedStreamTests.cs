using System.Buffers.Binary;
using System.IO.Hashing;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The recorded stream frame <c>MMRS</c> version 1 (FR-022, contracts/recorded-stream-format.md): the
/// layout of the <c>two-steps</c> stream, the round trip, the frame checks 1–4 and 7 in contract order,
/// check 5's <c>stepCount</c> limit and check 12's step numbers, and the errors of the embedded config
/// block, initial state and input commands, reported with format
/// <c>stream</c>, file offsets and the field prefixed by its place in the stream. Every test builds the
/// stream itself: 30 Hz, the placeholder configuration, the worked example state, and two steps.
/// </summary>
public sealed class RecordedStreamTests
{
    /// <summary>The <c>two-steps</c> match salt.</summary>
    private const ulong TwoStepsSalt = 0x0123456789abcdefUL;

    /// <summary>The <c>tiny-world</c> collision fixture's checksum.</summary>
    private const ulong TinyWorldChecksum = 0x9224e3fa3b3fca9cUL;

    private const int FileLength = 950;
    private const int ConfigStart = 64;
    private const int ConfigLength = 698;
    private const int StateStart = 762;
    private const int StateLength = 136;
    private const int StepsStart = 898;
    private const int StepsLength = 44;
    private const int Step1CommandStart = 904;
    private const int Step1HashStart = 921;
    private const int Step2Start = 929;
    private const int TrailerStart = 942;

    /// <summary>The <c>example</c> input command's 17 bytes (contracts/input-command-format.md#example).</summary>
    private const string ExampleCommandHex = "490101000000640000008100004000fc00";

    public static TheoryData<ulong, ulong> SeedsAndSalts => new()
    {
        { 0UL, TwoStepsSalt },
        { 7UL, 0x8000000000000001UL },
    };

    public static TheoryData<string> FrameCases =>
    [
        "3-bytes",
        "unknown-magic",
        "5-bytes",
        "version-2",
        "version-2-truncated-to-6",
        "7-bytes",
        "header-length-72",
        "truncated-to-40",
        "truncated-to-900",
        "4-bytes-appended",
        "checksum-mismatch",
    ];

    public static TheoryData<string> EmbeddedConfigAndStateCases =>
    [
        "config-count-short",
        "config-not-ascending",
        "config-unknown-field",
        "config-missing-field",
        "state-has-events",
        "state-position-x-9000",
    ];

    public static TheoryData<string> EmbeddedInputCases =>
    [
        "command-bad-version",
        "command-unknown-magic",
    ];

    public static TheoryData<string> StepCountAndNumberCases =>
    [
        "zero-steps",
        "step-count-too-many",
        "first-step-number",
        "step-number-gap",
    ];

    /// <summary>Sequence 1, client step 100, Forward and Fire, yaw 16384, pitch −1024, slot 0.</summary>
    private static InputCommand Example => new(1, 100, Buttons.Forward | Buttons.Fire, 16384, -1024, 0);

    /// <summary>
    /// US6 scenario 1 for streams: <c>Encode</c> writes the contract's layout, and <c>Decode</c> gives back
    /// every header field, the configuration's bits, the initial state, the steps, their commands and
    /// expected hashes, and re-encodes to the same bytes. The second row is a generated stream's header:
    /// seed 7 and a match salt with the top bit set.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeedsAndSalts))]
    public void EncodeThenDecodeIsTheIdentity(ulong seed, ulong matchSalt)
    {
        var config = TestConfigs.At(30);
        var stream = TwoSteps(config, seed, matchSalt);

        var bytes = stream.Encode();

        Assert.Equal(FileLength, bytes.Length);
        Assert.Equal("MMRS"u8.ToArray(), bytes[..4]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((ushort)64, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((ushort)30, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(seed, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20)));
        Assert.Equal(matchSalt, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(28)));
        Assert.Equal(TinyWorldChecksum, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(36)));
        Assert.Equal((uint)ConfigLength, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44)));
        Assert.Equal((uint)StateLength, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(48)));
        Assert.Equal((uint)StepsLength, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52)));
        Assert.Equal((ulong)FileLength, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(56)));

        // Blocks: config [64, 762), initial state [762, 898), steps [898, 942), trailer [942, 950).
        Assert.Equal(ConfigBlock.Encode(config), bytes[ConfigStart..StateStart]);
        Assert.Equal(TestStates.WorkedExample, bytes[StateStart..StepsStart]);

        // Step 1: step 1, one command for character index 0, the example command, expected hash 1.
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(StepsStart)));
        Assert.Equal((byte)1, bytes[StepsStart + 4]);
        Assert.Equal((byte)0, bytes[StepsStart + 5]);
        Assert.Equal(Convert.FromHexString(ExampleCommandHex), bytes[Step1CommandStart..Step1HashStart]);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Step1HashStart)));

        // Step 2: step 2, no command, expected hash 2.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Step2Start)));
        Assert.Equal((byte)0, bytes[Step2Start + 4]);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(Step2Start + 5)));

        Assert.Equal(
            XxHash64.HashToUInt64(bytes.AsSpan(0, TrailerStart), 0),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(TrailerStart)));

        var decoded = RecordedStream.Decode(bytes);

        Assert.Equal(1u, decoded.RulesRevision);
        Assert.Equal(30, decoded.StepHz);
        Assert.Equal(1, decoded.CharacterCount);
        Assert.Equal(seed, decoded.Seed);
        Assert.Equal(matchSalt, decoded.MatchSalt);
        Assert.Equal(TinyWorldChecksum, decoded.CollisionChecksum);
        Assert.Equal(ConfigBlock.Encode(config), ConfigBlock.Encode(decoded.Config));
        Assert.Equal(TestStates.WorkedExample, decoded.InitialState.ToArray());

        Assert.Equal(2, decoded.Steps.Count);
        var step1 = decoded.Steps[0];
        Assert.Equal(1u, step1.Step);
        var command = Assert.Single(step1.Commands);
        Assert.Equal((byte)0, command.CharacterIndex);
        Assert.Equal(Example, command.Command);
        Assert.Equal(1UL, step1.ExpectedHash);
        var step2 = decoded.Steps[1];
        Assert.Equal(2u, step2.Step);
        Assert.Empty(step2.Commands);
        Assert.Equal(2UL, step2.ExpectedHash);

        Assert.Equal(bytes, decoded.Encode());
    }

    /// <summary>
    /// US6 scenarios 2 to 4 for the frame: checks 1–4 and 7 of contracts/recorded-stream-format.md#decoding,
    /// in contract order. The version comes before the length, so a version-2 stream cut to 6 bytes is
    /// refused as <c>UnsupportedVersion</c>, and the checksum comes before any value.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrameCases))]
    public void FrameChecksRefuseInContractOrder(string caseName)
    {
        var bytes = TwoSteps(TestConfigs.At(30), 0, TwoStepsSalt).Encode();
        var refusal = caseName switch
        {
            "3-bytes" => new Refusal(bytes[..3], FormatErrorCode.Truncated, 3, "magic"),
            "unknown-magic" => new Refusal(Write(bytes, 0, "4d4d5258"), FormatErrorCode.UnknownMagic, 0, "magic"),
            "5-bytes" => new Refusal(bytes[..5], FormatErrorCode.Truncated, 5, "version"),
            "version-2" => new Refusal(
                Write(bytes, 4, "0200"),
                FormatErrorCode.UnsupportedVersion,
                4,
                "version",
                Message: "stream version at offset 4: unsupported version 2; this reader supports version 1"),
            "version-2-truncated-to-6" => new Refusal(Write(bytes, 4, "0200")[..6], FormatErrorCode.UnsupportedVersion, 4, "version"),
            "7-bytes" => new Refusal(bytes[..7], FormatErrorCode.Truncated, 7, "headerLength"),
            "header-length-72" => new Refusal(Write(bytes, 6, "4800"), FormatErrorCode.Inconsistent, 6, "headerLength"),
            "truncated-to-40" => new Refusal(bytes[..40], FormatErrorCode.Truncated, 40, "header"),
            "truncated-to-900" => new Refusal(bytes[..900], FormatErrorCode.Truncated, 900, "fileLength"),
            "4-bytes-appended" => new Refusal([.. bytes, 0, 0, 0, 0], FormatErrorCode.Oversized, FileLength, "fileLength"),
            "checksum-mismatch" => new Refusal(Write(bytes, Step1HashStart, "ff"), FormatErrorCode.ChecksumMismatch, TrailerStart, "checksum"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        AssertRefused(refusal);
    }

    /// <summary>
    /// Checks 9 and 11 (byte-only half): an error inside the config block or the initial state is the
    /// block decoder's error, re-reported with format <c>stream</c>, the file offset and the field
    /// prefixed by <c>config.</c> or <c>initialState.</c>. Every mutation is followed by
    /// <c>fixChecksum</c>, so the decoder reaches the block.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmbeddedConfigAndStateCases))]
    public void EmbeddedConfigAndStateErrorsCarryTheirPrefix(string caseName)
    {
        var bytes = TwoSteps(TestConfigs.At(30), 0, TwoStepsSalt).Encode();
        var refusal = caseName switch
        {
            // 115 entries in a 698-byte block: 6 bytes more than 2 + 6 · 115.
            "config-count-short" => new Refusal(
                FixChecksum(Write(bytes, ConfigStart, "7300")),
                FormatErrorCode.Oversized,
                756,
                "config.count"),

            // Entries 0 (0x0001, 30) and 1 (0x0002, 64) swapped.
            "config-not-ascending" => new Refusal(
                FixChecksum(Write(Write(bytes, 66, "020040000000"), 72, "01001e000000")),
                FormatErrorCode.Inconsistent,
                72,
                "config.entries[1].fieldId",
                Message: "stream config.entries[1].fieldId at offset 72: field 0x0001 is not above 0x0002"),
            "config-unknown-field" => new Refusal(
                FixChecksum(Write(bytes, 756, "0160")),
                FormatErrorCode.Inconsistent,
                756,
                "config.entries[115].fieldId"),

            // The last entry (input.weaponSlots) deleted, with the count, configLength and fileLength to match.
            "config-missing-field" => new Refusal(
                FixChecksum(Write(Write(Write(Delete(bytes, 756, 6), ConfigStart, "7300"), 44, "b4020000"), 56, "b003000000000000")),
                FormatErrorCode.Inconsistent,
                756,
                "config.input.weaponSlots",
                Message: "stream config.input.weaponSlots at offset 756: field 0x5001 is missing"),

            // One expiry (projectile 1, Range) in the initial state, with initialStateLength and fileLength to match.
            "state-has-events" => new Refusal(
                FixChecksum(Write(Write(Insert(Write(bytes, 890, "0100"), 892, "0100000001"), 48, "8d000000"), 56, "bb03000000000000")),
                FormatErrorCode.Inconsistent,
                890,
                "initialState.events.expiries.count",
                Message: "stream initialState.events.expiries.count at offset 890: a loaded state has no events, found 1"),
            "state-position-x-9000" => new Refusal(
                FixChecksum(Write(bytes, StateStart + 16, TestStates.F32(9000f))),
                FormatErrorCode.OutOfRange,
                778,
                "initialState.characters[0].position.x"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        AssertRefused(refusal);
    }

    /// <summary>
    /// Check 12's per-command decode: a command that fails the input codec is reported with the codec's
    /// code, at its file offset (the command's start plus the codec's offset), with the field prefixed by
    /// the command's place: <c>steps[0].commands[0].input.</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmbeddedInputCases))]
    public void EmbeddedInputErrorsCarryTheirStepAndCommandPath(string caseName)
    {
        var bytes = TwoSteps(TestConfigs.At(30), 0, TwoStepsSalt).Encode();
        var refusal = caseName switch
        {
            "command-bad-version" => new Refusal(
                FixChecksum(Write(bytes, Step1CommandStart + 1, "02")),
                FormatErrorCode.UnsupportedVersion,
                905,
                "steps[0].commands[0].input.version",
                Message: "stream steps[0].commands[0].input.version at offset 905: unsupported version 2; this reader supports version 1"),
            "command-unknown-magic" => new Refusal(
                FixChecksum(Write(bytes, Step1CommandStart, "4a")),
                FormatErrorCode.UnknownMagic,
                904,
                "steps[0].commands[0].input.formatId"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        AssertRefused(refusal);
    }

    /// <summary>
    /// Check 5's <c>stepCount</c> limit and check 12's step numbers (brought forward from T236 in the
    /// Phase 3 review): <c>stepCount</c> 0 is <c>OutOfRange</c> and 1,048,577 is <c>Oversized</c>, both at 16
    /// and before the checksum (check 7), so those rows keep the stale trailer; a step entry whose number is
    /// not the initial state's step (0 in <c>two-steps</c>) + i + 1 is <c>Inconsistent</c> at that entry's
    /// <c>steps[i].step</c>, with the trailer fixed so the decoder reaches it. The rows match the golden
    /// <c>zero-steps</c> and <c>step-number-gap</c> cases and T228's <c>step-count-too-many</c> and
    /// <c>first-step-number</c> rows.
    /// </summary>
    [Theory]
    [MemberData(nameof(StepCountAndNumberCases))]
    public void StepCountAndStepNumbersAreRefusedAtTheirField(string caseName)
    {
        var bytes = TwoSteps(TestConfigs.At(30), 0, TwoStepsSalt).Encode();
        var refusal = caseName switch
        {
            "zero-steps" => new Refusal(
                Write(bytes, 16, "00000000"),
                FormatErrorCode.OutOfRange,
                16,
                "stepCount",
                Message: "stream stepCount at offset 16: 0 is below 1"),
            "step-count-too-many" => new Refusal(
                Write(bytes, 16, "01001000"),
                FormatErrorCode.Oversized,
                16,
                "stepCount",
                Message: "stream stepCount at offset 16: 1048577 is above 1048576"),
            "first-step-number" => new Refusal(
                FixChecksum(Write(bytes, StepsStart, "02000000")),
                FormatErrorCode.Inconsistent,
                StepsStart,
                "steps[0].step",
                Message: "stream steps[0].step at offset 898: 2 is not 1, the initial state's step 0 plus 1"),
            "step-number-gap" => new Refusal(
                FixChecksum(Write(bytes, Step2Start, "03000000")),
                FormatErrorCode.Inconsistent,
                Step2Start,
                "steps[1].step",
                Message: "stream steps[1].step at offset 929: 3 is not 2, the initial state's step 0 plus 2"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        AssertRefused(refusal);
    }

    /// <summary>
    /// The <c>two-steps</c> stream: rules revision 1, 30 Hz, the given seed and match salt, tiny-world's
    /// checksum, <paramref name="config"/>, the worked example as the initial state, step 1 with the
    /// example command for character index 0 and expected hash 1, and step 2 with no command and expected
    /// hash 2.
    /// </summary>
    private static RecordedStream TwoSteps(SimConfig config, ulong seed, ulong matchSalt)
    {
        RecordedStep[] steps =
        [
            new RecordedStep(1, [new RecordedCommand(0, Example)], 1),
            new RecordedStep(2, [], 2),
        ];
        return new RecordedStream(
            rulesRevision: 1,
            stepHz: 30,
            seed: seed,
            matchSalt: matchSalt,
            collisionChecksum: TinyWorldChecksum,
            config: config,
            initialState: TestStates.WorkedExample,
            steps: steps);
    }

    private static void AssertRefused(Refusal refusal)
    {
        var exception = Assert.Throws<CoreFormatException>(() => RecordedStream.Decode(refusal.Bytes));

        Assert.Equal("stream", exception.Error.Format);
        Assert.Equal(refusal.Code, exception.Error.Code);
        Assert.Equal(refusal.Offset, exception.Error.Offset);
        Assert.Equal(refusal.Field, exception.Error.Field);
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"stream {refusal.Field} at offset {refusal.Offset}: ", exception.Message, StringComparison.Ordinal);
        if (refusal.Message is not null)
        {
            Assert.Equal(refusal.Message, exception.Message);
        }
    }

    /// <summary>The <c>write</c> mutation: a copy with <paramref name="hex"/> written at <paramref name="offset"/>.</summary>
    private static byte[] Write(byte[] bytes, int offset, string hex) => Write(bytes, offset, Convert.FromHexString(hex));

    private static byte[] Write(byte[] bytes, int offset, byte[] value)
    {
        var copy = (byte[])bytes.Clone();
        value.CopyTo(copy.AsSpan(offset));
        return copy;
    }

    /// <summary>The <c>insert</c> mutation: <paramref name="hex"/> inserted before <paramref name="offset"/>.</summary>
    private static byte[] Insert(byte[] bytes, int offset, string hex) =>
        [.. bytes.AsSpan(0, offset), .. Convert.FromHexString(hex), .. bytes.AsSpan(offset)];

    /// <summary>The <c>delete</c> mutation: <paramref name="length"/> bytes removed at <paramref name="offset"/>.</summary>
    private static byte[] Delete(byte[] bytes, int offset, int length) =>
        [.. bytes.AsSpan(0, offset), .. bytes.AsSpan(offset + length)];

    /// <summary>The <c>fixChecksum</c> mutation: the 8-byte trailer rewritten as the XxHash64 (seed 0) of every byte before it.</summary>
    private static byte[] FixChecksum(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        var trailer = copy.Length - 8;
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(trailer), XxHash64.HashToUInt64(copy.AsSpan(0, trailer), 0));
        return copy;
    }

    private sealed record Refusal(byte[] Bytes, FormatErrorCode Code, long Offset, string Field, string? Message = null);
}
