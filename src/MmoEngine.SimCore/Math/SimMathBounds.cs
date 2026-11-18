namespace MmoEngine.SimCore.Math;

/// <summary>
/// The documented error bounds of <see cref="SimMath"/> (FR-013, SC-004, US3.8), in ulps of the
/// float result against the exact value, per specs/015-shared-simulation-core/research.md R3. The
/// measured maxima in each field's documentation come from exhaustive sweeps of the implementation
/// against <c>Math.*(double)</c> (and <c>double.SinPi</c>/<c>CosPi</c> for binary angles).
/// <c>Math/FixedTableBoundsTests</c> checks every in-domain row of the fixed table against these
/// constants on every pull request, and <c>Math/ExhaustiveSweepTests</c> checks the whole domains when
/// <c>MMO_EXHAUSTIVE_MATH=1</c>. A bound is never widened past R3's table; a row over its bound is
/// fixed in the algorithm or the coefficients.
/// </summary>
public static class SimMathBounds
{
    /// <summary>
    /// The largest |x| that Sin, Cos, SinCos and Tan reduce by the 4-part Cody–Waite split, where the
    /// small-argument bounds hold. Above it, the exact Payne–Hanek reduction runs and the
    /// large-argument bounds hold.
    /// </summary>
    public const float ReductionLimit = 12867f;

    /// <summary>
    /// Sin, Cos and SinCos for |x| ≤ <see cref="ReductionLimit"/>, swept exhaustively: at most 1 ulp.
    /// Measured maximum: 0.790 ulp for Sin and 0.799 ulp for Cos.
    /// </summary>
    public const float SinCosMaxUlp = 1f;

    /// <summary>
    /// Sin, Cos and SinCos for finite |x| &gt; <see cref="ReductionLimit"/> (9.6e8 floats), swept
    /// exhaustively: at most 2 ulp. Measured maximum: 0.804 ulp.
    /// </summary>
    public const float SinCosLargeMaxUlp = 2f;

    /// <summary>
    /// Tan for |x| ≤ <see cref="ReductionLimit"/>, swept exhaustively: at most 1.5 ulp. Measured
    /// maximum: 1.278 ulp.
    /// </summary>
    public const float TanMaxUlp = 1.5f;

    /// <summary>
    /// Tan for finite |x| &gt; <see cref="ReductionLimit"/> (9.6e8 floats), swept exhaustively: at most
    /// 2.5 ulp. Measured maximum: 1.317 ulp.
    /// </summary>
    public const float TanLargeMaxUlp = 2.5f;

    /// <summary>
    /// Asin and Acos on [−1, 1], swept exhaustively: at most 1 ulp. Measured maximum: 0.875 ulp for
    /// Asin and 0.902 ulp for Acos.
    /// </summary>
    public const float AsinAcosMaxUlp = 1f;

    /// <summary>Atan on every float, swept exhaustively: at most 1 ulp. Measured maximum: 0.851 ulp.</summary>
    public const float AtanMaxUlp = 1f;

    /// <summary>
    /// Atan2 over 2^22 directions at 5 radii, plus 64 million random pairs: at most 2 ulp. Measured
    /// maximum: 1.475 ulp.
    /// </summary>
    public const float Atan2MaxUlp = 2f;

    /// <summary>
    /// SinCosBinaryAngle on all 65,536 binary angles, swept exhaustively: at most 1 ulp. Measured
    /// maximum: 0.737 ulp.
    /// </summary>
    public const float SinCosBinaryAngleMaxUlp = 1f;

    /// <summary>
    /// Sqrt on every non-negative float: at most 0.5 ulp, because it is the correctly rounded IEEE 754
    /// square root (<c>MathF.Sqrt</c>), exact in every build.
    /// </summary>
    public const float SqrtMaxUlp = 0.5f;
}
