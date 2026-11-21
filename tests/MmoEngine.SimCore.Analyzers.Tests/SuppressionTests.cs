using Microsoft.CodeAnalysis;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// The determinism errors cannot be lowered or hidden by the mechanisms that hide ordinary warnings,
/// and a <c>[SuppressMessage]</c> is still reported, marked suppressed, so the backstop can count it
/// (SC-003, FR-014), per specs/015-shared-simulation-core/contracts/analyzer-rules.md#severity-and-suppression.
/// The sample is one banned platform-math call.
/// </summary>
public sealed class SuppressionTests
{
    /// <summary>The sample member: one <c>MathF.Sin</c> call, which reports MMO1002.</summary>
    private const string Sample = "public static float M(float x) => MathF.Sin(x);";

    /// <summary>The file the <c>assembly</c> row puts its assembly-level <c>[SuppressMessage]</c> in.</summary>
    private const string SuppressionsPath = "GlobalSuppressions.cs";

    /// <summary>
    /// SC-003: with <c>#pragma warning disable MMO1002</c> around the member (<c>pragma</c>), with
    /// <c>SpecificDiagnosticOptions</c> setting MMO1002 to Suppress, which is what <c>NoWarn</c> and
    /// <c>dotnet_diagnostic.MMO1002.severity = none</c> map to (<c>specific-suppress</c>), and with
    /// warnings not treated as errors (<c>warnings-not-errors</c>), the sample still reports exactly one
    /// diagnostic: MMO1002, at Error, not suppressed.
    /// </summary>
    [Theory]
    [InlineData("pragma")]
    [InlineData("specific-suppress")]
    [InlineData("warnings-not-errors")]
    public async Task SeverityCannotBeLowered(string mechanism)
    {
        var (member, options) = mechanism switch
        {
            "pragma" => (
                "#pragma warning disable MMO1002\n    " + Sample + "\n#pragma warning restore MMO1002",
                AnalyzerHarness.SampleOptions),
            "specific-suppress" => (
                Sample,
                AnalyzerHarness.SampleOptions.WithSpecificDiagnosticOptions(
                    new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal) { ["MMO1002"] = ReportDiagnostic.Suppress })),
            // TreatWarningsAsErrors=false: the Error severity has to come from the descriptor alone.
            "warnings-not-errors" => (
                Sample,
                AnalyzerHarness.SampleOptions.WithGeneralDiagnosticOption(ReportDiagnostic.Default)),
            _ => throw new ArgumentOutOfRangeException(nameof(mechanism), mechanism, "Unknown suppression mechanism."),
        };

        var result = await AnalyzerHarness.AnalyzeMemberAsync(member, options);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 1,
            $"Expected exactly one diagnostic under '{mechanism}', got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        var diagnostic = result.Diagnostics[0];
        Assert.Equal("MMO1002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.False(diagnostic.IsSuppressed, $"'{mechanism}' must not suppress {AnalyzerHarness.Describe(diagnostic)}.");
    }

    /// <summary>
    /// FR-014: <c>[SuppressMessage("Determinism", "MMO1002")]</c> on the member (<c>member</c>) or on the
    /// assembly, in a separate file (<c>assembly</c>), hides MMO1002 from the build, but the analyzer
    /// run with suppressed diagnostics reported still gives exactly one MMO1002, with
    /// <see cref="Diagnostic.IsSuppressed"/> true, which the backstop counts.
    /// </summary>
    [Theory]
    [InlineData("member")]
    [InlineData("assembly")]
    public async Task SuppressMessageIsReportedAsSuppressed(string scope)
    {
        (string Path, string Text)[] files = scope switch
        {
            "member" =>
            [
                (AnalyzerHarness.SamplePath, AnalyzerHarness.SampleText("[System.Diagnostics.CodeAnalysis.SuppressMessage(\"Determinism\", \"MMO1002\")] " + Sample)),
            ],
            "assembly" =>
            [
                (AnalyzerHarness.SamplePath, AnalyzerHarness.SampleText(Sample)),
                (SuppressionsPath, "[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Determinism\", \"MMO1002\")]\n"),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown suppression scope."),
        };

        var result = await AnalyzerHarness.AnalyzeSourcesAsync(files);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sources must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 1 && result.Diagnostics[0].Id == "MMO1002",
            $"Expected exactly one MMO1002 with the {scope} [SuppressMessage], got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        Assert.True(
            result.Diagnostics[0].IsSuppressed,
            $"The {scope} [SuppressMessage] must report {AnalyzerHarness.Describe(result.Diagnostics[0])} as suppressed.");
    }
}
