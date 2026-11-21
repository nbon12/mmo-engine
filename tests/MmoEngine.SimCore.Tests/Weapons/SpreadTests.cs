using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;

namespace MmoEngine.SimCore.Tests.Weapons;

/// <summary>
/// US4.2 and FR-008: the stateless seeded spread (data-model.md#weapon-step, One shot steps 2 and 3).
/// <c>Spread.Hash</c> is <c>Mix(Mix(matchSalt ^ characterId) ^ (((ulong)slot &lt;&lt; 32) | shotCounter))</c>
/// with the SplitMix64 finaliser, and <c>Spread.Direction</c> turns a hash and an aim into a unit
/// direction uniform in solid angle inside the cone whose versine bound is the compiled
/// <c>VersMax</c>. Expected values are computed independently here, the finaliser written out again and
/// the direction formula evaluated with <see cref="double"/> maths, never by calling a second core
/// method. The finaliser itself is pinned by <c>SplitMix64Tests</c>.
/// </summary>
public sealed class SpreadTests
{
    /// <summary>The salt of the distribution test, named in each of its assertion messages.</summary>
    private const ulong SpreadSalt = 0x0015_5EED_0000_0004UL;

    /// <summary>
    /// The χ² bound for 20 bins (19 degrees of freedom) at the 0.1% significance level. The 5% level
    /// (30.1) is not used: with one fixed salt a uniform hash misses it one time in twenty.
    /// </summary>
    private const double VersineChiSquareBound = 43.82;

    /// <summary>
    /// The χ² bound for 16 bins (15 degrees of freedom) at the 0.1% significance level. With
    /// <see cref="SpreadSalt"/> the data-model.md hash puts φ's 20,000 values in 16 bins with χ² ≈ 27.8,
    /// above the 5% level's 25.0, although the hash is uniform.
    /// </summary>
    private const double PhiChiSquareBound = 37.70;

    /// <summary>
    /// US4.2: given one salt, character ID, slot and shot counter, When the spread hash and the direction
    /// are computed twice, Then both calls give the same hash, equal to the data-model.md expression with
    /// the finaliser written in the test, and the same direction bits. Each component is within 1e-6 of
    /// the formula evaluated in <see cref="double"/> from the same hash, aim and <c>VersMax</c> (read from
    /// <c>CompiledConfig.Create</c> of <c>TestConfigs.At(30)</c> with <c>weapons[0].spreadHalfAngleDeg</c>
    /// set to the row's value). With spread 0 the direction is the aim axis itself, bit for bit:
    /// f = (cp·sy, sp, cp·cy) from <see cref="SimMath.SinCosBinaryAngle"/>.
    /// </summary>
    [Theory]
    [InlineData(0UL, 1U, 0, 0U, (ushort)0, (short)0, 1.5f)]
    [InlineData(0x0123456789ABCDEFUL, 7U, 1, 12345U, (ushort)16384, (short)2048, 0.5f)]
    [InlineData(0xFFFFFFFFFFFFFFFFUL, 0xFFFFFFFFU, 7, 0xFFFFFFFFU, (ushort)65535, (short)-16384, 45f)]
    [InlineData(42UL, 3U, 0, 1U, (ushort)32768, (short)16384, 1.5f)]
    [InlineData(0x5EEDUL, 2U, 1, 99U, (ushort)12345, (short)-100, 0f)]
    public void SameSaltPlayerAndCounterGiveTheSameDirectionBits(
        ulong salt,
        uint characterId,
        int slot,
        uint shotCounter,
        ushort yaw,
        short pitch,
        float spreadHalfAngleDeg)
    {
        var versMax = VersMaxFor(spreadHalfAngleDeg);
        var row = $"salt 0x{salt:X16}, character {characterId}, slot {slot}, counter {shotCounter}, yaw {yaw}, pitch {pitch}, spread {spreadHalfAngleDeg}°";

        var hash = Spread.Hash(salt, characterId, slot, shotCounter);
        var again = Spread.Hash(salt, characterId, slot, shotCounter);
        var expectedHash = Mix(Mix(salt ^ characterId) ^ (((ulong)slot << 32) | shotCounter));
        Assert.True(again == hash, $"{row}: the second Hash call returned 0x{again:X16}, the first 0x{hash:X16}");
        Assert.True(
            hash == expectedHash,
            $"{row}: Hash returned 0x{hash:X16}, expected Mix(Mix(salt ^ characterId) ^ ((slot << 32) | counter)) = 0x{expectedHash:X16}");

        var direction = Spread.Direction(hash, yaw, pitch, versMax);
        var repeated = Spread.Direction(hash, yaw, pitch, versMax);
        Assert.True(
            Bits(repeated) == Bits(direction),
            $"{row}: the second Direction call returned bits {Bits(repeated)}, the first {Bits(direction)}");

        var (x, y, z) = ReferenceDirection(hash, yaw, pitch, versMax);
        AssertWithin(x, direction.X, 1e-6, $"{row}: direction.X");
        AssertWithin(y, direction.Y, 1e-6, $"{row}: direction.Y");
        AssertWithin(z, direction.Z, 1e-6, $"{row}: direction.Z");

        if (spreadHalfAngleDeg == 0f)
        {
            Assert.True(versMax == 0f, $"{row}: VersMax is {versMax:R}, expected 0 for a 0° cone");
            SimMath.SinCosBinaryAngle(yaw, out var sy, out var cy);
            SimMath.SinCosBinaryAngle(unchecked((ushort)pitch), out var sp, out var cp);
            var axis = (
                BitConverter.SingleToUInt32Bits(cp * sy),
                BitConverter.SingleToUInt32Bits(sp),
                BitConverter.SingleToUInt32Bits(cp * cy));
            Assert.True(
                Bits(direction) == axis,
                $"{row}: a 0° cone gave direction bits {Bits(direction)}, expected the aim axis f = (cp·sy, sp, cp·cy) bits {axis}");
        }
    }

    /// <summary>
    /// US4.2: given base inputs (salt 0x0123456789ABCDEF, character 7, slot 0, yaw 12345, pitch 2048, the
    /// rifle's VersMax at 1.5°), When one input (the row's) is changed by +1, Then for every shot counter
    /// 0..999 the hash differs from the base hash and the direction differs from the base direction in
    /// at least one component's bits.
    /// </summary>
    [Theory]
    [InlineData("shotCounter")]
    [InlineData("salt")]
    [InlineData("characterId")]
    [InlineData("slot")]
    public void ADifferentShotCounterChangesTheDirection(string changed)
    {
        const ulong salt = 0x0123456789ABCDEFUL;
        const uint characterId = 7;
        const int slot = 0;
        const ushort yaw = 12345;
        const short pitch = 2048;
        var versMax = VersMaxFor(1.5f);

        for (var counter = 0u; counter < 1000u; counter++)
        {
            var baseHash = Spread.Hash(salt, characterId, slot, counter);
            var changedHash = changed switch
            {
                "shotCounter" => Spread.Hash(salt, characterId, slot, counter + 1u),
                "salt" => Spread.Hash(salt + 1UL, characterId, slot, counter),
                "characterId" => Spread.Hash(salt, characterId + 1u, slot, counter),
                "slot" => Spread.Hash(salt, characterId, slot + 1, counter),
                _ => throw new ArgumentOutOfRangeException(nameof(changed), changed, "Not a row of this test."),
            };
            Assert.True(
                changedHash != baseHash,
                $"counter {counter}: {changed} + 1 gave the base hash 0x{baseHash:X16} again");

            var baseBits = Bits(Spread.Direction(baseHash, yaw, pitch, versMax));
            var changedBits = Bits(Spread.Direction(changedHash, yaw, pitch, versMax));
            Assert.True(
                changedBits != baseBits,
                $"counter {counter}: {changed} + 1 gave the base direction bits {baseBits} again");
        }
    }

    /// <summary>
    /// US4.2 and FR-008: given <see cref="SpreadSalt"/>, character 1, slot 0, yaw 12345, pitch 2048 and the
    /// 1.5° cone, When the directions of shot counters 0..19,999 are computed, Then every direction is a
    /// unit vector within 1e-6, lies inside the cone (its angle to the aim axis f is at most
    /// 1.5°·π/180·(1 + 1e-4)), and the directions are uniform in solid angle: the versine
    /// (1 − dir·f)/VersMax in 20 equal bins over [0, 1) and φ = atan2(dir·u, dir·r) in 16 equal bins
    /// over [0, 2π) both pass a χ² test at the 0.1% level. f, r = (−cy, 0, sy) and
    /// u = (−sp·sy, cp, −sp·cy) are evaluated in <see cref="double"/>. The angle is the angle between the
    /// two vectors, the acos of the double dot product divided by |dir|, so that the float direction's
    /// length error (bounded by the first assertion) is not read as an angle at the cone's edge.
    /// </summary>
    [Fact]
    public void DirectionsFallInsideTheConeWithAUniformDistribution()
    {
        const int count = 20_000;
        const int versineBinCount = 20;
        const int phiBinCount = 16;
        const ushort yaw = 12345;
        const short pitch = 2048;
        var versMax = VersMaxFor(1.5f);
        var maxAngle = ((1.5 * double.Pi) / 180.0) * (1.0 + 1e-4);

        var sy = double.SinPi(yaw / 32768.0);
        var cy = double.CosPi(yaw / 32768.0);
        var sp = double.SinPi(pitch / 32768.0);
        var cp = double.CosPi(pitch / 32768.0);
        var f = (X: cp * sy, Y: sp, Z: cp * cy);
        var r = (X: -cy, Y: 0.0, Z: sy);
        var u = (X: -sp * sy, Y: cp, Z: -sp * cy);

        var versineBins = new int[versineBinCount];
        var phiBins = new int[phiBinCount];
        var worstLengthError = 0.0;
        var worstLengthCounter = 0u;
        var worstAngle = 0.0;
        var worstAngleCounter = 0u;
        for (var counter = 0u; counter < count; counter++)
        {
            var direction = Spread.Direction(Spread.Hash(SpreadSalt, 1u, 0, counter), yaw, pitch, versMax);
            var d = (X: (double)direction.X, Y: (double)direction.Y, Z: (double)direction.Z);

            var length = double.Sqrt((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z));
            if (double.Abs(length - 1.0) > worstLengthError || double.IsNaN(length))
            {
                worstLengthError = double.IsNaN(length) ? double.PositiveInfinity : double.Abs(length - 1.0);
                worstLengthCounter = counter;
            }

            var dot = Dot(d, f);
            var angle = double.Acos(double.Clamp(dot / length, -1.0, 1.0));
            if (angle > worstAngle || double.IsNaN(angle))
            {
                worstAngle = double.IsNaN(angle) ? double.PositiveInfinity : angle;
                worstAngleCounter = counter;
            }

            var versine = (1.0 - dot) / versMax;
            versineBins[BinOf(versine, versineBinCount)]++;

            var phi = double.Atan2(Dot(d, u), Dot(d, r));
            if (phi < 0.0)
            {
                phi += 2.0 * double.Pi;
            }

            phiBins[BinOf(phi / (2.0 * double.Pi), phiBinCount)]++;
        }

        var salt = $"SpreadSalt 0x{SpreadSalt:X16}";
        Assert.True(
            worstLengthError <= 1e-6,
            $"{salt}: counter {worstLengthCounter} gave |dir| off 1 by {worstLengthError:R}, more than 1e-6");
        Assert.True(
            worstAngle <= maxAngle,
            $"{salt}: counter {worstAngleCounter} lies {worstAngle:R} rad from the aim axis, outside the cone bound {maxAngle:R} rad");

        var versineChiSquare = ChiSquare(versineBins, count);
        Assert.True(
            versineChiSquare < VersineChiSquareBound,
            $"{salt}: the versine bins [{string.Join(", ", versineBins)}] give χ² {versineChiSquare:R}, not below {VersineChiSquareBound}");
        var phiChiSquare = ChiSquare(phiBins, count);
        Assert.True(
            phiChiSquare < PhiChiSquareBound,
            $"{salt}: the φ bins [{string.Join(", ", phiBins)}] give χ² {phiChiSquare:R}, not below {PhiChiSquareBound}");
    }

    /// <summary>The SplitMix64 finaliser of data-model.md#weapon-step, written out again.</summary>
    private static ulong Mix(ulong z)
    {
        unchecked
        {
            z ^= z >> 30;
            z *= 0xBF58476D1CE4E5B9UL;
            z ^= z >> 27;
            z *= 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return z;
        }
    }

    /// <summary>
    /// <c>VersMax</c> of slot 0 from <c>CompiledConfig.Create</c> of <c>TestConfigs.At(30)</c> with
    /// <c>weapons[0].spreadHalfAngleDeg</c> set to <paramref name="spreadHalfAngleDeg"/>.
    /// </summary>
    private static float VersMaxFor(float spreadHalfAngleDeg)
    {
        var config = TestConfigs.At(30);
        config.Weapons![0]!.SpreadHalfAngleDeg = spreadHalfAngleDeg;
        return CompiledConfig.Create(config).Weapons[0].VersMax;
    }

    /// <summary>
    /// data-model.md's direction in <see cref="double"/>: u1 = (h >> 40) / 2²⁴, s = u1·VersMax,
    /// cosθ = 1 − s, sinθ = sqrt(s(2 − s)), φ = (ushort)h·π/32768, the aim basis from
    /// <see cref="double.SinPi"/> and <see cref="double.CosPi"/> of yaw/32768 and pitch/32768, and
    /// dir = cosθ·f + sinθ·cosφ·r + sinθ·sinφ·u.
    /// </summary>
    private static (double X, double Y, double Z) ReferenceDirection(ulong hash, ushort yaw, short pitch, float versMax)
    {
        var u1 = (hash >> 40) / 16777216.0;
        var s = u1 * versMax;
        var cosT = 1.0 - s;
        var sinT = double.Sqrt(s * (2.0 - s));
        var phiTurns = unchecked((ushort)hash) / 32768.0;
        var sinPhi = double.SinPi(phiTurns);
        var cosPhi = double.CosPi(phiTurns);

        var sy = double.SinPi(yaw / 32768.0);
        var cy = double.CosPi(yaw / 32768.0);
        var sp = double.SinPi(pitch / 32768.0);
        var cp = double.CosPi(pitch / 32768.0);
        var f = (X: cp * sy, Y: sp, Z: cp * cy);
        var r = (X: -cy, Y: 0.0, Z: sy);
        var u = (X: -sp * sy, Y: cp, Z: -sp * cy);

        var a = sinT * cosPhi;
        var b = sinT * sinPhi;
        return (
            (cosT * f.X) + (a * r.X) + (b * u.X),
            (cosT * f.Y) + (a * r.Y) + (b * u.Y),
            (cosT * f.Z) + (a * r.Z) + (b * u.Z));
    }

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>The bin of <paramref name="unit"/> in [0, 1) split into <paramref name="bins"/>, rounding error clamped to the end bins.</summary>
    private static int BinOf(double unit, int bins) => int.Clamp((int)double.Floor(unit * bins), 0, bins - 1);

    private static double ChiSquare(int[] bins, int count)
    {
        var expected = (double)count / bins.Length;
        var sum = 0.0;
        foreach (var observed in bins)
        {
            var difference = observed - expected;
            sum += (difference * difference) / expected;
        }

        return sum;
    }

    private static void AssertWithin(double expected, float actual, double tolerance, string what) =>
        Assert.True(
            double.Abs(actual - expected) <= tolerance,
            $"{what} is {actual:R}, expected {expected:R} within {tolerance}");

    private static (uint X, uint Y, uint Z) Bits(Vec3 v) => (
        BitConverter.SingleToUInt32Bits(v.X),
        BitConverter.SingleToUInt32Bits(v.Y),
        BitConverter.SingleToUInt32Bits(v.Z));
}
