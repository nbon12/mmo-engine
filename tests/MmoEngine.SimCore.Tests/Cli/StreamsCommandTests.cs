using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// <c>simcore streams record</c> and <c>simcore streams verify</c>
/// (contracts/simcore-cli.md#streams-record, contracts/simcore-cli.md#streams-verify, FR-028, US1.7).
/// <c>streams record</c> is the only way the committed expected hashes change: it builds the selected
/// scenarios with <see cref="ScenarioCatalog"/>, runs them through the server build, writes the changed
/// streams and appends one entry to the streams folder's regeneration log, and refuses with exit 2 (or
/// exit 1 for a step fault) writing nothing. <c>streams verify</c> runs every stream through the server
/// build and compares each step's hash with the stream's expected hash.
/// </summary>
/// <remarks>
/// The committed <c>simcore/streams</c>, <c>simcore/content</c> and <c>simcore/config</c> are read only
/// through <see cref="RepoPaths"/> or by the CLI, which opens them read-only. Every test that records, or
/// verifies anything other than the committed streams, works on its own <see cref="TempDirectories"/>
/// copy, so the tests are parallel-safe and rerun-safe and never write under <c>simcore/</c>.
/// </remarks>
public sealed class StreamsCommandTests
{
    private const string FlatRun30 = "flat-run-30hz";

    /// <summary>The regeneration log's file name inside a streams folder.</summary>
    private const string RegenerationLog = "REGENERATIONS.md";

    /// <summary>One byte over <c>CollisionFile.MaxFileLength</c> (256 MiB).</summary>
    private const long OversizedLength = 268_435_457;

    /// <summary>The refusal of <see cref="OversizedLength"/>'s <c>big.mmcd</c>, after the command's name (contracts/simcore-cli.md#content-files).</summary>
    private const string OversizedRefusal =
        "content big.mmcd: collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456";

    /// <summary>A regeneration entry's heading: the run's UTC time to the second.</summary>
    private const string EntryHeadingPattern = @"^## \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$";

    private const string TableHeader = "| Stream | File hash | Final state hash | First changed step |";

    private const string TableRule = "| --- | --- | --- | --- |";

    /// <summary>The expected hash <c>VerifyReportsADivergenceAgainstTheExpectedHashes</c> writes into <c>jump-land</c>.</summary>
    private const ulong WrongHash = 0x0123456789abcdefUL;

    /// <summary>The index of the <c>jump-land</c> step whose expected hash is replaced: step 12.</summary>
    private const int DivergentIndex = 11;

    /// <summary>A collision checksum no content file has.</summary>
    private const ulong AbsentChecksum = 0x0011223344556677UL;

    /// <summary>The index of the step entry the <c>renumbered-step</c> copy renumbers.</summary>
    private const int RenumberedIndex = 3;

    /// <summary>How far the <c>renumbered-step</c> copy raises that entry's step number.</summary>
    private const uint Renumbering = 100;

    /// <summary>The field the fault test makes non-finite.</summary>
    private const string VelocityYPath = "characters[0].velocity.y";

    /// <summary>A quiet NaN's bits.</summary>
    private const uint NaNBits = 0x7FC00000u;

    public static TheoryData<string> RecordKinds => ["changed", "added"];

    /// <summary>The regeneration log before the run: absent, empty, ending without a line feed, or with one.</summary>
    public static TheoryData<string> LogShapes => ["no-log", "empty-log", "no-final-line-feed", "final-line-feed"];

    /// <summary>The present but invalid reasons; the missing one is the <c>[InlineData(null)]</c> row.</summary>
    public static TheoryData<string> InvalidReasons => ["", "   ", new string('a', 501), "a\nb"];

    public static TheoryData<string> SelectorCases => ["both", "neither", "unknown"];

    public static TheoryData<string> RefusedInputCases =>
        ["invalid-config", "empty-content", "oversized-content", "invalid-at-the-scenario-rate", "unreadable-old-stream", "unwritable-stream"];

    /// <summary>Rows of <see cref="RecordRefusesAPlannedScenarioItsConfigurationCannotGiveAndWritesNothing"/>.</summary>
    public static TheoryData<string> RefusedPlanCases => ["aim-out-of-reach", "still-flying", "missing-weapon-slot"];

    /// <summary>Old copies of flat-run-30hz that decode: from another rules revision, or one step short.</summary>
    public static TheoryData<string> DecodableOldStreamCases => ["other-rules-revision", "fewer-steps"];

    /// <summary>Streams <c>streams verify</c> refuses before their first step: an invalid embedded configuration, or a file that cannot be read.</summary>
    public static TheoryData<string> RefusedVerifyCases => ["invalid-config", "unreadable-file"];

    public static TheoryData<string> EmptyDirectoryCases => ["empty", "missing"];

    /// <summary>The undecodable copies of <c>flat-run-30hz.mmrs</c> <see cref="Undecodable"/> makes.</summary>
    public static TheoryData<string> UndecodableStreamCases => ["zero-steps", "renumbered-step"];

    /// <summary>
    /// Rows <c>(command, option, value)</c>: every path option of <c>streams record</c> and
    /// <c>streams verify</c>, each given as an empty string and as whitespace only.
    /// </summary>
    public static TheoryData<string, string, string> EmptyPathCases
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            (string Command, string Option)[] options =
                [("record", "--out"), ("record", "--content"), ("record", "--config"), ("verify", "--dir"), ("verify", "--content")];
            foreach (var (command, option) in options)
            {
                data.Add(command, option, string.Empty);
                data.Add(command, option, "   ");
            }

            return data;
        }
    }

    /// <summary>
    /// US1.7 (and, with <c>MovementScenarioTests</c>' US1.2 to US1.6 outcomes, the US1 Independent Test):
    /// Given the committed <c>simcore/streams/*.mmrs</c> (n files) and <c>simcore/content/</c>, When
    /// <c>simcore streams verify --dir &lt;root&gt;/simcore/streams --content &lt;root&gt;/simcore/content</c>
    /// runs in a child process, Then it exits 0 and prints one line
    /// <c>&lt;name&gt;: match, &lt;steps&gt; steps, final &lt;x16&gt;</c> per file in ordinal name order, with
    /// the file's step count and last expected hash, and the last line
    /// <c>streams verify: n streams, n match, 0 diverged, 0 not comparable, 0 refused</c>; and in the test
    /// process <see cref="StreamRunner"/> gives every file per-step hashes equal to its expected hashes,
    /// the last of which equals the child's printed final hash. Every stream is stepped in two processes.
    /// </summary>
    [Fact]
    public void EveryCommittedStreamMatchesInAFreshProcess()
    {
        var streamsDirectory = RepoPaths.Combine("simcore", "streams");
        var contentDirectory = RepoPaths.Combine("simcore", "content");
        var names = StreamNames(streamsDirectory);
        Assert.True(names.Count > 0, $"{streamsDirectory} holds no committed .mmrs stream");

        var (exit, stdout, stderr) = SimcoreCli.RunInFreshProcess(
            "streams", "verify", "--dir", streamsDirectory, "--content", contentDirectory);

        Assert.True(exit == 0, $"simcore streams verify exited {exit} in the child process; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(
            lines.Length == names.Count + 1,
            $"the child printed {lines.Length} lines for {names.Count} streams; stdout: {stdout}");

        var worlds = CommittedWorlds();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var file = $"simcore/streams/{name}.mmrs";
            var bytes = RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs");
            var committed = RecordedStream.Decode(bytes);
            var last = committed.Steps[^1].ExpectedHash;
            Assert.Equal($"{name}: match, {committed.Steps.Count} steps, final {last:x16}", lines[i]);
            var printedFinal = ulong.Parse(lines[i][(lines[i].LastIndexOf(' ') + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

            // The same stream, stepped again in this process.
            var hashes = RecordHashes(StreamRunner.Run(bytes, worlds), name);
            Assert.True(
                hashes.Count == committed.Steps.Count,
                $"{name}: the in-process run gave {hashes.Count} records for {committed.Steps.Count} steps");
            for (var s = 0; s < hashes.Count; s++)
            {
                Assert.True(
                    hashes[s] == committed.Steps[s].ExpectedHash,
                    $"{name}: step {committed.Steps[s].Step} hashed {hashes[s]:x16} in process; {file} expects {committed.Steps[s].ExpectedHash:x16}");
            }

            Assert.True(
                hashes[^1] == printedFinal,
                $"{name}: the in-process run's final hash is {hashes[^1]:x16}; the child printed {printedFinal:x16}");
        }

        Assert.Equal(
            $"streams verify: {names.Count} streams, {names.Count} match, 0 diverged, 0 not comparable, 0 refused",
            lines[^1]);
    }

    /// <summary>
    /// FR-028, contracts/simcore-cli.md#streams-record: Given copies of the committed streams, content and
    /// configuration in a temp directory, and either (<c>changed</c>) the copied configuration's
    /// <c>movement.runSpeed</c> set to 6.5 or (<c>added</c>) the copied <c>stairs-climb.mmrs</c> deleted,
    /// When <c>streams record --scenario &lt;name&gt; --reason "Test regeneration"</c> runs with
    /// <c>--out</c>, <c>--content</c> and <c>--config</c> on the copies, Then it exits 0 and prints
    /// <c>flat-run-30hz: changed; file &lt;old&gt; -&gt; &lt;new&gt;; final state &lt;old&gt; -&gt; &lt;new&gt;; first changed step 3</c>
    /// or <c>stairs-climb: added; file &lt;x16&gt;; final state &lt;x16&gt;</c>, then the total line; the
    /// regeneration log keeps its old text as a prefix and appends one entry (UTC heading, the reason, the
    /// command, the rules revision, the table header and the stream's row); and nothing else in the temp
    /// directory changes. The rewritten <c>flat-run-30hz</c> first differs at step 3, the step the 6.5 m/s
    /// cap first bites at 30 Hz; the re-added <c>stairs-climb</c> is byte-identical to the committed one.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordKinds))]
    public void RecordAppendsTheReasonAndOldAndNewHashes(string kind)
    {
        using var sandbox = Sandbox.Create($"streams-record-{kind}");
        var scenario = kind switch
        {
            "changed" => FlatRun30,
            "added" => "stairs-climb",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown row."),
        };
        var streamPath = sandbox.Stream(scenario);
        var oldBytes = File.ReadAllBytes(streamPath);
        if (kind == "changed")
        {
            EditConfig(sandbox.Config, "movement", "runSpeed", 6.5);
        }
        else
        {
            File.Delete(streamPath);
        }

        var oldLog = File.ReadAllText(sandbox.LogPath);
        var before = Snapshot(sandbox.Root);
        var started = WholeSecond(DateTime.UtcNow);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--scenario", scenario, "--reason", "Test regeneration"));

        var finished = DateTime.UtcNow;
        Assert.True(exit == 0, $"{kind}: simcore streams record --scenario {scenario} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.True(File.Exists(streamPath), $"{kind}: {streamPath} was not written");
        var newBytes = File.ReadAllBytes(streamPath);
        var recorded = RecordedStream.Decode(newBytes);
        var newFile = StreamBytes.Trailer(newBytes);
        var newFinal = recorded.Steps[^1].ExpectedHash;

        string resultLine;
        string totalLine;
        string row;
        string[] differences;
        if (kind == "changed")
        {
            var old = RecordedStream.Decode(oldBytes);
            var oldFile = StreamBytes.Trailer(oldBytes);
            var oldFinal = old.Steps[^1].ExpectedHash;
            var firstChanged = FirstChangedStep(old, recorded);
            Assert.True(
                firstChanged == 3u,
                $"{kind}: the old and new {scenario} expected hashes first differ at step {firstChanged?.ToString(CultureInfo.InvariantCulture) ?? "none"}, not 3");
            resultLine = $"{scenario}: changed; file {oldFile:x16} -> {newFile:x16}; final state {oldFinal:x16} -> {newFinal:x16}; first changed step 3";
            totalLine = "streams record: 1 streams, 1 changed, 0 added, 0 removed, 0 unchanged";
            row = $"| {scenario} | {oldFile:x16} -> {newFile:x16} | {oldFinal:x16} -> {newFinal:x16} | 3 |";
            differences = [$"changed streams/{RegenerationLog}", $"changed streams/{scenario}.mmrs"];
        }
        else
        {
            Assert.True(
                newBytes.AsSpan().SequenceEqual(oldBytes),
                $"{kind}: the re-recorded {scenario} ({newBytes.Length} bytes, trailer {newFile:x16}) differs from the committed one ({oldBytes.Length} bytes, trailer {StreamBytes.Trailer(oldBytes):x16})");
            resultLine = $"{scenario}: added; file {newFile:x16}; final state {newFinal:x16}";
            totalLine = "streams record: 1 streams, 0 changed, 1 added, 0 removed, 0 unchanged";
            row = $"| {scenario} | added: {newFile:x16} | added: {newFinal:x16} | |";
            differences = [$"added streams/{scenario}.mmrs", $"changed streams/{RegenerationLog}"];
        }

        Assert.Equal(resultLine + "\n" + totalLine + "\n", stdout);

        var entry = AppendedEntry(oldLog, File.ReadAllText(sandbox.LogPath), kind);
        Assert.True(entry.Length > 0 && Regex.IsMatch(entry[0], EntryHeadingPattern), $"{kind}: the appended entry starts with \"{entry.FirstOrDefault()}\", not a ## <UTC> heading");
        AssertHeadingWithin(entry[0], started, finished, kind);
        Assert.Equal(
            [
                "- Reason: Test regeneration",
                $"- Command: `simcore streams record --scenario {scenario}`",
                $"- Rules revision: {SimCoreVersion.RulesRevision}",
                TableHeader,
                TableRule,
                row,
            ],
            entry[1..]);

        Assert.Equal(differences, Differences(before, Snapshot(sandbox.Root)));
    }

    /// <summary>
    /// contracts/simcore-cli.md#regenerationsmd: Given copies of the committed streams, content and
    /// configuration with <c>stairs-climb.mmrs</c> deleted and the copied <c>REGENERATIONS.md</c>
    /// (<c>no-log</c>) deleted, (<c>empty-log</c>) emptied, (<c>no-final-line-feed</c>) replaced by
    /// <c># Old log</c>, a blank line and <c>- an entry without a final line feed</c> with no line feed after
    /// it, or (<c>final-line-feed</c>) replaced by <c># Old log</c> and a line feed, When
    /// <c>streams record --scenario stairs-climb --reason "Log shape"</c> runs on the copies, Then it exits 0
    /// and the log is exactly its old text (for <c>no-log</c> and <c>empty-log</c>, the
    /// <c># Stream regenerations</c> heading, a blank line and the sentence saying <c>simcore streams record</c>
    /// appends the file, so the log starts with its heading), one line feed when that text did not end with
    /// one, a blank line, and the entry: the UTC heading, a blank line, the
    /// reason, command and rules revision lines, a blank line, the table header and rule, and the
    /// <c>stairs-climb</c> row, each line ending with a line feed.
    /// </summary>
    [Theory]
    [MemberData(nameof(LogShapes))]
    public void RecordStartsOrExtendsTheRegenerationLog(string caseName)
    {
        const string scenario = "stairs-climb";
        const string preamble =
            "# Stream regenerations\n\nThis file is appended by `simcore streams record`; each entry gives the reason and the old and new hashes.\n";
        using var sandbox = Sandbox.Create($"streams-record-log-{caseName}");
        var streamBytes = File.ReadAllBytes(sandbox.Stream(scenario));
        File.Delete(sandbox.Stream(scenario));
        var prefix = caseName switch
        {
            "no-log" or "empty-log" => preamble,
            "no-final-line-feed" => "# Old log\n\n- an entry without a final line feed\n",
            "final-line-feed" => "# Old log\n",
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
        };
        switch (caseName)
        {
            case "no-log":
                File.Delete(sandbox.LogPath);
                break;
            case "empty-log":
                File.WriteAllText(sandbox.LogPath, string.Empty);
                break;
            case "no-final-line-feed":
                File.WriteAllText(sandbox.LogPath, prefix[..^1]);
                break;
            default:
                File.WriteAllText(sandbox.LogPath, prefix);
                break;
        }

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--scenario", scenario, "--reason", "Log shape"));

        Assert.True(exit == 0, $"{caseName}: simcore streams record --scenario {scenario} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        var log = File.ReadAllText(sandbox.LogPath);
        var heading = Regex.Match(log, EntryHeadingPattern, RegexOptions.Multiline);
        Assert.True(heading.Success, $"{caseName}: the log has no ## <UTC> heading: {log}");
        var row = $"| {scenario} | added: {StreamBytes.Trailer(streamBytes):x16} | added: {RecordedStream.Decode(streamBytes).Steps[^1].ExpectedHash:x16} | |";
        var expected =
            prefix + "\n" +
            heading.Value + "\n\n" +
            "- Reason: Log shape\n" +
            $"- Command: `simcore streams record --scenario {scenario}`\n" +
            $"- Rules revision: {SimCoreVersion.RulesRevision}\n\n" +
            TableHeader + "\n" +
            TableRule + "\n" +
            row + "\n";
        Assert.Equal(expected, log);
    }

    /// <summary>
    /// FR-028, contracts/simcore-cli.md#streams-record step 5: Given copies of the committed streams plus a
    /// copy of <c>flat-run-30hz.mmrs</c> named <c>old-scenario.mmrs</c>, a name the catalog does not
    /// hold, When <c>streams record --all --reason "Remove old"</c> runs, Then it exits 0, prints
    /// <c>old-scenario: removed; file &lt;x16&gt;</c>, deletes the file, prints a total line with
    /// <c>, 0 changed, \d+ added, 1 removed, </c>, appends an entry whose command is
    /// <c>simcore streams record --all</c> and whose table has the row
    /// <c>| old-scenario | removed: &lt;x16&gt; | removed | |</c>, and rewrites no committed stream.
    /// </summary>
    [Fact]
    public void RecordAllRemovesStreamsNoLongerInTheCatalog()
    {
        Assert.DoesNotContain("old-scenario", ScenarioCatalog.Names);
        using var sandbox = Sandbox.Create("streams-record-remove");
        var oldScenario = sandbox.Stream("old-scenario");
        File.Copy(sandbox.Stream(FlatRun30), oldScenario);
        var trailer = StreamBytes.Trailer(File.ReadAllBytes(oldScenario));
        var oldLog = File.ReadAllText(sandbox.LogPath);
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--all", "--reason", "Remove old"));

        Assert.True(exit == 0, $"simcore streams record --all exited {exit}; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.Contains($"old-scenario: removed; file {trailer:x16}", lines);
        Assert.False(File.Exists(oldScenario), $"{oldScenario} was not deleted");
        Assert.True(
            lines.Length > 0 && lines[^1].StartsWith("streams record: ", StringComparison.Ordinal)
                && Regex.IsMatch(lines[^1], @", 0 changed, \d+ added, 1 removed, "),
            $"the total line is \"{lines.LastOrDefault()}\"; it should report 0 changed and 1 removed");

        var entry = AppendedEntry(oldLog, File.ReadAllText(sandbox.LogPath), "--all");
        Assert.Contains("- Reason: Remove old", entry);
        Assert.Contains("- Command: `simcore streams record --all`", entry);
        Assert.Contains($"| old-scenario | removed: {trailer:x16} | removed | |", entry);

        var differences = Differences(before, Snapshot(sandbox.Root));
        var rewritten = differences
            .Where(d => d.StartsWith("changed ", StringComparison.Ordinal) && d != $"changed streams/{RegenerationLog}")
            .ToArray();
        Assert.True(rewritten.Length == 0, $"--all rewrote files it reported unchanged: {string.Join(", ", rewritten)}");
        Assert.Equal(["removed streams/old-scenario.mmrs"], differences.Where(d => d.StartsWith("removed ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record step 1: Given a valid selector and a <c>--reason</c> that is
    /// missing, empty, whitespace only, 501 characters long or two lines, When <c>streams record</c> runs,
    /// Then it exits 2, names <c>--reason</c> on stderr, and every file in the temp directory keeps its
    /// bytes and last-write time, with none added or removed.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [MemberData(nameof(InvalidReasons))]
    public void RecordRefusesAMissingEmptyLongOrMultiLineReason(string? reason)
    {
        using var sandbox = Sandbox.Create("streams-record-reason");
        var before = Snapshot(sandbox.Root);
        string[] selection = reason is null
            ? ["--scenario", FlatRun30]
            : ["--scenario", FlatRun30, "--reason", reason];
        var label = reason is null ? "missing --reason" : $"--reason \"{reason}\" ({reason.Length} characters)";

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs(selection));

        Assert.True(exit == 2, $"{label}: simcore streams record exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("--reason", stderr, StringComparison.Ordinal);
        AssertNothingWritten(before, sandbox.Root, label);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record step 1: Given both <c>--scenario flat-run-30hz</c> and
    /// <c>--all</c>, neither, or <c>--scenario no-such-scenario</c>, with a valid reason, When
    /// <c>streams record</c> runs, Then it exits 2 and writes nothing; both and neither name
    /// <c>--scenario</c> and <c>--all</c> on stderr, and the unknown name's refusal names it and lists every
    /// name of <see cref="ScenarioCatalog.Names"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(SelectorCases))]
    public void RecordRefusesBothOrNeitherSelectorAndUnknownNames(string caseName)
    {
        using var sandbox = Sandbox.Create($"streams-record-{caseName}");
        string[] selection = caseName switch
        {
            "both" => ["--scenario", FlatRun30, "--all"],
            "neither" => [],
            "unknown" => ["--scenario", "no-such-scenario"],
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row."),
        };
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs([.. selection, "--reason", "Test regeneration"]));

        Assert.True(exit == 2, $"{caseName}: simcore streams record exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        if (caseName == "unknown")
        {
            Assert.Contains("no-such-scenario", stderr, StringComparison.Ordinal);
            foreach (var name in ScenarioCatalog.Names)
            {
                Assert.True(stderr.Contains(name, StringComparison.Ordinal), $"unknown: stderr does not list the known scenario {name}; stderr: {stderr}");
            }
        }
        else
        {
            Assert.Contains("--scenario", stderr, StringComparison.Ordinal);
            Assert.Contains("--all", stderr, StringComparison.Ordinal);
        }

        AssertNothingWritten(before, sandbox.Root, caseName);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record steps 3 to 6 and #content-files: Given <c>--scenario flat-run-30hz</c>
    /// with (<c>invalid-config</c>) the copied configuration's <c>movement.gravity</c> set to 0,
    /// (<c>empty-content</c>) an empty <c>--content</c> directory, or (<c>oversized-content</c>) a sparse
    /// <c>big.mmcd</c> of 268,435,457 bytes next to the copied <c>test-world.mmcd</c>; or
    /// (<c>invalid-at-the-scenario-rate</c>) <c>--scenario flat-run-20hz</c> with the rifle at 1500 rounds a
    /// minute, which rule R11 accepts at the file's 30 Hz and refuses at the scenario's 20 Hz;
    /// (<c>unreadable-old-stream</c>) <c>flat-run-30hz.mmrs</c> replaced by a dangling symbolic link; or
    /// (<c>unwritable-stream</c>) <c>flat-run-30hz.mmrs</c> replaced by a directory; When
    /// <c>streams record</c> runs, Then it exits 2 and writes nothing, and stderr gives
    /// <c>movement.gravity: must be between 0.1 and 100</c>, names <c>test-world.mmcd</c>, has the line
    /// <c>streams record: content big.mmcd: collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456</c>,
    /// has <c>streams record: flat-run-20hz: the configuration is invalid</c> and the
    /// <c>weapons[0].roundsPerMinute</c> error, or has
    /// <c>streams record: &lt;out&gt;: the streams cannot be read (</c> or <c>…cannot be written (</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedInputCases))]
    public void RecordRefusesAnInvalidConfigurationOrMissingContent(string caseName)
    {
        using var sandbox = Sandbox.Create($"streams-record-{caseName}");
        var content = sandbox.Content;
        var scenario = FlatRun30;
        string? dangling = null;
        switch (caseName)
        {
            case "invalid-config":
                EditConfig(sandbox.Config, "movement", "gravity", 0);
                break;
            case "invalid-at-the-scenario-rate":
                scenario = "flat-run-20hz";
                var config = JsonNode.Parse(File.ReadAllText(sandbox.Config))!.AsObject();
                config["weapons"]![0]!["roundsPerMinute"] = 1500;
                File.WriteAllText(sandbox.Config, config.ToJsonString());
                break;
            case "unreadable-old-stream":
                // The link is made after the snapshot and removed before the check, since it cannot be read.
                File.Delete(sandbox.Stream(FlatRun30));
                dangling = sandbox.Stream(FlatRun30);
                break;
            case "unwritable-stream":
                File.Delete(sandbox.Stream(FlatRun30));
                Directory.CreateDirectory(sandbox.Stream(FlatRun30));
                break;
            case "empty-content":
                content = Directory.CreateDirectory(Path.Combine(sandbox.Root, "empty-content")).FullName;
                break;
            case "oversized-content":
                CreateSparse(Path.Combine(sandbox.Content, "big.mmcd"), OversizedLength);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var before = Snapshot(sandbox.Root);
        if (dangling is not null)
        {
            File.CreateSymbolicLink(dangling, Path.Combine(sandbox.Root, "no-such-target"));
        }

        var (exit, stdout, stderr) = SimcoreCli.Run(
            "streams", "record", "--scenario", scenario, "--reason", "Test regeneration",
            "--out", sandbox.Streams, "--content", content, "--config", sandbox.Config);

        if (dangling is not null)
        {
            File.Delete(dangling);
        }

        Assert.True(exit == 2, $"{caseName}: simcore streams record exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        switch (caseName)
        {
            case "invalid-config":
                Assert.Contains("movement.gravity: must be between 0.1 and 100", stderr, StringComparison.Ordinal);
                break;
            case "empty-content":
                Assert.Contains("test-world.mmcd", stderr, StringComparison.Ordinal);
                break;
            case "invalid-at-the-scenario-rate":
                Assert.Contains("streams record: flat-run-20hz: the configuration is invalid", Lines(stderr));
                Assert.Contains(Lines(stderr), line => line.StartsWith("weapons[0].roundsPerMinute: must be at most", StringComparison.Ordinal));
                break;
            case "unreadable-old-stream":
                Assert.Contains(Lines(stderr), line => line.StartsWith($"streams record: {sandbox.Streams}: the streams cannot be read (", StringComparison.Ordinal));
                break;
            case "unwritable-stream":
                Assert.Contains(Lines(stderr), line => line.StartsWith($"streams record: {sandbox.Streams}: the streams cannot be written (", StringComparison.Ordinal));
                break;
            default:
                Assert.Contains($"streams record: {OversizedRefusal}", Lines(stderr));
                break;
        }

        AssertNothingWritten(before, sandbox.Root, caseName);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record step 3: Given a <see cref="StepFault"/> taken from
    /// <c>SimWorld.StepWithInjectedBits</c> with NaN bits (0x7FC00000) at
    /// <c>characters[0].velocity.y</c>, When <c>Program.Run</c> runs
    /// <c>streams record --scenario flat-run-30hz --reason Fault</c> on the copies with a <c>runStream</c>
    /// that throws <c>new StepFaultException(7, fault)</c>, Then it exits 1, stderr has the line
    /// <c>streams record: flat-run-30hz: step 7 faulted: NonFinite at characters[0].velocity.y</c>, and
    /// every file in the temp directory keeps its bytes and last-write time, with none added or removed.
    /// The fault reaches the CLI only through its stream-runner parameter; the CLI calls no core internals.
    /// </summary>
    [Fact]
    public void RecordExitsOneWhenAServerRunFaultsAndWritesNothing()
    {
        var fault = NonFiniteVelocityFault();
        using var sandbox = Sandbox.Create("streams-record-fault");
        var before = Snapshot(sandbox.Root);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exit = Program.Run(
            ["streams", "record", "--scenario", FlatRun30, "--reason", "Fault", "--out", sandbox.Streams, "--content", sandbox.Content, "--config", sandbox.Config],
            stdout,
            stderr,
            environmentName: "Development",
            runStream: (_, _) => throw new StepFaultException(7, fault));

        Assert.True(exit == 1, $"simcore streams record exited {exit} on a step fault, expected 1; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("streams record: flat-run-30hz: step 7 faulted: NonFinite at characters[0].velocity.y", Lines(stderr.ToString()));
        AssertNothingWritten(before, sandbox.Root, "step fault");
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record step 3: Given copies of the committed streams, content and
    /// configuration, the configuration edited so that it still passes validation but a planned definition's
    /// plan cannot meet it, (<c>aim-out-of-reach</c>) <c>weapons[0].muzzleSpeed</c> 5 for
    /// <c>mesh-occludes-shot</c>; (<c>still-flying</c>) <c>projectileClasses[0].maxRangeM</c> 10000 and
    /// <c>lifetimeMs</c> 60000 for <c>shots-at-known-distances</c>; or (<c>missing-weapon-slot</c>) one weapon
    /// and <c>input.weaponSlots</c> 1 for <c>shots-at-known-distances</c>, When
    /// <c>simcore streams record --scenario &lt;name&gt;</c> runs in a child process, Then it exits 2, prints
    /// nothing on stdout, has the stderr line
    /// <c>streams record: mesh-occludes-shot: (&lt;x&gt;, &lt;y&gt;, &lt;z&gt;) is out of reach of slot 0's weapon from (&lt;x&gt;, &lt;y&gt;, &lt;z&gt;).</c>,
    /// exactly <c>streams record: shots-at-known-distances: at 30 Hz 1 projectiles are still flying at the last step.</c>
    /// or exactly <c>streams record: shots-at-known-distances: the configuration has no weapons[1], whose spread and recoil kick the scenario sets to 0.</c>,
    /// shows no unhandled exception, and every file in the temp directory keeps its bytes and last-write time,
    /// with none added or removed.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedPlanCases))]
    public void RecordRefusesAPlannedScenarioItsConfigurationCannotGiveAndWritesNothing(string caseName)
    {
        using var sandbox = Sandbox.Create($"streams-record-plan-{caseName}");
        var config = JsonNode.Parse(File.ReadAllText(sandbox.Config))!.AsObject();
        string scenario;
        switch (caseName)
        {
            case "aim-out-of-reach":
                scenario = "mesh-occludes-shot";
                config["weapons"]![0]!["muzzleSpeed"] = 5;
                break;
            case "still-flying":
                scenario = "shots-at-known-distances";
                config["projectileClasses"]![0]!["maxRangeM"] = 10000;
                config["projectileClasses"]![0]!["lifetimeMs"] = 60000;
                break;
            case "missing-weapon-slot":
                scenario = "shots-at-known-distances";
                config["weapons"]!.AsArray().RemoveAt(1);
                config["input"]!["weaponSlots"] = 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        File.WriteAllText(sandbox.Config, config.ToJsonString());
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(
            "streams", "record", "--scenario", scenario, "--reason", "Test regeneration",
            "--out", sandbox.Streams, "--content", sandbox.Content, "--config", sandbox.Config);

        Assert.True(exit == 2, $"{caseName}: simcore streams record exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.True(stdout.Length == 0, $"{caseName}: stdout is \"{stdout}\"; a refusal prints nothing there");
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.Ordinal);
        var prefix = $"streams record: {scenario}: ";
        switch (caseName)
        {
            case "aim-out-of-reach":
                Assert.Contains(
                    Lines(stderr),
                    line => Regex.IsMatch(line, @"^streams record: mesh-occludes-shot: \(-?[0-9.]+, -?[0-9.]+, -?[0-9.]+\) is out of reach of slot 0's weapon from \(-?[0-9.]+, -?[0-9.]+, -?[0-9.]+\)\.$"));
                break;
            case "still-flying":
                Assert.Contains($"{prefix}at 30 Hz 1 projectiles are still flying at the last step.", Lines(stderr));
                break;
            default:
                Assert.Contains($"{prefix}the configuration has no weapons[1], whose spread and recoil kick the scenario sets to 0.", Lines(stderr));
                break;
        }

        AssertNothingWritten(before, sandbox.Root, caseName);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record step 4: Given copies of the committed streams and
    /// configuration, whose <c>wall-slide-45.mmrs</c> and regeneration log carry a last-write time of
    /// 2020-01-01, When <c>streams record --scenario wall-slide-45 --reason "No change"</c> runs, Then it
    /// exits 0 and prints exactly <c>wall-slide-45: unchanged; file &lt;x16&gt;; final state &lt;x16&gt;</c> (the
    /// committed trailer and last expected hash) and
    /// <c>streams record: 1 streams, 0 changed, 0 added, 0 removed, 1 unchanged</c>, and no file in the temp
    /// directory changes its bytes or last-write time: neither the stream nor the log is rewritten.
    /// </summary>
    [Fact]
    public void RecordLeavesUnchangedStreamsAndRegenerationsUntouched()
    {
        const string Scenario = "wall-slide-45";
        using var sandbox = Sandbox.Create("streams-record-unchanged");
        var streamPath = sandbox.Stream(Scenario);
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(streamPath, old);
        File.SetLastWriteTimeUtc(sandbox.LogPath, old);
        var bytes = File.ReadAllBytes(streamPath);
        var trailer = StreamBytes.Trailer(bytes);
        var final = RecordedStream.Decode(bytes).Steps[^1].ExpectedHash;
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--scenario", Scenario, "--reason", "No change"));

        Assert.True(exit == 0, $"simcore streams record --scenario {Scenario} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            $"{Scenario}: unchanged; file {trailer:x16}; final state {final:x16}\n" +
            "streams record: 1 streams, 0 changed, 0 added, 0 removed, 1 unchanged\n",
            stdout);
        AssertNothingWritten(before, sandbox.Root, "an unchanged stream");
        Assert.Equal(old, File.GetLastWriteTimeUtc(streamPath));
        Assert.Equal(old, File.GetLastWriteTimeUtc(sandbox.LogPath));
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record (an old file that no longer decodes; added in the Phase 3
    /// review): Given copies of the committed streams, content and configuration whose
    /// <c>flat-run-30hz.mmrs</c> is replaced by an undecodable copy (<c>zero-steps</c>: no step entries,
    /// with a consistent frame; <c>renumbered-step</c>: <c>steps[3].step</c> raised by 100, trailer fixed),
    /// When <c>streams record --scenario flat-run-30hz --reason "Test regeneration"</c> runs, Then it exits
    /// 0; the stream is rewritten byte-identical to the committed one; stdout is exactly
    /// <c>flat-run-30hz: changed; file &lt;old trailer&gt; -&gt; &lt;new trailer&gt;; final state unreadable -&gt; &lt;new final&gt;; first changed step &lt;first step&gt;</c>
    /// and the total line; and the regeneration log gains one entry whose row is
    /// <c>| flat-run-30hz | &lt;old&gt; -&gt; &lt;new&gt; | unreadable -&gt; &lt;new final&gt; | &lt;first step&gt; |</c>. Only the
    /// stream and the log change, so the stream and its log entry are written together.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableStreamCases))]
    public void RecordReplacesAnOldStreamThatNoLongerDecodesAndLogsIt(string caseName)
    {
        using var sandbox = Sandbox.Create($"streams-record-{caseName}");
        var streamPath = sandbox.Stream(FlatRun30);
        var committed = File.ReadAllBytes(streamPath);
        var (undecodable, _) = Undecodable(caseName, committed);
        File.WriteAllBytes(streamPath, undecodable);
        var oldFile = StreamBytes.Trailer(undecodable);
        var oldLog = File.ReadAllText(sandbox.LogPath);
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--scenario", FlatRun30, "--reason", "Test regeneration"));

        Assert.True(exit == 0, $"{caseName}: simcore streams record --scenario {FlatRun30} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        var newBytes = File.ReadAllBytes(streamPath);
        Assert.True(
            newBytes.AsSpan().SequenceEqual(committed),
            $"{caseName}: the re-recorded {FlatRun30} ({newBytes.Length} bytes, trailer {StreamBytes.Trailer(newBytes):x16}) differs from the committed one ({committed.Length} bytes, trailer {StreamBytes.Trailer(committed):x16})");
        var recorded = RecordedStream.Decode(newBytes);
        var newFile = StreamBytes.Trailer(newBytes);
        var newFinal = recorded.Steps[^1].ExpectedHash;
        var firstStep = recorded.Steps[0].Step;
        Assert.Equal(
            $"{FlatRun30}: changed; file {oldFile:x16} -> {newFile:x16}; final state unreadable -> {newFinal:x16}; first changed step {firstStep}\n" +
            "streams record: 1 streams, 1 changed, 0 added, 0 removed, 0 unchanged\n",
            stdout);

        var entry = AppendedEntry(oldLog, File.ReadAllText(sandbox.LogPath), caseName);
        Assert.True(entry.Length > 0 && Regex.IsMatch(entry[0], EntryHeadingPattern), $"{caseName}: the appended entry starts with \"{entry.FirstOrDefault()}\", not a ## <UTC> heading");
        Assert.Equal(
            [
                "- Reason: Test regeneration",
                $"- Command: `simcore streams record --scenario {FlatRun30}`",
                $"- Rules revision: {SimCoreVersion.RulesRevision}",
                TableHeader,
                TableRule,
                $"| {FlatRun30} | {oldFile:x16} -> {newFile:x16} | unreadable -> {newFinal:x16} | {firstStep} |",
            ],
            entry[1..]);
        Assert.Equal([$"changed streams/{RegenerationLog}", $"changed streams/{FlatRun30}.mmrs"], Differences(before, Snapshot(sandbox.Root)));
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-record steps 4 and 6 and #regenerationsmd: Given copies of the committed
    /// streams, content and configuration whose <c>flat-run-30hz.mmrs</c> is replaced by a copy that decodes
    /// but differs: (<c>other-rules-revision</c>) the committed file with rules revision 2 (trailer fixed), or
    /// (<c>fewer-steps</c>) the committed stream re-encoded without its last step; When
    /// <c>streams record --scenario flat-run-30hz --reason "Test regeneration"</c> runs, Then it exits 0, the
    /// stream is rewritten byte-identical to the committed one, stdout is exactly
    /// <c>flat-run-30hz: changed; file &lt;old&gt; -&gt; &lt;new&gt;; final state &lt;old final&gt; -&gt; &lt;new final&gt;; first changed step &lt;s&gt;</c>
    /// and the total line, and the log entry's rules revision line is <c>- Rules revision: 1 (was 2)</c> or
    /// <c>- Rules revision: 1</c>: s is <c>none</c> for the other revision, whose expected hashes are all equal,
    /// and the last step, the first step only the new stream holds, for the shorter one.
    /// </summary>
    [Theory]
    [MemberData(nameof(DecodableOldStreamCases))]
    public void RecordComparesADecodableOldStreamStepByStep(string caseName)
    {
        using var sandbox = Sandbox.Create($"streams-record-{caseName}");
        var streamPath = sandbox.Stream(FlatRun30);
        var committed = File.ReadAllBytes(streamPath);
        var stream = RecordedStream.Decode(committed);
        byte[] old;
        string firstChanged;
        string revision;
        switch (caseName)
        {
            case "other-rules-revision":
                var two = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(two, SimCoreVersion.RulesRevision + 1u);
                old = StreamBytes.WithBytes(committed, 8, two);
                firstChanged = "none";
                revision = $"- Rules revision: {SimCoreVersion.RulesRevision} (was {SimCoreVersion.RulesRevision + 1u})";
                break;
            case "fewer-steps":
                old = new RecordedStream(
                    stream.RulesRevision,
                    stream.StepHz,
                    stream.Seed,
                    stream.MatchSalt,
                    stream.CollisionChecksum,
                    stream.Config,
                    stream.InitialState,
                    [.. stream.Steps.Take(stream.Steps.Count - 1)]).Encode();
                firstChanged = stream.Steps[^1].Step.ToString(CultureInfo.InvariantCulture);
                revision = $"- Rules revision: {SimCoreVersion.RulesRevision}";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        File.WriteAllBytes(streamPath, old);
        var oldFinal = RecordedStream.Decode(old).Steps[^1].ExpectedHash;
        var oldLog = File.ReadAllText(sandbox.LogPath);

        var (exit, stdout, stderr) = SimcoreCli.Run(sandbox.RecordArgs("--scenario", FlatRun30, "--reason", "Test regeneration"));

        Assert.True(exit == 0, $"{caseName}: simcore streams record --scenario {FlatRun30} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        var newBytes = File.ReadAllBytes(streamPath);
        Assert.True(newBytes.AsSpan().SequenceEqual(committed), $"{caseName}: the re-recorded {FlatRun30} differs from the committed one");
        var newFinal = stream.Steps[^1].ExpectedHash;
        Assert.Equal(
            $"{FlatRun30}: changed; file {StreamBytes.Trailer(old):x16} -> {StreamBytes.Trailer(committed):x16}; final state {oldFinal:x16} -> {newFinal:x16}; first changed step {firstChanged}\n" +
            "streams record: 1 streams, 1 changed, 0 added, 0 removed, 0 unchanged\n",
            stdout);
        var entry = AppendedEntry(oldLog, File.ReadAllText(sandbox.LogPath), caseName);
        Assert.Contains(revision, entry);
        Assert.Contains(
            $"| {FlatRun30} | {StreamBytes.Trailer(old):x16} -> {StreamBytes.Trailer(committed):x16} | {oldFinal:x16} -> {newFinal:x16} | {firstChanged} |",
            entry);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify (the Refused outcome): Given a <c>--dir</c> holding
    /// <c>flat-run-30hz.mmrs</c> as (<c>invalid-config</c>) the committed stream re-encoded with
    /// <c>movement.gravity</c> 0 in its configuration, or (<c>unreadable-file</c>) a dangling symbolic link,
    /// with the copied content, When <c>streams verify</c> runs, Then it exits 2 and stdout is exactly the
    /// refusal, <c>REFUSED stream flat-run-30hz: the configuration is invalid</c> followed by
    /// <c>  movement.gravity: must be between 0.1 and 100</c>, or one line starting
    /// <c>REFUSED stream flat-run-30hz: the file cannot be read (</c>, then
    /// <c>streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedVerifyCases))]
    public void VerifyRefusesAnInvalidConfigurationOrAnUnreadableFile(string caseName)
    {
        using var directory = TempDirectories.Create($"streams-verify-{caseName}");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(content, "content", "test-world.mmcd");
        var path = Path.Combine(streams, $"{FlatRun30}.mmrs");
        switch (caseName)
        {
            case "invalid-config":
                var stream = RecordedStream.Decode(RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs"));
                var config = stream.Config;
                config.Movement!.Gravity = 0f;
                File.WriteAllBytes(path, new RecordedStream(
                    stream.RulesRevision, stream.StepHz, stream.Seed, stream.MatchSalt, stream.CollisionChecksum, config, stream.InitialState, stream.Steps).Encode());
                break;
            case "unreadable-file":
                File.CreateSymbolicLink(path, directory.Combine("no-such-target"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 2, $"{caseName}: simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        string[] refusal = caseName == "invalid-config"
            ? [$"REFUSED stream {FlatRun30}: the configuration is invalid", "  movement.gravity: must be between 0.1 and 100"]
            : [lines[0]];
        Assert.StartsWith(caseName == "invalid-config" ? refusal[0] : $"REFUSED stream {FlatRun30}: the file cannot be read (", lines[0]);
        Assert.Equal([.. refusal, "streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused"], lines);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify: Given copies of the committed streams and content with
    /// <c>jump-land.mmrs</c> rewritten by <c>StreamBytes.WithExpectedHash(bytes, 11, 0x0123456789abcdef)</c>
    /// (step 12), When <c>streams verify</c> runs on the copies, Then it exits 1, prints
    /// <c>DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes</c>
    /// followed by <c>  hash  expected 0123456789abcdef  server &lt;x16&gt;</c>, where x16 is the real hash (the
    /// committed expected hash of step 12), and the last line reports n streams, n - 1 match, 1 diverged.
    /// </summary>
    [Fact]
    public void VerifyReportsADivergenceAgainstTheExpectedHashes()
    {
        using var sandbox = Sandbox.Create("streams-verify-diverged");
        var realHash = WriteDivergedJumpLand(sandbox.Streams);
        var count = StreamNames(sandbox.Streams).Count;

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", sandbox.Streams, "--content", sandbox.Content);

        Assert.True(exit == 1, $"simcore streams verify exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        AssertDivergenceReport(lines, realHash, stdout);
        Assert.Equal($"streams verify: {count} streams, {count - 1} match, 1 diverged, 0 not comparable, 0 refused", lines[^1]);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify: Given a <c>--dir</c> holding only a copy of
    /// <c>flat-run-30hz.mmrs</c> and an empty <c>--content</c> directory, When <c>streams verify</c> runs,
    /// Then it exits 2 and prints exactly
    /// <c>REFUSED stream flat-run-30hz: stream collisionChecksum at offset 36: collision data &lt;x16&gt; was not supplied</c>,
    /// x16 being the test world's checksum, and
    /// <c>streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused</c>.
    /// </summary>
    [Fact]
    public void VerifyRefusesAStreamWhoseCollisionFileIsMissing()
    {
        using var directory = TempDirectories.Create("streams-verify-no-content");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(streams, "streams", $"{FlatRun30}.mmrs");
        var checksum = CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd")).Checksum;

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 2, $"simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            $"REFUSED stream {FlatRun30}: stream collisionChecksum at offset 36: collision data {checksum:x16} was not supplied\n" +
            "streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused\n",
            stdout);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify: Given a <c>--dir</c> holding the diverged
    /// <c>jump-land.mmrs</c> (step 12's expected hash replaced) and <c>other-world.mmrs</c>, a copy of
    /// <c>flat-run-30hz.mmrs</c> whose <c>collisionChecksum</c> names collision data no content file has,
    /// and the copied content, When <c>streams verify</c> runs, Then it exits 1 (a divergence outranks a
    /// refusal), prints both the divergence report and
    /// <c>REFUSED stream other-world: stream collisionChecksum at offset 36: collision data 0011223344556677 was not supplied</c>,
    /// and the last line reports 2 streams, 0 match, 1 diverged, 1 refused.
    /// </summary>
    [Fact]
    public void VerifyExitsOneWhenADivergenceAndARefusalBothOccur()
    {
        using var directory = TempDirectories.Create("streams-verify-both");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(content, "content", "test-world.mmcd");
        CopyCommitted(streams, "streams", "jump-land.mmrs");
        var realHash = WriteDivergedJumpLand(streams);
        var flatRun = RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs");
        File.WriteAllBytes(Path.Combine(streams, "other-world.mmrs"), WithCollisionChecksum(flatRun, AbsentChecksum));

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 1, $"simcore streams verify exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        AssertDivergenceReport(lines, realHash, stdout);
        Assert.Contains(
            $"REFUSED stream other-world: stream collisionChecksum at offset 36: collision data {AbsentChecksum:x16} was not supplied",
            lines);
        Assert.Equal("streams verify: 2 streams, 0 match, 1 diverged, 0 not comparable, 1 refused", lines[^1]);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify and contracts/recorded-stream-format.md#decoding checks 5 and
    /// 12 (added in the Phase 3 review): Given a <c>--dir</c> holding an undecodable
    /// <c>flat-run-30hz.mmrs</c> (<c>zero-steps</c> or <c>renumbered-step</c>, as in
    /// <see cref="RecordReplacesAnOldStreamThatNoLongerDecodesAndLogsIt"/>) and an intact copy of
    /// <c>jump-land.mmrs</c>, with the copied content, When <c>streams verify</c> runs, Then it exits 2 and
    /// stdout is exactly <c>REFUSED stream flat-run-30hz: </c> and the decoder's message
    /// (<c>stream stepCount at offset 16: 0 is below 1</c>, or
    /// <c>stream steps[3].step at offset &lt;o&gt;: &lt;n + 100&gt; is not &lt;n&gt;, the initial state's step &lt;S₀&gt; plus 4</c>),
    /// then <c>jump-land: match, &lt;steps&gt; steps, final &lt;x16&gt;</c>, then
    /// <c>streams verify: 2 streams, 1 match, 0 diverged, 0 not comparable, 1 refused</c>: the refused
    /// stream neither crashes the command nor stops it verifying the next one.
    /// </summary>
    [Theory]
    [MemberData(nameof(UndecodableStreamCases))]
    public void VerifyRefusesAStreamThatNoLongerDecodesAndVerifiesTheRest(string caseName)
    {
        using var directory = TempDirectories.Create($"streams-verify-{caseName}");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(content, "content", "test-world.mmcd");
        CopyCommitted(streams, "streams", "jump-land.mmrs");
        var (undecodable, refusal) = Undecodable(caseName, RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs"));
        File.WriteAllBytes(Path.Combine(streams, $"{FlatRun30}.mmrs"), undecodable);
        var jumpLand = RecordedStream.Decode(RepoPaths.ReadAllBytes("simcore", "streams", "jump-land.mmrs"));

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 2, $"{caseName}: simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(
            $"REFUSED stream {FlatRun30}: {refusal}\n" +
            $"jump-land: match, {jumpLand.Steps.Count} steps, final {jumpLand.Steps[^1].ExpectedHash:x16}\n" +
            "streams verify: 2 streams, 1 match, 0 diverged, 0 not comparable, 1 refused\n",
            stdout);
    }

    /// <summary>
    /// contracts/simcore-cli.md#streams-verify: Given (<c>empty</c>) an empty <c>--dir</c> or
    /// (<c>missing</c>) a <c>--dir</c> that does not exist, with a copy of the committed content, When
    /// <c>streams verify</c> runs, Then it exits 2; the empty directory prints exactly
    /// <c>streams verify: 0 streams, 0 match, 0 diverged, 0 not comparable, 0 refused</c>, and the missing
    /// one names the directory on stderr.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyDirectoryCases))]
    public void VerifyOfAnEmptyDirectoryExitsTwo(string caseName)
    {
        using var directory = TempDirectories.Create($"streams-verify-{caseName}");
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(content, "content", "test-world.mmcd");
        var streams = directory.Combine("streams");
        switch (caseName)
        {
            case "empty":
                Directory.CreateDirectory(streams);
                break;
            case "missing":
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 2, $"{caseName}: simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        if (caseName == "empty")
        {
            Assert.Equal("streams verify: 0 streams, 0 match, 0 diverged, 0 not comparable, 0 refused\n", stdout);
        }
        else
        {
            Assert.Contains(streams, stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// contracts/simcore-cli.md#settings-output-and-exit-codes: Given copies of the committed streams,
    /// content and configuration, and <c>streams record --scenario flat-run-30hz --reason "Test
    /// regeneration"</c> or <c>streams verify</c> on them with one path option given as
    /// <paramref name="value"/> (empty, or whitespace only), When the command runs, Then it exits 2, a usage
    /// error rather than an unhandled exception; stderr names the option as
    /// <c>&lt;option&gt;: the path must not be empty or whitespace</c> and has no exception; stdout is empty,
    /// so no stream ran; and every file in the temp directory keeps its bytes and last-write time, with none
    /// added or removed.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyPathCases))]
    public void StreamsRefuseAnEmptyOrWhitespacePathOption(string command, string option, string value)
    {
        using var sandbox = Sandbox.Create($"streams-{command}-empty-path");
        string[] args = command switch
        {
            "record" => sandbox.RecordArgs("--scenario", FlatRun30, "--reason", "Test regeneration"),
            "verify" => ["streams", "verify", "--dir", sandbox.Streams, "--content", sandbox.Content],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown row."),
        };
        var at = Array.IndexOf(args, option);
        Assert.True(at >= 0 && at + 1 < args.Length, $"streams {command} has no {option} to replace in {string.Join(' ', args)}");
        args[at + 1] = value;
        var label = $"streams {command} {option} \"{value}\"";
        var before = Snapshot(sandbox.Root);

        var (exit, stdout, stderr) = SimcoreCli.Run(args);

        Assert.True(exit == 2, $"{label}: exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains($"{option}: the path must not be empty or whitespace", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", stderr, StringComparison.Ordinal);
        Assert.True(stdout.Length == 0, $"{label}: printed results although the path was refused; stdout: {stdout}");
        AssertNothingWritten(before, sandbox.Root, label);
    }

    /// <summary>
    /// contracts/simcore-cli.md#content-files: Given a <c>--dir</c> holding only a copy of
    /// <c>flat-run-30hz.mmrs</c> and a <c>--content</c> holding a copy of <c>test-world.mmcd</c> and a
    /// sparse <c>big.mmcd</c> of 268,435,457 bytes, When <c>streams verify</c> runs, Then it exits 2, stderr
    /// has the line
    /// <c>streams verify: content big.mmcd: collision fileLength at offset 8: the file is 268435457 bytes; the limit is 268435456</c>,
    /// and no stream ran: stdout has no <c>flat-run-30hz</c> line.
    /// </summary>
    [Fact]
    public void VerifyRefusesAnOversizedCollisionFileBeforeRunningAnyStream()
    {
        using var directory = TempDirectories.Create("streams-verify-oversized");
        var streams = Directory.CreateDirectory(directory.Combine("streams")).FullName;
        var content = Directory.CreateDirectory(directory.Combine("content")).FullName;
        CopyCommitted(streams, "streams", $"{FlatRun30}.mmrs");
        CopyCommitted(content, "content", "test-world.mmcd");
        CreateSparse(Path.Combine(content, "big.mmcd"), OversizedLength);

        var (exit, stdout, stderr) = SimcoreCli.Run("streams", "verify", "--dir", streams, "--content", content);

        Assert.True(exit == 2, $"simcore streams verify exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains($"streams verify: {OversizedRefusal}", Lines(stderr));
        Assert.DoesNotContain(FlatRun30, stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// Replaces <c>jump-land.mmrs</c> in <paramref name="streams"/> with its copy whose step at index 11
    /// (step 12) expects <see cref="WrongHash"/>, and returns that step's committed expected hash, the hash
    /// the server computes.
    /// </summary>
    private static ulong WriteDivergedJumpLand(string streams)
    {
        var path = Path.Combine(streams, "jump-land.mmrs");
        var bytes = File.ReadAllBytes(path);
        var stream = RecordedStream.Decode(bytes);
        Assert.True(
            stream.Steps.Count > DivergentIndex && stream.Steps[DivergentIndex].Step == 12u,
            $"jump-land has {stream.Steps.Count} steps; steps[{DivergentIndex}] should be step 12");
        var realHash = stream.Steps[DivergentIndex].ExpectedHash;
        Assert.True(realHash != WrongHash, $"jump-land's step 12 already expects {WrongHash:x16}");
        File.WriteAllBytes(path, StreamBytes.WithExpectedHash(bytes, DivergentIndex, WrongHash));
        return realHash;
    }

    /// <summary>
    /// An undecodable copy of the committed <c>flat-run-30hz.mmrs</c> and the message the decoder refuses it
    /// with (contracts/recorded-stream-format.md#decoding): <c>zero-steps</c>, the stream with no step
    /// entries (<see cref="StreamBytes.WithoutSteps"/>), refused by check 5 at <c>stepCount</c>; or
    /// <c>renumbered-step</c>, entry <see cref="RenumberedIndex"/>'s step number raised by
    /// <see cref="Renumbering"/> (<see cref="StreamBytes.WithStepNumber"/>), refused by check 12 at that
    /// entry's <c>steps[i].step</c>, whose number must be S₀ + i + 1 with S₀ the initial state's
    /// <c>step</c>, its first u32 (contracts/state-serialization.md). Both copies keep a valid trailer.
    /// </summary>
    private static (byte[] Bytes, string Refusal) Undecodable(string caseName, byte[] committed)
    {
        switch (caseName)
        {
            case "zero-steps":
                return (StreamBytes.WithoutSteps(committed), "stream stepCount at offset 16: 0 is below 1");
            case "renumbered-step":
                var stream = RecordedStream.Decode(committed);
                Assert.True(stream.Steps.Count > RenumberedIndex, $"{FlatRun30} has {stream.Steps.Count} steps, too few to renumber steps[{RenumberedIndex}]");
                var initialStep = BinaryPrimitives.ReadUInt32LittleEndian(stream.InitialState.Span);
                var step = stream.Steps[RenumberedIndex].Step;
                Assert.True(
                    step == initialStep + RenumberedIndex + 1u,
                    $"{FlatRun30}'s steps[{RenumberedIndex}] is step {step}, not the initial state's step {initialStep} plus {RenumberedIndex + 1}");
                var renumbered = step + Renumbering;
                var offset = StreamBytes.StepOffset(committed, RenumberedIndex);
                return (
                    StreamBytes.WithStepNumber(committed, RenumberedIndex, renumbered),
                    $"stream steps[{RenumberedIndex}].step at offset {offset}: {renumbered} is not {step}, the initial state's step {initialStep} plus {RenumberedIndex + 1}");
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown row.");
        }
    }

    /// <summary>
    /// Asserts <paramref name="lines"/> hold the divergence report for the diverged <c>jump-land</c>: the
    /// heading line, then the hash line with the expected and the server's hash.
    /// </summary>
    private static void AssertDivergenceReport(string[] lines, ulong realHash, string stdout)
    {
        const string Heading = "DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes";
        var at = Array.IndexOf(lines, Heading);
        Assert.True(at >= 0 && at + 1 < lines.Length, $"stdout has no line \"{Heading}\" followed by the hash line; stdout: {stdout}");
        Assert.Equal($"  hash  expected {WrongHash:x16}  server {realHash:x16}", lines[at + 1]);
    }

    /// <summary>
    /// A copy of <paramref name="stream"/> with the header's <c>collisionChecksum</c> (u64 LE at offset 36,
    /// contracts/recorded-stream-format.md#header-64-bytes) set to <paramref name="checksum"/> and the trailer
    /// recomputed as the XxHash64 (seed 0) of every byte before it.
    /// </summary>
    private static byte[] WithCollisionChecksum(byte[] stream, ulong checksum)
    {
        var copy = (byte[])stream.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(36, 8), checksum);
        var trailer = copy.Length - 8;
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(trailer), XxHash64.HashToUInt64(copy.AsSpan(0, trailer), 0));
        return copy;
    }

    /// <summary>
    /// A <see cref="StepFault"/> from a real step: a world on <see cref="TestWorlds.Flat"/> at 30 Hz steps
    /// with NaN injected at <c>characters[0].velocity.y</c>, as <c>StreamRunnerTests</c> does.
    /// </summary>
    private static StepFault NonFiniteVelocityFault()
    {
        var world = new SimWorld(TestConfigs.Compiled(30), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(
            TestStates.Build(0, [TestStates.StandingCharacter(1, 0.5f, 0.5f) with { Position = (0.5f, MovementWorlds.ContactSkin, 0.5f) }]),
            matchSalt: 7);
        Assert.True(
            StateSerializer.TryFindField(VelocityYPath, out var ordinal, out var index),
            $"StateSerializer.TryFindField does not know {VelocityYPath}");
        var fault = world.StepWithInjectedBits([], ordinal, index, NaNBits).Fault;
        Assert.Equal(StepFaultCode.NonFinite, fault.Code);
        Assert.Equal(VelocityYPath, fault.Path);
        return fault;
    }

    /// <summary>The step number of the first entry whose expected hash differs between the two streams, or null.</summary>
    private static uint? FirstChangedStep(RecordedStream old, RecordedStream recorded)
    {
        var common = System.Math.Min(old.Steps.Count, recorded.Steps.Count);
        for (var i = 0; i < common; i++)
        {
            if (old.Steps[i].ExpectedHash != recorded.Steps[i].ExpectedHash)
            {
                return recorded.Steps[i].Step;
            }
        }

        return null;
    }

    /// <summary>The names of the <c>*.mmrs</c> files in <paramref name="directory"/>, without the extension, in ordinal order.</summary>
    private static List<string> StreamNames(string directory)
    {
        var names = Directory.GetFiles(directory)
            .Where(path => path.EndsWith(".mmrs", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Every committed <c>simcore/content/*.mmcd</c>, read through <see cref="RepoPaths"/> and decoded, in ordinal order.</summary>
    private static CollisionWorld[] CommittedWorlds()
    {
        var files = Directory.GetFiles(RepoPaths.Combine("simcore", "content"))
            .Where(path => path.EndsWith(".mmcd", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToList();
        files.Sort(StringComparer.Ordinal);
        Assert.True(files.Count > 0, "simcore/content holds no .mmcd collision file");
        return [.. files.Select(name => CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", name)))];
    }

    /// <summary>The hash of every packed step record in <paramref name="records"/>, in order; the records fill it exactly.</summary>
    private static List<ulong> RecordHashes(byte[] records, string name)
    {
        var hashes = new List<ulong>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(
                StepRecord.TryRead(records.AsSpan(offset), out _, out _, out var hash, out var length),
                $"{name}: the run's records do not parse at byte {offset} of {records.Length}");
            hashes.Add(hash);
            offset += length;
        }

        return hashes;
    }

    /// <summary>Copies the committed <c>simcore/&lt;folder&gt;/&lt;file&gt;</c>, read through <see cref="RepoPaths"/>, into <paramref name="target"/>.</summary>
    private static void CopyCommitted(string target, string folder, string file) =>
        File.WriteAllBytes(Path.Combine(target, file), RepoPaths.ReadAllBytes("simcore", folder, file));

    /// <summary>Creates a sparse file of <paramref name="length"/> zero bytes with <see cref="FileStream.SetLength"/>.</summary>
    private static void CreateSparse(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(length);
    }

    /// <summary>Sets <c>&lt;section&gt;.&lt;field&gt;</c> of the configuration file at <paramref name="path"/> to <paramref name="value"/>.</summary>
    private static void EditConfig(string path, string section, string field, double value)
    {
        var config = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        config[section]![field] = value;
        File.WriteAllText(path, config.ToJsonString());
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

    /// <summary>
    /// The non-blank lines the regeneration log gained: it must still start with <paramref name="before"/>,
    /// and the appended entry must start on a line of its own.
    /// </summary>
    private static string[] AppendedEntry(string before, string after, string label)
    {
        Assert.True(
            after.StartsWith(before, StringComparison.Ordinal),
            $"{label}: the regeneration log no longer starts with its old text ({before.Length} characters)");
        Assert.True(after.Length > before.Length, $"{label}: nothing was appended to the regeneration log");
        var appended = after[before.Length..];
        Assert.True(
            before.Length == 0 || before.EndsWith('\n') || appended.StartsWith('\n'),
            $"{label}: the appended entry does not start on a new line: \"{appended}\"");
        return [.. appended.Split('\n').Where(line => line.Length > 0)];
    }

    /// <summary>Asserts the heading's time lies between the whole second the run started in and the time it finished.</summary>
    private static void AssertHeadingWithin(string heading, DateTime started, DateTime finished, string label)
    {
        var time = DateTime.ParseExact(
            heading[3..],
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.True(
            time >= started && time <= finished,
            $"{label}: the entry heading's time {time:O} is outside the run, {started:O} to {finished:O}");
    }

    private static DateTime WholeSecond(DateTime time) => new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    /// <summary>
    /// Every file under <paramref name="root"/> by its relative path (with <c>/</c>): its length, the
    /// XxHash128 of its bytes, read through a read-only stream, and its last-write time.
    /// </summary>
    private static SortedDictionary<string, FileState> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, FileState>(StringComparer.Ordinal);
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

    /// <summary>
    /// <c>added &lt;path&gt;</c>, <c>removed &lt;path&gt;</c> and <c>changed &lt;path&gt;</c> (bytes or last-write time)
    /// for every difference between two snapshots, in ordinal order.
    /// </summary>
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

    /// <summary>Asserts no file under <paramref name="root"/> was added, removed or changed since <paramref name="before"/>.</summary>
    private static void AssertNothingWritten(SortedDictionary<string, FileState> before, string root, string label)
    {
        var differences = Differences(before, Snapshot(root));
        Assert.True(differences.Length == 0, $"{label}: the refused command wrote to the temp directory: {string.Join(", ", differences)}");
    }

    /// <summary>A file's length, content hash and last-write time.</summary>
    private readonly record struct FileState(long Length, UInt128 Hash, DateTime LastWriteUtc);

    /// <summary>
    /// A unique temp copy of <c>simcore/</c> (its <c>config</c>, <c>content</c> and <c>streams</c>), the
    /// folder every <c>streams record</c> test passes as <c>--out</c>, <c>--content</c> and <c>--config</c>.
    /// </summary>
    private sealed class Sandbox : IDisposable
    {
        private readonly TempDirectory _directory;

        private Sandbox(TempDirectory directory) => _directory = directory;

        public string Root => _directory.Path;

        public string Streams => _directory.Combine("streams");

        public string Content => _directory.Combine("content");

        public string Config => _directory.Combine("config", "default.json");

        public string LogPath => Path.Combine(Streams, RegenerationLog);

        public static Sandbox Create(string prefix) => new(TempDirectories.CopyOf(RepoPaths.SimCore, prefix));

        public string Stream(string name) => Path.Combine(Streams, $"{name}.mmrs");

        /// <summary><c>streams record &lt;selection&gt; --out &lt;streams&gt; --content &lt;content&gt; --config &lt;default.json&gt;</c>.</summary>
        public string[] RecordArgs(params string[] selection) =>
            ["streams", "record", .. selection, "--out", Streams, "--content", Content, "--config", Config];

        public void Dispose() => _directory.Dispose();
    }
}
