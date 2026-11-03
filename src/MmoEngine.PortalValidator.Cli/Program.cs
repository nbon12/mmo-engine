using System.CommandLine;
using System.CommandLine.Invocation;
using MmoEngine.Maps;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace MmoEngine.PortalValidator.Cli;

/// <summary>
/// <c>portal-validator &lt;map.json&gt; [--json]</c> (contracts/map-format.md). Writes one line per
/// finding, <c>&lt;Code&gt; &lt;location&gt; &lt;message&gt;</c>, or a JSON array with <c>--json</c>, on
/// stdout, and nothing else there. Diagnostics (argument errors, a map that can't be read, the
/// finding count) are Serilog compact JSON on stderr, as <c>regionizer-sim</c> writes them. Exits 0
/// with no findings, 1 with findings, and 2 when the map can't be read or fails its schema checks
/// (or the arguments are invalid).
/// </summary>
public static class Program
{
    public const int ExitClean = 0;
    public const int ExitFindings = 1;
    public const int ExitInvalidMap = 2;

    /// <summary>The <c>Tool</c> property on every log event.</summary>
    internal const string ToolName = "portal-validator";

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>The whole CLI with its output streams injected, so tests run it in process.</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Tool", ToolName)
            .WriteTo.Sink(new TextWriterSink(stderr, new CompactJsonFormatter()))
            .CreateLogger();

        var mapArgument = new Argument<string>("map") { Description = "The map file (JSON, format version 1)." };
        mapArgument.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && result.Tokens[0].Value.Length == 0)
            {
                result.AddError("The map path must not be empty.");
            }
        });
        var jsonOption = new Option<bool>("--json") { Description = "Write the findings as a JSON array." };
        var root = new RootCommand("Reports portal leaks, dead portals, missing closed-state policies and sealing failures in a map.")
        {
            mapArgument,
            jsonOption,
        };
        root.SetAction(parse => Validate(parse.GetValue(mapArgument)!, parse.GetValue(jsonOption), stdout, logger));

        var result = root.Parse(args);
        if (result.Action is ParseErrorAction)
        {
            foreach (var error in result.Errors)
            {
                logger.Error("Invalid arguments: {Error}", error.Message);
            }

            return ExitInvalidMap;
        }

        return result.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }

    private static int Validate(string path, bool json, TextWriter stdout, Serilog.ILogger logger)
    {
        MapDocument map;
        try
        {
            map = MapDocument.Load(path);
        }
        catch (MapFormatException ex)
        {
            logger.Error("Map {MapPath} could not be read or failed its schema checks: {Error}", path, ex.Message);
            return ExitInvalidMap;
        }

        var findings = Maps.PortalValidator.Validate(map);
        if (json)
        {
            stdout.WriteLine(PortalFindingJson.Serialize(findings));
        }
        else
        {
            foreach (var finding in findings)
            {
                stdout.WriteLine(finding.ToString());
            }
        }

        logger.Information("Validated map {MapName} ({MapPath}): {FindingCount} finding(s)", map.Name, path, findings.Count);
        return findings.Count == 0 ? ExitClean : ExitFindings;
    }

    /// <summary>Writes each event, formatted, to a text writer, so tests can capture stderr in process.</summary>
    private sealed class TextWriterSink(TextWriter writer, ITextFormatter formatter) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }
}
