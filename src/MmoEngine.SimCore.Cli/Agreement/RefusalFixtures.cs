using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>
/// The refusal fixtures of the agreement work directory (US2 scenario 5, US6 scenarios 2 and 5;
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#manifestjson): streams every build must refuse
/// before step 0, each with the text its error message must contain. <c>unsupported-version</c> is
/// flat-run-30hz with its <c>u16 version</c> set to one past the reader's; <c>rules-revision</c> is
/// flat-run-30hz with its <c>u32 rulesRevision</c> raised by one and its trailer rewritten, so it decodes
/// and is refused when opened; <c>collision-missing</c> pairs flat-run-30hz with
/// <see cref="SmallCollisionFile"/>, whose checksum it does not name.
/// </summary>
internal static class RefusalFixtures
{
    /// <summary>The refusal of a stream in another format version.</summary>
    public const string UnsupportedVersionName = "unsupported-version";

    /// <summary>The refusal of a stream recorded under another rules revision.</summary>
    public const string RulesRevisionName = "rules-revision";

    /// <summary>The refusal of a stream run without the collision data it names.</summary>
    public const string CollisionMissingName = "collision-missing";

    /// <summary>What a build's refusal of <c>rules-revision</c> must contain.</summary>
    public const string RulesRevisionExpected = "rulesRevision at offset 8";

    /// <summary>What a build's refusal of <c>collision-missing</c> must contain.</summary>
    public const string CollisionMissingExpected = "collisionChecksum at offset 36";

    private const int VersionOffset = 4;

    private const int RulesRevisionOffset = 8;

    private const int TrailerLength = 8;

    /// <summary>What a build's refusal of <c>unsupported-version</c> must contain: both versions.</summary>
    public static string UnsupportedVersionExpected { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"unsupported version {RecordedStream.FormatVersion + 1}; this reader supports version {RecordedStream.FormatVersion}");

    /// <summary>A copy of <paramref name="stream"/> with its <c>u16 version</c> at offset 4 one past the reader's; the trailer is unchanged.</summary>
    public static byte[] UnsupportedVersion(ReadOnlySpan<byte> stream)
    {
        var copy = stream.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(VersionOffset), RecordedStream.FormatVersion + 1);
        return copy;
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> with its <c>u32 rulesRevision</c> at offset 8 one past the core's
    /// and its trailer rewritten as the XxHash64 (seed 0) of every byte before it, so the file decodes.
    /// </summary>
    public static byte[] RulesRevision(ReadOnlySpan<byte> stream)
    {
        var copy = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(RulesRevisionOffset), SimCoreVersion.RulesRevision + 1u);
        var body = copy.AsSpan(0, copy.Length - TrailerLength);
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(body.Length), XxHash64.HashToUInt64(body));
        return copy;
    }

    /// <summary>
    /// A small heightfield-only collision file: 2 × 2 flat cells (3 × 3 samples at height 0) 1 m apart from
    /// (−1, −1), with 4 m grid cells and no mesh, encoded with <see cref="CollisionFile.Encode"/>.
    /// </summary>
    public static byte[] SmallCollisionFile() =>
        CollisionFile.Encode(new CollisionData(-1f, -1f, 1f, 4f, 3, 3, new float[9], []));
}
