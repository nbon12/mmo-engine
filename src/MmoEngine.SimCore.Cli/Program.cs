using System.CommandLine;
using MmoEngine.SimCore.Cli.Commands;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Cli;

/// <summary>
/// <c>simcore</c>: the shared simulation core's tools
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md). Logs are Serilog compact JSON on stderr
/// (<see cref="CliLogging"/>); results are plain text lines on stdout; the exit codes are
/// <see cref="CliExitCodes"/>.
/// </summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs the CLI with explicit writers, with the environment named by <c>DOTNET_ENVIRONMENT</c>.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, Environment.GetEnvironmentVariable(SimEnvironment.VariableName));

    /// <summary>
    /// Runs the CLI with <paramref name="environmentName"/> standing in for <c>DOTNET_ENVIRONMENT</c>
    /// (null: unset), so a test can check the environment without touching the process environment. An
    /// unknown name writes <c>DOTNET_ENVIRONMENT: &lt;message&gt;</c> to stderr and exits 2 before any
    /// command runs; a parse error is logged and exits 2.
    /// </summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? environmentName)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var (_, environmentError) = SimEnvironment.Resolve(environmentName);
        if (environmentError is not null)
        {
            stderr.Write($"{environmentError.Path}: {environmentError.Message}\n");
            return CliExitCodes.Usage;
        }

        using var logger = CliLogging.Create(stderr);
        var root = new RootCommand("simcore: the shared simulation core's tools (specs/015-shared-simulation-core/contracts/simcore-cli.md).")
        {
            ConfigCommand.Create(stdout, stderr, logger),
        };

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                logger.Error("Invalid arguments: {Error}", error.Message);
            }

            return CliExitCodes.Usage;
        }

        return parse.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }
}
