using System.Buffers.Binary;
using System.IO.Hashing;
using MmoEngine.SimCore.Server.Golden;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The golden fixtures' mutations (FR-023, contracts/input-command-format.md#shared-fixture-rules, the
/// Mutations table): <c>GoldenMutations.Apply</c> turns a base case's <c>.bin</c> into a refusal case's
/// <c>.bin</c> by applying <c>truncate</c>, <c>append</c>, <c>write</c>, <c>insert</c>, <c>delete</c> and
/// <c>fixChecksum</c> in order, each to the bytes the ones before it left. A mutation whose arguments do not
/// fit those bytes is refused with a <see cref="GoldenCaseException"/> naming its index, so
/// <c>golden write</c> can say which mutation of which case is wrong.
/// </summary>
/// <remarks>
/// The expected bytes are literals from the contract's table, or, for <c>fixChecksum</c>, the trailer
/// computed here with <see cref="XxHash64"/> (seed 0). Nothing is read from <c>simcore/</c>.
/// </remarks>
public sealed class GoldenMutationsTests
{
    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>Every format: the byte ops apply to any of them.</summary>
    private static readonly string[] AllFormats = [InputFormat, CollisionFormat, StreamFormat];

    /// <summary>The formats with an XxHash64 trailer, the only ones <c>fixChecksum</c> applies to.</summary>
    private static readonly string[] TrailerFormats = [CollisionFormat, StreamFormat];

    /// <summary>The six-byte base of the byte ops: <c>00 01 02 03 04 05</c>.</summary>
    private static byte[] Base6 => [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

    /// <summary>The 16-byte base of <c>fixChecksum</c>: <c>00</c> to <c>0f</c>, so its last 8 bytes are not already a trailer.</summary>
    private static byte[] Base16 => [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    /// <summary>
    /// The Mutations table: given the base <c>00 01 02 03 04 05</c> (a 16-byte base for
    /// <c>fixChecksum</c>), when the one op is applied, then the result is exactly the contract's:
    /// <c>truncate 2</c> gives <c>00 01</c>; <c>append "ff"</c> gives <c>00 01 02 03 04 05 ff</c>;
    /// <c>write 1 "aabb"</c> gives <c>00 aa bb 03 04 05</c> (and the hex is read case-insensitively);
    /// <c>insert 2 "cc"</c> gives <c>00 01 cc 02 03 04 05</c>; <c>delete 1 2</c> gives <c>00 03 04 05</c>;
    /// and <c>fixChecksum</c>, on collision or stream bytes, sets the last 8 bytes to
    /// <c>XxHash64.HashToUInt64</c> of the first 8, little-endian. The byte ops give the same result for
    /// every format, and the base is left unchanged.
    /// </summary>
    [Theory]
    [InlineData("truncate")]
    [InlineData("append")]
    [InlineData("write")]
    [InlineData("insert")]
    [InlineData("delete")]
    [InlineData("fixChecksum")]
    public void EachOpEditsTheBytesAsTheContractSays(string op)
    {
        var row = OpRow(op);
        foreach (var format in row.Formats)
        {
            foreach (var (mutation, expected) in row.Cases)
            {
                var baseBytes = row.Base;
                var untouched = (byte[])baseBytes.Clone();

                var result = GoldenMutations.Apply(format, baseBytes, [mutation]);

                Assert.True(
                    result.AsSpan().SequenceEqual(expected),
                    $"{format}: {Describe(mutation)} on {Hex(untouched)} gave {Hex(result)}, expected {Hex(expected)}");
                Assert.True(baseBytes.AsSpan().SequenceEqual(untouched), $"{format}: {Describe(mutation)} changed its base to {Hex(baseBytes)}");
            }
        }
    }

    /// <summary>
    /// Mutations apply in order, each to the bytes the ones before it left: given the base
    /// <c>00 01 02 03 04 05</c>, <c>write 0 "ff"</c> then <c>truncate 1</c> gives <c>ff</c> (applying each to
    /// the base would give <c>00</c>), and <c>truncate 2</c> then <c>append "ff"</c> gives
    /// <c>00 01 ff</c> (the reverse order would give <c>00 01</c>).
    /// </summary>
    [Fact]
    public void OpsApplyInOrder()
    {
        GoldenMutation[] writeThenTruncate = [Write(0, "ff"), Truncate(1)];
        byte[] expectedFirst = [0xff];
        var first = GoldenMutations.Apply(InputFormat, Base6, writeThenTruncate);
        Assert.True(first.AsSpan().SequenceEqual(expectedFirst), $"write 0 \"ff\" then truncate 1 gave {Hex(first)}, expected ff");

        GoldenMutation[] truncateThenAppend = [Truncate(2), Append("ff")];
        byte[] expectedSecond = [0x00, 0x01, 0xff];
        var second = GoldenMutations.Apply(InputFormat, Base6, truncateThenAppend);
        Assert.True(second.AsSpan().SequenceEqual(expectedSecond), $"truncate 2 then append \"ff\" gave {Hex(second)}, expected 00 01 ff");
    }

    /// <summary>
    /// A mutation whose arguments do not fit is refused naming it: given a list whose mutation i is a
    /// <c>truncate</c> past the current length, a <c>write</c>, <c>insert</c> or <c>delete</c> past the
    /// current end, a <c>hex</c> of odd length or with a non-hex digit, an unknown <c>op</c>, or a
    /// <c>fixChecksum</c> on an input command or on fewer than 8 bytes, when the list is applied, then
    /// <c>GoldenMutations.Apply</c> throws <see cref="GoldenCaseException"/> whose message starts with
    /// <c>mutations[i]: </c> and goes on to name the problem. Most rows put a valid mutation first, and the
    /// lengths they check are the lengths the earlier mutations left, not the base's.
    /// </summary>
    [Theory]
    [InlineData("truncate-past-length")]
    [InlineData("write-past-end")]
    [InlineData("insert-past-end")]
    [InlineData("delete-past-end")]
    [InlineData("hex-odd-length")]
    [InlineData("hex-not-hex")]
    [InlineData("unknown-op")]
    [InlineData("fix-checksum-input")]
    [InlineData("fix-checksum-short")]
    public void BadArgumentsAreRefusedNamingTheMutation(string row)
    {
        var bad = BadRow(row);
        var prefix = $"mutations[{bad.FailingIndex}]: ";

        var thrown = Assert.Throws<GoldenCaseException>(() => GoldenMutations.Apply(bad.Format, bad.Base, bad.Mutations));

        Assert.StartsWith(prefix, thrown.Message, StringComparison.Ordinal);
        Assert.True(thrown.Message.Length > prefix.Length, $"{row}: the message '{thrown.Message}' names no problem after '{prefix}'");
    }

    private static OpCase OpRow(string op)
    {
        switch (op)
        {
            case "truncate":
                return new(AllFormats, Base6, [(Truncate(2), [0x00, 0x01])]);
            case "append":
                return new(AllFormats, Base6, [(Append("ff"), [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0xff])]);
            case "write":
                return new(
                    AllFormats,
                    Base6,
                    [
                        (Write(1, "aabb"), [0x00, 0xaa, 0xbb, 0x03, 0x04, 0x05]),
                        (Write(1, "AaBb"), [0x00, 0xaa, 0xbb, 0x03, 0x04, 0x05]),
                    ]);
            case "insert":
                return new(AllFormats, Base6, [(Insert(2, "cc"), [0x00, 0x01, 0xcc, 0x02, 0x03, 0x04, 0x05])]);
            case "delete":
                return new(AllFormats, Base6, [(Delete(1, 2), [0x00, 0x03, 0x04, 0x05])]);
            case "fixChecksum":
                {
                    var baseBytes = Base16;
                    var expected = (byte[])baseBytes.Clone();
                    BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(8), XxHash64.HashToUInt64(baseBytes.AsSpan(0, 8), 0));
                    return new(TrailerFormats, baseBytes, [(FixChecksum(), expected)]);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "no mutation op row");
        }
    }

    private static BadCase BadRow(string row) => row switch
    {
        // After truncate 3 the bytes are 3 long, so truncate 4 runs past them, though the base has 6.
        "truncate-past-length" => new(InputFormat, Base6, [Truncate(3), Truncate(4)], 1),

        // After delete 0 1 the bytes are 5 long; write 4 "aabb" would end at 6.
        "write-past-end" => new(InputFormat, Base6, [Delete(0, 1), Write(4, "aabb")], 1),

        // After two appends the bytes are 8 long; insert 8 would append, insert 9 is past the end.
        "insert-past-end" => new(InputFormat, Base6, [Append("ff"), Append("ee"), Insert(9, "cc")], 2),

        // delete 5 2 on 6 bytes would end at 7.
        "delete-past-end" => new(InputFormat, Base6, [Delete(5, 2)], 0),

        "hex-odd-length" => new(CollisionFormat, Base6, [Write(0, "aa"), Write(0, "abc")], 1),

        "hex-not-hex" => new(StreamFormat, Base6, [Append("zz")], 0),

        "unknown-op" => new(InputFormat, Base6, [Append("ff"), new GoldenMutation("rotate", 0, 0, null)], 1),

        // An input command has no trailer.
        "fix-checksum-input" => new(InputFormat, Base16, [Write(0, "ff"), FixChecksum()], 1),

        // After truncate 7 there are fewer than the trailer's 8 bytes.
        "fix-checksum-short" => new(StreamFormat, Base16, [Truncate(7), FixChecksum()], 1),

        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no bad-argument row"),
    };

    private static GoldenMutation Truncate(long length) => new("truncate", 0, length, null);

    private static GoldenMutation Append(string hex) => new("append", 0, 0, hex);

    private static GoldenMutation Write(long offset, string hex) => new("write", offset, 0, hex);

    private static GoldenMutation Insert(long offset, string hex) => new("insert", offset, 0, hex);

    private static GoldenMutation Delete(long offset, long length) => new("delete", offset, length, null);

    private static GoldenMutation FixChecksum() => new("fixChecksum", 0, 0, null);

    private static string Describe(GoldenMutation mutation) =>
        $"{mutation.Op} (offset {mutation.Offset}, length {mutation.Length}, hex \"{mutation.Hex}\")";

    private static string Hex(byte[] bytes) => bytes.Length == 0 ? "(no bytes)" : Convert.ToHexStringLower(bytes);

    /// <summary>One op's row: the formats it is applied in, the base, and each mutation with its expected bytes.</summary>
    private sealed record OpCase(string[] Formats, byte[] Base, (GoldenMutation Mutation, byte[] Expected)[] Cases);

    /// <summary>One bad-argument row: the format, the base, the mutations, and the index of the one that is refused.</summary>
    private sealed record BadCase(string Format, byte[] Base, GoldenMutation[] Mutations, int FailingIndex);
}
