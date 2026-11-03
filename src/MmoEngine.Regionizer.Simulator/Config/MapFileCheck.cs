using MmoEngine.Maps;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>
/// The <c>map.file</c> check (data-model.md, contracts/map-format.md): a map file named in the
/// configuration must load with MmoEngine.Maps and pass the portal validator before a run starts.
/// A load error or any validator finding is a <see cref="ConfigError"/> on <c>map.file</c> (exit code 2).
/// </summary>
public static class MapFileCheck
{
    /// <summary>The JSON path every map error is reported under.</summary>
    public const string ConfigPath = "map.file";

    /// <summary>
    /// The file a <c>map.file</c> value names: an absolute path as given; a relative one (such as
    /// <c>simulator/maps/stacked-base.json</c>) under the working directory or the nearest directory
    /// above it that holds it, then likewise above the application. Null when none holds it.
    /// </summary>
    public static string? Resolve(string file) => Resolve(file, Environment.CurrentDirectory, AppContext.BaseDirectory);

    /// <summary><see cref="Resolve(string)"/> with the working and application directories given.</summary>
    internal static string? Resolve(string file, string workingDirectory, string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        if (Path.IsPathRooted(file))
        {
            return File.Exists(file) ? Path.GetFullPath(file) : null;
        }

        foreach (var start in new[] { workingDirectory, applicationDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, file);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The errors of the map at <paramref name="file"/>: not found, unreadable, or one per validator finding.</summary>
    public static IReadOnlyList<ConfigError> Validate(string file)
    {
        var resolved = Resolve(file);
        if (resolved is null)
        {
            return [new ConfigError(ConfigPath, $"file '{file}' was not found above the working directory or the tool")];
        }

        MapDocument map;
        try
        {
            map = MapDocument.Load(resolved);
        }
        catch (MapFormatException ex)
        {
            return [new ConfigError(ConfigPath, $"'{file}' is not a valid map: {ex.Message}")];
        }

        return [.. PortalValidator.Validate(map).Select(f => new ConfigError(ConfigPath, $"'{file}' fails the portal validator: {f}"))];
    }
}
