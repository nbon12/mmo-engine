using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Simulator.Sweep;

/// <summary>One swept value of a cell: which parameter, which of its values, and the value.</summary>
/// <param name="ParameterIndex">The parameter's index in <see cref="SweepSpec.Parameters"/>.</param>
/// <param name="Path">The parameter's JSON path.</param>
/// <param name="ValueIndex">The value's index in the parameter's values.</param>
/// <param name="Value">The value.</param>
public sealed record SweepAssignment(int ParameterIndex, string Path, int ValueIndex, JsonNode? Value);

/// <summary>One cell: its ID (<c>c000</c>, <c>c001</c>, ...) and the swept values applied over <c>base</c>.</summary>
public sealed record SweepCell(int Index, string CellId, IReadOnlyList<SweepAssignment> Assignments)
{
    /// <summary>The swept values as run config overrides, JSON path to value.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Paths() =>
        Assignments.ToDictionary(a => a.Path, a => a.Value, StringComparer.Ordinal);
}

/// <summary>One run of a sweep: a cell and a seed, its folder and its validated configuration.</summary>
/// <param name="Index">The run's index in <see cref="SweepPlan.Runs"/> (cell-major, then seed).</param>
/// <param name="Cell">The cell.</param>
/// <param name="SeedIndex">The seed's index, the same in every cell.</param>
/// <param name="Seed">The seed.</param>
/// <param name="Folder">The run folder relative to the sweep folder, <c>&lt;cellId&gt;/seed-&lt;i&gt;</c>.</param>
/// <param name="Loaded">The run's configuration: <c>base</c> with the cell's values and the seed applied.</param>
public sealed record SweepRun(int Index, SweepCell Cell, int SeedIndex, ulong Seed, string Folder, LoadedRunConfig Loaded);

/// <summary>A validated sweep: its cells, seeds and the precomputed run list, in a fixed order.</summary>
/// <param name="Spec">The specification.</param>
/// <param name="Scenario">The base scenario (sweep.json's <c>scenario</c>).</param>
/// <param name="Seeds">Seed <c>i</c> of every cell.</param>
/// <param name="Cells">The cells, in cell ID order.</param>
/// <param name="Runs">Every (cell, seed) run, cell-major.</param>
public sealed record SweepPlan(SweepSpec Spec, string Scenario, IReadOnlyList<ulong> Seeds, IReadOnlyList<SweepCell> Cells, IReadOnlyList<SweepRun> Runs);

/// <summary>The outcome of loading a sweep specification: a plan, or the errors that stop it (exit code 2).</summary>
public sealed record SweepLoadResult(SweepPlan? Plan, IReadOnlyList<ConfigError> Errors)
{
    public bool Succeeded => Plan is not null && Errors.Count == 0;
}

/// <summary>Expands a sweep's parameters into cells and its seed count into seeds.</summary>
public static class SweepCells
{
    /// <summary>
    /// The cells of <paramref name="spec"/>, numbered in a fixed order. One at a time: each
    /// parameter in order, each of its values in order, one cell each. Grid: the cartesian product,
    /// the first parameter slowest and the last fastest.
    /// </summary>
    public static IReadOnlyList<SweepCell> Expand(SweepSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var assignments = new List<SweepAssignment[]>();
        if (spec.Mode == SweepMode.Grid)
        {
            var counters = new int[spec.Parameters.Count];
            while (true)
            {
                assignments.Add([.. counters.Select((v, p) => Assign(spec, p, v))]);
                var p = counters.Length - 1;
                while (p >= 0 && ++counters[p] == spec.Parameters[p].Values.Count)
                {
                    counters[p] = 0;
                    p--;
                }

                if (p < 0)
                {
                    break;
                }
            }
        }
        else
        {
            for (var p = 0; p < spec.Parameters.Count; p++)
            {
                for (var v = 0; v < spec.Parameters[p].Values.Count; v++)
                {
                    assignments.Add([Assign(spec, p, v)]);
                }
            }
        }

        return [.. assignments.Select((a, i) => new SweepCell(i, CellId(i), a))];
    }

    /// <summary>The ID of cell <paramref name="index"/>: <c>c000</c>, <c>c001</c>, ...</summary>
    public static string CellId(int index) => "c" + index.ToString("000", CultureInfo.InvariantCulture);

    /// <summary>Seed <c>i</c> of every cell, <see cref="DeterministicRandom.SweepSeed"/>: common random numbers across cells.</summary>
    public static IReadOnlyList<ulong> Seeds(SweepSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return [.. Enumerable.Range(0, spec.Seeds).Select(i => DeterministicRandom.SweepSeed(spec.SweepSeed, i))];
    }

    private static SweepAssignment Assign(SweepSpec spec, int parameter, int value) =>
        new(parameter, spec.Parameters[parameter].Path, value, spec.Parameters[parameter].Values[value]);
}

/// <summary>
/// Loads a sweep specification (contracts/config-and-cli.md): reads the JSON, validates its fields,
/// expands its cells and seeds, and loads and validates every run's configuration before anything
/// runs (FR-026). Errors are keyed by their JSON path in the specification: a swept value that makes
/// a run invalid is reported at <c>parameters[i].values[j]</c>, anything else in the base at
/// <c>base.&lt;path&gt;</c>.
/// </summary>
public static class SweepSpecLoader
{
    /// <summary>Loads the sweep specification file at <paramref name="path"/>.</summary>
    /// <param name="path">The sweep JSON file.</param>
    /// <param name="environmentName">The raw <c>DOTNET_ENVIRONMENT</c> value (null when unset).</param>
    /// <param name="scenariosDirectory">Where the scenario defaults live, or null to search for them.</param>
    public static SweepLoadResult LoadFile(string path, string? environmentName = null, string? scenariosDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return Fail(new ConfigError("sweep", $"file '{path}' does not exist"));
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

        return node is JsonObject spec
            ? Load(spec, environmentName, scenariosDirectory)
            : Fail(new ConfigError("sweep", "must be a JSON object"));
    }

    /// <summary>Loads a sweep specification given as a JSON object.</summary>
    public static SweepLoadResult Load(JsonObject spec, string? environmentName = null, string? scenariosDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        SweepSpec typed;
        try
        {
            typed = spec.Deserialize<SweepSpec>(ConfigJson.SerializerOptions)!;
        }
        catch (JsonException ex)
        {
            return Fail(new ConfigError(ConfigPaths.FromJsonPath(ex.Path), $"has an invalid value or is not a known field ({FirstLine(ex.Message)})"));
        }

        var specErrors = SweepSpecValidator.Errors(typed);
        return specErrors.Count > 0 ? new SweepLoadResult(null, specErrors) : Plan(typed, environmentName, scenariosDirectory);
    }

    private static SweepLoadResult Plan(SweepSpec spec, string? environmentName, string? scenariosDirectory)
    {
        var cells = SweepCells.Expand(spec);
        var seeds = SweepCells.Seeds(spec);
        var errors = new List<ConfigError>();
        var runs = new List<SweepRun>(cells.Count * seeds.Count);
        foreach (var cell in cells)
        {
            var paths = cell.Paths();
            for (var i = 0; i < seeds.Count; i++)
            {
                var load = RunConfigLoader.Load(spec.Base, new RunConfigOverrides(seeds[i], null, paths), environmentName, scenariosDirectory);
                if (!load.Succeeded)
                {
                    foreach (var error in load.Errors)
                    {
                        var mapped = Map(error, cell);
                        if (!errors.Contains(mapped))
                        {
                            errors.Add(mapped);
                        }
                    }

                    break;
                }

                runs.Add(new SweepRun(runs.Count, cell, i, seeds[i], $"{cell.CellId}/seed-{i}", load.Loaded!));
            }
        }

        if (errors.Count > 0)
        {
            return new SweepLoadResult(null, errors);
        }

        var scenario = spec.Base["scenario"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : runs[0].Loaded.Config.Scenario;
        return new SweepLoadResult(new SweepPlan(spec, scenario, seeds, cells, runs), []);
    }

    /// <summary>Keys a run configuration error by where it comes from in the sweep specification.</summary>
    private static ConfigError Map(ConfigError error, SweepCell cell)
    {
        foreach (var a in cell.Assignments)
        {
            if (error.Path == a.Path || error.Path.StartsWith(a.Path + ".", StringComparison.Ordinal) || error.Path.StartsWith(a.Path + "[", StringComparison.Ordinal))
            {
                return new ConfigError(
                    string.Create(CultureInfo.InvariantCulture, $"parameters[{a.ParameterIndex}].values[{a.ValueIndex}]"),
                    $"{error.Path} = {ConfigJson.Canonical(a.Value)}: {error.Message}");
            }
        }

        return error.Path switch
        {
            EnvironmentNames.VariableName => error,
            "config" => error with { Path = "base" },
            _ => error with { Path = "base." + error.Path },
        };
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    private static SweepLoadResult Fail(ConfigError error) => new(null, [error]);
}
