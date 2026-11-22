using System.CommandLine;
using System.Globalization;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>config check &lt;file&gt;</c> (FR-029, specs/015-shared-simulation-core/contracts/simcore-cli.md#config-check):
/// reads the file, binds it with <see cref="SimConfigJson.Parse"/>, validates what binds with
/// <see cref="SimConfigValidator.Errors"/>, and prints one <c>path: message</c> line per binding or
/// validation error on stdout, in rule-table order, and nothing when the file is valid. It exits 0 when
/// valid and 1 when invalid. A file it cannot check (one that does not exist, is longer than
/// <see cref="SimConfigJson.MaxFileLength"/>, holds more than that as it is read, or cannot be read) is a
/// refused input: one <c>&lt;path&gt;: …</c> line on stderr and exit 2, as the CLI's exit-code table gives
/// it; an empty or whitespace-only path is refused when the command line is parsed
/// (<see cref="PathOptions"/>), also exit 2.
/// </summary>
internal static class ConfigCommand
{
    /// <summary>The <c>config</c> command with its <c>check</c> subcommand.</summary>
    public static Command Create(TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);
        var file = PathOptions.RequireNonEmpty(new Argument<string>("file") { Description = "The configuration file to check, for example simcore/config/default.json." });
        var check = new Command("check", "Validates a configuration file against the core's rule table.") { file };
        check.SetAction(parse => Check(parse.GetValue(file)!, stdout, stderr, logger));
        return new Command("config", "Simulation configuration files.") { check };
    }

    /// <summary>Checks <paramref name="path"/> and returns the exit code.</summary>
    internal static int Check(string path, TextWriter stdout, TextWriter stderr, ILogger logger) =>
        Check(path, stdout, stderr, logger, OpenRead);

    /// <summary>
    /// <see cref="Check(string, TextWriter, TextWriter, ILogger)"/>, reading the file through the stream
    /// <paramref name="open"/> gives for the path, so a test can give a stream without a length or a file
    /// that grows after its length is checked.
    /// </summary>
    internal static int Check(string path, TextWriter stdout, TextWriter stderr, ILogger logger, Func<string, Stream> open)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            stderr.Write($"{path}: the file does not exist\n");
            return CliExitCodes.Usage;
        }

        // Refused from its length, before it is read, with SimConfigJson.Load's limit.
        if (file.Length > SimConfigJson.MaxFileLength)
        {
            stderr.Write(string.Create(CultureInfo.InvariantCulture, $"{path}: the file is {file.Length} bytes; the limit is {SimConfigJson.MaxFileLength}\n"));
            return CliExitCodes.Usage;
        }

        // Read through SimConfigJson's bounded reader, so a pipe, a device or a file that grew since its
        // length was checked is refused once it passes the limit instead of being read whole.
        string? json;
        try
        {
            using var stream = open(path);
            json = SimConfigJson.ReadText(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.Write($"{path}: the file cannot be read ({ex.Message})\n");
            return CliExitCodes.Usage;
        }

        if (json is null)
        {
            stderr.Write($"{path}: {SimConfigJson.MoreThanTheLimit}\n");
            return CliExitCodes.Usage;
        }

        var load = SimConfigJson.Parse(json);
        IReadOnlyList<ConfigError> errors = load.Config is null ? load.Errors : SimConfigValidator.Errors(load.Config);
        foreach (var error in errors)
        {
            stdout.Write($"{error.Path}: {error.Message}\n");
        }

        logger.Information("Checked configuration {Path}: {ErrorCount} errors", path, errors.Count);
        return errors.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failed;
    }

    /// <summary>The file at <paramref name="path"/>, opened read-only and shared for reading.</summary>
    private static Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
}
