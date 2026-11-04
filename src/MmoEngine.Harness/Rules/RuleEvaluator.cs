using System.Globalization;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Rules;

/// <summary>
/// Decides one rule's outcome from its query's result and, for baseline mode, the baseline value
/// (FR-011 to FR-013). It is pure, so every mode and comparison is tested without a store.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>Relative slack for floating-point comparisons against a computed limit.</summary>
    private const double Epsilon = 1e-9;

    public static RuleResult Evaluate(RuleDefinition rule, RuleQueryOutcome query, double? baseline)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!query.HasRow)
        {
            return new RuleResult(rule.Id, RuleOutcome.Inconclusive, null, null, 0, "the query returned no row");
        }

        if (query.Value is not { } value || double.IsNaN(value))
        {
            return new RuleResult(rule.Id, RuleOutcome.Inconclusive, null, null, query.Samples, "the query returned no value (NULL or NaN)");
        }

        if (query.Samples < rule.MinSamples)
        {
            return new RuleResult(rule.Id, RuleOutcome.Inconclusive, value, null, query.Samples,
                string.Create(CultureInfo.InvariantCulture, $"{query.Samples} samples, below the floor of {rule.MinSamples}"));
        }

        return rule.Mode switch
        {
            RuleMode.Fixed => Fixed(rule, value, query.Samples),
            RuleMode.Baseline => Baseline(rule, value, query.Samples, baseline),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
    }

    /// <summary>A run's verdict: the worst outcome of its rules, or "none" when no rule applied.</summary>
    public static string Verdict(IReadOnlyCollection<RuleResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Count == 0 ? Verdicts.None : results.Max(r => r.Outcome).ToString().ToLowerInvariant();
    }

    /// <summary>The exit code for a set of results (FR-015).</summary>
    public static int ExitCode(IReadOnlyCollection<RuleResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
        {
            return HarnessExitCodes.Ok;
        }

        return results.Max(r => r.Outcome) switch
        {
            RuleOutcome.Error => HarnessExitCodes.Invalid,
            RuleOutcome.Failed => HarnessExitCodes.RuleFailed,
            RuleOutcome.Inconclusive => HarnessExitCodes.Inconclusive,
            _ => HarnessExitCodes.Ok,
        };
    }

    private static RuleResult Fixed(RuleDefinition rule, double value, ulong samples)
    {
        var passed = rule.Comparison switch
        {
            Comparison.LessOrEqual => value <= rule.Threshold,
            Comparison.Less => value < rule.Threshold,
            Comparison.GreaterOrEqual => value >= rule.Threshold,
            Comparison.Greater => value > rule.Threshold,
            Comparison.Equal => value == rule.Threshold,
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        var note = string.Create(CultureInfo.InvariantCulture, $"value {value:R} must be {Symbol(rule.Comparison)} {rule.Threshold:R}");
        return new RuleResult(rule.Id, passed ? RuleOutcome.Passed : RuleOutcome.Failed, value, rule.Threshold, samples, note);
    }

    private static RuleResult Baseline(RuleDefinition rule, double value, ulong samples, double? baseline)
    {
        if (baseline is not { } b)
        {
            return new RuleResult(rule.Id, RuleOutcome.Passed, value, null, samples, "no baseline (report only)");
        }

        double limit;
        bool passed;
        var slack = Epsilon * Math.Max(1, Math.Abs(b));
        if (rule.Direction == Direction.LowerIsBetter)
        {
            limit = b + (Math.Abs(b) * rule.MaxRegression);
            passed = value <= limit + slack;
        }
        else
        {
            limit = b - (Math.Abs(b) * rule.MaxRegression);
            passed = value >= limit - slack;
        }

        var note = string.Create(CultureInfo.InvariantCulture,
            $"baseline {b:R}, allowed regression {rule.MaxRegression * 100:R}% ({(rule.Direction == Direction.LowerIsBetter ? "lower" : "higher")} is better), limit {limit:R}");
        return new RuleResult(rule.Id, passed ? RuleOutcome.Passed : RuleOutcome.Failed, value, limit, samples, note);
    }

    private static string Symbol(Comparison comparison) => comparison switch
    {
        Comparison.LessOrEqual => "<=",
        Comparison.Less => "<",
        Comparison.GreaterOrEqual => ">=",
        Comparison.Greater => ">",
        _ => "=",
    };
}
