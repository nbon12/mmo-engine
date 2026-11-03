using System.Text.Json;
using MmoEngine.Maps.Tests.Support;
using Xunit;
using CliProgram = MmoEngine.PortalValidator.Cli.Program;

namespace MmoEngine.Maps.Tests;

/// <summary>The <c>portal-validator</c> CLI, run in process (T055, contracts/map-format.md exit codes).</summary>
public sealed class PortalValidatorCliTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "a.json", "b.json" },
        new[] { "--bogus", "a.json" },
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
        var findings = PortalValidator.Validate(MapDocument.Load(path));

        var (exitCode, stdout, stderr) = Run(path);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(findings.Select(f => $"{f.Code} {f.Location} {f.Message}"), Lines(stdout));
        Assert.Contains($"{findings.Count} finding(s)", stderr, StringComparison.Ordinal);
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
        Assert.Contains(path, stderr, StringComparison.Ordinal);
        Assert.Contains(expectedError, stderr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArgumentsExitWithTwo(string[] args)
    {
        var (exitCode, stdout, stderr) = Run(args);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.StartsWith("portal-validator: ", stderr, StringComparison.Ordinal);
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
}
