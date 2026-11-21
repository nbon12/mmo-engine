using System;
using System.Collections.Generic;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// Thrown by <c>CompiledConfig.Create</c> when <see cref="ConfigGuard.Check"/> finds any error
/// (specs/015-shared-simulation-core/contracts/config-rules.md#error-shape). It carries the guard's list,
/// and its message is <c>The configuration is invalid: </c> followed by the errors joined with
/// <c>"; "</c>.
/// </summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigError> errors)
        : base("The configuration is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    /// <summary>The guard's errors, in rule-table order.</summary>
    public IReadOnlyList<ConfigError> Errors { get; }
}
