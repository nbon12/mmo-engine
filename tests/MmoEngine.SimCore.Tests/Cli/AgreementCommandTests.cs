using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// <c>simcore agreement prepare</c> and <c>simcore agreement compare</c> (FR-026 server half, US2 scenarios 4
/// and 5; contracts/simcore-cli.md#agreement-prepare, #agreement-compare, #agreement-work-directory and
/// #manifestjson). <c>prepare</c> runs every committed stream through the server build, checks its expected
/// hashes, generates <c>seed-&lt;nnn&gt;</c> streams, and writes the work directory every build runs from, with
/// <c>manifest.json</c> last. <c>compare</c> compares each build folder's step records and table results with
/// the server's, names the first divergent stream, step and field, and exits 0 when every build agrees, 1 on a
/// divergence and 2 when it cannot run or the work directory is NotComparable.
/// </summary>
/// <remarks>
/// The committed <c>simcore/streams</c>, <c>simcore/content</c> and <c>simcore/config/default.json</c> are read
/// only through <see cref="RepoPaths"/> or by the CLI, which opens them read-only; every work directory, copy
/// and edited input is in its own <see cref="TempDirectories"/> directory, so the tests are parallel-safe and
/// rerun-safe and never write under <c>simcore/</c>. The compare tests stand in for the WebAssembly builds with
/// <see cref="WorkDirectories.WriteBuild"/> folders. Output is split on <c>\n</c>; one trailing line break
/// ends the last line rather than starting another.
/// </remarks>
public sealed class AgreementCommandTests
{
    private const string FlatRun30 = "flat-run-30hz";

    /// <summary>flat-run-30hz's middle step, the step at index (30 − 1) / 2: the perturbation's step.</summary>
    private const uint PerturbedStep = 15;

    /// <summary>The field a one-ulp perturbation of <c>characters[0].position.x</c> changes.</summary>
    private const string PositionX = "characters[0].position.x";

    /// <summary><c>characters[0].position.x</c>'s offset in a canonical state.</summary>
    private const int PositionXOffset = 16;

    /// <summary>Every generated stream's step count (contracts/simcore-cli.md#generated-streams).</summary>
    private const int GeneratedStepCount = 300;

    /// <summary>The seeds CI generates: <c>--seeds 1-50</c>.</summary>
    private const int AllSeeds = 50;

    /// <summary>The streams <see cref="WorkDirectories.DefaultSeeds"/> generates.</summary>
    private const int DefaultSeedCount = 2;

    private const string ManifestFile = "manifest.json";

    /// <summary>One byte over <c>CollisionFile.MaxFileLength</c> (256 MiB).</summary>
    private const long OversizedLength = 268_435_457;

    /// <summary>The refusal of <see cref="OversizedLength"/>'s <c>big.mmcd</c> (contracts/simcore-cli.md#content-files).</summary>
    private const string OversizedRefusal =
        "agreement prepare: content big.mmcd: collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456";

    /// <summary>The expected hash the diverged copy of <c>jump-land</c> carries at step 12.</summary>
    private const ulong WrongHash = 0x0123456789abcdefUL;

    /// <summary>The index of the <c>jump-land</c> step whose expected hash is altered: step 12.</summary>
    private const int DivergentIndex = 11;

    /// <summary>The stream header's <c>u16 version</c> offset.</summary>
    private const int VersionOffset = 4;

    /// <summary>The stream header's <c>u32 rulesRevision</c> offset.</summary>
    private const int RulesRevisionOffset = 8;

    /// <summary>Math row 0's <c>r0</c> in an <c>MMXR</c> results file: after the 16-byte header (wasm-api.md#cross-build-tables).</summary>
    private const int MathRow0R0Offset = 16;

    private const string SummaryHeading = "## SimCore agreement";

    private const string SummaryTableHeader = "| Build | Required | Status | Streams | Steps | First divergence | Tables |";

    /// <summary>The message the build error files hold.</summary>
    private const string Boom = "boom";

    /// <summary>The known builds, in contract order (contracts/simcore-cli.md#build-names).</summary>
    private static readonly string[] KnownBuilds =
        ["node-interp", "node-interp-nojit", "chromium-worker", "firefox-worker", "webkit-worker", "node-interp-relinked", "node-aot"];

    /// <summary>The default <c>--require</c>.</summary>
    private static readonly string[] RequiredBuilds = ["node-interp", "node-interp-nojit", "chromium-worker"];

    private static readonly Regex StreamNamePattern = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    /// <summary>A Markdown table's separator row for seven columns.</summary>
    private static readonly Regex SummarySeparator = new(@"^\|( *:?-{3,}:? *\|){7}$", RegexOptions.CultureInvariant);

    /// <summary>The inputs <c>agreement prepare</c> refuses before it writes anything.</summary>
    public static TheoryData<string> RefusedPrepareCases =>
    [
        "non-empty-work", "work-is-a-file", "seeds-0-5", "seeds-5-3", "seeds-1-1000", "seeds-abc", "missing-streams", "oversized-content",
        "missing-content", "missing-config", "no-flat-run-30hz", "no-test-world", "generated-stream-refused", "work-under-a-file",
        "stream-name-uppercase", "stream-name-tables", "stream-name-generated",
    ];

    /// <summary>Rows (case, the exact line a refused or NotComparable committed stream prints).</summary>
    public static TheoryData<string, string> RefusedStreamCases => new()
    {
        { "refused", $"REFUSED stream {FlatRun30}: stream collisionChecksum at offset 36: collision data {TestWorldChecksum():x16} was not supplied" },
        { "not-comparable", $"NOT COMPARABLE stream {FlatRun30}: unsupported version 2; this reader supports version 1" },
    };

    public static TheoryData<string> TablesMismatchCases => ["flipped-bit", "error-file"];

    /// <summary>Rows (case, the NOT COMPARABLE line, with <c>&lt;work&gt;</c> standing for the work directory).</summary>
    public static TheoryData<string, string> NotComparableWorkCases => new()
    {
        { "rules-revision", "NOT COMPARABLE work <work>: rules revision 2 in the work directory; this core implements rules revision 1" },
        { "recorded-stream-format", "NOT COMPARABLE work <work>: recorded stream format version 2 in the work directory; this reader supports version 1" },
        { "manifest-format", "NOT COMPARABLE work <work>: manifest format version 2 in the work directory; this reader supports version 1" },
        { "manifest-format-reshaped", "NOT COMPARABLE work <work>: manifest format version 2 in the work directory; this reader supports version 1" },
        {
            "manifest-format-and-rules",
            "NOT COMPARABLE work <work>: manifest format version 2 in the work directory; this reader supports version 1\n"
                + "NOT COMPARABLE work <work>: rules revision 2 in the work directory; this core implements rules revision 1"
        },
    };

    public static TheoryData<string> CannotRunCases =>
    [
        "no-manifest", "invalid-manifest", "missing-server-records", "missing-server-tables", "unknown-required-build",
        "truncated-server-records", "corrupt-work-stream", "path-outside-work", "missing-manifest-member", "invalid-stream-name",
        "unwritable-summary", "truncated-server-records-no-builds", "truncated-server-tables",
    ];

    /// <summary>
    /// FR-026 server half and contracts/simcore-cli.md#agreement-prepare: Given the committed streams, content
    /// and configuration, When <c>agreement prepare --seeds 1-50 --work &lt;new temp path&gt;</c> runs, Then it
    /// exits 0 and prints exactly
    /// <c>prepared &lt;work&gt;: &lt;c + 50&gt; streams (&lt;c&gt; named, 50 generated), &lt;steps&gt; steps, rules revision 1</c>,
    /// with c the committed <c>*.mmrs</c> count and steps the sum of every stream's step count; and the work
    /// directory holds: a manifest with <c>formatVersion</c> 1, <c>rulesRevision</c> 1, <c>formats</c> all 1
    /// and <c>seeds</c> 1 to 50; the committed streams in ordinal order, copied byte for byte, then
    /// <c>seed-001</c> to <c>seed-050</c> (origin <c>generated</c>, seeds 1 to 50), with every path existing,
    /// every header value, each <c>server/&lt;stream&gt;.records</c> equal to <see cref="StreamRunner"/>'s run and
    /// its hashes equal to the stream's expected hashes, each <c>collision/&lt;x16&gt;.mmcd</c> a copy of
    /// <c>test-world.mmcd</c>, and <c>batch</c> exactly for the streams whose every step has one command for
    /// character index 0 (flat-run-30hz's <c>commands.bin</c> is 30 × 17 bytes); <c>streams/seed-001.mmrs</c>
    /// equal to <c>StreamGenerator.Build(1, …)</c> with its expected hashes filled; the perturbation
    /// flat-run-30hz, step 15, <c>characters[0].position.x</c>; the three refusals, each refused by
    /// <see cref="StreamRunner"/> with a message containing its <c>expected</c> text; the cross-build tables
    /// equal to <c>CrossBuildTables.BuildInputs()</c>, the config block of default.json and <c>Evaluate</c> of
    /// the two; and no file written after <c>manifest.json</c>.
    /// </summary>
    [Fact]
    public void PrepareWritesEveryArtifactAndTheManifestLast()
    {
        var committed = CommittedStreams();
        var (streamCount, stepTotal) = Totals(committed, AllSeeds);
        using var directory = TempDirectories.Create("agreement-prepare-all");
        var work = directory.Combine("work");

        var (exit, stdout, stderr) = SimcoreCli.Run(WorkDirectories.PrepareArgs(work, "1-50"));

        Assert.True(exit == 0, $"simcore agreement prepare --seeds 1-50 exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            $"prepared {work}: {streamCount} streams ({committed.Count} named, {AllSeeds} generated), {stepTotal} steps, rules revision 1\n",
            stdout);

        var manifest = ReadJson(Path.Combine(work, ManifestFile));
        Assert.Equal(1L, Number(manifest, "formatVersion", ManifestFile));
        Assert.Equal(1L, Number(manifest, "rulesRevision", ManifestFile));
        var formats = Child(manifest, "formats", ManifestFile);
        Assert.Equal(1L, Number(formats, "inputCommand", "formats"));
        Assert.Equal(1L, Number(formats, "collisionData", "formats"));
        Assert.Equal(1L, Number(formats, "recordedStream", "formats"));
        var seeds = Child(manifest, "seeds", ManifestFile);
        Assert.Equal(1L, Number(seeds, "first", "seeds"));
        Assert.Equal((long)AllSeeds, Number(seeds, "last", "seeds"));

        var entries = Entries(manifest, "streams", ManifestFile);
        string[] expectedNames =
            [.. committed.Select(stream => stream.Name), .. Enumerable.Range(1, AllSeeds).Select(seed => $"seed-{seed:D3}")];
        Assert.Equal(expectedNames, entries.Select(entry => Text(entry, "name", "a manifest stream")));

        var testWorldFile = TestWorldFile();
        var testWorld = CollisionFile.Decode(testWorldFile);
        for (var i = 0; i < entries.Length; i++)
        {
            var committedStream = i < committed.Count ? committed[i] : null;
            AssertStreamEntry(work, entries[i], committedStream, i - committed.Count + 1, testWorldFile, testWorld);
        }

        var flatRunEntry = entries[committed.FindIndex(stream => stream.Name == FlatRun30)];
        var flatRunCommands = ReadWorkFile(work, Text(Child(flatRunEntry, "batch", FlatRun30), "commands", FlatRun30), FlatRun30);
        Assert.True(
            flatRunCommands.Length == 30 * InputCodec.EncodedSize,
            $"{FlatRun30}'s commands.bin is {flatRunCommands.Length} bytes, not 30 × {InputCodec.EncodedSize}");

        AssertFirstGeneratedStream(work, testWorld);
        AssertPerturbation(manifest, committed);
        AssertRefusals(work, manifest, committed);
        AssertTables(work, manifest);
        AssertManifestWrittenLast(work);
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-prepare and #content-files: Given a <c>--work</c> that is a
    /// non-empty directory or a file, a <c>--seeds</c> of <c>0-5</c>, <c>5-3</c>, <c>1-1000</c> or
    /// <c>abc</c>, a <c>--streams</c> directory that does not exist, or a <c>--content</c> directory holding a
    /// copy of <c>test-world.mmcd</c> and a sparse <c>big.mmcd</c> of 268,435,457 bytes; or a
    /// <c>--content</c> or <c>--config</c> that does not exist, a <c>--streams</c> holding only
    /// <c>flat-run-20hz.mmrs</c> (no <c>flat-run-30hz</c>), a <c>--content</c> holding the test world as
    /// <c>world.mmcd</c> (no <c>test-world.mmcd</c>), <c>--seeds 3</c> with a <c>--config</c> whose rifle fires
    /// 1500 rounds a minute (valid at its 30 Hz, refused by rule R11 at seed 3's 20 Hz), a <c>--work</c>
    /// below a file, which cannot be created once every stream has run, or a <c>--streams</c> holding
    /// <c>flat-run-30hz.mmrs</c> and a committed stream under a name the manifest cannot hold
    /// (<c>Flat_Run.mmrs</c>, <c>tables.mmrs</c>) or under the name of a stream <c>--seeds 1-2</c> generates
    /// (<c>seed-001.mmrs</c>, a copy of <c>jump-land.mmrs</c>); When <c>agreement prepare</c> runs, Then it
    /// exits 2, stderr has an <c>agreement prepare: </c> line naming the refused input (for the oversized file
    /// exactly
    /// <c>agreement prepare: content big.mmcd: collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456</c>,
    /// and for a stream name exactly
    /// <c>agreement prepare: --streams &lt;dir&gt;: &lt;file&gt;: the stream name &lt;name&gt; is not lowercase letters, digits and hyphens, other than tables</c>
    /// or <c>... the stream name seed-001 is also the name of a stream --seeds 1-2 generates</c>, as the only
    /// <c>agreement prepare: </c> line), nothing is prepared, and every file and directory in the temp
    /// directory is unchanged, with none added: a refused work directory keeps its one file, and a work
    /// directory that did not exist still does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedPrepareCases))]
    public void PrepareRefusesANonEmptyWorkDirectoryOrBadSeeds(string caseName)
    {
        // The prefix does not name the row, so no path in stderr can contain the refused --seeds value by chance.
        using var directory = TempDirectories.Create("agreement-prepare-refused-input");
        var work = directory.Combine("work");
        var seeds = WorkDirectories.DefaultSeeds;
        string? streams = null;
        string? content = null;
        string? config = null;
        var workExisted = false;
        string named;
        string? exactLine = null;
        switch (caseName)
        {
            case "non-empty-work":
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, "keep.txt"), "keep\n");
                workExisted = true;
                named = work;
                break;
            case "work-is-a-file":
                File.WriteAllText(work, "a file, not a directory\n");
                workExisted = true;
                named = work;
                break;
            case "seeds-0-5":
                seeds = "0-5";
                named = seeds;
                break;
            case "seeds-5-3":
                seeds = "5-3";
                named = seeds;
                break;
            case "seeds-1-1000":
                seeds = "1-1000";
                named = seeds;
                break;
            case "seeds-abc":
                seeds = "abc";
                named = seeds;
                break;
            case "missing-streams":
                streams = directory.Combine("no-such-streams");
                named = streams;
                break;
            case "oversized-content":
                content = Directory.CreateDirectory(directory.Combine("content")).FullName;
                CopyCommitted(content, "content", "test-world.mmcd");
                CreateSparse(Path.Combine(content, "big.mmcd"), OversizedLength);
                named = "big.mmcd";
                break;
            case "missing-content":
                content = directory.Combine("no-such-content");
                named = content;
                break;
            case "missing-config":
                config = directory.Combine("no-such-config.json");
                named = config;
                break;
            case "no-flat-run-30hz":
                streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
                CopyCommitted(streams, "streams", "flat-run-20hz.mmrs");
                named = FlatRun30;
                break;
            case "no-test-world":
                content = Directory.CreateDirectory(directory.Combine("content")).FullName;
                File.WriteAllBytes(Path.Combine(content, "world.mmcd"), TestWorldFile());
                named = "test-world.mmcd";
                break;
            case "generated-stream-refused":
                seeds = "3";
                config = directory.Combine("fast-rifle.json");
                var fastRifle = JsonNode.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"))!.AsObject();
                fastRifle["weapons"]![0]!["roundsPerMinute"] = 1500;
                File.WriteAllText(config, fastRifle.ToJsonString());
                named = "seed-003";
                break;
            case "work-under-a-file":
                File.WriteAllText(directory.Combine("blocker"), "a file where the work directory's parent should be\n");
                work = Path.Combine(directory.Combine("blocker"), "work");
                named = work;
                break;
            case "stream-name-uppercase":
            case "stream-name-tables":
                streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
                CopyCommitted(streams, "streams", $"{FlatRun30}.mmrs");
                var badName = caseName == "stream-name-uppercase" ? "Flat_Run" : "tables";
                File.WriteAllBytes(Path.Combine(streams, $"{badName}.mmrs"), RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs"));
                named = $"{badName}.mmrs";
                exactLine = $"agreement prepare: --streams {streams}: {badName}.mmrs: the stream name {badName} is not lowercase letters, digits and hyphens, other than tables";
                break;
            case "stream-name-generated":
                streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
                CopyCommitted(streams, "streams", $"{FlatRun30}.mmrs");
                File.WriteAllBytes(Path.Combine(streams, "seed-001.mmrs"), RepoPaths.ReadAllBytes("simcore", "streams", "jump-land.mmrs"));
                named = "seed-001.mmrs";
                exactLine = $"agreement prepare: --streams {streams}: seed-001.mmrs: the stream name seed-001 is also the name of a stream --seeds {seeds} generates";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var before = Snapshot(directory.Path);

        var (exit, stdout, stderr) = SimcoreCli.Run(WorkDirectories.PrepareArgs(work, seeds, streams, content, config));

        Assert.True(exit == 2, $"{caseName}: simcore agreement prepare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var errors = Lines(stderr);
        if (caseName == "oversized-content")
        {
            Assert.Contains(OversizedRefusal, errors);
        }
        else if (exactLine is not null)
        {
            Assert.Equal([exactLine], errors.Where(line => line.StartsWith("agreement prepare: ", StringComparison.Ordinal)));
        }
        else
        {
            var refusals = errors.Count(line => line.StartsWith("agreement prepare: ", StringComparison.Ordinal) && line.Contains(named, StringComparison.Ordinal));
            Assert.True(refusals > 0, $"{caseName}: stderr has no \"agreement prepare: \" line naming {named}; stderr: {stderr}");
        }

        Assert.DoesNotContain("prepared ", stdout, StringComparison.Ordinal);
        var differences = Differences(before, Snapshot(directory.Path));
        Assert.True(differences.Length == 0, $"{caseName}: the refused command wrote to the temp directory: {string.Join(", ", differences)}");
        Assert.True(workExisted || !Path.Exists(work), $"{caseName}: the refused command created {work}");
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-prepare step 1: Given a temp copy of the committed streams whose
    /// <c>jump-land.mmrs</c> expects <c>0123456789abcdef</c> at step 12 (trailer rewritten), When
    /// <c>agreement prepare</c> runs on it, Then it exits 1, stdout has the line
    /// <c>DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes</c>
    /// followed by <c>  hash  expected 0123456789abcdef  server &lt;x16&gt;</c> with the hash the server
    /// computes, and no <c>manifest.json</c> is written.
    /// </summary>
    [Fact]
    public void PrepareExitsOneWhenACommittedStreamDivergesAndWritesNoManifest()
    {
        using var streams = TempDirectories.CopyOf(RepoPaths.Combine("simcore", "streams"), "agreement-prepare-diverged-streams");
        using var directory = TempDirectories.Create("agreement-prepare-diverged");
        var work = directory.Combine("work");
        var jumpLandPath = streams.Combine("jump-land.mmrs");
        var jumpLand = File.ReadAllBytes(jumpLandPath);
        var decoded = RecordedStream.Decode(jumpLand);
        Assert.True(
            decoded.Steps.Count > DivergentIndex && decoded.Steps[DivergentIndex].Step == 12u,
            $"jump-land has {decoded.Steps.Count} steps; steps[{DivergentIndex}] should be step 12");
        var realHash = decoded.Steps[DivergentIndex].ExpectedHash;
        Assert.True(realHash != WrongHash, $"jump-land's step 12 already expects {WrongHash:x16}");
        File.WriteAllBytes(jumpLandPath, StreamBytes.WithExpectedHash(jumpLand, DivergentIndex, WrongHash));

        var (exit, stdout, stderr) = SimcoreCli.Run(WorkDirectories.PrepareArgs(work, WorkDirectories.DefaultSeeds, streams.Path));

        Assert.True(exit == 1, $"simcore agreement prepare exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        const string Heading = "DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes";
        var at = Array.IndexOf(lines, Heading);
        Assert.True(at >= 0 && at + 1 < lines.Length, $"stdout has no line \"{Heading}\" followed by the hash line; stdout: {stdout}");
        Assert.Equal($"  hash  expected {WrongHash:x16}  server {realHash:x16}", lines[at + 1]);
        Assert.False(File.Exists(Path.Combine(work, ManifestFile)), $"a manifest was written although jump-land diverged: {Path.Combine(work, ManifestFile)}");
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-prepare step 1, and US2 scenario 5 for the <c>not-comparable</c> row
    /// (the agreement suite loading a stream recorded in another format version refuses it, naming both
    /// versions, instead of reporting a divergence): Given a <c>--streams</c> temp directory holding one
    /// copy of <c>flat-run-30hz.mmrs</c> and <c>--config</c> default.json, with (<c>refused</c>) the copy
    /// unchanged and an empty, existing <c>--content</c> directory, so the collision data it names is not
    /// supplied, or (<c>not-comparable</c>) the copy's <c>u16</c> at offset 4 set to 2 and <c>--content</c>
    /// the committed content, When <c>agreement prepare</c> runs, Then it exits 2, stdout holds exactly once
    /// the line <paramref name="expectedLine"/> and no line starting with <c>DIVERGED</c>, and no
    /// <c>manifest.json</c> is written.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedStreamCases))]
    public void PrepareExitsTwoWhenACommittedStreamIsRefusedAndWritesNoManifest(string caseName, string expectedLine)
    {
        using var directory = TempDirectories.Create($"agreement-prepare-{caseName}");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var work = directory.Combine("work");
        var flatRun = RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs");
        string content;
        switch (caseName)
        {
            case "refused":
                File.WriteAllBytes(Path.Combine(streams, $"{FlatRun30}.mmrs"), flatRun);
                content = Directory.CreateDirectory(directory.Combine("empty-content")).FullName;
                break;
            case "not-comparable":
                var otherVersion = (byte[])flatRun.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(otherVersion.AsSpan(VersionOffset, 2), 2);
                File.WriteAllBytes(Path.Combine(streams, $"{FlatRun30}.mmrs"), otherVersion);
                content = RepoPaths.Combine("simcore", "content");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var (exit, stdout, stderr) = SimcoreCli.Run(WorkDirectories.PrepareArgs(work, WorkDirectories.DefaultSeeds, streams, content));

        Assert.True(exit == 2, $"{caseName}: simcore agreement prepare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(
            lines.Count(line => line == expectedLine) == 1,
            $"{caseName}: stdout should hold the line \"{expectedLine}\" exactly once; stdout: {stdout}");
        Assert.DoesNotContain(lines, line => line.StartsWith("DIVERGED", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(work, ManifestFile)), $"{caseName}: a manifest was written although the stream was not run");
    }

    /// <summary>
    /// contracts/recorded-stream-format.md#regeneration ("tests never write") and
    /// contracts/simcore-cli.md#agreement-prepare: Given the XxHash64 of every file under <c>simcore/</c>, read
    /// read-only, When <c>agreement prepare</c> runs on the committed streams, content and configuration, Then
    /// it writes its manifest and every file under <c>simcore/</c> has the same path and XxHash64 afterwards,
    /// with none added or removed.
    /// </summary>
    [Fact]
    public void PrepareNeverWritesUnderSimcore()
    {
        var before = HashTree(RepoPaths.SimCore);
        Assert.True(before.Length > 0, "simcore/ holds no file");

        using (var work = WorkDirectories.Prepare())
        {
            Assert.True(File.Exists(work.Combine(ManifestFile)), $"agreement prepare wrote no {ManifestFile} in {work.Path}");
        }

        Assert.Equal(before, HashTree(RepoPaths.SimCore));
    }

    /// <summary>
    /// US2 scenario 4, SC-002 and FR-026: Given a prepared work directory where <c>node-interp</c>'s
    /// <c>flat-run-30hz.records</c> is the run perturbed by one ulp at step 15 and <c>node-interp-nojit</c> and
    /// <c>chromium-worker</c> equal the server, When <c>agreement compare</c> runs, Then it exits 1 and prints
    /// exactly <c>node-interp: diverged in 1 of &lt;n&gt; streams; first: flat-run-30hz at step 15; tables match</c>,
    /// the two match lines, the four <c>not run</c> lines, the first divergence report (its heading, both
    /// hashes, <c>  1 field differs</c> and the <c>characters[0].position.x</c> line with the server's bits and
    /// the build's, one ulp above) and <c>agreement: diverged</c>; the report is also in
    /// <c>report/divergences/node-interp/flat-run-30hz.txt</c>; and <c>report/agreement.json</c> has outcome
    /// <c>Diverged</c>, every build in contract order, and node-interp's <c>diverged[0]</c>
    /// {flat-run-30hz, 15, server hash, build hash, 1, null, the report path}.
    /// </summary>
    [Fact]
    public void CompareExitsOneOnADivergedBuildAndWritesItsReport()
    {
        using var work = PreparePerturbed();
        var (streamCount, stepTotal) = Totals(CommittedStreams(), DefaultSeedCount);
        var report = ExpectedReport(work, "node-interp", FlatRun30);
        var serverRecord = RecordAt(work, WorkDirectories.ServerFolder, FlatRun30, PerturbedStep);
        var buildRecord = RecordAt(work, "node-interp", FlatRun30, PerturbedStep);
        var serverBits = BinaryPrimitives.ReadUInt32LittleEndian(serverRecord.State.AsSpan(PositionXOffset));
        var buildBits = BinaryPrimitives.ReadUInt32LittleEndian(buildRecord.State.AsSpan(PositionXOffset));
        Assert.True(buildBits == serverBits + 1u, $"the perturbed position.x bits are 0x{buildBits:X8}, not the server's 0x{serverBits:X8} + 1");

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 1, $"simcore agreement compare exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        string[] expected =
        [
            $"node-interp: diverged in 1 of {streamCount} streams; first: {FlatRun30} at step {PerturbedStep}; tables match",
            MatchLine("node-interp-nojit", streamCount, stepTotal),
            MatchLine("chromium-worker", streamCount, stepTotal),
            "firefox-worker: not run",
            "webkit-worker: not run",
            "node-interp-relinked: not run",
            "node-aot: not run",
            .. report,
            "agreement: diverged",
        ];
        Assert.Equal(expected, Lines(stdout));

        Assert.True(report.Length == 4, $"the node-interp report has {report.Length} lines, not 4: {string.Join(" | ", report)}");
        Assert.Equal($"DIVERGED stream {FlatRun30}, build node-interp, first divergent step {PerturbedStep}", report[0]);
        Assert.Equal($"  hash  server {serverRecord.Hash:x16}  node-interp {buildRecord.Hash:x16}", report[1]);
        Assert.Equal("  1 field differs", report[2]);
        Assert.StartsWith($"  {PositionX} ", report[3]);
        Assert.Contains($"server 0x{serverBits:X8} ", report[3], StringComparison.Ordinal);
        Assert.Contains($"node-interp 0x{buildBits:X8} ", report[3], StringComparison.Ordinal);

        var reportFile = work.Combine("report", "divergences", "node-interp", $"{FlatRun30}.txt");
        Assert.True(File.Exists(reportFile), $"{reportFile} was not written");
        Assert.Equal(report, Lines(File.ReadAllText(reportFile)));

        var agreement = ReadJson(work.Combine("report", "agreement.json"));
        Assert.Equal(1L, Number(agreement, "formatVersion", "agreement.json"));
        Assert.Equal("Diverged", Text(agreement, "outcome", "agreement.json"));
        Assert.Equal(1L, Number(agreement, "rulesRevision", "agreement.json"));
        Assert.Equal((long)streamCount, Number(agreement, "streams", "agreement.json"));
        Assert.Equal(RequiredBuilds, Items(agreement, "required", "agreement.json").Select(item => item!.GetValue<string>()));
        var builds = Entries(agreement, "builds", "agreement.json");
        Assert.Equal(KnownBuilds, builds.Select(build => Text(build, "build", "a build")));

        var nodeInterp = Build(agreement, "node-interp");
        Assert.True(Flag(nodeInterp, "required", "node-interp"), "node-interp should be required");
        Assert.Equal("Diverged", Text(nodeInterp, "status", "node-interp"));
        Assert.Equal((long)streamCount, Number(nodeInterp, "streams", "node-interp"));
        Assert.Equal(stepTotal, Number(nodeInterp, "steps", "node-interp"));
        var diverged = Entries(nodeInterp, "diverged", "node-interp");
        Assert.True(diverged.Length == 1, $"node-interp lists {diverged.Length} diverged streams, not 1");
        var first = diverged[0];
        Assert.Equal(FlatRun30, Text(first, "stream", "node-interp diverged[0]"));
        Assert.Equal((long)PerturbedStep, Number(first, "step", "node-interp diverged[0]"));
        Assert.Equal(X16(serverRecord.Hash), Text(first, "serverHash", "node-interp diverged[0]"));
        Assert.Equal(X16(buildRecord.Hash), Text(first, "buildHash", "node-interp diverged[0]"));
        Assert.Equal(1L, Number(first, "differingFields", "node-interp diverged[0]"));
        AssertJsonNull(first, "error", "node-interp diverged[0]");
        Assert.Equal($"report/divergences/node-interp/{FlatRun30}.txt", Text(first, "report", "node-interp diverged[0]"));
        AssertTablesEntry(nodeInterp, "Match", null, "node-interp");

        foreach (var name in new[] { "node-interp-nojit", "chromium-worker" })
        {
            var build = Build(agreement, name);
            Assert.True(Flag(build, "required", name), $"{name} should be required");
            Assert.Equal("Match", Text(build, "status", name));
            Assert.True(Entries(build, "diverged", name).Length == 0, $"{name} lists diverged streams although it equals the server");
            AssertTablesEntry(build, "Match", null, name);
        }

        foreach (var name in KnownBuilds[3..])
        {
            var build = Build(agreement, name);
            Assert.False(Flag(build, "required", name), $"{name} should not be required");
            Assert.Equal("NotRun", Text(build, "status", name));
        }
    }

    /// <summary>
    /// FR-026 and the US2 Independent Test: Given a prepared work directory and the three required builds
    /// written equal to the server, When <c>agreement compare</c> runs, Then it exits 0 and stdout is exactly
    /// <c>&lt;build&gt;: match, &lt;n&gt; streams, &lt;steps&gt; steps, tables match</c> for node-interp,
    /// node-interp-nojit and chromium-worker, then <c>firefox-worker: not run</c>, <c>webkit-worker: not run</c>,
    /// <c>node-interp-relinked: not run</c> and <c>node-aot: not run</c>, then <c>agreement: match</c>; and
    /// <c>report/agreement.json</c> has outcome <c>Match</c>.
    /// </summary>
    [Fact]
    public void CompareMatchesABuildEqualToTheServer()
    {
        using var work = PrepareWithBuilds(RequiredBuilds);
        var (streamCount, stepTotal) = Totals(CommittedStreams(), DefaultSeedCount);

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 0, $"simcore agreement compare exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            MatchLine("node-interp", streamCount, stepTotal) + "\n" +
            MatchLine("node-interp-nojit", streamCount, stepTotal) + "\n" +
            MatchLine("chromium-worker", streamCount, stepTotal) + "\n" +
            "firefox-worker: not run\n" +
            "webkit-worker: not run\n" +
            "node-interp-relinked: not run\n" +
            "node-aot: not run\n" +
            "agreement: match\n",
            stdout);
        Assert.Equal("Match", Text(ReadJson(work.Combine("report", "agreement.json")), "outcome", "agreement.json"));
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare: Given a prepared work directory with only node-interp and
    /// chromium-worker written, When <c>agreement compare</c> runs with the default <c>--require</c>, Then it
    /// exits 2, stdout has the line <c>node-interp-nojit: not run</c>, and its last line is
    /// <c>agreement: cannot run: required build node-interp-nojit did not run</c>.
    /// </summary>
    [Fact]
    public void RequiredBuildThatDidNotRunExitsTwo()
    {
        using var work = PrepareWithBuilds("node-interp", "chromium-worker");

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 2, $"simcore agreement compare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains("node-interp-nojit: not run", lines);
        Assert.True(lines.Length > 0, $"simcore agreement compare printed nothing; stderr: {stderr}");
        Assert.Equal("agreement: cannot run: required build node-interp-nojit did not run", lines[^1]);
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare: Given a prepared work directory with the three required
    /// builds and the optional webkit-worker written equal to the server, and no firefox-worker folder, When
    /// <c>agreement compare</c> runs, Then it exits 0 with <c>firefox-worker: not run</c>, a match line for
    /// webkit-worker and <c>agreement: match</c>; and <c>report/agreement.json</c> lists firefox-worker as
    /// {required false, status <c>NotRun</c>} and webkit-worker as {required false, status <c>Match</c>}.
    /// </summary>
    [Fact]
    public void OptionalBuildThatDidNotRunIsNotRun()
    {
        using var work = PrepareWithBuilds([.. RequiredBuilds, "webkit-worker"]);
        var (streamCount, stepTotal) = Totals(CommittedStreams(), DefaultSeedCount);

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 0, $"simcore agreement compare exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains("firefox-worker: not run", lines);
        Assert.Contains(MatchLine("webkit-worker", streamCount, stepTotal), lines);
        Assert.Equal("agreement: match", lines.LastOrDefault());

        var agreement = ReadJson(work.Combine("report", "agreement.json"));
        var firefox = Build(agreement, "firefox-worker");
        Assert.False(Flag(firefox, "required", "firefox-worker"), "firefox-worker should not be required");
        Assert.Equal("NotRun", Text(firefox, "status", "firefox-worker"));
        var webkit = Build(agreement, "webkit-worker");
        Assert.False(Flag(webkit, "required", "webkit-worker"), "webkit-worker should not be required");
        Assert.Equal("Match", Text(webkit, "status", "webkit-worker"));
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare: Given a prepared work directory whose chromium-worker folder
    /// has <c>flat-run-30hz.error.txt</c> holding <c>boom</c> instead of <c>flat-run-30hz.records</c>, When
    /// <c>agreement compare</c> runs, Then it exits 1 with
    /// <c>chromium-worker: diverged in 1 of &lt;n&gt; streams; first: flat-run-30hz at step &lt;first&gt;; tables match</c>,
    /// the report (on stdout and in <c>report/divergences/chromium-worker/flat-run-30hz.txt</c>) starts with
    /// the <c>DIVERGED</c> heading at the stream's first step and has the line <c>  error  boom</c>, and
    /// chromium-worker's <c>diverged[0]</c> is flat-run-30hz at that step with <c>error</c> <c>boom</c>.
    /// </summary>
    [Fact]
    public void BuildErrorFileIsADivergence()
    {
        using var work = PrepareWithBuilds(RequiredBuilds);
        File.Delete(work.Combine("chromium-worker", $"{FlatRun30}.records"));
        File.WriteAllText(work.Combine("chromium-worker", $"{FlatRun30}.error.txt"), Boom);
        var (streamCount, _) = Totals(CommittedStreams(), DefaultSeedCount);
        var firstStep = RecordedStream.Decode(RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs")).Steps[0].Step;

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 1, $"simcore agreement compare exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains($"chromium-worker: diverged in 1 of {streamCount} streams; first: {FlatRun30} at step {firstStep}; tables match", lines);
        Assert.Contains($"DIVERGED stream {FlatRun30}, build chromium-worker, first divergent step {firstStep}", lines);
        Assert.Contains($"  error  {Boom}", lines);
        Assert.Equal("agreement: diverged", lines.LastOrDefault());

        var reportFile = work.Combine("report", "divergences", "chromium-worker", $"{FlatRun30}.txt");
        Assert.True(File.Exists(reportFile), $"{reportFile} was not written");
        var report = Lines(File.ReadAllText(reportFile));
        Assert.True(report.Length > 0, $"{reportFile} is empty");
        Assert.Equal($"DIVERGED stream {FlatRun30}, build chromium-worker, first divergent step {firstStep}", report[0]);
        Assert.Contains($"  error  {Boom}", report);

        var chromium = Build(ReadJson(work.Combine("report", "agreement.json")), "chromium-worker");
        Assert.Equal("Diverged", Text(chromium, "status", "chromium-worker"));
        var diverged = Entries(chromium, "diverged", "chromium-worker");
        Assert.True(diverged.Length == 1, $"chromium-worker lists {diverged.Length} diverged streams, not 1");
        Assert.Equal(FlatRun30, Text(diverged[0], "stream", "chromium-worker diverged[0]"));
        Assert.Equal((long)firstStep, Number(diverged[0], "step", "chromium-worker diverged[0]"));
        Assert.Equal(Boom, Text(diverged[0], "error", "chromium-worker diverged[0]"));
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare: Given the diverged work directory of
    /// <see cref="CompareExitsOneOnADivergedBuildAndWritesItsReport"/> and a <c>--summary</c> file holding
    /// <c>existing\n</c>, When <c>agreement compare --summary &lt;file&gt;</c> runs, Then it exits 1 and the
    /// file still starts with <c>existing\n</c>, followed by the <c>## SimCore agreement</c> heading, the table
    /// header <c>| Build | Required | Status | Streams | Steps | First divergence | Tables |</c> and its
    /// separator, one seven-cell row per known build in contract order, and a fenced code block holding exactly
    /// node-interp's first divergence report.
    /// </summary>
    [Fact]
    public void SummaryAppendsTheSimCoreAgreementSection()
    {
        using var work = PreparePerturbed();
        var report = ExpectedReport(work, "node-interp", FlatRun30);
        var summary = work.Beside("summary.md");
        File.WriteAllText(summary, "existing\n");

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path, "--summary", summary);

        Assert.True(exit == 1, $"simcore agreement compare exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var text = File.ReadAllText(summary);
        Assert.StartsWith("existing\n", text);
        var appended = Lines(text["existing\n".Length..]);
        var heading = Array.FindIndex(appended, line => line.Length > 0);
        Assert.True(heading >= 0 && appended[heading] == SummaryHeading, $"the appended section does not start with \"{SummaryHeading}\":\n{text}");

        var header = Array.IndexOf(appended, SummaryTableHeader, heading + 1);
        Assert.True(header > heading, $"the summary has no table header \"{SummaryTableHeader}\" after its heading:\n{text}");
        Assert.True(
            header + 1 < appended.Length && SummarySeparator.IsMatch(appended[header + 1]),
            $"the table header is not followed by a seven-column separator row:\n{text}");
        var rows = appended.Skip(header + 2).TakeWhile(line => line.StartsWith('|')).ToArray();
        Assert.True(rows.Length == KnownBuilds.Length, $"the table has {rows.Length} rows, not one per known build ({KnownBuilds.Length}):\n{text}");
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.StartsWith($"| {KnownBuilds[i]} |", rows[i]);
            Assert.True(rows[i].Count(c => c == '|') == 8, $"row {i} does not have seven cells: \"{rows[i]}\"");
        }

        var open = Array.FindIndex(appended, header + 2 + rows.Length, line => line.StartsWith("```", StringComparison.Ordinal));
        Assert.True(open >= 0, $"the summary has no fenced code block after its table:\n{text}");
        var close = Array.IndexOf(appended, "```", open + 1);
        Assert.True(close > open, $"the code block opened at line {open} of the section is not closed:\n{text}");
        Assert.Equal(report, appended[(open + 1)..close]);
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare (a report or summary that cannot be written): Given the
    /// diverged work directory of <see cref="CompareExitsOneOnADivergedBuildAndWritesItsReport"/> and a
    /// <c>--summary</c> path that is a directory, When <c>agreement compare --summary &lt;dir&gt;</c> runs,
    /// Then it exits 2, not 1: stdout is the diverged run's seven build lines and node-interp's first report,
    /// then, in place of <c>agreement: diverged</c>, exactly one more line,
    /// <c>agreement: cannot run: the report cannot be written (&lt;message&gt;)</c>; and
    /// <c>report/agreement.json</c>, written before the summary, has outcome <c>Diverged</c>.
    /// </summary>
    [Fact]
    public void UnwritableSummaryAfterADivergenceExitsTwo()
    {
        using var work = PreparePerturbed();
        var (streamCount, stepTotal) = Totals(CommittedStreams(), DefaultSeedCount);
        var report = ExpectedReport(work, "node-interp", FlatRun30);
        var summary = Directory.CreateDirectory(work.Beside("summary-directory")).FullName;

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path, "--summary", summary);

        Assert.True(exit == 2, $"simcore agreement compare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        string[] before =
        [
            $"node-interp: diverged in 1 of {streamCount} streams; first: {FlatRun30} at step {PerturbedStep}; tables match",
            MatchLine("node-interp-nojit", streamCount, stepTotal),
            MatchLine("chromium-worker", streamCount, stepTotal),
            "firefox-worker: not run",
            "webkit-worker: not run",
            "node-interp-relinked: not run",
            "node-aot: not run",
            .. report,
        ];
        var lines = Lines(stdout);
        Assert.True(lines.Length == before.Length + 1, $"stdout has {lines.Length} lines, not {before.Length + 1}: {stdout}");
        Assert.Equal(before, lines[..^1]);
        Assert.StartsWith("agreement: cannot run: the report cannot be written (", lines[^1]);
        Assert.EndsWith(")", lines[^1]);
        Assert.Equal("Diverged", Text(ReadJson(work.Combine("report", "agreement.json")), "outcome", "agreement.json"));
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare and wasm-api.md#cross-build-tables: Given a prepared work
    /// directory with the three required builds equal to the server, except that node-interp's
    /// <c>tables.bin</c> has (<c>flipped-bit</c>) bit 0 of math row 0's <c>r0</c> flipped or (<c>error-file</c>)
    /// is replaced by <c>tables.error.txt</c> holding <c>boom</c>, When <c>agreement compare</c> runs, Then it
    /// exits 1 with <c>node-interp: streams match, &lt;n&gt; streams, &lt;steps&gt; steps; tables diverged</c> (or
    /// <c>tables missing</c>) and <c>agreement: diverged</c>; <c>report/divergences/node-interp/tables.txt</c>
    /// holds <c>TablesComparison</c>'s first-mismatch line (which stdout also prints) or <c>boom</c>; and
    /// node-interp's <c>agreement.json</c> entry is <c>Diverged</c> with no diverged stream and tables
    /// {<c>Diverged</c> or <c>Missing</c>, that line or <c>boom</c>}.
    /// </summary>
    [Theory]
    [MemberData(nameof(TablesMismatchCases))]
    public void CompareReportsATablesMismatchAsDiverged(string caseName)
    {
        using var work = PrepareWithBuilds(RequiredBuilds);
        var (streamCount, stepTotal) = Totals(CommittedStreams(), DefaultSeedCount);
        var tables = work.Combine("node-interp", WorkDirectories.TablesFile);
        string mismatch;
        string status;
        string word;
        switch (caseName)
        {
            case "flipped-bit":
                var results = File.ReadAllBytes(tables);
                results[MathRow0R0Offset] ^= 0x01;
                File.WriteAllBytes(tables, results);
                var comparison = TablesComparison.Compare(
                    "node-interp", File.ReadAllBytes(work.Combine("tables", "inputs.bin")), File.ReadAllBytes(work.Combine("server", WorkDirectories.TablesFile)), results);
                Assert.True(comparison.Status == TablesStatus.Diverged, $"TablesComparison gives {comparison.Status} for the flipped bit, not Diverged");
                mismatch = comparison.FirstMismatch ?? throw new InvalidOperationException("TablesComparison gave no first mismatch.");
                Assert.StartsWith("tables: build node-interp, first mismatch at math row 0: ", mismatch);
                status = "Diverged";
                word = "diverged";
                break;
            case "error-file":
                File.Delete(tables);
                File.WriteAllText(work.Combine("node-interp", "tables.error.txt"), Boom);
                mismatch = Boom;
                status = "Missing";
                word = "missing";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 1, $"{caseName}: simcore agreement compare exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains($"node-interp: streams match, {streamCount} streams, {stepTotal} steps; tables {word}", lines);
        if (caseName == "flipped-bit")
        {
            Assert.Contains(mismatch, lines);
        }

        Assert.Equal("agreement: diverged", lines.LastOrDefault());
        var tablesReport = work.Combine("report", "divergences", "node-interp", "tables.txt");
        Assert.True(File.Exists(tablesReport), $"{caseName}: {tablesReport} was not written");
        Assert.Equal(mismatch, File.ReadAllText(tablesReport).TrimEnd('\n'));

        var nodeInterp = Build(ReadJson(work.Combine("report", "agreement.json")), "node-interp");
        Assert.Equal("Diverged", Text(nodeInterp, "status", "node-interp"));
        Assert.True(Entries(nodeInterp, "diverged", "node-interp").Length == 0, $"{caseName}: node-interp lists a diverged stream although its streams match");
        AssertTablesEntry(nodeInterp, status, mismatch, "node-interp");
    }

    /// <summary>
    /// US2 scenario 5 and contracts/simcore-cli.md#agreement-compare: Given a prepared work directory whose
    /// node-interp build diverges on flat-run-30hz (one ulp at step 15) and whose node-interp-nojit and
    /// chromium-worker builds equal the server, and whose <c>manifest.json</c> has (<c>rules-revision</c>)
    /// <c>rulesRevision</c> 2, (<c>recorded-stream-format</c>) <c>formats.recordedStream</c> 2,
    /// (<c>manifest-format</c>) <c>formatVersion</c> 2, or (<c>manifest-format-reshaped</c>) <c>formatVersion</c>
    /// 2 with no <c>formats</c> member and a <c>rulesRevision</c> that is a string, a manifest of another format
    /// version whose other versions are not where version 1 puts them, or (<c>manifest-format-and-rules</c>)
    /// <c>formatVersion</c> 2 with no <c>formats</c> and <c>rulesRevision</c> 2, which is still compared, When
    /// <c>agreement compare</c> runs, Then it exits 2, stdout is exactly the lines of
    /// <paramref name="expectedLine"/> (one per difference, in the contract's order, each naming the work
    /// directory and both values), then <c>agreement: not comparable</c>, with no build line and no <c>DIVERGED</c> report, and no
    /// <c>report/</c> is written: a work directory from other rules is refused instead of reporting the
    /// divergence it holds, and nothing is compared or written.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotComparableWorkCases))]
    public void WorkDirectoryFromAnotherRulesRevisionIsNotComparable(string caseName, string expectedLine)
    {
        using var work = PreparePerturbed();
        var manifestPath = work.Combine(ManifestFile);
        var manifest = ReadJson(manifestPath);
        switch (caseName)
        {
            case "rules-revision":
                manifest["rulesRevision"] = 2;
                break;
            case "recorded-stream-format":
                Child(manifest, "formats", ManifestFile)["recordedStream"] = 2;
                break;
            case "manifest-format":
                manifest["formatVersion"] = 2;
                break;
            case "manifest-format-reshaped":
                manifest["formatVersion"] = 2;
                Assert.True(manifest.Remove("formats"), $"{ManifestFile} has no formats member to remove");
                manifest["rulesRevision"] = "one";
                break;
            case "manifest-format-and-rules":
                manifest["formatVersion"] = 2;
                Assert.True(manifest.Remove("formats"), $"{ManifestFile} has no formats member to remove");
                manifest["rulesRevision"] = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var expected = expectedLine.Replace("<work>", work.Path, StringComparison.Ordinal).Split('\n');

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 2, $"{caseName}: simcore agreement compare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal([.. expected, "agreement: not comparable"], Lines(stdout));
        var report = work.Combine("report");
        Assert.False(Directory.Exists(report), $"{caseName}: agreement compare wrote {report} for a work directory that is not comparable");
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare: Given a prepared work directory with the three required
    /// builds equal to the server and (<c>no-manifest</c>) no <c>manifest.json</c>, (<c>invalid-manifest</c>) a
    /// manifest that is not valid JSON, (<c>missing-server-records</c>) no
    /// <c>server/flat-run-30hz.records</c>, (<c>missing-server-tables</c>) no <c>server/tables.bin</c>,
    /// (<c>unknown-required-build</c>) <c>--require node-interp,unknown-build</c>,
    /// (<c>truncated-server-records</c>) a <c>server/flat-run-30hz.records</c> cut 3 bytes short,
    /// (<c>corrupt-work-stream</c>) a <c>streams/flat-run-30hz.mmrs</c> holding 64 zero bytes,
    /// (<c>path-outside-work</c>) a manifest whose first stream's <c>file</c> is <c>../outside.mmrs</c>,
    /// (<c>missing-manifest-member</c>) a manifest without <c>tables</c>, (<c>invalid-stream-name</c>) a
    /// manifest whose first stream is named <c>Tables</c>, (<c>unwritable-summary</c>) a <c>--summary</c>
    /// that is a directory, (<c>truncated-server-records-no-builds</c>) the records cut 3 bytes short with no
    /// build folder at all, or (<c>truncated-server-tables</c>) a <c>server/tables.bin</c> cut 1 byte short,
    /// When <c>agreement compare</c> runs, Then it exits 2 and its last line is
    /// <c>agreement: cannot run: &lt;reason&gt;</c>, the reason naming the manifest, the missing, short or
    /// refused file by its work-directory path, <c>unknown-build</c>, the manifest member, or the report that
    /// cannot be written. A cut records file is exactly
    /// <c>server/flat-run-30hz.records: 29 whole records and &lt;last record's length − 3&gt; more bytes; the stream has 30 expected hashes</c>,
    /// with or without builds, and a cut tables file exactly
    /// <c>server/tables.bin (&lt;n − 1&gt; bytes) and tables/inputs.bin (&lt;m&gt; bytes) do not hold the same whole table</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(CannotRunCases))]
    public void CompareCannotRunWithoutItsInputs(string caseName)
    {
        using var work = PrepareWithBuilds(RequiredBuilds);
        string[] extra = [];
        string named;
        switch (caseName)
        {
            case "no-manifest":
                File.Delete(work.Combine(ManifestFile));
                named = ManifestFile;
                break;
            case "invalid-manifest":
                File.WriteAllText(work.Combine(ManifestFile), "{ \"formatVersion\": ");
                named = ManifestFile;
                break;
            case "missing-server-records":
                File.Delete(work.Combine(WorkDirectories.ServerFolder, $"{FlatRun30}.records"));
                named = $"server/{FlatRun30}.records";
                break;
            case "missing-server-tables":
                File.Delete(work.Combine(WorkDirectories.ServerFolder, WorkDirectories.TablesFile));
                named = "server/tables.bin";
                break;
            case "unknown-required-build":
                extra = ["--require", "node-interp,unknown-build"];
                named = "unknown-build";
                break;
            case "truncated-server-records":
            case "truncated-server-records-no-builds":
                if (caseName == "truncated-server-records-no-builds")
                {
                    foreach (var build in RequiredBuilds)
                    {
                        Directory.Delete(work.Combine(build), recursive: true);
                    }
                }

                var serverRecords = work.Combine(WorkDirectories.ServerFolder, $"{FlatRun30}.records");
                var whole = File.ReadAllBytes(serverRecords);
                var records = ReadRecords(whole, $"server/{FlatRun30}");
                Assert.True(records.Count == 30, $"server/{FlatRun30}.records holds {records.Count} records, not 30");
                File.WriteAllBytes(serverRecords, whole[..^3]);
                // A record is u32 step, u32 stateLength, the state and u64 hash: 16 bytes besides the state.
                var lastLength = 16 + records[^1].State.Length;
                named = $"server/{FlatRun30}.records: 29 whole records and {lastLength - 3} more bytes; the stream has 30 expected hashes";
                break;
            case "truncated-server-tables":
                var serverTables = work.Combine(WorkDirectories.ServerFolder, WorkDirectories.TablesFile);
                var tables = File.ReadAllBytes(serverTables);
                File.WriteAllBytes(serverTables, tables[..^1]);
                var inputsLength = new FileInfo(work.Combine("tables", "inputs.bin")).Length;
                named = $"server/tables.bin ({tables.Length - 1} bytes) and tables/inputs.bin ({inputsLength} bytes) do not hold the same whole table";
                break;
            case "corrupt-work-stream":
                File.WriteAllBytes(work.Combine("streams", $"{FlatRun30}.mmrs"), new byte[64]);
                named = $"streams/{FlatRun30}.mmrs: ";
                break;
            case "path-outside-work":
                EditManifest(work, manifest => Entries(manifest, "streams", ManifestFile)[0]["file"] = "../outside.mmrs");
                named = "../outside.mmrs is not a relative path inside the work directory";
                break;
            case "missing-manifest-member":
                EditManifest(work, manifest => manifest.Remove("tables"));
                named = "manifest.json: $.tables is missing";
                break;
            case "invalid-stream-name":
                EditManifest(work, manifest => Entries(manifest, "streams", ManifestFile)[0]["name"] = "Tables");
                named = "manifest.json: $.streams[0].name Tables is not";
                break;
            case "unwritable-summary":
                extra = ["--summary", Directory.CreateDirectory(work.Beside("summary-directory")).FullName];
                named = "the report cannot be written";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var (exit, stdout, stderr) = SimcoreCli.Run(["agreement", "compare", "--work", work.Path, .. extra]);

        Assert.True(exit == 2, $"{caseName}: simcore agreement compare exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(lines.Length > 0, $"{caseName}: simcore agreement compare printed nothing; stderr: {stderr}");
        var last = lines[^1].Replace('\\', '/');
        Assert.StartsWith("agreement: cannot run: ", last);
        Assert.Contains(named, last, StringComparison.Ordinal);
        if (caseName.StartsWith("truncated-server-", StringComparison.Ordinal))
        {
            Assert.Equal($"agreement: cannot run: {named}", last);
        }
    }

    /// <summary>
    /// contracts/simcore-cli.md#agreement-compare ("folders with other names are ignored"): Given a prepared work
    /// directory with the three required builds equal to the server, When <c>agreement compare</c> runs before
    /// and after a <c>&lt;work&gt;/other-build/</c> folder holding garbage records and tables is added, Then
    /// both runs exit 0 and print the same lines, none naming <c>other-build</c>.
    /// </summary>
    [Fact]
    public void UnknownFoldersAreIgnored()
    {
        using var work = PrepareWithBuilds(RequiredBuilds);
        var (firstExit, firstStdout, firstStderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);
        Assert.True(firstExit == 0, $"simcore agreement compare exited {firstExit}, expected 0; stdout: {firstStdout}; stderr: {firstStderr}");

        var other = Directory.CreateDirectory(work.Combine("other-build")).FullName;
        File.WriteAllBytes(Path.Combine(other, $"{FlatRun30}.records"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(other, WorkDirectories.TablesFile), new byte[] { 4, 5, 6 });

        var (exit, stdout, stderr) = SimcoreCli.Run("agreement", "compare", "--work", work.Path);

        Assert.True(exit == 0, $"with other-build/: simcore agreement compare exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(Lines(firstStdout), Lines(stdout));
        Assert.DoesNotContain("other-build", stdout, StringComparison.Ordinal);
    }

    /// <summary>Rewrites the work directory's <c>manifest.json</c> with <paramref name="edit"/> applied to it.</summary>
    private static void EditManifest(PreparedWork work, Action<JsonObject> edit)
    {
        var path = work.Combine(ManifestFile);
        var manifest = ReadJson(path);
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    /// <summary>One prepared work directory entry of <c>streams[]</c>, checked against the contract's table.</summary>
    private static void AssertStreamEntry(
        string work, JsonObject entry, CommittedStream? committed, int seed, byte[] testWorldFile, CollisionWorld testWorld)
    {
        var name = Text(entry, "name", "a manifest stream");
        var label = $"manifest stream {name}";
        Assert.Matches(StreamNamePattern, name);
        Assert.NotEqual("tables", name);

        var file = Text(entry, "file", label);
        Assert.Equal($"streams/{name}.mmrs", file);
        var bytes = ReadWorkFile(work, file, label);
        var stream = RecordedStream.Decode(bytes);
        if (committed is not null)
        {
            Assert.Equal("committed", Text(entry, "origin", label));
            Assert.True(bytes.AsSpan().SequenceEqual(committed.Bytes), $"{label}: {file} is not a copy of simcore/streams/{name}.mmrs");
            Assert.Equal((long)stream.Seed, Number(entry, "seed", label));
        }
        else
        {
            Assert.Equal("generated", Text(entry, "origin", label));
            Assert.Equal((long)seed, Number(entry, "seed", label));
            Assert.True(stream.Seed == (ulong)seed, $"{label}: the header seed is {stream.Seed}, not {seed}");
        }

        var checksum = X16(stream.CollisionChecksum);
        Assert.Equal(checksum, Text(entry, "collisionChecksum", label));
        var collision = Text(entry, "collision", label);
        Assert.Equal($"collision/{checksum}.mmcd", collision);
        Assert.True(
            ReadWorkFile(work, collision, label).AsSpan().SequenceEqual(testWorldFile),
            $"{label}: {collision} is not a copy of simcore/content/test-world.mmcd");
        Assert.Equal((long)stream.StepHz, Number(entry, "stepRateHz", label));
        Assert.Equal((long)stream.Steps.Count, Number(entry, "steps", label));
        Assert.Equal((long)stream.CharacterCount, Number(entry, "characters", label));

        var serverRecords = Text(entry, "serverRecords", label);
        Assert.Equal($"server/{name}.records", serverRecords);
        var records = ReadWorkFile(work, serverRecords, label);
        var expectedRecords = StreamRunner.Run(bytes, new[] { testWorld });
        Assert.True(
            records.AsSpan().SequenceEqual(expectedRecords),
            $"{label}: {serverRecords} ({records.Length} bytes) is not StreamRunner.Run of the stream ({expectedRecords.Length} bytes)");
        var parsed = ReadRecords(records, label);
        Assert.True(parsed.Count == stream.Steps.Count, $"{label}: {parsed.Count} server records for {stream.Steps.Count} steps");
        for (var s = 0; s < parsed.Count; s++)
        {
            Assert.True(
                parsed[s].Hash == stream.Steps[s].ExpectedHash,
                $"{label}: step {stream.Steps[s].Step} expects {stream.Steps[s].ExpectedHash:x16}; the server hashed {parsed[s].Hash:x16}");
        }

        Assert.Equal(X16(parsed[^1].Hash), Text(entry, "finalHash", label));
        AssertBatch(work, entry, name, stream, label);
    }

    /// <summary>
    /// The entry's <c>batch</c>: null unless every step carries exactly one command, for character index 0; then
    /// the three paths, which hold the stream's config block, its initial state and its commands at 17 bytes a
    /// step, and the header's match salt in x16.
    /// </summary>
    private static void AssertBatch(string work, JsonObject entry, string name, RecordedStream stream, string label)
    {
        var batchable = stream.Steps.All(step => step.Commands.Length == 1 && step.Commands[0].CharacterIndex == 0);
        Assert.True(entry.TryGetPropertyValue("batch", out var node), $"{label} has no \"batch\"");
        if (!batchable)
        {
            Assert.True(node is null, $"{label}: batch is {node?.ToJsonString()}, but not every step has exactly one command for character index 0");
            return;
        }

        Assert.True(node is JsonObject, $"{label}: batch is null, but every step has one command for character index 0");
        var batch = node!.AsObject();
        var config = Text(batch, "config", label);
        var initialState = Text(batch, "initialState", label);
        var commands = Text(batch, "commands", label);
        Assert.Equal($"batch/{name}/config.bin", config);
        Assert.Equal($"batch/{name}/initial-state.bin", initialState);
        Assert.Equal($"batch/{name}/commands.bin", commands);
        Assert.Equal(X16(stream.MatchSalt), Text(batch, "matchSalt", label));

        Assert.True(
            ReadWorkFile(work, config, label).AsSpan().SequenceEqual(ConfigBlock.Encode(stream.Config)),
            $"{label}: {config} is not the stream's config block");
        Assert.True(
            ReadWorkFile(work, initialState, label).AsSpan().SequenceEqual(stream.InitialState.Span),
            $"{label}: {initialState} is not the stream's initial state");
        var expected = new byte[stream.Steps.Count * InputCodec.EncodedSize];
        for (var s = 0; s < stream.Steps.Count; s++)
        {
            var command = stream.Steps[s].Commands[0].Command;
            Assert.True(
                InputCodec.TryEncode(in command, expected.AsSpan(s * InputCodec.EncodedSize, InputCodec.EncodedSize)),
                $"{label}: steps[{s}]'s command does not encode");
        }

        var written = ReadWorkFile(work, commands, label);
        Assert.True(
            written.AsSpan().SequenceEqual(expected),
            $"{label}: {commands} ({written.Length} bytes) is not the stream's {stream.Steps.Count} commands at {InputCodec.EncodedSize} bytes each");
    }

    /// <summary><c>streams/seed-001.mmrs</c> is <c>StreamGenerator.Build(1, default.json, test world)</c> with its expected hashes filled, byte for byte.</summary>
    private static void AssertFirstGeneratedStream(string work, CollisionWorld testWorld)
    {
        var built = StreamGenerator.Build(1, TestConfigs.Default(), testWorld);
        var expected = ScenarioDefinition.WithExpectedHashes(built, StreamRunner.Run(built.Encode(), new[] { testWorld })).Encode();
        var written = ReadWorkFile(work, "streams/seed-001.mmrs", "seed-001");
        Assert.True(
            written.AsSpan().SequenceEqual(expected),
            $"streams/seed-001.mmrs ({written.Length} bytes) is not StreamGenerator.Build(1, …) with its expected hashes filled ({expected.Length} bytes)");
    }

    /// <summary>The manifest's <c>perturbation</c> is flat-run-30hz at its middle step, 15, naming <c>characters[0].position.x</c>.</summary>
    private static void AssertPerturbation(JsonObject manifest, List<CommittedStream> committed)
    {
        var flatRun = committed.Single(stream => stream.Name == FlatRun30).Stream;
        var middle = flatRun.Steps[(flatRun.Steps.Count - 1) / 2].Step;
        Assert.True(middle == PerturbedStep, $"{FlatRun30}'s step at index ({flatRun.Steps.Count} - 1) / 2 is {middle}, not {PerturbedStep}");
        var perturbation = Child(manifest, "perturbation", ManifestFile);
        Assert.Equal(FlatRun30, Text(perturbation, "stream", "perturbation"));
        Assert.Equal((long)PerturbedStep, Number(perturbation, "step", "perturbation"));
        Assert.Equal(PositionX, Text(perturbation, "field", "perturbation"));
    }

    /// <summary>
    /// The manifest's three <c>refusals</c>: their names, files and expected texts; the two edited copies of
    /// flat-run-30hz byte for byte; a small collision file whose checksum flat-run-30hz does not name; and each
    /// refused by <see cref="StreamRunner"/> before step 0 with a message containing its expected text.
    /// </summary>
    private static void AssertRefusals(string work, JsonObject manifest, List<CommittedStream> committed)
    {
        var flatRun = committed.Single(stream => stream.Name == FlatRun30);
        var flatRunCollision = $"collision/{X16(flatRun.Stream.CollisionChecksum)}.mmcd";
        var refusals = Entries(manifest, "refusals", ManifestFile);
        Assert.Equal(["unsupported-version", "rules-revision", "collision-missing"], refusals.Select(refusal => Text(refusal, "name", "a refusal")));
        (string Stream, string Expected)[] expected =
        [
            ("refusals/unsupported-version.mmrs", "unsupported version 2; this reader supports version 1"),
            ("refusals/rules-revision.mmrs", "rulesRevision at offset 8"),
            ($"streams/{FlatRun30}.mmrs", "collisionChecksum at offset 36"),
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Stream, Text(refusals[i], "stream", $"refusals[{i}]"));
            Assert.Equal(expected[i].Expected, Text(refusals[i], "expected", $"refusals[{i}]"));
        }

        Assert.Equal(flatRunCollision, Text(refusals[0], "collision", "refusals[0]"));
        Assert.Equal(flatRunCollision, Text(refusals[1], "collision", "refusals[1]"));
        var missingCollision = Text(refusals[2], "collision", "refusals[2]");
        var small = CollisionFile.Decode(ReadWorkFile(work, missingCollision, "refusals[2]"));
        Assert.Equal($"collision/{X16(small.Checksum)}.mmcd", missingCollision);
        Assert.True(
            small.Checksum != flatRun.Stream.CollisionChecksum,
            $"the collision-missing refusal's collision data {small.Checksum:x16} is the one {FlatRun30} names");

        var unsupported = (byte[])flatRun.Bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(unsupported.AsSpan(VersionOffset, 2), 2);
        Assert.True(
            ReadWorkFile(work, expected[0].Stream, "refusals[0]").AsSpan().SequenceEqual(unsupported),
            $"{expected[0].Stream} is not {FlatRun30} with the u16 at offset {VersionOffset} set to 2");
        var revision = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(revision, SimCoreVersion.RulesRevision + 1u);
        var otherRules = ReadWorkFile(work, expected[1].Stream, "refusals[1]");
        Assert.True(
            otherRules.AsSpan().SequenceEqual(StreamBytes.WithBytes(flatRun.Bytes, RulesRevisionOffset, revision)),
            $"{expected[1].Stream} is not {FlatRun30} with its rules revision raised by one and its trailer rewritten");
        Assert.Equal(SimCoreVersion.RulesRevision + 1u, RecordedStream.Decode(otherRules).RulesRevision);

        for (var i = 0; i < refusals.Length; i++)
        {
            var streamBytes = ReadWorkFile(work, Text(refusals[i], "stream", $"refusals[{i}]"), $"refusals[{i}]");
            var collisionBytes = ReadWorkFile(work, Text(refusals[i], "collision", $"refusals[{i}]"), $"refusals[{i}]");
            byte[]? records = null;
            var thrown = Assert.Throws<CoreFormatException>(() => records = StreamRunner.Run(streamBytes, collisionBytes));
            Assert.Null(records);
            Assert.Contains(expected[i].Expected, thrown.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The manifest's <c>tables</c> paths, and the files they name: <c>CrossBuildTables.BuildInputs()</c>, the
    /// config block of default.json, and <c>CrossBuildTables.Evaluate</c> of the two.
    /// </summary>
    private static void AssertTables(string work, JsonObject manifest)
    {
        var tables = Child(manifest, "tables", ManifestFile);
        Assert.Equal("tables/inputs.bin", Text(tables, "inputs", "tables"));
        Assert.Equal("tables/config.bin", Text(tables, "config", "tables"));
        Assert.Equal("server/tables.bin", Text(tables, "serverResults", "tables"));

        var inputs = CrossBuildTables.BuildInputs();
        var block = ConfigBlock.Encode(TestConfigs.Default());
        var results = CrossBuildTables.Evaluate(inputs, block);
        Assert.True(ReadWorkFile(work, "tables/inputs.bin", "tables").AsSpan().SequenceEqual(inputs), "tables/inputs.bin is not CrossBuildTables.BuildInputs()");
        Assert.True(ReadWorkFile(work, "tables/config.bin", "tables").AsSpan().SequenceEqual(block), "tables/config.bin is not the config block of default.json");
        Assert.True(ReadWorkFile(work, "server/tables.bin", "tables").AsSpan().SequenceEqual(results), "server/tables.bin is not CrossBuildTables.Evaluate of the inputs and the config block");
    }

    /// <summary>No file in the work directory was last written after <c>manifest.json</c>.</summary>
    private static void AssertManifestWrittenLast(string work)
    {
        var manifest = Path.Combine(work, ManifestFile);
        var manifestTime = File.GetLastWriteTimeUtc(manifest);
        foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories))
        {
            var time = File.GetLastWriteTimeUtc(file);
            Assert.True(
                file == manifest || time <= manifestTime,
                $"{Path.GetRelativePath(work, file)} was last written at {time:O}, after {ManifestFile} at {manifestTime:O}");
        }
    }

    /// <summary>A build's <c>tables</c> entry in <c>agreement.json</c>: its status and first mismatch (null for none).</summary>
    private static void AssertTablesEntry(JsonObject build, string status, string? firstMismatch, string label)
    {
        var tables = Child(build, "tables", label);
        Assert.Equal(status, Text(tables, "status", $"{label} tables"));
        if (firstMismatch is null)
        {
            AssertJsonNull(tables, "firstMismatch", $"{label} tables");
        }
        else
        {
            Assert.Equal(firstMismatch, Text(tables, "firstMismatch", $"{label} tables"));
        }
    }

    /// <summary>A prepared work directory with <paramref name="builds"/> written equal to the server.</summary>
    private static PreparedWork PrepareWithBuilds(params string[] builds)
    {
        var work = WorkDirectories.Prepare();
        try
        {
            foreach (var build in builds)
            {
                WorkDirectories.WriteBuild(work.Path, build);
            }

            return work;
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A prepared work directory whose node-interp folder has flat-run-30hz perturbed by one ulp at step 15, and
    /// whose node-interp-nojit and chromium-worker folders equal the server.
    /// </summary>
    private static PreparedWork PreparePerturbed()
    {
        var work = WorkDirectories.Prepare();
        try
        {
            WorkDirectories.WriteBuild(work.Path, "node-interp", perturbStream: FlatRun30, perturbStep: PerturbedStep);
            WorkDirectories.WriteBuild(work.Path, "node-interp-nojit");
            WorkDirectories.WriteBuild(work.Path, "chromium-worker");
            return work;
        }
        catch
        {
            work.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The lines of <c>DivergenceReport.CompareBuild</c>'s report for <paramref name="build"/>'s records of
    /// <paramref name="stream"/> against the server's and the stream's expected hashes: the report the command
    /// must print and write.
    /// </summary>
    private static string[] ExpectedReport(PreparedWork work, string build, string stream)
    {
        var server = File.ReadAllBytes(work.Combine(WorkDirectories.ServerFolder, stream + WorkDirectories.RecordsExtension));
        var records = File.ReadAllBytes(work.Combine(build, stream + WorkDirectories.RecordsExtension));
        var decoded = RecordedStream.Decode(File.ReadAllBytes(work.Combine("streams", stream + ".mmrs")));
        var expectedHashes = decoded.Steps.Select(step => step.ExpectedHash).ToArray();
        var comparison = DivergenceReport.CompareBuild(stream, build, server, records, expectedHashes);
        return Lines(comparison.Report);
    }

    /// <summary>The record of step <paramref name="step"/> in <c>&lt;work&gt;/&lt;folder&gt;/&lt;stream&gt;.records</c>.</summary>
    private static StepRecordBytes RecordAt(PreparedWork work, string folder, string stream, uint step)
    {
        var records = ReadRecords(File.ReadAllBytes(work.Combine(folder, stream + WorkDirectories.RecordsExtension)), $"{folder}/{stream}");
        var matches = records.Where(record => record.Step == step).ToArray();
        Assert.True(matches.Length == 1, $"{folder}/{stream}.records holds {matches.Length} records of step {step}, not 1");
        return matches[0];
    }

    /// <summary><c>&lt;build&gt;: match, &lt;n&gt; streams, &lt;steps&gt; steps, tables match</c>.</summary>
    private static string MatchLine(string build, int streams, long steps) =>
        $"{build}: match, {streams} streams, {steps} steps, tables match";

    /// <summary>
    /// The streams a work directory prepared with <paramref name="generated"/> seeds holds, and their total step
    /// count: the committed streams, then <paramref name="generated"/> generated ones of 300 steps each.
    /// </summary>
    private static (int Streams, long Steps) Totals(List<CommittedStream> committed, int generated) =>
        (committed.Count + generated, committed.Sum(stream => (long)stream.Stream.Steps.Count) + ((long)generated * GeneratedStepCount));

    /// <summary>Every committed <c>simcore/streams/*.mmrs</c>, read through <see cref="RepoPaths"/> and decoded, in ordinal name order.</summary>
    private static List<CommittedStream> CommittedStreams()
    {
        var names = Directory.GetFiles(RepoPaths.Combine("simcore", "streams"))
            .Where(path => path.EndsWith(".mmrs", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToList();
        names.Sort(StringComparer.Ordinal);
        Assert.True(names.Count > 0, "simcore/streams holds no committed .mmrs stream");
        return names.ConvertAll(name =>
        {
            var bytes = RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs");
            return new CommittedStream(name, bytes, RecordedStream.Decode(bytes));
        });
    }

    /// <summary>The committed <c>simcore/content/test-world.mmcd</c>, read through <see cref="RepoPaths"/>.</summary>
    private static byte[] TestWorldFile() => RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");

    /// <summary>The checksum of the committed test world.</summary>
    private static ulong TestWorldChecksum() => CollisionFile.Decode(TestWorldFile()).Checksum;

    /// <summary>
    /// Every packed step record in <paramref name="records"/> (<c>u32 step | u32 stateLength | state | u64 hash</c>,
    /// little-endian; contracts/state-serialization.md#step-record), read with <see cref="BinaryPrimitives"/>; the
    /// records must fill the bytes exactly.
    /// </summary>
    private static List<StepRecordBytes> ReadRecords(byte[] records, string label)
    {
        var list = new List<StepRecordBytes>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(records.Length - offset >= 16, $"{label}: {records.Length - offset} bytes at {offset} are too few for a step record");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            Assert.True(
                records.Length - offset - 16L >= length,
                $"{label}: the record at {offset} declares a {length}-byte state; {records.Length - offset - 16} bytes follow");
            var state = records.AsSpan(offset + 8, (int)length).ToArray();
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset + 8 + (int)length));
            list.Add(new StepRecordBytes(step, state, hash));
            offset += 16 + (int)length;
        }

        return list;
    }

    /// <summary>The bytes of the work directory file at the manifest-relative <paramref name="relative"/> path, which must exist.</summary>
    private static byte[] ReadWorkFile(string work, string relative, string label)
    {
        var path = Path.Combine(work, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{label}: {relative} does not exist in the work directory");
        return File.ReadAllBytes(path);
    }

    private static string X16(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Copies the committed <c>simcore/&lt;folder&gt;/&lt;file&gt;</c>, read through <see cref="RepoPaths"/>, into <paramref name="target"/>.</summary>
    private static void CopyCommitted(string target, string folder, string file) =>
        File.WriteAllBytes(Path.Combine(target, file), RepoPaths.ReadAllBytes("simcore", folder, file));

    /// <summary>Creates a sparse file of <paramref name="length"/> zero bytes with <see cref="FileStream.SetLength"/>.</summary>
    private static void CreateSparse(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    /// <summary>The JSON object in the file at <paramref name="path"/>, which must exist.</summary>
    private static JsonObject ReadJson(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        var node = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(node is JsonObject, $"{path} does not hold a JSON object");
        return node!.AsObject();
    }

    private static JsonNode Member(JsonObject obj, string key, string label)
    {
        Assert.True(obj.TryGetPropertyValue(key, out var value) && value is not null, $"{label} has no non-null \"{key}\"");
        return value!;
    }

    private static string Text(JsonObject obj, string key, string label) => Member(obj, key, label).GetValue<string>();

    private static long Number(JsonObject obj, string key, string label) => Member(obj, key, label).GetValue<long>();

    private static bool Flag(JsonObject obj, string key, string label) => Member(obj, key, label).GetValue<bool>();

    private static JsonObject Child(JsonObject obj, string key, string label) => Member(obj, key, label).AsObject();

    private static JsonArray Items(JsonObject obj, string key, string label) => Member(obj, key, label).AsArray();

    /// <summary>The objects of the array <paramref name="key"/>.</summary>
    private static JsonObject[] Entries(JsonObject obj, string key, string label) =>
        [.. Items(obj, key, label).Select(item => item!.AsObject())];

    /// <summary>Asserts <paramref name="key"/> is present and JSON <c>null</c>.</summary>
    private static void AssertJsonNull(JsonObject obj, string key, string label)
    {
        Assert.True(obj.TryGetPropertyValue(key, out var value), $"{label} has no \"{key}\"");
        Assert.True(value is null, $"{label}.{key} is {value?.ToJsonString()}, not null");
    }

    /// <summary>The one entry of <c>agreement.json</c>'s <c>builds</c> for <paramref name="build"/>.</summary>
    private static JsonObject Build(JsonObject agreement, string build)
    {
        var matches = Entries(agreement, "builds", "agreement.json").Where(entry => Text(entry, "build", "a build") == build).ToArray();
        Assert.True(matches.Length == 1, $"agreement.json lists {matches.Length} entries for {build}, not 1");
        return matches[0];
    }

    /// <summary>
    /// <c>&lt;relative path&gt; &lt;XxHash64&gt;</c> for every file under <paramref name="root"/>, in ordinal order,
    /// each read through a read-only stream.
    /// </summary>
    private static string[] HashTree(string root)
    {
        var entries = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var hash = new XxHash64();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash.Append(stream);
            }

            entries.Add($"{Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')} {hash.GetCurrentHashAsUInt64():x16}");
        }

        entries.Sort(StringComparer.Ordinal);
        return [.. entries];
    }

    /// <summary>
    /// Every directory (as <c>&lt;path&gt;/</c>) and file under <paramref name="root"/> by its relative path, a file
    /// with its length, the XxHash128 of its bytes, read through a read-only stream, and its last-write time.
    /// </summary>
    private static SortedDictionary<string, FileState> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, FileState>(StringComparer.Ordinal);
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
        {
            snapshot.Add(Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/') + "/", default);
        }

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var hash = new XxHash128();
            long length;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash.Append(stream);
                length = stream.Length;
            }

            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            snapshot.Add(relative, new FileState(length, hash.GetCurrentHashAsUInt128(), File.GetLastWriteTimeUtc(file)));
        }

        return snapshot;
    }

    /// <summary><c>added</c>, <c>removed</c> and <c>changed</c> entries between two snapshots, in ordinal order.</summary>
    private static string[] Differences(SortedDictionary<string, FileState> before, SortedDictionary<string, FileState> after)
    {
        var differences = new List<string>();
        foreach (var (path, state) in before)
        {
            if (!after.TryGetValue(path, out var now))
            {
                differences.Add($"removed {path}");
            }
            else if (now != state)
            {
                differences.Add($"changed {path}");
            }
        }

        foreach (var path in after.Keys)
        {
            if (!before.ContainsKey(path))
            {
                differences.Add($"added {path}");
            }
        }

        differences.Sort(StringComparer.Ordinal);
        return [.. differences];
    }

    /// <summary>The lines of <paramref name="text"/>, split on <c>\n</c>, without the empty string after a final newline.</summary>
    private static string[] Lines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>A committed stream: its name, its file's bytes and the file decoded.</summary>
    private sealed record CommittedStream(string Name, byte[] Bytes, RecordedStream Stream);

    /// <summary>One step record read back: its step number, state bytes and hash.</summary>
    private sealed record StepRecordBytes(uint Step, byte[] State, ulong Hash);

    /// <summary>A file's length, content hash and last-write time; <c>default</c> for a directory.</summary>
    private readonly record struct FileState(long Length, UInt128 Hash, DateTime LastWriteUtc);
}
