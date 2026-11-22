using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// The backstop: the analyzer, run in the test process over every source file of the core with the
/// core's own options and references, finds nothing, not even a suppressed diagnostic (US3's
/// Independent Test, SC-003), per specs/015-shared-simulation-core/contracts/analyzer-rules.md#severity-and-suppression.
/// It does not depend on the build having loaded the analyzer, so <c>RunAnalyzers=false</c>, a CS9057
/// load failure or a <c>[SuppressMessage]</c> cannot hide a violation from it. Two injected files prove
/// it reports a real violation and counts a suppressed one.
/// </summary>
public sealed class BackstopTests
{
    /// <summary>The path the injected file is compiled under, next to the core's sources.</summary>
    private const string InjectedPath = "Injected.cs";

    private const string SimMathPath = "src/MmoEngine.SimCore/Math/SimMath.cs";

    private const string CoefficientsPath = "src/MmoEngine.SimCore/Math/SimMathCoefficients.g.cs";

    /// <summary>A core type that enumerates a <c>Dictionary</c>, which MMO1004 reports.</summary>
    private const string InjectedHashOrder =
        "namespace MmoEngine.SimCore; internal static class Injected { internal static int M(System.Collections.Generic.Dictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; } }";

    /// <summary>A core type with a <c>System.MathF.Sin</c> call (MMO1002), hidden from the build by an assembly-level <c>[SuppressMessage]</c>.</summary>
    private const string InjectedSuppressed = """
        [assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Determinism", "MMO1002")]

        namespace MmoEngine.SimCore;

        internal static class Injected
        {
            internal static float M(float x) => System.MathF.Sin(x);
        }
        """;

    /// <summary>
    /// US3-IT, SC-003: every <c>src/MmoEngine.SimCore/**/*.cs</c> file, generated ones included
    /// (<c>SimMath.cs</c> and <c>SimMathCoefficients.g.cs</c> among them) and none under <c>bin/</c> or
    /// <c>obj/</c>, compiled with the core's options (nullable on, unsafe off, latest language version)
    /// against the .NET 10 reference assemblies and System.IO.Hashing, has zero compiler errors and zero
    /// analyzer diagnostics, suppressed ones included. A failure lists every diagnostic, suppressed ones
    /// marked <c>[suppressed]</c>.
    /// </summary>
    [Fact]
    public async Task CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes()
    {
        var trees = AnalyzerHarness.CoreSourceTrees();
        var paths = trees.Select(t => t.FilePath).ToArray();
        Assert.Contains(SimMathPath, paths);
        Assert.Contains(CoefficientsPath, paths);
        Assert.All(paths, path => Assert.False(
            path.Split('/').Any(segment => segment is "bin" or "obj"),
            $"{path} has a bin or obj segment; the backstop analyzes committed sources only."));

        var result = await AnalyzerHarness.AnalyzeTreesAsync(trees, AnalyzerHarness.CoreOptions, AnalyzerHarness.CoreReferences);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The core's sources must compile in the backstop:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 0,
            $"The core's sources must have no determinism diagnostics, suppressed ones included:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
    }

    /// <summary>
    /// The backstop is not vacuous: the core's sources plus <c>Injected.cs</c>, which enumerates a
    /// <c>Dictionary&lt;int, int&gt;</c>, report exactly one diagnostic, MMO1004, in <c>Injected.cs</c>.
    /// </summary>
    [Fact]
    public async Task BackstopReportsAnInjectedViolation()
    {
        var result = await AnalyzeCoreWithAsync(InjectedHashOrder);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The core's sources and {InjectedPath} must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 1,
            $"Expected exactly one diagnostic, MMO1004 in {InjectedPath}, got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        var diagnostic = result.Diagnostics[0];
        Assert.Equal("MMO1004", diagnostic.Id);
        Assert.Equal(InjectedPath, diagnostic.Location.SourceTree?.FilePath);
    }

    /// <summary>
    /// The backstop counts what the build hides: the core's sources plus <c>Injected.cs</c>, whose
    /// <c>System.MathF.Sin</c> call is under an assembly-level <c>[SuppressMessage("Determinism", "MMO1002")]</c>,
    /// report exactly one MMO1002 with <see cref="Diagnostic.IsSuppressed"/> true, in <c>Injected.cs</c>.
    /// <see cref="CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes"/>'s zero-diagnostics
    /// predicate counts it.
    /// </summary>
    [Fact]
    public async Task BackstopCountsAnAssemblySuppressedDiagnostic()
    {
        var result = await AnalyzeCoreWithAsync(InjectedSuppressed);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The core's sources and {InjectedPath} must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 1,
            $"Expected exactly one suppressed MMO1002 in {InjectedPath}, got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        var diagnostic = result.Diagnostics[0];
        Assert.Equal("MMO1002", diagnostic.Id);
        Assert.True(diagnostic.IsSuppressed, $"The assembly [SuppressMessage] must report {AnalyzerHarness.Describe(diagnostic)} as suppressed.");
        Assert.Equal(InjectedPath, diagnostic.Location.SourceTree?.FilePath);
    }

    /// <summary>Analyzes the core's source trees plus <paramref name="injected"/> as <c>Injected.cs</c>, with the core's options and references.</summary>
    private static Task<AnalysisResult> AnalyzeCoreWithAsync(string injected)
    {
        var injectedTree = CSharpSyntaxTree.ParseText(SourceText.From(injected, Encoding.UTF8), AnalyzerHarness.ParseOptions, InjectedPath);
        return AnalyzerHarness.AnalyzeTreesAsync(
            [.. AnalyzerHarness.CoreSourceTrees(), injectedTree],
            AnalyzerHarness.CoreOptions,
            AnalyzerHarness.CoreReferences);
    }
}
