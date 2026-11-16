using System;
using System.Globalization;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Geometry;

/// <summary>
/// A 3-vector of <see cref="float"/>s in the core's frame: right-handed, Y up, metres
/// (specs/015-shared-simulation-core/data-model.md#conventions). Every operation is written in float with
/// the contract's parenthesisation and without fused multiply-add, so every build rounds it the same way:
/// <see cref="Dot"/> is <c>((a.X*b.X) + (a.Y*b.Y)) + (a.Z*b.Z)</c>, each <see cref="Cross"/> component is
/// <c>(a.Y*b.Z) - (a.Z*b.Y)</c> and so on, and <see cref="Length"/> is
/// <c>SimMath.Sqrt(Dot(a, a))</c>.
/// </summary>
/// <remarks>
/// Two vectors are equal when their components have the same bits, so <c>-0</c> differs from <c>+0</c>
/// and a NaN equals itself: the same rule the canonical state and its hash apply.
/// </remarks>
public readonly struct Vec3(float X, float Y, float Z) : IEquatable<Vec3>
{
    /// <summary>The X component, in metres.</summary>
    public float X { get; } = X;

    /// <summary>The Y component, in metres; +Y is up.</summary>
    public float Y { get; } = Y;

    /// <summary>The Z component, in metres.</summary>
    public float Z { get; } = Z;

    /// <summary>The per-component sum.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>The per-component difference.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Each component times the scalar.</summary>
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    /// <summary>The scalar times each component; the same bits as <c>a * s</c>.</summary>
    public static Vec3 operator *(float s, Vec3 a) => new(s * a.X, s * a.Y, s * a.Z);

    /// <summary>The dot product, <c>((a.X*b.X) + (a.Y*b.Y)) + (a.Z*b.Z)</c>.</summary>
    public static float Dot(Vec3 a, Vec3 b) => ((a.X * b.X) + (a.Y * b.Y)) + (a.Z * b.Z);

    /// <summary>
    /// The cross product, <c>((a.Y*b.Z) - (a.Z*b.Y), (a.Z*b.X) - (a.X*b.Z), (a.X*b.Y) - (a.Y*b.X))</c>.
    /// </summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    /// <summary>The squared length, <c>Dot(a, a)</c>.</summary>
    public static float LengthSquared(Vec3 a) => Dot(a, a);

    /// <summary>The length, <c>SimMath.Sqrt(LengthSquared(a))</c>.</summary>
    public static float Length(Vec3 a) => SimMath.Sqrt(LengthSquared(a));

    /// <summary>True when every component of the two vectors has the same bits.</summary>
    public bool Equals(Vec3 other) =>
        BitConverter.SingleToUInt32Bits(X) == BitConverter.SingleToUInt32Bits(other.X)
        && BitConverter.SingleToUInt32Bits(Y) == BitConverter.SingleToUInt32Bits(other.Y)
        && BitConverter.SingleToUInt32Bits(Z) == BitConverter.SingleToUInt32Bits(other.Z);

    public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

    /// <summary>A hash of the component bits; it uses no per-process seed.</summary>
    public override int GetHashCode() => unchecked((int)(
        BitConverter.SingleToUInt32Bits(X)
        ^ (BitConverter.SingleToUInt32Bits(Y) * 0x9E3779B1u)
        ^ (BitConverter.SingleToUInt32Bits(Z) * 0x85EBCA77u)));

    /// <summary>The components in invariant round-trip form, <c>(x, y, z)</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({X:R}, {Y:R}, {Z:R})");
}
