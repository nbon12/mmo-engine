using FluentValidation;
using MmoEngine.SimCore.Config;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The server's FluentValidation adapter for <see cref="SimConfig"/> (FR-029,
/// specs/015-shared-simulation-core/contracts/config-rules.md#server). It applies the core's
/// <see cref="ConfigGuard.Check"/> and reports its errors unchanged, path for path and message for message,
/// in the guard's order, so there is one rule table.
/// </summary>
public sealed class SimConfigValidator : AbstractValidator<SimConfig>
{
    public SimConfigValidator()
    {
        RuleFor(c => c).Custom((config, context) =>
        {
            foreach (var error in ConfigGuard.Check(config))
            {
                context.AddFailure(error.Path, error.Message);
            }
        });
    }

    /// <summary>The validator's failures as <see cref="ConfigError"/>s: <see cref="ConfigGuard.Check"/>'s list.</summary>
    public static IReadOnlyList<ConfigError> Errors(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new SimConfigValidator().Validate(config);
        var errors = new ConfigError[result.Errors.Count];
        for (var i = 0; i < errors.Length; i++)
        {
            errors[i] = new ConfigError(result.Errors[i].PropertyName, result.Errors[i].ErrorMessage);
        }

        return errors;
    }
}
