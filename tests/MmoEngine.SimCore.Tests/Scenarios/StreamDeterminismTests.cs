using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// US1 acceptance scenario 8: a recorded stream made for a step length of 20, 30 or 60 Hz gives
/// identical per-step hashes when it is stepped twice. The committed <c>flat-run-&lt;hz&gt;hz</c> stream
/// runs twice over the committed <c>simcore/content/test-world.mmcd</c>, both read through
/// <see cref="RepoPaths"/>, read-only; and its definition is built twice in process, each time over its own
/// decoding of the generated test world, and both builds run.
/// </summary>
public sealed class StreamDeterminismTests
{
    /// <summary>
    /// US1.8: Given a step length of <paramref name="hz"/> and the committed
    /// <c>simcore/streams/flat-run-&lt;hz&gt;hz.mmrs</c> (header <c>stepHz</c> == <paramref name="hz"/>) with
    /// <c>simcore/content/test-world.mmcd</c>, When <see cref="StreamRunner"/> runs it twice, Then both
    /// outputs are byte-identical, hold <paramref name="hz"/> records, and each record's hash equals that
    /// step's committed expected hash; and When <c>ScenarioCatalog.Get("flat-run-&lt;hz&gt;hz")</c> is built
    /// twice at <paramref name="hz"/>, Then both encode to identical bytes and run to byte-identical records.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StreamForEachRateGivesIdenticalHashesTwice(int hz)
    {
        var name = $"flat-run-{hz}hz";
        var streamBytes = RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs");
        var collisionBytes = RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");
        var committed = RecordedStream.Decode(streamBytes);
        Assert.True(committed.StepHz == hz, $"{name}: the committed stream's stepHz is {committed.StepHz}, not {hz}");

        // The committed stream, run twice.
        var first = StreamRunner.Run(streamBytes, collisionBytes);
        var second = StreamRunner.Run(streamBytes, collisionBytes);
        AssertIdentical(first, second, $"{name}: the committed stream's two runs");
        var hashes = RecordHashes(first, name);
        Assert.True(hashes.Count == hz, $"{name}: the run gave {hashes.Count} records, not {hz}");
        Assert.True(
            committed.Steps.Count == hz,
            $"{name}: the committed stream has {committed.Steps.Count} steps, not {hz}");
        for (var i = 0; i < hz; i++)
        {
            Assert.True(
                hashes[i] == committed.Steps[i].ExpectedHash,
                $"{name}: step {committed.Steps[i].Step} hashed {hashes[i]:x16}; the committed stream expects {committed.Steps[i].ExpectedHash:x16}");
        }

        // The definition, built twice over two decodings of the generated world, and both builds run.
        var worldA = TestWorlds.TestWorld;
        var worldB = TestWorlds.Load(TestWorldGenerator.Generate());
        var builtA = ScenarioCatalog.Get(name).Build(TestConfigs.Default(), worldA, hz);
        var builtB = ScenarioCatalog.Get(name).Build(TestConfigs.Default(), worldB, hz);
        var encodedA = builtA.Encode();
        var encodedB = builtB.Encode();
        AssertIdentical(encodedA, encodedB, $"{name}: the two builds' encoded streams");

        var runA = StreamRunner.Run(encodedA, new[] { worldA });
        var runB = StreamRunner.Run(encodedB, new[] { worldB });
        AssertIdentical(runA, runB, $"{name}: the two builds' runs");
        var builtHashes = RecordHashes(runA, name);
        Assert.True(builtHashes.Count == hz, $"{name}: a build's run gave {builtHashes.Count} records, not {hz}");
    }

    /// <summary>Asserts <paramref name="a"/> and <paramref name="b"/> are the same bytes, naming the first that differs.</summary>
    private static void AssertIdentical(byte[] a, byte[] b, string what)
    {
        Assert.True(a.Length > 0, $"{what}: no bytes");
        var common = System.Math.Min(a.Length, b.Length);
        var first = a.AsSpan(0, common).CommonPrefixLength(b.AsSpan(0, common));
        Assert.True(
            a.Length == b.Length && first == common,
            $"{what} differ: {a.Length} and {b.Length} bytes, first difference at byte {first}");
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
}
