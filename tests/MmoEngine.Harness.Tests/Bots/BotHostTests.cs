using System.Globalization;
using System.Net;
using MmoEngine.Harness.Bots;
using MmoEngine.Harness.Rules;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Rules;
using MmoEngine.Harness.Tests.Support;
using Serilog.Core;

namespace MmoEngine.Harness.Tests.Bots;

/// <summary>
/// The bot host's frame-timing tests run alone: a 60 Hz frame loop that shares the CPU with the rest of the
/// suite misses deadlines it would keep on an idle runner (SC-006), so parallel execution is turned off for
/// this collection only (constitution, Principle VIII: an opt-out with a documented reason).
/// </summary>
[CollectionDefinition(nameof(FrameTimingCollection), DisableParallelization = true)]
public sealed class FrameTimingCollection;

/// <summary>US5: bots and a stub target prove the measurement path (spec 014, User Story 5).</summary>
[Collection(nameof(FrameTimingCollection))]
public sealed class BotHostTests(ClickHouseFixture clickHouse)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>US5 scenario 1: 4 bots for 2 seconds write 110 to 130 frames each, each frame numbered once and in order.</summary>
    [Fact]
    public async Task FourBotsForTwoSecondsWriteEachFrameOnceAndInOrder()
    {
        await using var target = new StubTarget(new IPEndPoint(IPAddress.Loopback, 0));
        var sink = new MemoryFrameSink();

        var result = await BotHost.RunAsync(target.LocalEndPoint,
            new BotHostOptions { RunId = RunFolders.UniqueId("bot"), Bots = 4, Duration = TimeSpan.FromSeconds(2) }, sink, Ct);

        Assert.Equal(4, result.FramesPerBot.Count);
        foreach (var bot in Enumerable.Range(1, 4).Select(i => (uint)i))
        {
            var frames = sink.Frames.Where(f => f.BotId == bot).Select(f => f.Frame).ToList();
            Assert.InRange(frames.Count, 110, 130);
            Assert.Equal(Enumerable.Range(0, frames.Count).Select(i => (ulong)i), frames);
        }
    }

    /// <summary>US5 scenario 2: bots record snapshot receipts and underruns per frame, and the stub echoes every input.</summary>
    [Fact]
    public async Task BotsRecordSnapshotsAndTheStubEchoesEveryInput()
    {
        await using var target = new StubTarget(new IPEndPoint(IPAddress.Loopback, 0));
        var sink = new MemoryFrameSink();

        await BotHost.RunAsync(target.LocalEndPoint,
            new BotHostOptions { RunId = RunFolders.UniqueId("bot"), Bots = 2, Duration = TimeSpan.FromSeconds(1) }, sink, Ct);
        await Task.Delay(100, Ct);

        var frames = sink.Frames;
        Assert.True(frames.Sum(f => f.SnapshotsReceived) > 0);
        Assert.Contains(frames, f => f.SnapshotsReceived > 0);
        Assert.True(target.InputsReceived > 0);
        Assert.Equal(target.InputsReceived, target.InputsEchoed);
        Assert.Equal(frames.Count, target.InputsReceived);

        // The stub acknowledges exactly what the bot predicted, so there is no correction or snap.
        Assert.All(frames, f => Assert.Equal(0, f.CorrectionM));
        Assert.DoesNotContain(frames, f => f.Snap);
    }

    /// <summary>
    /// US5 scenario 3: a host made to miss more deadlines than its allowance marks its rows invalid, and the
    /// committed host-health rule is inconclusive for the run.
    /// </summary>
    [Fact]
    public async Task HostThatMissesDeadlinesIsInvalidAndHostHealthIsInconclusive()
    {
        await using var target = new StubTarget(new IPEndPoint(IPAddress.Loopback, 0));
        using var store = clickHouse.CreateStore();
        var runId = RunFolders.UniqueId("bot");

        // Every 5th frame takes 40 ms, more than two frame periods, so it misses its deadline: 20% of frames.
        var result = await BotHost.RunAsync(target.LocalEndPoint, new BotHostOptions
        {
            RunId = runId,
            HostId = "overloaded",
            Bots = 2,
            Duration = TimeSpan.FromSeconds(1),
            FrameWork = frame => { if (frame % 5 == 4) { Thread.Sleep(40); } },
        }, new StoreFrameSink(store), Ct);
        await store.UpsertRunAsync(new RunRow { RunId = runId, Kind = "bot", Loaded = true, Started = DateTime.UtcNow }, Ct);
        var rules = RuleFileParser.LoadFolder(Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules"));
        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, rules, [], null, Ct);

        Assert.False(result.Valid);
        Assert.True(result.DeadlineMisses > 0.01 * result.Frames);
        Assert.Equal(0, Convert.ToInt64(await store.ScalarAsync("SELECT countIf(valid = 1) FROM bot_frames WHERE run_id = {run_id:String}", runId, Ct), CultureInfo.InvariantCulture));
        Assert.Equal(RuleOutcome.Inconclusive, report.Results.Single(r => r.RuleId == "bot.host_health").Outcome);
        Assert.Equal(HarnessExitCodes.Inconclusive, report.ExitCode);
    }

    /// <summary>The host-health rule passes a healthy host's rows.</summary>
    [Fact]
    public async Task HealthyHostPassesHostHealth()
    {
        using var store = clickHouse.CreateStore();
        var runId = RunFolders.UniqueId("bot");
        await new StoreFrameSink(store).WriteAsync(
            [.. Enumerable.Range(0, 10).Select(i => new BotFrame(runId, "healthy", 1, (ulong)i, false, 0, false, false, 0, 1))], Ct);
        await store.UpsertRunAsync(new RunRow { RunId = runId, Kind = "bot", Loaded = true, Started = DateTime.UtcNow }, Ct);
        var rules = RuleFileParser.LoadFolder(Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules"));

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, rules, [], null, Ct);

        Assert.Equal(RuleOutcome.Passed, report.Results.Single(r => r.RuleId == "bot.host_health").Outcome);
    }

    /// <summary>US5 scenario 4: batches of 5,000 rows, a flush once a second for a trickle, and a final flush.</summary>
    [Fact]
    public async Task WriterFlushesAtFiveThousandRowsAndFinallyAtCompletion()
    {
        var sink = new MemoryFrameSink();
        var writer = new BatchWriter(sink);

        for (var i = 0; i < 12_000; i++)
        {
            writer.Add(new BotFrame("r", "h", 1, (ulong)i, false, 0, false, false, 0, 0));
        }

        await writer.CompleteAsync();

        Assert.Equal([5000, 5000, 2000], sink.Batches.Select(b => b.Count));
        Assert.Equal(Enumerable.Range(0, 12_000).Select(i => (ulong)i), sink.Frames.Select(f => f.Frame));
    }

    [Fact]
    public async Task WriterFlushesATrickleOnceASecond()
    {
        var sink = new MemoryFrameSink();
        await using var writer = new BatchWriter(sink);

        for (var i = 0; i < 10; i++)
        {
            writer.Add(new BotFrame("r", "h", 1, (ulong)i, false, 0, false, false, 0, 0));
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (sink.Batches.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(50, Ct);
        }

        Assert.Single(sink.Batches);
        Assert.Equal(10, sink.Batches[0].Count);
        Assert.InRange(waited.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
    }

    /// <summary>The store sink writes valid rows that the rules read.</summary>
    [Fact]
    public async Task StoreSinkWritesBotFrames()
    {
        using var store = clickHouse.CreateStore();
        var runId = RunFolders.UniqueId("bot");

        await new StoreFrameSink(store).WriteAsync([new BotFrame(runId, "h", 3, 7, true, 0.25, false, true, 0, 2)], Ct);

        Assert.Equal("3,7,1,0.25,1,2,1", await store.ScalarAsync(
            "SELECT concat(toString(bot_id), ',', toString(frame), ',', toString(deadline_missed), ',', toString(correction_m), ',', " +
            "toString(underrun), ',', toString(snapshots_received), ',', toString(valid)) FROM bot_frames WHERE run_id = {run_id:String}", runId, Ct));
    }
}
