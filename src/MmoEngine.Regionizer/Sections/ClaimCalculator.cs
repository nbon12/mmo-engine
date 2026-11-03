namespace MmoEngine.Regionizer;

/// <summary>
/// Computes an entity's claim: the sections within its radius of its anchor (research D1), reached
/// from its own section through same-area eight-neighbour steps and open portals (research D2).
/// Results are sorted ascending. Uses only +, −, ×, ÷ and floor, so it is deterministic.
/// </summary>
internal sealed class ClaimCalculator
{
    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    private readonly SectionTopology _topology;
    private readonly bool[] _portalOpen;
    private readonly int _sectionSize;
    private readonly int[] _visitStamp;
    private int _stamp;
    private int[] _queue = new int[64];

    public ClaimCalculator(SectionTopology topology, bool[] portalOpen)
    {
        _topology = topology;
        _portalOpen = portalOpen;
        _sectionSize = topology.SectionSize;
        _visitStamp = new int[topology.SectionCount];
    }

    /// <summary>An upper bound on the claim size of one layer for a radius, used to size buffers.</summary>
    public int Capacity(double radius)
    {
        var side = (2 * ((int)Math.Min(4096, Math.Floor(radius / _sectionSize)) + 3)) + 1;
        return side * side;
    }

    /// <summary>
    /// Writes the claim of an entity at (<paramref name="x"/>, <paramref name="y"/>) in section
    /// <paramref name="ownSection"/> into <paramref name="into"/>, growing it if needed, and returns the count.
    /// </summary>
    public int Compute(int ownSection, double x, double y, double radius, ClaimAnchoring anchoring, ref int[] into)
    {
        var anchor = new Anchor(_topology.KeyOf(ownSection), x, y, radius, anchoring, _sectionSize);
        var layerSlot = _topology.LayerSlotOf(ownSection);
        ref readonly var layer = ref _topology.LayerAt(layerSlot);

        anchor.Range(out var x0, out var x1, out var y0, out var y1);
        x0 = Math.Max(x0, layer.OriginX);
        y0 = Math.Max(y0, layer.OriginY);
        x1 = Math.Min(x1, layer.OriginX + layer.Width - 1);
        y1 = Math.Min(y1, layer.OriginY + layer.Height - 1);

        if (layer.UniformArea && !PortalInBox(layer, x0, x1, y0, y1))
        {
            var count = 0;
            for (var sy = y0; sy <= y1; sy++)
            {
                for (var sx = x0; sx <= x1; sx++)
                {
                    if (anchor.Within(sx, sy))
                    {
                        Append(ref into, ref count, layer.IndexOf(sx, sy));
                    }
                }
            }

            return count;
        }

        return Walk(ownSection, anchor, ref into);
    }

    /// <summary>
    /// Whether the claim's bounding box contains <paramref name="section"/>'s grid position on any
    /// layer. A superset test, used to find claims a portal toggle may change.
    /// </summary>
    public bool BoxContains(int ownSection, double x, double y, double radius, ClaimAnchoring anchoring, int section)
    {
        var own = _topology.KeyOf(ownSection);
        var key = _topology.KeyOf(section);
        var anchor = new Anchor(own, x, y, radius, anchoring, _sectionSize);
        anchor.Range(out var x0, out var x1, out var y0, out var y1);
        return key.X >= x0 && key.X <= x1 && key.Y >= y0 && key.Y <= y1;
    }

    private int Walk(int ownSection, in Anchor anchor, ref int[] into)
    {
        NextStamp();
        var head = 0;
        var tail = 0;
        var count = 0;
        Enqueue(ownSection, ref tail);
        _visitStamp[ownSection] = _stamp;

        while (head < tail)
        {
            var current = _queue[head++];
            Append(ref into, ref count, current);
            var layerSlot = _topology.LayerSlotOf(current);
            ref readonly var layer = ref _topology.LayerAt(layerSlot);
            var key = _topology.KeyOf(current);
            var area = _topology.AreaOf(current);

            foreach (var (dx, dy) in Neighbours)
            {
                var nx = key.X + dx;
                var ny = key.Y + dy;
                if (!layer.Contains(nx, ny))
                {
                    continue;
                }

                var n = layer.IndexOf(nx, ny);
                if (_visitStamp[n] == _stamp || _topology.AreaOf(n) != area || !anchor.Within(nx, ny))
                {
                    continue;
                }

                _visitStamp[n] = _stamp;
                Enqueue(n, ref tail);
            }

            foreach (var link in _topology.LinksOf(current))
            {
                if (!_portalOpen[link.PortalSlot] || _visitStamp[link.Other] == _stamp)
                {
                    continue;
                }

                var other = _topology.KeyOf(link.Other);
                if (!anchor.Within(other.X, other.Y))
                {
                    continue;
                }

                _visitStamp[link.Other] = _stamp;
                Enqueue(link.Other, ref tail);
            }
        }

        into.AsSpan(0, count).Sort();
        return count;
    }

    private bool PortalInBox(in SectionTopology.Layer layer, int x0, int x1, int y0, int y1)
    {
        if (!layer.HasPortals)
        {
            return false;
        }

        foreach (var endpoint in _topology.PortalEndpoints)
        {
            var key = _topology.KeyOf(endpoint);
            if (key.Layer == layer.Id && key.X >= x0 && key.X <= x1 && key.Y >= y0 && key.Y <= y1)
            {
                return true;
            }
        }

        return false;
    }

    private void NextStamp()
    {
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visitStamp);
            _stamp = 1;
        }
    }

    private void Enqueue(int section, ref int tail)
    {
        if (tail == _queue.Length)
        {
            Array.Resize(ref _queue, _queue.Length * 2);
        }

        _queue[tail++] = section;
    }

    private static void Append(ref int[] into, ref int count, int section)
    {
        if (count == into.Length)
        {
            Array.Resize(ref into, Math.Max(8, into.Length * 2));
        }

        into[count++] = section;
    }

    /// <summary>The claim geometry of research D1, in absolute section coordinates.</summary>
    internal readonly struct Anchor
    {
        private readonly ClaimAnchoring _mode;
        private readonly int _ax;
        private readonly int _ay;
        private readonly double _px;
        private readonly double _py;
        private readonly double _r;
        private readonly double _r2;
        private readonly int _s;

        public Anchor(SectionKey own, double x, double y, double radius, ClaimAnchoring mode, int sectionSize)
        {
            _mode = mode;
            _ax = own.X;
            _ay = own.Y;
            _px = x;
            _py = y;
            _r = radius;
            _r2 = radius * radius;
            _s = sectionSize;
        }

        public void Range(out int x0, out int x1, out int y0, out int y1)
        {
            if (_mode == ClaimAnchoring.Section)
            {
                var k = (int)Math.Floor(_r / _s) + 1;
                x0 = _ax - k;
                x1 = _ax + k;
                y0 = _ay - k;
                y1 = _ay + k;
            }
            else
            {
                x0 = (int)Math.Floor((_px - _r) / _s) - 1;
                x1 = (int)Math.Floor((_px + _r) / _s) + 1;
                y0 = (int)Math.Floor((_py - _r) / _s) - 1;
                y1 = (int)Math.Floor((_py + _r) / _s) + 1;
            }
        }

        public bool Within(int sx, int sy)
        {
            double dx;
            double dy;
            if (_mode == ClaimAnchoring.Section)
            {
                dx = Math.Max(0, Math.Abs(sx - _ax) - 1) * (double)_s;
                dy = Math.Max(0, Math.Abs(sy - _ay) - 1) * (double)_s;
            }
            else
            {
                var left = sx * (double)_s;
                var bottom = sy * (double)_s;
                dx = Math.Max(Math.Max(left - _px, 0), _px - (left + _s));
                dy = Math.Max(Math.Max(bottom - _py, 0), _py - (bottom + _s));
            }

            return (dx * dx) + (dy * dy) <= _r2;
        }
    }
}
