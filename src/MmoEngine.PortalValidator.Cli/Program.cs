using System.CommandLine;
using System.CommandLine.Invocation;
using MmoEngine.Maps;

namespace MmoEngine.PortalValidator.Cli;

/// <summary>
/// <c>portal-validator &lt;map.json&gt; [--json]</c> (contracts/map-format.md). Writes one line per
/// finding, <c>&lt;Code&gt; &lt;location&gt; &lt;message&gt;</c>, or a JSON array with <c>--json</c>, on
/// stdout. Exits 0 with no findings, 1 with findings, and 2 when the map can't be read or fails its
/// schema checks (or the arguments are invalid).
/// </summary>
public static class Program
{
    public const int ExitClean = 0;
    public const int ExitFindings = 1;
    public const int ExitInvalidMap = 2;

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>The whole CLI with its output streams injected, so tests run it in process.</summary>
    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var mapArgument = new Argument<string>("map") { Description = "The map file (JSON, format version 1)." };
        var jsonOption = new Option<bool>("--json") { Description = "Write the findings as a JSON array." };
        var root = new RootCommand("Reports portal leaks, dead portals, missing closed-state policies and sealing failures in a map.")
        {
            mapArgument,
            jsonOption,
        };
        root.SetAction(parse => Validate(parse.GetValue(mapArgument)!, parse.GetValue(jsonOption), stdout, stderr));

        var result = root.Parse(args);
        if (result.Action is ParseErrorAction)
        {
            foreach (var error in result.Errors)
            {
                stderr.WriteLine($"portal-validator: {error.Message}");
            }

            return ExitInvalidMap;
        }

        return result.Invoke(new InvocationConfiguration { Output = stdout, Error = stderr });
    }

    private static int Validate(string path, bool json, TextWriter stdout, TextWriter stderr)
    {
        MapDocument map;
        try
        {
            map = MapDocument.Load(path);
        }
        catch (MapFormatException ex)
        {
            stderr.WriteLine($"portal-validator: {ex.Message}");
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

        stderr.WriteLine($"portal-validator: {map.Name}: {findings.Count} finding(s)");
        return findings.Count == 0 ? ExitClean : ExitFindings;
    }
}
