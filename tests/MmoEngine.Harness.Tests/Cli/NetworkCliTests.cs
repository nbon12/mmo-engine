using System.Net;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Bots;
using MmoEngine.Harness.Cli;
using MmoEngine.Harness.Network;
using MmoEngine.Harness.Tests.Bots;
using MmoEngine.Harness.Tests.Rules;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Cli;

/// <summary>The profile, probe, calibrate, stub-target and bots commands end to end (contracts/harness-cli.md).</summary>
[Collection(nameof(FrameTimingCollection))]
public sealed class NetworkCliTests(ClickHouseFixture clickHouse)
{
    private static string Profiles => Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "profiles");

    private HarnessCliOptions Options() => new()
    {
        GetEnvironmentVariable = name => name == HarnessSettings.ClickHouseVariable ? clickHouse.ConnectionString : null,
    };

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, Options());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ProfileListShowsEightProfiles()
    {
        var (exit, stdout, _) = Run("profile", "list", "--profiles", Profiles);

        Assert.Equal(0, exit);
        var names = JsonNode.Parse(stdout)!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(8, names.Count);
        Assert.Contains("Poor-reorder", names);
    }

    [Fact]
    public void ProfileNetemAndChaosPrintTheGeneratedSettings()
    {
        var netem = Run("profile", "netem", "--profiles", Profiles, "--profile", "Poor", "--device", "eth0", "--seed", "5");
        var chaos = Run("profile", "chaos", "--profiles", Profiles, "--profile", "typical-reorder", "--namespace", "ns", "--label", "app=probe");

        Assert.Equal(0, netem.ExitCode);
        Assert.Contains("netem delay 125ms 35.355ms distribution normal loss random 3% rate 10gbit seed 5", netem.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, chaos.ExitCode);
        Assert.Contains("      app: \"probe\"", chaos.Stdout, StringComparison.Ordinal);
        Assert.Contains("reorder:", chaos.Stdout, StringComparison.Ordinal);
    }

    /// <summary>FR-026: a calibration search applies per-direction settings other than the profile's.</summary>
    [Fact]
    public void ProfileNetemAppliesPerDirectionOverrides()
    {
        var netem = Run("profile", "netem", "--profiles", Profiles, "--profile", "Typical", "--device", "mmo-up", "--seed", "2",
            "--delay-ms", "39.5", "--jitter-ms", "8.25");

        Assert.Equal(0, netem.ExitCode);
        Assert.Contains("dev mmo-up root handle 1: netem delay 39.5ms 8.25ms distribution normal loss random 0.5% rate 10gbit seed 2", netem.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("profile", "netem", "--profile", "Nowhere")]
    [InlineData("profile", "netem", "--profile", "LAN", "--jitter-ms", "-1")]
    [InlineData("profile", "netem", "--profile", "LAN", "--loss-percent", "101")]
    [InlineData("profile", "chaos", "--profile", "Typical", "--label", "novalue")]
    [InlineData("probe", "run", "--target", "not-an-endpoint", "--profile", "LAN")]
    [InlineData("bots", "run", "--target", "127.0.0.1:9", "--bots", "0")]
    public void InvalidNetworkArgumentsExitTwo(params string[] args)
    {
        var (exit, _, _) = Run([.. args, .. args[0] == "bots" ? Array.Empty<string>() : ["--profiles", Profiles]]);

        Assert.Equal(HarnessExitCodes.Invalid, exit);
    }

    [Fact]
    public void InvalidProfileFolderExitsTwo()
    {
        var folder = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(folder, "bad.json"), "{");

        Assert.Equal(HarnessExitCodes.Invalid, Run("profile", "list", "--profiles", folder).ExitCode);
    }

    /// <summary>US4 end to end on loopback: probe run writes net_samples and calibrates LAN; calibrate re-reads the store.</summary>
    [Fact]
    public async Task ProbeRunThenCalibrateAgainstLan()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));
        var runId = RunFolders.UniqueId("probe");
        var profiles = RunFolders.TempFolder();
        RunFolders.CopyDirectory(Profiles, profiles);

        var probe = Run("probe", "run", "--target", server.LocalEndPoint.ToString(), "--profile", "LAN", "--seconds", "1", "--profiles", profiles, "--run-id", runId);
        var calibrate = Run("calibrate", "--run-id", runId, "--profile", "LAN", "--profiles", profiles, "--write-calibrated");

        var report = JsonNode.Parse(probe.Stdout)!;
        Assert.Equal(30, report["sent"]!.GetValue<int>());
        Assert.Equal(0, report["upLossPercent"]!.GetValue<double>());
        Assert.Contains(probe.ExitCode, new[] { HarnessExitCodes.Ok, HarnessExitCodes.OutsideTolerance });
        Assert.Equal(probe.ExitCode, calibrate.ExitCode);
        Assert.Equal(report["medianRoundTripMs"]!.GetValue<double>(), JsonNode.Parse(calibrate.Stdout)!["medianRoundTripMs"]!.GetValue<double>(), 9);
        var written = NetworkProfile.Parse(File.ReadAllText(Path.Combine(profiles, "lan.json")), "lan.json");
        Assert.Equal(calibrate.ExitCode == 0, written.Calibrated is not null);
    }

    [Fact]
    public void CalibrateWithoutSamplesExitsTwo()
    {
        var (exit, _, stderr) = Run("calibrate", "--run-id", RunFolders.UniqueId("none"), "--profile", "LAN", "--profiles", Profiles);

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains("no net_samples rows", stderr, StringComparison.Ordinal);
    }

    /// <summary>US5 end to end: a stub target, the bots command writing bot_frames, then rules run on the bot run.</summary>
    [Fact]
    public async Task BotsRunWritesFramesAndARunRow()
    {
        await using var target = new StubTarget(new IPEndPoint(IPAddress.Loopback, 0));
        var runId = RunFolders.UniqueId("bot");

        var bots = Run("bots", "run", "--target", target.LocalEndPoint.ToString(), "--bots", "2", "--seconds", "1", "--run-id", runId);
        var rules = Run("rules", "run", "--run-id", runId, "--rules", Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules"));

        Assert.Equal(0, bots.ExitCode);
        var report = JsonNode.Parse(bots.Stdout)!;
        Assert.True(report["frames"]!.GetValue<long>() > 50);
        var results = JsonNode.Parse(rules.Stdout)!["results"]!.AsArray();
        Assert.Contains(results, r => r!["ruleId"]!.GetValue<string>() == "bot.host_health");

        // bot.correction_p99 needs 100,000 samples, so a 1-second run is inconclusive by design.
        Assert.Equal("inconclusive", results.Single(r => r!["ruleId"]!.GetValue<string>() == "bot.correction_p99")!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task ServeCommandsListenAndStop()
    {
        var probe = Task.Run(() => Run("probe", "serve", "--bind", "127.0.0.1:0", "--seconds", "0.2"), TestContext.Current.CancellationToken);
        var stub = Task.Run(() => Run("stub-target", "--bind", "127.0.0.1:0", "--seconds", "0.2"), TestContext.Current.CancellationToken);

        var (probeExit, probeOut, _) = await probe;
        var (stubExit, stubOut, _) = await stub;

        Assert.Equal(0, probeExit);
        Assert.StartsWith("127.0.0.1:", JsonNode.Parse(probeOut)!["listening"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, stubExit);
        Assert.StartsWith("127.0.0.1:", JsonNode.Parse(stubOut)!["listening"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost:7400", true)]
    [InlineData("127.0.0.1:7400", true)]
    [InlineData("[::1]:7400", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("host:notaport", false)]
    [InlineData("no-such-host.invalid:7400", false)]
    public void EndPointsParse(string text, bool valid)
    {
        using var stderr = new StringWriter();
        var context = new CliContext(TextWriter.Null, Serilog.Core.Logger.None, Options());

        Assert.Equal(valid, NetworkCommands.TryEndPoint(context, text, out _));
    }

    [Theory]
    [InlineData("profile", "list")]
    [InlineData("probe", "serve", "--bind", "nowhere")]
    [InlineData("stub-target", "--bind", "nowhere")]
    [InlineData("bots", "run", "--target", "nowhere")]
    public void DefaultProfilesFolderOrBadEndpointsExitTwo(params string[] args) =>
        Assert.Equal(HarnessExitCodes.Invalid, Run(args).ExitCode);

    [Fact]
    public void BadProfilesFolderOrUnknownProfileExitTwo()
    {
        var folder = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(folder, "bad.json"), "{");

        Assert.Equal(HarnessExitCodes.Invalid, Run("profile", "netem", "--profiles", folder, "--profile", "LAN").ExitCode);
        Assert.Equal(HarnessExitCodes.Invalid, Run("calibrate", "--run-id", "x", "--profiles", Profiles, "--profile", "Nowhere").ExitCode);
    }

    [Fact]
    public void ChaosUsesTheProbeLabelByDefault()
    {
        var (exit, stdout, _) = Run("profile", "chaos", "--profiles", Profiles, "--profile", "LAN");

        Assert.Equal(0, exit);
        Assert.Contains("app.kubernetes.io/part-of: \"harness-probe\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableStoreExitsOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var options = new HarnessCliOptions
        {
            GetEnvironmentVariable = name => name == HarnessSettings.ClickHouseVariable ? "Host=127.0.0.1;Port=1;Username=x;Timeout=5" : null,
        };

        var exit = Program.Run(["calibrate", "--run-id", "x", "--profiles", Profiles, "--profile", "LAN"], stdout, stderr, options);

        Assert.Equal(HarnessExitCodes.Error, exit);
    }

    /// <summary>FR-026: a profile that passes calibration gets its calibrated settings, runner class and kernel stored in its file.</summary>
    [Fact]
    public async Task PassingCalibrationWritesCalibratedSettingsToTheProfileFile()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));
        var profiles = RunFolders.TempFolder();
        // The file name deliberately differs from the profile name: the catalog records where each profile came from.
        File.WriteAllText(Path.Combine(profiles, "loopback_profile.json"),
            "{\"name\":\"Loop\",\"version\":1,\"roundTripMs\":0.5,\"jitterMs\":0,\"lossPercent\":0,\"tolerances\":{\"absoluteMs\":20,\"lossPoints\":0.25}}");
        var runId = RunFolders.UniqueId("probe");

        var probe = Run("probe", "run", "--target", server.LocalEndPoint.ToString(), "--profile", "Loop", "--seconds", "1", "--profiles", profiles, "--run-id", runId);
        var calibrate = Run("calibrate", "--run-id", runId, "--profile", "Loop", "--profiles", profiles, "--write-calibrated");

        Assert.Equal(0, probe.ExitCode);
        Assert.Equal(0, calibrate.ExitCode);
        var written = NetworkProfile.Parse(File.ReadAllText(Path.Combine(profiles, "loopback_profile.json")), "loopback_profile.json");
        Assert.NotNull(written.Calibrated);
        Assert.Equal(runId, written.Calibrated.SourceRunId);
        Assert.Equal(RunnerClass.Current().Id, written.Calibrated.RunnerClass);
        Assert.False(string.IsNullOrEmpty(written.Calibrated.Kernel));

        // The settings a calibration search applied are the ones stored.
        var tried = Run("calibrate", "--run-id", runId, "--profile", "Loop", "--profiles", profiles, "--write-calibrated",
            "--delay-ms", "0.125", "--jitter-ms", "0", "--loss-percent", "0");
        Assert.Equal(0, tried.ExitCode);
        var stored = NetworkProfile.Parse(File.ReadAllText(Path.Combine(profiles, "loopback_profile.json")), "loopback_profile.json");
        Assert.Equal(new DirectionSettings(0.125, 0, 0), stored.Calibrated!.PerDirection);
        Assert.Equal(0.5, stored.RoundTripMs);

        // A reordering variant is derived from its base profile, so it has no file to store settings in.
        var variant = Run("calibrate", "--run-id", runId, "--profile", "Loop-reorder", "--profiles", profiles, "--write-calibrated");
        Assert.Equal(0, variant.ExitCode);
        Assert.Contains("derived reordering variant", variant.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(profiles, "loop-reorder.json")));
    }
}
