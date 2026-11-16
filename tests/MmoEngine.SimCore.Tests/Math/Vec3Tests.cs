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

    private static void AssertBits(string label, Vec3 actual, uint x, uint y, uint z) =>
        Assert.True(
            Bits(actual.X) == x && Bits(actual.Y) == y && Bits(actual.Z) == z,
            $"{label} is (0x{Bits(actual.X):X8}, 0x{Bits(actual.Y):X8}, 0x{Bits(actual.Z):X8}), expected (0x{x:X8}, 0x{y:X8}, 0x{z:X8})");

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
