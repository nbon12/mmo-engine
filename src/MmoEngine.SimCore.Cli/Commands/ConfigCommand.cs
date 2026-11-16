using System.CommandLine;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>config check &lt;file&gt;</c> (FR-029, specs/015-shared-simulation-core/contracts/simcore-cli.md#config-check):
/// binds the file with <see cref="SimConfigJson.Load"/>, validates what binds with
/// <see cref="SimConfigValidator.Errors"/>, and prints one <c>path: message</c> line per binding or
/// validation error on stdout, in rule-table order, and nothing when the file is valid. It exits 0 when
/// valid, 1 when invalid and 2 when the file does not exist.
/// </summary>
internal static class ConfigCommand
{
    /// <summary>The <c>config</c> command with its <c>check</c> subcommand.</summary>
    public static Command Create(TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);
        var file = new Argument<string>("file") { Description = "The configuration file to check, for example simcore/config/default.json." };
        var check = new Command("check", "Validates a configuration file against the core's rule table.") { file };
        check.SetAction(parse => Check(parse.GetValue(file)!, stdout, stderr, logger));
        return new Command("config", "Simulation configuration files.") { check };
    }

    /// <summary>Checks <paramref name="path"/> and returns the exit code.</summary>
    internal static int Check(string path, TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        if (!File.Exists(path))
        {
            stderr.Write($"{path}: the file does not exist\n");
            return CliExitCodes.Usage;
        }

        var load = SimConfigJson.Load(path);
        IReadOnlyList<ConfigError> errors = load.Config is null ? load.Errors : SimConfigValidator.Errors(load.Config);
        foreach (var error in errors)
        {
            stdout.Write($"{error.Path}: {error.Message}\n");
        }

        logger.Information("Checked configuration {Path}: {ErrorCount} errors", path, errors.Count);
        return errors.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failed;
    }
}
