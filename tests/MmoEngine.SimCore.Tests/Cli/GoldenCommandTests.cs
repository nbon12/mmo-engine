using System.Globalization;
using System.IO.Hashing;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#golden-write (FR-023, US6 Independent Test): <c>simcore golden write --dir
/// &lt;dir&gt;</c> reads every <c>&lt;case&gt;.json</c> under <c>input/</c>, <c>collision/</c> and
/// <c>stream/</c>, computes every <c>.bin</c> in memory (a valid case's from its encoder, a refusal case's
/// from its mutations applied to its base), writes only the files whose bytes differ, and prints one
/// <c>&lt;format&gt;/&lt;case&gt;: written|unchanged, &lt;n&gt; bytes</c> line per case and a total. A case it
/// cannot build (invalid JSON, a format that differs from its folder, a base that is not a valid case,
/// an encoder refusal) or a missing folder exits 2, and nothing is written.
/// </summary>
/// <remarks>
/// Every test runs the command on its own <see cref="TempDirectories"/> copy of the committed
/// <c>simcore/golden/</c>, whose files are opened read-only; nothing under the repository is written. Each
/// copy's files are given a last-write time of 2020-01-01 before the run, so a file the command rewrote
/// with the same bytes still shows up as changed.
/// </remarks>
public sealed class GoldenCommandTests
{
    /// <summary>The number of golden cases: 38 input, 22 collision and 32 stream.</summary>
    private const int CaseCount = 92;

    /// <summary>The last-write time every copied file is given before a run.</summary>
    private static readonly DateTime OldWriteTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// US6 Independent Test: given a copy of <c>simcore/golden/</c>, when <c>simcore golden write --dir
    /// &lt;copy&gt;</c> runs, then it exits 0 with no Error-level event on stderr; stdout is exactly 93
    /// lines, one <c>&lt;format&gt;/&lt;case&gt;: unchanged, &lt;length&gt; bytes</c> line per case (formats in
    /// the order input, collision, stream, cases in ordinal order), starting with
    /// <c>input/aim-pitch-down: unchanged, 17 bytes</c> and ending with
    /// <c>golden write: 92 cases, 0 written, 92 unchanged</c>; every <c>.bin</c> in the copy is the
    /// committed one byte for byte, with its last-write time unchanged, so none was rewritten; and no file
    /// appeared in the copy.
    /// </summary>
    [Fact]
    public void GoldenWriteReproducesTheCommittedFixtures()
    {
        using var copy = CopyOfGolden("golden-write-unchanged");
        var before = Snapshot(copy.Path);

        var (exit, stdout, stderr) = SimcoreCli.Run("golden", "write", "--dir", copy.Path);

        Assert.True(exit == 0, $"simcore golden write --dir {copy.Path} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        AssertNoErrorEvent(stderr);
        var lines = Lines(stdout);
        Assert.True(lines.Length == CaseCount + 1, $"golden write printed {lines.Length} lines, expected {CaseCount + 1}: {stdout}");
        Assert.Equal(ExpectedLines(written: []), lines);
        Assert.Equal("input/aim-pitch-down: unchanged, 17 bytes", lines[0]);
        Assert.Contains("collision/full-cell: unchanged, 3176 bytes", lines);
        Assert.Contains("collision/tiny-world: unchanged, 188 bytes", lines);
        Assert.Contains("stream/two-steps: unchanged, 950 bytes", lines);
        Assert.Equal("golden write: 92 cases, 0 written, 92 unchanged", lines[^1]);

        AssertBinsAreTheCommittedOnes(copy.Path);
        var differences = Differences(before, Snapshot(copy.Path));
        Assert.True(
            differences.Length == 0,
            $"golden write changed the copy, though every .bin was already the committed one: {string.Join(", ", differences)}");
    }

    /// <summary>
    /// contracts/simcore-cli.md#golden-write step 4: given a copy of <c>simcore/golden/</c> with
    /// <c>input/example.bin</c> deleted and byte 20 of <c>collision/tiny-world.bin</c> flipped, when
    /// <c>simcore golden write</c> runs, then it exits 0; the lines <c>input/example: written, 17 bytes</c>
    /// and <c>collision/tiny-world: written, 188 bytes</c> appear in their ordinal places among 90
    /// <c>unchanged</c> lines; the total is <c>golden write: 92 cases, 2 written, 90 unchanged</c>; both
    /// files equal the committed ones again; and only those two files changed, with no temporary file left
    /// in either folder (the writes go through <c>AtomicFile</c>).
    /// </summary>
    [Fact]
    public void GoldenWriteRewritesOnlyChangedCases()
    {
        using var copy = CopyOfGolden("golden-write-changed");
        var example = copy.Combine("input", "example.bin");
        var tinyWorld = copy.Combine("collision", "tiny-world.bin");
        File.Delete(example);
        var flipped = File.ReadAllBytes(tinyWorld);
        flipped[20] ^= 0xFF;
        File.WriteAllBytes(tinyWorld, flipped);
        File.SetLastWriteTimeUtc(tinyWorld, OldWriteTime);
        var before = Snapshot(copy.Path);

        var (exit, stdout, stderr) = SimcoreCli.Run("golden", "write", "--dir", copy.Path);

        Assert.True(exit == 0, $"simcore golden write --dir {copy.Path} exited {exit}; stdout: {stdout}; stderr: {stderr}");
        AssertNoErrorEvent(stderr);
        var lines = Lines(stdout);
        Assert.Equal(ExpectedLines(written: ["input/example", "collision/tiny-world"]), lines);
        Assert.Contains("input/example: written, 17 bytes", lines);
        Assert.Contains("collision/tiny-world: written, 188 bytes", lines);
        Assert.Equal("golden write: 92 cases, 2 written, 90 unchanged", lines[^1]);

        Assert.True(File.Exists(example), $"{example} was not written");
        Assert.Equal(GoldenFixtures.ReadBin("input", "example"), File.ReadAllBytes(example));
        Assert.Equal(GoldenFixtures.ReadBin("collision", "tiny-world"), File.ReadAllBytes(tinyWorld));
        AssertBinsAreTheCommittedOnes(copy.Path);
        foreach (var folder in new[] { "input", "collision" })
        {
            var temporary = Directory.GetFiles(copy.Combine(folder), "*.tmp");
            Assert.True(temporary.Length == 0, $"golden write left temporary files in {folder}/: {string.Join(", ", temporary)}");
        }

        string[] rewritten = ["added input/example.bin", "changed collision/tiny-world.bin"];
        Assert.Equal(rewritten, Differences(before, Snapshot(copy.Path)));
    }

    /// <summary>
    /// contracts/simcore-cli.md#golden-write steps 1 and 2: given a fresh copy of <c>simcore/golden/</c>
    /// with <c>input/example.bin</c> deleted and one case changed so that it cannot be built
    /// (<paramref name="mutationName"/>: <c>input/neutral.json</c> is not JSON; <c>collision/tiny-world.json</c>
    /// says it is an input case; <c>stream/truncated.json</c>'s base is the refusal case
    /// <c>checksum-mismatch</c>, or a case that does not exist; <c>collision/heightfield-only.json</c>'s
    /// <c>cellSize</c> is 0.1, below the 0.25 limit, so <c>CollisionFile.Encode</c> refuses it with
    /// <c>OutOfRange</c> at 24, <c>cellSize</c>; <c>stream/two-steps.json</c>'s config has no weapon entries,
    /// which the block's structure allows and <c>RecordedStream.Encode</c> refuses, because a configuration
    /// needs 1 to 8 weapons; <c>stream/truncated.json</c> truncates to 951 bytes, past its base's 950, so its
    /// mutation cannot be applied; the <c>stream/</c> folder is missing), when <c>simcore golden write</c>
    /// runs, then it exits 2; stdout's last line starts with
    /// <c>golden write: refused, &lt;format&gt;/&lt;case&gt;.json: </c>, naming the changed file (or
    /// <c>stream/</c> for the missing folder), and for the encoder and mutation rows ends with the encoder's
    /// error or the mutation's in full (<c>encoder-refuses</c>' is the whole line, ending with the encoder's
    /// <c>collision cellSize at offset 24: 0.1 is outside [0.25, 64]</c>, the <c>OutOfRange</c> detail);
    /// an Error event is logged on stderr; and nothing is written:
    /// <c>input/example.bin</c> is not recreated, and every other file keeps its bytes and last-write time.
    /// </summary>
    [Theory]
    [InlineData("invalid-json")]
    [InlineData("format-mismatch")]
    [InlineData("base-not-valid")]
    [InlineData("missing-base")]
    [InlineData("encoder-refuses")]
    [InlineData("encoder-refuses-config")]
    [InlineData("mutation-refused")]
    [InlineData("missing-format-folder")]
    public void GoldenWriteRefusesAnInvalidCaseAndWritesNothing(string mutationName)
    {
        using var copy = CopyOfGolden($"golden-write-{mutationName}");
        var example = copy.Combine("input", "example.bin");
        File.Delete(example);
        var changed = ChangeCase(copy, mutationName);
        var before = Snapshot(copy.Path);

        var (exit, stdout, stderr) = SimcoreCli.Run("golden", "write", "--dir", copy.Path);

        Assert.True(exit == 2, $"{mutationName}: simcore golden write exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(lines.Length > 0, $"{mutationName}: golden write printed nothing on stdout; stderr: {stderr}");
        Assert.StartsWith($"golden write: refused, {changed}: ", lines[^1], StringComparison.Ordinal);
        switch (mutationName)
        {
            case "encoder-refuses":
                Assert.Equal("golden write: refused, collision/heightfield-only.json: collision cellSize at offset 24: 0.1 is outside [0.25, 64]", lines[^1]);
                break;
            case "encoder-refuses-config":
                Assert.EndsWith(": the encoder refuses the case (The configuration is invalid: weapons: must have between 1 and 8 entries)", lines[^1], StringComparison.Ordinal);
                break;
            case "mutation-refused":
                Assert.EndsWith(": mutations[0]: truncate to 951 bytes is past the length 950", lines[^1], StringComparison.Ordinal);
                break;
            case "missing-format-folder":
                Assert.Equal("golden write: refused, stream/: the folder does not exist", lines[^1]);
                break;
        }

        AssertErrorEvent(stderr, mutationName);
        Assert.False(File.Exists(example), $"{mutationName}: the refused golden write recreated input/example.bin");
        AssertBinsAreTheCommittedOnes(copy.Path);
        var differences = Differences(before, Snapshot(copy.Path));
        Assert.True(
            differences.Length == 0,
            $"{mutationName}: the refused golden write changed the copy: {string.Join(", ", differences)}");
    }

    /// <summary>
    /// contracts/simcore-cli.md#golden-write: given a <c>--dir</c> that names a folder that does not exist
    /// under an empty temp root, when <c>simcore golden write</c> runs, then it exits 2, stdout's last line
    /// starts with <c>golden write: refused, </c>, an Error event is logged on stderr, and nothing is
    /// created: the folder still does not exist and the temp root is still empty.
    /// </summary>
    [Fact]
    public void GoldenWriteOfAMissingDirectoryExitsTwo()
    {
        using var root = TempDirectories.Create("golden-write-missing");
        var missing = root.Combine("no-such-golden");

        var (exit, stdout, stderr) = SimcoreCli.Run("golden", "write", "--dir", missing);

        Assert.True(exit == 2, $"simcore golden write --dir {missing} exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(lines.Length > 0, $"golden write printed nothing on stdout; stderr: {stderr}");
        Assert.StartsWith("golden write: refused, ", lines[^1], StringComparison.Ordinal);
        AssertErrorEvent(stderr, "a missing directory");
        Assert.False(Directory.Exists(missing), $"the refused golden write created {missing}");
        var entries = Directory.GetFileSystemEntries(root.Path);
        Assert.True(entries.Length == 0, $"the refused golden write created entries in the temp root: {string.Join(", ", entries)}");
    }

    /// <summary>
    /// contracts/simcore-cli.md#golden-write (exit 0 or 2): given a copy of <c>simcore/golden/</c> in which
    /// <c>input/example.bin</c> is a folder, so that case's computed bytes cannot be written there, when
    /// <c>simcore golden write</c> runs, then it exits 2; stdout's last line starts with
    /// <c>golden write: refused, input/example.bin: the file cannot be written (</c>; an Error event is logged
    /// on stderr; the folder is still there; no temporary file is left in <c>input/</c>; and every file keeps
    /// its bytes and last-write time.
    /// </summary>
    [Fact]
    public void GoldenWriteThatCannotWriteABinExitsTwo()
    {
        using var copy = CopyOfGolden("golden-write-unwritable");
        var example = copy.Combine("input", "example.bin");
        File.Delete(example);
        Directory.CreateDirectory(example);
        var before = Snapshot(copy.Path);

        var (exit, stdout, stderr) = SimcoreCli.Run("golden", "write", "--dir", copy.Path);

        Assert.True(exit == 2, $"simcore golden write exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        var lines = Lines(stdout);
        Assert.True(lines.Length > 0, $"golden write printed nothing on stdout; stderr: {stderr}");
        Assert.StartsWith("golden write: refused, input/example.bin: the file cannot be written (", lines[^1], StringComparison.Ordinal);
        AssertErrorEvent(stderr, "an unwritable .bin");
        Assert.True(Directory.Exists(example), $"the refused golden write removed the folder {example}");
        var temporary = Directory.GetFiles(copy.Combine("input"), "*.tmp");
        Assert.True(temporary.Length == 0, $"golden write left temporary files in input/: {string.Join(", ", temporary)}");
        var differences = Differences(before, Snapshot(copy.Path));
        Assert.True(differences.Length == 0, $"the refused golden write changed the copy: {string.Join(", ", differences)}");
    }

    /// <summary>
    /// The stdout <c>golden write</c> prints over the committed fixtures, as lines: one
    /// <c>&lt;format&gt;/&lt;case&gt;: written|unchanged, &lt;n&gt; bytes</c> line per case of the contracts'
    /// tables, formats in <see cref="GoldenFixtures.Formats"/> order and cases in ordinal order, with
    /// <c>written</c> for the cases in <paramref name="written"/> (as <c>&lt;format&gt;/&lt;case&gt;</c>) and
    /// n the committed <c>.bin</c>'s length; then the total line.
    /// </summary>
    private static string[] ExpectedLines(IReadOnlyCollection<string> written)
    {
        var lines = new List<string>();
        foreach (var format in GoldenFixtures.Formats)
        {
            foreach (var caseName in GoldenFixtures.ContractCaseNames(format).Order(StringComparer.Ordinal))
            {
                var label = $"{format}/{caseName}";
                var status = written.Contains(label, StringComparer.Ordinal) ? "written" : "unchanged";
                var length = GoldenFixtures.ReadBin(format, caseName).Length;
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {status}, {length} bytes"));
            }
        }

        Assert.True(lines.Count == CaseCount, $"the contracts' tables list {lines.Count} cases, expected {CaseCount}");
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"golden write: {CaseCount} cases, {written.Count} written, {CaseCount - written.Count} unchanged"));
        return [.. lines];
    }

    /// <summary>
    /// Changes one case of <paramref name="copy"/> for a row of
    /// <see cref="GoldenWriteRefusesAnInvalidCaseAndWritesNothing"/>, after asserting what the row assumes
    /// about the committed case, and returns the changed file as <c>&lt;format&gt;/&lt;case&gt;.json</c>.
    /// </summary>
    private static string ChangeCase(TempDirectory copy, string mutationName)
    {
        switch (mutationName)
        {
            case "invalid-json":
                File.WriteAllText(copy.Combine("input", "neutral.json"), "{");
                return "input/neutral.json";

            case "format-mismatch":
                EditCase(copy, "collision", "tiny-world", root =>
                {
                    Assert.Equal("collision", root["format"]?.GetValue<string>());
                    root["format"] = "input";
                });
                return "collision/tiny-world.json";

            case "base-not-valid":
                using (var target = GoldenFixtures.ReadJson("stream", "checksum-mismatch"))
                {
                    Assert.Equal("refusal", target.RootElement.GetProperty("kind").GetString());
                }

                EditCase(copy, "stream", "truncated", root =>
                {
                    Assert.Equal("two-steps", root["base"]?.GetValue<string>());
                    root["base"] = "checksum-mismatch";
                });
                return "stream/truncated.json";

            case "missing-base":
                Assert.False(File.Exists(copy.Combine("stream", "no-such-case.json")), "the copy has a stream/no-such-case.json");
                EditCase(copy, "stream", "truncated", root =>
                {
                    Assert.Equal("two-steps", root["base"]?.GetValue<string>());
                    root["base"] = "no-such-case";
                });
                return "stream/truncated.json";

            case "encoder-refuses":
                // 0.1 parses to exactly these bits, so the JSON is well formed and only the encoder's range
                // check (cellSize in [0.25, 64], contracts/collision-data-format.md#limits) refuses it.
                Assert.Equal(0x3DCCCCCDu, BitConverter.SingleToUInt32Bits(float.Parse("0.1", CultureInfo.InvariantCulture)));
                EditCase(copy, "collision", "heightfield-only", root =>
                {
                    Assert.IsType<JsonObject>(root["cellSize"]);
                    root["cellSize"] = JsonNode.Parse("""{ "bits": "0x3DCCCCCD", "value": 0.1 }""");
                });
                return "collision/heightfield-only.json";

            case "encoder-refuses-config":
                // The 42 weapon entries (fieldIds 0x2000 to 0x2FFF) removed: the block keeps its structure, with
                // weapon slots 0 to N − 1 present for N = 0, and only the configuration's shape rule refuses it.
                EditCase(copy, "stream", "two-steps", root =>
                {
                    var config = Assert.IsType<JsonArray>(root["config"]);
                    var weapons = config.Where(entry => entry!["fieldId"]!.GetValue<string>().StartsWith("0x2", StringComparison.Ordinal)).ToArray();
                    Assert.Equal(42, weapons.Length);
                    foreach (var entry in weapons)
                    {
                        config.Remove(entry);
                    }
                });
                return "stream/two-steps.json";

            case "mutation-refused":
                EditCase(copy, "stream", "truncated", root =>
                {
                    var truncate = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(root["mutations"])));
                    Assert.Equal(900, truncate["length"]?.GetValue<int>());
                    truncate["length"] = 951;
                });
                return "stream/truncated.json";

            case "missing-format-folder":
                Directory.Delete(copy.Combine("stream"), recursive: true);
                return "stream/";

            default:
                throw new ArgumentOutOfRangeException(nameof(mutationName), mutationName, "no golden write refusal row");
        }
    }

    /// <summary>
    /// Rewrites <c>&lt;format&gt;/&lt;case&gt;.json</c> in <paramref name="copy"/> after <paramref name="edit"/>
    /// changes its root object. Values the edit does not touch keep their raw JSON text.
    /// </summary>
    private static void EditCase(TempDirectory copy, string format, string caseName, Action<JsonObject> edit)
    {
        var path = copy.Combine(format, caseName + ".json");
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path)));
        edit(root);
        File.WriteAllText(path, root.ToJsonString(IndentedJson));
    }

    /// <summary>
    /// A unique temp copy of the committed <c>simcore/golden/</c>, copied read-only, with every file's
    /// last-write time set to <see cref="OldWriteTime"/>.
    /// </summary>
    private static TempDirectory CopyOfGolden(string prefix)
    {
        var copy = TempDirectories.CopyOf(GoldenFixtures.Root, prefix);
        foreach (var file in Directory.GetFiles(copy.Path, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, OldWriteTime);
        }

        return copy;
    }

    /// <summary>Asserts that every <c>.bin</c> under <paramref name="root"/> equals the committed file at the same path, byte for byte.</summary>
    private static void AssertBinsAreTheCommittedOnes(string root)
    {
        foreach (var file in Directory.GetFiles(root, "*.bin", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar);
            var committed = RepoPaths.ReadAllBytes(["simcore", "golden", .. relative]);
            Assert.True(
                File.ReadAllBytes(file).AsSpan().SequenceEqual(committed),
                $"{string.Join('/', relative)} in the copy differs from the committed one");
        }
    }

    /// <summary>Asserts that no stderr line is a compact JSON event at Error or Fatal level (<c>@l</c>).</summary>
    private static void AssertNoErrorEvent(string stderr)
    {
        var errors = Events(stderr).Where(IsErrorEvent).ToArray();
        Assert.True(errors.Length == 0, $"golden write logged Error-level events: {string.Join(" | ", errors)}");
    }

    /// <summary>Asserts that at least one stderr line is a compact JSON event at Error level.</summary>
    private static void AssertErrorEvent(string stderr, string label)
    {
        Assert.True(Events(stderr).Any(IsErrorEvent), $"{label}: the refused golden write logged no Error event; stderr: {stderr}");
    }

    private static bool IsErrorEvent(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("@l", out var level)
            && level.ValueKind == JsonValueKind.String
            && level.GetString() is "Error" or "Fatal";
    }

    /// <summary>The non-blank stderr lines, each asserted to be a compact JSON object.</summary>
    private static IEnumerable<string> Events(string stderr)
    {
        foreach (var raw in Lines(stderr))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                Assert.True(document.RootElement.ValueKind == JsonValueKind.Object, $"stderr line is a JSON {document.RootElement.ValueKind}, not an event: {line}");
            }
            catch (JsonException exception)
            {
                Assert.Fail($"stderr line is not a compact JSON event ({exception.Message}): {line}");
            }

            yield return line;
        }
    }

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
    /// <c>added &lt;path&gt;</c>, <c>removed &lt;path&gt;</c> and <c>changed &lt;path&gt;</c> (bytes or last-write
    /// time) for every difference between two snapshots, in ordinal order.
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

    /// <summary>A file's length, content hash and last-write time.</summary>
    private readonly record struct FileState(long Length, UInt128 Hash, DateTime LastWriteUtc);
}
