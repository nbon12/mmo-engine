using System.CommandLine;
using MmoEngine.Harness.Store;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace MmoEngine.Harness.Cli;

/// <summary>
/// <c>harness</c>: the telemetry and test harness tools (specs/014-telemetry-test-harness/contracts/harness-cli.md).
/// Logs are Serilog compact JSON on stderr; results are JSON lines on stdout.
/// </summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error, new HarnessCliOptions());

    /// <summary>Runs the CLI with explicit writers and options, as tests do.</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, HarnessCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(options);
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Tool", "harness")
            .WriteTo.Sink(new TextWriterSink(stderr, new CompactJsonFormatter()))
            .CreateLogger();

        var context = new CliContext(stdout, logger, options);
        var root = new RootCommand("The telemetry and test harness: store, loader, pass rules, golden runs, network profiles and bots.");
        foreach (var command in Commands.All(context))
        {
            root.Subcommands.Add(command);
        }

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                logger.Error("Invalid arguments: {Error}", error.Message);
            }

            return HarnessExitCodes.Invalid;
        }

        return parse.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }

    private sealed class TextWriterSink(TextWriter writer, ITextFormatter formatter) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }
}

/// <summary>What the CLI takes from its host: the environment (overridable in tests) and the store factory.</summary>
internal sealed record HarnessCliOptions
{
    /// <summary>Reads an environment variable; tests replace it so they never touch the process environment.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Creates the store from a validated connection string.</summary>
    public Func<string, IHarnessStore> CreateStore { get; init; } = connection => new ClickHouseStore(connection);
}

/// <summary>What every command needs.</summary>
internal sealed record CliContext(TextWriter Stdout, ILogger Logger, HarnessCliOptions Options)
{
    /// <summary>Resolves and validates the store settings, or logs why not (exit 2).</summary>
    public HarnessSettings? Settings(string? clickHouseOption)
    {
        try
        {
            return HarnessSettings.Resolve(
                Options.GetEnvironmentVariable(HarnessSettings.EnvironmentVariable),
                clickHouseOption,
                Options.GetEnvironmentVariable(HarnessSettings.ClickHouseVariable));
        }
        catch (HarnessSettingsException ex)
        {
            Logger.Error("Invalid settings: {Error}", ex.Message);
            return null;
        }
    }
}
