using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The golden fixtures' refusal cases (FR-020, FR-023, US6 scenarios 2 and 3,
/// contracts/input-command-format.md#shared-fixture-rules): each committed refusal <c>.bin</c> is refused
/// by its decoder with exactly the code, offset and field of its <c>expectedError</c>, and gives no result:
/// <c>InputCodec.TryDecode</c> returns false with a <c>default</c> command, and <c>CollisionFile.Decode</c>
/// and <c>RecordedStream.Decode</c> throw <see cref="CoreFormatException"/> without returning a value.
/// </summary>
/// <remarks>
/// The fixtures' <c>expectedError</c>s are read with <see cref="GoldenFixtures"/>, never with
/// <c>GoldenJson</c>, and each is also compared with the contract's table, held here as literals, so a
/// fixture authored with the wrong expectation cannot agree with a decoder that is wrong the same way.
/// Every fixture is opened read-only.
/// </remarks>
public sealed class GoldenRefusalTests
{
    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>
    /// T217's table, keyed <c>&lt;format&gt;/&lt;case&gt;</c>: the offset, field, <c>.bin</c> length and
    /// message of each unsupported-version case. The lengths are the bases' (<c>neutral</c> 17,
    /// <c>tiny-world</c> 188, <c>two-steps</c> 950), and <c>refuse-version-2-longer</c>'s 20 bytes show that
    /// the version is checked before the length.
    /// </summary>
    private static readonly Dictionary<string, VersionRow> ContractVersionRows = new(StringComparer.Ordinal)
    {
        ["input/refuse-version-0"] = new(1, "version", 17, "input version at offset 1: unsupported version 0; this reader supports version 1"),
        ["input/refuse-version-2"] = new(1, "version", 17, "input version at offset 1: unsupported version 2; this reader supports version 1"),
        ["input/refuse-version-2-longer"] = new(1, "version", 20, "input version at offset 1: unsupported version 2; this reader supports version 1"),
        ["collision/unsupported-version"] = new(4, "version", 188, "collision version at offset 4: unsupported version 2; this reader supports version 1"),
        ["stream/unsupported-version"] = new(4, "version", 950, "stream version at offset 4: unsupported version 2; this reader supports version 1"),
        ["stream/command-bad-version"] = new(905, "steps[0].commands[0].input.version", 950, "stream steps[0].commands[0].input.version at offset 905: unsupported version 2; this reader supports version 1"),
    };

    /// <summary>
    /// T218's 43 rows, keyed <c>&lt;format&gt;/&lt;case&gt;</c>: the code, offset and field of every decode
    /// refusal other than the unsupported-version and checksum cases, from the refusal tables of
    /// contracts/input-command-format.md#input-cases, contracts/collision-data-format.md#refusal-cases and
    /// contracts/recorded-stream-format.md#refusal-cases.
    /// </summary>
    private static readonly Dictionary<string, (FormatErrorCode Code, long Offset, string Field)> ContractDecodeRefusals = new(StringComparer.Ordinal)
    {
        ["input/refuse-empty"] = (FormatErrorCode.Truncated, 0, "formatId"),
        ["input/refuse-format-id"] = (FormatErrorCode.UnknownMagic, 0, "formatId"),
        ["input/refuse-only-format-id"] = (FormatErrorCode.Truncated, 1, "version"),
        ["input/refuse-truncated-2"] = (FormatErrorCode.Truncated, 2, "length"),
        ["input/refuse-truncated-16"] = (FormatErrorCode.Truncated, 16, "length"),
        ["input/refuse-oversized-18"] = (FormatErrorCode.Oversized, 17, "length"),

        ["collision/unknown-magic"] = (FormatErrorCode.UnknownMagic, 0, "magic"),
        ["collision/header-length"] = (FormatErrorCode.Inconsistent, 6, "headerLength"),
        ["collision/short-header"] = (FormatErrorCode.Truncated, 20, "header"),
        ["collision/truncated"] = (FormatErrorCode.Truncated, 100, "fileLength"),
        ["collision/trailing-bytes"] = (FormatErrorCode.Oversized, 188, "fileLength"),
        ["collision/columns-too-few"] = (FormatErrorCode.OutOfRange, 32, "columns"),
        ["collision/too-many-meshes"] = (FormatErrorCode.Oversized, 40, "meshCount"),
        ["collision/counts-disagree"] = (FormatErrorCode.Inconsistent, 8, "fileLength"),
        ["collision/reserved-nonzero"] = (FormatErrorCode.OutOfRange, 44, "reserved"),
        ["collision/grid-cell-too-small"] = (FormatErrorCode.OutOfRange, 28, "gridCellSize"),
        ["collision/extent-out-of-range"] = (FormatErrorCode.OutOfRange, 32, "columns"),
        ["collision/non-finite-height"] = (FormatErrorCode.NonFinite, 64, "heights[4]"),
        ["collision/height-out-of-range"] = (FormatErrorCode.OutOfRange, 64, "heights[4]"),
        ["collision/vertex-out-of-range"] = (FormatErrorCode.OutOfRange, 96, "meshes[0].vertices[0].x"),
        ["collision/mesh-index-out-of-range"] = (FormatErrorCode.OutOfRange, 148, "meshes[0].indices[1]"),
        ["collision/crowded-cell"] = (FormatErrorCode.OutOfRange, 3204, "grid.cells[0,0]"),

        ["stream/unknown-magic"] = (FormatErrorCode.UnknownMagic, 0, "magic"),
        ["stream/header-length"] = (FormatErrorCode.Inconsistent, 6, "headerLength"),
        ["stream/short-header"] = (FormatErrorCode.Truncated, 40, "header"),
        ["stream/truncated"] = (FormatErrorCode.Truncated, 900, "fileLength"),
        ["stream/trailing-bytes"] = (FormatErrorCode.Oversized, 950, "fileLength"),
        ["stream/too-many-characters"] = (FormatErrorCode.Oversized, 14, "characterCount"),
        ["stream/zero-steps"] = (FormatErrorCode.OutOfRange, 16, "stepCount"),
        ["stream/config-too-long"] = (FormatErrorCode.Oversized, 44, "configLength"),
        ["stream/state-too-long"] = (FormatErrorCode.Oversized, 48, "initialStateLength"),
        ["stream/lengths-disagree"] = (FormatErrorCode.Inconsistent, 56, "fileLength"),
        ["stream/step-hz-out-of-range"] = (FormatErrorCode.OutOfRange, 12, "stepHz"),
        ["stream/step-hz-mismatch"] = (FormatErrorCode.Inconsistent, 12, "stepHz"),
        ["stream/config-count-short"] = (FormatErrorCode.Oversized, 756, "config.count"),
        ["stream/config-not-ascending"] = (FormatErrorCode.Inconsistent, 72, "config.entries[1].fieldId"),
        ["stream/config-unknown-field"] = (FormatErrorCode.Inconsistent, 756, "config.entries[115].fieldId"),
        ["stream/config-missing-field"] = (FormatErrorCode.Inconsistent, 756, "config.input.weaponSlots"),
        ["stream/state-character-count"] = (FormatErrorCode.Inconsistent, 766, "initialState.characterCount"),
        ["stream/state-has-events"] = (FormatErrorCode.Inconsistent, 890, "initialState.events.expiries.count"),
        ["stream/step-number-gap"] = (FormatErrorCode.Inconsistent, 929, "steps[1].step"),
        ["stream/too-many-commands"] = (FormatErrorCode.Inconsistent, 902, "steps[0].commandCount"),
        ["stream/command-index-out-of-range"] = (FormatErrorCode.OutOfRange, 903, "steps[0].commands[0].characterIndex"),
    };

    /// <summary>
    /// US6 scenario 2: given the case's committed <c>.bin</c>, when it is decoded, then the error's code is
    /// <see cref="FormatErrorCode.UnsupportedVersion"/>, its offset and field equal the case's
    /// <c>expectedError</c> (and the contract's table), and its message names the version found and the
    /// version supported, exactly as the table gives it. The decoder gives no result: the input codec returns
    /// false with a <c>default</c> command, and the collision and stream decoders throw.
    /// </summary>
    [Theory]
    [InlineData(InputFormat, "refuse-version-0")]
    [InlineData(InputFormat, "refuse-version-2")]
    [InlineData(InputFormat, "refuse-version-2-longer")]
    [InlineData(CollisionFormat, "unsupported-version")]
    [InlineData(StreamFormat, "unsupported-version")]
    [InlineData(StreamFormat, "command-bad-version")]
    public void UnknownVersionIsRefusedNamingTheVersionFoundAndSupported(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        var row = ContractVersionRows[label];
        var expected = GoldenFixtures.ExpectedError(format, caseName);
        Assert.True(
            expected == (FormatErrorCode.UnsupportedVersion, row.Offset, row.Field),
            $"{label}.json: expectedError is {expected}; the contract's table gives ({FormatErrorCode.UnsupportedVersion}, {row.Offset}, {row.Field})");

        var bin = GoldenFixtures.ReadBin(format, caseName);
        Assert.True(bin.Length == row.Length, $"{label}.bin: {bin.Length} bytes; the contract's base and mutations give {row.Length}");

        var error = Refuse(format, label, bin);

        Assert.Equal(format, error.Format);
        Assert.Equal(FormatErrorCode.UnsupportedVersion, error.Code);
        Assert.Equal(expected.Offset, error.Offset);
        Assert.Equal(expected.Field, error.Field);
        Assert.Equal(row.Message, error.ToString());
    }

    /// <summary>
    /// US6 scenario 3: given each committed decode refusal other than the unsupported-version and checksum
    /// cases (43 rows: 6 input, 16 collision and 21 stream), when its <c>.bin</c> is decoded, then the error
    /// has exactly the code, offset and field of the case's <c>expectedError</c>, which are the contract
    /// table's, and the case's format; and no value is returned. For example, collision
    /// <c>crowded-cell</c> is <c>OutOfRange</c> at 3204, <c>grid.cells[0,0]</c>; collision
    /// <c>extent-out-of-range</c> <c>OutOfRange</c> at 32, <c>columns</c>; stream
    /// <c>state-character-count</c> <c>Inconsistent</c> at 766, <c>initialState.characterCount</c>; and
    /// stream <c>config-count-short</c> <c>Oversized</c> at 756, <c>config.count</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.OtherDecodeRefusals), MemberType = typeof(GoldenFixtures))]
    public void MalformedBytesAreRefusedAtTheirOffsetAndFieldWithNoResult(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        Assert.True(
            ContractDecodeRefusals.TryGetValue(label, out var contract),
            $"{label} is classified as a decode refusal with neither UnsupportedVersion nor ChecksumMismatch, but it is not one of T218's 43 contract rows");
        var expected = GoldenFixtures.ExpectedError(format, caseName);
        Assert.True(expected == contract, $"{label}.json: expectedError is {expected}; the contract's table gives {contract}");

        var error = Refuse(format, label, GoldenFixtures.ReadBin(format, caseName));

        Assert.Equal(format, error.Format);
        Assert.Equal(expected.Code, error.Code);
        Assert.Equal(expected.Offset, error.Offset);
        Assert.Equal(expected.Field, error.Field);
    }

    /// <summary>
    /// US6 scenario 3: decoding the committed <c>.bin</c> gives a refusal whose message,
    /// <c>"&lt;format&gt; &lt;field&gt; at offset &lt;n&gt;: &lt;detail&gt;"</c>, is exactly the one the
    /// contract prints (contracts/collision-data-format.md#check-order and
    /// contracts/recorded-stream-format.md#decoding), and whose field and offset are the case's
    /// <c>expectedError</c>'s.
    /// </summary>
    [Theory]
    [InlineData(CollisionFormat, "truncated", "collision fileLength at offset 100: the file declares 188 bytes, 100 are present")]
    [InlineData(CollisionFormat, "counts-disagree", "collision fileLength at offset 8: the counts need 200 bytes; fileLength is 188")]
    [InlineData(CollisionFormat, "non-finite-height", "collision heights[4] at offset 64: NaN is not finite")]
    [InlineData(CollisionFormat, "mesh-index-out-of-range", "collision meshes[0].indices[1] at offset 148: vertex index 4 is not below vertexCount 4")]
    [InlineData(CollisionFormat, "crowded-cell", "collision grid.cells[0,0] at offset 3204: 65 triangles; the limit is 64")]
    [InlineData(StreamFormat, "lengths-disagree", "stream fileLength at offset 56: the blocks need 951 bytes; fileLength is 950")]
    [InlineData(StreamFormat, "config-not-ascending", "stream config.entries[1].fieldId at offset 72: field 0x0001 is not above 0x0002")]
    [InlineData(StreamFormat, "config-missing-field", "stream config.input.weaponSlots at offset 756: field 0x5001 is missing")]
    [InlineData(StreamFormat, "state-has-events", "stream initialState.events.expiries.count at offset 890: a loaded state has no events, found 1")]
    public void ContractMessagesAreReproducedExactly(string format, string caseName, string message)
    {
        var label = $"{format}/{caseName}";
        var expected = GoldenFixtures.ExpectedError(format, caseName);
        Assert.StartsWith($"{format} {expected.Field} at offset {expected.Offset}: ", message, StringComparison.Ordinal);

        var error = Refuse(format, label, GoldenFixtures.ReadBin(format, caseName));

        Assert.Equal(message, error.ToString());
    }

    /// <summary>
    /// Decodes <paramref name="bin"/> with <paramref name="format"/>'s decoder, asserts that it is refused
    /// with no result, and returns the error. The input codec must return false with a <c>default</c>
    /// command; the collision and stream decoders must throw <see cref="CoreFormatException"/> (that exact
    /// type) before returning a value, with the error's message as the exception's.
    /// </summary>
    private static FormatError Refuse(string format, string label, byte[] bin)
    {
        switch (format)
        {
            case InputFormat:
                {
                    var decoded = InputCodec.TryDecode(bin, out var command, out var error);
                    Assert.False(decoded, $"{label}: TryDecode accepted the {bin.Length}-byte refusal case as {command}");
                    Assert.Equal(default(InputCommand), command);
                    return error;
                }

            case CollisionFormat:
                {
                    CollisionWorld? world = null;
                    var refusal = Assert.Throws<CoreFormatException>(() => world = CollisionFile.Decode(bin));
                    Assert.Null(world);
                    Assert.Equal(refusal.Error.ToString(), refusal.Message);
                    return refusal.Error;
                }

            case StreamFormat:
                {
                    RecordedStream? stream = null;
                    var refusal = Assert.Throws<CoreFormatException>(() => stream = RecordedStream.Decode(bin));
                    Assert.Null(stream);
                    Assert.Equal(refusal.Error.ToString(), refusal.Message);
                    return refusal.Error;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "no golden format");
        }
    }

    /// <summary>One row of T217's table: the offset, field, <c>.bin</c> length and exact message.</summary>
    private sealed record VersionRow(long Offset, string Field, int Length, string Message);
}
