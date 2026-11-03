using MmoEngine.Regionizer.Simulator.Config;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>
/// A named setup of map, fights, lanes, population and movement that a run executes (FR-023).
/// Register an implementation in <see cref="ScenarioRegistry"/>; its defaults live in
/// <c>simulator/scenarios/&lt;Name&gt;.json</c>.
/// </summary>
public interface IScenario
{
    /// <summary>One of <see cref="ScenarioNames.All"/>.</summary>
    string Name { get; }

    /// <summary>
    /// Validates the scenario's own values (mostly <c>scenarioParameters</c>), keyed by JSON path. Called
    /// only after the common configuration passed validation.
    /// </summary>
    IEnumerable<ConfigError> Validate(RunConfig config);

    /// <summary>
    /// Builds the world for a validated configuration: topology, entities in ascending ID order,
    /// movement and step-boundary events. Must be deterministic in the configuration and its seed.
    /// </summary>
    ScenarioWorld Build(RunConfig config);
}

/// <summary>The nine scenario names (FR-023), which are also the defaults file names.</summary>
public static class ScenarioNames
{
    public const string Baseline = "Baseline";
    public const string Spacing = "Spacing";
    public const string LaneTraffic = "LaneTraffic";
    public const string AirTraffic = "AirTraffic";
    public const string VehicleColumns = "VehicleColumns";
    public const string StackedAreas = "StackedAreas";
    public const string Migration = "Migration";
    public const string OneBase = "OneBase";
    public const string Soak = "Soak";

    public static IReadOnlyList<string> All { get; } =
        [Baseline, Spacing, LaneTraffic, AirTraffic, VehicleColumns, StackedAreas, Migration, OneBase, Soak];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);
}

/// <summary>
/// The scenario implementations, keyed by name, one for each of <see cref="ScenarioNames.All"/>.
/// Adding a scenario means adding its name there, its defaults file and its line here.
/// </summary>
public static class ScenarioRegistry
{
    private static readonly Dictionary<string, Func<IScenario>> Factories = new(StringComparer.Ordinal)
    {
        [ScenarioNames.Baseline] = () => new BaselineScenario(),
        [ScenarioNames.Spacing] = () => new SpacingScenario(),
        [ScenarioNames.LaneTraffic] = () => new LaneTrafficScenario(),
        [ScenarioNames.AirTraffic] = () => new AirTrafficScenario(),
        [ScenarioNames.VehicleColumns] = () => new VehicleColumnsScenario(),
        [ScenarioNames.StackedAreas] = () => new StackedAreasScenario(),
        [ScenarioNames.Migration] = () => new MigrationScenario(),
        [ScenarioNames.OneBase] = () => new OneBaseScenario(),
        [ScenarioNames.Soak] = () => new SoakScenario(),
    };

    /// <summary>A new instance of the named scenario.</summary>
    public static IScenario Create(string name) =>
        TryCreate(name, out var scenario) ? scenario : throw new KeyNotFoundException($"Unknown scenario '{name}'.");

    public static bool TryCreate(string? name, out IScenario scenario)
    {
        if (name is not null && Factories.TryGetValue(name, out var factory))
        {
            scenario = factory();
            return true;
        }

        scenario = null!;
        return false;
    }
}
