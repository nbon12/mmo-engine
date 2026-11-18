using System.Buffers.Binary;
using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Math;

/// <summary>
/// US3 scenario 8a, the bound half of SC-004 (FR-013): every in-domain row of the fixed math table that
/// every build evaluates is within its documented error bound of the true value, per research.md R3 and
/// contracts/wasm-api.md#cross-build-tables. The table is <c>CrossBuildTables.BuildInputs()</c>, so the
/// rows the bounds are checked on are the rows the cross-build comparison diffs bit for bit; there is one
/// table, not two. The test reads the <c>MMXT</c> math rows (<c>u8</c> function ID, <c>u32</c> a bits,
/// <c>u32</c> b bits at 16 + 12·i) and the <c>MMXR</c> results (<c>u32</c> r0 and r1 bits at 16 + 8·i)
/// with <see cref="BinaryPrimitives"/>, and measures each result against <see cref="MathReference"/>, the
/// double reference that <c>MathReferenceTests</c> guards. <c>System.Math</c> is written in full, because
/// <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
/// <remarks>
/// This test is the only definition of an out-of-domain row: judged from the input alone, a or b is NaN;
/// a is ±∞ for Sin, Cos, Tan and SinCos; |a| &gt; 1 for Asin and Acos; or a &lt; 0 (and not −0) for
/// Sqrt. Such a row must give NaN and so must its double reference; an in-domain row's reference must not
/// be NaN, which ties the predicate and the reference together row by row. Out-of-domain rows are counted
/// explicitly, never by comparing with NaN, because a comparison with NaN is false (research R3: the first
/// prototype harness missed asin(±1) = NaN that way).
/// </remarks>
public sealed class FixedTableBoundsTests
{
    private const int HeaderLength = 16;
    private const int MathCountOffset = 8;
    private const int MathRowLength = 12;
    private const int MathResultLength = 8;

    private const int Sin = 1;
    private const int Cos = 2;
    private const int Tan = 3;
    private const int SinCos = 4;
    private const int Asin = 5;
    private const int Acos = 6;
    private const int Atan = 7;
    private const int Atan2 = 8;
    private const int SinCosBinaryAngle = 9;
    private const int Sqrt = 10;

    /// <summary>The number of binary angles, each of which is one SinCosBinaryAngle row.</summary>
    private const int AngleCount = 65_536;

    /// <summary>The most failing rows a failure message lists.</summary>
    private const int ListedFailures = 20;

    private const uint PlusZero = 0x00000000u;
    private const uint MinusZero = 0x80000000u;
    private const uint SmallestSubnormal = 0x00000001u;
    private const uint LargestSubnormal = 0x007FFFFFu;
    private const uint SmallestNormal = 0x00800000u;
    private const uint QuarterPi = 0x3F490FDBu;
    private const uint ReductionLimitBits = 0x46490C00u;
    private const uint PlusInfinity = 0x7F800000u;
    private const uint MinusInfinity = 0xFF800000u;
    private const uint Half = 0x3F000000u;
    private const uint MinusHalf = 0xBF000000u;
    private const uint One = 0x3F800000u;
    private const uint MinusOne = 0xBF800000u;
    private const uint SevenSixteenths = 0x3EE00000u;
    private const uint ElevenSixteenths = 0x3F300000u;
    private const uint NineteenSixteenths = 0x3F980000u;
    private const uint ThirtyNineSixteenths = 0x401C0000u;

    /// <summary>
    /// US3 scenario 8a: Given the fixed table of <c>CrossBuildTables.BuildInputs()</c> and its results from
    /// <c>CrossBuildTables.Evaluate(inputs, default.json's block)</c>, When the rows of
    /// <paramref name="functionId"/> are compared with <see cref="MathReference.Reference"/>, Then every
    /// in-domain result is within its <see cref="SimMathBounds"/> bound by
    /// <see cref="MathReference.UlpError"/> (Sqrt: bit-identical to the correctly rounded
    /// <c>(float)System.Math.Sqrt(a)</c>), every out-of-domain row gives NaN against a NaN reference, a
    /// single-result function's r1 bits are 0, the domain endpoints are among the in-domain rows, and the
    /// function has at least the contract's row counts.
    /// </summary>
    [Theory]
    [InlineData(Sin)]
    [InlineData(Cos)]
    [InlineData(Tan)]
    [InlineData(SinCos)]
    [InlineData(Asin)]
    [InlineData(Acos)]
    [InlineData(Atan)]
    [InlineData(Atan2)]
    [InlineData(SinCosBinaryAngle)]
    [InlineData(Sqrt)]
    public void EveryInDomainRowIsWithinItsBound(int functionId)
    {
        var inputs = CrossBuildTables.BuildInputs();
        var results = CrossBuildTables.Evaluate(inputs, ConfigBlock.Encode(TestConfigs.Default()));

        var mathRows = (int)BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(MathCountOffset));
        Assert.Equal(mathRows, (int)BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(MathCountOffset)));
        Assert.True(
            results.Length >= HeaderLength + (MathResultLength * mathRows),
            $"the results are {results.Length} bytes, too short for {mathRows} math results");

        var name = Name(functionId);
        var twoResults = functionId is SinCos or SinCosBinaryAngle;
        var failures = new List<string>();
        var failureCount = 0;
        var rows = 0;
        var inDomain = 0;
        var outOfDomain = 0;
        var angleRows = new int[AngleCount];
        var endpointsSeen = new HashSet<(uint A, uint B)>();

        void Fail(string line)
        {
            failureCount++;
            if (failures.Count < ListedFailures)
            {
                failures.Add(line);
            }
        }

        for (var i = 0; i < mathRows; i++)
        {
            var row = inputs.AsSpan(HeaderLength + (MathRowLength * i), MathRowLength);
            if (row[0] != functionId)
            {
                continue;
            }

            rows++;
            var aBits = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            var bBits = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            var result = results.AsSpan(HeaderLength + (MathResultLength * i), MathResultLength);
            var r0Bits = BinaryPrimitives.ReadUInt32LittleEndian(result);
            var r1Bits = BinaryPrimitives.ReadUInt32LittleEndian(result[4..]);
            var a = BitConverter.UInt32BitsToSingle(aBits);
            var b = BitConverter.UInt32BitsToSingle(bBits);
            var r0 = BitConverter.UInt32BitsToSingle(r0Bits);
            var r1 = BitConverter.UInt32BitsToSingle(r1Bits);
            var (reference0, reference1) = MathReference.Reference(functionId, a, b);
            var where = string.Create(CultureInfo.InvariantCulture, $"{name} row {i} a 0x{aBits:X8} b 0x{bBits:X8}");

            if (functionId == SinCosBinaryAngle)
            {
                angleRows[(ushort)aBits]++;
            }

            if (!twoResults && r1Bits != 0u)
            {
                Fail(string.Create(CultureInfo.InvariantCulture, $"{where}: r1 0x{r1Bits:X8} is not 0 for a single-result function"));
            }

            if (IsOutOfDomain(functionId, a, b))
            {
                outOfDomain++;
                if (!float.IsNaN(r0) || (twoResults && !float.IsNaN(r1)))
                {
                    Fail(string.Create(CultureInfo.InvariantCulture, $"{where}: out of domain, but r0 0x{r0Bits:X8} r1 0x{r1Bits:X8} is not NaN"));
                }

                if (!double.IsNaN(reference0) || (twoResults && !double.IsNaN(reference1)))
                {
                    Fail(string.Create(CultureInfo.InvariantCulture, $"{where}: out of domain, but the reference {reference0:R}, {reference1:R} is not NaN"));
                }

                continue;
            }

            inDomain++;
            endpointsSeen.Add((aBits, bBits));
            if (double.IsNaN(reference0) || (twoResults && double.IsNaN(reference1)))
            {
                Fail(string.Create(CultureInfo.InvariantCulture, $"{where}: in domain, but the reference {reference0:R}, {reference1:R} is NaN"));
                continue;
            }

            var bound = Bound(functionId, a);
            if (functionId == Sqrt)
            {
                var expected = BitConverter.SingleToUInt32Bits((float)System.Math.Sqrt(a));
                if (r0Bits != expected)
                {
                    Fail(OutOfBound(where, "r0", r0Bits, MathReference.UlpError(r0, reference0), reference0, bound)
                        + string.Create(CultureInfo.InvariantCulture, $" (the correctly rounded result is 0x{expected:X8})"));
                }

                continue;
            }

            var error0 = MathReference.UlpError(r0, reference0);
            if (!(error0 <= bound))
            {
                Fail(OutOfBound(where, "r0", r0Bits, error0, reference0, bound));
            }

            if (twoResults)
            {
                var error1 = MathReference.UlpError(r1, reference1);
                if (!(error1 <= bound))
                {
                    Fail(OutOfBound(where, "r1", r1Bits, error1, reference1, bound));
                }
            }
        }

        Assert.True(rows > 0, $"{name} (function {functionId}) has no rows in the table");
        if (functionId == SinCosBinaryAngle)
        {
            Assert.Equal(AngleCount, rows);
            var repeated = Enumerable.Range(0, AngleCount).Where(angle => angleRows[angle] != 1).Take(ListedFailures).ToArray();
            Assert.True(
                repeated.Length == 0,
                "these angles do not appear exactly once: " + string.Join(", ", repeated.Select(angle => $"0x{angle:X4} ({angleRows[angle]} rows)")));
            Assert.Equal(0, outOfDomain);
        }
        else
        {
            Assert.True(
                inDomain >= MinimumInDomainRows(functionId),
                $"{name} has {inDomain} in-domain rows, fewer than the contract's {MinimumInDomainRows(functionId)}");
            Assert.True(outOfDomain > 0, $"{name} has no out-of-domain row, so its NaN handling is unchecked");
        }

        var missingEndpoints = Endpoints(functionId).Where(endpoint => !endpointsSeen.Contains(endpoint)).ToArray();
        Assert.True(
            missingEndpoints.Length == 0,
            $"{name} lacks these in-domain endpoint rows: " + string.Join(", ", missingEndpoints.Select(e => $"a 0x{e.A:X8} b 0x{e.B:X8}")));

        Assert.True(
            failureCount == 0,
            $"{failureCount} of {name}'s {rows} rows fail (up to {ListedFailures} listed):{Environment.NewLine}"
                + string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// The out-of-domain predicate of contracts/wasm-api.md#cross-build-tables, judged from the input alone:
    /// a or b is NaN; a is ±∞ for functions 1 to 4; |a| &gt; 1 for functions 5 and 6; or a &lt; 0 (and not
    /// −0, which compares equal to 0) for function 10.
    /// </summary>
    private static bool IsOutOfDomain(int functionId, float a, float b) =>
        float.IsNaN(a)
        || float.IsNaN(b)
        || (functionId is Sin or Cos or Tan or SinCos && float.IsInfinity(a))
        || (functionId is Asin or Acos && System.Math.Abs(a) > 1f)
        || (functionId == Sqrt && a < 0f);

    /// <summary>
    /// The bound, in ulps, of function <paramref name="functionId"/> at argument <paramref name="a"/>:
    /// the radian functions use the small-argument bound for |a| ≤ <see cref="SimMathBounds.ReductionLimit"/>
    /// (the Cody–Waite path) and the large-argument bound above it (the Payne–Hanek path).
    /// </summary>
    private static float Bound(int functionId, float a)
    {
        var small = System.Math.Abs(a) <= SimMathBounds.ReductionLimit;
        return functionId switch
        {
            Sin or Cos or SinCos => small ? SimMathBounds.SinCosMaxUlp : SimMathBounds.SinCosLargeMaxUlp,
            Tan => small ? SimMathBounds.TanMaxUlp : SimMathBounds.TanLargeMaxUlp,
            Asin or Acos => SimMathBounds.AsinAcosMaxUlp,
            Atan => SimMathBounds.AtanMaxUlp,
            Atan2 => SimMathBounds.Atan2MaxUlp,
            SinCosBinaryAngle => SimMathBounds.SinCosBinaryAngleMaxUlp,
            Sqrt => SimMathBounds.SqrtMaxUlp,
            _ => throw new ArgumentOutOfRangeException(nameof(functionId), functionId, "Function IDs are 1 to 10."),
        };
    }

    /// <summary>The contract's lowest in-domain row count for each function but SinCosBinaryAngle, which has exactly 65,536.</summary>
    private static int MinimumInDomainRows(int functionId) => functionId == Sqrt ? 1_024 : 4_096;

    /// <summary>
    /// The domain endpoints each function's in-domain rows must include, as (a, b) bits: ±0, the subnormals
    /// (and, for the radian functions, the smallest normal), π/4 and 12867 with their neighbours, ±1 with
    /// the neighbour inside the domain, ±½ with both neighbours, Atan's fdlibm breakpoints with their
    /// neighbours and ±∞, and Atan2's ±0/±∞ combinations and axis directions. SinCosBinaryAngle's every
    /// angle is checked separately.
    /// </summary>
    private static (uint A, uint B)[] Endpoints(int functionId) => functionId switch
    {
        Sin or Cos or Tan or SinCos => Single(
            PlusZero,
            MinusZero,
            SmallestSubnormal,
            LargestSubnormal,
            SmallestNormal,
            QuarterPi - 1,
            QuarterPi,
            QuarterPi + 1,
            ReductionLimitBits - 1,
            ReductionLimitBits,
            ReductionLimitBits + 1),
        Asin or Acos => Single(
            PlusZero,
            MinusZero,
            SmallestSubnormal,
            LargestSubnormal,
            Half - 1,
            Half,
            Half + 1,
            MinusHalf - 1,
            MinusHalf,
            MinusHalf + 1,
            One - 1,
            One,
            MinusOne - 1,
            MinusOne),
        Atan => Single(
            PlusZero,
            MinusZero,
            SmallestSubnormal,
            LargestSubnormal,
            SevenSixteenths - 1,
            SevenSixteenths,
            SevenSixteenths + 1,
            ElevenSixteenths - 1,
            ElevenSixteenths,
            ElevenSixteenths + 1,
            NineteenSixteenths - 1,
            NineteenSixteenths,
            NineteenSixteenths + 1,
            ThirtyNineSixteenths - 1,
            ThirtyNineSixteenths,
            ThirtyNineSixteenths + 1,
            PlusInfinity,
            MinusInfinity),
        Atan2 =>
        [
            .. from a in new[] { PlusZero, MinusZero, PlusInfinity, MinusInfinity }
               from b in new[] { PlusZero, MinusZero, PlusInfinity, MinusInfinity }
               select (a, b),
            (PlusZero, One),
            (One, PlusZero),
            (PlusZero, MinusOne),
            (MinusOne, PlusZero),
        ],
        SinCosBinaryAngle => [],
        Sqrt => Single(PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal),
        _ => throw new ArgumentOutOfRangeException(nameof(functionId), functionId, "Function IDs are 1 to 10."),
    };

    /// <summary>Single-argument endpoints: these a bits, each with b 0.</summary>
    private static (uint A, uint B)[] Single(params uint[] a) => a.Select(bits => (bits, 0u)).ToArray();

    /// <summary>The failure line for a result outside its bound.</summary>
    private static string OutOfBound(string where, string field, uint resultBits, double error, double reference, float bound) =>
        string.Create(CultureInfo.InvariantCulture, $"{where}: {field} 0x{resultBits:X8} is {error:F3} ulp from {reference:R}, bound {bound}");

    private static string Name(int functionId) => functionId switch
    {
        Sin => "Sin",
        Cos => "Cos",
        Tan => "Tan",
        SinCos => "SinCos",
        Asin => "Asin",
        Acos => "Acos",
        Atan => "Atan",
        Atan2 => "Atan2",
        SinCosBinaryAngle => "SinCosBinaryAngle",
        Sqrt => "Sqrt",
        _ => throw new ArgumentOutOfRangeException(nameof(functionId), functionId, "Function IDs are 1 to 10."),
    };
}
