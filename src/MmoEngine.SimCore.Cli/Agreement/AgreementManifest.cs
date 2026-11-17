using System.Buffers;
using System.Globalization;
using System.Text.Json;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>
/// The agreement work directory's <c>manifest.json</c>
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md#manifestjson): the core's rules revision and
/// format versions when it was prepared, the seeds, every stream, the perturbation, the cross-build tables
/// and the refusals. <see cref="ToJson"/> writes it with <see cref="Utf8JsonWriter"/> in the contract
/// example's field order, indented with <c>\n</c> line breaks; <see cref="ReadVersions"/> and
/// <see cref="Read"/> read it back with <see cref="System.Text.Json"/>. 64-bit checksums, hashes and salts
/// are 16 lowercase hex digits.
/// </summary>
/// <param name="FormatVersion">The manifest's own version, <see cref="CurrentFormatVersion"/>.</param>
/// <param name="RulesRevision">The core's <see cref="SimCoreVersion.RulesRevision"/> when the directory was prepared.</param>
/// <param name="Formats">The core's format versions when the directory was prepared.</param>
/// <param name="FirstSeed">The first generated seed.</param>
/// <param name="LastSeed">The last generated seed.</param>
/// <param name="Streams">The committed streams in name order, then the generated ones in seed order.</param>
/// <param name="Perturbation">The one-ulp check's stream, step and field (SC-002).</param>
/// <param name="Tables">The cross-build table files.</param>
/// <param name="Refusals">The streams every build must refuse.</param>
internal sealed record AgreementManifest(
    int FormatVersion,
    uint RulesRevision,
    ManifestFormats Formats,
    int FirstSeed,
    int LastSeed,
    IReadOnlyList<ManifestStream> Streams,
    ManifestPerturbation Perturbation,
    ManifestTables Tables,
    IReadOnlyList<ManifestRefusal> Refusals)
{
    /// <summary>The manifest's own format version.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>A committed stream's <c>origin</c>.</summary>
    public const string Committed = "committed";

    /// <summary>A generated stream's <c>origin</c>.</summary>
    public const string Generated = "generated";

    private const string Root = "$";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary>The manifest as UTF-8 JSON, in the contract example's field order, ending with a line break.</summary>
    public byte[] ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteNumber("rulesRevision", RulesRevision);
            writer.WriteStartObject("formats");
            writer.WriteNumber("inputCommand", Formats.InputCommand);
            writer.WriteNumber("collisionData", Formats.CollisionData);
            writer.WriteNumber("recordedStream", Formats.RecordedStream);
            writer.WriteEndObject();
            writer.WriteStartObject("seeds");
            writer.WriteNumber("first", FirstSeed);
            writer.WriteNumber("last", LastSeed);
            writer.WriteEndObject();
            writer.WriteStartArray("streams");
            foreach (var stream in Streams)
            {
                WriteStream(writer, stream);
            }

            writer.WriteEndArray();
            writer.WriteStartObject("perturbation");
            writer.WriteString("stream", Perturbation.Stream);
            writer.WriteNumber("step", Perturbation.Step);
            writer.WriteString("field", Perturbation.Field);
            writer.WriteEndObject();
            writer.WriteStartObject("tables");
            writer.WriteString("inputs", Tables.Inputs);
            writer.WriteString("config", Tables.Config);
            writer.WriteString("serverResults", Tables.ServerResults);
            writer.WriteEndObject();
            writer.WriteStartArray("refusals");
            foreach (var refusal in Refusals)
            {
                writer.WriteStartObject();
                writer.WriteString("name", refusal.Name);
                writer.WriteString("stream", refusal.Stream);
                writer.WriteString("collision", refusal.Collision);
                writer.WriteString("expected", refusal.Expected);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return [.. buffer.WrittenSpan, (byte)'\n'];
    }

    /// <summary>
    /// The versions a manifest was prepared under: <c>formatVersion</c>, <c>rulesRevision</c> and
    /// <c>formats</c>, read before anything else so a manifest from other rules or formats is reported as
    /// not comparable rather than read.
    /// </summary>
    /// <exception cref="InvalidDataException">A version is missing or not a whole number in range.</exception>
    public static ManifestVersions ReadVersions(JsonElement root)
    {
        var formats = Member(root, "formats", Root, JsonValueKind.Object);
        return new ManifestVersions(
            Int(root, "formatVersion", Root),
            UInt(root, "rulesRevision", Root),
            new ManifestFormats(
                Int(formats, "inputCommand", "$.formats"),
                Int(formats, "collisionData", "$.formats"),
                Int(formats, "recordedStream", "$.formats")));
    }

    /// <summary>The whole manifest. Stream names must match <c>^[a-z0-9-]+$</c>, be unique and not be <c>tables</c>.</summary>
    /// <exception cref="InvalidDataException">A member is missing, of the wrong kind or out of range, or a stream name is refused.</exception>
    public static AgreementManifest Read(JsonElement root)
    {
        var versions = ReadVersions(root);
        var seeds = Member(root, "seeds", Root, JsonValueKind.Object);
        var streams = new List<ManifestStream>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in Member(root, "streams", Root, JsonValueKind.Array).EnumerateArray())
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"$.streams[{index++}]");
            var stream = ReadStream(entry, path);
            if (!IsStreamName(stream.Name) || !names.Add(stream.Name))
            {
                throw Unreadable($"{path}.name {stream.Name} is not a unique name of lowercase letters, digits and hyphens, other than tables");
            }

            streams.Add(stream);
        }

        var perturbation = Member(root, "perturbation", Root, JsonValueKind.Object);
        var tables = Member(root, "tables", Root, JsonValueKind.Object);
        var refusals = new List<ManifestRefusal>();
        index = 0;
        foreach (var entry in Member(root, "refusals", Root, JsonValueKind.Array).EnumerateArray())
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"$.refusals[{index++}]");
            refusals.Add(new ManifestRefusal(Text(entry, "name", path), Text(entry, "stream", path), Text(entry, "collision", path), Text(entry, "expected", path)));
        }

        return new AgreementManifest(
            versions.FormatVersion,
            versions.RulesRevision,
            versions.Formats,
            Int(seeds, "first", "$.seeds"),
            Int(seeds, "last", "$.seeds"),
            streams,
            new ManifestPerturbation(Text(perturbation, "stream", "$.perturbation"), UInt(perturbation, "step", "$.perturbation"), Text(perturbation, "field", "$.perturbation")),
            new ManifestTables(Text(tables, "inputs", "$.tables"), Text(tables, "config", "$.tables"), Text(tables, "serverResults", "$.tables")),
            refusals);
    }

    /// <summary>Whether <paramref name="name"/> is a stream name: lowercase letters, digits and hyphens, and not <c>tables</c>.</summary>
    public static bool IsStreamName(string name) =>
        name.Length > 0 && name != "tables" && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static void WriteStream(Utf8JsonWriter writer, ManifestStream stream)
    {
        writer.WriteStartObject();
        writer.WriteString("name", stream.Name);
        writer.WriteString("origin", stream.Origin);
        writer.WriteNumber("seed", stream.Seed);
        writer.WriteString("file", stream.File);
        writer.WriteString("collision", stream.Collision);
        writer.WriteString("collisionChecksum", Hex(stream.CollisionChecksum));
        writer.WriteNumber("stepRateHz", stream.StepRateHz);
        writer.WriteNumber("steps", stream.Steps);
        writer.WriteNumber("characters", stream.Characters);
        writer.WriteString("serverRecords", stream.ServerRecords);
        writer.WriteString("finalHash", Hex(stream.FinalHash));
        if (stream.Batch is { } batch)
        {
            writer.WriteStartObject("batch");
            writer.WriteString("config", batch.Config);
            writer.WriteString("initialState", batch.InitialState);
            writer.WriteString("commands", batch.Commands);
            writer.WriteString("matchSalt", Hex(batch.MatchSalt));
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("batch");
        }

        writer.WriteEndObject();
    }

    private static ManifestStream ReadStream(JsonElement entry, string path)
    {
        ManifestBatch? batch = null;
        if (Member(entry, "batch", path, JsonValueKind.Undefined) is { ValueKind: not JsonValueKind.Null } node)
        {
            var batchPath = path + ".batch";
            batch = new ManifestBatch(Text(node, "config", batchPath), Text(node, "initialState", batchPath), Text(node, "commands", batchPath), HexValue(node, "matchSalt", batchPath));
        }

        return new ManifestStream(
            Text(entry, "name", path),
            Text(entry, "origin", path),
            Number(entry, "seed", path, static (JsonElement e, out ulong v) => e.TryGetUInt64(out v)),
            Text(entry, "file", path),
            Text(entry, "collision", path),
            HexValue(entry, "collisionChecksum", path),
            Int(entry, "stepRateHz", path),
            Int(entry, "steps", path),
            Int(entry, "characters", path),
            Text(entry, "serverRecords", path),
            HexValue(entry, "finalHash", path),
            batch);
    }

    /// <summary>
    /// The member <paramref name="name"/> of the object <paramref name="obj"/>, which must be of
    /// <paramref name="kind"/>; <see cref="JsonValueKind.Undefined"/> accepts an object or null.
    /// </summary>
    private static JsonElement Member(JsonElement obj, string name, string path, JsonValueKind kind)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var value)
            && (value.ValueKind == kind || (kind == JsonValueKind.Undefined && value.ValueKind is JsonValueKind.Object or JsonValueKind.Null)))
        {
            return value;
        }

        var expected = kind == JsonValueKind.Undefined ? "an object or null" : "a JSON " + kind.ToString().ToLowerInvariant();
        throw Unreadable($"{path}.{name} is missing or is not {expected}");
    }

    private static string Text(JsonElement obj, string name, string path) => Member(obj, name, path, JsonValueKind.String).GetString()!;

    private static int Int(JsonElement obj, string name, string path) =>
        Number(obj, name, path, static (JsonElement e, out int v) => e.TryGetInt32(out v));

    private static uint UInt(JsonElement obj, string name, string path) =>
        Number(obj, name, path, static (JsonElement e, out uint v) => e.TryGetUInt32(out v));

    private static T Number<T>(JsonElement obj, string name, string path, TryGet<T> get)
    {
        var value = Member(obj, name, path, JsonValueKind.Number);
        return get(value, out var number)
            ? number
            : throw Unreadable($"{path}.{name} {value.GetRawText()} is not a whole number in range");
    }

    /// <summary>A 64-bit value written as 16 lowercase hex digits.</summary>
    private static ulong HexValue(JsonElement obj, string name, string path)
    {
        var text = Text(obj, name, path);
        return text.Length == 16 && text.All(char.IsAsciiHexDigitLower) && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Unreadable($"{path}.{name} {text} is not 16 lowercase hex digits");
    }

    private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>A manifest that cannot be read: the message names <c>manifest.json</c> and the JSON path.</summary>
    private static InvalidDataException Unreadable(string problem) => new(string.Concat(WorkDirectory.ManifestFile, ": ", problem));

    private delegate bool TryGet<T>(JsonElement element, out T value);
}

/// <summary>The versions a manifest was prepared under.</summary>
internal sealed record ManifestVersions(int FormatVersion, uint RulesRevision, ManifestFormats Formats)
{
    /// <summary>
    /// Each way these versions differ from this build's, as <c>NOT COMPARABLE work</c> details naming both
    /// values (US2 scenario 5): the manifest's own format version, the rules revision, then the input
    /// command, collision data and recorded stream format versions. Empty when the work directory is
    /// comparable.
    /// </summary>
    public IReadOnlyList<string> Differences()
    {
        var differences = new List<string>();
        AddFormat(differences, "manifest", FormatVersion, AgreementManifest.CurrentFormatVersion);
        if (RulesRevision != SimCoreVersion.RulesRevision)
        {
            differences.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"rules revision {RulesRevision} in the work directory; this core implements rules revision {SimCoreVersion.RulesRevision}"));
        }

        var current = ManifestFormats.Current;
        AddFormat(differences, "input command", Formats.InputCommand, current.InputCommand);
        AddFormat(differences, "collision data", Formats.CollisionData, current.CollisionData);
        AddFormat(differences, "recorded stream", Formats.RecordedStream, current.RecordedStream);
        return differences;
    }

    private static void AddFormat(List<string> differences, string format, int version, int supported)
    {
        if (version != supported)
        {
            differences.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{format} format version {version} in the work directory; this reader supports version {supported}"));
        }
    }
}

/// <summary>The format versions: input command, collision data and recorded stream.</summary>
internal sealed record ManifestFormats(int InputCommand, int CollisionData, int RecordedStream)
{
    /// <summary>This build's format versions.</summary>
    public static ManifestFormats Current { get; } =
        new(InputCodec.FormatVersion, CollisionFile.FormatVersion, Formats.RecordedStream.FormatVersion);
}

/// <summary>One stream of the manifest; paths are relative to the work directory.</summary>
internal sealed record ManifestStream(
    string Name,
    string Origin,
    ulong Seed,
    string File,
    string Collision,
    ulong CollisionChecksum,
    int StepRateHz,
    int Steps,
    int Characters,
    string ServerRecords,
    ulong FinalHash,
    ManifestBatch? Batch);

/// <summary>A batchable stream's batch inputs and its header's match salt.</summary>
internal sealed record ManifestBatch(string Config, string InitialState, string Commands, ulong MatchSalt);

/// <summary>The one-ulp check: the stream, the step number and the field it changes.</summary>
internal sealed record ManifestPerturbation(string Stream, uint Step, string Field);

/// <summary>The cross-build table inputs, their config block and the server's results.</summary>
internal sealed record ManifestTables(string Inputs, string Config, string ServerResults);

/// <summary>A stream every build must refuse, the collision file to run it with, and the text its error must contain.</summary>
internal sealed record ManifestRefusal(string Name, string Stream, string Collision, string Expected);
