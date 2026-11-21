using System.Globalization;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;

namespace MmoEngine.SimCore.Server.Golden;

/// <summary>
/// A golden case's kind (specs/015-shared-simulation-core/contracts/input-command-format.md#shared-fixture-rules):
/// a valid case holds decoded values, a refusal case names a base and the mutations that break it.
/// </summary>
public enum GoldenKind
{
    /// <summary><c>"kind": "valid"</c>: encoding the JSON gives the <c>.bin</c>, and decoding the <c>.bin</c> gives the JSON.</summary>
    Valid,

    /// <summary><c>"kind": "refusal"</c>: the mutations applied to the base's <c>.bin</c> give a <c>.bin</c> the decoder refuses.</summary>
    Refusal,
}

/// <summary>
/// One mutation of a refusal case, as its JSON object holds it: the <c>op</c> (<c>truncate</c>,
/// <c>append</c>, <c>write</c>, <c>insert</c>, <c>delete</c> or <c>fixChecksum</c>) and its arguments. An
/// argument the op does not take is 0 or null. <see cref="GoldenMutations.Apply"/> applies it.
/// </summary>
public sealed record GoldenMutation(string Op, long Offset, long Length, string? Hex);

/// <summary>The refusal a refusal case expects: the error's code, byte offset and field.</summary>
public sealed record GoldenExpectedError(FormatErrorCode Code, long Offset, string Field);

/// <summary>
/// One config block entry of a stream case: the field ID, the rule table's path for it (documentation the
/// parser checks against the rule table), and the value's bits.
/// </summary>
public sealed record GoldenConfigEntry(ushort FieldId, string Path, uint ValueBits);

/// <summary>An input case's validation: <c>InputValidator.Validate(command, LastSequence, WeaponSlots)</c> gives <see cref="Expected"/>.</summary>
public sealed record GoldenValidate(uint LastSequence, int WeaponSlots, InputRejection Expected);

/// <summary>
/// What a collision case's world derives at load, as its JSON's <c>derived</c> object records it: the
/// heightfield triangle count, the mesh triangle IDs, the degenerate (dropped) ones, the grid's cells
/// along X and Z, and each cell's triangle IDs, cell after cell.
/// </summary>
public sealed record GoldenCollisionDerived(
    int HeightfieldTriangleCount,
    int[] MeshTriangleIds,
    int[] DegenerateTriangleIds,
    int GridCellsX,
    int GridCellsZ,
    int[][] Cells);

/// <summary>
/// A golden case, <c>simcore/golden/&lt;format&gt;/&lt;case&gt;.json</c>, as <see cref="GoldenJson.Parse"/>
/// reads it: the format it belongs to, the format version it pins, its name and its kind. The derived
/// records are the three valid shapes and the refusal shape.
/// </summary>
/// <remarks>
/// A valid case's <c>.bin</c> comes from its <c>EncodeBin()</c>. The name is not <c>Encode()</c> because
/// <c>RecordedStream.Encode()</c> must stay the only parameterless <c>Encode()</c> in <c>src/</c>: the
/// architecture test <c>StreamWriterTests</c> finds the code that encodes stream files by that text.
/// </remarks>
public abstract record GoldenCase(string Format, int FormatVersion, string Case, GoldenKind Kind);

/// <summary>A valid input case: the command and how it validates.</summary>
public sealed record InputValidCase(string Format, int FormatVersion, string Case, InputCommand Command, GoldenValidate Validate)
    : GoldenCase(Format, FormatVersion, Case, GoldenKind.Valid)
{
    /// <summary>The command's 17 bytes, from <see cref="InputCodec.TryEncode"/>.</summary>
    public byte[] EncodeBin()
    {
        var bytes = new byte[InputCodec.EncodedSize];
        if (!InputCodec.TryEncode(Command, bytes))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"InputCodec.TryEncode refused a {InputCodec.EncodedSize}-byte buffer."));
        }

        return bytes;
    }
}

/// <summary>
/// A valid collision case: the primary data, the file length and trailer checksum the JSON records (outputs
/// the encoder computes, which the tests compare), and the derived world.
/// </summary>
public sealed record CollisionValidCase(
    string Format,
    int FormatVersion,
    string Case,
    CollisionData Data,
    long FileLength,
    ulong Checksum,
    GoldenCollisionDerived Derived)
    : GoldenCase(Format, FormatVersion, Case, GoldenKind.Valid)
{
    /// <summary>The collision file, from <see cref="CollisionFile.Encode"/>. A <see cref="CoreFormatException"/> passes through.</summary>
    public byte[] EncodeBin() => CollisionFile.Encode(Data);
}

/// <summary>
/// A valid stream case: the stream, its config entries as the JSON lists them, and the block lengths, file
/// length and trailer checksum the JSON records (outputs the encoder computes, which the tests compare).
/// </summary>
public sealed record StreamValidCase(
    string Format,
    int FormatVersion,
    string Case,
    RecordedStream Stream,
    GoldenConfigEntry[] ConfigEntries,
    long ConfigLength,
    long InitialStateLength,
    long StepsLength,
    long FileLength,
    ulong Checksum)
    : GoldenCase(Format, FormatVersion, Case, GoldenKind.Valid)
{
    /// <summary>The stream file, from <see cref="RecordedStream.Encode"/>. A <see cref="CoreFormatException"/> passes through.</summary>
    public byte[] EncodeBin() => Stream.Encode();
}

/// <summary>
/// A refusal case: the valid case it is built from, the mutations that turn the base's <c>.bin</c> into
/// this case's, and what it expects: a format error, or (for a stream refused when it is opened) the
/// configuration errors. <see cref="RefusedAt"/> is <c>"open"</c> for a stream that decodes and is refused
/// by <c>StreamRunner</c> when it opens it, with <see cref="CollisionFixtures"/> the collision cases it is
/// opened with, and null for a decoder refusal. Absent lists are empty.
/// </summary>
public sealed record GoldenRefusalCase(
    string Format,
    int FormatVersion,
    string Case,
    string Base,
    GoldenMutation[] Mutations,
    GoldenExpectedError? ExpectedError,
    string? RefusedAt,
    string[] CollisionFixtures,
    (string Path, string Message)[] ExpectedConfigErrors)
    : GoldenCase(Format, FormatVersion, Case, GoldenKind.Refusal);

/// <summary>
/// A golden case that cannot be read or applied: a malformed JSON value, a missing or unknown field, or a
/// mutation whose arguments do not fit. Its message is <c>&lt;JSON path&gt;: &lt;problem&gt;</c>, or
/// <c>mutations[&lt;i&gt;]: &lt;problem&gt;</c> for a mutation, so <c>simcore golden write</c> can name it.
/// </summary>
public sealed class GoldenCaseException : Exception
{
    /// <summary>An exception with <paramref name="message"/>, <c>&lt;where&gt;: &lt;problem&gt;</c>.</summary>
    public GoldenCaseException(string message)
        : base(message)
    {
    }
}
