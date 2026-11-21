using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text.Json;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server.Golden;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The golden fixtures' refusal cases (FR-020, FR-021, FR-023, US6 scenarios 2 to 5,
/// contracts/input-command-format.md#shared-fixture-rules): each committed decode refusal <c>.bin</c> is
/// refused by its decoder with exactly the code, offset and field of its <c>expectedError</c>, and gives no
/// result: <c>InputCodec.TryDecode</c> returns false with a <c>default</c> command, and
/// <c>CollisionFile.Decode</c> and <c>RecordedStream.Decode</c> throw <see cref="CoreFormatException"/>
/// without returning a value. Each open refusal (<c>"refusedAt": "open"</c>) decodes, and
/// <see cref="StreamRunner"/> refuses it when it opens it, before any step runs. Every refusal case's
/// mutations, applied to its base's <c>.bin</c>, reproduce its own <c>.bin</c>.
/// </summary>
/// <remarks>
/// The fixtures' <c>expectedError</c>s, <c>collisionFixtures</c> and <c>expectedConfigErrors</c> are read
/// with <see cref="GoldenFixtures"/> and <see cref="JsonDocument"/>, never with <c>GoldenJson</c>, and each
/// is also compared with the contract's table, held here as literals, so a fixture authored with the wrong
/// expectation cannot agree with a decoder that is wrong the same way. Only
/// <see cref="MutationsReproduceEachRefusalFixture"/> parses a case with <c>GoldenJson</c>, to hand its
/// mutations to <c>GoldenMutations.Apply</c>, and it first compares them with the JSON as
/// <see cref="JsonDocument"/> reads it. Every fixture is opened read-only.
/// </remarks>
public sealed class GoldenRefusalTests
{
    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>
    /// The checksum of the <c>tiny-world</c> collision fixture: its trailer, and the <c>collisionChecksum</c>
    /// every stream fixture names (contracts/collision-data-format.md#golden-fixtures).
    /// </summary>
    private const ulong TinyWorldChecksum = 0x9224e3fa3b3fca9cUL;

    /// <summary>The length of the collision and stream trailers: a u64 XxHash64.</summary>
    private const int TrailerLength = 8;

    /// <summary>
    /// The refusal cases with no mutations, whose <c>.bin</c> is exactly their base's: the two open cases
    /// refused for their missing collision data (contracts/recorded-stream-format.md#refusal-cases).
    /// </summary>
    private static readonly string[] NoMutationCases = ["stream/collision-missing", "stream/collision-other"];

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

    /// <summary>T220's two rows, keyed <c>&lt;format&gt;/&lt;case&gt;</c>: the cases whose content no longer matches their trailer.</summary>
    private static readonly string[] ContractChecksumCases = ["collision/checksum-mismatch", "stream/checksum-mismatch"];

    /// <summary>
    /// T221's and T222's four rows, keyed <c>&lt;format&gt;/&lt;case&gt;</c>: the stream cases that decode
    /// and are refused when they are opened (contracts/recorded-stream-format.md#refusal-cases).
    /// </summary>
    private static readonly string[] ContractOpenCases =
        ["stream/collision-missing", "stream/collision-other", "stream/config-invalid", "stream/rules-revision"];

    /// <summary>
    /// The number of valid cases at the head of each format's <see cref="GoldenFixtures.ContractCaseNames"/>
    /// list, which holds the valid cases first and then the refusal cases (input 29, collision 4, stream 4).
    /// </summary>
    private static readonly (string Format, int ValidCount)[] ContractValidCounts =
        [(InputFormat, 29), (CollisionFormat, 4), (StreamFormat, 4)];

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
    /// US6 scenario 4: given a fixture whose content no longer matches its trailer (collision
    /// <c>checksum-mismatch</c> is <c>tiny-world</c> with <c>heights[4]</c> rewritten, and stream
    /// <c>checksum-mismatch</c> is <c>two-steps</c> with one expected-hash byte rewritten, neither with its
    /// trailer fixed), when it is decoded, then it is refused with
    /// <see cref="FormatErrorCode.ChecksumMismatch"/> at the trailer's offset (180 or 942), field
    /// <c>checksum</c>, and no value is returned. The message names the trailer, <c>tiny-world</c>'s
    /// <c>9224e3fa3b3fca9c</c> or <c>two-steps</c>' own, and the hash of the content, computed here as the
    /// XxHash64 (seed 0) of every byte before the trailer; for the collision case that is the contract's
    /// printed <c>20e8d90416fe5a4a</c>.
    /// </summary>
    [Theory]
    [InlineData(CollisionFormat, "checksum-mismatch")]
    [InlineData(StreamFormat, "checksum-mismatch")]
    public void ChecksumMismatchIsRefused(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        // The contract prints the collision case's content hash (contracts/collision-data-format.md#check-order);
        // the stream case's depends on the whole default configuration, so it is only computed.
        var (offset, trailer, printedContent) = format switch
        {
            CollisionFormat => (180, TinyWorldChecksum, (ulong?)0x20e8d90416fe5a4aUL),
            StreamFormat => (942, StreamBytes.Trailer(GoldenFixtures.ReadBin(StreamFormat, "two-steps")), null),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no checksum-mismatch row"),
        };
        var expected = GoldenFixtures.ExpectedError(format, caseName);
        Assert.True(
            expected == (FormatErrorCode.ChecksumMismatch, offset, "checksum"),
            $"{label}.json: expectedError is {expected}; the contract's table gives ({FormatErrorCode.ChecksumMismatch}, {offset}, checksum)");

        // Given: the base's trailer, unchanged, after content that no longer hashes to it.
        var bin = GoldenFixtures.ReadBin(format, caseName);
        Assert.True(bin.Length == offset + TrailerLength, $"{label}.bin: {bin.Length} bytes; its base has {offset + TrailerLength}");
        var stored = BinaryPrimitives.ReadUInt64LittleEndian(bin.AsSpan(offset));
        Assert.True(stored == trailer, $"{label}.bin: the trailer is {stored:x16}, not its base's {trailer:x16}");
        var content = XxHash64.HashToUInt64(bin.AsSpan(0, offset));
        Assert.True(content != trailer, $"{label}.bin: the content still hashes to its trailer {trailer:x16}");
        Assert.True(
            printedContent is null || content == printedContent,
            $"{label}.bin: the content hashes to {content:x16}; the contract's message prints {printedContent:x16}");

        var error = Refuse(format, label, bin);

        Assert.Equal(format, error.Format);
        Assert.Equal(FormatErrorCode.ChecksumMismatch, error.Code);
        Assert.Equal(expected.Offset, error.Offset);
        Assert.Equal(expected.Field, error.Field);
        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"{format} checksum at offset {offset}: the trailer is {trailer:x16}, the content hashes to {content:x16}"),
            error.ToString());
    }

    /// <summary>
    /// US6 scenario 5: given a recorded stream that names collision data by checksum (<c>9224e3fa3b3fca9c</c>,
    /// <c>tiny-world</c>), which <see cref="RecordedStream.Decode"/> accepts, when <see cref="StreamRunner"/>
    /// opens it with no collision world (<c>collision-missing</c>) or only with <c>heightfield-only</c>, whose
    /// checksum differs (<c>collision-other</c>), then it is refused with
    /// <see cref="FormatErrorCode.CollisionDataMissing"/> at offset 36, field <c>collisionChecksum</c>, before
    /// any step runs: the refusal is the call's only outcome, no record array is returned, and the exception
    /// is not a <see cref="StepFaultException"/>.
    /// </summary>
    [Theory]
    [InlineData("collision-missing")]
    [InlineData("collision-other")]
    public void StreamWithoutItsCollisionDataIsRefusedBeforeStepZero(string caseName)
    {
        var label = $"{StreamFormat}/{caseName}";
        string[] supplied = caseName switch
        {
            "collision-missing" => [],
            "collision-other" => ["heightfield-only"],
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "no missing-collision row"),
        };
        var open = ReadOpenCase(caseName);
        Assert.Equal(supplied, open.CollisionFixtures);
        Assert.Empty(open.ExpectedConfigErrors);
        var expected = GoldenFixtures.ExpectedError(StreamFormat, caseName);
        Assert.True(
            expected == (FormatErrorCode.CollisionDataMissing, 36, "collisionChecksum"),
            $"{label}.json: expectedError is {expected}; the contract's table gives ({FormatErrorCode.CollisionDataMissing}, 36, collisionChecksum)");

        // Given: the stream decodes and names tiny-world, and no supplied world has that checksum.
        var bin = GoldenFixtures.ReadBin(StreamFormat, caseName);
        var decoded = RecordedStream.Decode(bin);
        Assert.Equal(TinyWorldChecksum, decoded.CollisionChecksum);
        Assert.Equal(SimCoreVersion.RulesRevision, decoded.RulesRevision);
        var worlds = GoldenFixtures.CollisionFixtureWorlds(caseName);
        Assert.Equal(supplied.Length, worlds.Count);
        Assert.All(worlds, world => Assert.NotEqual(TinyWorldChecksum, world.Checksum));

        var thrown = OpenOnly(bin, worlds, label);

        var refusal = Assert.IsType<CoreFormatException>(thrown);
        Assert.Equal(StreamFormat, refusal.Error.Format);
        Assert.Equal(FormatErrorCode.CollisionDataMissing, refusal.Error.Code);
        Assert.Equal(expected.Offset, refusal.Error.Offset);
        Assert.Equal(expected.Field, refusal.Error.Field);
        Assert.Equal("stream collisionChecksum at offset 36: collision data 9224e3fa3b3fca9c was not supplied", refusal.Error.ToString());
        Assert.Equal(refusal.Error.ToString(), refusal.Message);
    }

    /// <summary>
    /// US6 scenario 5, the other open refusals: given a stream that decodes and is opened with the collision
    /// data it names (<c>tiny-world</c>), when <see cref="StreamRunner"/> opens it, then it is refused before
    /// any step runs. <c>rules-revision</c>, recorded under rules revision 0, is refused at open step 2 with
    /// <see cref="FormatErrorCode.RulesRevisionMismatch"/> at offset 8, field <c>rulesRevision</c>, naming both
    /// revisions. <c>config-invalid</c>, whose <c>movement.gravity</c> is NaN, is refused at open step 4 with
    /// a <see cref="ConfigValidationException"/> holding exactly the one error of its
    /// <c>expectedConfigErrors</c>, <c>movement.gravity: must be finite</c>. Neither returns a record array or
    /// throws a <see cref="StepFaultException"/>.
    /// </summary>
    [Theory]
    [InlineData("rules-revision")]
    [InlineData("config-invalid")]
    public void OtherOpenRefusalsHappenBeforeStepZero(string caseName)
    {
        var label = $"{StreamFormat}/{caseName}";
        string[] supplied = ["tiny-world"];
        var open = ReadOpenCase(caseName);
        Assert.Equal(supplied, open.CollisionFixtures);

        // Given: both decode, because these are open-time refusals, and the collision data they name is supplied.
        var bin = GoldenFixtures.ReadBin(StreamFormat, caseName);
        var decoded = RecordedStream.Decode(bin);
        Assert.Equal(TinyWorldChecksum, decoded.CollisionChecksum);
        var worlds = GoldenFixtures.CollisionFixtureWorlds(caseName);
        Assert.Equal(TinyWorldChecksum, Assert.Single(worlds).Checksum);

        switch (caseName)
        {
            case "rules-revision":
                {
                    Assert.Equal(0u, decoded.RulesRevision);
                    Assert.Empty(open.ExpectedConfigErrors);
                    var expected = GoldenFixtures.ExpectedError(StreamFormat, caseName);
                    Assert.True(
                        expected == (FormatErrorCode.RulesRevisionMismatch, 8, "rulesRevision"),
                        $"{label}.json: expectedError is {expected}; the contract's table gives ({FormatErrorCode.RulesRevisionMismatch}, 8, rulesRevision)");

                    var thrown = OpenOnly(bin, worlds, label);

                    var refusal = Assert.IsType<CoreFormatException>(thrown);
                    Assert.Equal(StreamFormat, refusal.Error.Format);
                    Assert.Equal(FormatErrorCode.RulesRevisionMismatch, refusal.Error.Code);
                    Assert.Equal(expected.Offset, refusal.Error.Offset);
                    Assert.Equal(expected.Field, refusal.Error.Field);
                    Assert.Equal("rules revision 0 in the stream; this core implements rules revision 1", refusal.Error.Detail);
                    Assert.Equal("stream rulesRevision at offset 8: rules revision 0 in the stream; this core implements rules revision 1", refusal.Message);
                    break;
                }

            case "config-invalid":
                {
                    Assert.Equal(SimCoreVersion.RulesRevision, decoded.RulesRevision);
                    Assert.False(open.HasExpectedError, $"{label}.json has an expectedError; the contract gives a configuration error instead");
                    (string Path, string Message)[] contract = [("movement.gravity", "must be finite")];
                    Assert.Equal(contract, open.ExpectedConfigErrors);

                    var thrown = OpenOnly(bin, worlds, label);

                    var invalid = Assert.IsType<ConfigValidationException>(thrown);
                    var error = Assert.Single(invalid.Errors);
                    Assert.Equal(new ConfigError("movement.gravity", "must be finite"), error);
                    Assert.Equal(open.ExpectedConfigErrors, invalid.Errors.Select(e => (e.Path, e.Message)).ToArray());
                    Assert.Equal("movement.gravity: must be finite", error.ToString());
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "no other open-refusal row");
        }
    }

    /// <summary>
    /// FR-023, contracts/input-command-format.md#shared-fixture-rules: given each committed refusal case,
    /// open refusals included, whose <c>base</c> names a valid case of the same format, when its
    /// <c>mutations</c>, parsed with <see cref="GoldenJson"/>, are applied in order with
    /// <see cref="GoldenMutations.Apply"/> to the base's committed <c>.bin</c>, then the result equals the
    /// case's committed <c>.bin</c> byte for byte. The parsed base and mutations are first compared with
    /// the JSON's <c>base</c> and <c>mutations</c> as <see cref="JsonDocument"/> reads them, so a parser
    /// that drops or misreads a mutation cannot agree with itself through <c>golden write</c>. The cases
    /// with no mutations are exactly <c>collision-missing</c> and <c>collision-other</c>, and each equals
    /// its base's bytes.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.RefusalCases), MemberType = typeof(GoldenFixtures))]
    public void MutationsReproduceEachRefusalFixture(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        using var json = GoldenFixtures.ReadJson(format, caseName);
        var root = json.RootElement;
        Assert.True(
            root.TryGetProperty("base", out var baseElement) && baseElement.ValueKind == JsonValueKind.String,
            $"{label}.json has no string base");
        var baseName = baseElement.GetString()!;
        Assert.True(
            root.TryGetProperty("mutations", out var mutationsElement) && mutationsElement.ValueKind == JsonValueKind.Array,
            $"{label}.json has no mutations array");
        var rawMutations = mutationsElement.EnumerateArray().ToArray();

        // Given: the base is a valid case of the same format, and the parsed case holds the JSON's base and mutations.
        var validNames = GoldenFixtures.ValidCaseNames(format).Select(row => row.Data).ToArray();
        Assert.True(
            validNames.Contains(baseName, StringComparer.Ordinal),
            $"{label}.json's base '{baseName}' is not a valid {format} case; the valid cases are {string.Join(", ", validNames)}");
        var parsed = Assert.IsType<GoldenRefusalCase>(GoldenJson.Parse(format, caseName + ".json", GoldenFixtures.ReadJsonBytes(format, caseName)));
        Assert.Equal(format, parsed.Format);
        Assert.Equal(caseName, parsed.Case);
        Assert.Equal(GoldenKind.Refusal, parsed.Kind);
        Assert.Equal(baseName, parsed.Base);
        Assert.True(
            parsed.Mutations.Length == rawMutations.Length,
            $"{label}: GoldenJson parsed {parsed.Mutations.Length} mutations; the JSON holds {rawMutations.Length}");
        for (var i = 0; i < rawMutations.Length; i++)
        {
            AssertMutationMatchesJson(label, i, rawMutations[i], parsed.Mutations[i]);
        }

        var baseBin = GoldenFixtures.ReadBin(format, baseName);
        var bin = GoldenFixtures.ReadBin(format, caseName);

        var applied = GoldenMutations.Apply(format, baseBin, parsed.Mutations);

        Assert.True(
            applied.AsSpan().SequenceEqual(bin),
            $"{label}: the {parsed.Mutations.Length} mutations applied to {format}/{baseName}.bin ({baseBin.Length} bytes) give {applied.Length} bytes, "
            + $"which differ from the committed {label}.bin ({bin.Length} bytes){FirstDifference(applied, bin)}");

        var hasNoMutations = rawMutations.Length == 0;
        var isNoMutationCase = NoMutationCases.Contains(label, StringComparer.Ordinal);
        Assert.True(
            hasNoMutations == isNoMutationCase,
            hasNoMutations
                ? $"{label}.json has no mutations; only {string.Join(" and ", NoMutationCases)} have none"
                : $"{label}.json has {rawMutations.Length} mutations; the contract gives it none");
        if (hasNoMutations)
        {
            Assert.True(
                bin.AsSpan().SequenceEqual(baseBin),
                $"{label}.bin ({bin.Length} bytes) has no mutations but differs from {format}/{baseName}.bin ({baseBin.Length} bytes){FirstDifference(bin, baseBin)}");
        }
    }

    /// <summary>
    /// US6 scenarios 2 to 5, checked from both sides: every refusal fixture reaches the Theory that asserts
    /// its refusal. <see cref="MalformedBytesAreRefusedAtTheirOffsetAndFieldWithNoResult"/> takes its rows
    /// from the fixtures' own classification (<c>refusedAt</c> and <c>expectedError.code</c>) and checks only
    /// that each row it receives is a contract row, and the other refusal Theories have fixed rows, so a
    /// fixture authored with a stray <c>"refusedAt": "open"</c>, or with the code <c>UnsupportedVersion</c>
    /// or <c>ChecksumMismatch</c>, would otherwise leave every Theory that asserts its refusal. Given the
    /// committed fixtures, when they are classified, then, compared as sets of
    /// <c>&lt;format&gt;/&lt;case&gt;</c> labels in ordinal order: <see cref="GoldenFixtures.OtherDecodeRefusals"/>
    /// is exactly T218's 43 contract rows; the <see cref="GoldenFixtures.DecodeRefusalCases"/> whose JSON
    /// code is <c>UnsupportedVersion</c> are exactly T217's 6 and those coded <c>ChecksumMismatch</c>
    /// exactly T220's 2, so the decode refusals are those 51; <see cref="GoldenFixtures.OpenRefusals"/> is
    /// exactly T221's and T222's 4; and <see cref="GoldenFixtures.RefusalCases"/> is the 55 refusal cases
    /// of the contract's case lists, each in exactly one of those groups.
    /// </summary>
    [Fact]
    public void EveryRefusalFixtureReachesTheTheoryThatChecksIt()
    {
        string[] contractRefusals =
        [
            .. ContractValidCounts.SelectMany(row => GoldenFixtures.ContractCaseNames(row.Format).Skip(row.ValidCount).Select(name => $"{row.Format}/{name}")),
        ];
        string[] groups = [.. ContractDecodeRefusals.Keys, .. ContractVersionRows.Keys, .. ContractChecksumCases, .. ContractOpenCases];
        Assert.True(
            groups.Distinct(StringComparer.Ordinal).Count() == groups.Length,
            $"the contract's refusal groups share a case: {string.Join(", ", groups.GroupBy(label => label, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))}");
        AssertSameLabels("the four contract groups (T217, T218, T220, T221 and T222)", contractRefusals, groups);

        var decode = Labels(GoldenFixtures.DecodeRefusalCases);
        var codes = decode.ToDictionary(label => label, ExpectedCode, StringComparer.Ordinal);

        AssertSameLabels("OtherDecodeRefusals, T218's rows", ContractDecodeRefusals.Keys, Labels(GoldenFixtures.OtherDecodeRefusals));
        AssertSameLabels(
            "the decode refusals coded UnsupportedVersion, T217's rows",
            ContractVersionRows.Keys,
            decode.Where(label => codes[label] == nameof(FormatErrorCode.UnsupportedVersion)));
        AssertSameLabels(
            "the decode refusals coded ChecksumMismatch, T220's rows",
            ContractChecksumCases,
            decode.Where(label => codes[label] == nameof(FormatErrorCode.ChecksumMismatch)));
        AssertSameLabels("DecodeRefusalCases", [.. ContractDecodeRefusals.Keys, .. ContractVersionRows.Keys, .. ContractChecksumCases], decode);
        AssertSameLabels("OpenRefusals, T221's and T222's rows", ContractOpenCases, Labels(GoldenFixtures.OpenRefusals));
        AssertSameLabels("RefusalCases", contractRefusals, Labels(GoldenFixtures.RefusalCases));
    }

    /// <summary>
    /// Runs <paramref name="bin"/> with <see cref="StreamRunner.Run(ReadOnlySpan{byte}, IReadOnlyList{CollisionWorld}, int)"/>
    /// and returns what it threw, asserting that the refusal is the call's only outcome and came before step
    /// zero: an exception was thrown, no record array was returned, and the exception is not a
    /// <see cref="StepFaultException"/>, which only a running step throws.
    /// </summary>
    private static Exception OpenOnly(byte[] bin, IReadOnlyList<CollisionWorld> worlds, string label)
    {
        byte[]? records = null;
        var thrown = Record.Exception(() => records = StreamRunner.Run(bin, worlds));

        Assert.True(records is null, $"{label}: StreamRunner.Run returned {records?.Length} bytes of step records; the stream must be refused when it is opened");
        Assert.NotNull(thrown);
        Assert.IsNotType<StepFaultException>(thrown);
        return thrown;
    }

    /// <summary>
    /// An open refusal's JSON, read with <see cref="JsonDocument"/>: it must be a refusal with
    /// <c>"refusedAt": "open"</c> and a <c>collisionFixtures</c> array of names. Returns those names, its
    /// <c>expectedConfigErrors</c> as <c>(path, message)</c> pairs (empty when absent), and whether it has an
    /// <c>expectedError</c>.
    /// </summary>
    private static OpenCase ReadOpenCase(string caseName)
    {
        var label = $"{StreamFormat}/{caseName}";
        using var json = GoldenFixtures.ReadJson(StreamFormat, caseName);
        var root = json.RootElement;
        Assert.True(
            root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "refusal",
            $"{label}.json is not a refusal case");
        Assert.True(
            root.TryGetProperty("refusedAt", out var stage) && stage.ValueKind == JsonValueKind.String && stage.GetString() == "open",
            $"{label}.json has no \"refusedAt\": \"open\"; the contract refuses it when the stream is opened");
        Assert.True(
            root.TryGetProperty("collisionFixtures", out var fixtures) && fixtures.ValueKind == JsonValueKind.Array,
            $"{label}.json has no collisionFixtures array");
        var names = fixtures.EnumerateArray().Select(name => name.GetString() ?? throw new InvalidDataException($"{label}.json's collisionFixtures holds a {name.ValueKind}")).ToArray();

        var configErrors = Array.Empty<(string Path, string Message)>();
        if (root.TryGetProperty("expectedConfigErrors", out var errors))
        {
            Assert.True(errors.ValueKind == JsonValueKind.Array, $"{label}.json's expectedConfigErrors is a {errors.ValueKind}, not an array");
            configErrors = errors.EnumerateArray()
                .Select(error => (
                    error.GetProperty("path").GetString() ?? throw new InvalidDataException($"{label}.json has a configuration error with no path"),
                    error.GetProperty("message").GetString() ?? throw new InvalidDataException($"{label}.json has a configuration error with no message")))
                .ToArray();
        }

        return new OpenCase(names, configErrors, root.TryGetProperty("expectedError", out _));
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

    /// <summary>
    /// Asserts that mutation <paramref name="index"/> as <see cref="GoldenJson"/> parsed it holds what the
    /// JSON's object does, read with <see cref="JsonDocument"/>: the same <c>op</c>, and the <c>offset</c>,
    /// <c>length</c> and <c>hex</c> it gives (each only when the JSON has it; <c>hex</c> compared ignoring
    /// case, because <see cref="GoldenMutations.Apply"/> parses it case-insensitively).
    /// </summary>
    private static void AssertMutationMatchesJson(string label, int index, JsonElement raw, GoldenMutation parsed)
    {
        var at = $"{label}.json mutations[{index}]";
        Assert.True(raw.ValueKind == JsonValueKind.Object, $"{at} is a {raw.ValueKind}, not an object");
        Assert.True(
            raw.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String,
            $"{at} has no string op");
        Assert.True(parsed.Op == op.GetString(), $"{at}: GoldenJson parsed op '{parsed.Op}'; the JSON holds '{op.GetString()}'");
        if (raw.TryGetProperty("offset", out var offset))
        {
            Assert.True(
                offset.ValueKind == JsonValueKind.Number && parsed.Offset == offset.GetInt64(),
                $"{at}: GoldenJson parsed offset {parsed.Offset}; the JSON holds {offset.GetRawText()}");
        }

        if (raw.TryGetProperty("length", out var length))
        {
            Assert.True(
                length.ValueKind == JsonValueKind.Number && parsed.Length == length.GetInt64(),
                $"{at}: GoldenJson parsed length {parsed.Length}; the JSON holds {length.GetRawText()}");
        }

        if (raw.TryGetProperty("hex", out var hex))
        {
            Assert.True(
                hex.ValueKind == JsonValueKind.String && string.Equals(parsed.Hex, hex.GetString(), StringComparison.OrdinalIgnoreCase),
                $"{at}: GoldenJson parsed hex \"{parsed.Hex}\"; the JSON holds {hex.GetRawText()}");
        }
    }

    /// <summary>The rows of a <c>(format, case)</c> member of <see cref="GoldenFixtures"/>, as <c>&lt;format&gt;/&lt;case&gt;</c> labels.</summary>
    private static string[] Labels(TheoryData<string, string> rows) => [.. rows.Select(row => $"{row.Data.Item1}/{row.Data.Item2}")];

    /// <summary>
    /// The <c>expectedError.code</c> of the case labelled <c>&lt;format&gt;/&lt;case&gt;</c>, read with
    /// <see cref="JsonDocument"/>, or null when it has no <c>expectedError</c> object with a string code.
    /// </summary>
    private static string? ExpectedCode(string label)
    {
        var slash = label.IndexOf('/', StringComparison.Ordinal);
        using var json = GoldenFixtures.ReadJson(label[..slash], label[(slash + 1)..]);
        return json.RootElement.TryGetProperty("expectedError", out var expected)
            && expected.ValueKind == JsonValueKind.Object
            && expected.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
    }

    /// <summary>
    /// Asserts that <paramref name="actual"/> holds exactly the labels of <paramref name="expected"/>, each
    /// once, compared in ordinal order, naming the missing and unexpected labels on failure.
    /// </summary>
    private static void AssertSameLabels(string what, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var want = expected.Order(StringComparer.Ordinal).ToArray();
        var got = actual.Order(StringComparer.Ordinal).ToArray();
        var missing = want.Except(got, StringComparer.Ordinal).ToArray();
        var unexpected = got.Except(want, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0 && unexpected.Length == 0 && got.Length == want.Length,
            $"{what}: {got.Length} cases, the contract gives {want.Length}; missing [{string.Join(", ", missing)}], not expected [{string.Join(", ", unexpected)}]");
        Assert.Equal(want, got);
    }

    /// <summary>
    /// <c>"; the first difference is at offset &lt;n&gt;"</c> with both bytes, or the shorter length when one
    /// array is a prefix of the other, for a failure message; empty when the arrays are equal.
    /// </summary>
    private static string FirstDifference(byte[] actual, byte[] expected)
    {
        var common = System.Math.Min(actual.Length, expected.Length);
        for (var i = 0; i < common; i++)
        {
            if (actual[i] != expected[i])
            {
                return string.Create(CultureInfo.InvariantCulture, $"; the first difference is at offset {i}: {actual[i]:x2}, expected {expected[i]:x2}");
            }
        }

        return actual.Length == expected.Length
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"; one is a prefix of the other, which ends at offset {common}");
    }

    /// <summary>One row of T217's table: the offset, field, <c>.bin</c> length and exact message.</summary>
    private sealed record VersionRow(long Offset, string Field, int Length, string Message);

    /// <summary>
    /// What an open refusal's JSON names: the collision fixtures it is opened with, its expected configuration
    /// errors (empty when it expects a format error), and whether it has an <c>expectedError</c>.
    /// </summary>
    private sealed record OpenCase(string[] CollisionFixtures, (string Path, string Message)[] ExpectedConfigErrors, bool HasExpectedError);
}
