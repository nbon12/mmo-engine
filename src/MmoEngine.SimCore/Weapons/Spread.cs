using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Weapons;

/// <summary>
/// The stateless seeded spread (FR-008, specs/015-shared-simulation-core/data-model.md#weapon-step, One
/// shot steps 2 and 3, research R10). A shot's direction is a pure function of the match salt, the
/// shooter's ID, the weapon slot, the weapon's shot counter and the aim, so shots can be replayed in any
/// order and every build gives the same bits. The hash reuses <see cref="SplitMix64.Mix"/> as its
/// finaliser. The direction is uniform in solid angle inside the cone whose versine bound is the compiled
/// <c>VersMax</c>, written in float with the contract's parenthesisation and without fused multiply-add.
/// It allocates nothing and holds no state.
/// </summary>
public static class Spread
{
    /// <summary>2⁻²⁴, exactly: turns the hash's top 24 bits into a uniform in [0, 1).</summary>
    private const float TwoToMinus24 = 5.9604645E-08f;

    /// <summary>
    /// The spread hash: <c>Mix(Mix(matchSalt ^ characterId) ^ (((ulong)slot &lt;&lt; 32) | shotCounter))</c>
    /// with <see cref="SplitMix64.Mix"/> as the finaliser.
    /// </summary>
    /// <param name="matchSalt">The world's match salt.</param>
    /// <param name="characterId">The shooter's character ID.</param>
    /// <param name="slot">The weapon slot that fired.</param>
    /// <param name="shotCounter">The weapon's shot counter before this shot.</param>
    public static ulong Hash(ulong matchSalt, uint characterId, int slot, uint shotCounter) =>
        SplitMix64.Mix(SplitMix64.Mix(matchSalt ^ characterId) ^ (((ulong)slot << 32) | shotCounter));

    /// <summary>
    /// True when the recoil yaw kick of the shot with hash <paramref name="hash"/> is positive: bit 16 of
    /// the hash is 0.
    /// </summary>
    public static bool KicksYawPositive(ulong hash) => (hash & 0x1_0000UL) == 0UL;

    /// <summary>
    /// The shot direction for <paramref name="hash"/> around the aim (<paramref name="yaw"/>,
    /// <paramref name="pitch"/>), a unit vector inside the cone of versine bound
    /// <paramref name="versMax"/>: u1 = <c>(float)(uint)(hash &gt;&gt; 40) * 2⁻²⁴</c>, φ = <c>(ushort)hash</c>,
    /// <c>s = u1 * versMax</c>, <c>cosT = 1f - s</c>, <c>sinT = SimMath.Sqrt(s * (2f - s))</c>; with (sy, cy),
    /// (sp, cp) and (sφ, cφ) from <see cref="SimMath.SinCosBinaryAngle"/>, f = (cp·sy, sp, cp·cy),
    /// r = (−cy, 0, sy), u = (−sp·sy, cp, −sp·cy), and each component is
    /// <c>((cosT * f) + ((sinT * cφ) * r)) + ((sinT * sφ) * u)</c>.
    /// </summary>
    /// <param name="hash">The shot's <see cref="Hash"/>.</param>
    /// <param name="yaw">The aim yaw with recoil, a binary angle; yaw 0 faces +Z.</param>
    /// <param name="pitch">The aim pitch with recoil, a binary angle in [−16384, 16384].</param>
    /// <param name="versMax">The weapon's compiled versine bound, 2·sin²(θmax/2).</param>
    public static Vec3 Direction(ulong hash, ushort yaw, short pitch, float versMax)
    {
        // The top 24 bits go through uint: a ulong-to-float conversion is banned (MMO1009).
        var u1 = (float)(uint)(hash >> 40) * TwoToMinus24;
        var phi = unchecked((ushort)hash);

        var s = u1 * versMax;
        var cosT = 1f - s;
        var sinT = SimMath.Sqrt(s * (2f - s));

        SimMath.SinCosBinaryAngle(yaw, out var sy, out var cy);
        SimMath.SinCosBinaryAngle(unchecked((ushort)pitch), out var sp, out var cp);
        SimMath.SinCosBinaryAngle(phi, out var sinPhi, out var cosPhi);

        var a = sinT * cosPhi;
        var b = sinT * sinPhi;

        // f = (cp·sy, sp, cp·cy), r = (−cy, 0, sy), u = (−sp·sy, cp, −sp·cy).
        var fx = cp * sy;
        var fz = cp * cy;
        var ux = -sp * sy;
        var uz = -sp * cy;

        return new Vec3(
            ((cosT * fx) + (a * -cy)) + (b * ux),
            ((cosT * sp) + (a * 0f)) + (b * cp),
            ((cosT * fz) + (a * sy)) + (b * uz));
    }
}
