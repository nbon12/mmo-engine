using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Tests.Math;

/// <summary>
/// <see cref="Vec3"/> computes every operation in float with the contract's parenthesisation, so
/// every build rounds the same way: <c>Dot</c> is <c>((a.X*b.X) + (a.Y*b.Y)) + (a.Z*b.Z)</c>, each
/// <c>Cross</c> component is <c>(a.Y*b.Z) - (a.Z*b.Y)</c> and so on, and <c>Length</c> is
/// <c>SimMath.Sqrt(Dot(a, a))</c>. Assertions compare bits through
/// <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </summary>
public sealed class Vec3Tests
{
    private const uint PlusZero = 0x00000000u;
    private const uint One = 0x3F800000u;
    private const uint MinusOne = 0xBF800000u;

    /// <summary>
    /// Rows <c>(case, equal, hash of the first, hash of the second)</c> for
    /// <see cref="EqualityAndHashFollowTheComponentBits"/>. A hash is
    /// <c>(int)(x ^ (y·0x9E3779B1) ^ (z·0x85EBCA77))</c> over the component bits, wrapping: (1.5, −2.25, 8)
    /// has bits (0x3FC00000, 0xC0100000, 0x41000000) and hash 1406140416, and (1.5, −2.25, 9) has z bits
    /// 0x41100000 and hash −1163919360.
    /// </summary>
    public static TheoryData<string, bool, int, int> EqualityCases => new()
    {
        { "same-bits", true, 1406140416, 1406140416 },
        { "another-component", false, 1406140416, -1163919360 },
        { "negative-zero", false, 0, int.MinValue },
        { "same-nan", true, 0x7FC00000, 0x7FC00000 },
        { "boxed-string", false, 1406140416, 0 },
    };

    [Fact]
    public void OperationsUseTheContractsParenthesisation()
    {
        // Dot associates left to right: (1 + 1e8) + (-1e8) is 0 in float, where 1 + (1e8 + (-1e8))
        // would be 1.
        var dot = Vec3.Dot(new Vec3(1f, 1e8f, -1e8f), new Vec3(1f, 1f, 1f));
        Assert.True(Bits(dot) == PlusZero, $"Dot((1, 1e8, -1e8), (1, 1, 1)) is 0x{Bits(dot):X8}, expected 0x00000000");

        AssertBits("Cross((1, 0, 0), (0, 1, 0))", Vec3.Cross(new Vec3(1f, 0f, 0f), new Vec3(0f, 1f, 0f)), PlusZero, PlusZero, One);
        AssertBits("Cross((0, 1, 0), (1, 0, 0))", Vec3.Cross(new Vec3(0f, 1f, 0f), new Vec3(1f, 0f, 0f)), PlusZero, PlusZero, MinusOne);

        var v = new Vec3(3f, 4f, 0f);
        var length = Vec3.Length(v);
        Assert.True(Bits(length) == Bits(5f), $"Length((3, 4, 0)) is 0x{Bits(length):X8}, expected exactly 5 (0x{Bits(5f):X8})");
        Assert.True(Bits(length) == Bits(SimMath.Sqrt(Vec3.Dot(v, v))), $"Length((3, 4, 0)) is 0x{Bits(length):X8}, not SimMath.Sqrt(Dot(a, a))");

        // Per component and exact: each component is the one float operation on that component, and
        // every result below is representable, so there is no rounding to hide a mix-up.
        var a = new Vec3(1.5f, -2.25f, 8f);
        var b = new Vec3(0.25f, 4f, -0.5f);
        AssertBits("(1.5, -2.25, 8) + (0.25, 4, -0.5)", a + b, Bits(1.75f), Bits(1.75f), Bits(7.5f));
        AssertBits("(1.5, -2.25, 8) - (0.25, 4, -0.5)", a - b, Bits(1.25f), Bits(-6.25f), Bits(8.5f));
        AssertBits("(1.5, -2.25, 8) * 2", a * 2f, Bits(3f), Bits(-4.5f), Bits(16f));
    }

    /// <summary>
    /// <see cref="Vec3"/>'s equality and hash use the components' bits (data-model.md#conventions, the
    /// rule the canonical state applies), through the boxed <c>Equals(object)</c> too: Given
    /// (<c>same-bits</c>) two copies of (1.5, −2.25, 8), (<c>another-component</c>) that and (1.5, −2.25,
    /// 9), (<c>negative-zero</c>) (+0, 0, 0) and (−0, 0, 0), (<c>same-nan</c>) two (NaN 0x7FC00000, 0, 0),
    /// or (<c>boxed-string</c>) (1.5, −2.25, 8) and its own <c>ToString()</c> text, When they are compared
    /// boxed and their hashes taken, Then <c>Equals(object)</c> is exactly the row's <c>equal</c>, and each
    /// <c>GetHashCode()</c> is exactly the row's value (no per-process seed); the string row's hash is not
    /// compared.
    /// </summary>
    [Theory]
    [MemberData(nameof(EqualityCases))]
    public void EqualityAndHashFollowTheComponentBits(string caseName, bool equal, int firstHash, int secondHash)
    {
        var nan = BitConverter.UInt32BitsToSingle(0x7FC00000u);
        var (first, second) = caseName switch
        {
            "same-bits" => (new Vec3(1.5f, -2.25f, 8f), (object)new Vec3(1.5f, -2.25f, 8f)),
            "another-component" => (new Vec3(1.5f, -2.25f, 8f), new Vec3(1.5f, -2.25f, 9f)),
            "negative-zero" => (new Vec3(0f, 0f, 0f), new Vec3(-0f, 0f, 0f)),
            "same-nan" => (new Vec3(nan, 0f, 0f), new Vec3(nan, 0f, 0f)),
            "boxed-string" => (new Vec3(1.5f, -2.25f, 8f), "(1.5, -2.25, 8)"),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
        };

        Assert.Equal(equal, first.Equals(second));
        Assert.Equal(firstHash, first.GetHashCode());
        if (second is Vec3 vector)
        {
            Assert.Equal(equal, vector.Equals((object)first));
            Assert.Equal(secondHash, vector.GetHashCode());
        }
        else
        {
            Assert.Equal((string)second, first.ToString());
        }
    }

    private static void AssertBits(string label, Vec3 actual, uint x, uint y, uint z) =>
        Assert.True(
            Bits(actual.X) == x && Bits(actual.Y) == y && Bits(actual.Z) == z,
            $"{label} is (0x{Bits(actual.X):X8}, 0x{Bits(actual.Y):X8}, 0x{Bits(actual.Z):X8}), expected (0x{x:X8}, 0x{y:X8}, 0x{z:X8})");

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
