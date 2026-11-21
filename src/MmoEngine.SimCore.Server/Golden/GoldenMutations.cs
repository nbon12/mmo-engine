using System.Buffers.Binary;
using System.IO.Hashing;

namespace MmoEngine.SimCore.Server.Golden;

/// <summary>
/// The golden fixtures' mutations (specs/015-shared-simulation-core/contracts/input-command-format.md#shared-fixture-rules,
/// the Mutations table): they turn a base case's <c>.bin</c> into a refusal case's <c>.bin</c>.
/// </summary>
public static class GoldenMutations
{
    /// <summary>The <c>u64</c> XxHash64 trailer of the collision and stream formats.</summary>
    private const int TrailerLength = 8;

    private const string InputFormat = "input";
    private const string CollisionFormat = "collision";
    private const string StreamFormat = "stream";

    /// <summary>
    /// Applies <paramref name="mutations"/> in order to a copy of <paramref name="baseBytes"/>, each to the
    /// bytes the ones before it left, and returns the result; the base is not changed.
    /// <list type="bullet">
    /// <item><description><c>truncate</c> keeps the first <c>length</c> bytes;</description></item>
    /// <item><description><c>append</c> adds the <c>hex</c> bytes at the end;</description></item>
    /// <item><description><c>write</c> overwrites bytes from <c>offset</c> with the <c>hex</c> bytes;</description></item>
    /// <item><description><c>insert</c> inserts the <c>hex</c> bytes before <c>offset</c>;</description></item>
    /// <item><description><c>delete</c> removes <c>length</c> bytes from <c>offset</c>;</description></item>
    /// <item><description><c>fixChecksum</c>, for <c>collision</c> and <c>stream</c> only, rewrites the 8-byte
    /// trailer as the XxHash64 (seed 0) of every byte before it, little-endian.</description></item>
    /// </list>
    /// Hex is read case-insensitively, two digits per byte.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not <c>input</c>, <c>collision</c> or <c>stream</c>.</exception>
    /// <exception cref="GoldenCaseException">
    /// A mutation does not fit the current bytes or is malformed: a <c>truncate</c> past the length, a
    /// <c>write</c>, <c>insert</c> or <c>delete</c> past the end, a negative offset or length, a missing,
    /// odd-length or non-hex <c>hex</c>, an unknown op, or a <c>fixChecksum</c> on an input command or on
    /// fewer than 8 bytes. The message is <c>mutations[&lt;i&gt;]: &lt;problem&gt;</c>.
    /// </exception>
    public static byte[] Apply(string format, ReadOnlySpan<byte> baseBytes, IReadOnlyList<GoldenMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(mutations);
        if (format is not (InputFormat or CollisionFormat or StreamFormat))
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "The golden formats are input, collision and stream.");
        }

        var bytes = baseBytes.ToArray();
        for (var i = 0; i < mutations.Count; i++)
        {
            var mutation = mutations[i] ?? throw Problem(i, $"is null");
            bytes = mutation.Op switch
            {
                "truncate" => Truncate(i, bytes, mutation),
                "append" => [.. bytes, .. Hex(i, mutation)],
                "write" => Write(i, bytes, mutation),
                "insert" => Insert(i, bytes, mutation),
                "delete" => Delete(i, bytes, mutation),
                "fixChecksum" => FixChecksum(i, format, bytes),
                _ => throw Problem(i, $"unknown op \"{mutation.Op}\"; the ops are truncate, append, write, insert, delete and fixChecksum"),
            };
        }

        return bytes;
    }

    private static byte[] Truncate(int index, byte[] bytes, GoldenMutation mutation)
    {
        var length = NotNegative(index, "length", mutation.Length);
        if (length > bytes.Length)
        {
            throw Problem(index, $"truncate to {length} bytes is past the length {bytes.Length}");
        }

        return bytes[..(int)length];
    }

    private static byte[] Write(int index, byte[] bytes, GoldenMutation mutation)
    {
        var offset = NotNegative(index, "offset", mutation.Offset);
        var data = Hex(index, mutation);
        if (offset > bytes.Length || data.Length > bytes.Length - offset)
        {
            throw Problem(index, $"write of {data.Length} bytes at offset {offset} runs past the end, {bytes.Length}");
        }

        var result = (byte[])bytes.Clone();
        data.CopyTo(result.AsSpan((int)offset));
        return result;
    }

    private static byte[] Insert(int index, byte[] bytes, GoldenMutation mutation)
    {
        var offset = NotNegative(index, "offset", mutation.Offset);
        var data = Hex(index, mutation);
        if (offset > bytes.Length)
        {
            throw Problem(index, $"insert at offset {offset} is past the end, {bytes.Length}");
        }

        var at = (int)offset;
        return [.. bytes.AsSpan(0, at), .. data, .. bytes.AsSpan(at)];
    }

    private static byte[] Delete(int index, byte[] bytes, GoldenMutation mutation)
    {
        var offset = NotNegative(index, "offset", mutation.Offset);
        var length = NotNegative(index, "length", mutation.Length);
        if (offset > bytes.Length || length > bytes.Length - offset)
        {
            throw Problem(index, $"delete of {length} bytes at offset {offset} runs past the end, {bytes.Length}");
        }

        var start = (int)offset;
        var end = start + (int)length;
        return [.. bytes.AsSpan(0, start), .. bytes.AsSpan(end)];
    }

    private static byte[] FixChecksum(int index, string format, byte[] bytes)
    {
        if (format == InputFormat)
        {
            throw Problem(index, $"fixChecksum applies to collision data and streams; an input command has no trailer");
        }

        if (bytes.Length < TrailerLength)
        {
            throw Problem(index, $"fixChecksum needs the {TrailerLength}-byte trailer; there are {bytes.Length} bytes");
        }

        var result = (byte[])bytes.Clone();
        var trailer = result.Length - TrailerLength;
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(trailer), XxHash64.HashToUInt64(result.AsSpan(0, trailer), 0));
        return result;
    }

    /// <summary>The mutation's <c>hex</c> as bytes: two hex digits per byte, either case.</summary>
    private static byte[] Hex(int index, GoldenMutation mutation)
    {
        var hex = mutation.Hex ?? throw Problem(index, $"{mutation.Op} needs hex");
        if (hex.Length % 2 != 0)
        {
            throw Problem(index, $"hex \"{hex}\" has an odd number of digits, {hex.Length}");
        }

        foreach (var digit in hex)
        {
            if (!char.IsAsciiHexDigit(digit))
            {
                throw Problem(index, $"hex \"{hex}\" holds '{digit}', which is not a hex digit");
            }
        }

        return Convert.FromHexString(hex);
    }

    private static long NotNegative(int index, string name, long value) =>
        value >= 0 ? value : throw Problem(index, $"{name} {value} is negative");

    private static GoldenCaseException Problem(int index, FormattableString problem) =>
        new(string.Concat(FormattableString.Invariant($"mutations[{index}]: "), FormattableString.Invariant(problem)));
}
