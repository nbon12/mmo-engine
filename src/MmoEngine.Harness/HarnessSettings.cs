using FluentValidation;

namespace MmoEngine.Harness;

/// <summary>An invalid harness setting (FR-034). The tool refuses to start with exit code 2, naming the setting.</summary>
public sealed class HarnessSettingsException(string message) : Exception(message);

/// <summary>
/// The settings every store-backed harness command needs, validated at startup (FR-034). The connection
/// string comes from <c>--clickhouse</c> or <c>MMO_CLICKHOUSE</c>, never from a committed file.
/// </summary>
public sealed record HarnessSettings(string EnvironmentName, string ClickHouseConnectionString)
{
    /// <summary>The environment variable holding the ClickHouse connection string.</summary>
    public const string ClickHouseVariable = "MMO_CLICKHOUSE";

    /// <summary>The environment-name variable, as for every .NET tool in the repo.</summary>
    public const string EnvironmentVariable = "DOTNET_ENVIRONMENT";

    /// <summary>The known environment names (constitution, Configuration and secrets).</summary>
    public static IReadOnlyList<string> AllowedEnvironments { get; } = ["Development", "Dev", "Test", "Staging", "Production"];

    /// <summary>Builds and validates the settings; throws <see cref="HarnessSettingsException"/> on the first invalid one.</summary>
    public static HarnessSettings Resolve(string? environmentName, string? connectionOption, string? connectionVariable)
    {
        var settings = new HarnessSettings(
            string.IsNullOrEmpty(environmentName) ? "Development" : environmentName,
            !string.IsNullOrWhiteSpace(connectionOption) ? connectionOption : connectionVariable ?? "");
        var result = new Validator().Validate(settings);
        if (!result.IsValid)
        {
            var error = result.Errors[0];
            throw new HarnessSettingsException($"{error.PropertyName}: {error.ErrorMessage}");
        }

        return settings;
    }

    private sealed class Validator : AbstractValidator<HarnessSettings>
    {
        public Validator()
        {
            RuleFor(s => s.EnvironmentName).Must(n => AllowedEnvironments.Contains(n, StringComparer.Ordinal))
                .OverridePropertyName(EnvironmentVariable)
                .WithMessage(s => $"must be one of {string.Join(", ", AllowedEnvironments)}, was '{s.EnvironmentName}'");
            RuleFor(s => s.ClickHouseConnectionString).NotEmpty()
                .OverridePropertyName($"--clickhouse or {ClickHouseVariable}")
                .WithMessage("is required: a ClickHouse connection string such as Host=localhost;Port=18123;Username=harness");
            RuleFor(s => s.ClickHouseConnectionString).Must(c => c.Contains("Host=", StringComparison.OrdinalIgnoreCase))
                .When(s => !string.IsNullOrEmpty(s.ClickHouseConnectionString))
                .OverridePropertyName($"--clickhouse or {ClickHouseVariable}")
                .WithMessage("must be a ClickHouse connection string with Host=...");
        }
    }
}
