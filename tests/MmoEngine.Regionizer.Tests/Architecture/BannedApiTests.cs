using System.Text.RegularExpressions;
using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Architecture;

/// <summary>
/// The simulated path stays deterministic and lock-free (FR-016, FR-019, research D8, constitution
/// Principle II): the core library and the simulator's movement models use no transcendental or
/// single-precision math, no locks, no wall clock and no System.Random. A source scan stands in for a
/// banned-API analyzer until gameplay assemblies exist (tasks T004).
/// </summary>
public sealed class BannedApiTests
{
    /// <summary>The directories of the simulated path, relative to the repository root. Movement may not exist yet.</summary>
    private static readonly string[] ScannedDirectories =
    [
        Path.Combine("src", "MmoEngine.Regionizer"),
        Path.Combine("src", "MmoEngine.Regionizer.Simulator", "Movement"),
    ];

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
        var scanned = 0;
        var uses = new List<string>();
        foreach (var relative in ScannedDirectories)
        {
            var directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Kit1SourceScan.SourceFiles(directory))
            {
                scanned++;
                foreach (var line in Kit1SourceScan.Hits(File.ReadAllText(file), pattern))
                {
                    uses.Add($"{Path.GetRelativePath(root, file)}:{line}");
                }
            }
        }

        Assert.True(scanned > 0, $"No C# files found under {string.Join(", ", ScannedDirectories)} in {root}; the scan would pass vacuously.");
        Assert.True(uses.Count == 0, $"Banned API '{api}' is used on the simulated path ({why}): {string.Join(", ", uses)}");
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
    [InlineData("var url = \"http://example.test\"; var a = Math.Abs(x);")]
    [InlineData("var blocked = true;")]
    public void ScanAllowsPermittedCode(string line)
    {
        foreach (var (api, (pattern, _)) in Banned)
        {
            Assert.True(Kit1SourceScan.Hits(line, pattern).Length == 0, $"'{line}' was reported as a use of {api}");
        }
    }

    private static Regex Use(string pattern) => new(pattern, RegexOptions.CultureInvariant);
}
