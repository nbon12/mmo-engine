using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
/// section or list binds as null, which the guard's shape rules refuse with <c>is required</c>.
/// </remarks>
public static class SimConfigJson
{
    /// <summary>The largest configuration file read, 1 MiB; a longer one is refused before it is read.</summary>
    public const long MaxFileLength = 1024 * 1024;

    private const string RootPath = "config";

    /// <summary>The serializer options: camelCase names, unknown members refused, <c>fireMode</c> by name.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonDocumentOptions DocumentOptions => new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads and binds the file at <paramref name="path"/>. A missing file, a file longer than
    /// <see cref="MaxFileLength"/> (refused from its length, before it is read) or a file that cannot be
    /// read is one error at <c>config</c>; otherwise the result is <see cref="Parse"/>'s.
    /// </summary>
    public static SimConfigLoad Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return Fail(RootPath, $"file '{path}' does not exist");
        }

        if (file.Length > MaxFileLength)
        {
            return Fail(RootPath, string.Create(CultureInfo.InvariantCulture, $"the file is {file.Length} bytes; the limit is {MaxFileLength}"));
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(RootPath, $"file '{path}' cannot be read ({ex.Message})");
        }

        return Parse(json);
    }

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
