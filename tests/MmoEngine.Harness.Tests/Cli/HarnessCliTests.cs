using System.Text.Json.Nodes;
using MmoEngine.Harness.Cli;
using MmoEngine.Harness.Golden;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Golden;
using MmoEngine.Harness.Tests.Rules;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Cli;

/// <summary>The <c>harness</c> CLI end to end: exit codes and stdout, against a real ClickHouse (contracts/harness-cli.md).</summary>
public sealed class HarnessCliTests(ClickHouseFixture clickHouse)
{
    private HarnessCliOptions Options(string? environment = null, string? connection = "use-fixture") => new()
    {
        GetEnvironmentVariable = name => name switch
        {
            HarnessSettings.EnvironmentVariable => environment,
            HarnessSettings.ClickHouseVariable => connection == "use-fixture" ? clickHouse.ConnectionString : connection,
            _ => null,
        },
    };

    private (int ExitCode, string Stdout, string Stderr) Run(HarnessCliOptions options, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, options);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void SchemaApplyListsEveryTable()
    {
        var (exit, stdout, _) = Run(Options(), "schema", "apply");

        Assert.Equal(0, exit);
        var tables = JsonNode.Parse(stdout)!["tables"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
        Assert.Equal(SchemaScripts.Tables(), tables);
        Assert.Contains("rule_results", tables);
    }

    /// <summary>US1 + US2 through the CLI: load, then rules run exits 5 on a seeded violation and keeps artifacts.</summary>
    [Fact]
    public void LoadThenRulesRunFailsCiOnAViolationAndKeepsArtifacts()
    {
        var (folder, runId) = RunFolders.CleanRun();
        RunFolders.SeedViolation(folder);
        var artifacts = RunFolders.TempFolder();
        var rules = Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules");

        var load = Run(Options(), "load", folder);
        var again = Run(Options(), "load", folder);
        var rulesRun = Run(Options(), "rules", "run", "--run-id", runId, "--rules", rules, "--artifacts", artifacts);

        Assert.Equal(0, load.ExitCode);
        Assert.False(JsonNode.Parse(load.Stdout)!["alreadyLoaded"]!.GetValue<bool>());
        Assert.True(JsonNode.Parse(again.Stdout)!["alreadyLoaded"]!.GetValue<bool>());
        Assert.Equal(HarnessExitCodes.RuleFailed, rulesRun.ExitCode);
        var report = JsonNode.Parse(rulesRun.Stdout)!;
        Assert.Equal("failed", report["verdict"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(report["artifacts"]!.GetValue<string>(), "inputs.json")));
    }

    [Fact]
    public void RulesRunPassesACleanRun()
    {
        var (folder, runId) = RunFolders.CleanRun();
        var rules = Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules");
        Run(Options(), "load", folder);

        var (exit, stdout, _) = Run(Options(), "rules", "run", "--run-id", runId, "--rules", rules);

        Assert.Equal(0, exit);
        Assert.Equal("passed", JsonNode.Parse(stdout)!["verdict"]!.GetValue<string>());
    }

    [Fact]
    public void LoadingASweepFolderLoadsItsCellsAndRuns()
    {
        var (folder, sweep) = RunFolders.Sweep();

        var (exit, stdout, _) = Run(Options(), "load", folder);

        Assert.Equal(0, exit);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal(sweep, json["sweep"]!.GetValue<string>());
        Assert.Equal(2, json["cellRows"]!.GetValue<int>());
        Assert.Equal(2, json["runsLoaded"]!.GetValue<int>());
    }

    /// <summary>US1 scenario 3 through the CLI: an incomplete folder exits 2.</summary>
    [Fact]
    public void LoadRefusesAnIncompleteFolderWithExitTwo()
    {
        var (folder, _) = RunFolders.CleanRun();
        File.WriteAllText(Path.Combine(folder, "_incomplete"), "");

        var (exit, _, stderr) = Run(Options(), "load", folder);

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains("incomplete", stderr, StringComparison.Ordinal);
    }

    /// <summary>US2 scenario 7 through the CLI: an invalid rule file exits 2 and names the file and field.</summary>
    [Fact]
    public void InvalidRuleFileExitsTwoNamingFileAndField()
    {
        var rules = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(rules, "bad.yaml"), "id: bad\napplies_to: [simulator]\nquery: SELECT 1, 1\nmode: fixed\nthreshold: 0\n");

        var (exit, _, stderr) = Run(Options(), "rules", "run", "--run-id", "anything", "--rules", rules);

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains("bad.yaml", stderr, StringComparison.Ordinal);
        Assert.Contains("query", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesRunOnARunThatIsNotLoadedExitsTwo()
    {
        var rules = Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules");

        var (exit, _, stderr) = Run(Options(), "rules", "run", "--run-id", RunFolders.UniqueId("missing"), "--rules", rules);

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains("not loaded", stderr, StringComparison.Ordinal);
    }

    /// <summary>FR-034: invalid settings stop the tool with exit 2, naming the setting.</summary>
    [Theory]
    [InlineData("Prod", "use-fixture", "DOTNET_ENVIRONMENT")]
    [InlineData(null, null, "MMO_CLICKHOUSE")]
    [InlineData(null, "not a connection string", "MMO_CLICKHOUSE")]
    public void InvalidSettingsExitTwoNamingTheSetting(string? environment, string? connection, string named)
    {
        var (exit, _, stderr) = Run(Options(environment, connection), "schema", "apply");

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains(named, stderr, StringComparison.Ordinal);
    }

    /// <summary>Edge case: an unreachable store exits 1, never a pass.</summary>
    [Fact]
    public void UnreachableStoreExitsOne()
    {
        var (exit, _, stderr) = Run(Options(connection: "Host=127.0.0.1;Port=1;Username=nobody;Timeout=5"), "schema", "apply");

        Assert.Equal(HarnessExitCodes.Error, exit);
        Assert.Contains("Store error", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownArgumentsExitTwo()
    {
        var (exit, _, _) = Run(Options(), "load");

        Assert.Equal(HarnessExitCodes.Invalid, exit);
    }

    /// <summary>US3 through the CLI: record, then check a matching replay (0) and a diverged one (7).</summary>
    [Fact]
    public void GoldenRecordThenCheckReportsMatchAndDivergence()
    {
        var config = new JsonObject
        {
            ["scenario"] = "Baseline",
            ["seed"] = 3,
            ["durationTicks"] = 20,
            ["population"] = new JsonObject { ["total"] = 200, ["limit"] = 10000 },
        };
        var root = RunFolders.TempFolder();
        var configPath = Path.Combine(root, "config.json");
        File.WriteAllText(configPath, config.ToJsonString());
        var run = GoldenRunTests.Simulate(config);
        var golden = Path.Combine(root, "golden");

        var record = Run(Options(), "golden", "record", "--run", run, "--config", configPath, "--out", golden, "--name", "cli");
        var match = Run(Options(), "golden", "check", "--golden", golden, "--run", GoldenRunTests.Simulate(config));
        var diverged = GoldenRunTests.Simulate(config);
        RunFolders.CorruptHashesFrom(diverged, 5);
        var mismatch = Run(Options(), "golden", "check", "--golden", golden, "--run", diverged);

        Assert.Equal(0, record.ExitCode);
        Assert.Equal("cli", JsonNode.Parse(record.Stdout)!["name"]!.GetValue<string>());
        Assert.Equal(0, match.ExitCode);
        Assert.Equal(HarnessExitCodes.Diverged, mismatch.ExitCode);
        Assert.Equal(5, JsonNode.Parse(mismatch.Stdout)!["firstDivergentTick"]!.GetValue<long>());
        Assert.Equal(nameof(GoldenOutcome.Diverged), JsonNode.Parse(mismatch.Stdout)!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void GoldenCheckOnAMissingGoldenFolderExitsTwo()
    {
        var (exit, _, stderr) = Run(Options(), "golden", "check", "--golden", RunFolders.TempFolder(), "--run", RunFolders.TempFolder());

        Assert.Equal(HarnessExitCodes.Invalid, exit);
        Assert.Contains("golden.json", stderr, StringComparison.Ordinal);
    }

    /// <summary>FR-014: the baselines table mirrors the committed files; an invalid file exits 2.</summary>
    [Fact]
    public async Task BaselinesSyncCopiesCommittedFilesIntoTheTable()
    {
        var folder = RunFolders.TempFolder();
        var rule = RunFolders.UniqueId("rule");
        File.WriteAllText(Path.Combine(folder, "b.yaml"),
            $"- rule_id: {rule}\n  profile: Typical\n  value: 0.5\n  source_run_id: run-9\n  set_at: 2026-11-04T00:00:00Z\n");

        var (exit, stdout, _) = Run(Options(), "rules", "baselines", "sync", "--baselines", folder);

        Assert.Equal(0, exit);
        Assert.Equal(1, JsonNode.Parse(stdout)!["baselines"]!.GetValue<long>());
        using var store = clickHouse.CreateStore();
        var value = await store.ScalarAsync("SELECT value FROM baselines FINAL WHERE rule_id = {run_id:String}", rule, TestContext.Current.CancellationToken);
        Assert.Equal(0.5, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));

        File.WriteAllText(Path.Combine(folder, "bad.yaml"), "- rule_id: x\n");
        Assert.Equal(HarnessExitCodes.Invalid, Run(Options(), "rules", "baselines", "sync", "--baselines", folder).ExitCode);
    }
}
