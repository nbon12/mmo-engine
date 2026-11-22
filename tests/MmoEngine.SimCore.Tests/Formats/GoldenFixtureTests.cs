using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server.Golden;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The golden fixtures' valid cases and the committed fixture set (FR-020, FR-023, US6 scenario 1,
/// contracts/input-command-format.md#shared-fixture-rules): every valid case's <c>.json</c>, parsed with
/// <c>GoldenJson</c> and encoded, gives its <c>.bin</c> byte for byte, and decoding the <c>.bin</c> gives
/// the JSON's values back; every case of the contracts' tables is committed as a pair and nothing else
/// is; the JSON's floats, 64-bit values and bit patterns are written in the contract's forms; and the
/// input, collision and stream cases carry their validation outcome, derived data and rule-table paths.
/// </summary>
/// <remarks>
/// The decoded values are compared with the JSON as <see cref="JsonDocument"/> reads it, not with what
/// <c>GoldenJson</c> parsed, so a parser that misreads a field cannot agree with itself through
/// <c>golden write</c>'s output. Values the contracts pin (lengths, checksums, derived data, validation
/// outcomes) are also checked against literals here. Every fixture is opened read-only through
/// <see cref="GoldenFixtures"/>.
/// </remarks>
public sealed partial class GoldenFixtureTests
{
    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>The collision files' <c>u64 fileLength</c> offset.</summary>
    private const int CollisionFileLengthOffset = 8;

    /// <summary>The stream header's <c>u32 configLength</c>, <c>u32 initialStateLength</c>, <c>u32 stepsLength</c> and <c>u64 fileLength</c> offsets.</summary>
    private const int ConfigLengthOffset = 44;
    private const int InitialStateLengthOffset = 48;
    private const int StepsLengthOffset = 52;
    private const int StreamFileLengthOffset = 56;

    private const int TrailerLength = 8;

    /// <summary>Negative zero's bits: <c>tiny-world</c>'s <c>heights[8]</c>, kept bit for bit.</summary>
    private const uint NegativeZeroBits = 0x80000000u;

    /// <summary>
    /// The validation outcome of every valid input case, from the "Validates as" column of
    /// contracts/input-command-format.md#input-cases.
    /// </summary>
    private static readonly Dictionary<string, InputRejection> ContractValidation = new(StringComparer.Ordinal)
    {
        ["neutral"] = InputRejection.None,
        ["example"] = InputRejection.None,
        ["button-forward"] = InputRejection.None,
        ["button-back"] = InputRejection.None,
        ["button-left"] = InputRejection.None,
        ["button-right"] = InputRejection.None,
        ["button-jump"] = InputRejection.None,
        ["button-crouch"] = InputRejection.None,
        ["button-walk"] = InputRejection.None,
        ["button-fire"] = InputRejection.None,
        ["button-reload"] = InputRejection.None,
        ["all-buttons"] = InputRejection.None,
        ["aim-yaw-max"] = InputRejection.None,
        ["aim-pitch-up"] = InputRejection.None,
        ["aim-pitch-down"] = InputRejection.None,
        ["sequence-max"] = InputRejection.None,
        ["weapon-slot-last"] = InputRejection.None,
        ["reject-sequence-zero"] = InputRejection.SequenceNotIncreasing,
        ["reject-sequence-repeat"] = InputRejection.SequenceNotIncreasing,
        ["reject-sequence-first"] = InputRejection.SequenceNotIncreasing,
        ["reject-button-9"] = InputRejection.UnknownButtons,
        ["reject-buttons-reserved"] = InputRejection.UnknownButtons,
        ["reject-buttons-all"] = InputRejection.UnknownButtons,
        ["reject-pitch-above"] = InputRejection.PitchOutOfRange,
        ["reject-pitch-below"] = InputRejection.PitchOutOfRange,
        ["reject-pitch-min"] = InputRejection.PitchOutOfRange,
        ["reject-pitch-max"] = InputRejection.PitchOutOfRange,
        ["reject-weapon-slot"] = InputRejection.UnknownWeaponSlot,
        ["reject-weapon-slot-max"] = InputRejection.UnknownWeaponSlot,
    };

    /// <summary>
    /// The collision valid cases' file lengths: contracts/collision-data-format.md#tiny-world (188), and
    /// #other-valid-cases (<c>heightfield-only</c> 92, <c>full-cell</c> 3176); <c>extent-at-limit</c> is
    /// <c>tiny-world</c> with another origin, so it has <c>tiny-world</c>'s layout.
    /// </summary>
    private static readonly Dictionary<string, long> ContractCollisionFileLengths = new(StringComparer.Ordinal)
    {
        ["tiny-world"] = 188,
        ["heightfield-only"] = 92,
        ["extent-at-limit"] = 188,
        ["full-cell"] = 3176,
    };

    /// <summary>
    /// The derived data of each collision valid case, from T216's table: heightfield triangle count, mesh
    /// triangle IDs, degenerate (dropped) IDs, grid cells along X and Z, and each cell's triangle IDs.
    /// </summary>
    private static readonly Dictionary<string, PinnedDerived> ContractDerived = new(StringComparer.Ordinal)
    {
        ["tiny-world"] = new(8, [8, 9, 10], [10], 1, 1, [[8, 9]]),
        ["extent-at-limit"] = new(8, [8, 9, 10], [10], 1, 1, [[8, 9]]),
        ["heightfield-only"] = new(8, [], [], 1, 1, [[]]),
        ["full-cell"] = new(8, [.. Enumerable.Range(8, 64)], [], 1, 1, [[.. Enumerable.Range(8, 64)]]),
    };

    /// <summary>
    /// US6 scenario 1: given each committed valid case, when its <c>.json</c> is parsed with
    /// <c>GoldenJson.Parse</c> and encoded (<c>InputCodec.TryEncode</c> into 17 bytes,
    /// <c>CollisionFile.Encode</c> or <c>RecordedStream.Encode</c>), then the bytes equal the <c>.bin</c>;
    /// when the <c>.bin</c> is decoded, then every decoded value equals the JSON's; and the decoded value
    /// re-encodes to the <c>.bin</c>. Collision and stream cases also carry their encoded lengths and
    /// trailer checksum in the JSON.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCases), MemberType = typeof(GoldenFixtures))]
    public void EachValidFixtureRoundTripsToItsBytes(string format, string caseName)
    {
        var label = $"{format}/{caseName}";
        var bin = GoldenFixtures.ReadBin(format, caseName);
        var parsed = Parse(format, caseName);
        using var json = GoldenFixtures.ReadJson(format, caseName);
        var root = json.RootElement;

        switch (format)
        {
            case InputFormat:
                InputRoundTrip(label, Assert.IsType<InputValidCase>(parsed), root, bin);
                break;
            case CollisionFormat:
                CollisionRoundTrip(label, caseName, Assert.IsType<CollisionValidCase>(parsed), root, bin);
                break;
            case StreamFormat:
                StreamRoundTrip(label, caseName, Assert.IsType<StreamValidCase>(parsed), root, bin);
                break;
            default:
                Assert.Fail($"{label}: no golden format {format}");
                break;
        }
    }

    /// <summary>
    /// FR-023: the case names that have both a <c>.json</c> and a <c>.bin</c> in
    /// <c>simcore/golden/&lt;format&gt;/</c> are exactly the contract's (38 input, 22 collision and 32
    /// stream cases), the folder holds nothing else, and every JSON names its folder as its
    /// <c>format</c>, <c>formatVersion</c> 1, its file name as its <c>case</c>, and the kind the contract's
    /// table gives it.
    /// </summary>
    [Theory]
    [InlineData(InputFormat)]
    [InlineData(CollisionFormat)]
    [InlineData(StreamFormat)]
    public void EveryContractCaseIsCommitted(string format)
    {
        var (total, valid) = format switch
        {
            InputFormat => (38, 29),
            CollisionFormat => (22, 4),
            StreamFormat => (32, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no golden format"),
        };
        var contract = GoldenFixtures.ContractCaseNames(format);
        Assert.True(contract.Count == total, $"{format}: the contract lists {total} cases; ContractCaseNames holds {contract.Count}");
        var expected = contract.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            expected.Distinct(StringComparer.Ordinal).Count() == total,
            $"{format}: ContractCaseNames repeats a name: {string.Join(", ", expected)}");

        var entries = Directory.GetFileSystemEntries(Path.Combine(GoldenFixtures.Root, format))
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var jsonNames = NamesWithExtension(entries, ".json");
        var binNames = NamesWithExtension(entries, ".bin");
        var paired = jsonNames.Where(binNames.Contains).Order(StringComparer.Ordinal).ToArray();
        var missing = expected.Except(paired, StringComparer.Ordinal).ToArray();
        var extra = paired.Except(expected, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"{format}/: {missing.Length} contract cases have no .json and .bin pair [{string.Join(", ", missing)}]; {extra.Length} pairs are no contract case [{string.Join(", ", extra)}]");
        Assert.Equal(expected, paired);

        var allowed = expected.SelectMany(name => new[] { name + ".json", name + ".bin" }).ToHashSet(StringComparer.Ordinal);
        var others = entries.Where(entry => !allowed.Contains(entry)).ToArray();
        Assert.True(others.Length == 0, $"{format}/ holds entries that are not a contract case's .json and .bin: {string.Join(", ", others)}");

        for (var i = 0; i < contract.Count; i++)
        {
            var name = contract[i];
            var label = $"{format}/{name}.json";
            using var json = GoldenFixtures.ReadJson(format, name);
            var root = json.RootElement;
            Assert.True(Text(root, "format", label) == format, $"{label}: format is {root.GetProperty("format").GetRawText()}, expected \"{format}\"");
            Assert.True(Integer(root, "formatVersion", label, 0, int.MaxValue) == 1, $"{label}: formatVersion is {root.GetProperty("formatVersion").GetRawText()}, expected 1");
            Assert.True(Text(root, "case", label) == name, $"{label}: case is {root.GetProperty("case").GetRawText()}, expected \"{name}\"");
            var kind = i < valid ? "valid" : "refusal";
            Assert.True(Text(root, "kind", label) == kind, $"{label}: kind is {root.GetProperty("kind").GetRawText()}; the contract's table makes it \"{kind}\"");
        }
    }

    /// <summary>
    /// The JSON value forms of the shared fixture rules: every float object <c>{bits, value}</c> has
    /// <c>bits</c> as <c>0x</c> and 8 uppercase hex digits, and its <c>value</c>'s raw JSON text parses
    /// (<c>float.Parse</c>, invariant culture) to exactly those bits (<c>-0.0</c> gives <c>0x80000000</c>);
    /// every 64-bit value is 16 lowercase hex digits; every <c>fieldId</c> is <c>0x</c> and 4 uppercase
    /// hex digits and every <c>valueBits</c> <c>0x</c> and 8. Each format has these values exactly where
    /// its contract puts them, so a value written as a plain number is counted missing.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCases), MemberType = typeof(GoldenFixtures))]
    public void FloatValuesParseBackToTheirBits(string format, string caseName)
    {
        var label = $"{format}/{caseName}.json";
        using var json = GoldenFixtures.ReadJson(format, caseName);
        var root = json.RootElement;
        var scan = new BitScan();
        Scan(root, "$", scan);
        Assert.True(scan.Problems.Count == 0, $"{label}:{Environment.NewLine}{string.Join(Environment.NewLine, scan.Problems)}");

        var (floats, x16s, entries) = format switch
        {
            InputFormat => (0, 0, 0),
            CollisionFormat => (CollisionFloatCount(root, label), 1, 0),
            StreamFormat => (0, 4 + ArrayOf(root, "steps", label).GetArrayLength(), ArrayOf(root, "config", label).GetArrayLength()),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no golden format"),
        };
        Assert.True(scan.Floats == floats, $"{label}: {scan.Floats} float objects; the format's fields hold {floats}");
        Assert.True(scan.X16s == x16s, $"{label}: {scan.X16s} 64-bit values; the format's fields hold {x16s}");
        Assert.True(scan.FieldIds == entries, $"{label}: {scan.FieldIds} fieldIds; expected {entries}, one per config entry");
        Assert.True(scan.ValueBits == entries, $"{label}: {scan.ValueBits} valueBits; expected {entries}, one per config entry");

        if (format == CollisionFormat)
        {
            // Every collision valid case has tiny-world's heightfield, whose last sample is negative zero.
            var last = ArrayOf(root, "heights", label)[8];
            Assert.True(
                Text(last, "bits", label + " $.heights[8]") == "0x80000000" && Property(last, "value", label + " $.heights[8]").GetRawText() == "-0.0",
                $"{label}: $.heights[8] is {last.GetRawText()}, expected {{ \"bits\": \"0x80000000\", \"value\": -0.0 }}");
        }
    }

    /// <summary>
    /// Every valid input case records its validation: <c>InputValidator.Validate(command,
    /// validate.lastSequence, validate.weaponSlots)</c> equals <c>validate.expected</c>, which is the
    /// contract's "Validates as" value (for example <c>reject-sequence-first</c> gives
    /// <c>SequenceNotIncreasing</c>, <c>reject-buttons-all</c> <c>UnknownButtons</c>,
    /// <c>reject-pitch-min</c> <c>PitchOutOfRange</c> and <c>weapon-slot-last</c> <c>None</c>), with
    /// <c>lastSequence</c> 0 (5 for <c>reject-sequence-repeat</c>) and <c>weaponSlots</c> 2.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCaseNames), InputFormat, MemberType = typeof(GoldenFixtures))]
    public void InputCasesValidateAsRecorded(string caseName)
    {
        var label = $"input/{caseName}.json";
        var parsed = Assert.IsType<InputValidCase>(Parse(InputFormat, caseName));
        using var json = GoldenFixtures.ReadJson(InputFormat, caseName);
        var validate = Property(json.RootElement, "validate", label);
        var lastSequence = (uint)Integer(validate, "lastSequence", label + " validate", 0, uint.MaxValue);
        var weaponSlots = (int)Integer(validate, "weaponSlots", label + " validate", 0, int.MaxValue);
        var expected = Enum.Parse<InputRejection>(Text(validate, "expected", label + " validate"));

        Assert.True(ContractValidation.TryGetValue(caseName, out var contract), $"{label}: not a valid input case of the contract");
        Assert.True(expected == contract, $"{label}: validate.expected is {expected}; the contract's table gives {contract}");
        var contractLastSequence = caseName == "reject-sequence-repeat" ? 5u : 0u;
        Assert.True(
            lastSequence == contractLastSequence && weaponSlots == 2,
            $"{label}: validates with lastSequence {lastSequence} and weaponSlots {weaponSlots}; the contract gives {contractLastSequence} and 2");
        Assert.True(
            parsed.Validate.LastSequence == lastSequence && parsed.Validate.WeaponSlots == weaponSlots && parsed.Validate.Expected == expected,
            $"{label}: GoldenJson parsed validate as ({parsed.Validate.LastSequence}, {parsed.Validate.WeaponSlots}, {parsed.Validate.Expected}); the JSON holds ({lastSequence}, {weaponSlots}, {expected})");

        var actual = InputValidator.Validate(parsed.Command, lastSequence, weaponSlots);

        Assert.True(actual == expected, $"{label}: InputValidator.Validate gives {actual} for {parsed.Command}; the case records {expected}");
    }

    /// <summary>
    /// Every valid collision case carries the world its decoder builds: the decoded world's heightfield
    /// triangle count, mesh triangle IDs, degenerate (dropped) triangle IDs and count, grid cells along X
    /// and Z, and each cell's triangle list (through <c>world.Grid.CellStart</c> and
    /// <c>CellTriangles</c>) equal the JSON's <c>derived</c> object, and that object equals T216's pinned
    /// table: <c>tiny-world</c> and <c>extent-at-limit</c> 8, [8, 9, 10], [10], 1 × 1, [[8, 9]];
    /// <c>heightfield-only</c> 8, [], [], 1 × 1, [[]]; <c>full-cell</c> 8, 8 to 71, none, 1 × 1, [[8, …, 71]].
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCaseNames), CollisionFormat, MemberType = typeof(GoldenFixtures))]
    public void CollisionCasesHaveTheirDerivedData(string caseName)
    {
        var label = $"collision/{caseName}";
        var world = CollisionFile.Decode(GoldenFixtures.ReadBin(CollisionFormat, caseName));
        using var json = GoldenFixtures.ReadJson(CollisionFormat, caseName);
        var derived = Property(json.RootElement, "derived", label + ".json");
        var recorded = new PinnedDerived(
            (int)Integer(derived, "heightfieldTriangleCount", label + ".json derived", 0, int.MaxValue),
            IntArray(ArrayOf(derived, "meshTriangleIds", label + ".json derived"), label + ".json derived.meshTriangleIds"),
            IntArray(ArrayOf(derived, "degenerateTriangleIds", label + ".json derived"), label + ".json derived.degenerateTriangleIds"),
            (int)Integer(derived, "gridCellsX", label + ".json derived", 0, int.MaxValue),
            (int)Integer(derived, "gridCellsZ", label + ".json derived", 0, int.MaxValue),
            [.. ArrayOf(derived, "cells", label + ".json derived").EnumerateArray().Select((cell, c) => IntArray(cell, $"{label}.json derived.cells[{c}]"))]);

        Assert.True(ContractDerived.TryGetValue(caseName, out var contract), $"{label}: not a valid collision case of the contract");
        AssertDerived($"{label}.json's derived against the pinned table", contract, recorded);

        var meshIds = Enumerable.Range(world.Meshes.FirstId, world.MeshTriangleCount).ToArray();
        var dropped = meshIds.Where(id => !world.Meshes.TryGetMeshTriangle(id, out _)).ToArray();
        Assert.True(
            world.DegenerateTriangleCount == dropped.Length,
            $"{label}: DegenerateTriangleCount is {world.DegenerateTriangleCount}, but {dropped.Length} mesh IDs are unused");
        var cellStart = world.Grid.CellStart.ToArray();
        var cellTriangles = world.Grid.CellTriangles.ToArray();
        var cellCount = world.GridCellsX * world.GridCellsZ;
        Assert.True(cellStart.Length == cellCount + 1, $"{label}: CellStart holds {cellStart.Length} offsets for {cellCount} cells");
        int[][] cells = [.. Enumerable.Range(0, cellCount).Select(c => cellTriangles[cellStart[c]..cellStart[c + 1]])];
        var built = new PinnedDerived(world.HeightfieldTriangleCount, meshIds, dropped, world.GridCellsX, world.GridCellsZ, cells);

        AssertDerived($"{label}'s decoded world against its .json's derived", recorded, built);
    }

    /// <summary>
    /// Every valid stream case writes its config entries in strictly ascending <c>fieldId</c>, one per row
    /// of <c>ConfigFields.Expand</c> for the stream configuration's weapon and projectile-class counts, each
    /// with that row's <c>Path</c>; <c>two-steps</c> holds exactly the 116 entries of
    /// <c>simcore/config/default.json</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCaseNames), StreamFormat, MemberType = typeof(GoldenFixtures))]
    public void StreamConfigEntriesNameTheirRuleTablePaths(string caseName)
    {
        var label = $"stream/{caseName}.json";
        var config = RecordedStream.Decode(GoldenFixtures.ReadBin(StreamFormat, caseName)).Config;
        var weaponCount = config.Weapons!.Count;
        var classCount = config.ProjectileClasses!.Count;
        var rows = ConfigFields.Expand(weaponCount, classCount);
        var paths = rows.ToDictionary(row => row.FieldId, row => row.Path);
        using var json = GoldenFixtures.ReadJson(StreamFormat, caseName);
        var entries = ArrayOf(json.RootElement, "config", label);

        var fieldIds = new List<ushort>();
        var problems = new List<string>();
        var k = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var where = $"{label} config[{k}]";
            var fieldId = (ushort)HexBits(Property(entry, "fieldId", where), where + ".fieldId", 4);
            var path = Text(entry, "path", where);
            if (fieldIds.Count > 0 && fieldId <= fieldIds[^1])
            {
                problems.Add($"{where}: fieldId 0x{fieldId:X4} is not above 0x{fieldIds[^1]:X4}");
            }

            if (!paths.TryGetValue(fieldId, out var rulePath))
            {
                problems.Add($"{where}: fieldId 0x{fieldId:X4} is no row of ConfigFields.Expand({weaponCount}, {classCount})");
            }
            else if (path != rulePath)
            {
                problems.Add($"{where}: fieldId 0x{fieldId:X4} has path {path}; the rule table's is {rulePath}");
            }

            fieldIds.Add(fieldId);
            k++;
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(
            fieldIds.SequenceEqual(rows.Select(row => row.FieldId)),
            $"{label}: {fieldIds.Count} config entries; ConfigFields.Expand gives {rows.Length}, one entry per field");
        if (caseName == "two-steps")
        {
            Assert.True(fieldIds.Count == 116, $"{label}: {fieldIds.Count} config entries, expected the 116 of default.json");
        }
    }

    private static GoldenCase Parse(string format, string caseName) =>
        GoldenJson.Parse(format, caseName + ".json", GoldenFixtures.ReadJsonBytes(format, caseName));

    private static void InputRoundTrip(string label, InputValidCase parsed, JsonElement root, byte[] bin)
    {
        var encoded = new byte[InputCodec.EncodedSize];
        Assert.True(InputCodec.TryEncode(parsed.Command, encoded), $"{label}: TryEncode refused a 17-byte buffer");
        AssertBytes($"{label}: the encoding of its .json", bin, encoded);

        Assert.True(InputCodec.TryDecode(bin, out var decoded, out var error), $"{label}: TryDecode refused the .bin: {error}");
        var command = Property(root, "command", label);
        var path = label + " command";
        AssertField(label, "sequence", (uint)Integer(command, "sequence", path, 0, uint.MaxValue), decoded.Sequence);
        AssertField(label, "clientStep", (uint)Integer(command, "clientStep", path, 0, uint.MaxValue), decoded.ClientStep);
        AssertField(label, "buttons", (ushort)Integer(command, "buttons", path, 0, ushort.MaxValue), (ushort)decoded.Buttons);
        AssertField(label, "aimYaw", (ushort)Integer(command, "aimYaw", path, 0, ushort.MaxValue), decoded.AimYaw);
        AssertField(label, "aimPitch", (short)Integer(command, "aimPitch", path, short.MinValue, short.MaxValue), decoded.AimPitch);
        AssertField(label, "weaponSlot", (byte)Integer(command, "weaponSlot", path, 0, byte.MaxValue), decoded.WeaponSlot);

        var reencoded = new byte[InputCodec.EncodedSize];
        Assert.True(InputCodec.TryEncode(decoded, reencoded), $"{label}: TryEncode refused the decoded command");
        AssertBytes($"{label}: the re-encoding of the decoded command", bin, reencoded);
    }

    private static void CollisionRoundTrip(string label, string caseName, CollisionValidCase parsed, JsonElement root, byte[] bin)
    {
        var encoded = CollisionFile.Encode(parsed.Data);
        AssertBytes($"{label}: the encoding of its .json", bin, encoded);

        var world = CollisionFile.Decode(bin);
        var data = world.Data;
        AssertField(label, "originX", FloatBits(Property(root, "originX", label), label + " originX"), BitConverter.SingleToUInt32Bits(data.OriginX));
        AssertField(label, "originZ", FloatBits(Property(root, "originZ", label), label + " originZ"), BitConverter.SingleToUInt32Bits(data.OriginZ));
        AssertField(label, "cellSize", FloatBits(Property(root, "cellSize", label), label + " cellSize"), BitConverter.SingleToUInt32Bits(data.CellSize));
        AssertField(label, "gridCellSize", FloatBits(Property(root, "gridCellSize", label), label + " gridCellSize"), BitConverter.SingleToUInt32Bits(data.GridCellSize));
        AssertField(label, "columns", Integer(root, "columns", label, 0, uint.MaxValue), data.Columns);
        AssertField(label, "rows", Integer(root, "rows", label, 0, uint.MaxValue), data.Rows);
        AssertField(label, "meshCount", Integer(root, "meshCount", label, 0, uint.MaxValue), data.Meshes.Length);

        var heights = ArrayOf(root, "heights", label);
        AssertField(label, "heights.length", heights.GetArrayLength(), data.Heights.Length);
        for (var n = 0; n < data.Heights.Length; n++)
        {
            AssertField(label, $"heights[{n}]", FloatBits(heights[n], $"{label} heights[{n}]"), BitConverter.SingleToUInt32Bits(data.Heights[n]));
        }

        if (caseName == "tiny-world")
        {
            Assert.True(
                BitConverter.SingleToUInt32Bits(data.Heights[8]) == NegativeZeroBits,
                $"{label}: decoded heights[8] has bits 0x{BitConverter.SingleToUInt32Bits(data.Heights[8]):X8}, expected negative zero 0x80000000");
        }

        var meshes = ArrayOf(root, "meshes", label);
        AssertField(label, "meshes.length", meshes.GetArrayLength(), data.Meshes.Length);
        for (var m = 0; m < data.Meshes.Length; m++)
        {
            var mesh = data.Meshes[m];
            var where = $"{label} meshes[{m}]";
            AssertField(label, $"meshes[{m}].meshId", (uint)Integer(meshes[m], "meshId", where, 0, uint.MaxValue), mesh.MeshId);
            var vertices = ArrayOf(meshes[m], "vertices", where);
            AssertField(label, $"meshes[{m}].vertices.length · 3", vertices.GetArrayLength() * 3, mesh.Vertices.Length);
            for (var v = 0; v < vertices.GetArrayLength(); v++)
            {
                var vertex = vertices[v];
                Assert.True(
                    vertex.ValueKind == JsonValueKind.Array && vertex.GetArrayLength() == 3,
                    $"{where}.vertices[{v}] is {vertex.GetRawText()}, not [x, y, z]");
                for (var axis = 0; axis < 3; axis++)
                {
                    var name = $"meshes[{m}].vertices[{v}].{"xyz"[axis]}";
                    AssertField(label, name, FloatBits(vertex[axis], $"{label} {name}"), BitConverter.SingleToUInt32Bits(mesh.Vertices[(3 * v) + axis]));
                }
            }

            var indices = ArrayOf(meshes[m], "indices", where);
            uint[] recorded = [.. indices.EnumerateArray().Select((index, i) => (uint)IntegerValue(index, $"{where}.indices[{i}]", 0, uint.MaxValue))];
            Assert.True(recorded.SequenceEqual(mesh.Indices), $"{where}.indices: the JSON holds [{string.Join(", ", recorded)}], the .bin decodes to [{string.Join(", ", mesh.Indices)}]");
        }

        AssertBytes($"{label}: the re-encoding of the decoded world's data", bin, CollisionFile.Encode(world.Data));

        var fileLength = AssertLengthAndChecksum(label, root, encoded, CollisionFileLengthOffset);
        Assert.True(ContractCollisionFileLengths.TryGetValue(caseName, out var contractLength), $"{label}: not a valid collision case of the contract");
        Assert.True(fileLength == contractLength, $"{label}: fileLength is {fileLength}; the contract's layout gives {contractLength}");
        if (caseName == "tiny-world")
        {
            Assert.True(Text(root, "checksum", label) == "9224e3fa3b3fca9c", $"{label}: checksum is {Text(root, "checksum", label)}; the contract gives 9224e3fa3b3fca9c");
        }
    }

    private static void StreamRoundTrip(string label, string caseName, StreamValidCase parsed, JsonElement root, byte[] bin)
    {
        var encoded = parsed.Stream.Encode();
        AssertBytes($"{label}: the encoding of its .json", bin, encoded);

        var stream = RecordedStream.Decode(bin);
        AssertField(label, "rulesRevision", (uint)Integer(root, "rulesRevision", label, 0, uint.MaxValue), stream.RulesRevision);
        AssertField(label, "stepHz", Integer(root, "stepHz", label, 0, ushort.MaxValue), stream.StepHz);
        AssertField(label, "characterCount", Integer(root, "characterCount", label, 0, ushort.MaxValue), stream.CharacterCount);
        AssertField(label, "stepCount", Integer(root, "stepCount", label, 0, uint.MaxValue), stream.Steps.Count);
        AssertField(label, "seed", Text(root, "seed", label), X16(stream.Seed));
        AssertField(label, "matchSalt", Text(root, "matchSalt", label), X16(stream.MatchSalt));
        AssertField(label, "collisionChecksum", Text(root, "collisionChecksum", label), X16(stream.CollisionChecksum));

        // The config entries, re-read from the decoded configuration through its block.
        var block = ConfigBlock.Encode(stream.Config);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var config = ArrayOf(root, "config", label);
        AssertField(label, "config.length", config.GetArrayLength(), (int)count);
        for (var k = 0; k < count; k++)
        {
            var entry = block.AsSpan(ConfigBlock.CountLength + (ConfigBlock.EntryLength * k));
            var where = $"{label} config[{k}]";
            AssertField(label, $"config[{k}].fieldId", HexBits(Property(config[k], "fieldId", where), where + ".fieldId", 4), BinaryPrimitives.ReadUInt16LittleEndian(entry));
            AssertField(label, $"config[{k}].valueBits", HexBits(Property(config[k], "valueBits", where), where + ".valueBits", 8), BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]));
        }

        var state = Convert.FromHexString(Text(root, "initialStateHex", label));
        AssertBytes($"{label}: initialStateHex against the decoded initial state", state, stream.InitialState.ToArray());

        var steps = ArrayOf(root, "steps", label);
        AssertField(label, "steps.length", steps.GetArrayLength(), stream.Steps.Count);
        for (var i = 0; i < stream.Steps.Count; i++)
        {
            var step = stream.Steps[i];
            var where = $"{label} steps[{i}]";
            AssertField(label, $"steps[{i}].step", (uint)Integer(steps[i], "step", where, 0, uint.MaxValue), step.Step);
            AssertField(label, $"steps[{i}].expectedHash", Text(steps[i], "expectedHash", where), X16(step.ExpectedHash));
            var commands = ArrayOf(steps[i], "commands", where);
            AssertField(label, $"steps[{i}].commands.length", commands.GetArrayLength(), step.Commands.Length);
            for (var c = 0; c < step.Commands.Length; c++)
            {
                var recorded = commands[c];
                var decoded = step.Commands[c];
                var name = $"steps[{i}].commands[{c}]";
                var at = $"{label} {name}";
                AssertField(label, name + ".characterIndex", (byte)Integer(recorded, "characterIndex", at, 0, byte.MaxValue), decoded.CharacterIndex);
                AssertField(label, name + ".sequence", (uint)Integer(recorded, "sequence", at, 0, uint.MaxValue), decoded.Command.Sequence);
                AssertField(label, name + ".clientStep", (uint)Integer(recorded, "clientStep", at, 0, uint.MaxValue), decoded.Command.ClientStep);
                AssertField(label, name + ".buttons", (ushort)Integer(recorded, "buttons", at, 0, ushort.MaxValue), (ushort)decoded.Command.Buttons);
                AssertField(label, name + ".aimYaw", (ushort)Integer(recorded, "aimYaw", at, 0, ushort.MaxValue), decoded.Command.AimYaw);
                AssertField(label, name + ".aimPitch", (short)Integer(recorded, "aimPitch", at, short.MinValue, short.MaxValue), decoded.Command.AimPitch);
                AssertField(label, name + ".weaponSlot", (byte)Integer(recorded, "weaponSlot", at, 0, byte.MaxValue), decoded.Command.WeaponSlot);
            }
        }

        AssertBytes($"{label}: the re-encoding of the decoded stream", bin, stream.Encode());

        var fileLength = AssertLengthAndChecksum(label, root, encoded, StreamFileLengthOffset);
        var configLength = Integer(root, "configLength", label, 0, uint.MaxValue);
        var initialStateLength = Integer(root, "initialStateLength", label, 0, uint.MaxValue);
        var stepsLength = Integer(root, "stepsLength", label, 0, uint.MaxValue);
        AssertField(label, "configLength", configLength, (long)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(ConfigLengthOffset)));
        AssertField(label, "initialStateLength", initialStateLength, (long)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(InitialStateLengthOffset)));
        AssertField(label, "stepsLength", stepsLength, (long)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(StepsLengthOffset)));
        if (caseName == "two-steps")
        {
            Assert.True(
                (configLength, initialStateLength, stepsLength, fileLength) == (698, 136, 44, 950),
                $"{label}: lengths ({configLength}, {initialStateLength}, {stepsLength}, {fileLength}); the contract gives (698, 136, 44, 950)");
        }
    }

    /// <summary>
    /// The encoded file's length and its <c>u64 fileLength</c> at <paramref name="fileLengthOffset"/>
    /// equal the JSON's <c>fileLength</c>, and its trailer as x16 equals the JSON's <c>checksum</c>.
    /// </summary>
    private static long AssertLengthAndChecksum(string label, JsonElement root, byte[] encoded, int fileLengthOffset)
    {
        var fileLength = Integer(root, "fileLength", label, 0, long.MaxValue);
        AssertField(label, "fileLength (the encoded length)", fileLength, (long)encoded.Length);
        AssertField(label, "fileLength (the header field)", (ulong)fileLength, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(fileLengthOffset)));
        AssertField(label, "checksum", Text(root, "checksum", label), X16(BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(encoded.Length - TrailerLength))));
        return fileLength;
    }

    private static void AssertDerived(string what, PinnedDerived expected, PinnedDerived actual)
    {
        Assert.True(
            actual.HeightfieldTriangleCount == expected.HeightfieldTriangleCount,
            $"{what}: heightfield triangle count {actual.HeightfieldTriangleCount}, expected {expected.HeightfieldTriangleCount}");
        Assert.True(
            actual.MeshTriangleIds.SequenceEqual(expected.MeshTriangleIds),
            $"{what}: mesh triangle IDs [{string.Join(", ", actual.MeshTriangleIds)}], expected [{string.Join(", ", expected.MeshTriangleIds)}]");
        Assert.True(
            actual.DegenerateTriangleIds.SequenceEqual(expected.DegenerateTriangleIds),
            $"{what}: degenerate triangle IDs [{string.Join(", ", actual.DegenerateTriangleIds)}], expected [{string.Join(", ", expected.DegenerateTriangleIds)}]");
        Assert.True(
            actual.GridCellsX == expected.GridCellsX && actual.GridCellsZ == expected.GridCellsZ,
            $"{what}: grid {actual.GridCellsX} × {actual.GridCellsZ}, expected {expected.GridCellsX} × {expected.GridCellsZ}");
        Assert.True(actual.Cells.Length == expected.Cells.Length, $"{what}: {actual.Cells.Length} cells, expected {expected.Cells.Length}");
        for (var c = 0; c < expected.Cells.Length; c++)
        {
            Assert.True(
                actual.Cells[c].SequenceEqual(expected.Cells[c]),
                $"{what}: cell {c} holds [{string.Join(", ", actual.Cells[c])}], expected [{string.Join(", ", expected.Cells[c])}]");
        }
    }

    private static void AssertField<T>(string label, string field, T recorded, T decoded)
    {
        Assert.True(EqualityComparer<T>.Default.Equals(recorded, decoded), $"{label}: {field} is {decoded} in the bytes, {recorded} in the JSON");
    }

    private static void AssertBytes(string what, byte[] expected, byte[] actual)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return;
        }

        var first = expected.AsSpan().CommonPrefixLength(actual);
        Assert.Fail($"{what}: {actual.Length} bytes against the expected {expected.Length}; they first differ at offset {first}");
    }

    /// <summary>
    /// Walks a case's JSON, collecting every float object <c>{bits, value}</c>, every 64-bit value and
    /// every <c>fieldId</c> and <c>valueBits</c>, with the problems of each.
    /// </summary>
    private static void Scan(JsonElement element, string path, BitScan scan)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when element.TryGetProperty("bits", out _):
                ScanFloat(element, path, scan);
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = $"{path}.{property.Name}";
                    switch (property.Name)
                    {
                        case "seed" or "matchSalt" or "collisionChecksum" or "checksum" or "expectedHash":
                            scan.X16s++;
                            CheckPattern(property.Value, child, X16Pattern(), "16 lowercase hex digits", scan);
                            break;
                        case "fieldId":
                            scan.FieldIds++;
                            CheckPattern(property.Value, child, FieldIdPattern(), "0x and 4 uppercase hex digits", scan);
                            break;
                        case "valueBits":
                            scan.ValueBits++;
                            CheckPattern(property.Value, child, BitsPattern(), "0x and 8 uppercase hex digits", scan);
                            break;
                        default:
                            Scan(property.Value, child, scan);
                            break;
                    }
                }

                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Scan(item, $"{path}[{i}]", scan);
                    i++;
                }

                break;

            default:
                break;
        }
    }

    private static void ScanFloat(JsonElement element, string path, BitScan scan)
    {
        scan.Floats++;
        var names = element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(["bits", "value"]))
        {
            scan.Problems.Add($"{path}: a float object holds exactly bits and value, not {string.Join(", ", names)}");
            return;
        }

        var bits = element.GetProperty("bits");
        if (!CheckPattern(bits, path + ".bits", BitsPattern(), "0x and 8 uppercase hex digits", scan))
        {
            return;
        }

        var value = element.GetProperty("value");
        if (value.ValueKind != JsonValueKind.Number)
        {
            scan.Problems.Add($"{path}.value: {value.GetRawText()} is not a JSON number");
            return;
        }

        var expected = uint.Parse(bits.GetString()!.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var parsed = BitConverter.SingleToUInt32Bits(float.Parse(value.GetRawText(), CultureInfo.InvariantCulture));
        if (parsed != expected)
        {
            scan.Problems.Add($"{path}.value: {value.GetRawText()} parses to bits 0x{parsed:X8}, not {bits.GetString()}");
        }
    }

    private static bool CheckPattern(JsonElement value, string path, Regex pattern, string form, BitScan scan)
    {
        if (value.ValueKind != JsonValueKind.String || !pattern.IsMatch(value.GetString()!))
        {
            scan.Problems.Add($"{path}: {value.GetRawText()} is not {form}");
            return false;
        }

        return true;
    }

    /// <summary>The float objects a collision case's fields hold: the four header floats, every height and three per vertex.</summary>
    private static int CollisionFloatCount(JsonElement root, string label)
    {
        var count = 4 + ArrayOf(root, "heights", label).GetArrayLength();
        foreach (var mesh in ArrayOf(root, "meshes", label).EnumerateArray())
        {
            count += 3 * ArrayOf(mesh, "vertices", label + " meshes[]").GetArrayLength();
        }

        return count;
    }

    private static HashSet<string> NamesWithExtension(string[] entries, string extension) =>
        entries.Where(entry => entry.EndsWith(extension, StringComparison.Ordinal))
            .Select(entry => entry[..^extension.Length])
            .ToHashSet(StringComparer.Ordinal);

    private static string X16(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    private static JsonElement Property(JsonElement element, string name, string path)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            Assert.Fail($"{path}: no property {name}");
            return default;
        }

        return value;
    }

    private static JsonElement ArrayOf(JsonElement element, string name, string path)
    {
        var value = Property(element, name, path);
        Assert.True(value.ValueKind == JsonValueKind.Array, $"{path}.{name} is {value.GetRawText()}, not an array");
        return value;
    }

    private static string Text(JsonElement element, string name, string path)
    {
        var value = Property(element, name, path);
        Assert.True(value.ValueKind == JsonValueKind.String, $"{path}.{name} is {value.GetRawText()}, not a string");
        return value.GetString()!;
    }

    private static long Integer(JsonElement element, string name, string path, long min, long max) =>
        IntegerValue(Property(element, name, path), $"{path}.{name}", min, max);

    private static long IntegerValue(JsonElement value, string path, long min, long max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < min || number > max)
        {
            Assert.Fail($"{path} is {value.GetRawText()}, not an integer in [{min}, {max}]");
            return 0;
        }

        return number;
    }

    private static int[] IntArray(JsonElement array, string path)
    {
        Assert.True(array.ValueKind == JsonValueKind.Array, $"{path} is {array.GetRawText()}, not an array");
        return [.. array.EnumerateArray().Select((item, i) => (int)IntegerValue(item, $"{path}[{i}]", 0, int.MaxValue))];
    }

    /// <summary>A float object's <c>bits</c>, read after its <c>0x</c>; the form itself is <see cref="FloatValuesParseBackToTheirBits"/>'s.</summary>
    private static uint FloatBits(JsonElement floatObject, string path) => HexBits(Property(floatObject, "bits", path), path + ".bits", 8);

    /// <summary>A <c>0x</c> hex string of <paramref name="digits"/> digits, as a number.</summary>
    private static uint HexBits(JsonElement value, string path, int digits)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
        if (text.Length != 2 + digits
            || !text.StartsWith("0x", StringComparison.Ordinal)
            || !uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
        {
            Assert.Fail($"{path} is {value.GetRawText()}, not 0x and {digits} hex digits");
            return 0;
        }

        return bits;
    }

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex X16Pattern();

    [GeneratedRegex("^0x[0-9A-F]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex BitsPattern();

    [GeneratedRegex("^0x[0-9A-F]{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldIdPattern();

    /// <summary>A collision world's derived data as the JSON's <c>derived</c> object records it.</summary>
    private sealed record PinnedDerived(int HeightfieldTriangleCount, int[] MeshTriangleIds, int[] DegenerateTriangleIds, int GridCellsX, int GridCellsZ, int[][] Cells);

    /// <summary>What <see cref="Scan"/> found in one case's JSON.</summary>
    private sealed class BitScan
    {
        public int Floats { get; set; }

        public int X16s { get; set; }

        public int FieldIds { get; set; }

        public int ValueBits { get; set; }

        public List<string> Problems { get; } = [];
    }
}
