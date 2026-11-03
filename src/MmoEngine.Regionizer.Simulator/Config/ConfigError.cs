namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>
/// One invalid configuration value, keyed by its JSON path (for example
/// <c>regionizer.sectionSize</c>) or, for the environment name, by <c>DOTNET_ENVIRONMENT</c>.
/// </summary>
public sealed record ConfigError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>Thrown when a run is started with a configuration that fails validation (exit code 2).</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigError> errors)
        : base("The configuration is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<ConfigError> Errors { get; }
}

/// <summary>
/// The environment name check (constitution, Operations): <c>DOTNET_ENVIRONMENT</c> must be one of
/// the known names, and is Development when unset.
/// </summary>
public static class EnvironmentNames
{
    public const string VariableName = "DOTNET_ENVIRONMENT";

    public const string Default = "Development";

    public static IReadOnlyList<string> Allowed { get; } = ["Development", "Dev", "Test", "Staging", "Production"];

    /// <summary>The effective name, or an error when <paramref name="value"/> is set to an unknown name.</summary>
    public static (string Name, ConfigError? Error) Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (Default, null);
        }

        return Allowed.Contains(value, StringComparer.Ordinal)
            ? (value, null)
            : (value, new ConfigError(VariableName, $"must be one of {string.Join(", ", Allowed)}, was '{value}'"));
    }
}
