using System.Text.RegularExpressions;
using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Architecture;

/// <summary>
/// The simulated path stays deterministic and lock-free (FR-016, FR-019, research D8, constitution
/// Principle II): the core library, the map library and every simulator folder except <c>Output/</c> and
/// <c>Analysis/</c> use no transcendental or single-precision math, no locks, no wall clock and no
/// System.Random. The run's wall-clock reads (manifest times, wall-clock seconds and the bookkeeping
/// meter) live in <c>Output/</c>; <c>Analysis/</c> is offline arithmetic outside the simulated path. A
/// source scan stands in for a banned-API analyzer until gameplay assemblies exist (tasks T004).
/// </summary>
public sealed class BannedApiTests
{
    private static readonly string Simulator = Path.Combine("src", "MmoEngine.Regionizer.Simulator");

    /// <summary>The simulator folders the scan leaves out (tasks T004).</summary>
    private static readonly string[] UnscannedSimulatorFolders = ["Output", "Analysis"];

    /// <summary>Each banned API: its name, the pattern that finds a use, and why it is banned.</summary>
    private static readonly Dictionary<string, (Regex Pattern, string Why)> Banned = new()
    {
        ["Math.Sin"] = (Use(@"\bMath\.Sin"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Cos"] = (Use(@"\bMath\.Cos"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Tan"] = (Use(@"\bMath\.Tan"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Atan"] = (Use(@"\bMath\.Atan"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Exp"] = (Use(@"\bMath\.Exp"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Log"] = (Use(@"\bMath\.Log"), "transcendental functions are not correctly rounded, so results can differ across platforms (research D8)"),
        ["Math.Pow"] = (Use(@"\bMath\.Pow"), "Math.Pow is not correctly rounded, so results can differ across platforms (research D8)"),
        ["MathF."] = (Use(@"\bMathF\."), "single-precision math is not used on the simulated path; positions are doubles with IEEE basic operations (research D8)"),
        ["lock ("] = (Use(@"\block\s*\("), "gameplay and regionizer code takes no locks (constitution Principle II)"),
        ["Monitor."] = (Use(@"\bMonitor\."), "gameplay and regionizer code takes no locks (constitution Principle II)"),
        ["DateTime.Now"] = (Use(@"\bDateTime\.Now\b"), "simulated time comes from the injected 60 Hz time base, never the wall clock (research D7)"),
        ["DateTime.UtcNow"] = (Use(@"\bDateTime\.UtcNow\b"), "simulated time comes from the injected 60 Hz time base, never the wall clock (research D7)"),
        ["System.Random"] = (Use(@"\bSystem\.Random\b|\bnew\s+Random\s*\(|(?<![\w.])Random\.Shared\b"), "randomness comes only from the simulator's seeded named streams (FR-016)"),
    };

    public static TheoryData<string> BannedApis => [.. Banned.Keys];

    /// <summary>A line of code that uses each banned API, for checking that the scan finds it.</summary>
    public static TheoryData<string, string> BannedUses => new()
    {
        { "Math.Sin", "var a = Math.Sin(x);" },
        { "Math.Sin", "var url = \"http://example.test\"; var a = Math.Sin(x);" },
        { "Math.Cos", "var a = System.Math.Cos(x);" },
        { "Math.Tan", "var a = Math.Tanh(x);" },
        { "Math.Atan", "var a = Math.Atan2(y, x);" },
        { "Math.Exp", "var a = Math.Exp(x);" },
        { "Math.Log", "var a = Math.Log10(x);" },
        { "Math.Pow", "var a = Math.Pow(x, 2);" },
        { "MathF.", "var a = MathF.Sqrt(x);" },
        { "lock (", "lock (_gate) { n++; }" },
        { "Monitor.", "System.Threading.Monitor.Enter(_gate);" },
        { "DateTime.Now", "var t = DateTime.Now;" },
        { "DateTime.UtcNow", "var t = DateTime.UtcNow.Ticks;" },
        { "System.Random", "var r = new System.Random(1);" },
        { "System.Random", "var r = new Random(1);" },
        { "System.Random", "var n = Random.Shared.Next();" },
    };

    [Theory]
    [MemberData(nameof(BannedApis))]
    public void SimulatedPathDoesNotUseBannedApi(string api)
    {
        var root = Kit1SourceScan.RepoRoot();
        var (pattern, why) = Banned[api];
        var files = ScannedFiles(root);
        var uses = new List<string>();
        foreach (var file in files)
        {
            foreach (var line in Kit1SourceScan.Hits(File.ReadAllText(file), pattern))
            {
                uses.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        Assert.True(files.Count > 0, $"No C# files found under {string.Join(", ", ScannedDirectories(root))} in {root}; the scan would pass vacuously.");
        Assert.True(uses.Count == 0, $"Banned API '{api}' is used on the simulated path ({why}): {string.Join(", ", uses)}");
    }

    /// <summary>
    /// T004's scope: the core, the map library and every simulator folder except Output/ and Analysis/,
    /// each holding source, so no part of the simulated path passes the scan by being left out.
    /// </summary>
    [Fact]
    public void ScanCoversTheCoreTheMapsAndEverySimulatorFolderButOutputAndAnalysis()
    {
        var root = Kit1SourceScan.RepoRoot();

        var scanned = ScannedDirectories(root);

        string[] required =
        [
            Path.Combine("src", "MmoEngine.Regionizer"),
            Path.Combine("src", "MmoEngine.Maps"),
            .. new[] { "Config", "Metrics", "Monitors", "Movement", "Random", "Running", "Scenarios", "Scheduling", "Sweep", "Trace" }
                .Select(folder => Path.Combine(Simulator, folder)),
        ];
        Assert.All(required, directory => Assert.Contains(directory, scanned));
        Assert.All(UnscannedSimulatorFolders, folder => Assert.DoesNotContain(Path.Combine(Simulator, folder), scanned));
        Assert.All(scanned, directory => Assert.True(
            Kit1SourceScan.SourceFiles(Path.Combine(root, directory)).Length > 0,
            $"{directory} holds no C# source in {root}"));
    }

    [Theory]
    [MemberData(nameof(BannedUses))]
    public void ScanFindsEachBannedUseInCodeButNotInComments(string api, string use)
    {
        var (pattern, _) = Banned[api];
        var source = string.Join('\n', "namespace N;", "// " + use, "/* " + use + " */", "/// <summary>" + use + "</summary>", use);

        Assert.Equal([5], Kit1SourceScan.Hits(source, pattern));
    }

    [Theory]
    [InlineData("var a = Math.Floor(x / s);")]
    [InlineData("var a = Math.Sqrt(x);")]
    [InlineData("var r = DeterministicRandom.ForStream(seed, \"movement\");")]
    [InlineData("var r = new DeterministicRandom(seed);")]
    [InlineData("namespace MmoEngine.Regionizer.Simulator.Random;")]
    [InlineData("var t = Stopwatch.GetTimestamp();")]
    [InlineData("var started = WallClock.UtcNow();")]
    [InlineData("var wall = WallClock.StartTimer();")]
    [InlineData("var url = \"http://example.test\"; var a = Math.Abs(x);")]
    [InlineData("var blocked = true;")]
    public void ScanAllowsPermittedCode(string line)
    {
        foreach (var (api, (pattern, _)) in Banned)
        {
            Assert.True(Kit1SourceScan.Hits(line, pattern).Length == 0, $"'{line}' was reported as a use of {api}");
        }
    }

    /// <summary>
    /// The directories of the simulated path, relative to the repository root: the core library, the map
    /// library and every simulator folder except <c>Output/</c> and <c>Analysis/</c>. The simulator's
    /// folders are listed from disk, so a new folder is scanned without editing this test.
    /// </summary>
    private static string[] ScannedDirectories(string root)
    {
        var simulatorFolders = Directory.EnumerateDirectories(Path.Combine(root, Simulator))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name is not ("bin" or "obj") && !UnscannedSimulatorFolders.Contains(name, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => Path.Combine(Simulator, name));
        return [Path.Combine("src", "MmoEngine.Regionizer"), Path.Combine("src", "MmoEngine.Maps"), .. simulatorFolders];
    }

    /// <summary>The C# files of <see cref="ScannedDirectories"/>, plus any at the simulator project's top level.</summary>
    private static List<string> ScannedFiles(string root)
    {
        var files = new List<string>();
        foreach (var relative in ScannedDirectories(root))
        {
            var directory = Path.Combine(root, relative);
            Assert.True(Directory.Exists(directory), $"{relative} is missing from {root}; the scan would skip it.");
            files.AddRange(Kit1SourceScan.SourceFiles(directory));
        }

        files.AddRange(Directory.EnumerateFiles(Path.Combine(root, Simulator), "*.cs", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal));
        return files;
    }

    private static Regex Use(string pattern) => new(pattern, RegexOptions.CultureInvariant);
}
