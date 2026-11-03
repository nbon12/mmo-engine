using System.CommandLine;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// One <c>regionizer-sim</c> subcommand. Every non-abstract implementation in this assembly with a
/// parameterless constructor is registered automatically, ordered by name, so adding a command means
/// adding one file under <c>Commands/</c>.
/// </summary>
internal interface ICliCommand
{
    /// <summary>The subcommand's name, for example <c>run</c>.</summary>
    string Name { get; }

    /// <summary>Builds the command. Its action returns the process exit code (<see cref="ExitCodes"/>).</summary>
    Command Create(CliContext context);
}

/// <summary>What every command shares: the output writers, the logger, the environment name and test hooks.</summary>
internal sealed class CliContext(TextWriter stdout, TextWriter stderr, Serilog.ILogger logger, string? environmentName, RunOptions runOptions)
{
    public TextWriter Stdout { get; } = stdout;

    public TextWriter Stderr { get; } = stderr;

    /// <summary>Serilog, compact JSON on stderr.</summary>
    public Serilog.ILogger Logger { get; } = logger;

    /// <summary>The raw <c>DOTNET_ENVIRONMENT</c> value (null when unset).</summary>
    public string? EnvironmentName { get; } = environmentName;

    /// <summary>Run options from the caller (tests inject hooks here); commands add the logger and environment.</summary>
    public RunOptions RunOptions { get; } = runOptions;

    /// <summary>Logs each configuration error as a structured event naming its JSON path.</summary>
    public int InvalidConfig(IEnumerable<Config.ConfigError> errors)
    {
        foreach (var error in errors)
        {
            Logger.Error("Invalid configuration: {Path}: {Error}", error.Path, error.Message);
        }

        return ExitCodes.InvalidConfig;
    }
}

/// <summary>Finds the commands in this assembly.</summary>
internal static class CliCommands
{
    public static IReadOnlyList<ICliCommand> All() =>
        [.. typeof(ICliCommand).Assembly.GetTypes()
            .Where(t => typeof(ICliCommand).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (ICliCommand)Activator.CreateInstance(t)!)
            .OrderBy(c => c.Name, StringComparer.Ordinal)];
}
