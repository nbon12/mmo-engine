using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using SimulatorEnvironmentNames = MmoEngine.Regionizer.Simulator.Config.EnvironmentNames;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/simcore-cli.md#settings-output-and-exit-codes: <c>DOTNET_ENVIRONMENT</c> must be
/// Development, Dev, Test, Staging or Production, and is Development when unset. An invalid value exits 2
/// and names the variable. The known set is the simulator's, kept equal by
/// <see cref="KnownEnvironmentsEqualTheSimulators"/>. The tests pass the name to <c>Program.Run</c>
/// directly, so none reads or sets the process's environment.
/// </summary>
public sealed class EnvironmentTests
{
    private const string UnknownEnvironmentMessage =
        "DOTNET_ENVIRONMENT: must be one of Development, Dev, Test, Staging, Production, was 'Prod'";

    [Fact]
    public void UnknownEnvironmentExitsTwoNamingTheVariable()
    {
        var unknown = Run("Prod", "config", "check", DefaultConfigPath);

        Assert.True(unknown.Exit == 2, $"environment 'Prod': exit {unknown.Exit}, expected 2; stderr: {unknown.Stderr}");
        Assert.Contains(UnknownEnvironmentMessage, unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, unknown.Stdout);

        // Unset: Development, so the valid placeholders check cleanly.
        var unset = Run(null, "config", "check", DefaultConfigPath);

        Assert.True(unset.Exit == 0, $"environment unset: exit {unset.Exit}, expected 0; stdout: {unset.Stdout}; stderr: {unset.Stderr}");
        Assert.DoesNotContain("DOTNET_ENVIRONMENT:", unset.Stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, unset.Stdout);
    }

    [Fact]
    public void KnownEnvironmentsEqualTheSimulators()
    {
        Assert.Equal(SimulatorEnvironmentNames.Allowed, SimEnvironment.Allowed);
        Assert.Equal("Development", SimEnvironment.Default);
        Assert.Equal(SimulatorEnvironmentNames.Default, SimEnvironment.Default);
        Assert.Equal(SimulatorEnvironmentNames.VariableName, SimEnvironment.VariableName);
    }

    private static string DefaultConfigPath => RepoPaths.Combine("simcore", "config", "default.json");

    /// <summary>Runs the CLI in process with <paramref name="environmentName"/> as <c>DOTNET_ENVIRONMENT</c> (null: unset).</summary>
    private static (int Exit, string Stdout, string Stderr) Run(string? environmentName, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = Program.Run(args, stdout, stderr, environmentName: environmentName);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
