using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>Values applied over the merged configuration, after the scenario defaults and the run file.</summary>
/// <param name="Seed">The CLI's <c>--seed</c>.</param>
/// <param name="DurationTicks">The CLI's <c>--ticks</c>.</param>
/// <param name="Paths">Dotted JSON paths and their values, applied last (a sweep cell's parameters).</param>
public sealed record RunConfigOverrides(ulong? Seed = null, long? DurationTicks = null, IReadOnlyDictionary<string, JsonNode?>? Paths = null);

/// <summary>A configuration that passed validation, with its canonical form and hash (research D12).</summary>
/// <param name="Config">The typed configuration.</param>
/// <param name="CanonicalJson">The effective configuration as canonical JSON.</param>
/// <param name="ConfigHash">XxHash64 of <paramref name="CanonicalJson"/>, 16 hex digits.</param>
/// <param name="EnvironmentName">The validated <c>DOTNET_ENVIRONMENT</c>.</param>
public sealed record LoadedRunConfig(RunConfig Config, string CanonicalJson, string ConfigHash, string EnvironmentName);

/// <summary>The outcome of loading a configuration: either a loaded config or the errors that stop the run.</summary>
public sealed record ConfigLoadResult(LoadedRunConfig? Loaded, IReadOnlyList<ConfigError> Errors)
{
    public bool Succeeded => Loaded is not null && Errors.Count == 0;
}

/// <summary>
/// Loads a run configuration: reads the run file, deep-merges it over the scenario's defaults in
/// <c>simulator/scenarios/&lt;scenario&gt;.json</c>, applies overrides, binds it to <see cref="RunConfig"/>
/// and validates it (FR-026). Every error names its JSON path.
/// </summary>
public static class RunConfigLoader
{
    /// <summary>Loads and validates the run config file at <paramref name="path"/>.</summary>
    /// <param name="path">The run config JSON file.</param>
    /// <param name="overrides">Values applied last, or null.</param>
    /// <param name="environmentName">The raw <c>DOTNET_ENVIRONMENT</c> value (null when unset).</param>
    /// <param name="scenariosDirectory">Where the scenario defaults live, or null to search for them.</param>
    public static ConfigLoadResult LoadFile(string path, RunConfigOverrides? overrides = null, string? environmentName = null, string? scenariosDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return Fail(new ConfigError("config", $"file '{path}' does not exist"));
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return Fail(new ConfigError(ConfigPaths.FromJsonPath(ex.Path), $"is not valid JSON ({ex.Message})"));
        }

        if (node is not JsonObject runConfig)
        {
            return Fail(new ConfigError("config", "must be a JSON object"));
        }

        return Load(runConfig, overrides, environmentName, scenariosDirectory);
    }

    /// <summary>Loads and validates a run config given as a JSON object.</summary>
    public static ConfigLoadResult Load(JsonObject runConfig, RunConfigOverrides? overrides = null, string? environmentName = null, string? scenariosDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(runConfig);
        var errors = new List<ConfigError>();
        var (environment, environmentError) = EnvironmentNames.Resolve(environmentName);
        if (environmentError is not null)
        {
            errors.Add(environmentError);
        }

        var scenario = ScenarioName(runConfig, overrides);
        if (scenario is null || !ScenarioNames.IsKnown(scenario))
        {
            errors.Add(new ConfigError("scenario", $"must be one of {string.Join(", ", ScenarioNames.All)}, was '{scenario}'"));
            return new ConfigLoadResult(null, errors);
        }

        var defaults = ScenarioDefaults.TryLoad(scenario, scenariosDirectory, out var defaultsError);
        if (defaults is null)
        {
            errors.Add(defaultsError!);
            return new ConfigLoadResult(null, errors);
        }

        var merged = ConfigJson.DeepMerge(defaults, runConfig);
        Apply(merged, overrides);

        RunConfig config;
        try
        {
            config = merged.Deserialize<RunConfig>(ConfigJson.SerializerOptions)!;
        }
        catch (JsonException ex)
        {
            errors.Add(new ConfigError(ConfigPaths.FromJsonPath(ex.Path), $"has an invalid value or is not a known field ({FirstLine(ex.Message)})"));
            return new ConfigLoadResult(null, errors);
        }

        errors.AddRange(Validate(config));
        if (errors.Count > 0)
        {
            return new ConfigLoadResult(null, errors);
        }

        var canonical = ConfigJson.Canonical(config);
        return new ConfigLoadResult(new LoadedRunConfig(config, canonical, ConfigJson.Hash(canonical), environment), []);
    }

    /// <summary>Every error in a typed configuration: the common rules, then the scenario's own.</summary>
    public static IReadOnlyList<ConfigError> Validate(RunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<ConfigError>(RunConfigValidator.Errors(config));
        if (errors.Count == 0 && ScenarioRegistry.TryCreate(config.Scenario, out var scenario))
        {
            errors.AddRange(scenario.Validate(config));
        }

        return errors;
    }

    /// <summary>Wraps a typed configuration that already passed <see cref="Validate"/>.</summary>
    public static LoadedRunConfig FromConfig(RunConfig config, string? environmentName = null)
    {
        var errors = new List<ConfigError>(Validate(config));
        var (environment, environmentError) = EnvironmentNames.Resolve(environmentName);
        if (environmentError is not null)
        {
            errors.Add(environmentError);
        }

        if (errors.Count > 0)
        {
            throw new ConfigValidationException(errors);
        }

        var canonical = ConfigJson.Canonical(config);
        return new LoadedRunConfig(config, canonical, ConfigJson.Hash(canonical), environment);
    }

    private static string? ScenarioName(JsonObject runConfig, RunConfigOverrides? overrides)
    {
        if (overrides?.Paths is { } paths && paths.TryGetValue("scenario", out var overridden))
        {
            return overridden is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }

        return runConfig["scenario"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;
    }

    private static void Apply(JsonObject merged, RunConfigOverrides? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        if (overrides.Seed is { } seed)
        {
            merged["seed"] = seed;
        }

        if (overrides.DurationTicks is { } ticks)
        {
            merged["durationTicks"] = ticks;
        }

        if (overrides.Paths is { } paths)
        {
            foreach (var (path, value) in paths.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                ConfigJson.SetPath(merged, path, value);
            }
        }
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    private static ConfigLoadResult Fail(ConfigError error) => new(null, [error]);
}

/// <summary>Finds and reads the scenario defaults files (<c>simulator/scenarios/&lt;scenario&gt;.json</c>).</summary>
public static class ScenarioDefaults
{
    /// <summary>
    /// The defaults directory: <paramref name="explicitDirectory"/> when given; otherwise a
    /// <c>scenarios</c> folder next to the application (the CLI copies them there), or the nearest
    /// <c>simulator/scenarios</c> above the working directory or the application.
    /// </summary>
    public static string? Locate(string? explicitDirectory = null) =>
        Locate(explicitDirectory, AppContext.BaseDirectory, Environment.CurrentDirectory);

    /// <summary><see cref="Locate(string?)"/> with the application and working directories given.</summary>
    internal static string? Locate(string? explicitDirectory, string applicationDirectory, string workingDirectory)
    {
        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            return Directory.Exists(explicitDirectory) ? explicitDirectory : null;
        }

        var beside = Path.Combine(applicationDirectory, "scenarios");
        if (File.Exists(Path.Combine(beside, "Baseline.json")))
        {
            return beside;
        }

        return SearchUp(workingDirectory) ?? SearchUp(applicationDirectory);
    }

    /// <summary>The defaults of <paramref name="scenario"/> as a JSON object, or null with an error.</summary>
    public static JsonObject? TryLoad(string scenario, string? explicitDirectory, out ConfigError? error)
    {
        var directory = Locate(explicitDirectory);
        if (directory is null)
        {
            error = new ConfigError("scenario", "the scenario defaults directory (simulator/scenarios) was not found");
            return null;
        }

        var file = Path.Combine(directory, scenario + ".json");
        if (!File.Exists(file))
        {
            error = new ConfigError("scenario", $"has no defaults file '{file}'");
            return null;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject obj)
            {
                error = null;
                return obj;
            }
        }
        catch (JsonException ex)
        {
            error = new ConfigError("scenario", $"defaults file '{file}' is not valid JSON ({ex.Message})");
            return null;
        }

        error = new ConfigError("scenario", $"defaults file '{file}' must hold a JSON object");
        return null;
    }

    private static string? SearchUp(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "simulator", "scenarios");
            if (File.Exists(Path.Combine(candidate, "Baseline.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}
