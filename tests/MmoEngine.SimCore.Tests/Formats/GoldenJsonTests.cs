using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server.Golden;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The golden fixtures' JSON reader, <c>GoldenJson.Parse</c> (FR-023,
/// contracts/input-command-format.md#shared-fixture-rules): it reads the contracts' example cases into
/// their typed cases, reads the JSON value forms exactly (an x16 is 16 lowercase hex digits, a float is
/// <c>{bits, value}</c> with authoritative uppercase <c>bits</c>, a <c>fieldId</c> and a
/// <c>valueBits</c> are <c>0x</c> and 4 or 8 uppercase hex digits), and refuses a malformed value with a
/// <see cref="GoldenCaseException"/> whose message is exactly <c>&lt;JSON path&gt;: &lt;problem&gt;</c>.
/// </summary>
/// <remarks>
/// The cases are written here, not read from <c>simcore/golden/</c>: the input and refusal examples are the
/// contracts' JSON verbatim, and the stream and collision cases are built from the placeholder
/// configuration, the worked example state and <c>tiny-world</c>, with every length and checksum computed
/// from the bytes the contract's layout gives, so each case is valid as a whole. Each malformed row edits
/// one value of a case that <see cref="ParsesTheContractExamples"/> or
/// <see cref="ParsesX16FloatObjectsAndBitStrings"/> parses, so that value is the row's only problem.
/// </remarks>
public sealed class GoldenJsonTests
{
    /// <summary>The input <c>example</c> case of contracts/input-command-format.md#input-cases, verbatim.</summary>
    private const string InputExampleJson = """
        {
          "format": "input",
          "formatVersion": 1,
          "case": "example",
          "kind": "valid",
          "command": {
            "sequence": 1,
            "clientStep": 100,
            "buttons": 129,
            "aimYaw": 16384,
            "aimPitch": -1024,
            "weaponSlot": 0
          },
          "validate": { "lastSequence": 0, "weaponSlots": 2, "expected": "None" }
        }
        """;

    /// <summary>The input <c>refuse-version-2</c> case of contracts/input-command-format.md#input-cases, verbatim.</summary>
    private const string RefuseVersion2Json = """
        {
          "format": "input",
          "formatVersion": 1,
          "case": "refuse-version-2",
          "kind": "refusal",
          "base": "neutral",
          "mutations": [ { "op": "write", "offset": 1, "hex": "02" } ],
          "expectedError": { "code": "UnsupportedVersion", "offset": 1, "field": "version" }
        }
        """;

    /// <summary>
    /// A stream refusal refused when it is opened, as contracts/recorded-stream-format.md#refusal-cases
    /// describes <c>config-invalid</c>: <c>"refusedAt": "open"</c>, the collision fixtures it is opened
    /// with, and <c>expectedConfigErrors</c> in place of <c>expectedError</c>.
    /// </summary>
    private const string ConfigInvalidJson = """
        {
          "format": "stream",
          "formatVersion": 1,
          "case": "config-invalid",
          "kind": "refusal",
          "base": "two-steps",
          "mutations": [ { "op": "write", "offset": 122, "hex": "0000c07f" }, { "op": "fixChecksum" } ],
          "refusedAt": "open",
          "collisionFixtures": [ "tiny-world" ],
          "expectedConfigErrors": [ { "path": "movement.gravity", "message": "must be finite" } ]
        }
        """;

    /// <summary>The input command of contracts/input-command-format.md#example: sequence 1, client step 100, Forward and Fire, yaw 16384, pitch −1024, slot 0.</summary>
    private const string ExampleCommandHex = "490101000000640000008100004000fc00";

    /// <summary><c>tiny-world</c>'s trailer (contracts/collision-data-format.md#tiny-world), the collision data every stream case names.</summary>
    private const ulong TinyWorldChecksum = 0x9224e3fa3b3fca9cUL;

    /// <summary>The <c>seeded</c> stream's seed (contracts/recorded-stream-format.md#other-valid-cases).</summary>
    private const ulong SeededSeed = 7;

    /// <summary>A match salt with the top bit set, written <c>"fedcba9876543210"</c>.</summary>
    private const ulong HighMatchSalt = 0xFEDCBA9876543210UL;

    private const uint NegativeZeroBits = 0x80000000u;

    private const int StreamHeaderLength = 64;
    private const int TrailerLength = 8;

    /// <summary>The stream header's <c>u32 configLength</c>, followed by <c>initialStateLength</c> and <c>stepsLength</c>.</summary>
    private const int StreamConfigLengthOffset = 44;

    /// <summary>
    /// Malformed values, each <c>(json, expectedMessage)</c>: a valid case with one value broken. The
    /// edited property is written first in the JSON, so each row's display name shows it.
    /// </summary>
    public static TheoryData<string, string> MalformedValues => new()
    {
        // An x16 with uppercase letters, with 15 digits, and with a 0x prefix (16 characters in all).
        { Edited(SeededStreamCase(), "matchSalt", root => root["matchSalt"] = "FEDCBA9876543210"), "$.matchSalt: must be 16 lowercase hex digits" },
        { Edited(SeededStreamCase(), "matchSalt", root => root["matchSalt"] = "edcba9876543210"), "$.matchSalt: must be 16 lowercase hex digits" },
        { Edited(SeededStreamCase(), "matchSalt", root => root["matchSalt"] = "0xfedcba98765432"), "$.matchSalt: must be 16 lowercase hex digits" },

        // Float bits in lowercase, and with 7 digits.
        { Edited(TinyWorldCase(), "heights", root => root["heights"]![0] = FloatObject("0x3f800000", "1.0")), "$.heights[0].bits: must be 0x and 8 uppercase hex digits" },
        { Edited(TinyWorldCase(), "heights", root => root["heights"]![0] = FloatObject("0x3F80000", "1.0")), "$.heights[0].bits: must be 0x and 8 uppercase hex digits" },

        // A float value that does not parse to its bits.
        { Edited(TinyWorldCase(), "heights", root => root["heights"]![0] = FloatObject("0x3F800000", "2.0")), "$.heights[0].value: 2 does not have bits 0x3F800000" },

        // A fieldId with 3 digits.
        { Edited(SeededStreamCase(), "config", root => root["config"]![0]!["fieldId"] = "0x001"), "$.config[0].fieldId: must be 0x and 4 uppercase hex digits" },

        // An unknown kind.
        { Edited(Node(InputExampleJson), "kind", root => root["kind"] = "broken"), "$.kind: must be \"valid\" or \"refusal\"" },

        // A refusal with neither expectedError nor expectedConfigErrors.
        { Edited(Node(RefuseVersion2Json), "kind", root => root.Remove("expectedError")), "$: a refusal case needs expectedError or expectedConfigErrors" },

        // An integer that does not fit its field: aimYaw is a u16.
        { Edited(Node(InputExampleJson), "command", root => root["command"]!["aimYaw"] = 65536), "$.command.aimYaw: 65536 is outside [0, 65535]" },

        // An integer written as a string, with a fraction, and beyond 64 bits.
        { Edited(Node(InputExampleJson), "command", root => root["command"]!["aimYaw"] = "16384"), "$.command.aimYaw: must be an integer" },
        { Edited(Node(InputExampleJson), "command", root => root["command"]!["aimYaw"] = JsonNode.Parse("1.5")), "$.command.aimYaw: must be an integer" },
        { Edited(Node(InputExampleJson), "command", root => root["command"]!["aimYaw"] = JsonNode.Parse("18446744073709551616")), "$.command.aimYaw: 18446744073709551616 is outside [0, 65535]" },

        // A formatVersion other than the format's, a field the case's shape does not have, and an enum name that is no member.
        { Edited(Node(InputExampleJson), "formatVersion", root => root["formatVersion"] = 2), "$.formatVersion: 2 is not 1, the input format's version" },
        { Edited(Node(InputExampleJson), "extra", root => root["extra"] = 1), "$.extra: is not a field of a valid input case" },
        { Edited(Node(InputExampleJson), "validate", root => root["validate"]!["expected"] = "Accepted"), "$.validate.expected: \"Accepted\" is not a InputRejection" },

        // Refusal cases: the code None, a refusedAt other than "open", refusedAt on an input case, both expectations,
        // configuration errors or collision fixtures on a case that is not refused when opened, and a case refused when
        // opened that does not name the collision fixtures it is opened with.
        { Edited(Node(RefuseVersion2Json), "expectedError", root => root["expectedError"]!["code"] = "None"), "$.expectedError.code: None is no refusal" },
        { Edited(Node(RefuseVersion2Json), "refusedAt", root => root["refusedAt"] = "decode"), "$.refusedAt: must be \"open\"" },
        { Edited(Node(RefuseVersion2Json), "refusedAt", root => root["refusedAt"] = "open"), "$.refusedAt: only a stream case is refused at open" },
        { Edited(Node(RefuseVersion2Json), "expectedConfigErrors", root => root["expectedConfigErrors"] = GravityConfigErrors()), "$: a refusal case has expectedError or expectedConfigErrors, not both" },
        { Edited(Node(ConfigInvalidJson), "expectedConfigErrors", root => root.Remove("refusedAt")), "$.expectedConfigErrors: only a case refused at open has configuration errors" },
        { Edited(Node(RefuseVersion2Json), "collisionFixtures", root => root["collisionFixtures"] = new JsonArray("tiny-world")), "$.collisionFixtures: only a case refused at open names collision fixtures" },
        { Edited(Node(ConfigInvalidJson), "refusedAt", root => root.Remove("collisionFixtures")), "$.collisionFixtures: is required for a case refused at open" },

        // Mutations: an unknown op, odd hex, a missing argument and an argument the op does not take.
        { Edited(Node(RefuseVersion2Json), "mutations", root => root["mutations"]![0]!["op"] = "rotate"), "$.mutations[0].op: \"rotate\" is not an op; the ops are truncate, append, write, insert, delete and fixChecksum" },
        { Edited(Node(RefuseVersion2Json), "mutations", root => root["mutations"]![0]!["hex"] = "0"), "$.mutations[0].hex: must be hex digits, two per byte" },
        { Edited(Node(RefuseVersion2Json), "mutations", root => root["mutations"]![0]!.AsObject().Remove("offset")), "$.mutations[0].offset: is required for write" },
        { Edited(Node(RefuseVersion2Json), "mutations", root => root["mutations"]![0]!["length"] = 1), "$.mutations[0].length: is not an argument of write" },

        // Collision counts that disagree with their lists, a vertex that is not a triple, a float value that is a string.
        { Edited(TinyWorldCase(), "heights", root => root["heights"]!.AsArray().RemoveAt(8)), "$.heights: 8 heights; columns × rows is 9" },
        { Edited(TinyWorldCase(), "heights", root => root["heights"]![0] = FloatObject("0x00000000", "\"0.0\"")), "$.heights[0].value: must be a number" },
        { Edited(TinyWorldCase(), "meshCount", root => root["meshCount"] = 2), "$.meshCount: 2 is not the length of meshes, 1" },
        { Edited(TinyWorldCase(), "meshes", root => root["meshes"]![0]!["vertices"]![0]!.AsArray().RemoveAt(2)), "$.meshes[0].vertices[0]: must be [x, y, z]" },
        { Edited(TinyWorldCase(), "meshes", root => RemoveLast(root["meshes"]![0]!["indices"]!.AsArray())), "$.meshes[0].indices: 8 indices are not whole triangles" },

        // Stream counts that disagree with their lists.
        { Edited(SeededStreamCase(), "stepCount", root => root["stepCount"] = 3), "$.stepCount: 3 is not the length of steps, 2" },
        { Edited(SeededStreamCase(), "characterCount", root => root["characterCount"] = 2), "$.characterCount: 2 is not the initial state's characterCount, 1" },
        { Edited(SeededStreamCase(), "steps", root => root["steps"]![0]!["commands"] = RepeatedCommand(root, 256)), "$.steps[0].commands: 256 commands; commandCount holds at most 255" },

        // Config entries that break the block's structure (not ascending; input.weaponSlots missing), and a path that is not its field's.
        { Edited(SeededStreamCase(), "config", root => SwapFirstTwo(root["config"]!.AsArray())), "$.config[1].fieldId: field 0x0001 is not above 0x0002" },
        { Edited(SeededStreamCase(), "config", root => RemoveLast(root["config"]!.AsArray())), "$.config: input.weaponSlots: field 0x5001 is missing" },
        { Edited(SeededStreamCase(), "config", root => root["config"]![9]!["path"] = "movement.fallSpeed"), "$.config[9].path: \"movement.fallSpeed\" is not field 0x1007's path \"movement.gravity\"" },

        // State bytes in uppercase hex.
        { Edited(SeededStreamCase(), "initialStateHex", root => root["initialStateHex"] = root["initialStateHex"]!.GetValue<string>().ToUpperInvariant()), "$.initialStateHex: must be lowercase hex digits, two per byte" },
    };

    /// <summary>
    /// Given the contracts' example JSON, when each is parsed, then: the input <c>example</c> is an
    /// <see cref="InputValidCase"/> whose command is sequence 1, client step 100, Forward and Fire, yaw
    /// 16384, pitch −1024, slot 0, validating as <see cref="InputRejection.None"/> with
    /// <c>lastSequence</c> 0 and <c>weaponSlots</c> 2; <c>refuse-version-2</c> is a refusal case on base
    /// <c>neutral</c> with one <c>write</c> mutation at offset 1 with hex <c>02</c> and the expected error
    /// (<c>UnsupportedVersion</c>, 1, <c>version</c>); and a stream refusal with
    /// <c>"refusedAt": "open"</c>, <c>"collisionFixtures": ["tiny-world"]</c> and the expected configuration
    /// error <c>movement.gravity: must be finite</c> keeps all three.
    /// </summary>
    [Fact]
    public void ParsesTheContractExamples()
    {
        var example = Assert.IsType<InputValidCase>(Parse(InputExampleJson));
        Assert.Equal("input", example.Format);
        Assert.Equal(1, example.FormatVersion);
        Assert.Equal("example", example.Case);
        Assert.Equal(GoldenKind.Valid, example.Kind);
        Assert.Equal(new InputCommand(1, 100, Buttons.Forward | Buttons.Fire, 16384, -1024, 0), example.Command);
        Assert.Equal(new GoldenValidate(0, 2, InputRejection.None), example.Validate);

        var refusal = Assert.IsType<GoldenRefusalCase>(Parse(RefuseVersion2Json));
        Assert.Equal("input", refusal.Format);
        Assert.Equal("refuse-version-2", refusal.Case);
        Assert.Equal(GoldenKind.Refusal, refusal.Kind);
        Assert.Equal("neutral", refusal.Base);
        var write = Assert.Single(refusal.Mutations);
        Assert.Equal("write", write.Op);
        Assert.Equal(1L, write.Offset);
        Assert.Equal("02", write.Hex);
        Assert.Equal(new GoldenExpectedError(FormatErrorCode.UnsupportedVersion, 1, "version"), refusal.ExpectedError);
        Assert.Null(refusal.RefusedAt);
        Assert.Empty(refusal.CollisionFixtures);
        Assert.Empty(refusal.ExpectedConfigErrors);

        var open = Assert.IsType<GoldenRefusalCase>(Parse(ConfigInvalidJson));
        Assert.Equal("stream", open.Format);
        Assert.Equal("two-steps", open.Base);
        string[] ops = ["write", "fixChecksum"];
        Assert.Equal(ops, open.Mutations.Select(mutation => mutation.Op).ToArray());
        Assert.Equal("open", open.RefusedAt);
        string[] fixtures = ["tiny-world"];
        Assert.Equal(fixtures, open.CollisionFixtures);
        (string Path, string Message)[] configErrors = [("movement.gravity", "must be finite")];
        Assert.Equal(configErrors, open.ExpectedConfigErrors);
        Assert.Null(open.ExpectedError);
    }

    /// <summary>
    /// Given a valid stream case whose <c>matchSalt</c> is <c>"fedcba9876543210"</c> and whose config
    /// entries include <c>{ "fieldId": "0x1007", "path": "movement.gravity", "valueBits": "0x411CF5C3" }</c>,
    /// and a valid collision case whose <c>heights[8]</c> is <c>{ "bits": "0x80000000", "value": -0.0 }</c>,
    /// when each is parsed, then the salt is <c>0xFEDCBA9876543210</c>, the entry has <c>fieldId</c>
    /// <c>0x1007</c> and <c>valueBits</c> <c>0x411CF5C3</c>, and the height has exactly the bits
    /// <c>0x80000000</c>, negative zero. The other x16 values (seed, collision checksum, trailer checksum)
    /// are read the same way.
    /// </summary>
    [Fact]
    public void ParsesX16FloatObjectsAndBitStrings()
    {
        var streamJson = SeededStreamCase().ToJsonString();
        Assert.Contains("\"matchSalt\":\"fedcba9876543210\"", streamJson, StringComparison.Ordinal);
        Assert.Contains("{\"fieldId\":\"0x1007\",\"path\":\"movement.gravity\",\"valueBits\":\"0x411CF5C3\"}", streamJson, StringComparison.Ordinal);

        var stream = Assert.IsType<StreamValidCase>(Parse(streamJson));
        Assert.Equal(0xFEDCBA9876543210UL, stream.Stream.MatchSalt);
        Assert.Equal(SeededSeed, stream.Stream.Seed);
        Assert.Equal(TinyWorldChecksum, stream.Stream.CollisionChecksum);
        Assert.Equal(StreamTrailer(SeededStreamFile()), stream.Checksum);
        var gravity = Assert.Single(stream.ConfigEntries, entry => entry.FieldId == 0x1007);
        Assert.Equal(new GoldenConfigEntry(0x1007, "movement.gravity", 0x411CF5C3u), gravity);

        var collisionJson = TinyWorldCase().ToJsonString();
        Assert.Contains("{\"bits\":\"0x80000000\",\"value\":-0.0}", collisionJson, StringComparison.Ordinal);

        var collision = Assert.IsType<CollisionValidCase>(Parse(collisionJson));
        Assert.Equal(NegativeZeroBits, BitConverter.SingleToUInt32Bits(collision.Data.Heights[8]));
        Assert.Equal(TinyWorldChecksum, collision.Checksum);
    }

    /// <summary>
    /// Given the seeded stream case and the <c>tiny-world</c> collision case, when each is parsed, then the
    /// stream case keeps the lengths its JSON records (<c>configLength</c> 698, <c>initialStateLength</c> 136,
    /// <c>stepsLength</c> 44, <c>fileLength</c> 950), each equal to the header field its encoding writes,
    /// and the collision case keeps <c>fileLength</c> 188, the length of its encoding, and its
    /// <c>derived</c> object: 8 heightfield triangles, mesh triangle IDs [8, 9, 10] of which [10] is
    /// degenerate, and a 1 × 1 grid whose one cell holds [8, 9].
    /// </summary>
    [Fact]
    public void KeepsTheRecordedLengthsAndDerivedData()
    {
        var stream = Assert.IsType<StreamValidCase>(Parse(SeededStreamCase().ToJsonString()));
        Assert.Equal(698L, stream.ConfigLength);
        Assert.Equal(136L, stream.InitialStateLength);
        Assert.Equal(44L, stream.StepsLength);
        Assert.Equal(950L, stream.FileLength);
        var encoded = stream.EncodeBin();
        Assert.Equal(stream.FileLength, encoded.Length);
        Assert.Equal(stream.ConfigLength, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(StreamConfigLengthOffset)));
        Assert.Equal(stream.InitialStateLength, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(StreamConfigLengthOffset + 4)));
        Assert.Equal(stream.StepsLength, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(StreamConfigLengthOffset + 8)));

        var collision = Assert.IsType<CollisionValidCase>(Parse(TinyWorldCase().ToJsonString()));
        Assert.Equal(188L, collision.FileLength);
        Assert.Equal(collision.FileLength, collision.EncodeBin().Length);
        var derived = collision.Derived;
        Assert.Equal(8, derived.HeightfieldTriangleCount);
        int[] meshTriangleIds = [8, 9, 10];
        Assert.Equal(meshTriangleIds, derived.MeshTriangleIds);
        int[] degenerateTriangleIds = [10];
        Assert.Equal(degenerateTriangleIds, derived.DegenerateTriangleIds);
        Assert.Equal(1, derived.GridCellsX);
        Assert.Equal(1, derived.GridCellsZ);
        int[] cellTriangleIds = [8, 9];
        Assert.Equal(cellTriangleIds, Assert.Single(derived.Cells));
    }

    /// <summary>
    /// Given a case with one malformed value, when it is parsed, then it throws
    /// <see cref="GoldenCaseException"/> whose message is exactly <c>&lt;JSON path&gt;: &lt;problem&gt;</c>:
    /// an x16 with uppercase letters, 15 digits or a <c>0x</c> prefix; float <c>bits</c> in lowercase or
    /// with 7 digits; a float <c>value</c> that does not parse to its bits, or that is a string; a
    /// <c>fieldId</c> with 3 digits; an unknown <c>kind</c>; a refusal with neither <c>expectedError</c> nor
    /// <c>expectedConfigErrors</c>, or with both; <c>aimYaw</c> 65536, which does not fit a u16, or written as
    /// a string, a fraction or a number beyond 64 bits; a <c>formatVersion</c> other than the format's; a
    /// field the case's shape does not have; an enum name that is no member; the code <c>None</c>; a
    /// <c>refusedAt</c> other than <c>open</c>, or on an input case; configuration errors or
    /// <c>collisionFixtures</c> on a case that is not refused when opened, and no <c>collisionFixtures</c> on
    /// one that is; a mutation with an unknown op, odd hex, a missing argument or one its op
    /// does not take; collision and stream counts that disagree with their lists (<c>heights</c>,
    /// <c>meshCount</c>, a vertex that is not a triple, indices that are not whole triangles,
    /// <c>stepCount</c>, <c>characterCount</c>, 256 commands in a step); config entries that are not
    /// ascending, that miss a field, or whose <c>path</c> is not their field's; and uppercase state hex.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedValues))]
    public void RefusesMalformedValuesNamingTheirPath(string json, string expectedMessage)
    {
        var thrown = Assert.Throws<GoldenCaseException>(() => Parse(json));

        Assert.Equal(expectedMessage, thrown.Message);
    }

    /// <summary>
    /// Parses <paramref name="json"/> with <c>GoldenJson.Parse</c>, passing its own <c>format</c> as the
    /// folder and <c>&lt;case&gt;.json</c> as the file name, as <c>golden write</c> would for a committed case.
    /// </summary>
    private static GoldenCase Parse(string json)
    {
        string format;
        string caseName;
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            format = root.GetProperty("format").GetString()!;
            caseName = root.GetProperty("case").GetString()!;
        }

        return GoldenJson.Parse(format, caseName + ".json", Encoding.UTF8.GetBytes(json));
    }

    private static JsonObject Node(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary><c>config-invalid</c>'s <c>expectedConfigErrors</c>: <c>movement.gravity: must be finite</c>.</summary>
    private static JsonArray GravityConfigErrors() =>
        [new JsonObject { ["path"] = "movement.gravity", ["message"] = "must be finite" }];

    /// <summary><paramref name="array"/> without its last element.</summary>
    private static void RemoveLast(JsonArray array) => array.RemoveAt(array.Count - 1);

    /// <summary><paramref name="array"/> with its first two elements swapped.</summary>
    private static void SwapFirstTwo(JsonArray array)
    {
        var first = array[0]!.DeepClone();
        array[0] = array[1]!.DeepClone();
        array[1] = first;
    }

    /// <summary><paramref name="count"/> copies of step 1's first command of a stream case.</summary>
    private static JsonArray RepeatedCommand(JsonObject root, int count)
    {
        var command = root["steps"]![0]!["commands"]![0]!;
        var commands = new JsonArray();
        for (var c = 0; c < count; c++)
        {
            commands.Add(command.DeepClone());
        }

        return commands;
    }

    /// <summary>
    /// <paramref name="root"/> with <paramref name="edit"/> applied, as JSON text with
    /// <paramref name="firstKey"/> written first and every other property after it in its order.
    /// </summary>
    private static string Edited(JsonObject root, string firstKey, Action<JsonObject> edit)
    {
        edit(root);
        var ordered = new JsonObject { [firstKey] = root[firstKey]?.DeepClone() };
        foreach (var (key, value) in root)
        {
            if (key != firstKey)
            {
                ordered[key] = value?.DeepClone();
            }
        }

        return ordered.ToJsonString();
    }

    /// <summary>
    /// The <c>seeded</c> stream case as JSON: <c>two-steps</c>' content (rules revision 1, 30 Hz, one
    /// character, the placeholder configuration's 116 entries, the worked example state, two steps, the
    /// first carrying the <c>example</c> command for index 0) with <c>seed</c> 7 and <c>matchSalt</c>
    /// <c>fedcba9876543210</c>. Its lengths and checksum are those of <see cref="SeededStreamFile"/>.
    /// </summary>
    private static JsonObject SeededStreamCase()
    {
        var configBlock = PlaceholderConfigBlock();
        var state = TestStates.WorkedExample;
        var steps = StepsBlock();
        var file = SeededStreamFile();

        var placeholder = TestConfigs.Default();
        var fields = ConfigFields.Expand(placeholder.Weapons!.Count, placeholder.ProjectileClasses!.Count);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(configBlock);
        var config = new JsonArray();
        for (var k = 0; k < count; k++)
        {
            var entry = configBlock.AsSpan(ConfigBlock.CountLength + (ConfigBlock.EntryLength * k));
            var fieldId = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var valueBits = BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]);
            config.Add(new JsonObject
            {
                ["fieldId"] = string.Create(CultureInfo.InvariantCulture, $"0x{fieldId:X4}"),
                ["path"] = fields.First(field => field.FieldId == fieldId).Path,
                ["valueBits"] = string.Create(CultureInfo.InvariantCulture, $"0x{valueBits:X8}"),
            });
        }

        var exampleCommand = new JsonObject
        {
            ["characterIndex"] = 0,
            ["sequence"] = 1,
            ["clientStep"] = 100,
            ["buttons"] = 129,
            ["aimYaw"] = 16384,
            ["aimPitch"] = -1024,
            ["weaponSlot"] = 0,
        };

        return new JsonObject
        {
            ["format"] = "stream",
            ["formatVersion"] = 1,
            ["case"] = "seeded",
            ["kind"] = "valid",
            ["rulesRevision"] = 1,
            ["stepHz"] = 30,
            ["characterCount"] = 1,
            ["stepCount"] = 2,
            ["seed"] = X16(SeededSeed),
            ["matchSalt"] = X16(HighMatchSalt),
            ["collisionChecksum"] = X16(TinyWorldChecksum),
            ["config"] = config,
            ["initialStateHex"] = Convert.ToHexStringLower(state),
            ["steps"] = new JsonArray
            {
                new JsonObject { ["step"] = 1, ["commands"] = new JsonArray { exampleCommand }, ["expectedHash"] = X16(1) },
                new JsonObject { ["step"] = 2, ["commands"] = new JsonArray(), ["expectedHash"] = X16(2) },
            },
            ["configLength"] = configBlock.Length,
            ["initialStateLength"] = state.Length,
            ["stepsLength"] = steps.Length,
            ["fileLength"] = file.Length,
            ["checksum"] = X16(StreamTrailer(file)),
        };
    }

    /// <summary>
    /// The <c>seeded</c> stream's bytes, written from contracts/recorded-stream-format.md#layout with
    /// <see cref="BinaryPrimitives"/>: the 64-byte header, the config block, the worked example state, the
    /// steps block and the XxHash64 (seed 0) trailer.
    /// </summary>
    private static byte[] SeededStreamFile()
    {
        var configBlock = PlaceholderConfigBlock();
        var state = TestStates.WorkedExample;
        var steps = StepsBlock();
        var stateStart = StreamHeaderLength + configBlock.Length;
        var stepsStart = stateStart + state.Length;
        var file = new byte[stepsStart + steps.Length + TrailerLength];
        var span = file.AsSpan();
        "MMRS"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], StreamHeaderLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 30);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 2);
        BinaryPrimitives.WriteUInt64LittleEndian(span[20..], SeededSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(span[28..], HighMatchSalt);
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], TinyWorldChecksum);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)configBlock.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], (uint)state.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], (uint)steps.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[56..], (ulong)file.Length);
        configBlock.CopyTo(span[StreamHeaderLength..]);
        state.CopyTo(span[stateStart..]);
        steps.CopyTo(span[stepsStart..]);
        var trailer = file.Length - TrailerLength;
        BinaryPrimitives.WriteUInt64LittleEndian(span[trailer..], XxHash64.HashToUInt64(span[..trailer], 0));
        return file;
    }

    /// <summary>The config block of <c>simcore/config/default.json</c>: 116 entries, 698 bytes.</summary>
    private static byte[] PlaceholderConfigBlock() => ConfigBlock.Encode(TestConfigs.Default());

    /// <summary>
    /// <c>two-steps</c>' 44-byte steps block: step 1 with one command, character index 0 and the
    /// <c>example</c> input command, expected hash 1; then step 2 with no command, expected hash 2.
    /// </summary>
    private static byte[] StepsBlock()
    {
        var block = new byte[31 + 13];
        var span = block.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 1);
        span[4] = 1;
        span[5] = 0;
        Convert.FromHexString(ExampleCommandHex).CopyTo(span[6..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[23..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[31..], 2);
        span[35] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], 2);
        return block;
    }

    private static ulong StreamTrailer(byte[] file) => BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(file.Length - TrailerLength));

    /// <summary>
    /// The <c>tiny-world</c> collision case as JSON (contracts/collision-data-format.md#tiny-world): its
    /// header fields, heights (<c>heights[8]</c> written <c>-0.0</c>) and mesh as float objects,
    /// <c>fileLength</c> 188, checksum <c>9224e3fa3b3fca9c</c> and its derived data.
    /// </summary>
    private static JsonObject TinyWorldCase()
    {
        var data = TestWorlds.TinyWorld();
        var heights = new JsonArray();
        foreach (var height in data.Heights)
        {
            heights.Add(FloatObject(height));
        }

        var meshes = new JsonArray();
        foreach (var mesh in data.Meshes)
        {
            var vertices = new JsonArray();
            for (var v = 0; v < mesh.Vertices.Length; v += 3)
            {
                vertices.Add(new JsonArray(FloatObject(mesh.Vertices[v]), FloatObject(mesh.Vertices[v + 1]), FloatObject(mesh.Vertices[v + 2])));
            }

            var indices = new JsonArray();
            foreach (var index in mesh.Indices)
            {
                indices.Add(JsonValue.Create(index));
            }

            meshes.Add(new JsonObject { ["meshId"] = mesh.MeshId, ["vertices"] = vertices, ["indices"] = indices });
        }

        return new JsonObject
        {
            ["format"] = "collision",
            ["formatVersion"] = 1,
            ["case"] = "tiny-world",
            ["kind"] = "valid",
            ["originX"] = FloatObject(data.OriginX),
            ["originZ"] = FloatObject(data.OriginZ),
            ["cellSize"] = FloatObject(data.CellSize),
            ["gridCellSize"] = FloatObject(data.GridCellSize),
            ["columns"] = data.Columns,
            ["rows"] = data.Rows,
            ["meshCount"] = data.Meshes.Length,
            ["heights"] = heights,
            ["meshes"] = meshes,
            ["fileLength"] = 188,
            ["checksum"] = X16(TinyWorldChecksum),
            ["derived"] = new JsonObject
            {
                ["heightfieldTriangleCount"] = 8,
                ["meshTriangleIds"] = new JsonArray { 8, 9, 10 },
                ["degenerateTriangleIds"] = new JsonArray { 10 },
                ["gridCellsX"] = 1,
                ["gridCellsZ"] = 1,
                ["cells"] = new JsonArray { new JsonArray { 8, 9 } },
            },
        };
    }

    /// <summary>
    /// A float object <c>{ "bits": "0x…", "value": … }</c>: uppercase bits, and the round-trip ("R")
    /// decimal, except negative zero, which the contract writes <c>-0.0</c>.
    /// </summary>
    private static JsonNode FloatObject(float value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value);
        var text = bits == NegativeZeroBits ? "-0.0" : value.ToString("R", CultureInfo.InvariantCulture);
        return FloatObject(string.Create(CultureInfo.InvariantCulture, $"0x{bits:X8}"), text);
    }

    /// <summary>A float object with exactly this <c>bits</c> string and this raw <c>value</c> text, which the JSON keeps as written.</summary>
    private static JsonNode FloatObject(string bits, string valueText) =>
        JsonNode.Parse($$"""{ "bits": "{{bits}}", "value": {{valueText}} }""")!;

    private static string X16(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
}
