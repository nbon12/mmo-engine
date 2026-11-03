using System.Diagnostics.CodeAnalysis;
using MmoEngine.Regionizer;

namespace MmoEngine.Maps;

/// <summary>
/// A map file (contracts/map-format.md, format version 1): layers of sections with area tags and
/// blocked sections, explicit adjacency, openings, portals, sealed areas and the portal schedule.
/// Immutable once loaded. Area tags get ids in ordinal tag-name order, so an id never depends on
/// the order in which the file mentions its tag.
/// </summary>
public sealed class MapDocument
{
    /// <summary>The only map format version this reader accepts.</summary>
    public const int SupportedFormatVersion = 1;

    private readonly Dictionary<int, MapLayer> _layerById;
    private readonly Dictionary<string, ushort> _areaIdByName;

    internal MapDocument(
        string name,
        int sectionSize,
        IReadOnlyList<MapLayer> layers,
        IReadOnlyList<string> areaNames,
        IReadOnlyList<string> sealedAreas,
        IReadOnlyList<MapConnection> adjacency,
        IReadOnlyList<MapConnection> openings,
        IReadOnlyList<MapPortal> portals,
        IReadOnlyList<PortalScheduleEntry> portalSchedule)
    {
        Name = name;
        SectionSize = sectionSize;
        Layers = layers;
        AreaNames = areaNames;
        SealedAreas = sealedAreas;
        Adjacency = adjacency;
        Openings = openings;
        Portals = portals;
        PortalSchedule = portalSchedule;
        _layerById = layers.ToDictionary(l => l.Id);
        _areaIdByName = new Dictionary<string, ushort>(areaNames.Count, StringComparer.Ordinal);
        for (var i = 0; i < areaNames.Count; i++)
        {
            _areaIdByName.Add(areaNames[i], (ushort)i);
        }
    }

    public string Name { get; }

    /// <summary>Section side in metres, a power of two.</summary>
    public int SectionSize { get; }

    /// <summary>The layers in file order, which is also their order in <see cref="ToTopology"/>.</summary>
    public IReadOnlyList<MapLayer> Layers { get; }

    /// <summary>Every area tag, sorted ordinally. The index of a tag is its area id.</summary>
    public IReadOnlyList<string> AreaNames { get; }

    public IReadOnlyList<string> SealedAreas { get; }

    public IReadOnlyList<MapConnection> Adjacency { get; }

    public IReadOnlyList<MapConnection> Openings { get; }

    public IReadOnlyList<MapPortal> Portals { get; }

    public IReadOnlyList<PortalScheduleEntry> PortalSchedule { get; }

    /// <summary>Reads and checks a map file. Throws <see cref="MapFormatException"/>, naming the file, if it can't be read or fails a schema check.</summary>
    public static MapDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MapFormatException($"{path}: could not be read: {ex.Message}", ex);
        }

        try
        {
            return MapReader.Read(json);
        }
        catch (MapFormatException ex)
        {
            throw new MapFormatException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses and checks map JSON. Throws <see cref="MapFormatException"/> naming the failing field.</summary>
    public static MapDocument Parse(string json) => MapReader.Read(json);

    /// <summary>The layer with this id, if the map has one.</summary>
    public bool TryGetLayer(int layerId, [MaybeNullWhen(false)] out MapLayer layer) => _layerById.TryGetValue(layerId, out layer);

    /// <summary>The id of an area tag, if the map uses it.</summary>
    public bool TryGetAreaId(string tag, out ushort areaId) => _areaIdByName.TryGetValue(tag, out areaId);

    /// <summary>The id of an area tag. Throws <see cref="ArgumentException"/> for a tag the map doesn't use.</summary>
    public ushort AreaIdOf(string tag) =>
        TryGetAreaId(tag, out var id) ? id : throw new ArgumentException($"Map {Name} has no area '{tag}'.", nameof(tag));

    /// <summary>Whether a section lies inside one of the map's layers.</summary>
    public bool Contains(SectionKey key) => TryGetLayer(key.Layer, out var layer) && layer.Contains(key.X, key.Y);

    /// <summary>The area id of a section. Throws <see cref="ArgumentOutOfRangeException"/> outside every layer.</summary>
    public ushort AreaIdAt(SectionKey key) => LayerOf(key).AreaIdAt(key.X, key.Y);

    /// <summary>Whether a section is blocked. Throws <see cref="ArgumentOutOfRangeException"/> outside every layer.</summary>
    public bool IsBlocked(SectionKey key) => LayerOf(key).IsBlocked(key.X, key.Y);

    /// <summary>
    /// The core topology of this map: every layer with its area ids, and every portal with its
    /// initial open flag. Blocked sections keep their area tag, since claims follow areas and
    /// portals only (research D2). Call it on a map the validator accepts: a portal endpoint outside
    /// every layer makes the core throw <see cref="ArgumentException"/>.
    /// </summary>
    public SectionTopology ToTopology()
    {
        var layers = new List<LayerSpec>(Layers.Count);
        foreach (var layer in Layers)
        {
            layers.Add(new LayerSpec(layer.Id, layer.OriginX, layer.OriginY, layer.Width, layer.Height, layer.AreaIds));
        }

        var portals = new List<PortalSpec>(Portals.Count);
        foreach (var portal in Portals)
        {
            portals.Add(new PortalSpec(portal.Id, portal.A, portal.B, portal.Open));
        }

        return SectionTopology.Build(SectionSize, layers, portals);
    }

    private MapLayer LayerOf(SectionKey key)
    {
        if (!TryGetLayer(key.Layer, out var layer))
        {
            throw new ArgumentOutOfRangeException(nameof(key), $"Map {Name} has no layer {key.Layer}.");
        }

        return layer;
    }
}
