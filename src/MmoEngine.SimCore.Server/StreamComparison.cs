namespace MmoEngine.SimCore.Server;

/// <summary>
/// How one build's run of one stream compares with the server build's
/// (contracts/recorded-stream-format.md#outcomes, contracts/state-serialization.md#divergence-report).
/// </summary>
public enum StreamOutcome
{
    /// <summary>Every step's record equals the server's, and every hash equals the stream's expected hash.</summary>
    Match = 0,

    /// <summary>Some step differs: a missing or unreadable record, another step number, another hash or state, or a hash that is not the expected one.</summary>
    Diverged = 1,
}

/// <summary>
/// The result of <see cref="DivergenceReport.CompareBuild"/> or <see cref="DivergenceReport.BuildError"/>
/// for one (stream, build) pair: the outcome, the first divergent step with the server's and the build's
/// hashes there (null where a side has no record), the number of differing field lines, the build's error
/// message when it wrote an error file instead of records, and the report's lines joined with <c>\n</c>
/// (empty for <see cref="StreamOutcome.Match"/>).
/// </summary>
/// <param name="Outcome">Match or Diverged.</param>
/// <param name="FirstDivergentStep">The step number of the first divergent step; null for a match.</param>
/// <param name="ServerHash">The server's record hash at that step; null for a match, a build error, or a step only the build has.</param>
/// <param name="BuildHash">The build's record hash at that step; null for a match, a build error, or a missing or unreadable build record.</param>
/// <param name="DifferingFields">The field lines the report lists; 0 unless the states were diffed.</param>
/// <param name="Error">The build's error message, for a build that wrote an error file; otherwise null.</param>
/// <param name="Report">The divergence report, its lines joined with <c>\n</c> and no line break after the last; empty for a match.</param>
public sealed record StreamComparison(
    StreamOutcome Outcome,
    uint? FirstDivergentStep,
    ulong? ServerHash,
    ulong? BuildHash,
    int DifferingFields,
    string? Error,
    string Report);
