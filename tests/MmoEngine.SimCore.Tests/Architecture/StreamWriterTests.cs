using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Architecture;

/// <summary>
/// FR-028, contracts/recorded-stream-format.md#regeneration: committed expected hashes change only
/// through <c>simcore streams record</c>. The stream file writer's three entry points (write a stream,
/// remove a stream, append to the regeneration log) are called from the record command's handler and
/// nowhere else, only the writer knows the regeneration log's file name, and
/// no test reaches the writer. The check reads the sources as text: every <c>*.cs</c> under
/// <c>src/</c> and <c>tests/</c>, outside <c>bin</c> and <c>obj</c>, through <see cref="RepoPaths"/>,
/// read-only.
/// </summary>
public sealed class StreamWriterTests
{
    private const string ThisFile = "tests/MmoEngine.SimCore.Tests/Architecture/StreamWriterTests.cs";

    private const string StreamsCommandFile = "src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs";

    private const string StreamFilesFile = "src/MmoEngine.SimCore.Cli/Commands/StreamFiles.cs";

    private const string RegenerationLog = "REGENERATIONS.md";

    private const string WriterPrefix = "StreamFiles.";

    private static readonly string[] WriterCalls =
        [WriterPrefix + "WriteCommitted(", WriterPrefix + "RemoveCommitted(", WriterPrefix + "AppendRegenerations("];

    /// <summary>
    /// Given every <c>src/**/*.cs</c> and <c>tests/**/*.cs</c> outside <c>bin</c> and <c>obj</c>, Then the
    /// texts <c>StreamFiles.WriteCommitted(</c>, <c>StreamFiles.RemoveCommitted(</c> and
    /// <c>StreamFiles.AppendRegenerations(</c> each occur exactly once in <c>src/</c>, all three in
    /// <c>src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs</c>;
    /// the text <c>REGENERATIONS.md</c> occurs in no <c>src/</c> file other than
    /// <c>src/MmoEngine.SimCore.Cli/Commands/StreamFiles.cs</c>; and no test file other than this one
    /// contains <c>StreamFiles.</c>.
    /// </summary>
    [Fact]
    public void OnlyTheRecordCommandWritesStreamFiles()
    {
        var sources = ReadSources("src");
        var tests = ReadSources("tests");
        Assert.True(sources.Count > 0, "no .cs file was found under src/");
        Assert.True(tests.ContainsKey(ThisFile), $"the scan of tests/ did not find {ThisFile}");

        foreach (var call in WriterCalls)
        {
            var hits = sources
                .Select(source => (source.Key, Count: Occurrences(source.Value, call)))
                .Where(hit => hit.Count > 0)
                .ToArray();
            var total = hits.Sum(hit => hit.Count);
            Assert.True(
                total == 1 && hits[0].Key == StreamsCommandFile,
                $"`{call}` must occur exactly once in src/, in {StreamsCommandFile}; it occurs {total} times: [{string.Join(", ", hits.Select(hit => $"{hit.Key} ×{hit.Count}"))}]");
        }

        var logNamers = sources
            .Where(source => source.Key != StreamFilesFile && source.Value.Contains(RegenerationLog, StringComparison.Ordinal))
            .Select(source => source.Key)
            .ToArray();
        Assert.True(
            logNamers.Length == 0,
            $"only {StreamFilesFile} may name {RegenerationLog} in src/; it also occurs in: {string.Join(", ", logNamers)}");

        var writerTests = tests
            .Where(test => test.Key != ThisFile && test.Value.Contains(WriterPrefix, StringComparison.Ordinal))
            .Select(test => test.Key)
            .ToArray();
        Assert.True(
            writerTests.Length == 0,
            $"no test may reach the stream file writer; `{WriterPrefix}` occurs in: {string.Join(", ", writerTests)}");
    }

    /// <summary>How many times <paramref name="text"/> holds <paramref name="value"/>, counting non-overlapping occurrences.</summary>
    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Every <c>*.cs</c> file under the repository's <paramref name="top"/> folder, skipping any folder
    /// named <c>bin</c> or <c>obj</c>, keyed by its repository-relative path with <c>/</c> separators and
    /// read through <see cref="RepoPaths.ReadAllText"/>.
    /// </summary>
    private static SortedDictionary<string, string> ReadSources(string top)
    {
        var sources = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(RepoPaths.Combine(top));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.GetFiles(directory))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(RepoPaths.Root, file).Replace(Path.DirectorySeparatorChar, '/');
                sources.Add(relative, RepoPaths.ReadAllText(relative));
            }

            foreach (var child in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is not ("bin" or "obj"))
                {
                    pending.Push(child);
                }
            }
        }

        return sources;
    }
}
