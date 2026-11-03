using System.Text.Json;
using MmoEngine.Maps.Tests.Support;
using Xunit;
using CliProgram = MmoEngine.PortalValidator.Cli.Program;

namespace MmoEngine.Maps.Tests;

/// <summary>
/// The <c>portal-validator</c> CLI, run in process (T055, contracts/map-format.md exit codes).
/// Findings go to stdout; diagnostics are Serilog compact JSON log events on stderr.
/// </summary>
public sealed class PortalValidatorCliTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "a.json", "b.json" },
        new[] { "--bogus", "a.json" },
        new[] { string.Empty },
    };

    [Theory]
    [InlineData("test/clean.json", 0)]
    [InlineData("stacked-base.json", 0)]
    [InlineData("test/seeded-leaks.json", 1)]
    [InlineData("test/dead-portal.json", 1)]
    [InlineData("test/missing-policy.json", 1)]
    [InlineData("test/unsealed.json", 1)]
    public void ExitCodeIsZeroWithoutFindingsAndOneWithFindings(string mapFile, int expectedExitCode)
    {
        var path = TestFiles.Map(mapFile);
        var map = MapDocument.Load(path);
        var findings = PortalValidator.Validate(map);

        var (exitCode, stdout, stderr) = Run(path);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(findings.Select(f => $"{f.Code} {f.Location} {f.Message}"), Lines(stdout));
        var summary = Assert.Single(LogEvents(stderr));
        Assert.Equal("Validated map {MapName} ({MapPath}): {FindingCount} finding(s)", summary.GetProperty("@mt").GetString());
        Assert.Equal(map.Name, summary.GetProperty("MapName").GetString());
        Assert.Equal(path, summary.GetProperty("MapPath").GetString());
        Assert.Equal(findings.Count, summary.GetProperty("FindingCount").GetInt32());
    }

    [Theory]
    [InlineData("missing", null, "could not be read")]
    [InlineData("folder", null, "could not be read")]
    [InlineData("file", "{", "invalid map JSON")]
    [InlineData("file", "{\"formatVersion\": 2}", "formatVersion: unsupported version 2")]
    [InlineData("file", "{\"formatVersion\": 1, \"name\": \"x\", \"sectionSize\": 16, \"layers\": []}", "layers: at least one layer is required")]
    public void UnreadableOrInvalidMapExitsWithTwo(string kind, string? content, string expectedError)
    {
        var directory = TestFiles.NewTempDirectory();
        var path = kind == "folder" ? directory : Path.Combine(directory, "map.json");
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        var (exitCode, stdout, stderr) = Run(path);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        var error = Assert.Single(LogEvents(stderr));
        Assert.Equal("Error", error.GetProperty("@l").GetString());
        Assert.Equal(path, error.GetProperty("MapPath").GetString());
        var message = error.GetProperty("Error").GetString();
        Assert.StartsWith($"{path}: ", message, StringComparison.Ordinal);
        Assert.Contains(expectedError, message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArgumentsExitWithTwo(string[] args)
    {
        var (exitCode, stdout, stderr) = Run(args);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        var events = LogEvents(stderr);
        Assert.NotEmpty(events);
        Assert.All(events, e =>
        {
            Assert.Equal("Error", e.GetProperty("@l").GetString());
            Assert.Equal("Invalid arguments: {Error}", e.GetProperty("@mt").GetString());
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("Error").GetString()));
        });
    }

    [Theory]
    [InlineData("test/clean.json", false)]
    [InlineData("test/clean.json", true)]
    [InlineData("test/seeded-leaks.json", false)]
    [InlineData("test/seeded-leaks.json", true)]
    [InlineData("test/unsealed.json", false)]
    [InlineData("test/unsealed.json", true)]
    public void StderrHoldsOnlyCompactJsonLogEventsAndStdoutOnlyTheFindings(string mapFile, bool json)
    {
        var path = TestFiles.Map(mapFile);
        var findings = PortalValidator.Validate(MapDocument.Load(path));
        var expectedStdout = json
            ? PortalFindingJson.Serialize(findings) + Environment.NewLine
            : string.Concat(findings.Select(f => $"{f.Code} {f.Location} {f.Message}{Environment.NewLine}"));

        var (_, stdout, stderr) = json ? Run(path, "--json") : Run(path);

        Assert.Equal(expectedStdout, stdout);
        var lines = Lines(stderr);
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.True(
                document.RootElement.TryGetProperty("@l", out _) || document.RootElement.TryGetProperty("@mt", out _),
                $"stderr line has neither @l nor @mt: {line}");
            Assert.Equal("portal-validator", document.RootElement.GetProperty("Tool").GetString());
        });
    }

    [Theory]
    [InlineData("test/seeded-leaks.json", "test/seeded-leaks.expected.json", 1)]
    [InlineData("test/clean.json", null, 0)]
    public void JsonOptionWritesTheFindingsAsAJsonArray(string mapFile, string? expectedFile, int expectedExitCode)
    {
        var expected = expectedFile is null ? [] : PortalFindingJson.Deserialize(File.ReadAllText(TestFiles.Map(expectedFile)));

        var (exitCode, stdout, _) = Run(TestFiles.Map(mapFile), "--json");

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(expected, PortalFindingJson.Deserialize(stdout));
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.All(document.RootElement.EnumerateArray(), finding =>
        {
            Assert.Equal(["code", "location", "message"], finding.EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.String, finding.GetProperty("code").ValueKind);
        });
    }

    [Fact]
    public void HelpDescribesTheJsonOptionAndExitsWithZero()
    {
        var (exitCode, stdout, _) = Run("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("--json", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void MainRunsWithTheConsoleStreams()
    {
        Assert.Equal(0, CliProgram.Main([TestFiles.Map("test/clean.json")]));
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliProgram.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Each stderr line parsed as a compact JSON log event from this tool, in order.</summary>
    private static List<JsonElement> LogEvents(string stderr)
    {
        var events = new List<JsonElement>();
        foreach (var line in Lines(stderr))
        {
            using var document = JsonDocument.Parse(line);
            var logEvent = document.RootElement.Clone();
            Assert.Equal(JsonValueKind.Object, logEvent.ValueKind);
            Assert.True(logEvent.TryGetProperty("@mt", out _), $"stderr line is not a compact JSON log event: {line}");
            Assert.Equal("portal-validator", logEvent.GetProperty("Tool").GetString());
            events.Add(logEvent);
        }

        return events;
    }
}
