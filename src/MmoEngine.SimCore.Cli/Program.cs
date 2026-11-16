using System.CommandLine;

namespace MmoEngine.SimCore.Cli;

/// <summary>
/// <c>simcore</c>: the shared simulation core's tools
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md).
/// </summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs the CLI with explicit writers, as tests do.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var root = new RootCommand("simcore: the shared simulation core's tools (specs/015-shared-simulation-core/contracts/simcore-cli.md).");
        return root.Parse(args).Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }
}
