namespace MmoEngine.Regionizer;

/// <summary>A layer of the section grid. <paramref name="AreaTags"/> is row-major, <c>Width * Height</c> long.</summary>
public readonly record struct LayerSpec(int LayerId, int OriginX, int OriginY, int Width, int Height, ushort[] AreaTags);

/// <summary>A declared portal between two sections, initially open or closed.</summary>
public readonly record struct PortalSpec(int PortalId, SectionKey A, SectionKey B, bool Open);

/// <summary>
/// The fixed geometry the regionizer works on: layers of power-of-two square sections in one dense
/// index space, area tags and portal endpoints (research D1, D2). Immutable once built.
/// </summary>
public sealed class SectionTopology
{
    private readonly Layer[] _layers;
    private readonly Dictionary<int, int> _slotByLayerId;
    private readonly int[] _layerSlotOfIndex;
    private readonly PortalSpec[] _portals;
    private readonly Dictionary<int, int> _portalSlotById;
    private readonly Dictionary<int, PortalLink[]> _linksBySection;

    private SectionTopology(int sectionSize, Layer[] layers, PortalSpec[] portals)
    {
        SectionSize = sectionSize;
        _layers = layers;
        _slotByLayerId = new Dictionary<int, int>(layers.Length);
        var count = 0;
        for (var i = 0; i < layers.Length; i++)
        {
            _slotByLayerId.Add(layers[i].Id, i);
            count += layers[i].Width * layers[i].Height;
        }

        SectionCount = count;
        _layerSlotOfIndex = new int[count];
        for (var i = 0; i < layers.Length; i++)
        {
            Array.Fill(_layerSlotOfIndex, i, layers[i].BaseIndex, layers[i].Width * layers[i].Height);
        }

        _portals = portals;
        _portalSlotById = new Dictionary<int, int>(portals.Length);
        var links = new Dictionary<int, List<PortalLink>>();
        for (var p = 0; p < portals.Length; p++)
        {
            var spec = portals[p];
            if (!_portalSlotById.TryAdd(spec.PortalId, p))
            {
                throw new ArgumentException($"Duplicate portal id {spec.PortalId}.");
            }

            if (!TryIndexOf(spec.A, out var a) || !TryIndexOf(spec.B, out var b))
            {
                throw new ArgumentException($"Portal {spec.PortalId} has an endpoint outside every layer.");
            }

            if (a == b)
            {
                throw new ArgumentException($"Portal {spec.PortalId} joins a section to itself.");
            }

            AddLink(links, a, new PortalLink(p, b));
            AddLink(links, b, new PortalLink(p, a));
            _layers[_layerSlotOfIndex[a]].HasPortals = true;
            _layers[_layerSlotOfIndex[b]].HasPortals = true;
        }

        _linksBySection = new Dictionary<int, PortalLink[]>(links.Count);
        foreach (var (section, list) in links)
        {
            _linksBySection.Add(section, [.. list]);
        }

        PortalEndpoints = [.. _linksBySection.Keys.Order()];
    }

    /// <summary>Section side in metres.</summary>
    public int SectionSize { get; }

    /// <summary>The number of sections over all layers; indices run from 0 to this minus one.</summary>
    public int SectionCount { get; }

    /// <summary>The number of declared portals.</summary>
    public int PortalCount => _portals.Length;

    internal int[] PortalEndpoints { get; }

    /// <summary>A topology with one open-world layer 0 covering the map, plus optional uniform layers of the same size.</summary>
    public static SectionTopology OpenWorld(int sectionSize, double widthMetres, double heightMetres, params int[] extraOpenLayers)
    {
        CheckSectionSize(sectionSize);
        if (widthMetres <= 0 || heightMetres <= 0)
        {
            throw new ArgumentException("Map sides must be positive.");
        }

        var w = (int)Math.Ceiling(widthMetres / sectionSize);
        var h = (int)Math.Ceiling(heightMetres / sectionSize);
        var specs = new List<LayerSpec> { new(0, 0, 0, w, h, new ushort[w * h]) };
        foreach (var id in extraOpenLayers)
        {
            specs.Add(new LayerSpec(id, 0, 0, w, h, new ushort[w * h]));
        }

        return Build(sectionSize, specs, []);
    }

    /// <summary>Builds a topology from explicit layers and portals.</summary>
    public static SectionTopology Build(int sectionSize, IReadOnlyList<LayerSpec> layers, IReadOnlyList<PortalSpec> portals)
    {
        CheckSectionSize(sectionSize);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(portals);
        if (layers.Count == 0)
        {
            throw new ArgumentException("A topology needs at least one layer.");
        }

        var built = new Layer[layers.Count];
        var baseIndex = 0;
        var seen = new HashSet<int>();
        for (var i = 0; i < layers.Count; i++)
        {
            var spec = layers[i];
            if (!seen.Add(spec.LayerId))
            {
                throw new ArgumentException($"Duplicate layer id {spec.LayerId}.");
            }

            if (spec.Width <= 0 || spec.Height <= 0)
            {
                throw new ArgumentException($"Layer {spec.LayerId} must have a positive size.");
            }

            if (spec.AreaTags is null || spec.AreaTags.Length != spec.Width * spec.Height)
            {
                throw new ArgumentException($"Layer {spec.LayerId} needs exactly Width * Height area tags.");
            }

            var uniform = true;
            for (var t = 1; t < spec.AreaTags.Length && uniform; t++)
            {
                uniform = spec.AreaTags[t] == spec.AreaTags[0];
            }

            built[i] = new Layer(spec.LayerId, spec.OriginX, spec.OriginY, spec.Width, spec.Height, baseIndex, (ushort[])spec.AreaTags.Clone(), uniform);
            baseIndex = checked(baseIndex + (spec.Width * spec.Height));
        }

        return new SectionTopology(sectionSize, built, [.. portals]);
    }

    /// <summary>The dense index of <paramref name="key"/>, if it lies inside its layer.</summary>
    public bool TryIndexOf(SectionKey key, out int index)
    {
        if (_slotByLayerId.TryGetValue(key.Layer, out var slot))
        {
            ref readonly var layer = ref _layers[slot];
            var lx = key.X - layer.OriginX;
            var ly = key.Y - layer.OriginY;
            if ((uint)lx < (uint)layer.Width && (uint)ly < (uint)layer.Height)
            {
                index = layer.BaseIndex + (ly * layer.Width) + lx;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>The key of a dense index.</summary>
    public SectionKey KeyOf(int index)
    {
        ref readonly var layer = ref _layers[_layerSlotOfIndex[index]];
        var local = index - layer.BaseIndex;
        return new SectionKey(layer.Id, layer.OriginX + (local % layer.Width), layer.OriginY + (local / layer.Width));
    }

    /// <summary>The area tag of a section.</summary>
    public ushort AreaOf(int index) => _layers[_layerSlotOfIndex[index]].AreaTags[index - _layers[_layerSlotOfIndex[index]].BaseIndex];

    /// <summary>The section holding a position: floor(x / s), floor(y / s) on the given layer.</summary>
    public bool TrySectionAt(int layer, double x, double y, out int index)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
        {
            index = -1;
            return false;
        }

        var sx = Math.Floor(x / SectionSize);
        var sy = Math.Floor(y / SectionSize);
        if (sx < int.MinValue || sx > int.MaxValue || sy < int.MinValue || sy > int.MaxValue)
        {
            index = -1;
            return false;
        }

        return TryIndexOf(new SectionKey(layer, (int)sx, (int)sy), out index);
    }

    /// <summary>Whether a layer with this id exists.</summary>
    public bool HasLayer(int layerId) => _slotByLayerId.ContainsKey(layerId);

    /// <summary>The portal's spec as built (its initial open flag).</summary>
    public PortalSpec PortalAt(int slot) => _portals[slot];

    internal bool TryPortalSlot(int portalId, out int slot) => _portalSlotById.TryGetValue(portalId, out slot);

    internal int LayerSlotOf(int index) => _layerSlotOfIndex[index];

    internal bool TryLayerSlot(int layerId, out int slot) => _slotByLayerId.TryGetValue(layerId, out slot);

    internal ref readonly Layer LayerAt(int slot) => ref _layers[slot];

    internal int LayerCount => _layers.Length;

    internal ReadOnlySpan<PortalLink> LinksOf(int index) =>
        _linksBySection.TryGetValue(index, out var links) ? links : ReadOnlySpan<PortalLink>.Empty;

    internal bool IsPortalEndpoint(int index) => _linksBySection.ContainsKey(index);

    private static void AddLink(Dictionary<int, List<PortalLink>> links, int section, PortalLink link)
    {
        if (!links.TryGetValue(section, out var list))
        {
            list = [];
            links.Add(section, list);
        }

        list.Add(link);
    }

    private static void CheckSectionSize(int sectionSize)
    {
        if (sectionSize <= 0 || (sectionSize & (sectionSize - 1)) != 0)
        {
            throw new ArgumentException($"Section size must be a positive power of two, was {sectionSize}.");
        }
    }

    internal struct Layer(int id, int originX, int originY, int width, int height, int baseIndex, ushort[] areaTags, bool uniformArea)
    {
        public readonly int Id = id;
        public readonly int OriginX = originX;
        public readonly int OriginY = originY;
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly int BaseIndex = baseIndex;
        public readonly ushort[] AreaTags = areaTags;
        public readonly bool UniformArea = uniformArea;
        public bool HasPortals;

        public readonly int IndexOf(int x, int y) => BaseIndex + ((y - OriginY) * Width) + (x - OriginX);

        public readonly bool Contains(int x, int y) => (uint)(x - OriginX) < (uint)Width && (uint)(y - OriginY) < (uint)Height;
    }

    internal readonly record struct PortalLink(int PortalSlot, int Other);
}
