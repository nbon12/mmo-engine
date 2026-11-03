using System.Text.Json.Nodes;
using FluentValidation;
using MmoEngine.Regionizer.Simulator.Config;

namespace MmoEngine.Regionizer.Simulator.Sweep;

/// <summary>How a sweep's parameters form cells (contracts/config-and-cli.md).</summary>
public enum SweepMode
{
    /// <summary>Each parameter stepped alone around <c>base</c>: one cell per parameter value.</summary>
    OneAtATime,

    /// <summary>The cartesian product of every parameter's values.</summary>
    Grid,
}

/// <summary>One swept parameter: a dotted JSON path into the run configuration and its values.</summary>
public sealed record SweepParameter
{
    /// <summary>For example <c>regionizer.claimRadius.infantry</c>.</summary>
    public string Path { get; init; } = string.Empty;

    public JsonArray Values { get; init; } = [];
}

/// <summary>
/// A sweep specification (contracts/config-and-cli.md, FR-025): a base run configuration, the
/// parameters to step, the number of seeds per cell and the sweep seed they are derived from.
/// </summary>
public sealed record SweepSpec
{
    /// <summary>The sweep's folder name under the output directory.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>A run configuration, deep-merged over its scenario's defaults like any run file.</summary>
    public JsonObject Base { get; init; } = null!;

    /// <summary>Seeds per cell.</summary>
    public int Seeds { get; init; }

    /// <summary>Seed <c>i</c> of every cell is SplitMix64 of (this, <c>i</c>).</summary>
    public ulong SweepSeed { get; init; }

    public SweepMode Mode { get; init; }

    public IReadOnlyList<SweepParameter> Parameters { get; init; } = [];
}

/// <summary>
/// Validates a sweep specification's own fields. Errors are keyed by JSON path, for example
/// <c>parameters[0].values</c>. Each cell's run configuration is validated afterwards by
/// <see cref="SweepSpecLoader"/>.
/// </summary>
public sealed class SweepSpecValidator : AbstractValidator<SweepSpec>
{
    public SweepSpecValidator()
    {
        RuleFor(s => s.Name)
            .Must(IsFolderName)
            .WithMessage("must be a folder name: letters, digits, '.', '-' or '_', starting with a letter or digit");
        RuleFor(s => s.Base).NotNull().WithMessage("is required");
        RuleFor(s => s.Seeds).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
        RuleFor(s => s.Mode).IsInEnum().WithMessage("must be OneAtATime or Grid");
        RuleFor(s => s.Parameters).NotEmpty().WithMessage("must list at least one parameter");
        RuleForEach(s => s.Parameters).NotNull().WithMessage("is required").SetValidator(new ParameterValidator());
        RuleFor(s => s.Parameters).Custom(NoOverlappingPaths);
        RuleFor(s => s.Base).Custom(NoTraces);
    }

    /// <summary>Validates <paramref name="spec"/> and returns its errors keyed by JSON path.</summary>
    public static IReadOnlyList<ConfigError> Errors(SweepSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var result = new SweepSpecValidator().Validate(spec);
        return [.. result.Errors.Select(e => new ConfigError(ConfigPaths.FromPropertyChain(e.PropertyName), e.ErrorMessage))];
    }

    private static bool IsFolderName(string? name) =>
        !string.IsNullOrEmpty(name)
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static void NoOverlappingPaths(IReadOnlyList<SweepParameter>? parameters, ValidationContext<SweepSpec> context)
    {
        if (parameters is null)
        {
            return;
        }

        for (var j = 1; j < parameters.Count; j++)
        {
            for (var i = 0; i < j; i++)
            {
                var a = parameters[i]?.Path ?? string.Empty;
                var b = parameters[j]?.Path ?? string.Empty;
                if (a.Length > 0 && b.Length > 0 && (a == b || b.StartsWith(a + ".", StringComparison.Ordinal) || a.StartsWith(b + ".", StringComparison.Ordinal)))
                {
                    context.AddFailure($"Parameters[{j}].Path", $"must not repeat or overlap parameters[{i}].path ({a})");
                    break;
                }
            }
        }
    }

    private static void NoTraces(JsonObject? baseConfig, ValidationContext<SweepSpec> context)
    {
        if (baseConfig?["trace"] is not JsonObject trace)
        {
            return;
        }

        foreach (var key in new[] { "record", "replay" })
        {
            if (trace[key] is not null)
            {
                context.AddFailure($"Base.Trace.{key}", "must be null in a sweep: every run writes its own folder, and replays need the recorded seed");
            }
        }
    }

    private sealed class ParameterValidator : AbstractValidator<SweepParameter>
    {
        public ParameterValidator()
        {
            RuleFor(p => p.Path)
                .Must(IsDottedPath)
                .WithMessage("must be a dotted JSON path into the run configuration, such as regionizer.claimRadius.infantry");
            RuleFor(p => p.Path)
                .Must(p => p != "seed")
                .WithMessage("is set by the sweep from sweepSeed; use seeds for more seeds per cell");
            RuleFor(p => p.Path)
                .Must(p => p is null || (p != "trace" && !p.StartsWith("trace.", StringComparison.Ordinal)))
                .WithMessage("cannot be swept: traces are not supported in sweeps");
            RuleFor(p => p.Values).NotEmpty().WithMessage("must list at least one value");
            RuleFor(p => p.Values).Must(Distinct).WithMessage("must not repeat a value");
        }

        private static bool IsDottedPath(string? path) =>
            !string.IsNullOrEmpty(path) && path.Split('.').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

        private static bool Distinct(JsonArray? values) =>
            values is null || values.Select(ConfigJson.Canonical).Distinct(StringComparer.Ordinal).Count() == values.Count;
    }
}
