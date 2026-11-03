using MmoEngine.Regionizer;

namespace MmoEngine.Maps;

/// <summary>An inclusive rectangle of sections, <c>[X0, Y0, X1, Y1]</c>, in its layer's section coordinates.</summary>
public readonly record struct MapRect(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Whether the section (<paramref name="x"/>, <paramref name="y"/>) lies inside, corners included.</summary>
    public bool Contains(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
}

/// <summary>An area tag and the rectangles it covers on one layer.</summary>
public sealed record MapArea(string Tag, IReadOnlyList<MapRect> Rects);

/// <summary>Which list of the map file a connection comes from.</summary>
public enum MapConnectionType : byte
{
    /// <summary>A hand-authored navigable connection (walkway, ramp, stairs).</summary>
    Adjacency,

    /// <summary>An interaction opening (window, murder hole): instant interactions without walking through.</summary>
    Opening,
}

/// <summary>An <c>adjacency</c> or <c>openings</c> entry: a connection between two sections, which may lie on different layers.</summary>
public sealed record MapConnection(MapConnectionType Type, int Index, string? Id, string? Kind, SectionKey A, SectionKey B)
{
    /// <summary>Where the entry sits in the file: <c>adjacency:walkway-1</c> by ID, or <c>adjacency[3]</c> by index when it has none.</summary>
    public string Location
    {
        get
        {
            var list = Type == MapConnectionType.Adjacency ? "adjacency" : "openings";
            return Id is null ? $"{list}[{Index}]" : $"{list}:{Id}";
        }
    }
}

/// <summary>
/// What crosses a portal while it is closed. A null field was absent from the file, which the
/// portal validator reports as a missing policy.
/// </summary>
public sealed record ClosedPolicy(bool? EntityTransfers, bool? DoorAndObjectiveEvents, bool? DeferredProjectiles);

/// <summary>A declared portal: an edge between two sections, initially open or closed, with its closed-state policy.</summary>
public sealed record MapPortal(int Id, string? Name, SectionKey A, SectionKey B, bool Open, ClosedPolicy? ClosedPolicy)
{
    /// <summary>Where the portal sits in the file: <c>portals:&lt;id&gt;</c>.</summary>
    public string Location => $"portals:{Id}";
}

/// <summary>
/// A fixed open and close cycle for a portal, used by the simulator only (the validator ignores it):
/// every <see cref="EverySeconds"/> the portal is open for <see cref="OpenSeconds"/>.
/// </summary>
public sealed record PortalScheduleEntry(int PortalId, double EverySeconds, double OpenSeconds);

/// <summary>
/// One layer's section grid: its rectangle in section coordinates, its area tags (rectangles over a
/// default area) and its blocked, non-navigable sections.
/// </summary>
public sealed class MapLayer
{
    private readonly ushort[] _areaIds;
    private readonly bool[] _blocked;

    internal MapLayer(
        int id,
        string? name,
        int originX,
        int originY,
        int width,
        int height,
        string defaultArea,
        IReadOnlyList<MapArea> areas,
        IReadOnlyList<MapRect> blocked,
        ushort[] areaIds,
        bool[] blockedCells)
    {
        Id = id;
        Name = name;
        OriginX = originX;
        OriginY = originY;
        Width = width;
        Height = height;
        DefaultArea = defaultArea;
        Areas = areas;
        Blocked = blocked;
        _areaIds = areaIds;
        _blocked = blockedCells;
    }

    public int Id { get; }

    public string? Name { get; }

    public int OriginX { get; }

    public int OriginY { get; }

    /// <summary>Width in sections.</summary>
    public int Width { get; }

    /// <summary>Height in sections.</summary>
    public int Height { get; }

    /// <summary>The area of every section that no <see cref="Areas"/> rectangle covers.</summary>
    public string DefaultArea { get; }

    public IReadOnlyList<MapArea> Areas { get; }

    /// <summary>The blocked rectangles as written. Blocked sections keep their area tag.</summary>
    public IReadOnlyList<MapRect> Blocked { get; }

    /// <summary>Row-major area ids, <c>Width * Height</c> long, as the core's <see cref="LayerSpec.AreaTags"/>.</summary>
    internal ushort[] AreaIds => _areaIds;

    /// <summary>Whether the section (<paramref name="x"/>, <paramref name="y"/>) lies inside this layer.</summary>
    public bool Contains(int x, int y) =>
        (ulong)((long)x - OriginX) < (ulong)Width && (ulong)((long)y - OriginY) < (ulong)Height;

    /// <summary>The area id of a section of this layer; <see cref="MapDocument.AreaNames"/> holds its tag.</summary>
    public ushort AreaIdAt(int x, int y) => _areaIds[LocalIndex(x, y)];

    /// <summary>Whether a section of this layer is blocked (not navigable).</summary>
    public bool IsBlocked(int x, int y) => _blocked[LocalIndex(x, y)];

    private int LocalIndex(int x, int y)
    {
        if (!Contains(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Section ({x}, {y}) is outside layer {Id}.");
        }

        return ((y - OriginY) * Width) + (x - OriginX);
    }
}
