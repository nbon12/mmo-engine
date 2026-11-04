namespace MmoEngine.Harness;

/// <summary>
/// Exit codes of the <c>harness</c> tools (contracts/harness-cli.md). 0 to 2 match the simulator's meaning;
/// 5 to 8 are new and don't collide with the simulator's 3 and 4.
/// </summary>
public static class HarnessExitCodes
{
    /// <summary>Everything passed, or the command succeeded.</summary>
    public const int Ok = 0;

    /// <summary>An infrastructure error: the store is unreachable, a file can't be read, or an unexpected failure.</summary>
    public const int Error = 1;

    /// <summary>Invalid input: a bad option, rule file, run folder or query.</summary>
    public const int Invalid = 2;

    /// <summary>At least one rule failed.</summary>
    public const int RuleFailed = 5;

    /// <summary>No rule failed, but at least one was inconclusive (too few samples, no value, invalid host).</summary>
    public const int Inconclusive = 6;

    /// <summary>A golden run diverged from its recording.</summary>
    public const int Diverged = 7;

    /// <summary>A golden run can't be compared: another runner class or format version.</summary>
    public const int NotComparable = 8;

    /// <summary>A calibration found the profile outside tolerance.</summary>
    public const int OutsideTolerance = 9;
}
