using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// <c>regionizer-sim check</c> and <c>regionizer-sim memo</c>, run in process through <see cref="Program.Run(string[], TextWriter, TextWriter)"/>
/// (contracts/config-and-cli.md): check writes checks.json and exits 0 within tolerance or 4 outside it;
/// memo writes the decision memo and exits 0, or 4 when a value cannot be read; both exit 2 on bad input.
/// </summary>
public sealed class AnalysisCliTests
{
    private const ulong Seed = 20261103;

    [Fact]
    public void CheckAndMemoAreRegisteredCommands()
    {
        var names = Simulator.Cli.Commands.CliCommands.All().Select(c => c.Name).ToList();

        Assert.Contains("check", names);
        Assert.Contains("memo", names);
    }

    [Theory]
    [InlineData("check", "--out", "--options")]
    [InlineData("memo", "results-dir", "--rules")]
    public void HelpListsTheContractArguments(string command, string first, string second)
    {
        var (code, stdout, _) = Invoke([command, "--help"]);

        Assert.Equal(ExitCodes.Clean, code);
        Assert.Contains(first, stdout, StringComparison.Ordinal);
        Assert.Contains(second, stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckWithinToleranceWritesChecksJsonAndExitsZero()
    {
        var output = TestRuns.TempDir();
        var options = WriteOptions(AnalyticChecksTests.SmallOptions(tolerance: 0.10));

        var (code, stdout, stderr) = Invoke(["check", "--out", output, "--options", options]);

        Assert.True(code == ExitCodes.Clean, $"seed {Seed}: {stdout} {stderr}");
        var path = Path.Combine(output, AnalyticChecks.FileName);
        using var checks = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(checks.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(Seed, checks.RootElement.GetProperty("seed").GetUInt64());
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.True(line["passed"]!.GetValue<bool>());
        Assert.Equal(Path.GetFullPath(path), line["checks"]!.GetValue<string>());
        Assert.Empty(line["failures"]!.AsArray());
        Assert.Contains("Analytic checks", stderr, StringComparison.Ordinal);
    }

    // FR-031: a disagreement outside tolerance exits 4, names the check with its seed, and still writes checks.json.
    [Fact]
    public void CheckOutsideToleranceExitsFour()
    {
        var output = TestRuns.TempDir();
        var options = WriteOptions(AnalyticChecksTests.SmallOptions(tolerance: 1e-9));

        var (code, stdout, stderr) = Invoke(["check", "--out", output, "--options", options]);

        Assert.Equal(ExitCodes.OutsideTolerance, code);
        Assert.Equal(4, code);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.False(line["passed"]!.GetValue<bool>());
        var failure = Assert.Single(line["failures"]!.AsArray())!.GetValue<string>();
        Assert.StartsWith("lane bridging: ", failure, StringComparison.Ordinal);
        Assert.Contains($"seed {Seed}", failure, StringComparison.Ordinal);
        Assert.Contains("Check outside tolerance", stderr, StringComparison.Ordinal);
        using var checks = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, AnalyticChecks.FileName)));
        Assert.False(checks.RootElement.GetProperty("passed").GetBoolean());
    }

    // A check that checks nothing is not a pass: no floor cases exits 4 and says so.
    [Fact]
    public void CheckWithNoFloorCasesExitsFour()
    {
        var output = TestRuns.TempDir();
        var small = AnalyticChecksTests.SmallOptions(tolerance: 0.10);
        var options = WriteOptions(small with
        {
            Floor = new FloorCheckOptions([], 1, 1),
            LaneBridging = small.LaneBridging with { Travellers = [30] },
        });

        var (code, stdout, _) = Invoke(["check", "--out", output, "--options", options]);

        Assert.Equal(ExitCodes.OutsideTolerance, code);
        var failures = JsonNode.Parse(stdout.Trim())!["failures"]!.AsArray().Select(f => f!.GetValue<string>());
        Assert.Equal(["claim-radius floor: no cases"], failures);
    }

    // checks.json cannot be written where a file already stands: exit 1, after logging why.
    [Fact]
    public void CheckThatCannotWriteChecksJsonExitsOne()
    {
        var blocker = WriteText("not a folder");
        var small = AnalyticChecksTests.SmallOptions(tolerance: 0.10);
        var options = WriteOptions(small with { LaneBridging = small.LaneBridging with { Travellers = [30] } });

        var (code, stdout, stderr) = Invoke(["check", "--out", blocker, "--options", options]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.Contains("Could not write", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-out")]
    [InlineData("options-absent")]
    [InlineData("options-not-json")]
    [InlineData("options-unknown-field")]
    [InlineData("options-missing-field")]
    [InlineData("options-too-few-lane-trials")]
    [InlineData("unknown-option")]
    public void CheckWithInvalidArgumentsOrOptionsExitsTwo(string variant)
    {
        var output = TestRuns.TempDir();
        var small = AnalyticChecksTests.OptionsJson(AnalyticChecksTests.SmallOptions(tolerance: 0.10));
        string[] args = variant switch
        {
            "missing-out" => ["check"],
            "options-absent" => ["check", "--out", output, "--options", Path.Combine(TestRuns.TempDir(), "none.json")],
            "options-not-json" => ["check", "--out", output, "--options", WriteText("{")],
            "options-unknown-field" => ["check", "--out", output, "--options", WriteText(Edit(small, o => o["extra"] = 1))],
            "options-missing-field" => ["check", "--out", output, "--options", WriteText(Edit(small, o => o.Remove("laneBridging")))],
            "options-too-few-lane-trials" => ["check", "--out", output, "--options", WriteText(Edit(small, o => o["laneBridging"]!["trialsPerStep"] = 199))],
            _ => ["check", "--out", output, "--fast"],
        };

        var (code, stdout, stderr) = Invoke(args);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.False(string.IsNullOrWhiteSpace(stderr));
        Assert.False(File.Exists(Path.Combine(output, AnalyticChecks.FileName)));
    }

    // US6.4 through the CLI: every value and measured option read, the memo written, exit 0.
    [Fact]
    public void MemoWritesTheDecisionMemoAndExitsZero()
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        MemoResultSet.Write(results);
        var memo = Path.Combine(root, "out", "decision-memo.md");

        var (code, stdout, stderr) = Invoke(["memo", results, "--rules", MemoResultSet.RulesPath(), "--out", memo]);

        Assert.True(code == ExitCodes.Clean, stderr);
        var expected = MemoGenerator.Generate(results, MemoRules.Load(MemoResultSet.RulesPath()));
        Assert.Equal(expected.Markdown, File.ReadAllText(memo));
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.True(line["success"]!.GetValue<bool>());
        Assert.Equal(10, line["values"]!.GetValue<int>());
        Assert.Equal(2, line["measuredOptions"]!.GetValue<int>());
        Assert.Empty(line["unreadable"]!.AsArray());
        Assert.Empty(line["flagged"]!.AsArray());
        Assert.Equal(Path.GetFullPath(memo), line["memo"]!.GetValue<string>());
    }

    // A value that cannot be read: the row is marked, the memo is still written, and the command exits 4.
    [Theory]
    [InlineData("baseline-final", "target-fight-size")]
    [InlineData("timer-mode", "timer-mode")]
    public void MemoWithAnUnreadableValueMarksItAndExitsFour(string removedSweep, string unreadableId)
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        MemoResultSet.Write(results);
        Directory.Delete(Path.Combine(results, removedSweep), recursive: true);
        var memo = Path.Combine(root, "decision-memo.md");

        var (code, stdout, stderr) = Invoke(["memo", results, "--rules", MemoResultSet.RulesPath(), "--out", memo]);

        Assert.Equal(4, code);
        Assert.Contains("UNREADABLE", File.ReadAllText(memo), StringComparison.Ordinal);
        var line = JsonNode.Parse(stdout.Trim())!;
        Assert.False(line["success"]!.GetValue<bool>());
        Assert.Equal([unreadableId], line["unreadable"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains(unreadableId, stderr, StringComparison.Ordinal);
    }

    // The memo cannot be written below a file: exit 1, after logging why.
    [Fact]
    public void MemoThatCannotBeWrittenExitsOne()
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        MemoResultSet.Write(results);
        var memo = Path.Combine(WriteText("not a folder"), "decision-memo.md");

        var (code, stdout, stderr) = Invoke(["memo", results, "--rules", MemoResultSet.RulesPath(), "--out", memo]);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.Contains("Could not write the memo", stderr, StringComparison.Ordinal);
    }

    // An empty results folder: every value is unreadable, but the memo is written and names them all.
    [Fact]
    public void MemoOverAnEmptyResultsFolderExitsFour()
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        Directory.CreateDirectory(results);
        var memo = Path.Combine(root, "decision-memo.md");

        var (code, stdout, _) = Invoke(["memo", results, "--rules", MemoResultSet.RulesPath(), "--out", memo]);

        Assert.Equal(4, code);
        Assert.Contains("**10 of 10 values and 2 of 2 measured options could not be read.**", File.ReadAllText(memo), StringComparison.Ordinal);
        Assert.Equal(12, JsonNode.Parse(stdout.Trim())!["unreadable"]!.AsArray().Count);
    }

    // Invalid memo rules stop the command before anything is written, naming the bad field (exit 2).
    [Theory]
    [InlineData("rules-absent", "none.json")]
    [InlineData("rules-not-json", "not valid JSON")]
    [InlineData("rules-old-format", "formatVersion: must be 2")]
    [InlineData("rules-population-missing", "values[0].population: is required")]
    [InlineData("rules-measured-option-one-option", "measuredOptions[0].options: must list at least two options to compare")]
    [InlineData("missing-rules", "--rules")]
    [InlineData("missing-out", "--out")]
    public void MemoWithInvalidRulesOrArgumentsExitsTwo(string variant, string named)
    {
        var root = TestRuns.TempDir();
        var results = Path.Combine(root, "results");
        MemoResultSet.Write(results);
        var memo = Path.Combine(root, "decision-memo.md");
        var committed = File.ReadAllText(MemoResultSet.RulesPath());
        string Rules(string text) => WriteText(text);
        string[] args = variant switch
        {
            "rules-absent" => ["memo", results, "--rules", Path.Combine(root, "none.json"), "--out", memo],
            "rules-not-json" => ["memo", results, "--rules", Rules("{"), "--out", memo],
            "rules-old-format" => ["memo", results, "--rules", Rules(Edit(committed, o => o["formatVersion"] = 1)), "--out", memo],
            "rules-population-missing" => ["memo", results, "--rules", Rules(Edit(committed, o => o["values"]![0]!.AsObject().Remove("population"))), "--out", memo],
            "rules-measured-option-one-option" => ["memo", results, "--rules", Rules(Edit(committed, o => o["measuredOptions"]![0]!["options"] = new JsonArray { 0 })), "--out", memo],
            "missing-rules" => ["memo", results, "--out", memo],
            _ => ["memo", results, "--rules", MemoResultSet.RulesPath()],
        };

        var (code, stdout, stderr) = Invoke(args);

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Equal(string.Empty, stdout.Trim());
        Assert.Contains(named, stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(memo));
    }

    private static string WriteOptions(AnalyticCheckOptions options) => WriteText(AnalyticChecksTests.OptionsJson(options));

    private static string WriteText(string text)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "input.json");
        File.WriteAllText(path, text);
        return path;
    }

    private static string Edit(string json, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    private static (int Code, string Stdout, string Stderr) Invoke(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = Program.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
