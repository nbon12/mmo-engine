using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim check --out &lt;dir&gt; [--options &lt;check-options.json&gt;]</c>: the analytic checks
/// against the core (the claim-radius floor, the merge distance and lane bridging; FR-031), written to
/// <c>checks.json</c>. Exit codes: 0 when every check is within tolerance, 4 when any is outside it, 2 for
/// invalid arguments or options, 1 for any other error (contracts/config-and-cli.md).
/// </summary>
internal sealed class CheckCommand : ICliCommand
{
    public string Name => "check";

    public Command Create(CliContext context)
    {
        var output = new Option<DirectoryInfo>("--out") { Description = "The folder checks.json is written to; created if needed.", Required = true };
        var options = new Option<FileInfo?>("--options")
        {
            Description = "Check inputs as JSON (every field of the defaults: seed, floor, mergeDistance, laneBridging); default: the issue's worked examples.",
        };
        var command = new Command(Name, "Checks the claim-radius floor, merge distance and lane-bridging estimate against the core; writes checks.json.")
        {
            output,
            options,
        };

        command.SetAction(parse => Execute(context, parse.GetValue(output)!, parse.GetValue(options)));
        return command;
    }

    private static int Execute(CliContext context, DirectoryInfo output, FileInfo? optionsFile)
    {
        AnalyticCheckOptions options;
        try
        {
            options = optionsFile is null ? AnalyticCheckOptions.Default : AnalyticCheckOptions.FromJson(File.ReadAllText(optionsFile.FullName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            context.Logger.Error("Invalid check options {File}: {Error}", optionsFile?.FullName, ex.Message);
            return ExitCodes.InvalidConfig;
        }

        CheckReport report;
        try
        {
            report = AnalyticChecks.RunAll(options);
        }
        catch (ArgumentException ex)
        {
            context.Logger.Error("Invalid check options: {Error}", ex.Message);
            return ExitCodes.InvalidConfig;
        }

        try
        {
            var path = report.WriteJson(output.FullName);
            var failures = report.Failures();
            foreach (var failure in failures)
            {
                context.Logger.Warning("Check outside tolerance: {Failure}", failure);
            }

            context.Logger.Information("Analytic checks {Outcome}: {Path}", report.Passed ? "passed" : "failed", path);
            var line = new JsonObject
            {
                ["checks"] = path,
                ["passed"] = report.Passed,
                ["seed"] = report.Seed,
                ["failures"] = new JsonArray([.. failures.Select(f => (JsonNode)f)]),
            };
            context.Stdout.WriteLine(line.ToJsonString());
            return report.Passed ? ExitCodes.Clean : ExitCodes.OutsideTolerance;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.Fatal(ex, "Could not write {File}: {Message}", AnalyticChecks.FileName, ex.Message);
            return ExitCodes.Error;
        }
    }
}
