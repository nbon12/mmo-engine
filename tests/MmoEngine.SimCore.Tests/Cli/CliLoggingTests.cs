using System.Text.Json;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#settings-output-and-exit-codes: logs are Serilog compact JSON on stderr,
/// with the property <c>Tool</c> = <c>simcore</c>. Stdout carries only results, so a valid
/// <c>config check</c>, which has none, writes nothing there.
/// </summary>
public sealed class CliLoggingTests
{
    [Fact]
    public void EveryLogLineIsCompactJsonWithToolSimcore()
    {
        var (exit, stdout, stderr) = SimcoreCli.Run("config", "check", RepoPaths.Combine("simcore", "config", "default.json"));

        Assert.Equal(string.Empty, stdout);
        var lines = new List<string>();
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length != 0)
            {
                lines.Add(trimmed);
            }
        }

        Assert.True(lines.Count > 0, $"config check (exit {exit}) wrote no log line to stderr");
        for (var i = 0; i < lines.Count; i++)
        {
            AssertCompactJsonWithToolSimcore(i, lines[i]);
        }
    }

    private static void AssertCompactJsonWithToolSimcore(int index, string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            Assert.Fail($"stderr line {index} is not JSON ({exception.Message}): {line}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            Assert.True(root.ValueKind == JsonValueKind.Object, $"stderr line {index} is a JSON {root.ValueKind}, not an object: {line}");
            Assert.True(root.TryGetProperty("@t", out _), $"stderr line {index} has no @t: {line}");
            Assert.True(root.TryGetProperty("@mt", out _), $"stderr line {index} has no @mt: {line}");
            Assert.True(
                root.TryGetProperty("Tool", out var tool) && tool.ValueKind == JsonValueKind.String && tool.GetString() == "simcore",
                $"stderr line {index} does not have \"Tool\": \"simcore\": {line}");
        }
    }
}
