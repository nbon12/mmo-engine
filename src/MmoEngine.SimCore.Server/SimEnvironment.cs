using MmoEngine.SimCore.Config;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The environment name check (constitution, Operations; contracts/simcore-cli.md#settings-output-and-exit-codes):
/// <c>DOTNET_ENVIRONMENT</c> must be one of the known names, and is Development when unset. The names and
/// the message are the simulator's (<c>MmoEngine.Regionizer.Simulator.Config.EnvironmentNames</c>); the
/// core's tooling keeps its own copy because it does not reference the simulator.
/// </summary>
public static class SimEnvironment
{
    /// <summary>The variable that names the environment.</summary>
    public const string VariableName = "DOTNET_ENVIRONMENT";

    /// <summary>The environment when the variable is unset or empty.</summary>
    public const string Default = "Development";

    /// <summary>The known environment names, compared ordinally.</summary>
    public static IReadOnlyList<string> Allowed { get; } = ["Development", "Dev", "Test", "Staging", "Production"];

    /// <summary>
    /// The effective name: <see cref="Default"/> when <paramref name="value"/> is null or empty, the value
    /// itself when it is known, and otherwise the value with the error
    /// <c>DOTNET_ENVIRONMENT: must be one of Development, Dev, Test, Staging, Production, was '&lt;value&gt;'</c>.
    /// </summary>
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
