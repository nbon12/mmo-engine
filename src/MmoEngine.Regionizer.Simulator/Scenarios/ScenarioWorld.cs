using System.Text.Json;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>Everything a scenario hands the engine: the topology, the entities, their movement and step-boundary events.</summary>
public sealed class ScenarioWorld
{
    public required SectionTopology Topology { get; init; }

    /// <summary>Entities in ascending ID order, compact from 0, as placed at step 0.</summary>
    public required IReadOnlyList<EntitySpec> Entities { get; init; }

    /// <summary>The mirror of entity state the movement models read and the engine writes.</summary>
    public required WorldState State { get; init; }

    /// <summary>The per-entity update source. <see cref="Running.RunOptions.MovementSourceFactory"/> can replace it.</summary>
    public required IMovementSource Movement { get; init; }

    /// <summary>Events applied at step boundaries, in list order (portal toggles, fight moves).</summary>
    public IReadOnlyList<IStepEventSource> StepEvents { get; init; } = [];

    /// <summary>The generated map, when the scenario uses one.</summary>
    public MapLayout? Layout { get; init; }

    public FightTable Fights { get; init; } = new();
}

/// <summary>Reads a scenario's <c>scenarioParameters</c> into a typed record, reporting errors by JSON path.</summary>
public static class ScenarioParameters
{
    public const string Path = "scenarioParameters";

    /// <summary>The parameters as <typeparamref name="T"/>, or null with the errors that prevented reading them.</summary>
    public static T? Read<T>(RunConfig config, List<ConfigError> errors)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(errors);
        if (config.ScenarioParameters is null)
        {
            errors.Add(new ConfigError(Path, "is required"));
            return null;
        }

        try
        {
            // A JSON object always deserializes to an instance.
            return config.ScenarioParameters.Deserialize<T>(ConfigJson.SerializerOptions)!;
        }
        catch (JsonException ex)
        {
            errors.Add(new ConfigError(ConfigPaths.FromJsonPath(ex.Path, Path), "has an invalid value or is not a known field"));
            return null;
        }
    }

    /// <summary>The parameters of a configuration that already passed validation.</summary>
    public static T Require<T>(RunConfig config)
        where T : class
    {
        var errors = new List<ConfigError>();
        return Read<T>(config, errors) ?? throw new ConfigValidationException(errors);
    }
}

/// <summary>
/// Builds a world entity by entity. Each entity gets its own movement stream, (seed, "movement", ID),
/// which its model uses both to place it and to move it.
/// </summary>
internal sealed class WorldBuilder
{
    private readonly List<EntitySpec> _entities = [];
    private readonly List<MotionModel> _models = [];
    private readonly List<Func<MovementContext, MotionModel>> _pending = [];

    public WorldBuilder(RunConfig config, MapLayout layout, SectionTopology topology, int airLayerId = -1)
    {
        Config = config;
        Layout = layout;
        Topology = topology;
        AirLayerId = airLayerId;
    }

    public RunConfig Config { get; }

    public MapLayout Layout { get; }

    public SectionTopology Topology { get; }

    /// <summary>The air layer's ID in <see cref="Topology"/>, or −1 when it has none.</summary>
    public int AirLayerId { get; }

    public FightTable Fights { get; } = new();

    /// <summary>Adds an entity whose model is created by <paramref name="model"/> once the context exists.</summary>
    public int Add(EntityClass cls, int fightTag, Func<DeterministicRandom, MovementContext, MotionModel> model)
    {
        var id = _pending.Count;
        var random = DeterministicRandom.ForEntity(Config.Seed, "movement", id);
        _pending.Add(context => model(random, context));
        _entities.Add(new EntitySpec(id, cls, 0, 0, 0, 0, fightTag));
        return id;
    }

    /// <summary>Creates the models, places every entity on its layer and returns the world.</summary>
    public ScenarioWorld Build(IReadOnlyList<IStepEventSource> stepEvents)
    {
        var state = new WorldState(_entities);
        var airLayer = AirLayerId >= 0 ? AirLayerPolicy.From(Config.AirLayer, AirLayerId) : AirLayerPolicy.None;
        var time = TimeBase.From(Config.Scheduling);
        var context = new MovementContext(state, Layout, Fights, Config.Movement, Config.Regionizer.ClaimRadius.CapturePointZone, time, airLayer);
        var specs = new List<EntitySpec>(_entities.Count);
        for (var id = 0; id < _pending.Count; id++)
        {
            var model = _pending[id](context);
            _models.Add(model);
            var (layer, x, y, z) = model.Place();
            state.Layer[id] = layer;
            state.X[id] = x;
            state.Y[id] = y;
            state.Z[id] = z;
            specs.Add(_entities[id] with { Layer = layer, X = x, Y = y, Z = z });
        }

        return new ScenarioWorld
        {
            Topology = Topology,
            Entities = specs,
            State = state,
            Movement = new SyntheticMovement([.. _models], time),
            StepEvents = stepEvents,
            Layout = Layout,
            Fights = Fights,
        };
    }
}
