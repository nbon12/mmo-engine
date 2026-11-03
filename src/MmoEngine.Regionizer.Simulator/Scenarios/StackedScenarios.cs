using MmoEngine.Maps;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>The stacked base's fights: a surface fight above an underground fight in a layer of a map file.</summary>
public interface IStackedBaseParameters
{
    /// <summary>Players in the surface fight, centred above the underground layer.</summary>
    int SurfaceFight { get; }

    /// <summary>Players in the underground fight, all infantry, kept inside the underground layer.</summary>
    int UndergroundFight { get; }

    /// <summary>The map layer the underground fight stands on (not the surface, layer 0).</summary>
    int UndergroundLayer { get; }

    /// <summary>The surface fight's composition (the underground fight is infantry only).</summary>
    CompositionParameters Composition { get; }

    double FightRadiusPerSqrtPlayer { get; }

    int ObjectivesPerBase { get; }

    double ObjectiveSpreadMetres { get; }
}

/// <summary><c>scenarioParameters</c> of Stacked areas.</summary>
public sealed record StackedAreasParameters : IStackedBaseParameters
{
    public int SurfaceFight { get; init; }

    public int UndergroundFight { get; init; }

    public int UndergroundLayer { get; init; } = 1;

    public CompositionParameters Composition { get; init; } = null!;

    public double FightRadiusPerSqrtPlayer { get; init; }

    public int ObjectivesPerBase { get; init; }

    public double ObjectiveSpreadMetres { get; init; }
}

/// <summary><c>scenarioParameters</c> of Soak: Migration's, plus the stacked base's fights.</summary>
public sealed record SoakParameters : MigrationParameters, IStackedBaseParameters
{
    public int SurfaceFight { get; init; }

    public int UndergroundFight { get; init; }

    public int UndergroundLayer { get; init; } = 1;
}

/// <summary>
/// Stacked areas (data-model.md): the map file <c>map.file</c> (by default
/// <c>simulator/maps/stacked-base.json</c>), loaded through MmoEngine.Maps and refused unless the
/// portal validator accepts it. A surface fight stands above an underground fight in the bunker
/// layer; the bunker's doors are the map's portals, opened and closed by its <c>portalSchedule</c>
/// (every 60 s, open 20 s), starting from each portal's state in the file. The map is centred in the
/// open world of <c>map.widthMetres</c> × <c>map.heightMetres</c>, which defaults to the map's own size.
/// </summary>
public sealed class StackedAreasScenario : IScenario
{
    public string Name => ScenarioNames.StackedAreas;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var p = ScenarioParameters.Read<StackedAreasParameters>(config, errors);
        if (p is null)
        {
            return errors;
        }

        StackedBase.ValidateFights(p, errors);
        LaneMapWorld.ValidateFightShape(p.Composition, p.FightRadiusPerSqrtPlayer, p.ObjectivesPerBase, p.ObjectiveSpreadMetres, errors);
        if (errors.Count == 0 && StackedBase.Load(config, p.UndergroundLayer, errors) is not null)
        {
            LaneMapWorld.CheckEntityLimit(config, 2L * p.ObjectivesPerBase, errors);
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var p = ScenarioParameters.Require<StackedAreasParameters>(config);
        var stacked = StackedBase.Require(config, p.UndergroundLayer);
        var (topology, airLayer) = stacked.BuildTopology(config);
        var layout = MapLayout.Open(config.Map.WidthMetres, config.Map.HeightMetres);
        var (fights, _) = PopulationScaler.Scale([p.SurfaceFight, p.UndergroundFight], [], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        var bases = stacked.AddFights(builder, p, fights[0], fights[1], firstBaseIndex: 0);
        LaneMapWorld.AddCapturePoints(builder, bases);
        return builder.Build(stacked.PortalSchedule());
    }
}

/// <summary>
/// Soak (data-model.md): Migration, Stacked areas and Air traffic at once, in interleaved mode, for
/// 24 simulated hours by default with metrics every 30 ticks. The lane map's fights migrate, the
/// stacked base from <c>map.file</c> is centred in the lane map with its surface and underground
/// fights (which stay put) and its door schedule, and aircraft use the air layer
/// (<c>airLayer.enabled</c>). Fight tags: the lane fights first, then the surface and underground fights.
/// </summary>
public sealed class SoakScenario : IScenario
{
    public string Name => ScenarioNames.Soak;

    public IEnumerable<ConfigError> Validate(RunConfig config)
    {
        var errors = new List<ConfigError>();
        var p = ScenarioParameters.Read<SoakParameters>(config, errors);
        if (p is null)
        {
            return errors;
        }

        StackedBase.ValidateFights(p, errors);
        LaneMapWorld.Validate(config, p, errors, extraEntities: 2L * p.ObjectivesPerBase);
        MigrationScenario.ValidateMigration(p, errors);
        if (errors.Count == 0)
        {
            StackedBase.Load(config, p.UndergroundLayer, errors);
        }

        return errors;
    }

    public ScenarioWorld Build(RunConfig config)
    {
        var p = ScenarioParameters.Require<SoakParameters>(config);
        var stacked = StackedBase.Require(config, p.UndergroundLayer);
        var (topology, airLayer) = stacked.BuildTopology(config);
        var layout = LaneMapWorld.GenerateLayout(config, p);
        var laneFights = p.Fights.Length;
        var (fights, others) = PopulationScaler.Scale([.. p.Fights, p.SurfaceFight, p.UndergroundFight], [p.Travellers, p.PointToPointAircraft], config.Population.Total);
        var builder = new WorldBuilder(config, layout, topology, airLayer);
        LaneMapWorld.AddFights(builder, p, fights[..laneFights]);
        var stackedBases = stacked.AddFights(builder, p, fights[laneFights], fights[laneFights + 1], firstBaseIndex: layout.Bases.Count);
        LaneMapWorld.AddTravellers(builder, others[0], layout.Lanes);
        LaneMapWorld.AddPointToPointAircraft(builder, others[1]);
        LaneMapWorld.AddCapturePoints(builder, layout.Bases.Concat(stackedBases));
        var migration = MigrationScenario.CreateEvents(config, p, builder.Fights.Fights.Take(laneFights), layout);
        return builder.Build([migration, .. stacked.PortalSchedule()]);
    }
}

/// <summary>
/// A map file's stacked base, loaded, validated and embedded in the open world: the map's layers keep
/// their relative positions and are shifted together by a whole number of sections so that their
/// bounding box is centred in <c>map.widthMetres</c> × <c>map.heightMetres</c>. Layer 0 of the world
/// is the open world with the map's surface layer painted into it (outside it, the surface's default
/// area); the map's other layers and portals are shifted the same way; the air layer, when enabled,
/// takes the next free layer ID.
/// </summary>
internal sealed class StackedBase
{
    private const string P = ScenarioParameters.Path;

    private StackedBase(MapDocument map, int offsetX, int offsetY, MapLayer underground, long[] scheduleSteps)
    {
        Map = map;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Underground = underground;
        ScheduleSteps = scheduleSteps;
        AirLayerId = map.Layers.Max(l => l.Id) + 1;
    }

    public MapDocument Map { get; }

    /// <summary>The shift of every map section into the world, in sections.</summary>
    public int OffsetX { get; }

    public int OffsetY { get; }

    public MapLayer Underground { get; }

    /// <summary>The air layer's ID when it is enabled: one more than the map's highest layer ID.</summary>
    public int AirLayerId { get; }

    /// <summary>Each <c>portalSchedule</c> entry's cycle length in base steps.</summary>
    private long[] ScheduleSteps { get; }

    /// <summary>Fight sizes must not be negative.</summary>
    public static void ValidateFights(IStackedBaseParameters p, List<ConfigError> errors)
    {
        if (p.SurfaceFight < 0)
        {
            errors.Add(new ConfigError($"{P}.surfaceFight", "must be at least 0"));
        }

        if (p.UndergroundFight < 0)
        {
            errors.Add(new ConfigError($"{P}.undergroundFight", "must be at least 0"));
        }
    }

    /// <summary>
    /// Loads <c>map.file</c> and checks it: it must exist and parse, the portal validator must report
    /// nothing, its section size must equal <c>regionizer.sectionSize</c>, it needs a surface layer 0
    /// and the underground layer, it must fit in the world, and each schedule cycle must last at least
    /// one base step. Returns null after adding the first error found.
    /// </summary>
    public static StackedBase? Load(RunConfig config, int undergroundLayer, List<ConfigError> errors)
    {
        var path = config.Map.File;
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(new ConfigError("map.file", $"is required: the {config.Scenario} scenario runs on a map file such as simulator/maps/stacked-base.json"));
            return null;
        }

        var resolved = MapFiles.Resolve(path);
        if (resolved is null)
        {
            errors.Add(new ConfigError("map.file", $"file '{path}' was not found in the working directory, the application directory or above them"));
            return null;
        }

        MapDocument map;
        try
        {
            map = MapDocument.Load(resolved);
        }
        catch (MapFormatException ex)
        {
            errors.Add(new ConfigError("map.file", $"could not be read: {ex.Message}"));
            return null;
        }

        var findings = PortalValidator.Validate(map);
        if (findings.Count > 0)
        {
            var shown = string.Join("; ", findings.Take(5));
            var more = findings.Count > 5 ? $"; and {findings.Count - 5} more" : string.Empty;
            errors.Add(new ConfigError("map.file", $"the portal validator rejects '{path}' with {findings.Count} finding(s): {shown}{more}"));
            return null;
        }

        var s = config.Regionizer.SectionSize;
        if (map.SectionSize != s)
        {
            errors.Add(new ConfigError("regionizer.sectionSize", $"must equal the section size of map file '{path}' ({map.SectionSize}), was {s}"));
            return null;
        }

        if (!map.TryGetLayer(0, out _))
        {
            errors.Add(new ConfigError("map.file", $"'{path}' has no layer 0, the surface that the open world extends"));
            return null;
        }

        if (undergroundLayer == 0 || !map.TryGetLayer(undergroundLayer, out var underground))
        {
            errors.Add(new ConfigError($"{P}.undergroundLayer", $"must name a layer of map file '{path}' other than the surface (layer 0), was {undergroundLayer}"));
            return null;
        }

        var minX = map.Layers.Min(l => (long)l.OriginX);
        var minY = map.Layers.Min(l => (long)l.OriginY);
        var spanX = map.Layers.Max(l => (long)l.OriginX + l.Width) - minX;
        var spanY = map.Layers.Max(l => (long)l.OriginY + l.Height) - minY;
        var (w, h) = WorldSections(config);
        if (spanX > w)
        {
            errors.Add(new ConfigError("map.widthMetres", $"must be at least {spanX * s} m to hold map file '{path}', was {config.Map.WidthMetres}"));
        }

        if (spanY > h)
        {
            errors.Add(new ConfigError("map.heightMetres", $"must be at least {spanY * s} m to hold map file '{path}', was {config.Map.HeightMetres}"));
        }

        var steps = new long[map.PortalSchedule.Count];
        for (var i = 0; i < steps.Length; i++)
        {
            steps[i] = TimeBase.SecondsToSteps(map.PortalSchedule[i].EverySeconds);
            if (steps[i] < 1)
            {
                errors.Add(new ConfigError("map.file", $"portalSchedule[{i}].everySeconds ({map.PortalSchedule[i].EverySeconds}) of '{path}' is shorter than one base step"));
            }
        }

        if (spanX > w || spanY > h || steps.Any(x => x < 1))
        {
            return null;
        }

        return new StackedBase(map, (int)(((w - spanX) / 2) - minX), (int)(((h - spanY) / 2) - minY), underground, steps);
    }

    /// <summary>The stacked base of a configuration that already passed validation.</summary>
    public static StackedBase Require(RunConfig config, int undergroundLayer)
    {
        var errors = new List<ConfigError>();
        return Load(config, undergroundLayer, errors) ?? throw new ConfigValidationException(errors);
    }

    /// <summary>The world topology (see the class summary) and the air layer's ID, or −1 without one.</summary>
    public (SectionTopology Topology, int AirLayerId) BuildTopology(RunConfig config)
    {
        var (w, h) = WorldSections(config);
        var surface = Map.Layers.First(l => l.Id == 0);
        var surfaceTags = new ushort[w * h];
        Array.Fill(surfaceTags, Map.AreaIdOf(surface.DefaultArea));
        for (var y = surface.OriginY; y < surface.OriginY + surface.Height; y++)
        {
            for (var x = surface.OriginX; x < surface.OriginX + surface.Width; x++)
            {
                surfaceTags[((y + OffsetY) * w) + x + OffsetX] = surface.AreaIdAt(x, y);
            }
        }

        var layers = new List<LayerSpec> { new(0, 0, 0, w, h, surfaceTags) };
        foreach (var layer in Map.Layers)
        {
            if (layer.Id == 0)
            {
                continue;
            }

            var tags = new ushort[layer.Width * layer.Height];
            for (var y = 0; y < layer.Height; y++)
            {
                for (var x = 0; x < layer.Width; x++)
                {
                    tags[(y * layer.Width) + x] = layer.AreaIdAt(layer.OriginX + x, layer.OriginY + y);
                }
            }

            layers.Add(new LayerSpec(layer.Id, layer.OriginX + OffsetX, layer.OriginY + OffsetY, layer.Width, layer.Height, tags));
        }

        var airLayer = -1;
        if (config.AirLayer.Enabled)
        {
            airLayer = AirLayerId;
            layers.Add(new LayerSpec(airLayer, 0, 0, w, h, new ushort[w * h]) { AllowsDirectEntry = true });
        }

        var portals = Map.Portals.Select(p => new PortalSpec(p.Id, Shift(p.A), Shift(p.B), p.Open)).ToList();
        return (SectionTopology.Build(Map.SectionSize, layers, portals), airLayer);
    }

    /// <summary>The underground layer in world metres, kept strictly inside its far edges.</summary>
    public MetreBounds UndergroundBounds()
    {
        var s = Map.SectionSize;
        var x0 = (double)(Underground.OriginX + OffsetX) * s;
        var y0 = (double)(Underground.OriginY + OffsetY) * s;
        return new MetreBounds(x0, y0, x0 + (Underground.Width * s) - EdgeMargin, y0 + (Underground.Height * s) - EdgeMargin);
    }

    /// <summary>
    /// Adds the surface fight (composition as configured) and the underground fight (infantry only),
    /// both centred on the underground layer, with one base each whose objectives are drawn from the
    /// "stacked-layout" stream. Fight indices and tags follow the fights already added. Returns the two
    /// bases, for their capture points.
    /// </summary>
    public Base[] AddFights(WorldBuilder builder, IStackedBaseParameters p, int surfacePlayers, int undergroundPlayers, int firstBaseIndex)
    {
        var random = DeterministicRandom.ForStream(builder.Config.Seed, "stacked-layout");
        var bunker = UndergroundBounds();
        var world = builder.Layout.Bounds;
        var s = Map.SectionSize;
        var cx = (Underground.OriginX + OffsetX + (Underground.Width / 2.0)) * s;
        var cy = (Underground.OriginY + OffsetY + (Underground.Height / 2.0)) * s;
        var surfaceBase = new Base(firstBaseIndex, -1, 0, cx, cy, Objectives(random, p, cx, cy, world), 0);
        var bunkerBase = new Base(firstBaseIndex + 1, -1, 0, cx, cy, Objectives(random, p, cx, cy, bunker), Underground.Id);
        var index = builder.Fights.Fights.Count;
        var r = p.FightRadiusPerSqrtPlayer;
        LaneMapWorld.AddFight(builder, new Fight(index, index, surfaceBase, surfacePlayers, r * Math.Sqrt(surfacePlayers)), p.Composition);
        LaneMapWorld.AddFight(
            builder,
            new Fight(index + 1, index + 1, bunkerBase, undergroundPlayers, r * Math.Sqrt(undergroundPlayers), bunker),
            new CompositionParameters { Infantry = 1 });
        return [surfaceBase, bunkerBase];
    }

    /// <summary>The door cycles of the map's <c>portalSchedule</c>, in file order.</summary>
    public IStepEventSource[] PortalSchedule()
    {
        var events = new IStepEventSource[Map.PortalSchedule.Count];
        for (var i = 0; i < events.Length; i++)
        {
            var entry = Map.PortalSchedule[i];
            var initiallyOpen = Map.Portals.First(p => p.Id == entry.PortalId).Open;
            events[i] = new MapPortalCycle(entry.PortalId, ScheduleSteps[i], TimeBase.SecondsToSteps(entry.OpenSeconds), initiallyOpen);
        }

        return events;
    }

    private const double EdgeMargin = 1e-3;

    /// <summary>The open world's size in sections, as <see cref="SectionTopology.OpenWorld"/> computes it (map sides are validated to 20 km).</summary>
    private static (int W, int H) WorldSections(RunConfig config)
    {
        var s = config.Regionizer.SectionSize;
        return ((int)Math.Ceiling(config.Map.WidthMetres / s), (int)Math.Ceiling(config.Map.HeightMetres / s));
    }

    private static List<(double X, double Y)> Objectives(DeterministicRandom random, IStackedBaseParameters p, double cx, double cy, MetreBounds bounds)
    {
        var objectives = new List<(double X, double Y)>(p.ObjectivesPerBase);
        for (var o = 0; o < p.ObjectivesPerBase; o++)
        {
            var (dx, dy) = random.UnitDisc();
            objectives.Add(bounds.Clamp(cx + (dx * p.ObjectiveSpreadMetres), cy + (dy * p.ObjectiveSpreadMetres)));
        }

        return objectives;
    }

    private SectionKey Shift(SectionKey key) => key with { X = key.X + OffsetX, Y = key.Y + OffsetY };
}

/// <summary>
/// Opens and closes a map portal on its <c>portalSchedule</c> cycle: open for <c>openSteps</c> of every
/// <c>everySteps</c>. The cycle starts in the portal's initial state from the map: a door that starts
/// closed stays closed for the first <c>everySteps − openSteps</c> steps, then opens. (Scheduling's
/// <see cref="PortalCycleEvents"/> always starts a cycle open.) A cycle open for none of its steps keeps
/// the portal closed, and one open for all of them keeps it open.
/// </summary>
public sealed class MapPortalCycle : IStepEventSource
{
    private readonly long _everySteps;
    private readonly long _openSteps;
    private readonly long _openPhase;
    private readonly long _closePhase;

    public MapPortalCycle(int portalId, long everySteps, long openSteps, bool initiallyOpen)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(everySteps, 1);
        PortalId = portalId;
        _everySteps = everySteps;
        _openSteps = Math.Clamp(openSteps, 0, everySteps);
        _openPhase = initiallyOpen ? 0 : everySteps - _openSteps;
        _closePhase = (_openPhase + _openSteps) % everySteps;
    }

    public int PortalId { get; }

    public void OnStepBoundary(long step, IStepBoundaryActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (_openSteps == 0 || _openSteps == _everySteps)
        {
            if (step == 0)
            {
                actions.SetPortalOpen(PortalId, _openSteps == _everySteps);
            }

            return;
        }

        var phase = step % _everySteps;
        if (phase == _openPhase)
        {
            actions.SetPortalOpen(PortalId, true);
        }
        else if (phase == _closePhase)
        {
            actions.SetPortalOpen(PortalId, false);
        }
    }
}

/// <summary>
/// Finds a map file named in a configuration. A relative path is looked up from the working directory
/// and each directory above it, then from the application directory and above, so
/// <c>simulator/maps/stacked-base.json</c> resolves from anywhere inside the repository.
/// </summary>
public static class MapFiles
{
    /// <summary>The full path of an existing map file, or null when none is found.</summary>
    public static string? Resolve(string path) => Resolve(path, Environment.CurrentDirectory, AppContext.BaseDirectory);

    /// <summary><see cref="Resolve(string)"/> with the working and application directories given.</summary>
    public static string? Resolve(string path, string workingDirectory, string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            return File.Exists(path) ? path : null;
        }

        return SearchUp(workingDirectory, path) ?? SearchUp(applicationDirectory, path);
    }

    private static string? SearchUp(string start, string relative)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
