using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>How region ticks are ordered (research D7).</summary>
public enum SchedulingMode
{
    /// <summary>Every ready region ticks every tick period (2 base steps by default), one after another in ID order.</summary>
    Lockstep,

    /// <summary>Region tick starts and ends interleave in a seeded order, and regions drop to the degraded rate at random.</summary>
    Interleaved,
}

/// <summary>
/// One run's effective configuration (contracts/config-and-cli.md). A run config file is deep-merged
/// over <c>simulator/scenarios/&lt;scenario&gt;.json</c>; every field is validated before a run starts
/// (FR-026). JSON names are the camelCase property names, so the path of
/// <see cref="RegionizerConfig.SectionSize"/> is <c>regionizer.sectionSize</c>.
/// </summary>
public sealed record RunConfig
{
    /// <summary>One of <see cref="Scenarios.ScenarioNames.All"/>.</summary>
    public string Scenario { get; init; } = string.Empty;

    public ulong Seed { get; init; }

    public SchedulingConfig Scheduling { get; init; } = null!;

    /// <summary>Continent ticks (30 Hz) to run.</summary>
    public long DurationTicks { get; init; }

    public RegionizerConfig Regionizer { get; init; } = null!;

    public MovementConfig Movement { get; init; } = null!;

    public MapConfig Map { get; init; } = null!;

    public PopulationConfig Population { get; init; } = null!;

    public AirLayerConfig AirLayer { get; init; } = null!;

    public TimersConfig Timers { get; init; } = null!;

    public MetricsConfig Metrics { get; init; } = null!;

    /// <summary>Scenario-specific values; each scenario reads and validates its own shape.</summary>
    public JsonObject? ScenarioParameters { get; init; }

    public TraceConfig Trace { get; init; } = new();
}

/// <summary>
/// <c>scheduling</c>: the scheduling mode, the tick rates (FR-018, research D7) and the interleaved
/// mode's drops to the degraded rate. The rates are tuning values (Principle VI); a config that
/// leaves them out gets 60, 30 and 20 Hz.
/// </summary>
public sealed record SchedulingConfig
{
    public SchedulingMode Mode { get; init; }

    /// <summary>Base steps per second: the continent time base. A multiple of both tick rates.</summary>
    public int BaseStepHz { get; init; } = TimeBase.DefaultBaseStepHz;

    /// <summary>Region ticks per second.</summary>
    public int TickHz { get; init; } = TimeBase.DefaultTickHz;

    /// <summary>Ticks per second of a region dropped to the degraded rate; lower than <see cref="TickHz"/>.</summary>
    public int DegradedTickHz { get; init; } = TimeBase.DefaultDegradedTickHz;

    /// <summary>The chance, at each region tick end, that the region drops to <see cref="DegradedTickHz"/> (the issue's "20 Hz").</summary>
    public double DropTo20HzProbability { get; init; }

    public int DropTicksMin { get; init; }

    public int DropTicksMax { get; init; }
}

/// <summary><c>regionizer</c>: the core's tuning values.</summary>
public sealed record RegionizerConfig
{
    public int SectionSize { get; init; }

    public ClaimAnchoring Anchoring { get; init; }

    public int MergeGap { get; init; }

    public ClaimRadiusConfig ClaimRadius { get; init; } = null!;

    public SplitConfig Split { get; init; } = null!;

    public TimerMode TimerMode { get; init; }
}

/// <summary><c>regionizer.claimRadius</c>, in metres.</summary>
public sealed record ClaimRadiusConfig
{
    public double Infantry { get; init; }

    public double GroundVehicle { get; init; }

    public double Aircraft { get; init; }

    public double Traveller { get; init; }

    public double CapturePointZone { get; init; }
}

/// <summary><c>regionizer.split</c>: the split damping thresholds (N_min, p_dead, T_min).</summary>
public sealed record SplitConfig
{
    public int MinSections { get; init; }

    public double MinDeadPercent { get; init; }

    public double MinAgeSeconds { get; init; }
}

/// <summary><c>movement</c>: speed ceilings, redeploys and the synthetic models' shape (data-model.md).</summary>
public sealed record MovementConfig
{
    public SpeedCeilingConfig SpeedCeiling { get; init; } = null!;

    public double RedeployProbabilityPerSecond { get; init; }

    public MovementModelConfig Models { get; init; } = null!;
}

/// <summary><c>movement.speedCeiling</c>, in metres per second.</summary>
public sealed record SpeedCeilingConfig
{
    public double Infantry { get; init; }

    public double GroundVehicle { get; init; }

    public double Aircraft { get; init; }
}

/// <summary><c>movement.models</c>: the movement models' parameters (data-model.md, Movement models).</summary>
public sealed record MovementModelConfig
{
    /// <summary>Speeds are U[min, max] times the class ceiling.</summary>
    public double SpeedFactorMin { get; init; }

    public double SpeedFactorMax { get; init; }

    /// <summary>Ground vehicles in a fight drive at U[this, max] times their ceiling.</summary>
    public double VehicleSpeedFactorMin { get; init; }

    public double WaypointIntervalSecondsMin { get; init; }

    public double WaypointIntervalSecondsMax { get; init; }

    /// <summary>The share of infantry waypoints picked near an objective.</summary>
    public double ObjectiveWaypointShare { get; init; }

    public double ObjectiveRadiusMetres { get; init; }

    /// <summary>The width of the annulus outside the fight radius where ground vehicles loiter.</summary>
    public double VehicleAnnulusMetres { get; init; }

    /// <summary>How far from the fight's centre a strafing pass enters and leaves.</summary>
    public double StrafeEntryMetres { get; init; }

    public double AircraftFightAltitudeMetres { get; init; }

    public double AircraftCruiseAltitudeMetres { get; init; }
}

/// <summary><c>map</c>: the generated map's size and lanes, or a map file.</summary>
public sealed record MapConfig
{
    public double WidthMetres { get; init; }

    public double HeightMetres { get; init; }

    public int Lanes { get; init; }

    public int BasesPerLane { get; init; }

    /// <summary>A map file (contracts/map-format.md), for scenarios with designed areas.</summary>
    public string? File { get; init; }
}

/// <summary><c>population</c>: the scaled entity total and its limit.</summary>
public sealed record PopulationConfig
{
    /// <summary>Scales every scenario count in proportion (data-model.md).</summary>
    public int Total { get; init; }

    public int Limit { get; init; }
}

/// <summary><c>airLayer</c>: aircraft above an altitude use their own section layer.</summary>
public sealed record AirLayerConfig
{
    public bool Enabled { get; init; }

    public double AltitudeMetres { get; init; }

    public double HysteresisMetres { get; init; }
}

/// <summary><c>timers</c>: synthetic gameplay timers, drawn from the "timers" stream.</summary>
public sealed record TimersConfig
{
    public double SetProbabilityPerEntityPerSecond { get; init; }

    /// <summary>Delay bounds in continent ticks (ticks × baseStepHz / tickHz base steps in absolute mode).</summary>
    public int DelayTicksMin { get; init; }

    public int DelayTicksMax { get; init; }
}

/// <summary><c>metrics</c>: sampling intervals, interaction ranges and budgets.</summary>
public sealed record MetricsConfig
{
    public int MetricsEveryTicks { get; init; }

    public int IslandCheckEveryTicks { get; init; }

    public int CrossRegionPairsEveryTicks { get; init; }

    public double DeferredRangeMetres { get; init; }

    public double ReadOnlyRangeMetres { get; init; }

    public int TargetFightSize { get; init; }

    public double BookkeepingBudgetMicros { get; init; }
}

/// <summary><c>trace</c>: optional trace recording and replay (contracts/trace-format.md).</summary>
public sealed record TraceConfig
{
    public string? Record { get; init; }

    public string? Replay { get; init; }
}
