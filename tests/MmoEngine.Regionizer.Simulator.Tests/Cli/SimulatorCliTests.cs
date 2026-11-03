using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Cli;

/// <summary><c>regionizer-sim run</c> exit codes and argument validation (contracts/config-and-cli.md).</summary>
public sealed class SimulatorCliTests
{
    [Fact]
    public void CleanRunExitsZeroAndWritesTheRunFolder()
    {
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 30));
        var output = TestRuns.TempDir();

        var (code, stdout, stderr) = Invoke(["run", config, "--out", output]);

        Assert.True(code == ExitCodes.Clean, stderr);
        Assert.True(File.Exists(Path.Combine(output, RunFolder.SummaryFile)));
        Assert.False(File.Exists(Path.Combine(output, RunFolder.IncompleteMarker)));
        var line = JsonNode.Parse(stdout.Trim())!.AsObject();
        Assert.Equal(0, line["exitCode"]!.GetValue<int>());
        Assert.True(line["completed"]!.GetValue<bool>());
        Assert.Equal(30, line["ticks"]!.GetValue<long>());
    }

    [Fact]
    public void SeedAndTicksOptionsOverrideTheConfig()
    {
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 30));
        var output = TestRuns.TempDir();

        var (code, _, stderr) = Invoke(["run", config, "--out", output, "--seed", "99", "--ticks", "12"]);

        Assert.True(code == ExitCodes.Clean, stderr);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(output, RunFolder.ManifestFile)))!;
        Assert.Equal(99UL, manifest["seed"]!.GetValue<ulong>());
        Assert.Equal(12, TestRuns.CsvRows(Path.Combine(output, RunFolder.TicksFile)).Count);
    }

    /// <summary>
    /// US2.4: Given a configuration whose section size is not a power of two, When the simulator
    /// starts, Then it refuses to run, exits with a non-zero code and names the invalid field.
    /// </summary>
    [Fact]
    public void SectionSizeNotAPowerOfTwoExitsTwoAndNamesTheField()
    {
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 30).With("regionizer.sectionSize", 12));
        var output = TestRuns.TempDir();

        var (code, stdout, stderr) = Invoke(["run", config, "--out", output]);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Contains("regionizer.sectionSize", stderr, StringComparison.Ordinal);
        Assert.Contains("must be a power of two between 1 and 1024", stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void InjectedViolationExitsThreeWithRuleTickAndSeed()
    {
        const ulong Seed = 5;
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", Seed, 30));
        var output = TestRuns.TempDir();
        var options = new RunOptions { AfterStep = CorruptAtTick(4) };

        var (code, stdout, stderr) = Invoke(["run", config, "--out", output], options);

        Assert.Equal(ExitCodes.Violation, code);
        var violation = JsonNode.Parse(stdout.Trim())!["firstViolation"]!;
        Assert.Equal(1, violation["ruleNumber"]!.GetValue<int>());
        Assert.Equal(4, violation["tick"]!.GetValue<long>());
        Assert.Equal(Seed, violation["seed"]!.GetValue<ulong>());
        Assert.Contains("\"Rule\":1", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-out")]
    [InlineData("missing-config")]
    [InlineData("negative-seed")]
    [InlineData("text-seed")]
    [InlineData("zero-ticks")]
    [InlineData("absent-file")]
    [InlineData("unknown-option")]
    [InlineData("unknown-command")]
    public void InvalidArgumentsExitTwo(string variant)
    {
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 10));
        var output = TestRuns.TempDir();
        string[] args = variant switch
        {
            "missing-out" => ["run", config],
            "missing-config" => ["run", "--out", output],
            "negative-seed" => ["run", config, "--out", output, "--seed", "-1"],
            "text-seed" => ["run", config, "--out", output, "--seed", "abc"],
            "zero-ticks" => ["run", config, "--out", output, "--ticks", "0"],
            "absent-file" => ["run", Path.Combine(TestRuns.TempDir(), "none.json"), "--out", output],
            "unknown-option" => ["run", config, "--out", output, "--speed", "2"],
            _ => ["walk", config],
        };

        var (code, _, stderr) = Invoke(args);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.False(Directory.Exists(output));
    }

    /// <summary>An unexpected error during a run (here, a step hook that throws) exits 1 and logs it as fatal.</summary>
    [Fact]
    public void AnUnexpectedErrorDuringARunExitsOne()
    {
        var config = TestRuns.WriteConfig(new JsonObject
        {
            ["scenario"] = "Baseline",
            ["durationTicks"] = 5,
            ["population"] = new JsonObject { ["total"] = 50 },
        });
        var options = new RunOptions { AfterStep = _ => throw new InvalidOperationException("step hook failed on purpose") };

        var (code, stdout, stderr) = Invoke(["run", config, "--out", TestRuns.TempDir()], options);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("\"@l\":\"Fatal\"", stderr, StringComparison.Ordinal);
        Assert.Contains("step hook failed on purpose", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpExitsZero()
    {
        var (code, stdout, _) = Invoke(["run", "--help"]);

        Assert.Equal(ExitCodes.Clean, code);
        Assert.Contains("--out", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAreCompactJsonOnStderr()
    {
        var config = TestRuns.WriteConfig(TestRuns.Small("Baseline", 3, 10));

        var (_, _, stderr) = Invoke(["run", config, "--out", TestRuns.TempDir()]);

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("@t", out _));
            Assert.True(doc.RootElement.TryGetProperty("@mt", out _));
        });
    }

    [Fact]
    public void RunCommandIsRegistered()
    {
        Assert.Contains("run", Simulator.Cli.Commands.CliCommands.All().Select(c => c.Name));
    }

    private static Action<StepHookContext> CorruptAtTick(long tick) => context =>
    {
        if (context.Step != tick * 2)
        {
            return;
        }

        var regions = context.Regionizer.RegionIds.ToArray();
        var owner = regions.First(id => context.Regionizer.GetRegion(id).SectionCount > 0);
        var other = regions.First(id => id != owner);
        context.Regionizer.TestHookListSectionInRegion(other, context.Regionizer.GetRegion(owner).Sections[0]);
    };

    private static (int Code, string Stdout, string Stderr) Invoke(string[] args, RunOptions? options = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr, (options ?? new RunOptions()) with { GitSha = "unknown" });
        return (code, stdout.ToString(), stderr.ToString());
    }
}
