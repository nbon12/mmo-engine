using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Rules;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Support;
using Serilog.Core;

namespace MmoEngine.Harness.Tests.Rules;

/// <summary>US2: a SQL rule with a threshold decides pass or fail (spec 014, User Story 2), against a real ClickHouse.</summary>
public sealed class RuleRunnerTests(ClickHouseFixture clickHouse)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RuleDefinition ViolationsRule => RuleFileParser.Parse(
        File.ReadAllText(Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules", "sim.violations.yaml")), "sim.violations.yaml");

    /// <summary>US2 scenario 1: one violation fails sim.violations with value 1, threshold 0, and exit code 5.</summary>
    [Fact]
    public async Task ZeroViolationsRuleFailsARunWithOneViolation()
    {
        var (folder, runId) = RunFolders.CleanRun();
        RunFolders.SeedViolation(folder);
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [ViolationsRule], [], null, Ct);

        Assert.Equal(HarnessExitCodes.RuleFailed, report.ExitCode);
        Assert.Equal("failed", report.Verdict);
        var row = await ResultRow(store, runId, "sim.violations");
        Assert.Equal(1d, row.Value);
        Assert.Equal(0d, row.Threshold);
        Assert.Equal("failed", row.Outcome);
        Assert.Equal("failed", (await store.GetRunAsync(runId, Ct))!.Verdict);
    }

    /// <summary>US2 scenario 2: the same rule passes a clean run, with exit code 0.</summary>
    [Fact]
    public async Task ZeroViolationsRulePassesACleanRun()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [ViolationsRule], [], null, Ct);

        Assert.Equal(HarnessExitCodes.Ok, report.ExitCode);
        var row = await ResultRow(store, runId, "sim.violations");
        Assert.Equal("passed", row.Outcome);
        Assert.Equal(0d, row.Value);
        Assert.Equal("passed", (await store.GetRunAsync(runId, Ct))!.Verdict);
        Assert.False((await store.GetRunAsync(runId, Ct))!.Retained);
    }

    /// <summary>US2 scenario 3: min_samples 1000 against a run with 60 tick rows is inconclusive, exit code 6.</summary>
    [Fact]
    public async Task RuleBelowItsSampleFloorIsInconclusive()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);
        var rule = ViolationsRule with { MinSamples = 1000 };

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [rule], [], null, Ct);

        Assert.Equal(HarnessExitCodes.Inconclusive, report.ExitCode);
        var row = await ResultRow(store, runId, "sim.violations");
        Assert.Equal("inconclusive", row.Outcome);
        Assert.Equal(60UL, row.Samples);
    }

    /// <summary>US2 scenarios 4 and 5 through the store: report-only without a baseline, then fail beyond 10% once one exists.</summary>
    [Theory]
    [InlineData(null, "passed", 0)]
    [InlineData(100d, "passed", 0)]
    [InlineData(90d, "failed", 5)]
    public async Task BaselineRuleReportsWithoutBaselineAndFailsBeyondTheRegression(double? baseline, string outcome, int exitCode)
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);

        // The value is 100 for every run: 100 passes against 100 (+10%), and fails against 90 (limit 99).
        var rule = new RuleDefinition
        {
            Id = "sim.hundred",
            File = "sim.hundred.yaml",
            AppliesTo = ["simulator"],
            Query = "SELECT 100.0, count() FROM sim_ticks WHERE run_id = {run_id:String}",
            Mode = RuleMode.Baseline,
            MaxRegression = 0.10,
        };
        IReadOnlyList<Baseline> baselines = baseline is { } b ? [new Baseline("sim.hundred", "none", b, "source-run", DateTime.UtcNow)] : [];

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [rule], baselines, null, Ct);

        Assert.Equal(exitCode, report.ExitCode);
        var row = await ResultRow(store, runId, "sim.hundred");
        Assert.Equal(outcome, row.Outcome);
        if (baseline is null)
        {
            Assert.Equal("no baseline (report only)", row.Note);
        }
    }

    /// <summary>US2 scenario 6: a failing run's rows are retained, and its manifest, seed and inputs are written out.</summary>
    [Fact]
    public async Task FailingRunIsRetainedAndKeepsManifestSeedAndInputs()
    {
        var (folder, runId) = RunFolders.CleanRun();
        RunFolders.SeedViolation(folder);
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);
        var artifacts = RunFolders.TempFolder();

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [ViolationsRule], [], artifacts, Ct);

        Assert.True((await store.GetRunAsync(runId, Ct))!.Retained);
        var ticks = RunFolders.CsvRows(Path.Combine(folder, "ticks.csv"));
        Assert.Equal(ticks, Count(await store.ScalarAsync("SELECT count() FROM sim_ticks_retained WHERE run_id = {run_id:String}", runId, Ct)));
        Assert.Equal(RunFolders.CsvRows(Path.Combine(folder, "events.csv")),
            Count(await store.ScalarAsync("SELECT count() FROM sim_events_retained WHERE run_id = {run_id:String}", runId, Ct)));

        Assert.NotNull(report.ArtifactsFolder);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(report.ArtifactsFolder, "manifest.json")))!;
        Assert.Equal(runId, manifest["runId"]!.GetValue<string>());
        var inputs = JsonNode.Parse(File.ReadAllText(Path.Combine(report.ArtifactsFolder, "inputs.json")))!;
        Assert.Equal(7UL, inputs["seed"]!.GetValue<ulong>());
        Assert.Equal("Baseline", inputs["parameters"]!["scenario"]!.GetValue<string>());
        var results = JsonNode.Parse(File.ReadAllText(Path.Combine(report.ArtifactsFolder, "rule-results.json")))!.AsArray();
        Assert.Equal("failed", results[0]!["outcome"]!.GetValue<string>());
    }

    /// <summary>US2 scenario 8: a rule for another run kind is skipped and writes no result.</summary>
    [Fact]
    public async Task RuleForAnotherRunKindIsSkipped()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);
        var botRule = ViolationsRule with { Id = "bot.only", AppliesTo = ["bot"] };

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [botRule, ViolationsRule], [], null, Ct);

        Assert.DoesNotContain(report.Results, r => r.RuleId == "bot.only");
        Assert.Equal(0, Count(await store.ScalarAsync(
            "SELECT count() FROM rule_results WHERE run_id = {run_id:String} AND rule_id = 'bot.only'", runId, Ct)));
        Assert.Equal(1, Count(await store.ScalarAsync(
            "SELECT count() FROM rule_results WHERE run_id = {run_id:String} AND rule_id = 'sim.violations'", runId, Ct)));
    }

    /// <summary>Edge case: a query that errors is recorded as an error result and exits 2, never a pass.</summary>
    [Fact]
    public async Task BrokenQueryIsAnErrorNotAPass()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);
        var broken = ViolationsRule with { Id = "sim.broken", Query = "SELECT no_such_column, count() FROM sim_ticks WHERE run_id = {run_id:String}" };

        var report = await new RuleRunner(store, Logger.None).RunAsync(runId, [broken], [], null, Ct);

        Assert.Equal(HarnessExitCodes.Invalid, report.ExitCode);
        Assert.Equal("error", (await ResultRow(store, runId, "sim.broken")).Outcome);
    }

    /// <summary>Edge case: a run that isn't loaded can't be evaluated.</summary>
    [Fact]
    public async Task RunThatIsNotLoadedIsRefused()
    {
        using var store = clickHouse.CreateStore();

        await Assert.ThrowsAsync<RunNotLoadedException>(() =>
            new RuleRunner(store, Logger.None).RunAsync(RunFolders.UniqueId("missing"), [ViolationsRule], [], null, Ct));
    }

    /// <summary>The committed timer-fault rule passes the clean fixture and fails one with a TimerFault event.</summary>
    [Theory]
    [InlineData(false, "passed")]
    [InlineData(true, "failed")]
    public async Task CommittedTimerFaultRuleCountsTimerFaultEvents(bool seedFault, string outcome)
    {
        var (folder, runId) = RunFolders.CleanRun();
        if (seedFault)
        {
            File.AppendAllText(Path.Combine(folder, "events.csv"), "10,20,TimerFault,4,,,,,\"timer 9 expected 30 actual 31\"\n");
        }

        using var store = clickHouse.CreateStore();
        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);
        var rules = RuleFileParser.LoadFolder(Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "rules"));

        await new RuleRunner(store, Logger.None).RunAsync(runId, rules, [], null, Ct);

        Assert.Equal(outcome, (await ResultRow(store, runId, "sim.timer_faults")).Outcome);
    }

    private static async Task<(double? Value, double? Threshold, ulong Samples, string Outcome, string Note)> ResultRow(
        ClickHouseStore store, string runId, string ruleId)
    {
        var json = (string)(await store.ScalarAsync(
            "SELECT toJSONString(tuple(value, threshold, samples, outcome, note)) FROM rule_results " +
            $"WHERE run_id = {{run_id:String}} AND rule_id = '{ruleId}' ORDER BY evaluated_at DESC LIMIT 1", runId, Ct))!;
        var array = JsonNode.Parse(json)!.AsArray();
        return (array[0]?.GetValue<double>(), array[1]?.GetValue<double>(), array[2]!.GetValue<ulong>(),
            array[3]!.GetValue<string>(), array[4]!.GetValue<string>());
    }

    private static long Count(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
