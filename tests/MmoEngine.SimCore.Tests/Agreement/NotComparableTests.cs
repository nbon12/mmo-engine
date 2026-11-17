using System.Buffers.Binary;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Agreement;

/// <summary>
/// NotComparable outcomes in <c>simcore streams verify</c> (US2 scenario 5;
/// contracts/simcore-cli.md#streams-verify, contracts/recorded-stream-format.md#outcomes). A stream
/// recorded under another format version, or under another rules revision, carries expected hashes that
/// say nothing about agreement, so verify prints <c>NOT COMPARABLE stream &lt;name&gt;: </c> and both
/// values, counts it as not comparable and exits 2, and never reports it as a divergence or a refusal.
/// Only the stream's own <c>version</c> (offset 4) and its <c>rulesRevision</c> (offset 8) are
/// NotComparable: an embedded input command of another version is an ordinary refusal.
/// </summary>
/// <remarks>
/// Each test writes one modified copy of the committed <c>simcore/streams/flat-run-30hz.mmrs</c>, read
/// read-only through <see cref="RepoPaths"/>, into its own <see cref="TempDirectories"/> directory, and
/// verifies that directory against the committed <c>simcore/content</c>, which the CLI opens read-only.
/// Nothing is written under <c>simcore/</c>, so the tests are parallel-safe and rerun-safe. Stdout is
/// split on <c>\n</c>; one trailing line break ends the last line rather than starting another.
/// </remarks>
public sealed class NotComparableTests
{
    private const string FlatRun30 = "flat-run-30hz";

    /// <summary>The stream header's <c>u16 version</c> offset.</summary>
    private const int VersionOffset = 4;

    /// <summary>The stream header's <c>u32 rulesRevision</c> offset.</summary>
    private const int RulesRevisionOffset = 8;

    /// <summary>The format version and the rules revision the modified copies carry.</summary>
    private const int OtherValue = 2;

    /// <summary>The expected hash the modified copies carry at step 12, so that each would diverge if it were run.</summary>
    private const ulong WrongHash = 0x0123456789abcdefUL;

    /// <summary>The index of the step whose expected hash is altered: step 12.</summary>
    private const int DivergentIndex = 11;

    /// <summary>
    /// The file offset of <c>steps[0].commands[0].input.version</c> in the committed flat-run-30hz:
    /// the steps block starts at 64 + 698 + 136 = 898, then the entry's <c>u32 step</c> and
    /// <c>u8 commandCount</c> (5 bytes), the command's <c>u8 characterIndex</c> and the input's
    /// <c>u8 formatId</c> come before it.
    /// </summary>
    private const int EmbeddedVersionOffset = 905;

    /// <summary>The input command's format identifier, <c>'I'</c>, the byte before its version.</summary>
    private const byte InputFormatId = 0x49;

    /// <summary>Rows (case, the exact NOT COMPARABLE line the copy gives).</summary>
    public static TheoryData<string, string> Cases => new()
    {
        { "unsupported-version", $"NOT COMPARABLE stream {FlatRun30}: unsupported version 2; this reader supports version 1" },
        { "rules-revision", $"NOT COMPARABLE stream {FlatRun30}: rules revision 2 in the stream; this core implements rules revision 1" },
    };

    /// <summary>
    /// US2 scenario 5 and contracts/simcore-cli.md#streams-verify: Given a unique temp directory holding one
    /// copy of <c>flat-run-30hz.mmrs</c> whose expected hash at step 12 is <c>0123456789abcdef</c>, not the
    /// committed one, so that it would diverge if it were run, recorded under another format version
    /// (<c>unsupported-version</c>: the <c>u16</c> at offset 4 set to 2) or another rules revision
    /// (<c>rules-revision</c>: the <c>u32</c> at offset 8 set to 2, with the XxHash64 trailer rewritten), When
    /// <c>simcore streams verify --dir &lt;temp&gt; --content &lt;root&gt;/simcore/content</c> runs, Then it
    /// exits 2, stdout holds exactly the line <paramref name="expectedLine"/> naming both values, the last
    /// line is <c>streams verify: 1 streams, 0 match, 0 diverged, 1 not comparable, 0 refused</c>, and no
    /// line starts with <c>DIVERGED</c> or <c>REFUSED</c>: the stream is refused as not comparable instead of
    /// reported as a divergence.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void AnotherFormatVersionOrRulesRevisionIsNotComparableNotDiverged(string caseName, string expectedLine)
    {
        var committed = RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs");
        var committedHash = RecordedStream.Decode(committed).Steps[DivergentIndex].ExpectedHash;
        Assert.True(
            committedHash != WrongHash,
            $"simcore/streams/{FlatRun30}.mmrs already expects {WrongHash:x16} at step 12, so the copy would not diverge");
        var diverging = StreamBytes.WithExpectedHash(committed, DivergentIndex, WrongHash);
        var modified = caseName switch
        {
            "unsupported-version" => WithVersion(diverging, OtherValue),
            "rules-revision" => WithRulesRevision(diverging, OtherValue),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
        };
        using var directory = TempDirectories.Create($"not-comparable-{caseName}");
        File.WriteAllBytes(directory.Combine($"{FlatRun30}.mmrs"), modified);

        var (exit, stdout, stderr) = SimcoreCli.Run(
            "streams", "verify", "--dir", directory.Path, "--content", RepoPaths.Combine("simcore", "content"));

        var label = $"{caseName}: simcore streams verify";
        Assert.True(exit == 2, $"{label} exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(
            lines.Count(line => line == expectedLine) == 1,
            $"{label} should print the line '{expectedLine}' exactly once; stdout: {stdout}");
        Assert.True(lines.Length > 0, $"{label} printed nothing; stderr: {stderr}");
        Assert.Equal("streams verify: 1 streams, 0 match, 0 diverged, 1 not comparable, 0 refused", lines[^1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("DIVERGED", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("REFUSED", StringComparison.Ordinal));
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify and contracts/recorded-stream-format.md#outcomes: Given a
    /// unique temp directory holding a copy of <c>flat-run-30hz.mmrs</c> whose <c>u8</c> at offset 905
    /// (<c>steps[0].commands[0].input.version</c>) is 2, with the XxHash64 trailer rewritten so the decode
    /// reaches the embedded command, When <c>streams verify</c> runs, Then it exits 2, prints exactly
    /// <c>REFUSED stream flat-run-30hz: stream steps[0].commands[0].input.version at offset 905: unsupported version 2; this reader supports version 1</c>,
    /// ends with <c>streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused</c>, and
    /// prints no line starting with <c>NOT COMPARABLE</c>: only the stream's own version and rules revision
    /// are NotComparable.
    /// </summary>
    [Fact]
    public void EmbeddedCommandVersionIsRefusedNotNotComparable()
    {
        var committed = RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs");
        Assert.True(
            StreamBytes.StepOffset(committed, 0) + 7 == EmbeddedVersionOffset
                && committed[EmbeddedVersionOffset - 1] == InputFormatId
                && committed[EmbeddedVersionOffset] == 1,
            $"offset {EmbeddedVersionOffset} of simcore/streams/{FlatRun30}.mmrs should be steps[0].commands[0].input.version (version 1, after the formatId 0x49); " +
            $"steps[0] starts at {StreamBytes.StepOffset(committed, 0)}");
        var modified = StreamBytes.WithBytes(committed, EmbeddedVersionOffset, [(byte)OtherValue]);
        using var directory = TempDirectories.Create("not-comparable-embedded-version");
        File.WriteAllBytes(directory.Combine($"{FlatRun30}.mmrs"), modified);

        var (exit, stdout, stderr) = SimcoreCli.Run(
            "streams", "verify", "--dir", directory.Path, "--content", RepoPaths.Combine("simcore", "content"));

        Assert.True(exit == 2, $"simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains(
            $"REFUSED stream {FlatRun30}: stream steps[0].commands[0].input.version at offset 905: unsupported version 2; this reader supports version 1",
            lines);
        Assert.True(lines.Length > 0, $"simcore streams verify printed nothing; stderr: {stderr}");
        Assert.Equal("streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused", lines[^1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("NOT COMPARABLE", StringComparison.Ordinal));
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> whose <c>u16 version</c> at offset 4 is <paramref name="version"/>.
    /// The trailer is left as it was: the version is checked before any other field, the checksum included.
    /// </summary>
    private static byte[] WithVersion(byte[] stream, int version)
    {
        var copy = (byte[])stream.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(VersionOffset, 2), (ushort)version);
        return copy;
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> whose <c>u32 rulesRevision</c> at offset 8 is
    /// <paramref name="rulesRevision"/>, with the XxHash64 trailer rewritten so the decode accepts it.
    /// </summary>
    private static byte[] WithRulesRevision(byte[] stream, int rulesRevision)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rulesRevision);
        return StreamBytes.WithBytes(stream, RulesRevisionOffset, bytes);
    }

    /// <summary>Stdout's lines, split on <c>\n</c>; a trailing line break ends the last line.</summary>
    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
