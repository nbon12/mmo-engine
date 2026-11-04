using MmoEngine.Harness.Rules;

namespace MmoEngine.Harness.Tests.Rules;

/// <summary>US2 scenario 7 and FR-009/FR-010: rule and baseline files are validated before any rule runs.</summary>
public sealed class RuleFileParserTests
{
    private const string ValidFixed = """
        id: sim.violations
        applies_to: [simulator, sweep-cell]
        min_samples: 1
        query: |
          SELECT sum(violations), count() FROM sim_ticks WHERE run_id = {run_id:String}
        mode: fixed
        comparison: "<="
        threshold: 0
        """;

    private const string ValidBaseline = """
        id: bot.correction_p99
        applies_to: [bot]
        min_samples: 100000
        query: |
          SELECT quantile(0.99)(correction_m), count()
          FROM bot_frames WHERE run_id = {run_id:String}
        mode: baseline
        max_regression: 10%
        """;

    [Fact]
    public void FixedRuleParsesEveryField()
    {
        var rule = RuleFileParser.Parse(ValidFixed, "sim.violations.yaml");

        Assert.Equal("sim.violations", rule.Id);
        Assert.Equal(["simulator", "sweep-cell"], rule.AppliesTo);
        Assert.Equal(1UL, rule.MinSamples);
        Assert.Equal(RuleMode.Fixed, rule.Mode);
        Assert.Equal(Comparison.LessOrEqual, rule.Comparison);
        Assert.Equal(0d, rule.Threshold);
        Assert.Contains("{run_id:String}", rule.Query, StringComparison.Ordinal);
    }

    /// <summary>The issue's own example rule (bot.correction_p99) parses as written.</summary>
    [Fact]
    public void IssueExampleBaselineRuleParses()
    {
        var rule = RuleFileParser.Parse(ValidBaseline, "bot.correction_p99.yaml");

        Assert.Equal(RuleMode.Baseline, rule.Mode);
        Assert.Equal(0.10, rule.MaxRegression, 12);
        Assert.Equal(Direction.LowerIsBetter, rule.Direction);
        Assert.Equal(100000UL, rule.MinSamples);
    }

    /// <summary>US2 scenario 7: a missing field, an unknown mode or a query without {run_id:String} is refused, naming the file and field.</summary>
    [Theory]
    [InlineData("id: \"\"\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0", "id")]
    [InlineData("id: a\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0", "applies_to")]
    [InlineData("id: a\napplies_to: [simulator]\nmode: fixed\nthreshold: 0", "query")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: sometimes\nthreshold: 0", "mode")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: SELECT 1, 1\nmode: fixed\nthreshold: 0", "query")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed", "threshold")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0\ncomparison: \"!=\"", "comparison")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: baseline", "max_regression")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: baseline\nmax_regression: lots", "max_regression")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: baseline\nmax_regression: 10%\ndirection: sideways", "direction")]
    [InlineData("id: a\napplies_to: [spaceship]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0", "applies_to")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0\nmax_regression: 10%", "max_regression")]
    [InlineData("id: a\napplies_to: [simulator]\nquery: \"SELECT 1, 1 WHERE {run_id:String} = ''\"\nmode: fixed\nthreshold: 0\nunknown_field: 1", "unknown field")]
    public void InvalidRuleFileIsRefusedNamingFileAndField(string yaml, string field)
    {
        var error = Assert.Throws<RuleFileException>(() => RuleFileParser.Parse(yaml, "broken.yaml"));

        Assert.StartsWith("broken.yaml: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateRuleIdsAcrossFilesAreRefused()
    {
        var folder = Support.RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(folder, "a.yaml"), ValidFixed);
        File.WriteAllText(Path.Combine(folder, "b.yaml"), ValidFixed);

        var error = Assert.Throws<RuleFileException>(() => RuleFileParser.LoadFolder(folder));

        Assert.Contains("sim.violations", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The committed rules in harness/rules/ all parse.</summary>
    [Fact]
    public void CommittedRulesParse()
    {
        var rules = RuleFileParser.LoadFolder(Path.Combine(RepoRoot(), "harness", "rules"));

        Assert.Contains(rules, r => r.Id == "sim.violations");
    }

    /// <summary>FR-014: a baseline entry must name its source run.</summary>
    [Fact]
    public void BaselineWithoutSourceRunIsRefused()
    {
        const string yaml = "- rule_id: bot.correction_p99\n  profile: Typical\n  value: 0.4\n  set_at: 2026-11-04T00:00:00Z";

        var error = Assert.Throws<RuleFileException>(() => BaselineFile.Parse(yaml, "bot.yaml"));

        Assert.Contains("source_run_id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineFileParsesAndFindsByRuleAndProfile()
    {
        const string yaml = "- rule_id: bot.correction_p99\n  profile: Typical\n  value: 0.4\n  source_run_id: bot-123\n  set_at: 2026-11-04T00:00:00Z";

        var baselines = BaselineFile.Parse(yaml, "bot.yaml");

        Assert.Equal(0.4, BaselineFile.Find(baselines, "bot.correction_p99", "Typical"));
        Assert.Null(BaselineFile.Find(baselines, "bot.correction_p99", "Poor"));
        Assert.Equal("bot-123", baselines[0].SourceRunId);
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MmoEngine.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
