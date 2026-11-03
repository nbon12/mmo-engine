using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>The share of each fight's players by class; the three shares sum to 1.</summary>
public sealed record CompositionParameters
{
    public double Infantry { get; init; }

    public double GroundVehicle { get; init; }

    public double Aircraft { get; init; }
}

/// <summary>
/// <c>scenarioParameters</c> of Baseline and the scenarios built on its lane map (data-model.md,
/// Scenario layout). Counts are scaled to <c>population.total</c>.
/// </summary>
public record BaselineParameters
{
    /// <summary>Players per fight; fight <c>i</c> carries tag <c>i</c>.</summary>
    public int[] Fights { get; init; } = [];

    /// <summary>The base of each fight (global, lane-major index), or null to spread them evenly.</summary>
    public int[]? FightBases { get; init; }

    public int Travellers { get; init; }

    public int PointToPointAircraft { get; init; }

    public CompositionParameters Composition { get; init; } = null!;

    /// <summary>A fight's radius is this many metres times √players.</summary>
    public double FightRadiusPerSqrtPlayer { get; init; }

    /// <summary>Objectives per base, each with a capture point.</summary>
    public int ObjectivesPerBase { get; init; }

    /// <summary>Objectives lie within this many metres of their base's centre.</summary>
    public double ObjectiveSpreadMetres { get; init; }
}

/// <summary>
/// Baseline (FR-023): a map of lanes and bases with fights at some bases, travellers on the lanes,
/// point-to-point aircraft and capture points at every base's objectives.
/// </summary>
public sealed class BaselineScenario : IScenario
{
    public string Name => ScenarioNames.Baseline;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var parameters = ScenarioParameters.Read<BaselineParameters>(config, errors);
        if (parameters is not null)
        {
            LaneMapWorld.Validate(config, parameters, errors);
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var parameters = ScenarioParameters.Require<BaselineParameters>(config);
        return LaneMapWorld.CreateBuilder(config, parameters).Build([]);
    }
}

/// <summary>The lane-map world shared by Baseline and the scenarios built on it.</summary>
internal static class LaneMapWorld
{
    private const string P = ScenarioParameters.Path;

    /// <summary>Checks the Baseline-shaped parameters against the common configuration.</summary>
    public static void Validate(RunConfig config, BaselineParameters p, List<ConfigError> errors)
    {
        if (p.Fights is null)
        {
            errors.Add(new ConfigError($"{P}.fights", "is required"));
            return;
        }

        for (var i = 0; i < p.Fights.Length; i++)
        {
            if (p.Fights[i] < 0)
            {
                errors.Add(new ConfigError($"{P}.fights[{i}]", "must be at least 0"));
            }
        }

        var baseCount = config.Map.Lanes * config.Map.BasesPerLane;
        if (p.FightBases is { } bases)
        {
            if (bases.Length != p.Fights.Length)
            {
                errors.Add(new ConfigError($"{P}.fightBases", $"must name one base per fight ({p.Fights.Length}), has {bases.Length}"));
            }

            for (var i = 0; i < bases.Length; i++)
            {
                if (bases[i] < 0 || bases[i] >= baseCount)
                {
                    errors.Add(new ConfigError($"{P}.fightBases[{i}]", $"must be a base index from 0 to {baseCount - 1}"));
                }
                else if (Array.IndexOf(bases, bases[i]) != i)
                {
                    errors.Add(new ConfigError($"{P}.fightBases[{i}]", $"repeats base {bases[i]}; each fight needs its own base"));
                }
            }
        }
        else if (p.Fights.Length > baseCount)
        {
            errors.Add(new ConfigError($"{P}.fights", $"has {p.Fights.Length} fights but the map has {baseCount} bases"));
        }

        if (p.Travellers < 0)
        {
            errors.Add(new ConfigError($"{P}.travellers", "must be at least 0"));
        }

        if (p.PointToPointAircraft < 0)
        {
            errors.Add(new ConfigError($"{P}.pointToPointAircraft", "must be at least 0"));
        }

        if (p.Composition is null)
        {
            errors.Add(new ConfigError($"{P}.composition", "is required"));
        }
        else
        {
            CheckShare(p.Composition.Infantry, "infantry", errors);
            CheckShare(p.Composition.GroundVehicle, "groundVehicle", errors);
            CheckShare(p.Composition.Aircraft, "aircraft", errors);
            var sum = p.Composition.Infantry + p.Composition.GroundVehicle + p.Composition.Aircraft;
            if (Math.Abs(sum - 1) > 1e-9)
            {
                errors.Add(new ConfigError($"{P}.composition", $"shares must sum to 1, sum to {sum}"));
            }
        }

        if (!RunConfigValidator.IsFinite(p.FightRadiusPerSqrtPlayer) || p.FightRadiusPerSqrtPlayer <= 0)
        {
            errors.Add(new ConfigError($"{P}.fightRadiusPerSqrtPlayer", "must be a finite number of metres greater than 0"));
        }

        if (p.ObjectivesPerBase is < 0 or > 16)
        {
            errors.Add(new ConfigError($"{P}.objectivesPerBase", "must be between 0 and 16"));
        }

        if (!RunConfigValidator.IsFinite(p.ObjectiveSpreadMetres) || p.ObjectiveSpreadMetres < 0)
        {
            errors.Add(new ConfigError($"{P}.objectiveSpreadMetres", "must be a finite number of metres, at least 0"));
        }

        if (errors.Count == 0)
        {
            var entities = (long)config.Population.Total + ((long)baseCount * p.ObjectivesPerBase);
            if (entities > config.Population.Limit)
            {
                errors.Add(new ConfigError(
                    "population.total",
                    $"the scenario creates {entities} entities ({config.Population.Total} players and {baseCount * p.ObjectivesPerBase} capture points), more than population.limit ({config.Population.Limit})"));
            }
        }
    }

    /// <summary>
    /// Lays out the map and places every entity: each fight's infantry, ground vehicles and aircraft,
    /// then travellers (round-robin over lanes, alternating infantry and ground-vehicle speed),
    /// point-to-point aircraft and one capture point per objective of every base.
    /// </summary>
    public static WorldBuilder CreateBuilder(RunConfig config, BaselineParameters p)
    {
        var map = config.Map;
        var layout = MapLayout.Generate(
            map.WidthMetres,
            map.HeightMetres,
            map.Lanes,
            map.BasesPerLane,
            p.ObjectivesPerBase,
            p.ObjectiveSpreadMetres,
            DeterministicRandom.ForStream(config.Seed, "layout"));
        var topology = SectionTopology.OpenWorld(config.Regionizer.SectionSize, map.WidthMetres, map.HeightMetres);
        var (fights, others) = PopulationScaler.Scale(p.Fights, [p.Travellers, p.PointToPointAircraft], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology);

        for (var i = 0; i < fights.Length; i++)
        {
            var baseIndex = p.FightBases?[i] ?? (int)((long)i * layout.Bases.Count / fights.Length);
            var players = fights[i];
            var fight = new Fight(i, i, layout.Bases[baseIndex], players, p.FightRadiusPerSqrtPlayer * Math.Sqrt(players));
            builder.Fights.Add(fight);
            var (infantry, vehicles, aircraft) = Compose(players, p.Composition);
            for (var k = 0; k < infantry; k++)
            {
                fight.Infantry.Add(builder.Add(EntityClass.Infantry, fight.Tag, (r, c) => new InfantryModel(r, c, fight)));
            }

            for (var k = 0; k < vehicles; k++)
            {
                fight.GroundVehicles.Add(builder.Add(EntityClass.GroundVehicle, fight.Tag, (r, c) => new GroundVehicleModel(r, c, fight)));
            }

            for (var k = 0; k < aircraft; k++)
            {
                fight.Aircraft.Add(builder.Add(EntityClass.Aircraft, -1, (r, c) => new StrafingAircraftModel(r, c, fight)));
            }
        }

        var ceilings = config.Movement.SpeedCeiling;
        for (var k = 0; k < others[0]; k++)
        {
            var lane = layout.Lanes[k % layout.Lanes.Count];
            var speed = k % 2 == 0 ? ceilings.Infantry : ceilings.GroundVehicle;
            builder.Add(EntityClass.Traveller, -1, (r, c) => new TravellerModel(r, c, lane, speed));
        }

        for (var k = 0; k < others[1]; k++)
        {
            builder.Add(EntityClass.Aircraft, -1, (r, c) => new PointToPointAircraftModel(r, c));
        }

        foreach (var atBase in layout.Bases)
        {
            for (var o = 0; o < atBase.Objectives.Count; o++)
            {
                var objective = o;
                builder.Add(EntityClass.CapturePoint, -1, (r, c) => new CapturePointModel(r, c, atBase, objective));
            }
        }

        return builder;
    }

    /// <summary>Infantry and ground vehicles rounded half away from zero; aircraft take the rest.</summary>
    public static (int Infantry, int GroundVehicles, int Aircraft) Compose(int players, CompositionParameters composition)
    {
        var infantry = (int)Math.Min(players, Math.Round(players * composition.Infantry, MidpointRounding.AwayFromZero));
        var vehicles = (int)Math.Min(players - infantry, Math.Round(players * composition.GroundVehicle, MidpointRounding.AwayFromZero));
        return (infantry, vehicles, players - infantry - vehicles);
    }

    private static void CheckShare(double share, string name, List<ConfigError> errors)
    {
        if (!RunConfigValidator.IsFinite(share) || share is < 0 or > 1)
        {
            errors.Add(new ConfigError($"{P}.composition.{name}", "must be between 0 and 1"));
        }
    }
}

/// <summary><c>scenarioParameters</c> of Migration: Baseline's, plus how often fights move and how many infantry teleport.</summary>
public sealed record MigrationParameters : BaselineParameters
{
    /// <summary>Every this many seconds, each fight moves to the next base on its lane.</summary>
    public double MigrationEverySeconds { get; init; }

    /// <summary>The share of a moving fight's infantry that take an Instant Action teleport.</summary>
    public double InstantActionShare { get; init; }
}

/// <summary>
/// Migration (data-model.md): Baseline, but every <c>migrationEverySeconds</c> each fight moves to the
/// next base on its lane (bouncing at the lane's ends) and <c>instantActionShare</c> of its infantry
/// redeploy there by two-step teleport; the rest walk.
/// </summary>
public sealed class MigrationScenario : IScenario
{
    public string Name => ScenarioNames.Migration;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var parameters = ScenarioParameters.Read<MigrationParameters>(config, errors);
        if (parameters is null)
        {
            return errors;
        }

        LaneMapWorld.Validate(config, parameters, errors);
        if (!RunConfigValidator.IsFinite(parameters.MigrationEverySeconds) || TimeBase.SecondsToSteps(parameters.MigrationEverySeconds) < 1)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.migrationEverySeconds", "must be a finite number of seconds, at least one base step"));
        }

        if (!RunConfigValidator.IsFinite(parameters.InstantActionShare) || parameters.InstantActionShare is < 0 or > 1)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.instantActionShare", "must be between 0 and 1"));
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var parameters = ScenarioParameters.Require<MigrationParameters>(config);
        var builder = LaneMapWorld.CreateBuilder(config, parameters);
        var migration = new MigrationEvents(
            builder.Fights,
            builder.Layout,
            TimeBase.SecondsToSteps(parameters.MigrationEverySeconds),
            parameters.InstantActionShare,
            DeterministicRandom.ForStream(config.Seed, "migration"));
        return builder.Build([migration]);
    }
}

/// <summary>Moves every fight to the next base on its lane at fixed step boundaries and flags Instant Action teleports.</summary>
public sealed class MigrationEvents : IStepEventSource
{
    private readonly FightTable _fights;
    private readonly MapLayout _layout;
    private readonly long _everySteps;
    private readonly double _instantActionShare;
    private readonly DeterministicRandom _random;

    public MigrationEvents(FightTable fights, MapLayout layout, long everySteps, double instantActionShare, DeterministicRandom random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(everySteps, 1);
        _fights = fights;
        _layout = layout;
        _everySteps = everySteps;
        _instantActionShare = instantActionShare;
        _random = random;
    }

    /// <summary>How many times fights have moved.</summary>
    public int Migrations { get; private set; }

    public void OnStepBoundary(long step, IStepBoundaryActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (step == 0 || step % _everySteps != 0)
        {
            return;
        }

        Migrations++;
        foreach (var fight in _fights.Fights)
        {
            var (next, direction) = _layout.NextOnLane(fight.Base, fight.Direction);
            fight.Direction = direction;
            fight.MoveTo(next);
            foreach (var infantry in fight.Infantry)
            {
                if (_random.Chance(_instantActionShare))
                {
                    actions.State.InstantActionPending[infantry] = true;
                }
            }
        }
    }
}
