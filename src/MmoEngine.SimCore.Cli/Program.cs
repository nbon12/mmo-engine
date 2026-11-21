using System.CommandLine;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Cli.Commands;
using MmoEngine.SimCore.Collision;
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
    /// (null: unset), so a test can check the environment without touching the process environment, and
    /// with the server build's <see cref="StreamRunner"/> as <c>streams record</c>'s stream runner.
    /// </summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? environmentName) =>
        Run(args, stdout, stderr, environmentName, static (stream, worlds) => StreamRunner.Run(stream.Span, worlds));

    /// <summary>
    /// Runs the CLI with <paramref name="environmentName"/> standing in for <c>DOTNET_ENVIRONMENT</c> and
    /// <paramref name="runStream"/> as the runner <c>streams record</c> runs every built stream through
    /// (contracts/simcore-cli.md#streams-record step 3), so a test can inject a step fault without the CLI
    /// calling core internals. An unknown environment name writes <c>DOTNET_ENVIRONMENT: &lt;message&gt;</c>
    /// to stderr and exits 2 before any command runs; a parse error is logged and exits 2.
    /// </summary>
    internal static int Run(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        string? environmentName,
        Func<ReadOnlyMemory<byte>, IReadOnlyList<CollisionWorld>, byte[]> runStream)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(runStream);
        var (_, environmentError) = SimEnvironment.Resolve(environmentName);
        if (environmentError is not null)
        {
            stderr.Write($"{environmentError.Path}: {environmentError.Message}\n");
            return CliExitCodes.Usage;
        }

        using var logger = CliLogging.Create(stderr);
        var root = new RootCommand("simcore: the shared simulation core's tools (specs/015-shared-simulation-core/contracts/simcore-cli.md).")
        {
            AgreementCommand.Create(stdout, stderr, logger),
            ConfigCommand.Create(stdout, stderr, logger),
            ContentCommand.Create(stdout, stderr, logger),
            GoldenCommand.Create(stdout, stderr, logger),
            MeasureCommand.Create(stdout, logger),
            StreamsCommand.Create(stdout, stderr, logger, runStream),
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
