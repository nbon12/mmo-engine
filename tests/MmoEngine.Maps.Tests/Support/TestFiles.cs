namespace MmoEngine.Maps.Tests.Support;

/// <summary>Paths to the repository's map files, and a unique temp folder per test.</summary>
internal static class TestFiles
{
    private static readonly Lazy<string> Root = new(FindRepositoryRoot);

    /// <summary>The folder holding global.json, found by walking up from the test binaries.</summary>
    public static string RepositoryRoot => Root.Value;

    /// <summary>A file under simulator/maps, for example <c>test/clean.json</c>.</summary>
    public static string Map(string relativePath) => Path.Combine(RepositoryRoot, "simulator", "maps", relativePath);

    public static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mmo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No folder above {AppContext.BaseDirectory} holds global.json.");
    }
}
