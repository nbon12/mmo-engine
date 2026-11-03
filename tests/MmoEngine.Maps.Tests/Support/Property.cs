using CsCheck;
using Xunit.Sdk;

namespace MmoEngine.Maps.Tests.Support;

/// <summary>
/// Runs a property over values drawn from a CsCheck generator with a fixed PCG stream and seed, so
/// every run sees the same values (constitution, Testing Standards: fixed seeds). A failure names
/// the seed, the iteration and the value.
/// </summary>
internal static class Property
{
    public static void ForAll<T>(Gen<T> gen, ulong seed, int iterations, Action<T> assert)
    {
        var pcg = new PCG(1, seed);
        for (var i = 0; i < iterations; i++)
        {
            var value = gen.Generate(pcg, null!, out _);
            try
            {
                assert(value);
            }
            catch (Exception ex)
            {
                throw new XunitException($"Property failed with seed {seed} at iteration {i} for {Check.Print(value)}: {ex.Message}", ex);
            }
        }
    }
}
