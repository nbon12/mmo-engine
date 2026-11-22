using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// The nine determinism descriptors agree with the release-tracking file and with the contract's
/// titles and message formats (FR-014, SC-003), per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#release-tracking, #rules and #messages.
/// Every rule is an Error, enabled by default, in category <c>Determinism</c> and not configurable, so
/// no severity setting can lower it.
/// </summary>
public sealed class DescriptorTests
{
    private const string SolutionFile = "MmoEngine.sln";

    private const string UnshippedPath = "src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Unshipped.md";

    private const string ShippedPath = "src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Shipped.md";

    private const string NewRulesHeading = "### New Rules";

    /// <summary>The rule IDs, in the order <see cref="DeterminismAnalyzer.SupportedDiagnostics"/> must list them.</summary>
    private static readonly string[] RuleIds =
        ["MMO1001", "MMO1002", "MMO1003", "MMO1004", "MMO1005", "MMO1006", "MMO1007", "MMO1008", "MMO1009"];

    /// <summary>
    /// <see cref="DeterminismAnalyzer.SupportedDiagnostics"/> is exactly MMO1001 to MMO1009 in order,
    /// each Error, enabled by default, category <c>Determinism</c> and tagged
    /// <see cref="WellKnownDiagnosticTags.NotConfigurable"/>. The <c>### New Rules</c> table of
    /// <c>AnalyzerReleases.Unshipped.md</c> lists the same nine, each
    /// <c>Determinism | Error | DeterminismAnalyzer</c>, and <c>AnalyzerReleases.Shipped.md</c> has no
    /// rule rows.
    /// </summary>
    [Fact]
    public void DescriptorsMatchTheReleaseFile()
    {
        var descriptors = new DeterminismAnalyzer().SupportedDiagnostics;

        Assert.Equal(RuleIds, descriptors.Select(d => d.Id));
        Assert.All(descriptors, d =>
        {
            Assert.Equal(DiagnosticSeverity.Error, d.DefaultSeverity);
            Assert.True(d.IsEnabledByDefault, $"{d.Id} must be enabled by default.");
            Assert.Equal("Determinism", d.Category);
            Assert.Contains(WellKnownDiagnosticTags.NotConfigurable, d.CustomTags);
        });

        var expectedRows = RuleIds.Select(id => (id, "Determinism", "Error", "DeterminismAnalyzer")).ToArray();
        Assert.Equal(expectedRows, NewRulesRows(ReadRepoText(UnshippedPath)));

        var shippedRows = ReadRepoText(ShippedPath).Split('\n').Where(IsRuleRow).ToArray();
        Assert.True(shippedRows.Length == 0, $"{ShippedPath} must have no rule rows, got:{Environment.NewLine}{string.Join(Environment.NewLine, shippedRows)}");
    }

    /// <summary>
    /// Each descriptor's title is the contract's Title (contracts/analyzer-rules.md#rules) and its message
    /// format is the contract's message format (#messages), verbatim.
    /// </summary>
    [Theory]
    [InlineData(
        "MMO1001",
        "Vector or SIMD type outside the lane-wise allowlist",
        "'{0}' uses '{1}', whose results can differ between the server and WebAssembly builds; use the core's scalar float types, or lane-wise Vector128<float> arithmetic from the allowlist")]
    [InlineData(
        "MMO1002",
        "Platform math outside the allowlist",
        "'{0}' is platform math, which is not correctly rounded and can differ between builds; use {1}")]
    [InlineData(
        "MMO1003",
        "Fused multiply-add",
        "'{0}' fuses (or may fuse) a multiply and an add, which rounds differently from 'a * b + c'; write the multiply and the add as separate float operations")]
    [InlineData(
        "MMO1004",
        "Iteration over a hash-ordered collection",
        "'{0}' enumerates '{1}', whose order is not defined; iterate a sorted array or List<T> kept alongside it, or a SortedDictionary or SortedSet")]
    [InlineData(
        "MMO1005",
        "Ambient time, randomness or a per-process hash seed",
        "'{0}' reads ambient time, randomness or a per-process hash seed; take the step number and step length from the caller, and draw randomness from the core's seeded spread hash")]
    [InlineData(
        "MMO1006",
        "Lock or blocking synchronization",
        "'{0}' takes a lock or blocks, which gameplay code must not do (constitution Principle II); the core is a pure step function: take state in and return state out")]
    [InlineData(
        "MMO1007",
        "Non-float floating point or platform-sized number",
        "'{0}' uses '{1}'; the core computes in 'float' only, and in 'int' or 'long' for fixed point and counters, so every build rounds and overflows the same way")]
    [InlineData(
        "MMO1008",
        "Platform query, conditional compilation or reflection",
        "'{0}' lets the server and WebAssembly builds take different paths; the core has one code path for every build")]
    [InlineData(
        "MMO1009",
        "Float-to-integer or ulong-to-float conversion outside the SimMath helpers",
        "'{0}' {1}")]
    public void TitlesAndMessageFormatsAreTheContracts(string id, string title, string messageFormat)
    {
        var descriptors = new DeterminismAnalyzer().SupportedDiagnostics;
        var matching = descriptors.Where(d => d.Id == id).ToArray();
        Assert.True(
            matching.Length == 1,
            $"Expected exactly one descriptor {id}, got [{string.Join(", ", descriptors.Select(d => d.Id))}].");

        var descriptor = matching[0];
        Assert.Equal(title, descriptor.Title.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(messageFormat, descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The rows of the release file's <c>### New Rules</c> table: the heading appears once, then the
    /// header <c>Rule ID | Category | Severity | Notes</c>, the dashed separator and one row per rule,
    /// up to the next blank line, heading or end of file. Each row is its four trimmed cells.
    /// </summary>
    private static (string Id, string Category, string Severity, string Notes)[] NewRulesRows(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var headings = lines.Select((line, index) => (line, index)).Where(l => l.line.Trim() == NewRulesHeading).ToArray();
        Assert.True(headings.Length == 1, $"{UnshippedPath} must have exactly one '{NewRulesHeading}' heading, found {headings.Length}.");

        var table = lines
            .Skip(headings[0].index + 1)
            .SkipWhile(string.IsNullOrWhiteSpace)
            .TakeWhile(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToArray();
        Assert.True(table.Length >= 2, $"{UnshippedPath}'s '{NewRulesHeading}' table needs a header and a separator line.");
        Assert.Equal(new[] { "Rule ID", "Category", "Severity", "Notes" }, Cells(table[0]));
        Assert.All(Cells(table[1]), cell => Assert.True(cell.Length > 0 && cell.All(c => c == '-'), $"'{table[1]}' must be the table's dashed separator."));

        return
        [
            .. table.Skip(2).Select(row =>
            {
                var cells = Cells(row);
                Assert.True(cells.Length == 4, $"'{row}' must have four cells: Rule ID, Category, Severity and Notes.");
                return (cells[0], cells[1], cells[2], cells[3]);
            }),
        ];
    }

    /// <summary>A line's <c>|</c>-separated cells, trimmed.</summary>
    private static string[] Cells(string line) => [.. line.Split('|').Select(cell => cell.Trim())];

    /// <summary>
    /// True for a release-file rule row: a line whose first <c>|</c>-separated cell is a rule ID, one
    /// or more ASCII capitals followed by one or more digits (<c>MMO1001</c>).
    /// </summary>
    private static bool IsRuleRow(string line)
    {
        if (!line.Contains('|'))
        {
            return false;
        }

        var id = Cells(line)[0];
        var letters = id.TakeWhile(char.IsAsciiLetterUpper).Count();
        return letters > 0 && id.Length > letters && id.Skip(letters).All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Reads a repository file as UTF-8 text, opened with <see cref="FileAccess.Read"/> only, from the
    /// directory holding MmoEngine.sln, found by walking up from <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private static string ReadRepoText(string relativePath)
    {
        var start = AppContext.BaseDirectory;
        var root = new DirectoryInfo(start);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, SolutionFile)))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new DirectoryNotFoundException($"No directory holding {SolutionFile} above {start}.");
        }

        using var stream = new FileStream(Path.Combine(root.FullName, relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
