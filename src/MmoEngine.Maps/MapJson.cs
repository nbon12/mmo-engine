using System.Text.Json;
using System.Text.Json.Serialization;

namespace MmoEngine.Maps;

/// <summary>
/// The map file as written, before validation. Every field is nullable so that the reader can name
/// a missing one. Fields the format does not define land in <c>Unknown</c>, and the reader rejects
/// them with their path.
/// </summary>
internal sealed class MapJson
{
    public int? FormatVersion { get; set; }

    public string? Name { get; set; }

    public int? SectionSize { get; set; }

    public List<LayerJson?>? Layers { get; set; }

    public List<string?>? SealedAreas { get; set; }

    public List<ConnectionJson?>? Adjacency { get; set; }

    public List<ConnectionJson?>? Openings { get; set; }

    public List<PortalJson?>? Portals { get; set; }

    public List<ScheduleJson?>? PortalSchedule { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class LayerJson
{
    public int? Id { get; set; }

    public string? Name { get; set; }

    public int? OriginX { get; set; }

    public int? OriginY { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public string? DefaultArea { get; set; }

    public List<AreaJson?>? Areas { get; set; }

    public List<int[]?>? Blocked { get; set; }

    /// <summary>Optional; absent means false. True for the optional air layer, which entities enter and leave without a portal.</summary>
    public bool? AllowsDirectEntry { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class AreaJson
{
    public string? Tag { get; set; }

    public List<int[]?>? Rects { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class ConnectionJson
{
    public string? Id { get; set; }

    public string? Kind { get; set; }

    public int[]? A { get; set; }

    public int[]? B { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class PortalJson
{
    public int? Id { get; set; }

    public string? Name { get; set; }

    public int[]? A { get; set; }

    public int[]? B { get; set; }

    public bool? Open { get; set; }

    public ClosedPolicyJson? ClosedPolicy { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class ClosedPolicyJson
{
    public bool? EntityTransfers { get; set; }

    public bool? DoorAndObjectiveEvents { get; set; }

    public bool? DeferredProjectiles { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class ScheduleJson
{
    public int? Portal { get; set; }

    public double? EverySeconds { get; set; }

    public double? OpenSeconds { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
