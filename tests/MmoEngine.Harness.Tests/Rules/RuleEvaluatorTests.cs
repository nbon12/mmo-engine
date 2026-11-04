using MmoEngine.Harness.Rules;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Tests.Rules;

/// <summary>US2: how one rule's result is decided (FR-011 to FR-013, FR-015), without a store.</summary>
public sealed class RuleEvaluatorTests
{
    private static RuleDefinition Fixed(double threshold, Comparison comparison = Comparison.LessOrEqual, ulong minSamples = 0) => new()
    {
        Id = "sim.violations",
        File = "sim.violations.yaml",
        AppliesTo = ["simulator"],
        Query = "SELECT sum(violations), count() FROM sim_ticks WHERE run_id = {run_id:String}",
        Mode = RuleMode.Fixed,
        Threshold = threshold,
        Comparison = comparison,
        MinSamples = minSamples,
    };

    private static RuleDefinition BaselineRule(double maxRegression, Direction direction) => new()
    {
        Id = "bot.correction_p99",
        File = "bot.correction_p99.yaml",
        AppliesTo = ["bot"],
        Query = "SELECT 1, 1 WHERE {run_id:String} != ''",
        Mode = RuleMode.Baseline,
        MaxRegression = maxRegression,
        Direction = direction,
    };

    /// <summary>US2 scenarios 1 and 2: a fixed "= 0 violations" rule fails on one violation and passes on none.</summary>
    [Theory]
    [InlineData(1d, RuleOutcome.Failed)]
    [InlineData(0d, RuleOutcome.Passed)]
    public void FixedZeroViolationsRuleFailsOnOneAndPassesOnNone(double violations, RuleOutcome expected)
    {
        var result = RuleEvaluator.Evaluate(Fixed(0), new RuleQueryOutcome(true, violations, 60), null);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(violations, result.Value);
        Assert.Equal(0d, result.Threshold);
        Assert.Equal(60UL, result.Samples);
    }

    /// <summary>Every fixed-mode comparison, on and off its boundary.</summary>
    [Theory]
    [InlineData(Comparison.LessOrEqual, 10d, 10d, true)]
    [InlineData(Comparison.LessOrEqual, 10.5d, 10d, false)]
    [InlineData(Comparison.Less, 10d, 10d, false)]
    [InlineData(Comparison.Less, 9d, 10d, true)]
    [InlineData(Comparison.GreaterOrEqual, 10d, 10d, true)]
    [InlineData(Comparison.GreaterOrEqual, 9d, 10d, false)]
    [InlineData(Comparison.Greater, 10d, 10d, false)]
    [InlineData(Comparison.Greater, 11d, 10d, true)]
    [InlineData(Comparison.Equal, 10d, 10d, true)]
    [InlineData(Comparison.Equal, 11d, 10d, false)]
    public void FixedComparisonsDecideOnTheirBoundary(Comparison comparison, double value, double threshold, bool passes)
    {
        var result = RuleEvaluator.Evaluate(Fixed(threshold, comparison), new RuleQueryOutcome(true, value, 1), null);

        Assert.Equal(passes ? RuleOutcome.Passed : RuleOutcome.Failed, result.Outcome);
    }

    /// <summary>US2 scenario 3: below the sample floor the result is inconclusive, never passed.</summary>
    [Fact]
    public void BelowTheSampleFloorIsInconclusive()
    {
        var result = RuleEvaluator.Evaluate(Fixed(0, minSamples: 1000), new RuleQueryOutcome(true, 0, 900), null);

        Assert.Equal(RuleOutcome.Inconclusive, result.Outcome);
        Assert.Equal(900UL, result.Samples);
        Assert.Contains("below the floor of 1000", result.Note, StringComparison.Ordinal);
    }

    /// <summary>Edge cases: no row, a NULL value or NaN are inconclusive.</summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, double.NaN)]
    public void NoRowOrNoValueIsInconclusive(bool hasRow, double? value)
    {
        var result = RuleEvaluator.Evaluate(Fixed(0), new RuleQueryOutcome(hasRow, value, 5), null);

        Assert.Equal(RuleOutcome.Inconclusive, result.Outcome);
    }

    /// <summary>US2 scenario 4: a baseline rule with no baseline passes, noted "no baseline".</summary>
    [Fact]
    public void BaselineRuleWithoutBaselineReportsOnly()
    {
        var result = RuleEvaluator.Evaluate(BaselineRule(0.10, Direction.LowerIsBetter), new RuleQueryOutcome(true, 1000, 1), null);

        Assert.Equal(RuleOutcome.Passed, result.Outcome);
        Assert.Equal("no baseline (report only)", result.Note);
    }

    /// <summary>US2 scenario 5: baseline 100, 10% allowed, lower is better: 111 fails and 110 passes; and the mirror case.</summary>
    [Theory]
    [InlineData(Direction.LowerIsBetter, 111d, false)]
    [InlineData(Direction.LowerIsBetter, 110d, true)]
    [InlineData(Direction.LowerIsBetter, 50d, true)]
    [InlineData(Direction.HigherIsBetter, 89d, false)]
    [InlineData(Direction.HigherIsBetter, 90d, true)]
    [InlineData(Direction.HigherIsBetter, 150d, true)]
    public void BaselineRuleFailsBeyondTheAllowedRegression(Direction direction, double value, bool passes)
    {
        var result = RuleEvaluator.Evaluate(BaselineRule(0.10, direction), new RuleQueryOutcome(true, value, 1), 100);

        Assert.Equal(passes ? RuleOutcome.Passed : RuleOutcome.Failed, result.Outcome);
        Assert.Equal(direction == Direction.LowerIsBetter ? 110d : 90d, result.Threshold!.Value, 9);
    }

    /// <summary>FR-015: the exit code and verdict are the worst outcome; no applicable rule passes with verdict "none".</summary>
    [Theory]
    [InlineData(new[] { RuleOutcome.Passed, RuleOutcome.Passed }, 0, "passed")]
    [InlineData(new[] { RuleOutcome.Passed, RuleOutcome.Inconclusive }, 6, "inconclusive")]
    [InlineData(new[] { RuleOutcome.Inconclusive, RuleOutcome.Failed }, 5, "failed")]
    [InlineData(new[] { RuleOutcome.Failed, RuleOutcome.Error }, 2, "error")]
    [InlineData(new RuleOutcome[0], 0, "none")]
    public void ExitCodeAndVerdictAreTheWorstOutcome(RuleOutcome[] outcomes, int exitCode, string verdict)
    {
        var results = outcomes.Select((o, i) => new RuleResult($"r{i}", o, 0, 0, 0, "")).ToList();

        Assert.Equal(exitCode, RuleEvaluator.ExitCode(results));
        Assert.Equal(verdict, RuleEvaluator.Verdict(results));
    }
}
