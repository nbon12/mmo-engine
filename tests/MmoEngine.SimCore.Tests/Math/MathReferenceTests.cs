using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Math;

/// <summary>
/// The double reference the trigonometric bound tests measure against (US3.8a's bound half, FR-013),
/// per research.md R3 and contracts/wasm-api.md#cross-build-tables. <see cref="MathReference"/> is test
/// code, so these tests guard the bound tests against vacuity: an ulp error measured in the wrong binade,
/// a NaN that compares as "within bound", or a reference computed in float instead of double would let
/// every bound pass. <c>System.Math</c> is written in full, because <c>MmoEngine.SimCore.Math</c>
/// shadows it.
/// </summary>
public sealed class MathReferenceTests
{
    /// <summary>
    /// A float result's bits, the double reference and the expected error: the spacing of floats in the
    /// reference's binade (so one ulp below 1 is half an ulp of 1), the subnormal spacing 2^-149 near
    /// zero, an exact zero only for a signed zero, and +∞ for a NaN result against a finite reference.
    /// </summary>
    public static TheoryData<uint, double, double> UlpErrorRows => new()
    {
        { Bits(1f), 1.0, 0.0 },
        { Bits(float.BitIncrement(1f)), 1.0, 1.0 },
        { Bits(float.BitDecrement(1f)), 1.0, 0.5 },
        { Bits(1f), 1.0 + System.Math.ScaleB(1.0, -24), 0.5 },
        { 0x00000001u, System.Math.ScaleB(1.0, -148), 1.0 },
        { 0x00000000u, 0.0, 0.0 },
        { 0x80000000u, 0.0, 0.0 },
        { Bits(float.Epsilon), 0.0, double.PositiveInfinity },
        { Bits(float.NaN), 1.0, double.PositiveInfinity },
    };

    /// <summary>
    /// A function ID of contracts/wasm-api.md#cross-build-tables, its a and b bits, and the two results
    /// the reference must give, as doubles: Sin and Atan2 (a is y, b is x) through <c>System.Math</c> in
    /// double on the float inputs, with 0 as the second result, and SinCosBinaryAngle at the four
    /// quadrant angles, whose sine and cosine are exact.
    /// </summary>
    public static TheoryData<int, uint, uint, double, double> ReferenceRows => new()
    {
        { 1, Bits(0.1f), 0u, System.Math.Sin((double)0.1f), 0.0 },
        { 1, Bits(-2.75f), 0u, System.Math.Sin(-2.75), 0.0 },
        { 8, Bits(1f), Bits(2f), System.Math.Atan2(1.0, 2.0), 0.0 },
        { 8, Bits(-3f), Bits(-0.5f), System.Math.Atan2(-3.0, -0.5), 0.0 },
        { 9, 0x0000u, 0u, 0.0, 1.0 },
        { 9, 0x4000u, 0u, 1.0, 0.0 },
        { 9, 0x8000u, 0u, 0.0, -1.0 },
        { 9, 0xC000u, 0u, -1.0, 0.0 },
    };

    /// <summary>
    /// <see cref="MathReference.UlpError"/> measures |result − reference| in ulps of the reference's own
    /// binade (<see cref="MathReference.Ulp"/>), is 0 against a zero reference only for ±0, and is +∞ for
    /// a nonzero result against a zero reference and for a NaN result against a finite one.
    /// </summary>
    [Theory]
    [MemberData(nameof(UlpErrorRows))]
    public void UlpErrorMeasuresInTheReferencesBinade(uint resultBits, double reference, double expected)
    {
        var result = BitConverter.UInt32BitsToSingle(resultBits);

        var error = MathReference.UlpError(result, reference);

        Assert.Equal(expected, error);
    }

    /// <summary>
    /// <see cref="MathReference.Reference"/> computes each function in double through
    /// <c>System.Math</c>, with Atan2's y from a and x from b, and SinCosBinaryAngle through
    /// <c>double.SinPi</c> and <c>double.CosPi</c> of the angle over 32768: at angles 0, 0x4000, 0x8000
    /// and 0xC000 the sine is 0, 1, 0 and −1 and the cosine 1, 0, −1 and 0. Both results compare exactly
    /// as doubles.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReferenceRows))]
    public void ReferenceUsesTheDoubleFunctions(int functionId, uint aBits, uint bBits, double expectedR0, double expectedR1)
    {
        var (r0, r1) = MathReference.Reference(functionId, BitConverter.UInt32BitsToSingle(aBits), BitConverter.UInt32BitsToSingle(bBits));

        Assert.Equal(expectedR0, r0);
        Assert.Equal(expectedR1, r1);
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
