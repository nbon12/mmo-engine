namespace MmoEngine.SimCore.Hashing;

/// <summary>
/// The core's SplitMix64 (Steele, Lea and Flood, with Vigna's constants): the one generator that
/// <c>CrossBuildTables</c>, <c>StreamGenerator</c> and the weapon spread draw from
/// (specs/015-shared-simulation-core/contracts/wasm-api.md#cross-build-tables,
/// contracts/simcore-cli.md#generated-streams, data-model.md#weapon-step). It is integer arithmetic only, so
/// every build returns the same values, and it allocates nothing.
/// </summary>
public static class SplitMix64
{
    /// <summary>The golden gamma that <see cref="Next"/> adds to the state each call.</summary>
    public const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// The data-model.md finaliser: <c>z ^= z &gt;&gt; 30; z *= 0xBF58476D1CE4E5B9; z ^= z &gt;&gt; 27;
    /// z *= 0x94D049BB133111EB; z ^= z &gt;&gt; 31</c>.
    /// </summary>
    public static ulong Mix(ulong z)
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

    /// <summary>Advances <paramref name="state"/> by <see cref="Gamma"/> and returns the finaliser of the new state.</summary>
    public static ulong Next(ref ulong state)
    {
        state = unchecked(state + Gamma);
        return Mix(state);
    }

    /// <summary>
    /// The counter-based form of the same sequence: the value of the (<paramref name="counter"/> + 1)-th
    /// <see cref="Next"/> call from state <paramref name="seed"/>.
    /// </summary>
    public static ulong At(ulong seed, ulong counter) => Mix(unchecked(seed + ((counter + 1UL) * Gamma)));
}
