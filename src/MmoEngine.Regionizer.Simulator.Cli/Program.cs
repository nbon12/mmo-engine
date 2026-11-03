using System.CommandLine;
using MmoEngine.Regionizer.Simulator.Cli.Commands;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace MmoEngine.Regionizer.Simulator.Cli;

/// <summary>
/// <c>regionizer-sim</c>: runs the regionizer simulator (contracts/config-and-cli.md). Subcommands are
/// the <see cref="ICliCommand"/> implementations in <c>Commands/</c>. Logs are Serilog compact JSON on
/// stderr; results go to stdout.
/// </summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Runs the CLI with explicit writers, as <see cref="Main"/> does with the console.</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr) => Run(args, stdout, stderr, new RunOptions());

    /// <summary>Runs the CLI with run options from the caller (tests inject step hooks here).</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, RunOptions runOptions)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(runOptions);
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Tool", "regionizer-sim")
            .WriteTo.Sink(new TextWriterSink(stderr, new CompactJsonFormatter()))
            .CreateLogger();

        var context = new CliContext(stdout, stderr, logger, Environment.GetEnvironmentVariable(EnvironmentNames.VariableName), runOptions);
        var root = new RootCommand("The regionizer simulator: deterministic, seeded runs of the regionizer core.");
        foreach (var command in CliCommands.All())
        {
            root.Subcommands.Add(command.Create(context));
        }

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                logger.Error("Invalid arguments: {Error}", error.Message);
            }

            return ExitCodes.InvalidConfig;
        }

        return parse.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }

    /// <summary>Writes each event, formatted, to a text writer.</summary>
    private sealed class TextWriterSink(TextWriter writer, ITextFormatter formatter) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }
}
