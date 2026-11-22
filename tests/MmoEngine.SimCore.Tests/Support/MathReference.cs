namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The double reference for the core's float32 math, and the ulp error measured against it, which the
/// bound tests in <c>tests/MmoEngine.SimCore.Tests/Math/</c> use (research.md R3,
/// contracts/wasm-api.md#cross-build-tables). Test code only: the core never computes in double.
/// <c>System.Math</c> is written in full, because <c>MmoEngine.SimCore.Math</c> shadows it.
/// </summary>
public static class MathReference
{
    /// <summary>
    /// The spacing of floats in <paramref name="reference"/>'s binade: 2^(e − 23) for the reference's
    /// exponent e, with e no lower than −126, so the spacing near zero is the subnormal spacing 2^-149.
    /// </summary>
    public static double Ulp(double reference) =>
        System.Math.ScaleB(1.0, System.Math.Max(System.Math.ILogB(System.Math.Abs(reference)), -126) - 23);

    /// <summary>
    /// |<paramref name="result"/> − <paramref name="reference"/>| in <see cref="Ulp"/>s of the reference.
    /// Against a zero reference it is 0 for ±0 and +∞ for anything else; a NaN result against a finite
    /// reference is +∞, so a NaN can never pass a bound.
    /// </summary>
    public static double UlpError(float result, double reference)
    {
        if (reference == 0.0)
        {
            return result == 0f ? 0.0 : double.PositiveInfinity;
        }

        if (float.IsNaN(result) && double.IsFinite(reference))
        {
            return double.PositiveInfinity;
        }

        return System.Math.Abs((double)result - reference) / Ulp(reference);
    }

    /// <summary>
    /// The double reference for one math row of the cross-build tables, by function ID: 1 Sin, 2 Cos,
    /// 3 Tan, 4 SinCos (sine, cosine), 5 Asin, 6 Acos, 7 Atan, 8 Atan2 (y = <paramref name="a"/>,
    /// x = <paramref name="b"/>), 9 SinCosBinaryAngle (sine, cosine of the angle in a's low 16 bits, in
    /// units of 2π/65536) and 10 Sqrt, all through <c>System.Math</c> or <c>double.SinPi</c> and
    /// <c>double.CosPi</c> in double. A single-result function's second value is 0, as its r1 is in the
    /// results table.
    /// </summary>
    public static (double R0, double R1) Reference(int functionId, float a, float b) => functionId switch
    {
        1 => (System.Math.Sin(a), 0.0),
        2 => (System.Math.Cos(a), 0.0),
        3 => (System.Math.Tan(a), 0.0),
        4 => (System.Math.Sin(a), System.Math.Cos(a)),
        5 => (System.Math.Asin(a), 0.0),
        6 => (System.Math.Acos(a), 0.0),
        7 => (System.Math.Atan(a), 0.0),
        8 => (System.Math.Atan2(a, b), 0.0),
        9 => BinaryAngle(a),
        10 => (System.Math.Sqrt(a), 0.0),
        _ => throw new ArgumentOutOfRangeException(nameof(functionId), functionId, "Function IDs are 1 to 10 (contracts/wasm-api.md#cross-build-tables)."),
    };

    /// <summary>The sine and cosine of the binary angle held in <paramref name="a"/>'s low 16 bits.</summary>
    private static (double Sine, double Cosine) BinaryAngle(float a)
    {
        var angle = (ushort)BitConverter.SingleToUInt32Bits(a);
        var halfTurns = angle / 32768.0;
        return (double.SinPi(halfTurns), double.CosPi(halfTurns));
    }
}
