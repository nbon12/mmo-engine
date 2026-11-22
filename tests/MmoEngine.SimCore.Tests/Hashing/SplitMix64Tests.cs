using MmoEngine.SimCore.Hashing;

namespace MmoEngine.SimCore.Tests.Hashing;

/// <summary>
/// The core's SplitMix64 (Steele, Lea and Flood; Vigna's constants), which <c>CrossBuildTables</c>,
/// <c>StreamGenerator</c> and Phase 6's <c>Spread</c> draw from (contracts/wasm-api.md#cross-build-tables,
/// contracts/simcore-cli.md#generated-streams, data-model.md#weapon-step). <c>Next</c> adds the golden
/// gamma <c>0x9E3779B97F4A7C15</c> to the state and returns the finaliser of the new state; <c>At</c> is
/// the counter-based form of the same sequence. The expected values are the generator's published
/// reference outputs, or the data-model.md finaliser written out again in the test, never a second call
/// into the core.
/// </summary>
public sealed class SplitMix64Tests
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;

    /// <summary>The first three outputs of SplitMix64 from state 0, the published reference sequence.</summary>
    private static readonly ulong[] ReferenceFromSeedZero =
    [
        0xE220A8397B1DCDAFUL,
        0x6E789E6AA1B965F4UL,
        0x06C45D188009454FUL,
    ];

    /// <summary>Rows (seed, counter): seeds {0, 0x5EED0001, 1000} crossed with counters {0, 1, 63}.</summary>
    public static TheoryData<ulong, ulong> SeedsAndCounters
    {
        get
        {
            var data = new TheoryData<ulong, ulong>();
            foreach (var seed in new ulong[] { 0UL, 0x5EED0001UL, 1000UL })
            {
                foreach (var counter in new ulong[] { 0UL, 1UL, 63UL })
                {
                    data.Add(seed, counter);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Given state 0, When <c>SplitMix64.Next(ref state)</c> is called three times, Then it returns
    /// <c>0xE220A8397B1DCDAF</c>, <c>0x6E789E6AA1B965F4</c> and <c>0x06C45D188009454F</c>, and the state
    /// advances by the gamma each call.
    /// </summary>
    [Fact]
    public void NextFromSeedZeroGivesTheReferenceSequence()
    {
        var state = 0UL;
        for (var i = 0; i < ReferenceFromSeedZero.Length; i++)
        {
            var value = SplitMix64.Next(ref state);

            Assert.True(
                value == ReferenceFromSeedZero[i],
                $"call {i + 1} from state 0 returned {value:X16}, expected {ReferenceFromSeedZero[i]:X16}");
            var expectedState = unchecked(Gamma * (ulong)(i + 1));
            Assert.True(
                state == expectedState,
                $"after call {i + 1} the state is {state:X16}, expected {expectedState:X16} ({i + 1} × the gamma)");
        }
    }

    /// <summary>
    /// <c>Mix</c> is the data-model.md finaliser: <c>Mix(0) == 0</c>, <c>Mix(gamma) == 0xE220A8397B1DCDAF</c>
    /// (the first reference output), and <c>Mix(0x0123456789ABCDEF)</c> equals the finaliser
    /// (<c>z ^= z &gt;&gt; 30; z *= 0xBF58476D1CE4E5B9; z ^= z &gt;&gt; 27; z *= 0x94D049BB133111EB;
    /// z ^= z &gt;&gt; 31</c>) computed in the test; the gamma is <c>SplitMix64.Gamma</c>.
    /// </summary>
    [Fact]
    public void MixIsTheFinaliserOfTheDataModel()
    {
        Assert.True(SplitMix64.Gamma == Gamma, $"SplitMix64.Gamma is {SplitMix64.Gamma:X16}, expected {Gamma:X16}");

        var zero = SplitMix64.Mix(0UL);
        Assert.True(zero == 0UL, $"Mix(0) is {zero:X16}, expected 0");

        var ofGamma = SplitMix64.Mix(Gamma);
        Assert.True(ofGamma == 0xE220A8397B1DCDAFUL, $"Mix({Gamma:X16}) is {ofGamma:X16}, expected E220A8397B1DCDAF");

        const ulong input = 0x0123456789ABCDEFUL;
        var mixed = SplitMix64.Mix(input);
        var expected = Finaliser(input);
        Assert.True(mixed == expected, $"Mix({input:X16}) is {mixed:X16}; the data-model.md finaliser gives {expected:X16}");
    }

    /// <summary>
    /// Given a seed and a counter, When <c>SplitMix64.At(seed, counter)</c> is evaluated, Then it equals
    /// the value of the (counter + 1)-th <c>Next</c> call from state <paramref name="seed"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(SeedsAndCounters))]
    public void AtIsTheCounterthValueOfNext(ulong seed, ulong counter)
    {
        var state = seed;
        var value = 0UL;
        for (var i = 0UL; i <= counter; i++)
        {
            value = SplitMix64.Next(ref state);
        }

        var at = SplitMix64.At(seed, counter);

        Assert.True(
            at == value,
            $"At(0x{seed:X}, {counter}) is {at:X16}; call {counter + 1} of Next from 0x{seed:X} returned {value:X16}");
    }

    /// <summary>data-model.md#weapon-step's finaliser, written out from the document.</summary>
    private static ulong Finaliser(ulong z)
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
}
