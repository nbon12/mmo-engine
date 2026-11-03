using System.Buffers.Binary;
using System.IO.Hashing;

namespace MmoEngine.Regionizer.Simulator.Metrics;

/// <summary>
/// The cross-region pairs metric (FR-024, research D10): pairs of players in different regions within
/// the read-only range, found through a uniform grid. Each pair is counted once: as deferred when it
/// lies within the deferred range, otherwise as read-only. Distances are horizontal.
/// </summary>
public static class CrossRegionPairs
{
    /// <summary>The grid cell side, at least the read-only range so only neighbouring cells can hold a pair.</summary>
    public const double GridCellMetres = 1000;

    /// <summary>Counts pairs among <paramref name="players"/>; <paramref name="ownerOf"/> gives each entity's region (0 in transit, skipped).</summary>
    public static (long Deferred, long ReadOnly) Count(
        IReadOnlyList<int> players,
        ReadOnlySpan<double> x,
        ReadOnlySpan<double> y,
        Func<int, long> ownerOf,
        double deferredRange,
        double readOnlyRange)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(ownerOf);
        var cell = Math.Max(GridCellMetres, readOnlyRange);
        var cells = new Dictionary<(long X, long Y), List<int>>();
        foreach (var id in players)
        {
            if (ownerOf(id) == 0)
            {
                continue;
            }

            var key = ((long)Math.Floor(x[id] / cell), (long)Math.Floor(y[id] / cell));
            if (!cells.TryGetValue(key, out var list))
            {
                list = [];
                cells.Add(key, list);
            }

            list.Add(id);
        }

        var deferred2 = deferredRange * deferredRange;
        var readOnly2 = readOnlyRange * readOnlyRange;
        long deferred = 0;
        long readOnly = 0;
        foreach (var (key, members) in cells)
        {
            // Same cell (each pair once), then the four neighbours "after" it, so every neighbouring pair of cells is visited once.
            CountWithin(members, x, y, ownerOf, deferred2, readOnly2, ref deferred, ref readOnly);
            foreach (var (dx, dy) in Forward)
            {
                if (cells.TryGetValue((key.X + dx, key.Y + dy), out var other))
                {
                    CountBetween(members, other, x, y, ownerOf, deferred2, readOnly2, ref deferred, ref readOnly);
                }
            }
        }

        return (deferred, readOnly);
    }

    private static readonly (int Dx, int Dy)[] Forward = [(1, -1), (1, 0), (1, 1), (0, 1)];

    private static void CountWithin(List<int> a, ReadOnlySpan<double> x, ReadOnlySpan<double> y, Func<int, long> ownerOf, double deferred2, double readOnly2, ref long deferred, ref long readOnly)
    {
        for (var i = 0; i < a.Count; i++)
        {
            var oi = ownerOf(a[i]);
            for (var j = i + 1; j < a.Count; j++)
            {
                Tally(a[i], a[j], oi, ownerOf(a[j]), x, y, deferred2, readOnly2, ref deferred, ref readOnly);
            }
        }
    }

    private static void CountBetween(List<int> a, List<int> b, ReadOnlySpan<double> x, ReadOnlySpan<double> y, Func<int, long> ownerOf, double deferred2, double readOnly2, ref long deferred, ref long readOnly)
    {
        foreach (var i in a)
        {
            var oi = ownerOf(i);
            foreach (var j in b)
            {
                Tally(i, j, oi, ownerOf(j), x, y, deferred2, readOnly2, ref deferred, ref readOnly);
            }
        }
    }

    private static void Tally(int i, int j, long oi, long oj, ReadOnlySpan<double> x, ReadOnlySpan<double> y, double deferred2, double readOnly2, ref long deferred, ref long readOnly)
    {
        if (oi == oj)
        {
            return;
        }

        var dx = x[i] - x[j];
        var dy = y[i] - y[j];
        var d2 = (dx * dx) + (dy * dy);
        if (d2 <= deferred2)
        {
            deferred++;
        }
        else if (d2 <= readOnly2)
        {
            readOnly++;
        }
    }
}

/// <summary>
/// Counts a region's islands the way the core's split does (research D4): its live sections, joined
/// when one entity claims both and, with a merge gap of 1, when they are same-area eight-neighbours or
/// joined by an open portal. Used to sample split latency (research D10).
/// </summary>
public sealed class IslandDetector
{
    private static readonly (int Dx, int Dy)[] Neighbours =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1),
    ];

    private readonly SectionTopology _topology;
    private readonly long[] _stamp;
    private readonly int[] _local;
    private readonly Dictionary<int, List<(int PortalId, int Other)>> _links = [];
    private long _current;
    private int[] _parent = new int[256];

    public IslandDetector(SectionTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        _topology = topology;
        _stamp = new long[topology.SectionCount];
        _local = new int[topology.SectionCount];
        for (var slot = 0; slot < topology.PortalCount; slot++)
        {
            var portal = topology.PortalAt(slot);
            if (topology.TryIndexOf(portal.A, out var a) && topology.TryIndexOf(portal.B, out var b))
            {
                Link(a, portal.PortalId, b);
                Link(b, portal.PortalId, a);
            }
        }
    }

    /// <summary>The number of islands of a live region (0 when it has no live section).</summary>
    public int CountIslands(Regionizer regionizer, long regionId)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        var region = regionizer.GetRegion(regionId);
        var sections = region.Sections;
        _current++;
        if (_parent.Length < sections.Length)
        {
            _parent = new int[Math.Max(sections.Length, _parent.Length * 2)];
        }

        var n = 0;
        foreach (var section in sections)
        {
            if (regionizer.ClaimCountOf(section) > 0)
            {
                _stamp[section] = _current;
                _local[section] = n;
                _parent[n] = n;
                n++;
            }
        }

        foreach (var entity in region.Entities)
        {
            // At a tick boundary every claimed section is a live section of the entity's region;
            // the stamp check keeps a corrupted state from reading another region's indices.
            var first = -1;
            foreach (var section in regionizer.GetEntity(entity).ClaimedSections)
            {
                if (_stamp[section] == _current)
                {
                    first = Join(first, _local[section]);
                }
            }
        }

        if (regionizer.Options.MergeGap == 1)
        {
            foreach (var section in sections)
            {
                if (_stamp[section] == _current)
                {
                    UnionNeighbours(regionizer, section);
                }
            }
        }

        var islands = 0;
        for (var i = 0; i < n; i++)
        {
            if (Find(i) == i)
            {
                islands++;
            }
        }

        return islands;
    }

    private void UnionNeighbours(Regionizer regionizer, int section)
    {
        var key = _topology.KeyOf(section);
        var area = _topology.AreaOf(section);
        foreach (var (dx, dy) in Neighbours)
        {
            if (_topology.TryIndexOf(new SectionKey(key.Layer, key.X + dx, key.Y + dy), out var other)
                && _stamp[other] == _current
                && _topology.AreaOf(other) == area)
            {
                Union(_local[section], _local[other]);
            }
        }

        if (_links.TryGetValue(section, out var links))
        {
            foreach (var (portalId, other) in links)
            {
                if (_stamp[other] == _current && regionizer.IsPortalOpen(portalId))
                {
                    Union(_local[section], _local[other]);
                }
            }
        }
    }

    private void Link(int section, int portalId, int other)
    {
        if (!_links.TryGetValue(section, out var list))
        {
            list = [];
            _links.Add(section, list);
        }

        list.Add((portalId, other));
    }

    /// <summary>Joins <paramref name="local"/> to the first section seen, and returns the first.</summary>
    private int Join(int first, int local)
    {
        if (first >= 0)
        {
            Union(first, local);
        }

        return first >= 0 ? first : local;
    }

    private int Find(int i)
    {
        while (_parent[i] != i)
        {
            _parent[i] = _parent[_parent[i]];
            i = _parent[i];
        }

        return i;
    }

    private void Union(int a, int b)
    {
        var ra = Find(a);
        var rb = Find(b);
        if (ra != rb)
        {
            _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }
}

/// <summary>
/// The per-tick state hash (FR-019, research D8): XxHash64 over, first, for each live region in
/// ascending ID, its ID, state, tick counter, sorted section indices, ascending entity IDs and timers
/// sorted by ID; then, for each entity in ascending ID, its ID and section index (−1 in transit). It
/// is prefixed by the previous tick's hash so the sequence is chained. All values little-endian.
/// </summary>
public sealed class StateHasher
{
    private byte[] _buffer = new byte[256];
    private int[] _sections = new int[16];
    private TimerView[] _timers = new TimerView[4];
    private int _length;

    /// <summary>The hash after the latest <see cref="Next"/>; 0 before the first.</summary>
    public ulong Current { get; private set; }

    /// <summary>Hashes the regionizer's state chained onto <see cref="Current"/> and returns the new value.</summary>
    public ulong Next(Regionizer regionizer)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        _length = 0;
        WriteUInt64(Current);
        foreach (var id in regionizer.RegionIds)
        {
            var region = regionizer.GetRegion(id);
            WriteInt64(id);
            WriteInt32((int)region.State);
            WriteInt64(region.TickCounter);

            var sections = region.Sections;
            if (_sections.Length < sections.Length)
            {
                _sections = new int[Math.Max(sections.Length, _sections.Length * 2)];
            }

            sections.CopyTo(_sections);
            var sorted = _sections.AsSpan(0, sections.Length);
            sorted.Sort();
            WriteInt32(sorted.Length);
            foreach (var s in sorted)
            {
                WriteInt32(s);
            }

            var entities = region.Entities;
            WriteInt32(entities.Length);
            foreach (var e in entities)
            {
                WriteInt32(e);
            }

            var timers = region.Timers;
            if (_timers.Length < timers.Length)
            {
                _timers = new TimerView[Math.Max(timers.Length, _timers.Length * 2)];
            }

            timers.CopyTo(_timers);
            var byId = _timers.AsSpan(0, timers.Length);
            byId.Sort(static (a, b) => a.TimerId.CompareTo(b.TimerId));
            WriteInt32(byId.Length);
            foreach (var t in byId)
            {
                WriteInt32(t.TimerId);
                WriteInt32(t.EntityId);
                WriteInt64(t.Deadline);
                WriteInt64(t.Delay);
                WriteInt64(t.SetAt);
            }
        }

        // Second part: each entity's section in ascending ID, so positions that diverge across a
        // section edge change the hash even when no region changes.
        var topology = regionizer.Topology;
        var ids = regionizer.EntityIds;
        WriteInt32(ids.Length);
        foreach (var id in ids)
        {
            WriteInt32(id);
            WriteInt32(SectionOf(regionizer.GetEntity(id), topology));
        }

        Current = XxHash64.HashToUInt64(_buffer.AsSpan(0, _length));
        return Current;
    }

    /// <summary>The entity's section index, or −1 while it is in transit (two-step teleport).</summary>
    internal static int SectionOf(EntityView entity, SectionTopology topology) =>
        !entity.InTransit && topology.TrySectionAt(entity.Layer, entity.X, entity.Y, out var index) ? index : -1;

    private void Ensure(int bytes)
    {
        if (_length + bytes > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_length + bytes, _buffer.Length * 2));
        }
    }

    private void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    private void WriteInt64(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    private void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }
}
