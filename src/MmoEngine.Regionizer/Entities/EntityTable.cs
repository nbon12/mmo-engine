namespace MmoEngine.Regionizer;

/// <summary>
/// Entities as a structure of arrays indexed by entity id. Each entity's claim lives in its own
/// buffer, sized when the entity is added, so steady-state moves allocate nothing.
/// </summary>
internal sealed class EntityTable
{
    private bool[] _exists = new bool[64];

    public EntityClass[] Class = new EntityClass[64];
    public int[] Layer = new int[64];
    public double[] X = new double[64];
    public double[] Y = new double[64];
    public double[] Z = new double[64];
    public int[] FightTag = new int[64];
    public long[] Region = new long[64];
    public bool[] InTransit = new bool[64];
    public bool[] Occupied = new bool[64];

    /// <summary>The section the entity stands in, or -1 (heavy projectile off the map, or in transit).</summary>
    public int[] Anchor = new int[64];

    public int[][] Claims = new int[64][];
    public int[] ClaimCount = new int[64];

    /// <summary>Transit destination and the timers carried through a two-step teleport.</summary>
    public (int Layer, double X, double Y, double Z)[] TransitTarget = new (int, double, double, double)[64];
    public List<(TimerView Timer, long Remaining)>?[] TransitTimers = new List<(TimerView, long)>?[64];

    /// <summary>Live entity ids, ascending.</summary>
    public List<int> Ids { get; } = [];

    public bool Exists(int id) => id >= 0 && id < _exists.Length && _exists[id];

    public void Add(int id, int claimCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        if (Exists(id))
        {
            throw new ArgumentException($"Entity {id} already exists.");
        }

        EnsureCapacity(id + 1);
        _exists[id] = true;
        Claims[id] = new int[Math.Max(1, claimCapacity)];
        ClaimCount[id] = 0;
        Anchor[id] = -1;
        Region[id] = 0;
        InTransit[id] = false;
        Occupied[id] = false;
        TransitTimers[id] = null;
        var at = Ids.BinarySearch(id);
        Ids.Insert(~at, id);
    }

    public void Remove(int id)
    {
        _exists[id] = false;
        Claims[id] = [];
        ClaimCount[id] = 0;
        TransitTimers[id] = null;
        var at = Ids.BinarySearch(id);
        if (at >= 0)
        {
            Ids.RemoveAt(at);
        }
    }

    private void EnsureCapacity(int size)
    {
        if (size <= _exists.Length)
        {
            return;
        }

        var n = _exists.Length;
        while (n < size)
        {
            n *= 2;
        }

        Array.Resize(ref _exists, n);
        Array.Resize(ref Class, n);
        Array.Resize(ref Layer, n);
        Array.Resize(ref X, n);
        Array.Resize(ref Y, n);
        Array.Resize(ref Z, n);
        Array.Resize(ref FightTag, n);
        Array.Resize(ref Region, n);
        Array.Resize(ref InTransit, n);
        Array.Resize(ref Occupied, n);
        Array.Resize(ref Anchor, n);
        Array.Resize(ref Claims, n);
        Array.Resize(ref ClaimCount, n);
        Array.Resize(ref TransitTarget, n);
        Array.Resize(ref TransitTimers, n);
    }
}
