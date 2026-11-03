using FluentValidation;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>
/// Validates every common configuration value before a run starts (FR-026, data-model.md). Errors
/// are keyed by JSON path, for example <c>regionizer.sectionSize: must be a power of two between 1
/// and 1024</c>. Scenario-specific values are validated by the scenario (<see cref="IScenario.Validate"/>).
/// </summary>
public sealed class RunConfigValidator : AbstractValidator<RunConfig>
{
    public RunConfigValidator()
    {
        RuleFor(c => c.Scenario)
            .Must(ScenarioNames.IsKnown)
            .WithMessage(c => $"must be one of {string.Join(", ", ScenarioNames.All)}, was '{c.Scenario}'");
        RuleFor(c => c.DurationTicks).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
        RuleFor(c => c.Scheduling).NotNull().WithMessage(Required).SetValidator(new SchedulingValidator());
        RuleFor(c => c.Regionizer).NotNull().WithMessage(Required).SetValidator(new RegionizerValidator());
        RuleFor(c => c.Movement).NotNull().WithMessage(Required).SetValidator(new MovementValidator());
        RuleFor(c => c.Map).NotNull().WithMessage(Required).SetValidator(new MapValidator());
        RuleFor(c => c.Population).NotNull().WithMessage(Required).SetValidator(new PopulationValidator());
        RuleFor(c => c.AirLayer).NotNull().WithMessage(Required).SetValidator(new AirLayerValidator());
        RuleFor(c => c.Timers).NotNull().WithMessage(Required).SetValidator(new TimersValidator());
        RuleFor(c => c.Metrics).NotNull().WithMessage(Required).SetValidator(new MetricsValidator());
        RuleFor(c => c.Trace).NotNull().WithMessage(Required);
    }

    private const string Required = "is required";

    /// <summary>The highest base step or tick rate accepted.</summary>
    private const int MaxRateHz = 1000;

    /// <summary>Validates <paramref name="config"/> and returns its errors keyed by JSON path.</summary>
    public static IReadOnlyList<ConfigError> Errors(RunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var result = new RunConfigValidator().Validate(config);
        return [.. result.Errors.Select(e => new ConfigError(ConfigPaths.FromPropertyChain(e.PropertyName), e.ErrorMessage))];
    }

    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed class SchedulingValidator : AbstractValidator<SchedulingConfig>
    {
        public SchedulingValidator()
        {
            RuleFor(s => s.Mode).IsInEnum().WithMessage("must be Lockstep or Interleaved");

            // Tick rates (FR-018, research D7). The 1,000 Hz ceiling keeps run lengths in base steps far from overflow.
            RuleFor(s => s.BaseStepHz).InclusiveBetween(1, MaxRateHz).WithMessage($"must be between 1 and {MaxRateHz} Hz");
            RuleFor(s => s.TickHz).InclusiveBetween(1, MaxRateHz).WithMessage($"must be between 1 and {MaxRateHz} Hz");
            RuleFor(s => s.DegradedTickHz).InclusiveBetween(1, MaxRateHz).WithMessage($"must be between 1 and {MaxRateHz} Hz");
            RuleFor(s => s.BaseStepHz)
                .Must((s, hz) => s.TickHz < 1 || s.DegradedTickHz < 1 || (hz % s.TickHz == 0 && hz % s.DegradedTickHz == 0))
                .WithMessage(s => $"must be a multiple of scheduling.tickHz ({s.TickHz}) and scheduling.degradedTickHz ({s.DegradedTickHz}), was {s.BaseStepHz}");
            RuleFor(s => s.DegradedTickHz)
                .LessThan(s => s.TickHz)
                .WithMessage(s => $"must be lower than scheduling.tickHz ({s.TickHz}), was {s.DegradedTickHz}");
            RuleFor(s => s.DropTo20HzProbability).InclusiveBetween(0, 1).WithMessage("must be between 0 and 1");
            RuleFor(s => s.DropTicksMin).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
            RuleFor(s => s.DropTicksMax).GreaterThanOrEqualTo(s => s.DropTicksMin).WithMessage("must be at least scheduling.dropTicksMin");
        }
    }

    private sealed class RegionizerValidator : AbstractValidator<RegionizerConfig>
    {
        public RegionizerValidator()
        {
            RuleFor(r => r.SectionSize)
                .Must(s => s is >= 1 and <= 1024 && (s & (s - 1)) == 0)
                .WithMessage("must be a power of two between 1 and 1024");
            RuleFor(r => r.Anchoring).IsInEnum().WithMessage("must be Section or Position");
            RuleFor(r => r.MergeGap).InclusiveBetween(0, 1).WithMessage("must be 0 or 1");
            RuleFor(r => r.TimerMode).IsInEnum().WithMessage("must be TickCounter or Absolute");
            RuleFor(r => r.ClaimRadius).NotNull().WithMessage(Required).SetValidator(new ClaimRadiusValidator());
            RuleFor(r => r.Split).NotNull().WithMessage(Required).SetValidator(new SplitValidator());
        }
    }

    private sealed class ClaimRadiusValidator : AbstractValidator<ClaimRadiusConfig>
    {
        public ClaimRadiusValidator()
        {
            const string Message = "must be a finite number of metres, at least 0";
            RuleFor(r => r.Infantry).Must(NonNegative).WithMessage(Message);
            RuleFor(r => r.GroundVehicle).Must(NonNegative).WithMessage(Message);
            RuleFor(r => r.Aircraft).Must(NonNegative).WithMessage(Message);
            RuleFor(r => r.Traveller).Must(NonNegative).WithMessage(Message);
            RuleFor(r => r.CapturePointZone).Must(NonNegative).WithMessage(Message);
        }
    }

    private sealed class SplitValidator : AbstractValidator<SplitConfig>
    {
        public SplitValidator()
        {
            RuleFor(s => s.MinSections).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
            RuleFor(s => s.MinDeadPercent).Must(p => IsFinite(p) && p is >= 0 and <= 100).WithMessage("must be between 0 and 100");
            RuleFor(s => s.MinAgeSeconds).Must(NonNegative).WithMessage("must be a finite number of seconds, at least 0");
        }
    }

    private sealed class MovementValidator : AbstractValidator<MovementConfig>
    {
        public MovementValidator()
        {
            RuleFor(m => m.SpeedCeiling).NotNull().WithMessage(Required).SetValidator(new SpeedCeilingValidator());
            RuleFor(m => m.RedeployProbabilityPerSecond).Must(Probability).WithMessage("must be between 0 and 1");
            RuleFor(m => m.Models).NotNull().WithMessage(Required).SetValidator(new MovementModelValidator());
        }
    }

    private sealed class SpeedCeilingValidator : AbstractValidator<SpeedCeilingConfig>
    {
        public SpeedCeilingValidator()
        {
            const string Message = "must be a finite speed greater than 0";
            RuleFor(s => s.Infantry).Must(Positive).WithMessage(Message);
            RuleFor(s => s.GroundVehicle).Must(Positive).WithMessage(Message);
            RuleFor(s => s.Aircraft).Must(Positive).WithMessage(Message);
        }
    }

    private sealed class MovementModelValidator : AbstractValidator<MovementModelConfig>
    {
        public MovementModelValidator()
        {
            const string Factor = "must be greater than 0 and at most 1";
            RuleFor(m => m.SpeedFactorMin).Must(f => IsFinite(f) && f is > 0 and <= 1).WithMessage(Factor);
            RuleFor(m => m.SpeedFactorMax).Must(f => IsFinite(f) && f is > 0 and <= 1).WithMessage(Factor);
            RuleFor(m => m.SpeedFactorMax).GreaterThanOrEqualTo(m => m.SpeedFactorMin).WithMessage("must be at least movement.models.speedFactorMin");
            RuleFor(m => m.VehicleSpeedFactorMin).Must(f => IsFinite(f) && f is > 0 and <= 1).WithMessage(Factor);
            RuleFor(m => m.VehicleSpeedFactorMin).LessThanOrEqualTo(m => m.SpeedFactorMax).WithMessage("must be at most movement.models.speedFactorMax");
            RuleFor(m => m.WaypointIntervalSecondsMin).Must(Positive).WithMessage("must be a finite number of seconds greater than 0");
            RuleFor(m => m.WaypointIntervalSecondsMax).GreaterThanOrEqualTo(m => m.WaypointIntervalSecondsMin).WithMessage("must be at least movement.models.waypointIntervalSecondsMin");
            RuleFor(m => m.ObjectiveWaypointShare).Must(Probability).WithMessage("must be between 0 and 1");
            RuleFor(m => m.ObjectiveRadiusMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
            RuleFor(m => m.VehicleAnnulusMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
            RuleFor(m => m.StrafeEntryMetres).Must(Positive).WithMessage("must be a finite number of metres greater than 0");
            RuleFor(m => m.AircraftFightAltitudeMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
            RuleFor(m => m.AircraftCruiseAltitudeMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
        }
    }

    private sealed class MapValidator : AbstractValidator<MapConfig>
    {
        public MapValidator()
        {
            const string Side = "must be between 500 and 20000 metres";
            RuleFor(m => m.WidthMetres).Must(w => IsFinite(w) && w is >= 500 and <= 20000).WithMessage(Side);
            RuleFor(m => m.HeightMetres).Must(h => IsFinite(h) && h is >= 500 and <= 20000).WithMessage(Side);
            RuleFor(m => m.Lanes).InclusiveBetween(1, 64).WithMessage("must be between 1 and 64");
            RuleFor(m => m.BasesPerLane).InclusiveBetween(1, 64).WithMessage("must be between 1 and 64");
            RuleFor(m => m.File).Must(f => f is null || f.Trim().Length > 0).WithMessage("must be null or a file path");
        }
    }

    private sealed class PopulationValidator : AbstractValidator<PopulationConfig>
    {
        public PopulationValidator()
        {
            RuleFor(p => p.Limit).GreaterThanOrEqualTo(0).WithMessage("must be at least 0");
            RuleFor(p => p.Total).GreaterThanOrEqualTo(0).WithMessage("must be at least 0");
            RuleFor(p => p.Total)
                .LessThanOrEqualTo(p => p.Limit)
                .WithMessage(p => $"must be at most population.limit ({p.Limit}), was {p.Total}");
        }
    }

    private sealed class AirLayerValidator : AbstractValidator<AirLayerConfig>
    {
        public AirLayerValidator()
        {
            RuleFor(a => a.AltitudeMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
            RuleFor(a => a.HysteresisMetres).Must(NonNegative).WithMessage("must be a finite number of metres, at least 0");
        }
    }

    private sealed class TimersValidator : AbstractValidator<TimersConfig>
    {
        public TimersValidator()
        {
            RuleFor(t => t.SetProbabilityPerEntityPerSecond).Must(Probability).WithMessage("must be between 0 and 1");
            RuleFor(t => t.DelayTicksMin).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
            RuleFor(t => t.DelayTicksMax).GreaterThanOrEqualTo(t => t.DelayTicksMin).WithMessage("must be at least timers.delayTicksMin");
        }
    }

    private sealed class MetricsValidator : AbstractValidator<MetricsConfig>
    {
        public MetricsValidator()
        {
            const string Interval = "must be at least 1";
            RuleFor(m => m.MetricsEveryTicks).GreaterThanOrEqualTo(1).WithMessage(Interval);
            RuleFor(m => m.IslandCheckEveryTicks).GreaterThanOrEqualTo(1).WithMessage(Interval);
            RuleFor(m => m.CrossRegionPairsEveryTicks).GreaterThanOrEqualTo(1).WithMessage(Interval);
            RuleFor(m => m.DeferredRangeMetres).Must(Positive).WithMessage("must be a finite number of metres greater than 0");
            RuleFor(m => m.ReadOnlyRangeMetres).Must(Positive).WithMessage("must be a finite number of metres greater than 0");
            RuleFor(m => m.ReadOnlyRangeMetres)
                .GreaterThanOrEqualTo(m => m.DeferredRangeMetres)
                .WithMessage("must be at least metrics.deferredRangeMetres");
            RuleFor(m => m.TargetFightSize).GreaterThanOrEqualTo(1).WithMessage("must be at least 1");
            RuleFor(m => m.BookkeepingBudgetMicros).Must(Positive).WithMessage("must be a finite number of microseconds greater than 0");
        }
    }

    private static bool NonNegative(double value) => IsFinite(value) && value >= 0;

    private static bool Positive(double value) => IsFinite(value) && value > 0;

    private static bool Probability(double value) => IsFinite(value) && value is >= 0 and <= 1;
}

/// <summary>Turns FluentValidation property chains into the JSON paths users write.</summary>
public static class ConfigPaths
{
    /// <summary><c>Regionizer.ClaimRadius.Infantry</c> becomes <c>regionizer.claimRadius.infantry</c>.</summary>
    public static string FromPropertyChain(string propertyChain)
    {
        if (string.IsNullOrEmpty(propertyChain))
        {
            return "config";
        }

        var parts = propertyChain.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && char.IsUpper(parts[i][0]))
            {
                parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
            }
        }

        return string.Join('.', parts);
    }

    /// <summary><c>$.regionizer.sectionSize</c> (a System.Text.Json path) becomes <c>regionizer.sectionSize</c>.</summary>
    public static string FromJsonPath(string? jsonPath, string prefix = "")
    {
        var path = jsonPath ?? string.Empty;
        if (path.StartsWith('$'))
        {
            path = path[1..];
        }

        path = path.TrimStart('.');
        if (prefix.Length > 0)
        {
            path = path.Length == 0 ? prefix : path.StartsWith('[') ? prefix + path : prefix + "." + path;
        }

        return path.Length == 0 ? "config" : path;
    }
}
