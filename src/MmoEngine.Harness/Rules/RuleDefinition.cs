namespace MmoEngine.Harness.Rules;

/// <summary>How a rule decides pass or fail (contracts/rule-file.md).</summary>
public enum RuleMode
{
    /// <summary>Compare against a threshold in the file.</summary>
    Fixed,

    /// <summary>Report until a baseline exists for the run's profile, then fail on a regression beyond the allowance.</summary>
    Baseline,
}

/// <summary>The comparison a fixed-mode value must satisfy against its threshold.</summary>
public enum Comparison
{
    LessOrEqual,
    Less,
    GreaterOrEqual,
    Greater,
    Equal,
}

/// <summary>Which way is better, for baseline mode.</summary>
public enum Direction
{
    LowerIsBetter,
    HigherIsBetter,
}

/// <summary>A validated rule file (FR-009).</summary>
public sealed record RuleDefinition
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public string Description { get; init; } = "";
    public required IReadOnlyList<string> AppliesTo { get; init; }
    public required string Query { get; init; }
    public required RuleMode Mode { get; init; }
    public ulong MinSamples { get; init; }

    /// <summary>Fixed mode: the threshold.</summary>
    public double Threshold { get; init; }

    /// <summary>Fixed mode: the comparison the value must satisfy.</summary>
    public Comparison Comparison { get; init; } = Comparison.LessOrEqual;

    /// <summary>Baseline mode: the allowed regression as a fraction (0.10 for 10%).</summary>
    public double MaxRegression { get; init; }

    /// <summary>Baseline mode: which way is better.</summary>
    public Direction Direction { get; init; } = Direction.LowerIsBetter;

    /// <summary>Whether the rule applies to a run kind.</summary>
    public bool AppliesToKind(string kind) => AppliesTo.Contains(kind, StringComparer.Ordinal);
}

/// <summary>A rule's outcome for one run.</summary>
public enum RuleOutcome
{
    Passed,
    Inconclusive,
    Failed,
    Error,
}

/// <summary>One rule's result for one run.</summary>
public sealed record RuleResult(string RuleId, RuleOutcome Outcome, double? Value, double? Threshold, ulong Samples, string Note)
{
    /// <summary>The <c>rule_results.outcome</c> text.</summary>
    public string OutcomeText => Outcome.ToString().ToLowerInvariant();
}
