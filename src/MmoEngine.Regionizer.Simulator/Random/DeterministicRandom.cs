namespace MmoEngine.Regionizer.Simulator.Random;

/// <summary>
/// Our own PRNG (research D8): xoshiro256** seeded through SplitMix64. Named streams are derived from
/// the run seed and an FNV-1a-64 hash of the stream name, so a runtime upgrade cannot change a
/// sequence and a draw in one stream cannot shift another. Uses no platform-dependent math.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>A generator seeded directly from a 64-bit seed.</summary>
    public DeterministicRandom(ulong seed)
    {
        var sm = seed;
        _s0 = SplitMix64(ref sm);
        _s1 = SplitMix64(ref sm);
        _s2 = SplitMix64(ref sm);
        _s3 = SplitMix64(ref sm);
        if ((_s0 | _s1 | _s2 | _s3) == 0)
        {
            _s0 = 1;
        }
    }

    /// <summary>The generator for a named stream of a run.</summary>
    public static DeterministicRandom ForStream(ulong runSeed, string streamName) =>
        new(DeriveSeed(runSeed, streamName));

    /// <summary>The generator for a named stream of one entity, independent of every other entity's.</summary>
    public static DeterministicRandom ForEntity(ulong runSeed, string streamName, int entityId)
    {
        var sm = DeriveSeed(runSeed, streamName) ^ ((ulong)(uint)entityId * 0x9E3779B97F4A7C15UL);
        return new DeterministicRandom(SplitMix64(ref sm));
    }

    /// <summary>SplitMix64(seed XOR FNV-1a-64(name)).</summary>
    public static ulong DeriveSeed(ulong runSeed, string streamName)
    {
        ArgumentNullException.ThrowIfNull(streamName);
        var sm = runSeed ^ Fnv1a64(streamName);
        return SplitMix64(ref sm);
    }

    /// <summary>The <paramref name="index"/>-th seed of a sweep: SplitMix64 of (sweep seed, index).</summary>
    public static ulong SweepSeed(ulong sweepSeed, int index)
    {
        var sm = sweepSeed + ((ulong)(uint)index * 0x9E3779B97F4A7C15UL);
        return SplitMix64(ref sm);
    }

    /// <summary>FNV-1a 64-bit over the UTF-16 code units of <paramref name="text"/>.</summary>
    public static ulong Fnv1a64(string text)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 0x100000001B3UL;
        }

        return hash;
    }

    /// <summary>The next 64 random bits (xoshiro256**).</summary>
    public ulong NextUInt64()
    {
        var result = RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>A double in [0, 1) with 53 random bits.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A double in [min, max).</summary>
    public double NextDouble(double min, double max) => min + ((max - min) * NextDouble());

    /// <summary>An integer in [min, max] inclusive, without modulo bias.</summary>
    public int NextInt(int min, int max)
    {
        if (max < min)
        {
            throw new ArgumentOutOfRangeException(nameof(max), "max must be at least min.");
        }

        var range = (ulong)((long)max - min) + 1;
        var limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong draw;
        do
        {
            draw = NextUInt64();
        }
        while (draw >= limit);

        return (int)((long)min + (long)(draw % range));
    }

    /// <summary>True with probability <paramref name="p"/>.</summary>
    public bool Chance(double p) => NextDouble() < p;

    /// <summary>A uniform point in the unit disc, by rejection sampling (no trigonometry).</summary>
    public (double X, double Y) UnitDisc()
    {
        while (true)
        {
            var x = NextDouble(-1, 1);
            var y = NextDouble(-1, 1);
            if ((x * x) + (y * y) <= 1)
            {
                return (x, y);
            }
        }
    }

    /// <summary>A uniform unit vector, by rejection sampling and a square root (no trigonometry).</summary>
    public (double X, double Y) UnitVector()
    {
        while (true)
        {
            var (x, y) = UnitDisc();
            var len2 = (x * x) + (y * y);
            if (len2 > 1e-12)
            {
                var len = Math.Sqrt(len2);
                return (x / len, y / len);
            }
        }
    }

    /// <summary>Shuffles <paramref name="items"/> in place (Fisher–Yates).</summary>
    public void Shuffle<T>(Span<T> items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = NextInt(0, i);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static ulong SplitMix64(ref ulong state)
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));
}
