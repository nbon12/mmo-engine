using CsCheck;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Random;

/// <summary>
/// The simulator's own PRNG (FR-016, research D8). Known answers come from a reference written here
/// straight from the published definitions of SplitMix64, xoshiro256** and FNV-1a 64, checked
/// against their published test vectors; the generator under test must agree with it.
/// </summary>
public sealed class PrngTests
{
    [Fact]
    public void ReferenceSplitMix64MatchesItsPublishedVectorsForSeedZero()
    {
        var state = 0UL;
        Assert.Equal(0xE220A8397B1DCDAFUL, Reference.SplitMix64(ref state));
        Assert.Equal(0x6E789E6AA1B965F4UL, Reference.SplitMix64(ref state));
        Assert.Equal(0x06C45D188009454FUL, Reference.SplitMix64(ref state));
        Assert.Equal(0xF88BB8A8724C81ECUL, Reference.SplitMix64(ref state));
    }

    [Theory]
    [InlineData("", 0xCBF29CE484222325UL)]
    [InlineData("a", 0xAF63DC4C8601EC8CUL)]
    [InlineData("foobar", 0x85944171F73967E8UL)]
    public void Fnv1a64MatchesPublishedVectors(string text, ulong expected)
    {
        Assert.Equal(expected, DeterministicRandom.Fnv1a64(text));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(20261103UL)]
    [InlineData(ulong.MaxValue)]
    public void Xoshiro256StarStarSeededBySplitMix64MatchesTheReference(ulong seed)
    {
        var random = new DeterministicRandom(seed);
        var reference = new Reference(seed);
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(reference.Next(), random.NextUInt64());
        }
    }

    [Fact]
    public void FirstXoshiroOutputForSeedZeroIsTheRotatedScrambleOfTheSplitMixState()
    {
        // xoshiro256**: result = rotl(s1 * 5, 7) * 9, with s1 the second SplitMix64(0) output.
        var s1 = unchecked(0x6E789E6AA1B965F4UL * 5);
        var expected = unchecked(((s1 << 7) | (s1 >> 57)) * 9);
        Assert.Equal(expected, new DeterministicRandom(0).NextUInt64());
    }

    [Theory]
    [InlineData(1UL, "movement")]
    [InlineData(1UL, "scheduling")]
    [InlineData(42UL, "timers")]
    public void StreamSeedIsSplitMix64OfRunSeedXorFnvOfName(ulong runSeed, string stream)
    {
        var state = runSeed ^ Reference.Fnv1a64(stream);
        Assert.Equal(Reference.SplitMix64(ref state), DeterministicRandom.DeriveSeed(runSeed, stream));

        var expected = new Reference(DeterministicRandom.DeriveSeed(runSeed, stream));
        var actual = DeterministicRandom.ForStream(runSeed, stream);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(expected.Next(), actual.NextUInt64());
        }
    }

    [Theory]
    [InlineData(20261103UL, 0)]
    [InlineData(20261103UL, 9)]
    [InlineData(7UL, 3)]
    public void SweepSeedIsSplitMix64OfSweepSeedPlusIndexTimesGolden(ulong sweepSeed, int index)
    {
        var state = sweepSeed + ((ulong)index * 0x9E3779B97F4A7C15UL);
        Assert.Equal(Reference.SplitMix64(ref state), DeterministicRandom.SweepSeed(sweepSeed, index));
    }

    [Fact]
    public void DrawsInOneStreamDoNotShiftAnother()
    {
        const ulong Seed = 99;
        var timersAlone = DeterministicRandom.ForStream(Seed, "timers");
        var expected = Enumerable.Range(0, 200).Select(_ => timersAlone.NextUInt64()).ToArray();

        var movement = DeterministicRandom.ForStream(Seed, "movement");
        var timers = DeterministicRandom.ForStream(Seed, "timers");
        var actual = new ulong[200];
        for (var i = 0; i < actual.Length; i++)
        {
            for (var k = 0; k < i % 7; k++)
            {
                movement.NextUInt64();
            }

            actual[i] = timers.NextUInt64();
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EachEntityHasItsOwnMovementStream()
    {
        const ulong Seed = 5;
        var entity2Alone = DeterministicRandom.ForEntity(Seed, "movement", 2);
        var expected = Enumerable.Range(0, 50).Select(_ => entity2Alone.NextUInt64()).ToArray();

        var entity1 = DeterministicRandom.ForEntity(Seed, "movement", 1);
        var entity2 = DeterministicRandom.ForEntity(Seed, "movement", 2);
        var actual = new ulong[50];
        for (var i = 0; i < actual.Length; i++)
        {
            entity1.NextUInt64();
            entity1.NextUInt64();
            actual[i] = entity2.NextUInt64();
        }

        Assert.Equal(expected, actual);
        Assert.NotEqual(DeterministicRandom.ForEntity(Seed, "movement", 1).NextUInt64(), DeterministicRandom.ForEntity(Seed, "movement", 2).NextUInt64());
    }

    [Theory]
    [InlineData("movement", "scheduling")]
    [InlineData("scheduling", "timers")]
    [InlineData("timers", "movement")]
    public void DifferentStreamNamesGiveDifferentSequences(string a, string b)
    {
        var ra = DeterministicRandom.ForStream(1, a);
        var rb = DeterministicRandom.ForStream(1, b);
        var same = Enumerable.Range(0, 16).Count(_ => ra.NextUInt64() == rb.NextUInt64());
        Assert.Equal(0, same);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(-5, 5)]
    [InlineData(30, 900)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void NextIntStaysWithinInclusiveBounds(int min, int max)
    {
        var random = new DeterministicRandom(3);
        for (var i = 0; i < 2000; i++)
        {
            var value = random.NextInt(min, max);
            Assert.InRange(value, min, max);
        }
    }

    [Fact]
    public void NextIntReachesEveryValueOfASmallRange()
    {
        var random = new DeterministicRandom(11);
        var seen = new HashSet<int>();
        for (var i = 0; i < 1000; i++)
        {
            seen.Add(random.NextInt(2, 7));
        }

        Assert.Equal([2, 3, 4, 5, 6, 7], seen.Order());
    }

    [Fact]
    public void NextIntRejectsMaxBelowMin()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeterministicRandom(1).NextInt(5, 4));
    }

    [Fact]
    public void NextDoubleIsTheTop53BitsScaledIntoTheUnitInterval()
    {
        var random = new DeterministicRandom(8);
        var reference = new Reference(8);
        for (var i = 0; i < 1000; i++)
        {
            var expected = (reference.Next() >> 11) * (1.0 / 9007199254740992.0);
            var value = random.NextDouble();
            Assert.Equal(expected, value);
            Assert.InRange(value, 0.0, 0.9999999999999999);
        }
    }

    [Fact]
    public void UnitDiscPointsLieInTheDiscAndUnitVectorsHaveLengthOne()
    {
        var random = new DeterministicRandom(13);
        for (var i = 0; i < 2000; i++)
        {
            var (x, y) = random.UnitDisc();
            Assert.True((x * x) + (y * y) <= 1, $"({x}, {y}) is outside the unit disc");
            var (ux, uy) = random.UnitVector();
            Assert.InRange(Math.Sqrt((ux * ux) + (uy * uy)), 1 - 1e-12, 1 + 1e-12);
        }
    }

    [Fact]
    public void ShuffleIsADeterministicPermutation()
    {
        var a = Enumerable.Range(0, 50).ToArray();
        var b = Enumerable.Range(0, 50).ToArray();
        new DeterministicRandom(17).Shuffle(a.AsSpan());
        new DeterministicRandom(17).Shuffle(b.AsSpan());
        Assert.Equal(a, b);
        Assert.Equal(Enumerable.Range(0, 50), a.Order());
        Assert.NotEqual(Enumerable.Range(0, 50), a);
    }

    [Fact]
    public void AnySeedGivesTheReferenceSequenceAndStaysInBounds()
    {
        Gen.Select(Gen.ULong, Gen.Int[-1000, 1000], Gen.Int[0, 1000]).Sample(
            t =>
            {
                var (seed, min, span) = t;
                var a = new DeterministicRandom(seed);
                var reference = new Reference(seed);
                for (var i = 0; i < 8; i++)
                {
                    Assert.Equal(reference.Next(), a.NextUInt64());
                }

                var value = new DeterministicRandom(seed).NextInt(min, min + span);
                Assert.InRange(value, min, min + span);
            },
            seed: TestRuns.PropertySeed,
            iter: 500,
            threads: 1);
    }

    /// <summary>The algorithms as published, written independently of the generator under test.</summary>
    private sealed class Reference
    {
        private readonly ulong[] _s = new ulong[4];

        public Reference(ulong seed)
        {
            var state = seed;
            for (var i = 0; i < 4; i++)
            {
                _s[i] = SplitMix64(ref state);
            }
        }

        public static ulong SplitMix64(ref ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public static ulong Fnv1a64(string text)
        {
            var hash = 14695981039346656037UL;
            foreach (var b in text)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        public ulong Next()
        {
            var result = Rotl(_s[1] * 5, 7) * 9;
            var t = _s[1] << 17;
            _s[2] ^= _s[0];
            _s[3] ^= _s[1];
            _s[1] ^= _s[2];
            _s[0] ^= _s[3];
            _s[2] ^= t;
            _s[3] = Rotl(_s[3], 45);
            return result;
        }

        private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));
    }
}
