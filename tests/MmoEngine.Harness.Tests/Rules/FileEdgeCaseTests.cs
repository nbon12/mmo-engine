using System.Text.Json.Nodes;
using MmoEngine.Harness.Golden;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Rules;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Rules;

/// <summary>Refusals for malformed rule, baseline, schema and golden files (FR-010, FR-014, FR-019).</summary>
public sealed class FileEdgeCaseTests
{
    private const string Entry = "- rule_id: r\n  profile: Typical\n  value: 1\n  source_run_id: run-1\n  set_at: 2026-11-04T00:00:00Z\n";

    [Fact]
    public void BaselineFolderLoadsEveryFileAndMissingFolderMeansNone()
    {
        var folder = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(folder, "a.yaml"), Entry);
        File.WriteAllText(Path.Combine(folder, "b.yml"), Entry.Replace("Typical", "Poor", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "README.md"), "not a baseline");

        var baselines = BaselineFile.LoadFolder(folder);

        Assert.Equal(2, baselines.Count);
        Assert.Empty(BaselineFile.LoadFolder(Path.Combine(folder, "missing")));
        Assert.Empty(BaselineFile.LoadFolder(null));
    }

    [Fact]
    public void DuplicateBaselineIsRefused()
    {
        var folder = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(folder, "a.yaml"), Entry);
        File.WriteAllText(Path.Combine(folder, "b.yaml"), Entry);

        Assert.Contains("more than once", Assert.Throws<RuleFileException>(() => BaselineFile.LoadFolder(folder)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- rule_id: r\n  unknown: 1\n")]
    [InlineData("[ this is : not, yaml")]
    public void InvalidBaselineYamlIsRefused(string yaml) =>
        Assert.StartsWith("x.yaml: ", Assert.Throws<RuleFileException>(() => BaselineFile.Parse(yaml, "x.yaml")).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("[ this is : not, yaml")]
    public void EmptyOrInvalidRuleYamlIsRefused(string yaml) =>
        Assert.StartsWith("x.yaml: ", Assert.Throws<RuleFileException>(() => RuleFileParser.Parse(yaml, "x.yaml")).Message, StringComparison.Ordinal);

    [Fact]
    public void MissingRulesFolderIsRefused() =>
        Assert.Contains("not found", Assert.Throws<RuleFileException>(() => RuleFileParser.LoadFolder(Path.Combine(RunFolders.TempFolder(), "none"))).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("<=", Comparison.LessOrEqual)]
    [InlineData("<", Comparison.Less)]
    [InlineData(">=", Comparison.GreaterOrEqual)]
    [InlineData(">", Comparison.Greater)]
    [InlineData("=", Comparison.Equal)]
    [InlineData("==", Comparison.Equal)]
    public void ComparisonsParse(string text, Comparison expected) => Assert.Equal(expected, RuleFileParser.ParseComparison(text));

    [Fact]
    public void UnknownEnumTextsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleFileParser.ParseMode("x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleFileParser.ParseComparison("!="));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuleFileParser.ParseDirection("x"));
        Assert.Equal(Direction.HigherIsBetter, RuleFileParser.ParseDirection("higher_is_better"));
    }

    [Theory]
    [InlineData("10%", 0.10)]
    [InlineData("0.25", 0.25)]
    [InlineData(" 5 % ", 0.05)]
    public void PercentagesParse(string text, double expected) => Assert.Equal(expected, RuleFileParser.ParsePercent(text.Replace(" %", "%", StringComparison.Ordinal)), 12);

    [Fact]
    public void SchemaFileWithoutCreateTableIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => SchemaScripts.TableName("SELECT 1"));

    [Fact]
    public void ArtifactsKeepANonJsonManifestAsText()
    {
        var folder = RunFolders.TempFolder();
        var run = new RunRow { RunId = "odd/run id", Kind = "probe", Manifest = "not json", Seed = 3 };

        var written = RuleRunner.WriteArtifacts(folder, run, [new RuleResult("r", RuleOutcome.Failed, 1, 0, 1, "n")]);

        Assert.EndsWith("odd_run_id", written, StringComparison.Ordinal);
        Assert.Equal("not json", JsonNode.Parse(File.ReadAllText(Path.Combine(written, "manifest.json")))!.GetValue<string>());
        Assert.Equal(3UL, JsonNode.Parse(File.ReadAllText(Path.Combine(written, "inputs.json")))!["seed"]!.GetValue<ulong>());
    }

    [Fact]
    public void MalformedGoldenFilesAreRefused()
    {
        var golden = RunFolders.TempFolder();
        var (run, _) = RunFolders.CleanRun();

        Assert.Contains("golden.json", Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run)).Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(golden, "golden.json"), "{ broken");
        Assert.Contains("not valid JSON", Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run)).Message, StringComparison.Ordinal);

        var runnerClass = RunnerClass.FromManifest(RunFolderReader.Open(run).Manifest).Id;
        File.WriteAllText(Path.Combine(golden, "golden.json"),
            new JsonObject { ["formatVersion"] = 1, ["name"] = "g", ["runnerClass"] = new JsonObject { ["id"] = runnerClass } }.ToJsonString());
        Assert.Contains("not found", Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run)).Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(golden, "ticks.csv"), "\n");
        Assert.Contains("is empty", Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run)).Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(golden, "ticks.csv"), "tick,regionCount\n0,1\n");
        Assert.Contains("no tick or stateHash", Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunWithUnreadableManifestIsRefusedNotCompared()
    {
        var golden = RunFolders.TempFolder();
        File.WriteAllText(Path.Combine(golden, "golden.json"), new JsonObject { ["formatVersion"] = 1, ["name"] = "g" }.ToJsonString());
        var (run, _) = RunFolders.CleanRun();
        File.WriteAllText(Path.Combine(run, "manifest.json"), "{ broken");

        Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run));
        File.Delete(Path.Combine(run, "manifest.json"));
        Assert.Throws<RunFolderException>(() => GoldenRuns.Compare(golden, run));
    }
}
