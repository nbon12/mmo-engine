using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// <c>SimConfigValidator</c>, the server's FluentValidation adapter, reports the guard's errors
/// unchanged, so there is one rule table (FR-029, contracts/config-rules.md#server).
/// </summary>
public sealed class SimConfigValidatorTests
{
    public static TheoryData<string> Cases =>
    [
        "default",
        "step-rate-61",
        "gravity-nan",
        "r11-violation",
        "zero-weapons",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ValidatorReportsTheGuardsErrorsUnchanged(string caseName)
    {
        var (config, expected) = Case(caseName);

        var guard = ConfigGuard.Check(config);
        var errors = SimConfigValidator.Errors(config);
        var result = new SimConfigValidator().Validate(config);

        // The row breaks exactly what it names, and the default row breaks nothing.
        Assert.Equal(expected, guard);

        // The adapter's two forms: path for path and message for message, in the guard's order.
        Assert.Equal(guard, errors);
        Assert.Equal(
            guard.Select(error => $"{error.Path}: {error.Message}").ToArray(),
            result.Errors.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}").ToArray());
    }

    private static (SimConfig Config, ConfigError[] Expected) Case(string caseName) => caseName switch
    {
        "default" => (TestConfigs.Default(), []),
        "step-rate-61" => (
            TestConfigs.With(c => c.StepRateHz = 61),
            [new("stepRateHz", "must be between 20 and 60")]),
        "gravity-nan" => (
            TestConfigs.With(c => c.Movement!.Gravity = float.NaN),
            [new("movement.gravity", "must be finite")]),
        "r11-violation" => (
            TestConfigs.With(c => c.Weapons![0]!.RoundsPerMinute = 1801),
            [new("weapons[0].roundsPerMinute", "must be at most weapons[0].maxShotsPerStep times 60 times stepRateHz (1800)")]),
        "zero-weapons" => (
            TestConfigs.With(c => c.Weapons = []),
            [new("weapons", "must have between 1 and 8 entries")]),
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
    };
}
