using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace MmoEngine.SimCore.Cli;

/// <summary>
/// The CLI's logger (specs/015-shared-simulation-core/contracts/simcore-cli.md#settings-output-and-exit-codes):
/// Serilog compact JSON on stderr, one event per line, each with the property <c>Tool</c> = <c>simcore</c>,
/// as <c>src/MmoEngine.Harness.Cli/Program.cs</c> logs. Stdout carries only results.
/// </summary>
internal static class CliLogging
{
    /// <summary>The value of every event's <c>Tool</c> property.</summary>
    public const string ToolName = "simcore";

    /// <summary>A logger at Information and above that writes compact JSON lines to <paramref name="stderr"/>.</summary>
    public static Logger Create(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Tool", ToolName)
            .WriteTo.Sink(new TextWriterSink(stderr, new CompactJsonFormatter()))
            .CreateLogger();
    }

    /// <summary>Writes each event, formatted, to a text writer, and flushes it.</summary>
    private sealed class TextWriterSink(TextWriter writer, ITextFormatter formatter) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }
}
