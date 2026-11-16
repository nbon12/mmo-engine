namespace MmoEngine.SimCore.Config;

/// <summary>
/// One configuration error: the camelCase JSON path of the field, such as
/// <c>weapons[0].roundsPerMinute</c>, and the message, such as <c>must be between 1 and 6000</c>
/// (specs/015-shared-simulation-core/contracts/config-rules.md#error-shape). It has the shape of the
/// simulator's <c>ConfigError</c> but is the core's own type, because the core references only the BCL
/// and System.IO.Hashing.
/// </summary>
public sealed record ConfigError(string Path, string Message)
{
    /// <summary><c>"{Path}: {Message}"</c>.</summary>
    public override string ToString() => $"{Path}: {Message}";
}
