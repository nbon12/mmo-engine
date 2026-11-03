using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>
/// Air traffic (data-model.md): Baseline's map and fights with point-to-point aircraft as the swept
/// value, run with and without the air layer (<c>airLayer.enabled</c>). With the layer, aircraft above
/// <c>airLayer.altitudeMetres</c> move onto their own section layer (ID 1), which allows direct entry.
/// </summary>
public sealed class AirTrafficScenario : IScenario
{
    public string Name => ScenarioNames.AirTraffic;

    public IEnumerable<ConfigError> Validate(RunConfig config) => LaneMapWorld.ValidateGenerated<BaselineParameters>(config);

    public ScenarioWorld Build(RunConfig config) =>
        LaneMapWorld.CreateBuilder(config, ScenarioParameters.Require<BaselineParameters>(config)).Build([]);
}

/// <summary>
/// One base (data-model.md): a single fight, 5,000 players by default, on the generated map (one lane
/// of one base by default). It is the dense end of the bookkeeping-cost reading (US7).
/// </summary>
public sealed class OneBaseScenario : IScenario
{
    public string Name => ScenarioNames.OneBase;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = LaneMapWorld.ValidateGenerated<BaselineParameters>(config);
        if (errors.Count == 0 && ScenarioParameters.Require<BaselineParameters>(config).Fights.Length != 1)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.fights", "must hold exactly one fight: One base is a single fight"));
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config) =>
        LaneMapWorld.CreateBuilder(config, ScenarioParameters.Require<BaselineParameters>(config)).Build([]);
}

/// <summary><c>scenarioParameters</c> of Spacing: Baseline's, plus the distance between the two bases.</summary>
public sealed record SpacingParameters : BaselineParameters
{
    /// <summary>Centre-to-centre distance between the two bases, in metres (swept 100–1,000 m).</summary>
    public double SpacingMetres { get; init; }
}

/// <summary>
/// Spacing (data-model.md): two bases <c>spacingMetres</c> apart, centred on one lane across the middle of
/// the map, with a fight at each. The map's <c>lanes</c> and <c>basesPerLane</c> are not used. The
/// defaults field no aircraft: a strafing pass reaches 800 m out, so aircraft would fuse the fights at
/// every swept spacing and hide what the sweep measures (the sprawl margin of research D15 covers them).
/// </summary>
public sealed class SpacingScenario : IScenario
{
    public string Name => ScenarioNames.Spacing;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var p = ScenarioParameters.Read<SpacingParameters>(config, errors);
        if (p is null)
        {
            return errors;
        }

        LaneMapWorld.RequireNoMapFile(config, errors);
        LaneMapWorld.Validate(config, p, errors, baseCount: 2);
        if (!RunConfigValidator.IsFinite(p.SpacingMetres) || p.SpacingMetres <= 0 || p.SpacingMetres >= config.Map.WidthMetres)
        {
            errors.Add(new ConfigError($"{ScenarioParameters.Path}.spacingMetres", $"must be a finite number of metres greater than 0 and less than map.widthMetres ({config.Map.WidthMetres})"));
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var p = ScenarioParameters.Require<SpacingParameters>(config);
        var map = config.Map;
        var centre = map.WidthMetres / 2;
        var half = p.SpacingMetres / 2;
        var layout = MapLayout.SingleLane(
            map.WidthMetres,
            map.HeightMetres,
            [centre - half, centre + half],
            p.ObjectivesPerBase,
            p.ObjectiveSpreadMetres,
            DeterministicRandom.ForStream(config.Seed, "layout"));
        var (topology, airLayer) = LaneMapWorld.OpenWorld(config);
        var (fights, others) = PopulationScaler.Scale(p.Fights, [p.Travellers, p.PointToPointAircraft], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        LaneMapWorld.AddFights(builder, p, fights);
        LaneMapWorld.AddTravellers(builder, others[0], layout.Lanes);
        LaneMapWorld.AddPointToPointAircraft(builder, others[1]);
        LaneMapWorld.AddCapturePoints(builder, layout.Bases);
        return builder.Build([]);
    }
}

/// <summary>How Lane traffic places its travellers.</summary>
public enum TravellerPlacement
{
    /// <summary>Each traveller at a uniform point of the lane, heading a random way (the lane-bridging model's assumption).</summary>
    Uniform,

    /// <summary>In clumps of <c>clumpSize</c> that start within <c>clumpSpreadMetres</c> of a uniform centre and travel together.</summary>
    Clumped,
}

/// <summary><c>scenarioParameters</c> of Lane traffic: Baseline's, plus the lane and how travellers are placed on it.</summary>
public sealed record LaneTrafficParameters : BaselineParameters
{
    /// <summary>The free stretch of lane between the two fights' outer edges, in metres.</summary>
    public double LaneLengthMetres { get; init; }

    public TravellerPlacement TravellerPlacement { get; init; }

    /// <summary>Travellers per clump when <see cref="TravellerPlacement"/> is <see cref="TravellerPlacement.Clumped"/>.</summary>
    public int ClumpSize { get; init; } = 1;

    /// <summary>How far a clump's members start from its centre, in metres.</summary>
    public double ClumpSpreadMetres { get; init; }
}

/// <summary>
/// Lane traffic (data-model.md): two bases joined by one lane across the middle of the map, a fight at
/// each, and travellers moving back and forth on the lane between them. <c>laneLengthMetres</c> is the
/// free stretch between the fights' outer edges (fight radius plus the vehicle annulus), which is the
/// lane ℓ of the lane-bridging estimate (research D16), so the bases stand that far apart plus both
/// edges. Travellers keep to the free stretch, uniformly placed or in clumps. Half travel at the
/// infantry ceiling and half at the ground-vehicle ceiling (alternating by traveller, or by clump).
/// The defaults field no aircraft, whose strafing passes would bridge the lane by themselves.
/// </summary>
public sealed class LaneTrafficScenario : IScenario
{
    private const string P = ScenarioParameters.Path;

    public string Name => ScenarioNames.LaneTraffic;

    /// <summary>
    /// The traveller count is the swept traffic level, so it is exact: only the fights scale, to
    /// <c>population.total</c> minus the travellers and aircraft. Scaling travellers with the population
    /// would move every point of the lane-traffic curve off its x value.
    /// </summary>
    internal static (int[] Fights, int[] Others) Counts(LaneTrafficParameters p, int total)
    {
        var (fights, _) = PopulationScaler.Scale(p.Fights, [], total - p.Travellers - p.PointToPointAircraft);
        return (fights, [p.Travellers, p.PointToPointAircraft]);
    }

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var p = ScenarioParameters.Read<LaneTrafficParameters>(config, errors);
        if (p is null)
        {
            return errors;
        }

        LaneMapWorld.RequireNoMapFile(config, errors);
        LaneMapWorld.Validate(config, p, errors, baseCount: 2);
        if (!RunConfigValidator.IsFinite(p.LaneLengthMetres) || p.LaneLengthMetres <= 0)
        {
            errors.Add(new ConfigError($"{P}.laneLengthMetres", "must be a finite number of metres greater than 0"));
        }

        if (p.ClumpSize < 1)
        {
            errors.Add(new ConfigError($"{P}.clumpSize", "must be at least 1"));
        }

        if (!RunConfigValidator.IsFinite(p.ClumpSpreadMetres) || p.ClumpSpreadMetres < 0)
        {
            errors.Add(new ConfigError($"{P}.clumpSpreadMetres", "must be a finite number of metres, at least 0"));
        }

        if (config.Population.Total < p.Travellers + p.PointToPointAircraft)
        {
            errors.Add(new ConfigError("population.total", $"must hold the {p.Travellers} travellers and {p.PointToPointAircraft} aircraft, which are exact counts here, was {config.Population.Total}"));
        }

        if (errors.Count == 0)
        {
            var (fights, _) = Counts(p, config.Population.Total);
            var (x0, x1) = BaseXs(config, p, fights);
            if (x0 < 0 || x1 >= config.Map.WidthMetres)
            {
                errors.Add(new ConfigError(
                    $"{P}.laneLengthMetres",
                    $"the lane and the fights at its ends need {x1 - x0:0.#} m between base centres plus both fights, more than map.widthMetres ({config.Map.WidthMetres}) holds"));
            }
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var p = ScenarioParameters.Require<LaneTrafficParameters>(config);
        var map = config.Map;
        var (fights, others) = Counts(p, config.Population.Total);
        var (x0, x1) = BaseXs(config, p, fights);
        var layout = MapLayout.SingleLane(
            map.WidthMetres,
            map.HeightMetres,
            [x0, x1],
            p.ObjectivesPerBase,
            p.ObjectiveSpreadMetres,
            DeterministicRandom.ForStream(config.Seed, "layout"));
        var lane = layout.Lanes[0];
        var centre = map.WidthMetres / 2;
        var traffic = lane with { StartX = centre - (p.LaneLengthMetres / 2), EndX = centre + (p.LaneLengthMetres / 2) };
        var (topology, airLayer) = LaneMapWorld.OpenWorld(config);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        LaneMapWorld.AddFights(builder, p, fights);
        if (p.TravellerPlacement == TravellerPlacement.Clumped)
        {
            AddClumps(builder, p, others[0], traffic);
        }
        else
        {
            LaneMapWorld.AddTravellers(builder, others[0], [traffic]);
        }

        LaneMapWorld.AddPointToPointAircraft(builder, others[1]);
        LaneMapWorld.AddCapturePoints(builder, layout.Bases);
        return builder.Build([]);
    }

    /// <summary>
    /// The two base centres: the free lane centred on the map, each base beyond it by its fight's
    /// outer edge (radius plus vehicle annulus; zero with no fight there).
    /// </summary>
    private static (double X0, double X1) BaseXs(RunConfig config, LaneTrafficParameters p, int[] scaledFights)
    {
        var edges = new double[2];
        for (var i = 0; i < scaledFights.Length; i++)
        {
            var at = p.FightBases?[i] ?? (int)((long)i * 2 / scaledFights.Length);
            var edge = (p.FightRadiusPerSqrtPlayer * Math.Sqrt(scaledFights[i])) + config.Movement.Models.VehicleAnnulusMetres;
            edges[at] = Math.Max(edges[at], edge);
        }

        var centre = config.Map.WidthMetres / 2;
        var half = p.LaneLengthMetres / 2;
        return (centre - half - edges[0], centre + half + edges[1]);
    }

    /// <summary>Clumps of <c>clumpSize</c> travellers; each clump's centre and direction come from the "clumps" stream.</summary>
    private static void AddClumps(WorldBuilder builder, LaneTrafficParameters p, int count, Lane traffic)
    {
        var random = DeterministicRandom.ForStream(builder.Config.Seed, "clumps");
        var ceilings = builder.Config.Movement.SpeedCeiling;
        TravellerClump clump = default;
        var speed = 0.0;
        for (var k = 0; k < count; k++)
        {
            var index = k / p.ClumpSize;
            if (k % p.ClumpSize == 0)
            {
                clump = new TravellerClump(traffic.StartX + (random.NextDouble() * traffic.Length), p.ClumpSpreadMetres, random.Chance(0.5) ? 1 : -1);
                speed = index % 2 == 0 ? ceilings.Infantry : ceilings.GroundVehicle;
            }

            var (start, s) = (clump, speed);
            builder.Add(EntityClass.Traveller, -1, (r, c) => new TravellerModel(r, c, traffic, s, start));
        }
    }
}

/// <summary><c>scenarioParameters</c> of Vehicle columns: Baseline's, plus the columns.</summary>
public sealed record VehicleColumnsParameters : BaselineParameters
{
    public int Columns { get; init; }

    /// <summary>Vehicles per column before population scaling; the scaled total is shared out over the columns.</summary>
    public int VehiclesPerColumn { get; init; }

    /// <summary>Distance between consecutive vehicles of a column along its route, in metres.</summary>
    public double ColumnSpacingMetres { get; init; }

    public double ColumnSpeedMetresPerSecond { get; init; }
}

/// <summary>
/// Vehicle columns (data-model.md): Baseline's fights, plus columns of ground vehicles driving back
/// and forth between fights at a fixed speed, a fixed distance apart. Column <c>c</c> drives between
/// the bases of fights <c>c</c> and <c>c + 1</c> (modulo the fight count), its leader starting at a
/// point of the route drawn from the "columns" stream. Column vehicles carry no fight tag.
/// </summary>
public sealed class VehicleColumnsScenario : IScenario
{
    private const string P = ScenarioParameters.Path;

    public string Name => ScenarioNames.VehicleColumns;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var p = ScenarioParameters.Read<VehicleColumnsParameters>(config, errors);
        if (p is null)
        {
            return errors;
        }

        LaneMapWorld.RequireNoMapFile(config, errors);
        LaneMapWorld.Validate(config, p, errors);
        if (p.Columns < 0)
        {
            errors.Add(new ConfigError($"{P}.columns", "must be at least 0"));
        }

        if (p.VehiclesPerColumn < 0)
        {
            errors.Add(new ConfigError($"{P}.vehiclesPerColumn", "must be at least 0"));
        }
        else if ((long)Math.Max(p.Columns, 0) * p.VehiclesPerColumn > int.MaxValue)
        {
            errors.Add(new ConfigError($"{P}.vehiclesPerColumn", "columns × vehiclesPerColumn must fit in a 32-bit count"));
        }

        if (!RunConfigValidator.IsFinite(p.ColumnSpacingMetres) || p.ColumnSpacingMetres < 0)
        {
            errors.Add(new ConfigError($"{P}.columnSpacingMetres", "must be a finite number of metres, at least 0"));
        }

        var ceiling = config.Movement.SpeedCeiling.GroundVehicle;
        if (!RunConfigValidator.IsFinite(p.ColumnSpeedMetresPerSecond) || p.ColumnSpeedMetresPerSecond <= 0 || p.ColumnSpeedMetresPerSecond > ceiling)
        {
            errors.Add(new ConfigError($"{P}.columnSpeedMetresPerSecond", $"must be greater than 0 and at most the ground-vehicle speed ceiling ({ceiling} m/s)"));
        }

        if (p.Columns > 0 && p.VehiclesPerColumn > 0 && p.Fights is { Length: < 2 })
        {
            errors.Add(new ConfigError($"{P}.fights", "needs at least two fights for the columns to drive between"));
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var p = ScenarioParameters.Require<VehicleColumnsParameters>(config);
        var layout = LaneMapWorld.GenerateLayout(config, p);
        var (topology, airLayer) = LaneMapWorld.OpenWorld(config);
        var (fights, others) = PopulationScaler.Scale(p.Fights, [p.Travellers, p.PointToPointAircraft, p.Columns * p.VehiclesPerColumn], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        LaneMapWorld.AddFights(builder, p, fights);
        LaneMapWorld.AddTravellers(builder, others[0], layout.Lanes);
        LaneMapWorld.AddPointToPointAircraft(builder, others[1]);
        AddColumns(builder, p, others[2]);
        LaneMapWorld.AddCapturePoints(builder, layout.Bases);
        return builder.Build([]);
    }

    /// <summary>Shares <paramref name="vehicles"/> out over the columns (the first ones get one more when it does not divide) and places them.</summary>
    private static void AddColumns(WorldBuilder builder, VehicleColumnsParameters p, int vehicles)
    {
        if (p.Columns == 0 || vehicles == 0)
        {
            return;
        }

        var random = DeterministicRandom.ForStream(builder.Config.Seed, "columns");
        var fights = builder.Fights.Fights;
        for (var c = 0; c < p.Columns; c++)
        {
            var from = fights[c % fights.Count].Base;
            var to = fights[(c + 1) % fights.Count].Base;
            var length = Math.Sqrt(Planar.DistanceSquared(from.X, from.Y, to.X, to.Y));
            var route = new ColumnRoute(from.X, from.Y, to.X, to.Y, random.NextDouble() * 2 * length, p.ColumnSpeedMetresPerSecond, TimeBase.From(builder.Config.Scheduling));
            var count = (vehicles / p.Columns) + (c < vehicles % p.Columns ? 1 : 0);
            for (var k = 0; k < count; k++)
            {
                var behind = k * p.ColumnSpacingMetres;
                builder.Add(EntityClass.GroundVehicle, -1, (r, ctx) => new ColumnVehicleModel(r, ctx, route, behind));
            }
        }
    }
}
