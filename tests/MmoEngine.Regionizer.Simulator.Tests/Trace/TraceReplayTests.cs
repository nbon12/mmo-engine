using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using MmoEngine.Regionizer.Simulator.Trace;

namespace MmoEngine.Regionizer.Simulator.Tests.Trace;

/// <summary>Trace recording and replay (US4, FR-022, contracts/trace-format.md).</summary>
public sealed class TraceReplayTests
{
    /// <summary>
    /// US4.4: Given a position trace recorded from a run, When the simulator replays it, Then the
    /// replayed run's per-tick state hashes match the recorded run's. Both scheduling modes; the
    /// interleaved run drops to 20 Hz often and the infantry redeploy often, so teleports are replayed too.
    /// </summary>
    [Theory]
    [InlineData("Lockstep", 7UL)]
    [InlineData("Interleaved", 11UL)]
    public void ReplayedTraceReproducesRecordedHashes(string scheduling, ulong seed)
    {
        const int Ticks = 60;
        var loaded = TestRuns.Load(Config(scheduling, seed, Ticks));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");

        var recorded = Record(loaded, trace);
        var summary = TraceReader.Validate(trace);
        ReplayMovementSource? source = null;
        var replayed = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true, MovementSourceFactory = ReplayMovementSource.Factory(trace, s => source = s) });
        using var replay = source!;

        Assert.True(recorded.Completed, $"seed {seed}: the recorded run stopped: {recorded.FirstViolation}");
        Assert.True(replayed.Completed, $"seed {seed}: the replayed run stopped: {replayed.FirstViolation}");
        Assert.Equal(Ticks, recorded.TickHashes.Count);
        Assert.True(
            recorded.TickHashes.SequenceEqual(replayed.TickHashes),
            $"seed {seed} ({scheduling}): hashes differ from tick {FirstDifference(recorded.TickHashes, replayed.TickHashes)}");
        Assert.Equal(recorded.FinalHash, replayed.FinalHash);

        // The trace, not the synthetic models, moved the replayed run: every recorded update was served once, on time.
        Assert.True(summary.Updates > 0, $"seed {seed}: the trace recorded no movement");
        Assert.Equal(summary.Updates, replay.UpdatesServed);
        Assert.Equal(0, replay.UpdatesCollapsed);
        Assert.True(CountOps(trace, "teleportBegin") > 0, $"seed {seed}: no teleport was recorded");
        Assert.Equal(loaded.Config.Seed, summary.Header.Seed);
        Assert.Equal(loaded.ConfigHash, summary.Header.ConfigHash);
        Assert.Equal(loaded.Config.Scheduling.Mode, summary.Header.Scheduling);
        if (scheduling == "Interleaved")
        {
            Assert.True(recorded.Scheduling.TwentyHzTicks > 0, $"seed {seed}: no region dropped to 20 Hz");
        }
    }

    /// <summary>The replay follows the trace: one shifted position changes the hashes from that tick on.</summary>
    [Fact]
    public void AChangedTraceChangesTheReplayedHashes()
    {
        const ulong Seed = 7;
        var loaded = TestRuns.Load(Config("Lockstep", Seed, 40));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        var recorded = Record(loaded, trace);
        var lines = File.ReadAllLines(trace);
        var target = Array.FindIndex(lines, l => l.StartsWith("20,", StringComparison.Ordinal) && l.Contains(",move,", StringComparison.Ordinal));
        Assert.True(target > 0, $"seed {Seed}: no move at step 20");
        var fields = lines[target].Split(',');
        var x = double.Parse(fields[5], CultureInfo.InvariantCulture);
        fields[5] = (x > 1000 ? x - 200 : x + 200).ToString("R", CultureInfo.InvariantCulture);
        lines[target] = string.Join(',', fields);
        var changed = Path.Combine(TestRuns.TempDir(), "changed.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        File.WriteAllLines(changed, lines);

        ReplayMovementSource? source = null;
        var replayed = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true, MovementSourceFactory = ReplayMovementSource.Factory(changed, s => source = s) });
        source!.Dispose();

        Assert.True(recorded.TickHashes.Take(10).SequenceEqual(replayed.TickHashes.Take(10)), $"seed {Seed}: ticks before the change should match");
        Assert.NotEqual(recorded.TickHashes[10], replayed.TickHashes[10]);
    }

    /// <summary>A replayed run records the same trace it replays: rows round-trip exactly.</summary>
    [Fact]
    public void RecordingAReplayGivesTheSameRows()
    {
        const ulong Seed = 7;
        var loaded = TestRuns.Load(Config("Lockstep", Seed, 30));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        Record(loaded, trace);
        var again = Path.Combine(TestRuns.TempDir(), "again.csv");

        ReplayMovementSource? source = null;
        using (var recorder = TraceRecorder.For(loaded, again))
        {
            TestRuns.Run(loaded, new RunOptions { Listeners = [recorder], MovementSourceFactory = ReplayMovementSource.Factory(trace, s => source = s) });
        }

        source!.Dispose();
        Assert.Equal(File.ReadAllLines(trace), File.ReadAllLines(again));
    }

    /// <summary>The <c>trace</c> config: <c>trace.record</c> records the run, <c>trace.replay</c> replays a trace.</summary>
    [Fact]
    public void TraceConfigRecordsAndReplaysThroughRunOptions()
    {
        const ulong Seed = 3;
        var trace = Path.Combine(TestRuns.TempDir(), "nested", "trace.csv");
        var recordConfig = TestRuns.Load(Config("Lockstep", Seed, 30).With("trace.record", trace));
        RunResult recorded;
        using (var attachment = TraceRunOptions.Attach(recordConfig, new RunOptions { KeepTickHashes = true }))
        {
            Assert.NotNull(attachment.Recorder);
            Assert.Null(attachment.ReplayPath);
            recorded = TestRuns.Run(recordConfig, attachment.Options);
            Assert.True(attachment.Recorder.Rows > 0);
        }

        var replayConfig = TestRuns.Load(Config("Lockstep", Seed, 30).With("trace.replay", trace));
        RunResult replayed;
        using (var attachment = TraceRunOptions.Attach(replayConfig, new RunOptions { KeepTickHashes = true }))
        {
            Assert.Null(attachment.Recorder);
            Assert.Equal(trace, attachment.ReplayPath);
            replayed = TestRuns.Run(replayConfig, attachment.Options);
            Assert.True(attachment.Replay!.UpdatesServed > 0);
        }

        Assert.True(recorded.TickHashes.SequenceEqual(replayed.TickHashes), $"seed {Seed}: hashes differ from tick {FirstDifference(recorded.TickHashes, replayed.TickHashes)}");
    }

    /// <summary>The trace file opens with its first row, after the run folder is cleared, so it may live inside a rerun's folder.</summary>
    [Fact]
    public void ATraceInsideTheRunFolderSurvivesARerun()
    {
        var output = TestRuns.TempDir();
        var trace = Path.Combine(output, "trace.csv");
        var loaded = TestRuns.Load(Config("Lockstep", 2, 10).With("trace.record", trace));

        for (var run = 0; run < 2; run++)
        {
            using var attachment = TraceRunOptions.Attach(loaded, new RunOptions { GitSha = "unknown" });
            SimulationRunner.Run(loaded, output, attachment.Options);
        }

        var summary = TraceReader.Validate(trace);
        Assert.Equal(loaded.Config.Seed, summary.Header.Seed);
        Assert.True(summary.Entities > 0 && summary.Updates > 0, $"seed {loaded.Config.Seed}: the trace lost its rows");
        Assert.True(File.Exists(Path.Combine(output, "summary.json")));
    }

    [Fact]
    public void WithoutTraceSettingsTheOptionsAreUnchanged()
    {
        var options = new RunOptions { KeepTickHashes = true };

        using var attachment = TraceRunOptions.Attach(TestRuns.Load(Config("Lockstep", 1, 5)), options);

        Assert.Same(options, attachment.Options);
        Assert.Null(attachment.Recorder);
        Assert.Null(attachment.Replay);
    }

    public static TheoryData<string, string, string> InvalidTraceSettings => new()
    {
        { "replay-missing", TraceRunOptions.ReplayPath, "does not exist" },
        { "replay-invalid", TraceRunOptions.ReplayPath, "is not a valid trace: line 1" },
        { "record-directory", TraceRunOptions.RecordPath, "is a directory" },
        { "record-is-replay", TraceRunOptions.RecordPath, "must name a different file than trace.replay" },
    };

    [Theory]
    [MemberData(nameof(InvalidTraceSettings))]
    public void InvalidTraceSettingsAreRefusedNamingTheField(string variant, string path, string message)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var valid = Path.Combine(dir, "valid.csv");
        Record(TestRuns.Load(Config("Lockstep", 1, 3)), valid);
        var invalid = Path.Combine(dir, "invalid.csv");
        File.WriteAllText(invalid, "not a trace\n");
        var config = Config("Lockstep", 1, 3);
        switch (variant)
        {
            case "replay-missing":
                config.With("trace.replay", Path.Combine(dir, "none.csv"));
                break;
            case "replay-invalid":
                config.With("trace.replay", invalid);
                break;
            case "record-directory":
                config.With("trace.record", dir);
                break;
            default:
                config.With("trace.replay", valid).With("trace.record", valid);
                break;
        }

        var ex = Assert.Throws<ConfigValidationException>(() => TraceRunOptions.Attach(TestRuns.Load(config), new RunOptions()));

        Assert.Contains(ex.Errors, e => e.Path == path && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void ATraceForOtherEntitiesCannotBeReplayed()
    {
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        Record(TestRuns.Load(Config("Lockstep", 5, 5)), trace);
        var otherSeed = TestRuns.Load(Config("Lockstep", 6, 5));

        var ex = Assert.Throws<TraceMismatchException>(() => TestRuns.Run(otherSeed, new RunOptions { MovementSourceFactory = ReplayMovementSource.Factory(trace) }));

        Assert.Matches("^entity [0-9]+: the trace places ", ex.Message);
    }

    /// <summary>A small Baseline with frequent redeploys (teleports) and, in interleaved mode, frequent 20 Hz drops.</summary>
    internal static JsonObject Config(string scheduling, ulong seed, long ticks) =>
        TestRuns.Small("Baseline", seed, ticks)
            .With("scheduling.mode", scheduling)
            .With("scheduling.dropTo20HzProbability", 0.2)
            .With("movement.redeployProbabilityPerSecond", 0.2);

    /// <summary>Runs <paramref name="loaded"/> recording its trace to <paramref name="trace"/>, keeping every tick's hash.</summary>
    internal static RunResult Record(LoadedRunConfig loaded, string trace)
    {
        using var recorder = TraceRecorder.For(loaded, trace);
        return TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true, Listeners = [recorder] });
    }

    private static int CountOps(string trace, string op) =>
        File.ReadLines(trace).Skip(2).Count(l => l.Split(',')[2] == op);

    private static int FirstDifference(IReadOnlyList<ulong> a, IReadOnlyList<ulong> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return a.Count == b.Count ? -1 : Math.Min(a.Count, b.Count);
    }
}
