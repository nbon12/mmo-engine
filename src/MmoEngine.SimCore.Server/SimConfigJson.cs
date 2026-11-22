using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MmoEngine.SimCore.Config;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The outcome of binding a configuration: the bound, still unvalidated <see cref="SimConfig"/>, or the
/// errors that stopped the binding, each keyed by its camelCase JSON path. Exactly one is set: a
/// configuration with no errors, or no configuration with at least one error.
/// </summary>
public sealed record SimConfigLoad(SimConfig? Config, IReadOnlyList<ConfigError> Errors);

/// <summary>
/// Binds a JSON configuration file to <see cref="SimConfig"/> (specs/015-shared-simulation-core/contracts/config-rules.md#server).
/// JSON problems use the repo's wording, as <c>src/MmoEngine.Regionizer.Simulator/Config/RunConfigLoader.cs</c>
/// words them: <c>is not valid JSON (…)</c>, <c>config: must be a JSON object</c>, and
/// <c>has an invalid value or is not a known field (…)</c> keyed by the camelCase path. Binding checks no
/// value: <see cref="SimConfigValidator"/> applies the core's guard to what binds.
/// </summary>
/// <remarks>
/// Property names are camelCase and an unknown property is refused. <c>fireMode</c> is a member name,
/// <c>Auto</c> or <c>Semi</c>. Floats are read straight to binary32 with <see cref="Utf8JsonReader.GetSingle"/>,
/// which rounds the decimal once, so <c>9.81</c> binds to the bits of the C# literal <c>9.81f</c>. An absent
/// section or list binds as null, which the guard's shape rules refuse with <c>is required</c>. An absent
/// leaf field (an <c>int</c>, a <c>float</c> or <c>fireMode</c>) is refused here, before the guard, with one
/// <c>&lt;path&gt;: is required</c> per field: it would otherwise bind as 0 or <c>Auto</c> and pass every
/// rule whose minimum is 0 (issue #71).
/// </remarks>
public static class SimConfigJson
{
    /// <summary>
    /// The largest configuration file read, 1 MiB. A longer file is refused from its length, before it is
    /// read, and one without a length (a pipe or a device), or one that grew after its length was checked,
    /// once more than the limit has been read.
    /// </summary>
    public const long MaxFileLength = 1024 * 1024;

    private const string RootPath = "config";

    /// <summary>
    /// The refusal of a file found to hold more than <see cref="MaxFileLength"/> bytes only as it is read:
    /// <c>the file holds more than 1048576 bytes; the limit is 1048576</c>.
    /// </summary>
    public static string MoreThanTheLimit { get; } =
        string.Create(CultureInfo.InvariantCulture, $"the file holds more than {MaxFileLength} bytes; the limit is {MaxFileLength}");

    /// <summary>The serializer options: camelCase names, unknown members refused, <c>fireMode</c> by name.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonDocumentOptions DocumentOptions => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads and binds the file at <paramref name="path"/>. A missing file, a file longer than
    /// <see cref="MaxFileLength"/> (refused from its length, before it is read, or as
    /// <see cref="MoreThanTheLimit"/> once more than the limit has been read) or a file that cannot be read
    /// is one error at <c>config</c>; otherwise the result is <see cref="Parse"/>'s.
    /// </summary>
    public static SimConfigLoad Load(string path) => Load(path, OpenRead);

    /// <summary>
    /// <see cref="Load(string)"/>, reading the file through the stream <paramref name="open"/> gives for the
    /// path, so a test can give a stream without a length or a file that grows after its length is checked.
    /// </summary>
    internal static SimConfigLoad Load(string path, Func<string, Stream> open)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(open);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return Fail(RootPath, $"file '{path}' does not exist");
        }

        if (file.Length > MaxFileLength)
        {
            return Fail(RootPath, string.Create(CultureInfo.InvariantCulture, $"the file is {file.Length} bytes; the limit is {MaxFileLength}"));
        }

        string? json;
        try
        {
            using var stream = open(path);
            json = ReadText(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(RootPath, $"file '{path}' cannot be read ({ex.Message})");
        }

        return json is null ? Fail(RootPath, MoreThanTheLimit) : Parse(json);
    }

    /// <summary>
    /// The text of a configuration file's <paramref name="stream"/>, or null when it holds more than
    /// <see cref="MaxFileLength"/> bytes, reading at most the limit + 1 bytes
    /// (<see cref="BoundedRead.ReadAtMost"/>). The bytes are decoded as <see cref="File.ReadAllText(string)"/>
    /// decodes a file: UTF-8, unless a byte-order mark names another encoding.
    /// </summary>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static string? ReadText(Stream stream)
    {
        var bytes = BoundedRead.ReadAtMost(stream, (int)MaxFileLength);
        if (bytes is null)
        {
            return null;
        }

        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The file at <paramref name="path"/>, opened read-only and shared for reading.</summary>
    internal static Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Binds JSON text. Every error names its path; the configuration is null when there is one.</summary>
    public static SimConfigLoad Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Fail(FromJsonPath(ex.Path), $"is not valid JSON ({ex.Message})");
        }

        if (node is not JsonObject root)
        {
            return Fail(RootPath, "must be a JSON object");
        }

        var missing = new List<ConfigError>();
        FindMissingLeaves(root, SerializerOptions.GetTypeInfo(typeof(SimConfig)), prefix: null, missing);
        if (missing.Count > 0)
        {
            return new SimConfigLoad(null, missing);
        }

        SimConfig? config;
        try
        {
            config = root.Deserialize<SimConfig>(SerializerOptions);
        }
        catch (JsonException ex)
        {
            return Fail(FromJsonPath(ex.Path), $"has an invalid value or is not a known field ({FirstLine(ex.Message)})");
        }

        return config is null
            ? Fail(RootPath, "must be a JSON object")
            : new SimConfigLoad(config, []);
    }

    /// <summary><c>$.weapons[0].fireMode</c> (a System.Text.Json path) becomes <c>weapons[0].fireMode</c>; the root is <c>config</c>.</summary>
    internal static string FromJsonPath(string? jsonPath)
    {
        var path = jsonPath ?? string.Empty;
        if (path.StartsWith('$'))
        {
            path = path[1..];
        }

        path = path.TrimStart('.');
        return path.Length == 0 ? RootPath : path;
    }

    /// <summary>
    /// Adds one <c>&lt;path&gt;: is required</c> to <paramref name="errors"/> for each leaf property (a
    /// non-nullable value type) of <paramref name="info"/>'s object type that <paramref name="node"/> leaves
    /// out, recursing into every present object and list element. Nullable properties (sections, lists, list
    /// elements, capsule points) are skipped, so an absent one binds as null and the guard reports it. A node
    /// of the wrong JSON kind is left to deserialization, which refuses it with its own error.
    /// </summary>
    private static void FindMissingLeaves(JsonNode? node, JsonTypeInfo info, string? prefix, List<ConfigError> errors)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject obj:
                foreach (var property in info.Properties)
                {
                    var path = prefix is null ? property.Name : $"{prefix}.{property.Name}";
                    if (!obj.TryGetPropertyValue(property.Name, out var child))
                    {
                        if (IsLeaf(property.PropertyType))
                        {
                            errors.Add(new ConfigError(path, "is required"));
                        }

                        continue;
                    }

                    FindMissingLeaves(child, SerializerOptions.GetTypeInfo(property.PropertyType), path, errors);
                }

                break;
            case JsonTypeInfoKind.Enumerable when node is JsonArray array && info.ElementType is { } elementType:
                var elementInfo = SerializerOptions.GetTypeInfo(elementType);
                for (var i = 0; i < array.Count; i++)
                {
                    FindMissingLeaves(array[i], elementInfo, string.Create(CultureInfo.InvariantCulture, $"{prefix}[{i}]"), errors);
                }

                break;
        }
    }

    private static bool IsLeaf(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    private static SimConfigLoad Fail(string path, string message) => new(null, [new ConfigError(path, message)]);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter<FireMode>(namingPolicy: null, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
