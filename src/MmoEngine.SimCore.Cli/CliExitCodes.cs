namespace MmoEngine.SimCore.Cli;

/// <summary>
/// The <c>simcore</c> exit codes (specs/015-shared-simulation-core/contracts/simcore-cli.md#settings-output-and-exit-codes).
/// </summary>
internal static class CliExitCodes
{
    /// <summary>Success: everything matched, or the input is valid.</summary>
    public const int Success = 0;

    /// <summary>A divergence, or a validation failure.</summary>
    public const int Failed = 1;

    /// <summary>
    /// A usage error, an unreadable or refused input, a NotComparable stream (another format version or
    /// rules revision), or a check that could not run.
    /// </summary>
    public const int Usage = 2;
}
