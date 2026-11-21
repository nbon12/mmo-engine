using System.Globalization;
using System.Reflection;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Tests.Math;

/// <summary>
/// The core's own trigonometry, square root, compare-selects and saturating conversions (FR-013,
/// research R3 and R4). Every exact assertion compares bits through
/// <see cref="BitConverter.SingleToUInt32Bits"/>, so a signed zero or a NaN payload cannot pass by
/// comparing equal. The bound rows compare with the <c>System.Math</c> double reference, in ulps of the
/// reference rounded to float.
/// </summary>
public sealed class SimMathTests
{
    private const uint PlusZero = 0x00000000u;
    private const uint MinusZero = 0x80000000u;
    private const uint SmallestSubnormal = 0x00000001u;
    private const uint LargestSubnormal = 0x007FFFFFu;
    private const uint Half = 0x3F000000u;
    private const uint One = 0x3F800000u;
    private const uint MinusOne = 0xBF800000u;
    private const uint Two = 0x40000000u;
    private const uint MinusTwo = 0xC0000000u;
    private const uint Five = 0x40A00000u;
    private const uint MinusFive = 0xC0A00000u;
    private const uint HalfPi = 0x3FC90FDBu;
    private const uint MinusHalfPi = 0xBFC90FDBu;
    private const uint Pi = 0x40490FDBu;
    private const uint MinusPi = 0xC0490FDBu;
    private const uint QuarterPi = 0x3F490FDBu;
    private const uint PositiveInfinity = 0x7F800000u;
    private const uint NegativeInfinity = 0xFF800000u;
    private const uint QuietNaN = 0x7FC00000u;
    private const uint PayloadNaN = 0x7FC00001u;
    private const uint ThreeE9 = 0x4F32D05Eu;
    private const uint MinusThreeE9 = 0xCF32D05Eu;
    private const uint TwoTo31 = 0x4F000000u;
    private const uint MinusTwoTo31 = 0xCF000000u;
    private const uint BelowTwoTo31 = 0x4EFFFFFFu;
    private const uint SignBit = 0x80000000u;

    /// <summary>The largest |x| the Cody–Waite reduction handles; Payne–Hanek takes over above it.</summary>
    private const float CodyWaiteLimit = 12867f;

    private const string WordPrefix = "TwoOverPi.Words[";

    /// <summary>
    /// The <c>name 0xXXXXXXXX</c> lines that <c>tools/simcore/emit_cs.py --list</c> prints, one per
    /// <c>const float</c> of <c>SimMathCoefficients</c>. The generator task (T042) pastes them here
    /// verbatim, one string per line.
    /// </summary>
    private static readonly string[] CoefficientLines =
    [
        "SinS1 0xBE2AAAA2",
        "SinS2 0x3C088363",
        "SinS3 0xB94C8E13",
        "CosC1 0x3D2AAAAB",
        "CosC2 0xBAB60BAE",
        "CosC3 0x37D037C8",
        "CosC4 0xB49AC28E",
        "TanT1 0x3EAAAAB0",
        "TanT2 0x3E0886CA",
        "TanT3 0x3D5D6E45",
        "TanT4 0x3CAE4E86",
        "TanT5 0x3C31D00B",
        "TanT6 0x38839B6B",
        "TanT7 0x3B8FE733",
        "AsinA0 0x3E2AAAE4",
        "AsinA1 0x3D998102",
        "AsinA2 0x3D3A3DDD",
        "AsinA3 0x3CC61F15",
        "AsinA4 0x3D2CAE46",
        "AtanB0 0x3EAAAAAA",
        "AtanB1 0xBE4CCC49",
        "AtanB2 0x3E123820",
        "AtanB3 0xBDE1832A",
        "AtanB4 0x3DA9EA60",
        "AtanB5 0xBD33ACA0",
        "HalfPiPart1 0x3FC90000",
        "HalfPiPart2 0x39FDA000",
        "HalfPiPart3 0x33A22000",
        "HalfPiPart4 0x2C34611A",
        "TwoOverPiSingle 0x3F22F983",
        "BinaryAngleHi 0x38C90000",
        "BinaryAngleLo 0x32FDAA22",
        "HalfPiHi 0x3FC90FDB",
        "HalfPiLo 0xB33BBD2E",
        "QuarterPiHi 0x3F490FDB",
        "QuarterPiLo 0xB2BBBD2E",
        "PiHi 0x40490FDB",
        "PiLo 0xB3BBBD2E",
        "AtanHalfHi 0x3EED6338",
        "AtanHalfLo 0x31AC376A",
        "AtanThreeHalvesHi 0x3F7B985F",
        "AtanThreeHalvesLo 0xB2D7E096",
        "ThreeQuarterPi 0x4016CBE4",
    ];

    /// <summary>The 320 bits of 2/π in <c>TwoOverPi.Words</c>, most significant word first.</summary>
    private static readonly uint[] TwoOverPiWords =
    [
        0xA2F9836Eu, 0x4E441529u, 0xFC2757D1u, 0xF534DDC0u, 0xDB629599u,
        0x3C439041u, 0xFE5163ABu, 0xDEBBC561u, 0xB7246E3Au, 0x424DD2E0u,
    ];

    /// <summary>
    /// The rows whose input is not finite or is outside the domain but whose result is finite:
    /// Atan(±∞) is ±π/2 (contracts/wasm-api.md#cross-build-tables).
    /// </summary>
    private static readonly Dictionary<(string Function, uint InputBits), uint> FiniteResults = new()
    {
        [("Atan", PositiveInfinity)] = HalfPi,
        [("Atan", NegativeInfinity)] = MinusHalfPi,
    };

    /// <summary>Every coefficient row from the generator's list, then every word of 2/π.</summary>
    public static TheoryData<string, uint> PinnedConstants
    {
        get
        {
            var data = new TheoryData<string, uint>();
            foreach (var line in CoefficientLines)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !parts[1].StartsWith("0x", StringComparison.Ordinal))
                {
                    throw new FormatException($"A coefficient line must be 'name 0xXXXXXXXX': '{line}'.");
                }

                data.Add(parts[0], uint.Parse(parts[1].AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }

            for (var i = 0; i < TwoOverPiWords.Length; i++)
            {
                data.Add(string.Create(CultureInfo.InvariantCulture, $"{WordPrefix}{i}]"), TwoOverPiWords[i]);
            }

            return data;
        }
    }

    /// <summary>
    /// The sample inputs of each reduction path and kernel, as (function, first argument bits, second
    /// argument bits). A one-argument function's second argument is 0. Atan2's rows are in argument
    /// order, so the first is C99's y and the second its x.
    /// </summary>
    public static TheoryData<string, uint, uint> ReductionRows
    {
        get
        {
            var data = new TheoryData<string, uint, uint>();

            // Sin, Cos, Tan and SinCos: no reduction and Cody–Waite reduction, then Payne–Hanek
            // reduction from just above 12867.
            uint[] codyWaite = [Bits(0.5f), QuarterPi - 1, QuarterPi, QuarterPi + 1, Bits(100f)];
            uint[] payneHanek =
            [
                Bits(CodyWaiteLimit) - 1, Bits(CodyWaiteLimit), Bits(CodyWaiteLimit) + 1,
                Bits(12868f), Bits(1e6f), Bits(1e20f), Bits(float.MaxValue),
            ];
            foreach (var function in new[] { "Sin", "Cos", "Tan", "SinCos" })
            {
                foreach (var x in codyWaite.Concat(payneHanek))
                {
                    data.Add(function, x, 0u);
                }
            }

            // Asin and Acos: the polynomial up to ½ and the square-root correction above it.
            uint[] arc = [Bits(0.25f), Half - 1, Half, Half + 1, Bits(0.9f), One, MinusOne];
            foreach (var function in new[] { "Asin", "Acos" })
            {
                foreach (var x in arc)
                {
                    data.Add(function, x, 0u);
                }
            }

            // Atan: each of fdlibm's breakpoints and its two neighbours, and a large argument.
            foreach (var breakpoint in new[] { 7f / 16f, 11f / 16f, 19f / 16f, 39f / 16f })
            {
                data.Add("Atan", Bits(breakpoint) - 1, 0u);
                data.Add("Atan", Bits(breakpoint), 0u);
                data.Add("Atan", Bits(breakpoint) + 1, 0u);
            }

            data.Add("Atan", Bits(1e10f), 0u);

            // Atan2(y, x).
            data.Add("Atan2", One, One);
            data.Add("Atan2", MinusOne, MinusOne);
            data.Add("Atan2", Bits(1e-30f), MinusOne);
            data.Add("Atan2", Bits(3f), Bits(-4f));
            return data;
        }
    }

    /// <summary>
    /// NaN in gives NaN out for every radian function and Sqrt, in either argument of Atan2; ±∞ gives
    /// NaN for Sin, Cos, Tan and SinCos; Asin and Acos just outside ±1 and at ±2 give NaN; Sqrt(−1)
    /// gives NaN. SinCos gives NaN in both outputs. The two Atan(±∞) rows give the finite ±π/2.
    /// </summary>
    [Theory]
    [InlineData("Sin", QuietNaN)]
    [InlineData("Cos", QuietNaN)]
    [InlineData("Tan", QuietNaN)]
    [InlineData("SinCos", QuietNaN)]
    [InlineData("Asin", QuietNaN)]
    [InlineData("Acos", QuietNaN)]
    [InlineData("Atan", QuietNaN)]
    [InlineData("Sqrt", QuietNaN)]
    [InlineData("Atan2(input, 1)", QuietNaN)]
    [InlineData("Atan2(1, input)", QuietNaN)]
    [InlineData("Sin", PositiveInfinity)]
    [InlineData("Sin", NegativeInfinity)]
    [InlineData("Cos", PositiveInfinity)]
    [InlineData("Cos", NegativeInfinity)]
    [InlineData("Tan", PositiveInfinity)]
    [InlineData("Tan", NegativeInfinity)]
    [InlineData("SinCos", PositiveInfinity)]
    [InlineData("SinCos", NegativeInfinity)]
    [InlineData("Asin", 0x3F800001u)]
    [InlineData("Asin", 0xBF800001u)]
    [InlineData("Asin", Two)]
    [InlineData("Asin", MinusTwo)]
    [InlineData("Acos", 0x3F800001u)]
    [InlineData("Acos", 0xBF800001u)]
    [InlineData("Acos", Two)]
    [InlineData("Acos", MinusTwo)]
    [InlineData("Sqrt", MinusOne)]
    [InlineData("Atan", PositiveInfinity)]
    [InlineData("Atan", NegativeInfinity)]
    public void NonFiniteInputsReturnNaN(string function, uint inputBits)
    {
        var outputs = Evaluate(function, Float(inputBits), 0f);

        if (FiniteResults.TryGetValue((function, inputBits), out var expected))
        {
            var bits = Bits(Assert.Single(outputs));
            Assert.True(bits == expected, $"{function}(0x{inputBits:X8}) is 0x{bits:X8}, expected the finite 0x{expected:X8}");
            return;
        }

        for (var i = 0; i < outputs.Length; i++)
        {
            Assert.True(float.IsNaN(outputs[i]), $"{function}(0x{inputBits:X8}) output {i} is 0x{Bits(outputs[i]):X8}, expected NaN");
        }
    }

    /// <summary>
    /// Signed zeros keep their sign where the function is odd, Cos(±0) is 1 and Acos(0) is π/2,
    /// subnormals pass through the odd functions unchanged, and Atan2 follows C99 Annex F at the
    /// signed zeros and the negative x axis.
    /// </summary>
    [Theory]
    [InlineData("Sin", PlusZero, 0u, PlusZero)]
    [InlineData("Sin", MinusZero, 0u, MinusZero)]
    [InlineData("Tan", PlusZero, 0u, PlusZero)]
    [InlineData("Tan", MinusZero, 0u, MinusZero)]
    [InlineData("Asin", PlusZero, 0u, PlusZero)]
    [InlineData("Asin", MinusZero, 0u, MinusZero)]
    [InlineData("Atan", PlusZero, 0u, PlusZero)]
    [InlineData("Atan", MinusZero, 0u, MinusZero)]
    [InlineData("Sqrt", PlusZero, 0u, PlusZero)]
    [InlineData("Sqrt", MinusZero, 0u, MinusZero)]
    [InlineData("Cos", PlusZero, 0u, One)]
    [InlineData("Cos", MinusZero, 0u, One)]
    [InlineData("Acos", PlusZero, 0u, HalfPi)]
    [InlineData("Sin", SmallestSubnormal, 0u, SmallestSubnormal)]
    [InlineData("Sin", LargestSubnormal, 0u, LargestSubnormal)]
    [InlineData("Tan", SmallestSubnormal, 0u, SmallestSubnormal)]
    [InlineData("Tan", LargestSubnormal, 0u, LargestSubnormal)]
    [InlineData("Asin", SmallestSubnormal, 0u, SmallestSubnormal)]
    [InlineData("Asin", LargestSubnormal, 0u, LargestSubnormal)]
    [InlineData("Atan", SmallestSubnormal, 0u, SmallestSubnormal)]
    [InlineData("Atan", LargestSubnormal, 0u, LargestSubnormal)]
    [InlineData("Atan2", PlusZero, PlusZero, PlusZero)]
    [InlineData("Atan2", MinusZero, PlusZero, MinusZero)]
    [InlineData("Atan2", PlusZero, MinusZero, Pi)]
    [InlineData("Atan2", PlusZero, MinusOne, Pi)]
    [InlineData("Atan2", MinusZero, MinusZero, MinusPi)]
    [InlineData("Atan2", MinusZero, MinusOne, MinusPi)]
    public void SignedZerosAndSubnormalsFollowTheContract(string function, uint aBits, uint bBits, uint expectedBits)
    {
        var bits = Bits(Assert.Single(Evaluate(function, Float(aBits), Float(bBits))));

        Assert.True(bits == expectedBits, $"{function}(0x{aBits:X8}, 0x{bBits:X8}) is 0x{bits:X8}, expected 0x{expectedBits:X8}");
    }

    /// <summary>
    /// The three conversions saturate before they cast (research R4): NaN gives 0, 2³¹ and above give
    /// <see cref="int.MaxValue"/>, −2³¹ and below give <see cref="int.MinValue"/>. RoundToInt ties to
    /// even, FloorToInt rounds toward −∞ and TruncateToInt toward 0.
    /// </summary>
    [Theory]
    [InlineData("FloorToInt", QuietNaN, 0)]
    [InlineData("FloorToInt", ThreeE9, int.MaxValue)]
    [InlineData("FloorToInt", TwoTo31, int.MaxValue)]
    [InlineData("FloorToInt", PositiveInfinity, int.MaxValue)]
    [InlineData("FloorToInt", MinusThreeE9, int.MinValue)]
    [InlineData("FloorToInt", MinusTwoTo31, int.MinValue)]
    [InlineData("FloorToInt", NegativeInfinity, int.MinValue)]
    [InlineData("FloorToInt", BelowTwoTo31, 2147483520)]
    [InlineData("RoundToInt", QuietNaN, 0)]
    [InlineData("RoundToInt", ThreeE9, int.MaxValue)]
    [InlineData("RoundToInt", TwoTo31, int.MaxValue)]
    [InlineData("RoundToInt", PositiveInfinity, int.MaxValue)]
    [InlineData("RoundToInt", MinusThreeE9, int.MinValue)]
    [InlineData("RoundToInt", MinusTwoTo31, int.MinValue)]
    [InlineData("RoundToInt", NegativeInfinity, int.MinValue)]
    [InlineData("RoundToInt", BelowTwoTo31, 2147483520)]
    [InlineData("TruncateToInt", QuietNaN, 0)]
    [InlineData("TruncateToInt", ThreeE9, int.MaxValue)]
    [InlineData("TruncateToInt", TwoTo31, int.MaxValue)]
    [InlineData("TruncateToInt", PositiveInfinity, int.MaxValue)]
    [InlineData("TruncateToInt", MinusThreeE9, int.MinValue)]
    [InlineData("TruncateToInt", MinusTwoTo31, int.MinValue)]
    [InlineData("TruncateToInt", NegativeInfinity, int.MinValue)]
    [InlineData("TruncateToInt", BelowTwoTo31, 2147483520)]
    [InlineData("RoundToInt", 0x3F000000u, 0)] // 0.5f
    [InlineData("RoundToInt", 0x3FC00000u, 2)] // 1.5f
    [InlineData("RoundToInt", 0x40200000u, 2)] // 2.5f
    [InlineData("RoundToInt", 0x40600000u, 4)] // 3.5f
    [InlineData("RoundToInt", 0xC0200000u, -2)] // -2.5f
    [InlineData("RoundToInt", 0x3EFFFFFFu, 0)] // 0.49999997f
    [InlineData("FloorToInt", 0xBFC00000u, -2)] // -1.5f
    [InlineData("FloorToInt", MinusZero, 0)]
    [InlineData("TruncateToInt", 0xBFC00000u, -1)] // -1.5f
    [InlineData("TruncateToInt", 0x3FFFFFFFu, 1)] // 1.9999999f
    public void SaturatingConversionsClampNaNAndOutOfRange(string conversion, uint inputBits, int expected)
    {
        var input = Float(inputBits);

        var actual = conversion switch
        {
            "FloorToInt" => SimMath.FloorToInt(input),
            "RoundToInt" => SimMath.RoundToInt(input),
            "TruncateToInt" => SimMath.TruncateToInt(input),
            _ => throw new ArgumentException($"Unknown conversion {conversion}.", nameof(conversion)),
        };

        Assert.True(actual == expected, $"{conversion}(0x{inputBits:X8}) is {actual}, expected {expected}");
    }

    /// <summary>
    /// Min is <c>a &lt; b ? a : b</c> and Max is <c>a &gt; b ? a : b</c>, so a signed zero or a NaN
    /// in either argument gives the same bits in every build (research R4). Clamp(value, min, max) is
    /// <c>value &lt; min ? min : (value &gt; max ? max : value)</c>: it returns the bound a value lies
    /// beyond, a NaN value unchanged and a signed zero with its sign, and it does not throw when the
    /// bounds cross. The third argument is used by Clamp only.
    /// </summary>
    [Theory]
    [InlineData("Min", PlusZero, MinusZero, 0u, MinusZero)]
    [InlineData("Min", MinusZero, PlusZero, 0u, PlusZero)]
    [InlineData("Min", PayloadNaN, One, 0u, One)]
    [InlineData("Min", One, PayloadNaN, 0u, PayloadNaN)]
    [InlineData("Max", PlusZero, MinusZero, 0u, MinusZero)]
    [InlineData("Max", MinusZero, PlusZero, 0u, PlusZero)]
    [InlineData("Max", PayloadNaN, One, 0u, One)]
    [InlineData("Max", One, PayloadNaN, 0u, PayloadNaN)]
    [InlineData("Clamp", Five, PlusZero, One, One)]
    [InlineData("Clamp", MinusFive, PlusZero, One, PlusZero)]
    [InlineData("Clamp", Half, PlusZero, One, Half)]
    [InlineData("Clamp", PayloadNaN, PlusZero, One, PayloadNaN)]
    [InlineData("Clamp", MinusZero, PlusZero, One, MinusZero)]
    [InlineData("Clamp", Half, One, PlusZero, One)]
    [InlineData("Clamp", Five, One, PlusZero, PlusZero)]
    public void MinMaxAreCompareSelects(string function, uint aBits, uint bBits, uint cBits, uint expectedBits)
    {
        var a = Float(aBits);
        var b = Float(bBits);

        var result = function switch
        {
            "Min" => SimMath.Min(a, b),
            "Max" => SimMath.Max(a, b),
            "Clamp" => SimMath.Clamp(a, b, Float(cBits)),
            _ => throw new ArgumentException($"Unknown function {function}.", nameof(function)),
        };

        var bits = Bits(result);
        Assert.True(bits == expectedBits, $"{function}(0x{aBits:X8}, 0x{bBits:X8}, 0x{cBits:X8}) is 0x{bits:X8}, expected 0x{expectedBits:X8}");
    }

    /// <summary>
    /// The binary-angle sine and cosine are exact at the four quadrant angles, symmetric at 45°, and
    /// reduce the quadrant in integers: for every one of the 65,536 angles a,
    /// SinCosBinaryAngle(a + 16384) is bit for bit (cos a, −sin a).
    /// </summary>
    [Fact]
    public void SinCosBinaryAngleIsExactAtTheQuadrants()
    {
        SimMath.SinCosBinaryAngle(0, out var s0, out var c0);
        Assert.True(s0 == 0f && Bits(c0) == One, $"angle 0: (0x{Bits(s0):X8}, 0x{Bits(c0):X8}), expected (0, 1)");

        SimMath.SinCosBinaryAngle(16384, out var s1, out var c1);
        Assert.True(Bits(s1) == One && c1 == 0f, $"angle 16384: (0x{Bits(s1):X8}, 0x{Bits(c1):X8}), expected (1, 0)");

        SimMath.SinCosBinaryAngle(32768, out var s2, out var c2);
        Assert.True(s2 == 0f && Bits(c2) == MinusOne, $"angle 32768: (0x{Bits(s2):X8}, 0x{Bits(c2):X8}), expected (0, -1)");

        SimMath.SinCosBinaryAngle(49152, out var s3, out var c3);
        Assert.True(Bits(s3) == MinusOne && c3 == 0f, $"angle 49152: (0x{Bits(s3):X8}, 0x{Bits(c3):X8}), expected (-1, 0)");

        SimMath.SinCosBinaryAngle(8192, out var s45, out var c45);
        Assert.True(Bits(s45) == Bits(c45), $"angle 8192: sine 0x{Bits(s45):X8} and cosine 0x{Bits(c45):X8} differ");
        var error = UlpError(s45, System.Math.Sqrt(0.5));
        Assert.True(error <= 1.0, $"angle 8192: 0x{Bits(s45):X8} is {error:F3} ulp from the square root of one half");

        var mismatches = 0;
        var first = new List<string>();
        for (var a = 0; a < 65536; a++)
        {
            SimMath.SinCosBinaryAngle((ushort)a, out var sine, out var cosine);
            SimMath.SinCosBinaryAngle((ushort)(a + 16384), out var shiftedSine, out var shiftedCosine);
            if (Bits(shiftedSine) != Bits(cosine) || Bits(shiftedCosine) != (Bits(sine) ^ SignBit))
            {
                mismatches++;
                if (first.Count < 8)
                {
                    first.Add($"a {a}: (0x{Bits(shiftedSine):X8}, 0x{Bits(shiftedCosine):X8}), expected (0x{Bits(cosine):X8}, 0x{Bits(sine) ^ SignBit:X8})");
                }
            }
        }

        Assert.True(mismatches == 0, $"{mismatches} angles a where SinCosBinaryAngle(a + 16384) is not (cos a, -sin a) bit for bit; first: {string.Join("; ", first)}");
    }

    /// <summary>
    /// Every generated <c>const float</c> of <c>SimMathCoefficients</c> and every word of
    /// <c>TwoOverPi.Words</c> has the bits the generator printed, so a regenerated or hand-edited
    /// constant cannot change the core's results unnoticed (research R3).
    /// </summary>
    [Theory]
    [MemberData(nameof(PinnedConstants))]
    public void CoefficientsHaveTheirPinnedBits(string name, uint bits)
    {
        uint actual;
        if (name.StartsWith(WordPrefix, StringComparison.Ordinal))
        {
            var index = int.Parse(name.AsSpan(WordPrefix.Length, name.Length - WordPrefix.Length - 1), CultureInfo.InvariantCulture);
            var words = TwoOverPi.Words;
            Assert.True(words.Length == TwoOverPiWords.Length, $"TwoOverPi.Words has {words.Length} words, expected {TwoOverPiWords.Length} (320 bits)");
            actual = words[index];
        }
        else
        {
            var field = typeof(SimMathCoefficients).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.True(field is { IsLiteral: true } && field.FieldType == typeof(float), $"SimMathCoefficients has no const float {name}");
            actual = Bits((float)field.GetRawConstantValue()!);
        }

        Assert.True(actual == bits, $"{name} is 0x{actual:X8}, pinned 0x{bits:X8}");
    }

    /// <summary>
    /// Each reduction path and kernel meets research R3's bound at its sample inputs, against the
    /// <c>System.Math</c> double reference: Sin and Cos 1 ulp for |x| ≤ 12867 and 2 above, Tan 1.5
    /// and 2.5, Asin, Acos and Atan 1, Atan2 2. SinCos meets the Sin and Cos bounds in each output.
    /// For Atan2, <paramref name="xBits"/> is the first argument (C99's y) and
    /// <paramref name="yBits"/> the second (C99's x).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReductionRows))]
    public void EachReductionPathMeetsItsBoundAtSampleInputs(string function, uint xBits, uint yBits)
    {
        var x = Float(xBits);
        var y = Float(yBits);
        var sinCosBound = float.Abs(x) <= CodyWaiteLimit ? 1.0 : 2.0;
        var tanBound = float.Abs(x) <= CodyWaiteLimit ? 1.5 : 2.5;

        (double Reference, double Bound)[] expected = function switch
        {
            "Sin" => [(System.Math.Sin(x), sinCosBound)],
            "Cos" => [(System.Math.Cos(x), sinCosBound)],
            "Tan" => [(System.Math.Tan(x), tanBound)],
            "SinCos" => [(System.Math.Sin(x), sinCosBound), (System.Math.Cos(x), sinCosBound)],
            "Asin" => [(System.Math.Asin(x), 1.0)],
            "Acos" => [(System.Math.Acos(x), 1.0)],
            "Atan" => [(System.Math.Atan(x), 1.0)],
            "Atan2" => [(System.Math.Atan2(x, y), 2.0)],
            _ => throw new ArgumentException($"Unknown function {function}.", nameof(function)),
        };

        var outputs = Evaluate(function, x, y);
        Assert.True(outputs.Length == expected.Length, $"{function} gave {outputs.Length} outputs, expected {expected.Length}");
        for (var i = 0; i < outputs.Length; i++)
        {
            var (reference, bound) = expected[i];
            var error = UlpError(outputs[i], reference);
            Assert.True(
                float.IsFinite(outputs[i]) && error <= bound,
                $"{function}(0x{xBits:X8}, 0x{yBits:X8}) output {i}: 0x{Bits(outputs[i]):X8} ({outputs[i]:R}), reference {reference:R}, error {error:F3} ulp, bound {bound} ulp");
        }
    }

    /// <summary>
    /// Calls the <c>SimMath</c> function a row names and returns its outputs: sine then cosine for
    /// SinCos, the one result otherwise. Atan2 takes <paramref name="a"/> as y and <paramref name="b"/>
    /// as x; the two named Atan2 forms put the row's input in one argument and 1 in the other.
    /// </summary>
    private static float[] Evaluate(string function, float a, float b)
    {
        switch (function)
        {
            case "Sin":
                return [SimMath.Sin(a)];
            case "Cos":
                return [SimMath.Cos(a)];
            case "Tan":
                return [SimMath.Tan(a)];
            case "SinCos":
                SimMath.SinCos(a, out var sine, out var cosine);
                return [sine, cosine];
            case "Asin":
                return [SimMath.Asin(a)];
            case "Acos":
                return [SimMath.Acos(a)];
            case "Atan":
                return [SimMath.Atan(a)];
            case "Atan2":
                return [SimMath.Atan2(a, b)];
            case "Atan2(input, 1)":
                return [SimMath.Atan2(a, 1f)];
            case "Atan2(1, input)":
                return [SimMath.Atan2(1f, a)];
            case "Sqrt":
                return [SimMath.Sqrt(a)];
            default:
                throw new ArgumentException($"Unknown function {function}.", nameof(function));
        }
    }

    /// <summary>
    /// |actual − reference| in ulps of the reference rounded to float: the spacing of floats at that
    /// magnitude, which is the smallest subnormal at 0.
    /// </summary>
    private static double UlpError(float actual, double reference)
    {
        var magnitude = float.Abs((float)reference);
        var ulp = (double)float.BitIncrement(magnitude) - magnitude;
        return System.Math.Abs(actual - reference) / ulp;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static float Float(uint bits) => BitConverter.UInt32BitsToSingle(bits);
}
