namespace MmoEngine.Regionizer.Simulator.Monitors;

/// <summary>Why a run stopped early.</summary>
public enum RunFailureKind
{
    /// <summary>The invariant checker reported a broken rule (FR-014, FR-020).</summary>
    Violation,

    /// <summary>The timer monitor saw a timer fire early, late or twice (FR-020).</summary>
    TimerFault,
}

/// <summary>The first violation or timer fault of a run, with what is needed to reproduce it.</summary>
/// <param name="Kind">Violation or timer fault.</param>
/// <param name="Rule">The broken invariant, for a violation.</param>
/// <param name="Tick">The continent tick (30 Hz) it was found at.</param>
/// <param name="TimeSteps">The base step it was found at.</param>
/// <param name="Seed">The run's seed.</param>
/// <param name="Detail">What was wrong.</param>
/// <param name="Count">How many violations or faults were found at that step.</param>
public sealed record RunFailure(RunFailureKind Kind, InvariantRule? Rule, long Tick, long TimeSteps, ulong Seed, string Detail, int Count)
{
    /// <summary>The rule number (1–6) for a violation, or 7 for a timer fault (the issue's seventh monitor).</summary>
    public int RuleNumber => Rule is { } rule ? (int)rule : 7;

    public override string ToString() =>
        Kind == RunFailureKind.Violation
            ? $"invariant rule {RuleNumber} ({Rule}) broken at tick {Tick} (step {TimeSteps}), seed {Seed}: {Detail}"
            : $"timer fault at tick {Tick} (step {TimeSteps}), seed {Seed}: {Detail}";
}

/// <summary>
/// Runs <see cref="InvariantChecker.Check"/> at a step boundary (no region ticking) and reports the
/// first violation it finds. The simulator calls it after every step in which anything changed.
/// </summary>
public sealed class InvariantMonitor
{
    private readonly List<Violation> _found = [];

    /// <summary>How many checks ran.</summary>
    public long Checks { get; private set; }

    /// <summary>The violations the last check found, in the checker's order.</summary>
    public IReadOnlyList<Violation> LastViolations => _found;

    /// <summary>Checks <paramref name="regionizer"/>; returns the first violation, or null when it is clean.</summary>
    public Violation? Check(Regionizer regionizer)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        Checks++;
        _found.Clear();
        return InvariantChecker.Check(regionizer, _found) > 0 ? _found[0] : null;
    }
}
