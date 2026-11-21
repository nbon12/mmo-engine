using System.Globalization;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Math;

/// <summary>
/// The exhaustive sweeps of research.md R3: each <see cref="SimMath"/> trigonometric function against its
/// <see cref="SimMathBounds"/> bound over its whole domain, measured by <see cref="MathReference.UlpError"/>
/// against <see cref="MathReference.Reference"/>. They are not a gate and own no scenario: the fixed table
/// of <see cref="FixedTableBoundsTests"/> gates every pull request (SC-004). They take minutes, so they run
/// only when <c>MMO_EXHAUSTIVE_MATH=1</c> is set, which only the measure job does, and skip otherwise; this
/// is the feature's one deliberate skip. Each prints its maxima as <c>SIMMATH</c> lines, which the measure
/// job copies to its job summary. <c>System.Math</c> is written in full, because
/// <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
/// <remarks>
/// The sweeps run with <see cref="Parallel.For(int, int, Action{int})"/> over fixed partitions, each with
/// its own maxima, merged in partition order, so the reported worst input is the same on every run (tests
/// are not analyzed). Every non-finite or out-of-domain input is checked for NaN and counted explicitly,
/// and every domain's input count is asserted, so neither a NaN that compares as "within bound" nor a
/// skipped range can pass.
/// </remarks>
public sealed class ExhaustiveSweepTests
{
    /// <summary>
    /// The SplitMix64 seed of the 67,108,864 random Atan2 pairs, printed on failure. Pair p is
    /// <c>SplitMix64.At(seed, p · 64 + attempt)</c>, a from its upper 32 bits and b from its lower 32, for
    /// the first attempt whose halves are both finite.
    /// </summary>
    private const ulong Atan2Seed = 0x5EED_5EED_0000_0008UL;

    /// <summary>The Atan2 directions k·2π/2^22 swept at each radius.</summary>
    private const int Atan2Directions = 1 << 22;

    /// <summary>The random finite Atan2 pairs: 67,108,864.</summary>
    private const long Atan2RandomPairs = 1L << 26;

    /// <summary>The attempts per random Atan2 pair before the last candidate is made finite (none reaches it in practice).</summary>
    private const int Atan2Attempts = 64;

    /// <summary>The fixed partitions every sweep is split into.</summary>
    private const int Partitions = 4_096;

    /// <summary>Every 32-bit pattern.</summary>
    private const long AllPatterns = 1L << 32;

    /// <summary>Every finite float: both signs of the patterns 0 to <c>float.MaxValue</c>'s bits.</summary>
    private const long AllFinite = 2L * (0x7F7FFFFFL + 1L);

    /// <summary>The NaN patterns: both signs of an all-ones exponent with a nonzero fraction.</summary>
    private const long AllNaN = 2L * ((1L << 23) - 1L);

    private const int SinId = 1;
    private const int CosId = 2;
    private const int TanId = 3;
    private const int SinCosId = 4;
    private const int AsinId = 5;
    private const int AcosId = 6;
    private const int AtanId = 7;
    private const int Atan2Id = 8;
    private const int SinCosBinaryAngleId = 9;

    /// <summary>The radii of the Atan2 direction sweep: 2^-100, 2^-20, 1, 2^20 and 2^100.</summary>
    private static readonly int[] Atan2RadiusExponents = [-100, -20, 0, 20, 100];

    /// <summary>The sweeps run only when <c>MMO_EXHAUSTIVE_MATH=1</c>.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("MMO_EXHAUSTIVE_MATH") == "1";

    /// <summary>
    /// Sweeps <paramref name="function"/> over its whole domain (research R3): Sin, Cos, SinCos and Tan over
    /// every float, against the small-argument bound for |x| ≤ 12867 and the large-argument bound for every
    /// finite float above; Asin and Acos over every float in [−1, 1]; Atan over every float, ±∞ included;
    /// Atan2 over 2^22 directions at five radii and 67,108,864 random finite pairs; SinCosBinaryAngle over
    /// all 65,536 angles. It writes one <c>SIMMATH</c> line per function and bound, then asserts that each
    /// maximum is within its bound, that each domain was swept in full, and that every NaN or out-of-domain
    /// input gives NaN.
    /// </summary>
    [Theory(Skip = "Set MMO_EXHAUSTIVE_MATH=1 to run the exhaustive sweeps (research.md R3)", SkipUnless = nameof(Enabled))]
    [InlineData("Sin")]
    [InlineData("Cos")]
    [InlineData("SinCos")]
    [InlineData("Tan")]
    [InlineData("Asin")]
    [InlineData("Acos")]
    [InlineData("Atan")]
    [InlineData("Atan2")]
    [InlineData("SinCosBinaryAngle")]
    public void EachFunctionStaysWithinItsBoundOverItsWholeDomain(string function)
    {
        var sweep = Plan(function);
        var merged = new Partition(sweep.Domains.Length);
        foreach (var pass in sweep.Passes)
        {
            merged.Merge(Run(pass, sweep.Domains.Length));
        }

        var output = TestContext.Current.TestOutputHelper;
        for (var d = 0; d < sweep.Domains.Length; d++)
        {
            var domain = sweep.Domains[d];
            var tally = merged.Domains[d];
            output?.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"SIMMATH {domain.Label} max {tally.Max:F3} ulp at a 0x{tally.A:X8} b 0x{tally.B:X8} (bound {domain.Bound})"));
        }

        for (var d = 0; d < sweep.Domains.Length; d++)
        {
            var domain = sweep.Domains[d];
            var tally = merged.Domains[d];
            Assert.True(
                tally.Inputs == domain.ExpectedInputs,
                $"{domain.Label} swept {tally.Inputs} inputs, not the domain's {domain.ExpectedInputs}{sweep.Context}");
            Assert.True(
                tally.Max <= domain.Bound,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{domain.Label} is {tally.Max:F3} ulp from the reference at a 0x{tally.A:X8} b 0x{tally.B:X8}, above its bound {domain.Bound}{sweep.Context}"));
        }

        Assert.True(
            merged.NaNChecks == sweep.ExpectedNaNChecks,
            $"{function} checked {merged.NaNChecks} NaN or out-of-domain inputs, not {sweep.ExpectedNaNChecks}{sweep.Context}");
        Assert.True(
            merged.NaNMisses == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{merged.NaNMisses} NaN or out-of-domain inputs of {function} do not give NaN, the first at a 0x{merged.MissA:X8} b 0x{merged.MissB:X8}{sweep.Context}"));
    }

    /// <summary>The domains, passes and expected NaN checks of one theory row.</summary>
    private static FunctionSweep Plan(string function) => function switch
    {
        "Sin" => Radian(SinId, "Sin", SimMathBounds.SinCosMaxUlp, SimMathBounds.SinCosLargeMaxUlp),
        "Cos" => Radian(CosId, "Cos", SimMathBounds.SinCosMaxUlp, SimMathBounds.SinCosLargeMaxUlp),
        "SinCos" => Radian(SinCosId, "SinCos", SimMathBounds.SinCosMaxUlp, SimMathBounds.SinCosLargeMaxUlp),
        "Tan" => Radian(TanId, "Tan", SimMathBounds.TanMaxUlp, SimMathBounds.TanLargeMaxUlp),
        "Asin" => Inverse(AsinId, "Asin"),
        "Acos" => Inverse(AcosId, "Acos"),
        "Atan" => new(
            [new("Atan", SimMathBounds.AtanMaxUlp, AllPatterns - AllNaN)],
            [new(AllPatterns, CheckAtan)],
            AllNaN,
            string.Empty),
        "Atan2" => new(
            [new("Atan2", SimMathBounds.Atan2MaxUlp, (Atan2RadiusExponents.Length * (long)Atan2Directions) + Atan2RandomPairs)],
            [new(Atan2RadiusExponents.Length * (long)Atan2Directions, CheckAtan2Direction), new(Atan2RandomPairs, CheckAtan2Pair)],
            2L * Atan2RandomPairs,
            string.Create(CultureInfo.InvariantCulture, $" (random pairs from SplitMix64 seed 0x{Atan2Seed:X16})")),
        "SinCosBinaryAngle" => new(
            [new("SinCosBinaryAngle", SimMathBounds.SinCosBinaryAngleMaxUlp, 1L << 16)],
            [new(1L << 16, CheckBinaryAngle)],
            0L,
            string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Not a swept SimMath function."),
    };

    /// <summary>
    /// Sin, Cos, SinCos or Tan over every 32-bit pattern: |x| ≤ 12867 against <paramref name="bound"/>, every
    /// finite float above against <paramref name="largeBound"/> (line <c><paramref name="name"/>Large</c>),
    /// and NaN and ±∞ to NaN.
    /// </summary>
    private static FunctionSweep Radian(int functionId, string name, float bound, float largeBound)
    {
        var small = 2L * (BitConverter.SingleToUInt32Bits(SimMathBounds.ReductionLimit) + 1L);
        return new(
            [new(name, bound, small), new(name + "Large", largeBound, AllFinite - small)],
            [new(AllPatterns, (i, part) => CheckRadian(functionId, (uint)i, part))],
            AllPatterns - AllFinite,
            string.Empty);
    }

    /// <summary>Asin or Acos over every 32-bit pattern: [−1, 1] against the bound, everything else to NaN.</summary>
    private static FunctionSweep Inverse(int functionId, string name)
    {
        var domain = 2L * (BitConverter.SingleToUInt32Bits(1f) + 1L);
        return new(
            [new(name, SimMathBounds.AsinAcosMaxUlp, domain)],
            [new(AllPatterns, (i, part) => CheckInverse(functionId, (uint)i, part))],
            AllPatterns - domain,
            string.Empty);
    }

    private static void CheckRadian(int functionId, uint bits, Partition part)
    {
        var x = BitConverter.UInt32BitsToSingle(bits);
        var (r0, r1) = Evaluate(functionId, x, 0f);
        if (!float.IsFinite(x))
        {
            part.ExpectNaN(float.IsNaN(r0) && (functionId != SinCosId || float.IsNaN(r1)), bits, 0u);
            return;
        }

        var domain = System.Math.Abs(x) <= SimMathBounds.ReductionLimit ? 0 : 1;
        part.Domains[domain].Note(Error(functionId, x, 0f, r0, r1), bits, 0u);
    }

    private static void CheckInverse(int functionId, uint bits, Partition part)
    {
        var x = BitConverter.UInt32BitsToSingle(bits);
        var (r0, r1) = Evaluate(functionId, x, 0f);
        if (!(x >= -1f && x <= 1f))
        {
            part.ExpectNaN(float.IsNaN(r0), bits, 0u);
            return;
        }

        part.Domains[0].Note(Error(functionId, x, 0f, r0, r1), bits, 0u);
    }

    private static void CheckAtan(long index, Partition part)
    {
        var bits = (uint)index;
        var x = BitConverter.UInt32BitsToSingle(bits);
        var (r0, r1) = Evaluate(AtanId, x, 0f);
        if (float.IsNaN(x))
        {
            part.ExpectNaN(float.IsNaN(r0), bits, 0u);
            return;
        }

        part.Domains[0].Note(Error(AtanId, x, 0f, r0, r1), bits, 0u);
    }

    /// <summary>Direction k·2π/2^22 at one of the five radii, the point rounded to float.</summary>
    private static void CheckAtan2Direction(long index, Partition part)
    {
        var radius = System.Math.ScaleB(1.0, Atan2RadiusExponents[(int)(index / Atan2Directions)]);
        var theta = (index % Atan2Directions) * (2.0 * System.Math.PI / Atan2Directions);
        var y = (float)(radius * System.Math.Sin(theta));
        var x = (float)(radius * System.Math.Cos(theta));
        var (r0, r1) = Evaluate(Atan2Id, y, x);
        part.Domains[0].Note(Error(Atan2Id, y, x, r0, r1), BitConverter.SingleToUInt32Bits(y), BitConverter.SingleToUInt32Bits(x));
    }

    /// <summary>One random finite pair, and the same pair with either argument NaN.</summary>
    private static void CheckAtan2Pair(long index, Partition part)
    {
        var (aBits, bBits) = DrawPair(index);
        var a = BitConverter.UInt32BitsToSingle(aBits);
        var b = BitConverter.UInt32BitsToSingle(bBits);
        var (r0, r1) = Evaluate(Atan2Id, a, b);
        part.Domains[0].Note(Error(Atan2Id, a, b, r0, r1), aBits, bBits);
        part.ExpectNaN(float.IsNaN(SimMath.Atan2(float.NaN, b)), BitConverter.SingleToUInt32Bits(float.NaN), bBits);
        part.ExpectNaN(float.IsNaN(SimMath.Atan2(a, float.NaN)), aBits, BitConverter.SingleToUInt32Bits(float.NaN));
    }

    private static void CheckBinaryAngle(long index, Partition part)
    {
        var bits = (uint)index;
        var a = BitConverter.UInt32BitsToSingle(bits);
        var (r0, r1) = Evaluate(SinCosBinaryAngleId, a, 0f);
        part.Domains[0].Note(Error(SinCosBinaryAngleId, a, 0f, r0, r1), bits, 0u);
    }

    /// <summary>
    /// Random pair <paramref name="pair"/>: the first of up to 64 draws <c>SplitMix64.At(seed, pair · 64 + attempt)</c>
    /// whose two halves are finite floats; after 64 the last draw's non-finite halves have their exponent's
    /// top bit cleared, so the sequence is fixed. Pairs are independent, so partitions draw in parallel.
    /// </summary>
    private static (uint A, uint B) DrawPair(long pair)
    {
        var a = 0u;
        var b = 0u;
        for (var attempt = 0; attempt < Atan2Attempts; attempt++)
        {
            var draw = SplitMix64.At(Atan2Seed, ((ulong)pair * Atan2Attempts) + (ulong)attempt);
            a = (uint)(draw >> 32);
            b = (uint)draw;
            if (float.IsFinite(BitConverter.UInt32BitsToSingle(a)) && float.IsFinite(BitConverter.UInt32BitsToSingle(b)))
            {
                return (a, b);
            }
        }

        return (MakeFinite(a), MakeFinite(b));
    }

    private static uint MakeFinite(uint bits) =>
        float.IsFinite(BitConverter.UInt32BitsToSingle(bits)) ? bits : bits & ~0x40000000u;

    /// <summary>The <see cref="SimMath"/> call function <paramref name="functionId"/> names; a single-result function's r1 is 0.</summary>
    private static (float R0, float R1) Evaluate(int functionId, float a, float b) => functionId switch
    {
        SinId => (SimMath.Sin(a), 0f),
        CosId => (SimMath.Cos(a), 0f),
        TanId => (SimMath.Tan(a), 0f),
        SinCosId => SinCos(a),
        AsinId => (SimMath.Asin(a), 0f),
        AcosId => (SimMath.Acos(a), 0f),
        AtanId => (SimMath.Atan(a), 0f),
        Atan2Id => (SimMath.Atan2(a, b), 0f),
        SinCosBinaryAngleId => SinCosBinaryAngle((ushort)BitConverter.SingleToUInt32Bits(a)),
        _ => throw new ArgumentOutOfRangeException(nameof(functionId), functionId, "Not a swept SimMath function."),
    };

    private static (float Sin, float Cos) SinCos(float x)
    {
        SimMath.SinCos(x, out var sin, out var cos);
        return (sin, cos);
    }

    private static (float Sin, float Cos) SinCosBinaryAngle(ushort angle)
    {
        SimMath.SinCosBinaryAngle(angle, out var sin, out var cos);
        return (sin, cos);
    }

    /// <summary>
    /// The row's error in ulps: the worse of r0's and (for SinCos and SinCosBinaryAngle) r1's against the
    /// double reference, with a NaN error (a NaN reference) as +∞, so it can never pass a bound.
    /// </summary>
    private static double Error(int functionId, float a, float b, float r0, float r1)
    {
        var (reference0, reference1) = MathReference.Reference(functionId, a, b);
        var error = Finite(MathReference.UlpError(r0, reference0));
        if (functionId is SinCosId or SinCosBinaryAngleId)
        {
            error = System.Math.Max(error, Finite(MathReference.UlpError(r1, reference1)));
        }

        return error;
    }

    private static double Finite(double error) => double.IsNaN(error) ? double.PositiveInfinity : error;

    /// <summary>Runs one pass over <c>[0, pass.Count)</c> in <see cref="Partitions"/> fixed partitions and merges them in order.</summary>
    private static Partition Run(Pass pass, int domains)
    {
        var parts = new Partition[Partitions];
        Parallel.For(0, Partitions, index =>
        {
            var part = new Partition(domains);
            var end = pass.Count * (index + 1) / Partitions;
            for (var i = pass.Count * index / Partitions; i < end; i++)
            {
                pass.Check(i, part);
            }

            parts[index] = part;
        });

        var merged = new Partition(domains);
        foreach (var part in parts)
        {
            merged.Merge(part);
        }

        return merged;
    }

    /// <summary>One <c>SIMMATH</c> line's domain: its label, its bound and the number of inputs it holds.</summary>
    private sealed record Domain(string Label, float Bound, long ExpectedInputs);

    /// <summary>One sweep over the indices <c>[0, Count)</c>, each checked by <paramref name="Check"/>.</summary>
    private sealed record Pass(long Count, Action<long, Partition> Check);

    /// <summary>A theory row's domains, passes, expected NaN checks and the context its failures print.</summary>
    private sealed record FunctionSweep(Domain[] Domains, Pass[] Passes, long ExpectedNaNChecks, string Context);

    /// <summary>One domain's maximum error, where it occurred, and how many inputs it saw.</summary>
    private sealed class Tally
    {
        public double Max { get; private set; } = -1.0;

        public uint A { get; private set; }

        public uint B { get; private set; }

        public long Inputs { get; private set; }

        public void Note(double error, uint a, uint b)
        {
            Inputs++;
            if (error > Max)
            {
                (Max, A, B) = (error, a, b);
            }
        }

        /// <summary>Adds <paramref name="other"/>, a later partition: an equal maximum keeps the earlier input.</summary>
        public void Merge(Tally other)
        {
            Inputs += other.Inputs;
            if (other.Max > Max)
            {
                (Max, A, B) = (other.Max, other.A, other.B);
            }
        }
    }

    /// <summary>One partition's tallies, one per domain, and its NaN checks.</summary>
    private sealed class Partition(int domains)
    {
        public Tally[] Domains { get; } = Enumerable.Range(0, domains).Select(_ => new Tally()).ToArray();

        public long NaNChecks { get; private set; }

        public long NaNMisses { get; private set; }

        public uint MissA { get; private set; }

        public uint MissB { get; private set; }

        /// <summary>Counts one NaN or out-of-domain input, and a miss when its result is not NaN.</summary>
        public void ExpectNaN(bool resultIsNaN, uint a, uint b)
        {
            NaNChecks++;
            if (!resultIsNaN)
            {
                if (NaNMisses == 0)
                {
                    (MissA, MissB) = (a, b);
                }

                NaNMisses++;
            }
        }

        /// <summary>Adds <paramref name="other"/>, a later partition: the first miss stays the earliest.</summary>
        public void Merge(Partition other)
        {
            for (var d = 0; d < Domains.Length; d++)
            {
                Domains[d].Merge(other.Domains[d]);
            }

            if (NaNMisses == 0 && other.NaNMisses != 0)
            {
                (MissA, MissB) = (other.MissA, other.MissB);
            }

            NaNChecks += other.NaNChecks;
            NaNMisses += other.NaNMisses;
        }
    }
}
