using System.Buffers.Binary;
using System.IO.Hashing;
using CsCheck;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Tests.Support;
using Xunit.Sdk;

namespace MmoEngine.SimCore.Tests.Properties;

/// <summary>
/// The decoders never give a partial result (FR-020, US6 scenario 3): every committed valid golden fixture
/// is truncated or corrupted at random, and its decoder either returns a whole value, one whose
/// re-encoding is exactly the changed bytes, or refuses: <c>InputCodec.TryDecode</c> returns false with a
/// <c>default</c> command, and <c>CollisionFile.Decode</c> and <c>RecordedStream.Decode</c> throw
/// <see cref="CoreFormatException"/>. Any other exception (an <see cref="IndexOutOfRangeException"/>,
/// <see cref="ArgumentOutOfRangeException"/>, <see cref="OverflowException"/>,
/// <see cref="OutOfMemoryException"/>, <see cref="NullReferenceException"/> or anything else) fails the
/// property, naming the seed and the changed bytes' length.
/// </summary>
/// <remarks>
/// <para>
/// Each case runs <see cref="Iterations"/> iterations. Each draws one change to the fixture's
/// <c>.bin</c>: a truncation to a length in [0, length); one to four byte flips, each an offset and a
/// non-zero XOR; or, for the collision and stream formats, which end in an XxHash64 trailer, the same
/// flips followed by the trailer rewritten as the XxHash64 (seed 0) of every byte before it, so the
/// decoder gets past the checksum to the value checks.
/// </para>
/// <para>
/// CsCheck draws every iteration after the first from a fresh random seed, so each iteration is its own
/// <c>Sample</c> of one draw on one thread, from the seed <c>new PCG(i, Seed)</c> for iteration i. The
/// property is reproducible from <see cref="Seed"/> alone, and every failure message names
/// <see cref="Seed"/>, the iteration, its CsCheck seed, the change and the changed bytes' length.
/// </para>
/// <para>
/// The fixtures are read once per case with <see cref="GoldenFixtures.ReadBin"/>, read-only, and every
/// change works on a copy, so the test is parallel-safe and leaves <c>simcore/</c> untouched.
/// </para>
/// </remarks>
public sealed class DecoderPropertyTests
{
    /// <summary>The property's fixed seed; iteration i samples from <c>new PCG(i, Seed)</c>.</summary>
    private const ulong Seed = 0x0015_0226_0000_0001UL;

    /// <summary>The iterations per case.</summary>
    private const int Iterations = 1_000;

    /// <summary>The most byte flips one change makes.</summary>
    private const int MaxFlips = 4;

    /// <summary>The length of the collision and stream trailers: a u64 XxHash64.</summary>
    private const int TrailerLength = 8;

    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>
    /// <b>Given</b> each committed valid fixture, <b>when</b> it is truncated, flipped, or flipped with its
    /// trailer rewritten, <b>then</b> its decoder returns a value that re-encodes to exactly the changed
    /// bytes, or refuses with no value, and throws nothing but <see cref="CoreFormatException"/>. Across the
    /// iterations at least one change must decode and at least one must be refused, so both outcomes are
    /// exercised.
    /// </summary>
    [Theory]
    [MemberData(nameof(GoldenFixtures.ValidCases), MemberType = typeof(GoldenFixtures))]
    public void TruncatedOrCorruptedFixturesNeverYieldAPartialResult(string format, string caseName)
    {
        var valid = GoldenFixtures.ReadBin(format, caseName);
        Assert.True(valid.Length > 0, $"{format}/{caseName}.bin is empty");
        var changes = Changes(valid.Length, hasTrailer: format != InputFormat);
        var accepted = 0;
        var refused = 0;

        for (var i = 0; i < Iterations; i++)
        {
            var iterationSeed = new PCG((uint)i, Seed).ToString();
            var context = $"seed 0x{Seed:X16}, iteration {i} of {Iterations} (CsCheck seed {iterationSeed}), {format}/{caseName}";
            changes.Sample(
                change =>
                {
                    var mutated = change.Apply(valid);
                    if (DecodesWhole(format, mutated, $"{context}, {change}, {mutated.Length} bytes"))
                    {
                        accepted++;
                    }
                    else
                    {
                        refused++;
                    }
                },
                seed: iterationSeed,
                iter: 1,
                threads: 1,
                print: change => $"{change} ({context})");
        }

        Assert.True(refused > 0, $"seed 0x{Seed:X16}, {format}/{caseName}: no change was refused, so the refusal path was not exercised");
        Assert.True(accepted > 0, $"seed 0x{Seed:X16}, {format}/{caseName}: no change decoded, so the re-encoding check was not exercised");
    }

    /// <summary>
    /// One change to a fixture of <paramref name="length"/> bytes: a truncation, flips, or (with a trailer)
    /// flips and a rewritten trailer, each kind equally likely.
    /// </summary>
    private static Gen<Change> Changes(int length, bool hasTrailer)
    {
        var truncation = Gen.Select(Gen.Int[0, length - 1], kept => new Change(ChangeKind.Truncation, kept, []));
        var flips = Gen.Select(Gen.Int[0, length - 1], Gen.Byte[1, byte.MaxValue], (offset, xor) => new Flip(offset, xor)).Array[1, MaxFlips];
        var flipped = Gen.Select(flips, chosen => new Change(ChangeKind.Flips, length, chosen));
        if (!hasTrailer)
        {
            return Gen.OneOf<Change>(truncation, flipped);
        }

        var rehashed = Gen.Select(flips, chosen => new Change(ChangeKind.FlipsThenTrailer, length, chosen));
        return Gen.OneOf<Change>(truncation, flipped, rehashed);
    }

    /// <summary>
    /// Decodes <paramref name="mutated"/> with <paramref name="format"/>'s decoder. Returns true when it gave
    /// a value, after asserting that the value re-encodes to exactly <paramref name="mutated"/>, and false
    /// when it refused, after asserting that the refusal left no value. Any exception other than the
    /// decoder's <see cref="CoreFormatException"/> fails the property.
    /// </summary>
    private static bool DecodesWhole(string format, byte[] mutated, string context) => format switch
    {
        InputFormat => InputDecodesWhole(mutated, context),
        CollisionFormat => CollisionDecodesWhole(mutated, context),
        StreamFormat => StreamDecodesWhole(mutated, context),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown golden format."),
    };

    private static bool InputDecodesWhole(byte[] mutated, string context)
    {
        bool decoded;
        InputCommand command;
        FormatError error;
        try
        {
            decoded = InputCodec.TryDecode(mutated, out command, out error);
        }
        catch (Exception exception)
        {
            throw Unexpected(context, "InputCodec.TryDecode", exception);
        }

        if (!decoded)
        {
            Assert.True(command == default, $"{context}: TryDecode refused ({error}) but gave the command {command}, not default");
            Assert.True(error.Format == InputFormat, $"{context}: TryDecode refused with format '{error.Format}', not '{InputFormat}' ({error})");
            return false;
        }

        var reencoded = new byte[InputCodec.EncodedSize];
        Assert.True(InputCodec.TryEncode(command, reencoded), $"{context}: TryEncode refused the decoded command {command}");
        AssertSameBytes(mutated, reencoded, context);
        return true;
    }

    private static bool CollisionDecodesWhole(byte[] mutated, string context)
    {
        CollisionWorld world;
        try
        {
            world = CollisionFile.Decode(mutated);
        }
        catch (CoreFormatException refusal)
        {
            AssertOwnRefusal(CollisionFormat, refusal, context);
            return false;
        }
        catch (Exception exception)
        {
            throw Unexpected(context, "CollisionFile.Decode", exception);
        }

        byte[] reencoded;
        try
        {
            reencoded = CollisionFile.Encode(world.Data);
        }
        catch (Exception exception)
        {
            throw new XunitException($"{context}: CollisionFile.Decode returned a world, and CollisionFile.Encode(world.Data) threw {exception.GetType().Name}: {exception.Message}", exception);
        }

        AssertSameBytes(mutated, reencoded, context);
        return true;
    }

    private static bool StreamDecodesWhole(byte[] mutated, string context)
    {
        RecordedStream stream;
        try
        {
            stream = RecordedStream.Decode(mutated);
        }
        catch (CoreFormatException refusal)
        {
            AssertOwnRefusal(StreamFormat, refusal, context);
            return false;
        }
        catch (Exception exception)
        {
            throw Unexpected(context, "RecordedStream.Decode", exception);
        }

        byte[] reencoded;
        try
        {
            reencoded = stream.Encode();
        }
        catch (Exception exception)
        {
            throw new XunitException($"{context}: RecordedStream.Decode returned a stream, and its Encode() threw {exception.GetType().Name}: {exception.Message}", exception);
        }

        AssertSameBytes(mutated, reencoded, context);
        return true;
    }

    /// <summary>A refusal names its own format, and its message is its error's text.</summary>
    private static void AssertOwnRefusal(string format, CoreFormatException refusal, string context)
    {
        Assert.True(refusal.Error.Format == format, $"{context}: refused with format '{refusal.Error.Format}', not '{format}' ({refusal.Message})");
        Assert.True(refusal.Message == refusal.Error.ToString(), $"{context}: the exception's message '{refusal.Message}' is not its error's text '{refusal.Error}'");
    }

    /// <summary>The property's failure for an exception that is not a refusal: the seed, the change and the changed bytes' length are in <paramref name="context"/>.</summary>
    private static XunitException Unexpected(string context, string decoder, Exception exception) =>
        new($"{context}: {decoder} threw {exception.GetType().FullName} instead of refusing with CoreFormatException: {exception.Message}", exception);

    private static void AssertSameBytes(byte[] expected, byte[] actual, string context)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return;
        }

        var length = expected.Length < actual.Length ? expected.Length : actual.Length;
        var first = 0;
        while (first < length && expected[first] == actual[first])
        {
            first++;
        }

        Assert.Fail($"{context}: the decoded value re-encodes to {actual.Length} bytes that differ from the {expected.Length} decoded bytes, first at offset {first}, so the decoder gave a partial or altered result");
    }

    /// <summary>The kinds of change: a truncation, flips, and flips with the trailer rewritten.</summary>
    private enum ChangeKind
    {
        Truncation,
        Flips,
        FlipsThenTrailer,
    }

    /// <summary>One byte flip: the byte at <paramref name="Offset"/> XORed with the non-zero <paramref name="Xor"/>.</summary>
    private readonly record struct Flip(int Offset, byte Xor);

    /// <summary>
    /// One change to a fixture: a truncation to <paramref name="Length"/> bytes, or <paramref name="Flips"/>
    /// applied in order to a copy of all <paramref name="Length"/> bytes, then for
    /// <see cref="ChangeKind.FlipsThenTrailer"/> the last 8 bytes rewritten as the XxHash64 (seed 0) of
    /// every byte before them, little-endian.
    /// </summary>
    private sealed record Change(ChangeKind Kind, int Length, Flip[] Flips)
    {
        public byte[] Apply(byte[] valid)
        {
            if (Kind == ChangeKind.Truncation)
            {
                return valid.AsSpan(0, Length).ToArray();
            }

            var copy = (byte[])valid.Clone();
            foreach (var flip in Flips)
            {
                copy[flip.Offset] ^= flip.Xor;
            }

            if (Kind == ChangeKind.FlipsThenTrailer)
            {
                var trailer = copy.Length - TrailerLength;
                BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(trailer), XxHash64.HashToUInt64(copy.AsSpan(0, trailer), 0));
            }

            return copy;
        }

        public override string ToString()
        {
            var flips = string.Join(", ", Flips.Select(flip => $"byte {flip.Offset} ^ 0x{flip.Xor:X2}"));
            return Kind switch
            {
                ChangeKind.Truncation => $"truncation to {Length} bytes",
                ChangeKind.Flips => $"flips [{flips}]",
                _ => $"flips [{flips}] then the trailer rewritten",
            };
        }
    }
}
