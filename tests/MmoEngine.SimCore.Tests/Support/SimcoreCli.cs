using System.Diagnostics;
using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Runs the <c>simcore</c> CLI in process, with the environment fixed to <c>Development</c> so no test
/// reads or sets the process's <c>DOTNET_ENVIRONMENT</c>, or in a fresh child process with
/// <see cref="RunInFreshProcess"/>. Callers pass paths rooted at <see cref="RepoPaths.Root"/> or at a
/// <see cref="TempDirectories"/> directory, never relative ones.
/// </summary>
public static class SimcoreCli
{
    /// <summary>How long <see cref="RunInFreshProcess"/> waits before it kills the child and fails.</summary>
    public static readonly TimeSpan FreshProcessTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The exit code and everything written to stdout and stderr.</summary>
    public static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, environmentName: "Development");
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Runs <c>dotnet &lt;AppContext.BaseDirectory&gt;/simcore.dll &lt;args&gt;</c> in a child process,
    /// so nothing the test process has loaded or computed can reach the run, with the working directory
    /// <see cref="RepoPaths.Root"/> and <c>DOTNET_ENVIRONMENT=Test</c>. The dotnet host is the one running
    /// the tests (<c>DOTNET_HOST_PATH</c>) when set. A child still running after
    /// <see cref="FreshProcessTimeout"/> is killed with its process tree and the test fails, naming the
    /// arguments.
    /// </summary>
    /// <returns>The exit code and everything the child wrote to stdout and stderr.</returns>
    public static (int Exit, string Stdout, string Stderr) RunInFreshProcess(params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var dll = Path.Combine(AppContext.BaseDirectory, "simcore.dll");
        Assert.True(File.Exists(dll), $"{dll} should be built next to the tests");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
        };
        start.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment[SimEnvironment.VariableName] = "Test";
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"simcore {string.Join(' ', args)} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(FreshProcessTimeout))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"simcore {string.Join(' ', args)} did not exit within {FreshProcessTimeout.TotalMinutes} minutes and was killed.");
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
