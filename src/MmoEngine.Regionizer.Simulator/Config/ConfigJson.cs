using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>
/// JSON handling for configuration (research D12): the serializer settings, the deep merge of a run
/// config over scenario defaults, dotted-path edits, canonical JSON and the config hash.
/// </summary>
public static class ConfigJson
{
    /// <summary>camelCase names, enums as strings, unknown fields rejected.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    /// <summary>
    /// Merges <paramref name="overlay"/> into a copy of <paramref name="baseObject"/>: objects merge
    /// key by key, recursively; arrays, scalars and nulls replace.
    /// </summary>
    public static JsonObject DeepMerge(JsonObject baseObject, JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(baseObject);
        ArgumentNullException.ThrowIfNull(overlay);
        var result = (JsonObject)baseObject.DeepClone();
        MergeInto(result, overlay);
        return result;
    }

    /// <summary>
    /// Sets the value at a dotted path such as <c>regionizer.claimRadius.infantry</c>, creating
    /// objects along the way. Sweeps use this to step a parameter.
    /// </summary>
    public static void SetPath(JsonObject root, string dottedPath, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(dottedPath);
        var parts = dottedPath.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next)
            {
                next = [];
                current[parts[i]] = next;
            }

            current = next;
        }

        current[parts[^1]] = value?.DeepClone();
    }

    /// <summary>The value at a dotted path, or null when any part is missing.</summary>
    public static JsonNode? GetPath(JsonObject root, string dottedPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        JsonNode? current = root;
        foreach (var part in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>The configuration as a JSON object, in the serializer's camelCase shape.</summary>
    public static JsonObject ToJsonObject(RunConfig config) =>
        JsonSerializer.SerializeToNode(config, SerializerOptions)!.AsObject();

    /// <summary>
    /// Canonical JSON: object keys sorted by ordinal, no whitespace, integers written as integers and
    /// other numbers in shortest round-trip form. Equal configurations give equal text.
    /// </summary>
    public static string Canonical(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The canonical JSON of a configuration.</summary>
    public static string Canonical(RunConfig config) => Canonical(ToJsonObject(config));

    /// <summary>XxHash64 of the canonical JSON, as 16 lowercase hex digits.</summary>
    public static string Hash(string canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        return XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(canonicalJson)).ToString("x16", CultureInfo.InvariantCulture);
    }

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
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject overlayChild && target[key] is JsonObject targetChild)
            {
                MergeInto(targetChild, overlayChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteCanonical(writer, value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteValue(writer, value);
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON node {node.GetType()}.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetValue<string>());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, value);
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON value kind {value.GetValueKind()}.");
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, JsonValue value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        if (element.TryGetInt64(out var integer))
        {
            writer.WriteNumberValue(integer);
        }
        else if (element.TryGetUInt64(out var unsigned))
        {
            writer.WriteNumberValue(unsigned);
        }
        else
        {
            var number = element.GetDouble();
            if (number == Math.Floor(number) && Math.Abs(number) < 9.0e15)
            {
                writer.WriteNumberValue((long)number);
            }
            else
            {
                writer.WriteNumberValue(number);
            }
        }
    }
}
