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
/// point-to-point aircraft and capture points at every base's objectives. With <c>airLayer.enabled</c>,
/// aircraft above the threshold altitude use their own section layer.
/// </summary>
public sealed class BaselineScenario : IScenario
{
    public string Name => ScenarioNames.Baseline;

    public IEnumerable<ConfigError> Validate(RunConfig config) => LaneMapWorld.ValidateGenerated<BaselineParameters>(config);

    public ScenarioWorld Build(RunConfig config)
    {
        var parameters = ScenarioParameters.Require<BaselineParameters>(config);
        return LaneMapWorld.CreateBuilder(config, parameters).Build([]);
    }
}

/// <summary>The lane-map world shared by Baseline and the scenarios built on it.</summary>
internal static class LaneMapWorld
{
    /// <summary>The air layer's ID on a generated map, which has only layer 0 otherwise.</summary>
    public const int OpenWorldAirLayerId = 1;

    private const string P = ScenarioParameters.Path;

    /// <summary>
    /// Reads and checks Baseline-shaped parameters for a scenario on the generated map (map.lanes ×
    /// map.basesPerLane bases, no map file).
    /// </summary>
    public static List<ConfigError> ValidateGenerated<T>(RunConfig config)
        where T : BaselineParameters
    {
        var errors = new List<ConfigError>();
        var parameters = ScenarioParameters.Read<T>(config, errors);
        if (parameters is not null)
        {
            RequireNoMapFile(config, errors);
            Validate(config, parameters, errors);
        }

        return errors;
    }

    /// <summary>
    /// Checks the Baseline-shaped parameters against the common configuration. <paramref name="baseCount"/>
    /// is the number of bases fights can stand at (default map.lanes × map.basesPerLane), and
    /// <paramref name="extraEntities"/> counts non-player entities the scenario adds besides one capture
    /// point per objective of each of those bases.
    /// </summary>
    public static void Validate(RunConfig config, BaselineParameters p, List<ConfigError> errors, int? baseCount = null, long extraEntities = 0)
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

        var bases = baseCount ?? (config.Map.Lanes * config.Map.BasesPerLane);
        if (p.FightBases is { } fightBases)
        {
            if (fightBases.Length != p.Fights.Length)
            {
                errors.Add(new ConfigError($"{P}.fightBases", $"must name one base per fight ({p.Fights.Length}), has {fightBases.Length}"));
            }

            for (var i = 0; i < fightBases.Length; i++)
            {
                if (fightBases[i] < 0 || fightBases[i] >= bases)
                {
                    errors.Add(new ConfigError($"{P}.fightBases[{i}]", $"must be a base index from 0 to {bases - 1}"));
                }
                else if (Array.IndexOf(fightBases, fightBases[i]) != i)
                {
                    errors.Add(new ConfigError($"{P}.fightBases[{i}]", $"repeats base {fightBases[i]}; each fight needs its own base"));
                }
            }
        }
        else if (p.Fights.Length > bases)
        {
            errors.Add(new ConfigError($"{P}.fights", $"has {p.Fights.Length} fights but the map has {bases} bases"));
        }

        if (p.Travellers < 0)
        {
            errors.Add(new ConfigError($"{P}.travellers", "must be at least 0"));
        }

        if (p.PointToPointAircraft < 0)
        {
            errors.Add(new ConfigError($"{P}.pointToPointAircraft", "must be at least 0"));
        }

        ValidateFightShape(p.Composition, p.FightRadiusPerSqrtPlayer, p.ObjectivesPerBase, p.ObjectiveSpreadMetres, errors);
        if (errors.Count == 0)
        {
            CheckEntityLimit(config, ((long)bases * p.ObjectivesPerBase) + extraEntities, errors);
        }
    }

    /// <summary>The composition, fight radius and objective checks every fight scenario shares.</summary>
    public static void ValidateFightShape(CompositionParameters? composition, double radiusPerSqrtPlayer, int objectivesPerBase, double objectiveSpread, List<ConfigError> errors)
    {
        if (composition is null)
        {
            errors.Add(new ConfigError($"{P}.composition", "is required"));
        }
        else
        {
            CheckShare(composition.Infantry, "infantry", errors);
            CheckShare(composition.GroundVehicle, "groundVehicle", errors);
            CheckShare(composition.Aircraft, "aircraft", errors);
            var sum = composition.Infantry + composition.GroundVehicle + composition.Aircraft;
            if (Math.Abs(sum - 1) > 1e-9)
            {
                errors.Add(new ConfigError($"{P}.composition", $"shares must sum to 1, sum to {sum}"));
            }
        }

        if (!RunConfigValidator.IsFinite(radiusPerSqrtPlayer) || radiusPerSqrtPlayer <= 0)
        {
            errors.Add(new ConfigError($"{P}.fightRadiusPerSqrtPlayer", "must be a finite number of metres greater than 0"));
        }

        if (objectivesPerBase is < 0 or > 16)
        {
            errors.Add(new ConfigError($"{P}.objectivesPerBase", "must be between 0 and 16"));
        }

        if (!RunConfigValidator.IsFinite(objectiveSpread) || objectiveSpread < 0)
        {
            errors.Add(new ConfigError($"{P}.objectiveSpreadMetres", "must be a finite number of metres, at least 0"));
        }
    }

    /// <summary>Refuses a population that, with the scenario's non-player entities, is over <c>population.limit</c>.</summary>
    public static void CheckEntityLimit(RunConfig config, long nonPlayers, List<ConfigError> errors)
    {
        var entities = config.Population.Total + nonPlayers;
        if (entities > config.Population.Limit)
        {
            errors.Add(new ConfigError(
                "population.total",
                $"the scenario creates {entities} entities ({config.Population.Total} players and {nonPlayers} capture points and other non-players), more than population.limit ({config.Population.Limit})"));
        }
    }

    /// <summary>A scenario that generates its own map refuses a map file rather than ignore it.</summary>
    public static void RequireNoMapFile(RunConfig config, List<ConfigError> errors)
    {
        if (config.Map.File is not null)
        {
            errors.Add(new ConfigError("map.file", $"must be null: the {config.Scenario} scenario generates its map from map.widthMetres, map.heightMetres and its layout"));
        }
    }

    /// <summary>The generated lane map of <c>map</c>, with objectives drawn from the "layout" stream.</summary>
    public static MapLayout GenerateLayout(RunConfig config, BaselineParameters p) =>
        MapLayout.Generate(
            config.Map.WidthMetres,
            config.Map.HeightMetres,
            config.Map.Lanes,
            config.Map.BasesPerLane,
            p.ObjectivesPerBase,
            p.ObjectiveSpreadMetres,
            DeterministicRandom.ForStream(config.Seed, "layout"));

    /// <summary>The open-world topology of <c>map</c>, plus the air layer (direct entry) when <c>airLayer.enabled</c>.</summary>
    public static (SectionTopology Topology, int AirLayerId) OpenWorld(RunConfig config)
    {
        var s = config.Regionizer.SectionSize;
        return config.AirLayer.Enabled
            ? (SectionTopology.OpenWorld(s, config.Map.WidthMetres, config.Map.HeightMetres, OpenWorldAirLayerId), OpenWorldAirLayerId)
            : (SectionTopology.OpenWorld(s, config.Map.WidthMetres, config.Map.HeightMetres), -1);
    }

    /// <summary>
    /// Lays out the generated map and places every entity: each fight's infantry, ground vehicles and
    /// aircraft, then travellers (round-robin over lanes, alternating infantry and ground-vehicle
    /// speed), point-to-point aircraft and one capture point per objective of every base.
    /// </summary>
    public static WorldBuilder CreateBuilder(RunConfig config, BaselineParameters p)
    {
        var layout = GenerateLayout(config, p);
        var (topology, airLayer) = OpenWorld(config);
        var (fights, others) = PopulationScaler.Scale(p.Fights, [p.Travellers, p.PointToPointAircraft], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        AddFights(builder, p, fights);
        AddTravellers(builder, others[0], layout.Lanes);
        AddPointToPointAircraft(builder, others[1]);
        AddCapturePoints(builder, layout.Bases);
        return builder;
    }

    /// <summary>Adds the lane fights with their scaled sizes: fight <c>i</c> at <c>fightBases[i]</c>, or spread evenly over the bases.</summary>
    public static void AddFights(WorldBuilder builder, BaselineParameters p, int[] scaledFights)
    {
        var bases = builder.Layout.Bases;
        for (var i = 0; i < scaledFights.Length; i++)
        {
            var baseIndex = p.FightBases?[i] ?? (int)((long)i * bases.Count / scaledFights.Length);
            var players = scaledFights[i];
            AddFight(builder, new Fight(i, i, bases[baseIndex], players, p.FightRadiusPerSqrtPlayer * Math.Sqrt(players)), p.Composition);
        }
    }

    /// <summary>Registers a fight and places its infantry, ground vehicles and aircraft by the composition.</summary>
    public static void AddFight(WorldBuilder builder, Fight fight, CompositionParameters composition)
    {
        builder.Fights.Add(fight);
        var (infantry, vehicles, aircraft) = Compose(fight.Players, composition);
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

    /// <summary>Travellers round-robin over the lanes; even ones move at the infantry ceiling, odd ones at the ground-vehicle ceiling.</summary>
    public static void AddTravellers(WorldBuilder builder, int count, IReadOnlyList<Lane> lanes)
    {
        var ceilings = builder.Config.Movement.SpeedCeiling;
        for (var k = 0; k < count; k++)
        {
            var lane = lanes[k % lanes.Count];
            var speed = k % 2 == 0 ? ceilings.Infantry : ceilings.GroundVehicle;
            builder.Add(EntityClass.Traveller, -1, (r, c) => new TravellerModel(r, c, lane, speed));
        }
    }

    public static void AddPointToPointAircraft(WorldBuilder builder, int count)
    {
        for (var k = 0; k < count; k++)
        {
            builder.Add(EntityClass.Aircraft, -1, (r, c) => new PointToPointAircraftModel(r, c));
        }
    }

    /// <summary>One capture point per objective of each base, on the base's layer.</summary>
    public static void AddCapturePoints(WorldBuilder builder, IEnumerable<Base> bases)
    {
        foreach (var atBase in bases)
        {
            for (var o = 0; o < atBase.Objectives.Count; o++)
            {
                var objective = o;
                builder.Add(EntityClass.CapturePoint, -1, (r, c) => new CapturePointModel(r, c, atBase, objective));
            }
        }
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
public record MigrationParameters : BaselineParameters
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

        LaneMapWorld.RequireNoMapFile(config, errors);
        LaneMapWorld.Validate(config, parameters, errors);
        ValidateMigration(parameters, TimeBase.From(config.Scheduling), errors);
        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var parameters = ScenarioParameters.Require<MigrationParameters>(config);
        var builder = LaneMapWorld.CreateBuilder(config, parameters);
        return builder.Build([CreateEvents(config, parameters, builder.Fights.Fights, builder.Layout)]);
    }

    /// <summary>The migration timing checks, shared with Soak.</summary>
    internal static void ValidateMigration(MigrationParameters parameters, TimeBase time, List<ConfigError> errors)
    {
        if (!RunConfigValidator.IsFinite(parameters.MigrationEverySeconds) || time.ToSteps(parameters.MigrationEverySeconds) < 1)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.migrationEverySeconds", "must be a finite number of seconds, at least one base step"));
        }

        if (!RunConfigValidator.IsFinite(parameters.InstantActionShare) || parameters.InstantActionShare is < 0 or > 1)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.instantActionShare", "must be between 0 and 1"));
        }
    }

    /// <summary>The migration events for <paramref name="fights"/> (lane fights only), drawing from the "migration" stream.</summary>
    internal static MigrationEvents CreateEvents(RunConfig config, MigrationParameters parameters, IEnumerable<Fight> fights, MapLayout layout) =>
        new(
            [.. fights],
            layout,
            TimeBase.From(config.Scheduling).ToSteps(parameters.MigrationEverySeconds),
            parameters.InstantActionShare,
            DeterministicRandom.ForStream(config.Seed, "migration"));
}

/// <summary>Moves fights to the next base on their lane at fixed step boundaries and flags Instant Action teleports.</summary>
public sealed class MigrationEvents : IStepEventSource
{
    private readonly IReadOnlyList<Fight> _fights;
    private readonly MapLayout _layout;
    private readonly long _everySteps;
    private readonly double _instantActionShare;
    private readonly DeterministicRandom _random;

    /// <param name="fights">The fights that migrate; each must stand at a base of <paramref name="layout"/>'s lanes.</param>
    /// <param name="layout">The lane map.</param>
    /// <param name="everySteps">Base steps between moves.</param>
    /// <param name="instantActionShare">The chance that each infantry of a moving fight teleports.</param>
    /// <param name="random">The "migration" stream.</param>
    public MigrationEvents(IReadOnlyList<Fight> fights, MapLayout layout, long everySteps, double instantActionShare, DeterministicRandom random)
    {
        ArgumentNullException.ThrowIfNull(fights);
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
        foreach (var fight in _fights)
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
