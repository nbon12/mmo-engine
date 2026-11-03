using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using MmoEngine.Regionizer.Simulator.Trace;

namespace MmoEngine.Regionizer.Simulator.Tests.Trace;

/// <summary><c>regionizer-sim replay</c>: exit codes, the recorded seed, and recording a replay.</summary>
public sealed class ReplayCommandTests
{
    [Fact]
    public void ReplayReproducesTheRecordedRunWithTheTracesSeed()
    {
        const ulong Seed = 13;
        const int Ticks = 40;
        var loaded = TestRuns.Load(TraceReplayTests.Config("Interleaved", Seed, Ticks));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        var recorded = TraceReplayTests.Record(loaded, trace);
        var updates = TraceReader.Validate(trace).Updates;

        // The config names another seed and records the replay; the trace's seed wins.
        var again = Path.Combine(TestRuns.TempDir(), "again.csv");
        var config = TestRuns.WriteConfig(TraceReplayTests.Config("Interleaved", 999, Ticks).With("trace.record", again));
        var output = TestRuns.TempDir();
        var (code, stdout, stderr) = Invoke(["replay", trace, "--config", config, "--out", output]);

        Assert.True(code == ExitCodes.Clean, stderr);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.Equal(updates, line["updatesServed"]!.GetValue<long>());
        Assert.Equal(0, line["updatesCollapsed"]!.GetValue<long>());
        Assert.Equal(trace, line["trace"]!.GetValue<string>());
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, RunFolder.ManifestFile)))!;
        Assert.Equal(Seed, manifest["seed"]!.GetValue<ulong>());
        Assert.Equal(trace, manifest["parameters"]!["trace"]!["replay"]!.GetValue<string>());
        var hashes = TestRuns.CsvColumn(Path.Combine(output, RunFolder.TicksFile), "stateHash");
        Assert.True(
            recorded.TickHashes.Select(RunFolder.FormatHash).SequenceEqual(hashes),
            $"seed {Seed}: the replayed ticks.csv hashes differ from the recorded run's");
        Assert.Equal(File.ReadAllLines(trace).Skip(1), File.ReadAllLines(again).Skip(1));
        Assert.DoesNotContain("\"@l\":\"Warning\"", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplayInAnotherSchedulingModeWarnsAndRuns()
    {
        var loaded = TestRuns.Load(TraceReplayTests.Config("Lockstep", 4, 20));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        TraceReplayTests.Record(loaded, trace);
        var config = TestRuns.WriteConfig(TraceReplayTests.Config("Interleaved", 4, 20));

        var (code, _, stderr) = Invoke(["replay", trace, "--config", config, "--out", TestRuns.TempDir()]);

        Assert.True(code == ExitCodes.Clean, stderr);
        Assert.Contains("\"@l\":\"Warning\"", stderr, StringComparison.Ordinal);
        Assert.Contains("hashes will not match the recorded run", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-trace", "does not exist")]
    [InlineData("malformed-trace", "line 3")]
    [InlineData("invalid-config", "regionizer.sectionSize")]
    [InlineData("other-entities", "the trace places")]
    [InlineData("record-over-replay", "trace.record")]
    public void InvalidInputExitsTwoAndWritesNothing(string variant, string message)
    {
        var loaded = TestRuns.Load(TraceReplayTests.Config("Lockstep", 4, 10));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        TraceReplayTests.Record(loaded, trace);
        var runConfig = TraceReplayTests.Config("Lockstep", 4, 10);
        switch (variant)
        {
            case "missing-trace":
                trace = Path.Combine(TestRuns.TempDir(), "none.csv");
                break;
            case "malformed-trace":
                File.WriteAllLines(trace, [.. File.ReadAllLines(trace).Take(2), "9,0,fly,,,,,,"]);
                break;
            case "invalid-config":
                runConfig.With("regionizer.sectionSize", 12);
                break;
            case "other-entities":
                runConfig.With("population.total", 120);
                break;
            default:
                runConfig.With("trace.record", trace);
                break;
        }

        var output = TestRuns.TempDir();
        var (code, stdout, stderr) = Invoke(["replay", trace, "--config", TestRuns.WriteConfig(runConfig), "--out", output]);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(output), $"{variant}: the replay wrote output");
    }

    [Fact]
    public void AnOutputPathThatIsAFileExitsOne()
    {
        var loaded = TestRuns.Load(TraceReplayTests.Config("Lockstep", 4, 5));
        var trace = Path.Combine(TestRuns.TempDir(), "trace.csv");
        TraceReplayTests.Record(loaded, trace);
        var file = Path.Combine(Path.GetDirectoryName(trace)!, "not-a-directory");
        File.WriteAllText(file, "x");

        var (code, stdout, stderr) = Invoke(["replay", trace, "--config", TestRuns.WriteConfig(TraceReplayTests.Config("Lockstep", 4, 5)), "--out", file]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("\"@l\":\"Fatal\"", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-config")]
    [InlineData("missing-out")]
    [InlineData("missing-trace")]
    public void MissingArgumentsExitTwo(string variant)
    {
        var config = TestRuns.WriteConfig(TraceReplayTests.Config("Lockstep", 4, 5));
        var output = TestRuns.TempDir();
        string[] args = variant switch
        {
            "missing-config" => ["replay", "trace.csv", "--out", output],
            "missing-out" => ["replay", "trace.csv", "--config", config],
            _ => ["replay", "--config", config, "--out", output],
        };

        var (code, _, stderr) = Invoke(args);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ReplayCommandIsRegisteredWithHelp()
    {
        Assert.Contains("replay", Simulator.Cli.Commands.CliCommands.All().Select(c => c.Name));

        var (code, stdout, _) = Invoke(["replay", "--help"]);

        Assert.Equal(ExitCodes.Clean, code);
        Assert.Contains("--config", stdout, StringComparison.Ordinal);
    }

    private static (int Code, string Stdout, string Stderr) Invoke(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr, new RunOptions { GitSha = "unknown" });
        return (code, stdout.ToString(), stderr.ToString());
    }
}
