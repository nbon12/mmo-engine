using System.Text.Json;
using System.Text.Json.Serialization;

namespace MmoEngine.Maps;

/// <summary>The kinds of portal validator finding (contracts/map-format.md, Validator findings).</summary>
public enum PortalFindingCode : byte
{
    /// <summary>An adjacency or opening crosses an area or layer boundary and matches no portal.</summary>
    Leak,

    /// <summary>A portal endpoint is outside its layer or blocked.</summary>
    DeadPortal,

    /// <summary>A portal's closed-state policy is absent or lacks one of its three fields.</summary>
    MissingPolicy,

    /// <summary>With all portals closed, a sealed area still reaches a section of another area.</summary>
    SealingFailure,
}

/// <summary>
/// One validator finding. <see cref="Location"/> names the entry in the file (<c>adjacency[2]</c>,
/// <c>openings:window-3</c>, <c>portals:1</c> or <c>sealedAreas:bunker</c>), and
/// <see cref="ToString"/> is the CLI's line format, <c>&lt;Code&gt; &lt;location&gt; &lt;message&gt;</c>.
/// </summary>
public sealed record PortalFinding(PortalFindingCode Code, string Location, string Message)
{
    public override string ToString() => $"{Code} {Location} {Message}";
}

/// <summary>
/// The JSON form of a list of findings, as <c>portal-validator --json</c> writes it: an array of
/// <c>{ "code", "location", "message" }</c> objects with the code as its name.
/// </summary>
public static class PortalFindingJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter<PortalFindingCode>(allowIntegerValues: false) },
    };

    public static string Serialize(IReadOnlyList<PortalFinding> findings) => JsonSerializer.Serialize(findings, Options);

    /// <summary>Reads a findings array, such as a <c>*.expected.json</c> file. Throws <see cref="JsonException"/> on anything else.</summary>
    public static IReadOnlyList<PortalFinding> Deserialize(string json) =>
        JsonSerializer.Deserialize<List<PortalFinding>>(json, Options) ?? throw new JsonException("Expected a findings array, found null.");
}
