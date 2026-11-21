using System.Text.Json.Nodes;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// FR-029, contracts/simcore-cli.md#config-check: <c>simcore config check &lt;file&gt;</c> binds the file
/// with <c>SimConfigJson</c>, validates it with <c>SimConfigValidator</c> and prints one
/// <c>path: message</c> line per error on stdout, in rule-table order, and nothing when the file is
/// valid. It exits 0 when valid, 1 when invalid, and 2 on a usage error or a refused input: a missing
/// file, one longer than 1 MiB or one that cannot be read, named on stderr, or an empty or
/// whitespace-only file argument, refused as <c>&lt;file&gt;: the path must not be empty or whitespace</c>.
/// </summary>
public sealed class ConfigCommandTests
{
    /// <summary>The two errors of the invalid copy, in rule-table order (stepRateHz is section 0, walkSpeed section 1).</summary>
    private const string InvalidOutput =
        "stepRateHz: must be between 20 and 60\n" +
        "movement.walkSpeed: must be between 0.1 and 20\n";

    /// <summary>The largest configuration file <c>config check</c> reads, 1 MiB.</summary>
    private const long MaxFileLength = 1024 * 1024;

    public static TheoryData<string> Cases =>
        ["valid", "invalid", "missing-file", "oversized-file", "unreadable-file", "no-file-argument", "empty-file-argument", "whitespace-file-argument"];

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConfigCheckExitsZeroForValidOneForInvalidTwoForMissingOrRefused(string caseName)
    {
        using var directory = TempDirectories.Create("config-check");
        var row = Row(caseName, directory);
        using var hold = row.Hold;

        var (exit, stdout, stderr) = SimcoreCli.Run(row.Args);

        Assert.True(
            exit == row.Exit,
            $"{caseName}: simcore {string.Join(' ', row.Args)} exited {exit}, expected {row.Exit}; stdout: {stdout}; stderr: {stderr}");
        if (row.Stdout is not null)
        {
            Assert.Equal(row.Stdout, stdout);
        }

        if (row.StderrContains is not null)
        {
            Assert.Contains(row.StderrContains, stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A row's arguments and what it must give. Only the invalid and oversized rows write a file, into
    /// <paramref name="directory"/>.
    /// </summary>
    private static ConfigCheckRow Row(string caseName, TempDirectory directory)
    {
        switch (caseName)
        {
            case "valid":
                return new(["config", "check", DefaultConfigPath], Exit: 0, Stdout: string.Empty);

            case "invalid":
                return new(["config", "check", WriteInvalidCopy(directory)], Exit: 1, Stdout: InvalidOutput);

            case "missing-file":
                var missing = directory.Combine("missing.json");
                return new(["config", "check", missing], Exit: 2, StderrContains: $"{missing}: the file does not exist");

            // One byte over the limit: refused from its length, before it is read, so nothing is
            // bound and nothing reaches stdout.
            case "oversized-file":
                var oversized = WriteOversizedFile(directory);
                return new(
                    ["config", "check", oversized],
                    Exit: 2,
                    Stdout: string.Empty,
                    StderrContains: $"{oversized}: the file is {MaxFileLength + 1} bytes; the limit is {MaxFileLength}");

            // A valid copy that another handle holds exclusively (FileShare.None) for the whole run, so it
            // exists and is short enough, but reading it fails: it is named on stderr with the reason, and
            // nothing is bound or printed on stdout.
            case "unreadable-file":
                var locked = WriteLockedCopy(directory, out var hold);
                return new(
                    ["config", "check", locked],
                    Exit: 2,
                    Stdout: string.Empty,
                    StderrContains: $"{locked}: the file cannot be read (",
                    Hold: hold);

            case "no-file-argument":
                return new(["config", "check"], Exit: 2);

            // An empty or whitespace-only path is a usage error when the command line is parsed, so no
            // file API sees it and nothing reaches stdout.
            case "empty-file-argument":
                return new(["config", "check", string.Empty], Exit: 2, Stdout: string.Empty, StderrContains: "<file>: the path must not be empty or whitespace");

            case "whitespace-file-argument":
                return new(["config", "check", "   "], Exit: 2, Stdout: string.Empty, StderrContains: "<file>: the path must not be empty or whitespace");

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case.");
        }
    }

    /// <summary>The committed placeholders, opened by the CLI by their absolute path.</summary>
    private static string DefaultConfigPath => RepoPaths.Combine("simcore", "config", "default.json");

    /// <summary>
    /// A copy of <c>simcore/config/default.json</c> with <c>stepRateHz</c> 61 and <c>movement.walkSpeed</c>
    /// 0, written into <paramref name="directory"/>. The repository file is only read.
    /// </summary>
    private static string WriteInvalidCopy(TempDirectory directory)
    {
        var config = JsonNode.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"))!.AsObject();
        config["stepRateHz"] = 61;
        config["movement"]!["walkSpeed"] = 0;
        var path = directory.Combine("default.json");
        File.WriteAllText(path, config.ToJsonString());
        return path;
    }

    /// <summary>
    /// A copy of <c>simcore/config/default.json</c> in <paramref name="directory"/>, with
    /// <paramref name="hold"/> an open handle that shares nothing, so no other open of the file can read
    /// it until the handle is disposed.
    /// </summary>
    private static string WriteLockedCopy(TempDirectory directory, out FileStream hold)
    {
        var path = directory.Combine("locked.json");
        File.WriteAllText(path, RepoPaths.ReadAllText("simcore", "config", "default.json"));
        hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return path;
    }

    /// <summary>A sparse file one byte longer than 1 MiB, written into <paramref name="directory"/>.</summary>
    private static string WriteOversizedFile(TempDirectory directory)
    {
        var path = directory.Combine("oversized.json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(MaxFileLength + 1);
        return path;
    }

    /// <summary>
    /// The arguments, the exit code, the exact stdout when given, a text stderr must contain when given, and
    /// a handle the row holds open while the command runs, if any.
    /// </summary>
    private sealed record ConfigCheckRow(string[] Args, int Exit, string? Stdout = null, string? StderrContains = null, IDisposable? Hold = null);
}
