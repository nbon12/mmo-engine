using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Server.Golden;

/// <summary>
/// Reads a golden case's JSON (specs/015-shared-simulation-core/contracts/input-command-format.md#shared-fixture-rules,
/// collision-data-format.md#golden-fixtures and recorded-stream-format.md#golden-fixtures) into its
/// <see cref="GoldenCase"/>. It reads the value forms exactly and every field strictly: an unknown field, a
/// missing one, a value of the wrong kind or width, or a value that disagrees with another (a
/// <c>meshCount</c> that is not the length of <c>meshes</c>) is refused with a
/// <see cref="GoldenCaseException"/> whose message is <c>&lt;JSON path&gt;: &lt;problem&gt;</c>, the path
/// written from the root <c>$</c>, as in <c>$.heights[0].bits</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>A 64-bit value (seed, salt, checksum, hash) is exactly 16 lowercase hex digits.</description></item>
/// <item><description>A float is <c>{ "bits": "0x3F800000", "value": 1.0 }</c>. The bits are authoritative and
/// are <c>0x</c> and 8 uppercase hex digits; <c>value</c>'s raw JSON text must parse with
/// <see cref="float.Parse(string, IFormatProvider)"/> (invariant culture) to exactly those bits, so negative
/// zero is <c>-0.0</c>.</description></item>
/// <item><description>A config entry's <c>fieldId</c> is <c>0x</c> and 4 uppercase hex digits and its
/// <c>valueBits</c> <c>0x</c> and 8.</description></item>
/// <item><description>An integer is a JSON number without a fraction or exponent that fits its field.</description></item>
/// </list>
/// The case's <c>format</c> must be its folder's, its <c>formatVersion</c> the format's version, and its
/// <c>case</c> its file name without <c>.json</c>.
/// </remarks>
public static class GoldenJson
{
    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";
    private const string RootPath = "$";
    private const string OpenStage = "open";

    private static readonly string[] CommandFields = ["sequence", "clientStep", "buttons", "aimYaw", "aimPitch", "weaponSlot"];
    private static readonly string[] StreamCommandFields = ["characterIndex", .. CommandFields];
    private static readonly string[] FloatFields = ["bits", "value"];

    private static readonly string[] InputValidFields = ["format", "formatVersion", "case", "kind", "command", "validate"];

    private static readonly string[] CollisionValidFields =
    [
        "format", "formatVersion", "case", "kind", "originX", "originZ", "cellSize", "gridCellSize", "columns", "rows",
        "meshCount", "heights", "meshes", "fileLength", "checksum", "derived",
    ];

    private static readonly string[] StreamValidFields =
    [
        "format", "formatVersion", "case", "kind", "rulesRevision", "stepHz", "characterCount", "stepCount", "seed",
        "matchSalt", "collisionChecksum", "config", "initialStateHex", "steps", "configLength", "initialStateLength",
        "stepsLength", "fileLength", "checksum",
    ];

    private static readonly string[] RefusalFields =
    [
        "format", "formatVersion", "case", "kind", "base", "mutations", "expectedError", "expectedConfigErrors",
        "refusedAt", "collisionFixtures",
    ];

    private static readonly string[] DerivedFields =
        ["heightfieldTriangleCount", "meshTriangleIds", "degenerateTriangleIds", "gridCellsX", "gridCellsZ", "cells"];

    private static readonly string[] Ops = ["truncate", "append", "write", "insert", "delete", "fixChecksum"];

    private static JsonDocumentOptions DocumentOptions => new()
    {
        AllowDuplicateProperties = false,
    };

    /// <summary>
    /// Parses one case: <paramref name="utf8Json"/> is the content of
    /// <c>&lt;folderFormat&gt;/&lt;fileName&gt;</c>. A valid case gives an <see cref="InputValidCase"/>,
    /// <see cref="CollisionValidCase"/> or <see cref="StreamValidCase"/>, a refusal case a
    /// <see cref="GoldenRefusalCase"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="folderFormat"/> is not <c>input</c>, <c>collision</c> or <c>stream</c>.</exception>
    /// <exception cref="GoldenCaseException">The JSON is not a well-formed case: <c>&lt;JSON path&gt;: &lt;problem&gt;</c>.</exception>
    public static GoldenCase Parse(string folderFormat, string fileName, ReadOnlySpan<byte> utf8Json)
    {
        ArgumentNullException.ThrowIfNull(folderFormat);
        ArgumentNullException.ThrowIfNull(fileName);
        if (folderFormat is not (InputFormat or CollisionFormat or StreamFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(folderFormat), folderFormat, "The golden formats are input, collision and stream.");
        }

        var text = utf8Json.StartsWith("\uFEFF"u8) ? utf8Json[3..] : utf8Json;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text.ToArray(), DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw Problem(RootPath, $"is not valid JSON ({ex.Message})");
        }

        using (document)
        {
            return ParseCase(folderFormat, fileName, document.RootElement);
        }
    }

    private static GoldenCase ParseCase(string folderFormat, string fileName, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Problem(RootPath, $"must be a JSON object");
        }

        var format = Text(Required(root, RootPath, "format"), "$.format");
        if (format != folderFormat)
        {
            throw Problem("$.format", $"\"{format}\" is not the folder's format \"{folderFormat}\"");
        }

        int expectedVersion = format switch
        {
            InputFormat => InputCodec.FormatVersion,
            CollisionFormat => CollisionFile.FormatVersion,
            _ => RecordedStream.FormatVersion,
        };
        var version = (int)Integer(Required(root, RootPath, "formatVersion"), "$.formatVersion", 0, ushort.MaxValue);
        if (version != expectedVersion)
        {
            throw Problem("$.formatVersion", $"{version} is not {expectedVersion}, the {format} format's version");
        }

        var caseName = Text(Required(root, RootPath, "case"), "$.case");
        if (fileName != caseName + ".json")
        {
            throw Problem("$.case", $"\"{caseName}\" does not match the file name \"{fileName}\"");
        }

        var kind = Required(root, RootPath, "kind");
        if (kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("valid" or "refusal"))
        {
            throw Problem("$.kind", $"must be \"valid\" or \"refusal\"");
        }

        if (kind.GetString() == "refusal")
        {
            return ParseRefusal(root, format, version, caseName);
        }

        return format switch
        {
            InputFormat => ParseInput(root, version, caseName),
            CollisionFormat => ParseCollision(root, version, caseName),
            _ => ParseStream(root, version, caseName),
        };
    }

    private static InputValidCase ParseInput(JsonElement root, int version, string caseName)
    {
        OnlyFields(root, RootPath, InputValidFields, "a valid input case");
        var command = Command(Required(root, RootPath, "command"), "$.command", CommandFields);

        var validate = Object(Required(root, RootPath, "validate"), "$.validate");
        OnlyFields(validate, "$.validate", ["lastSequence", "weaponSlots", "expected"], "validate");
        var lastSequence = (uint)Integer(Required(validate, "$.validate", "lastSequence"), "$.validate.lastSequence", 0, uint.MaxValue);
        var weaponSlots = (int)Integer(Required(validate, "$.validate", "weaponSlots"), "$.validate.weaponSlots", 0, int.MaxValue);
        var expected = EnumName<InputRejection>(Required(validate, "$.validate", "expected"), "$.validate.expected");

        return new InputValidCase(InputFormat, version, caseName, command, new GoldenValidate(lastSequence, weaponSlots, expected));
    }

    private static CollisionValidCase ParseCollision(JsonElement root, int version, string caseName)
    {
        OnlyFields(root, RootPath, CollisionValidFields, "a valid collision case");
        var originX = Float(Required(root, RootPath, "originX"), "$.originX");
        var originZ = Float(Required(root, RootPath, "originZ"), "$.originZ");
        var cellSize = Float(Required(root, RootPath, "cellSize"), "$.cellSize");
        var gridCellSize = Float(Required(root, RootPath, "gridCellSize"), "$.gridCellSize");
        var columns = (int)Integer(Required(root, RootPath, "columns"), "$.columns", 0, int.MaxValue);
        var rows = (int)Integer(Required(root, RootPath, "rows"), "$.rows", 0, int.MaxValue);
        var meshCount = Integer(Required(root, RootPath, "meshCount"), "$.meshCount", 0, uint.MaxValue);

        var heightElements = Array(Required(root, RootPath, "heights"), "$.heights");
        var heights = new float[heightElements.GetArrayLength()];
        var n = 0;
        foreach (var height in heightElements.EnumerateArray())
        {
            heights[n] = Float(height, Index("$.heights", n));
            n++;
        }

        var samples = (long)columns * rows;
        if (heights.Length != samples)
        {
            throw Problem("$.heights", $"{heights.Length} heights; columns × rows is {samples}");
        }

        var meshElements = Array(Required(root, RootPath, "meshes"), "$.meshes");
        if (meshCount != meshElements.GetArrayLength())
        {
            throw Problem("$.meshCount", $"{meshCount} is not the length of meshes, {meshElements.GetArrayLength()}");
        }

        var meshes = new MeshData[meshElements.GetArrayLength()];
        var m = 0;
        foreach (var mesh in meshElements.EnumerateArray())
        {
            meshes[m] = Mesh(mesh, Index("$.meshes", m));
            m++;
        }

        var fileLength = Integer(Required(root, RootPath, "fileLength"), "$.fileLength", 0, long.MaxValue);
        var checksum = X16(Required(root, RootPath, "checksum"), "$.checksum");
        var derived = Derived(Required(root, RootPath, "derived"), "$.derived");

        var data = new CollisionData(originX, originZ, cellSize, gridCellSize, columns, rows, heights, meshes);
        return new CollisionValidCase(CollisionFormat, version, caseName, data, fileLength, checksum, derived);
    }

    private static MeshData Mesh(JsonElement element, string path)
    {
        var mesh = Object(element, path);
        OnlyFields(mesh, path, ["meshId", "vertices", "indices"], "a mesh");
        var meshId = (uint)Integer(Required(mesh, path, "meshId"), Child(path, "meshId"), 0, uint.MaxValue);

        var verticesPath = Child(path, "vertices");
        var vertexElements = Array(Required(mesh, path, "vertices"), verticesPath);
        var vertices = new float[3 * vertexElements.GetArrayLength()];
        var v = 0;
        foreach (var vertex in vertexElements.EnumerateArray())
        {
            var vertexPath = Index(verticesPath, v);
            if (vertex.ValueKind != JsonValueKind.Array || vertex.GetArrayLength() != 3)
            {
                throw Problem(vertexPath, $"must be [x, y, z]");
            }

            for (var axis = 0; axis < 3; axis++)
            {
                vertices[(3 * v) + axis] = Float(vertex[axis], Index(vertexPath, axis));
            }

            v++;
        }

        var indicesPath = Child(path, "indices");
        var indexElements = Array(Required(mesh, path, "indices"), indicesPath);
        var indices = new uint[indexElements.GetArrayLength()];
        var k = 0;
        foreach (var index in indexElements.EnumerateArray())
        {
            indices[k] = (uint)Integer(index, Index(indicesPath, k), 0, uint.MaxValue);
            k++;
        }

        if (indices.Length % 3 != 0)
        {
            throw Problem(indicesPath, $"{indices.Length} indices are not whole triangles");
        }

        return new MeshData(meshId, vertices, indices);
    }

    private static GoldenCollisionDerived Derived(JsonElement element, string path)
    {
        var derived = Object(element, path);
        OnlyFields(derived, path, DerivedFields, "derived");
        var heightfieldTriangleCount = (int)Integer(Required(derived, path, "heightfieldTriangleCount"), Child(path, "heightfieldTriangleCount"), 0, int.MaxValue);
        var meshTriangleIds = Ints(Required(derived, path, "meshTriangleIds"), Child(path, "meshTriangleIds"));
        var degenerateTriangleIds = Ints(Required(derived, path, "degenerateTriangleIds"), Child(path, "degenerateTriangleIds"));
        var gridCellsX = (int)Integer(Required(derived, path, "gridCellsX"), Child(path, "gridCellsX"), 0, int.MaxValue);
        var gridCellsZ = (int)Integer(Required(derived, path, "gridCellsZ"), Child(path, "gridCellsZ"), 0, int.MaxValue);

        var cellsPath = Child(path, "cells");
        var cellElements = Array(Required(derived, path, "cells"), cellsPath);
        var cells = new int[cellElements.GetArrayLength()][];
        var c = 0;
        foreach (var cell in cellElements.EnumerateArray())
        {
            cells[c] = Ints(cell, Index(cellsPath, c));
            c++;
        }

        return new GoldenCollisionDerived(heightfieldTriangleCount, meshTriangleIds, degenerateTriangleIds, gridCellsX, gridCellsZ, cells);
    }

    private static StreamValidCase ParseStream(JsonElement root, int version, string caseName)
    {
        OnlyFields(root, RootPath, StreamValidFields, "a valid stream case");
        var rulesRevision = (uint)Integer(Required(root, RootPath, "rulesRevision"), "$.rulesRevision", 0, uint.MaxValue);
        var stepHz = (int)Integer(Required(root, RootPath, "stepHz"), "$.stepHz", 0, ushort.MaxValue);
        var characterCount = (int)Integer(Required(root, RootPath, "characterCount"), "$.characterCount", 0, ushort.MaxValue);
        var stepCount = Integer(Required(root, RootPath, "stepCount"), "$.stepCount", 0, uint.MaxValue);
        var seed = X16(Required(root, RootPath, "seed"), "$.seed");
        var matchSalt = X16(Required(root, RootPath, "matchSalt"), "$.matchSalt");
        var collisionChecksum = X16(Required(root, RootPath, "collisionChecksum"), "$.collisionChecksum");
        var entries = ConfigEntries(Required(root, RootPath, "config"), "$.config");
        var config = ConfigFrom(entries);
        var initialState = LowercaseHex(Required(root, RootPath, "initialStateHex"), "$.initialStateHex");

        var stepElements = Array(Required(root, RootPath, "steps"), "$.steps");
        var steps = new RecordedStep[stepElements.GetArrayLength()];
        var i = 0;
        foreach (var step in stepElements.EnumerateArray())
        {
            steps[i] = Step(step, Index("$.steps", i));
            i++;
        }

        var configLength = Integer(Required(root, RootPath, "configLength"), "$.configLength", 0, uint.MaxValue);
        var initialStateLength = Integer(Required(root, RootPath, "initialStateLength"), "$.initialStateLength", 0, uint.MaxValue);
        var stepsLength = Integer(Required(root, RootPath, "stepsLength"), "$.stepsLength", 0, uint.MaxValue);
        var fileLength = Integer(Required(root, RootPath, "fileLength"), "$.fileLength", 0, long.MaxValue);
        var checksum = X16(Required(root, RootPath, "checksum"), "$.checksum");

        if (stepCount != steps.Length)
        {
            throw Problem("$.stepCount", $"{stepCount} is not the length of steps, {steps.Length}");
        }

        var stream = new RecordedStream(rulesRevision, stepHz, seed, matchSalt, collisionChecksum, config, initialState, steps);
        if (characterCount != stream.CharacterCount)
        {
            throw Problem("$.characterCount", $"{characterCount} is not the initial state's characterCount, {stream.CharacterCount}");
        }

        return new StreamValidCase(StreamFormat, version, caseName, stream, entries, configLength, initialStateLength, stepsLength, fileLength, checksum);
    }

    private static GoldenConfigEntry[] ConfigEntries(JsonElement element, string path)
    {
        var array = Array(element, path);
        var entries = new GoldenConfigEntry[array.GetArrayLength()];
        var k = 0;
        foreach (var item in array.EnumerateArray())
        {
            var entryPath = Index(path, k);
            var entry = Object(item, entryPath);
            OnlyFields(entry, entryPath, ["fieldId", "path", "valueBits"], "a config entry");
            var fieldId = (ushort)HexBits(Required(entry, entryPath, "fieldId"), Child(entryPath, "fieldId"), 4);
            var fieldPath = Text(Required(entry, entryPath, "path"), Child(entryPath, "path"));
            var valueBits = HexBits(Required(entry, entryPath, "valueBits"), Child(entryPath, "valueBits"), 8);
            entries[k] = new GoldenConfigEntry(fieldId, fieldPath, valueBits);
            k++;
        }

        if (entries.Length > ushort.MaxValue)
        {
            throw Problem(path, $"{entries.Length} entries; the block's count holds at most {ushort.MaxValue}");
        }

        return entries;
    }

    /// <summary>
    /// The configuration of the entries: written as a config block and read with
    /// <see cref="ConfigBlock.Decode(ReadOnlySpan{byte})"/>, so the block's structure rules apply, and each
    /// entry's <c>path</c> must be its field's path in the rule table.
    /// </summary>
    private static SimConfig ConfigFrom(GoldenConfigEntry[] entries)
    {
        var block = new byte[ConfigBlock.CountLength + (ConfigBlock.EntryLength * entries.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)entries.Length);
        for (var k = 0; k < entries.Length; k++)
        {
            var entry = block.AsSpan(ConfigBlock.CountLength + (ConfigBlock.EntryLength * k));
            BinaryPrimitives.WriteUInt16LittleEndian(entry, entries[k].FieldId);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], entries[k].ValueBits);
        }

        SimConfig config;
        try
        {
            config = ConfigBlock.Decode(block);
        }
        catch (CoreFormatException ex)
        {
            var error = ex.Error;
            if (error.Field.StartsWith("entries[", StringComparison.Ordinal))
            {
                var k = (int)((error.Offset - ConfigBlock.CountLength) / ConfigBlock.EntryLength);
                throw Problem(Child(Index("$.config", k), "fieldId"), $"{error.Detail}");
            }

            throw Problem("$.config", $"{error.Field}: {error.Detail}");
        }

        var paths = ConfigFields.Expand(config.Weapons?.Count ?? 0, config.ProjectileClasses?.Count ?? 0)
            .ToDictionary(field => field.FieldId, field => field.Path);
        for (var k = 0; k < entries.Length; k++)
        {
            var rulePath = paths[entries[k].FieldId];
            if (entries[k].Path != rulePath)
            {
                throw Problem(Child(Index("$.config", k), "path"), $"\"{entries[k].Path}\" is not field 0x{entries[k].FieldId:X4}'s path \"{rulePath}\"");
            }
        }

        return config;
    }

    private static RecordedStep Step(JsonElement element, string path)
    {
        var step = Object(element, path);
        OnlyFields(step, path, ["step", "commands", "expectedHash"], "a step");
        var number = (uint)Integer(Required(step, path, "step"), Child(path, "step"), 0, uint.MaxValue);

        var commandsPath = Child(path, "commands");
        var commandElements = Array(Required(step, path, "commands"), commandsPath);
        if (commandElements.GetArrayLength() > byte.MaxValue)
        {
            throw Problem(commandsPath, $"{commandElements.GetArrayLength()} commands; commandCount holds at most {byte.MaxValue}");
        }

        var commands = new RecordedCommand[commandElements.GetArrayLength()];
        var c = 0;
        foreach (var item in commandElements.EnumerateArray())
        {
            var commandPath = Index(commandsPath, c);
            var command = Object(item, commandPath);
            var characterIndex = (byte)Integer(Required(command, commandPath, "characterIndex"), Child(commandPath, "characterIndex"), 0, byte.MaxValue);
            commands[c] = new RecordedCommand(characterIndex, Command(command, commandPath, StreamCommandFields));
            c++;
        }

        var expectedHash = X16(Required(step, path, "expectedHash"), Child(path, "expectedHash"));
        return new RecordedStep(number, commands, expectedHash);
    }

    /// <summary>An input command's six fields, from an object whose fields are exactly <paramref name="fields"/>.</summary>
    private static InputCommand Command(JsonElement element, string path, string[] fields)
    {
        var command = Object(element, path);
        OnlyFields(command, path, fields, "a command");
        var sequence = (uint)Integer(Required(command, path, "sequence"), Child(path, "sequence"), 0, uint.MaxValue);
        var clientStep = (uint)Integer(Required(command, path, "clientStep"), Child(path, "clientStep"), 0, uint.MaxValue);
        var buttons = (Buttons)(ushort)Integer(Required(command, path, "buttons"), Child(path, "buttons"), 0, ushort.MaxValue);
        var aimYaw = (ushort)Integer(Required(command, path, "aimYaw"), Child(path, "aimYaw"), 0, ushort.MaxValue);
        var aimPitch = (short)Integer(Required(command, path, "aimPitch"), Child(path, "aimPitch"), short.MinValue, short.MaxValue);
        var weaponSlot = (byte)Integer(Required(command, path, "weaponSlot"), Child(path, "weaponSlot"), 0, byte.MaxValue);
        return new InputCommand(sequence, clientStep, buttons, aimYaw, aimPitch, weaponSlot);
    }

    private static GoldenRefusalCase ParseRefusal(JsonElement root, string format, int version, string caseName)
    {
        OnlyFields(root, RootPath, RefusalFields, "a refusal case");
        var baseName = Text(Required(root, RootPath, "base"), "$.base");

        var mutationElements = Array(Required(root, RootPath, "mutations"), "$.mutations");
        var mutations = new GoldenMutation[mutationElements.GetArrayLength()];
        var i = 0;
        foreach (var mutation in mutationElements.EnumerateArray())
        {
            mutations[i] = Mutation(mutation, Index("$.mutations", i));
            i++;
        }

        GoldenExpectedError? expectedError = null;
        if (root.TryGetProperty("expectedError", out var errorElement))
        {
            var error = Object(errorElement, "$.expectedError");
            OnlyFields(error, "$.expectedError", ["code", "offset", "field"], "expectedError");
            var code = EnumName<FormatErrorCode>(Required(error, "$.expectedError", "code"), "$.expectedError.code");
            if (code == FormatErrorCode.None)
            {
                throw Problem("$.expectedError.code", $"None is no refusal");
            }

            var offset = Integer(Required(error, "$.expectedError", "offset"), "$.expectedError.offset", 0, long.MaxValue);
            var field = Text(Required(error, "$.expectedError", "field"), "$.expectedError.field");
            expectedError = new GoldenExpectedError(code, offset, field);
        }

        (string Path, string Message)[] configErrors = [];
        if (root.TryGetProperty("expectedConfigErrors", out var configErrorsElement))
        {
            var array = Array(configErrorsElement, "$.expectedConfigErrors");
            configErrors = new (string Path, string Message)[array.GetArrayLength()];
            var e = 0;
            foreach (var item in array.EnumerateArray())
            {
                var itemPath = Index("$.expectedConfigErrors", e);
                var configError = Object(item, itemPath);
                OnlyFields(configError, itemPath, ["path", "message"], "a configuration error");
                configErrors[e] = (
                    Text(Required(configError, itemPath, "path"), Child(itemPath, "path")),
                    Text(Required(configError, itemPath, "message"), Child(itemPath, "message")));
                e++;
            }
        }

        string? refusedAt = null;
        if (root.TryGetProperty("refusedAt", out var stage))
        {
            if (stage.ValueKind != JsonValueKind.String || stage.GetString() != OpenStage)
            {
                throw Problem("$.refusedAt", $"must be \"{OpenStage}\"");
            }

            if (format != StreamFormat)
            {
                throw Problem("$.refusedAt", $"only a stream case is refused at open");
            }

            refusedAt = OpenStage;
        }

        string[] collisionFixtures = [];
        var hasCollisionFixtures = root.TryGetProperty("collisionFixtures", out var fixturesElement);
        if (hasCollisionFixtures)
        {
            var array = Array(fixturesElement, "$.collisionFixtures");
            collisionFixtures = new string[array.GetArrayLength()];
            var f = 0;
            foreach (var item in array.EnumerateArray())
            {
                collisionFixtures[f] = Text(item, Index("$.collisionFixtures", f));
                f++;
            }
        }

        if (expectedError is null && configErrors.Length == 0)
        {
            throw Problem(RootPath, $"a refusal case needs expectedError or expectedConfigErrors");
        }

        if (expectedError is not null && configErrors.Length != 0)
        {
            throw Problem(RootPath, $"a refusal case has expectedError or expectedConfigErrors, not both");
        }

        if (configErrors.Length != 0 && refusedAt is null)
        {
            throw Problem("$.expectedConfigErrors", $"only a case refused at open has configuration errors");
        }

        // The collision fixtures are what a case refused at open is opened with: such a case names them, an
        // empty list included, and a decode refusal, which is never opened, has none to name.
        if (hasCollisionFixtures && refusedAt is null)
        {
            throw Problem("$.collisionFixtures", $"only a case refused at open names collision fixtures");
        }

        if (!hasCollisionFixtures && refusedAt is not null)
        {
            throw Problem("$.collisionFixtures", $"is required for a case refused at open");
        }

        return new GoldenRefusalCase(format, version, caseName, baseName, mutations, expectedError, refusedAt, collisionFixtures, configErrors);
    }

    /// <summary>
    /// One mutation: its <c>op</c> and exactly the arguments the op takes (<c>truncate length</c>,
    /// <c>append hex</c>, <c>write offset hex</c>, <c>insert offset hex</c>, <c>delete offset length</c>,
    /// <c>fixChecksum</c>). The hex keeps its written case; <see cref="GoldenMutations.Apply"/> reads either.
    /// </summary>
    private static GoldenMutation Mutation(JsonElement element, string path)
    {
        var mutation = Object(element, path);
        OnlyFields(mutation, path, ["op", "offset", "length", "hex"], "a mutation");
        var op = Text(Required(mutation, path, "op"), Child(path, "op"));
        if (!Ops.Contains(op, StringComparer.Ordinal))
        {
            throw Problem(Child(path, "op"), $"\"{op}\" is not an op; the ops are truncate, append, write, insert, delete and fixChecksum");
        }

        var takesOffset = op is "write" or "insert" or "delete";
        var takesLength = op is "truncate" or "delete";
        var takesHex = op is "append" or "write" or "insert";
        var offset = Argument(mutation, path, op, "offset", takesOffset) is { } offsetElement
            ? Integer(offsetElement, Child(path, "offset"), 0, long.MaxValue)
            : 0;
        var length = Argument(mutation, path, op, "length", takesLength) is { } lengthElement
            ? Integer(lengthElement, Child(path, "length"), 0, long.MaxValue)
            : 0;
        string? hex = null;
        if (Argument(mutation, path, op, "hex", takesHex) is { } hexElement)
        {
            hex = Text(hexElement, Child(path, "hex"));
            if (hex.Length % 2 != 0 || !hex.All(char.IsAsciiHexDigit))
            {
                throw Problem(Child(path, "hex"), $"must be hex digits, two per byte");
            }
        }

        return new GoldenMutation(op, offset, length, hex);
    }

    /// <summary>The argument <paramref name="name"/>: required when the op takes it, refused when it does not.</summary>
    private static JsonElement? Argument(JsonElement mutation, string path, string op, string name, bool takes)
    {
        var present = mutation.TryGetProperty(name, out var value);
        if (takes && !present)
        {
            throw Problem(Child(path, name), $"is required for {op}");
        }

        if (!takes && present)
        {
            throw Problem(Child(path, name), $"is not an argument of {op}");
        }

        return present ? value : null;
    }

    private static JsonElement Required(JsonElement element, string path, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw Problem(Child(path, name), $"is required");

    /// <summary>Refuses the first property of <paramref name="element"/> that is not in <paramref name="allowed"/>.</summary>
    private static void OnlyFields(JsonElement element, string path, string[] allowed, string what)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Problem(Child(path, property.Name), $"is not a field of {what}");
            }
        }
    }

    private static JsonElement Object(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Object ? element : throw Problem(path, $"must be an object");

    private static JsonElement Array(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Array ? element : throw Problem(path, $"must be an array");

    private static string Text(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Problem(path, $"must be a string");

    /// <summary>A JSON integer within [<paramref name="min"/>, <paramref name="max"/>]: a number with no fraction or exponent.</summary>
    private static long Integer(JsonElement element, string path, long min, long max)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw Problem(path, $"must be an integer");
        }

        if (!element.TryGetInt64(out var value))
        {
            var raw = element.GetRawText();
            var digits = raw.StartsWith('-') ? raw[1..] : raw;
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit))
            {
                throw Problem(path, $"{raw} is outside [{min}, {max}]");
            }

            throw Problem(path, $"must be an integer");
        }

        if (value < min || value > max)
        {
            throw Problem(path, $"{value} is outside [{min}, {max}]");
        }

        return value;
    }

    private static int[] Ints(JsonElement element, string path)
    {
        var array = Array(element, path);
        var values = new int[array.GetArrayLength()];
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            values[i] = (int)Integer(item, Index(path, i), 0, int.MaxValue);
            i++;
        }

        return values;
    }

    /// <summary>A 64-bit value: exactly 16 lowercase hex digits.</summary>
    private static ulong X16(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: 16 } text
            || !text.All(IsLowercaseHexDigit))
        {
            throw Problem(path, $"must be 16 lowercase hex digits");
        }

        return ulong.Parse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>A bit pattern: <c>0x</c> and exactly <paramref name="digits"/> uppercase hex digits.</summary>
    private static uint HexBits(JsonElement element, string path, int digits)
    {
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } text
            || text.Length != digits + 2
            || !text.StartsWith("0x", StringComparison.Ordinal)
            || !text[2..].All(IsUppercaseHexDigit))
        {
            throw Problem(path, $"must be 0x and {digits} uppercase hex digits");
        }

        return uint.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A float object <c>{ "bits": "0x…", "value": … }</c>: the bits, which <c>value</c>'s raw JSON text must
    /// parse to exactly.
    /// </summary>
    private static float Float(JsonElement element, string path)
    {
        var item = Object(element, path);
        OnlyFields(item, path, FloatFields, "a float object");
        var bits = HexBits(Required(item, path, "bits"), Child(path, "bits"), 8);
        var valuePath = Child(path, "value");
        var value = Required(item, path, "value");
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw Problem(valuePath, $"must be a number");
        }

        var parsed = float.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (BitConverter.SingleToUInt32Bits(parsed) != bits)
        {
            throw Problem(valuePath, $"{parsed.ToString("R", CultureInfo.InvariantCulture)} does not have bits 0x{bits:X8}");
        }

        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>Bytes written as lowercase hex, two digits per byte.</summary>
    private static byte[] LowercaseHex(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } text
            || text.Length % 2 != 0
            || !text.All(IsLowercaseHexDigit))
        {
            throw Problem(path, $"must be lowercase hex digits, two per byte");
        }

        return Convert.FromHexString(text);
    }

    /// <summary>A member name of <typeparamref name="TEnum"/>, written exactly; a number is refused.</summary>
    private static TEnum EnumName<TEnum>(JsonElement element, string path)
        where TEnum : struct, Enum
    {
        var text = Text(element, path);
        if (!Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal))
        {
            throw Problem(path, $"\"{text}\" is not a {typeof(TEnum).Name}");
        }

        return Enum.Parse<TEnum>(text);
    }

    private static bool IsLowercaseHexDigit(char c) => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f';

    private static bool IsUppercaseHexDigit(char c) => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F';

    private static string Child(string path, string name) => string.Concat(path, ".", name);

    private static string Index(string path, int index) => string.Concat(path, "[", index.ToString(CultureInfo.InvariantCulture), "]");

    private static GoldenCaseException Problem(string path, FormattableString problem) =>
        new(string.Concat(path, ": ", FormattableString.Invariant(problem)));
}
