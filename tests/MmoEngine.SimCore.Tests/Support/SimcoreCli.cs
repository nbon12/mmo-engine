using MmoEngine.SimCore.Cli;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Runs the <c>simcore</c> CLI in process, with the environment fixed to <c>Development</c> so no test
/// reads or sets the process's <c>DOTNET_ENVIRONMENT</c>. Callers pass paths rooted at
/// <see cref="RepoPaths.Root"/> or at a <see cref="TempDirectories"/> directory, never relative ones.
/// </summary>
public static class SimcoreCli
{
    /// <summary>The exit code and everything written to stdout and stderr.</summary>
    public static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, environmentName: "Development");
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
