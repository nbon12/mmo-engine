using System.CommandLine;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim run &lt;config.json&gt; --out &lt;dir&gt; [--seed N] [--ticks N]</c>: one run.
/// Exit codes: 0 clean, 3 invariant violation or timer fault, 2 invalid configuration (an invalid map
/// file included) or arguments, or an <c>--out</c> folder that is not a run folder, 1 any other error
/// (contracts/config-and-cli.md).
/// </summary>
internal sealed class RunCommand : ICliCommand
{
    public string Name => "run";

    public Command Create(CliContext context)
    {
        var config = new Argument<FileInfo>("config") { Description = "The run config JSON, deep-merged over simulator/scenarios/<scenario>.json." };
        var output = new Option<DirectoryInfo>(RunFolder.OutOption)
        {
            Description = "The run folder: missing, empty, or an earlier run's folder (emptied first). Any other non-empty folder is refused (exit 2).",
            Required = true,
        };
        var seed = new Option<ulong?>("--seed") { Description = "Overrides the config's seed." };
        var ticks = new Option<long?>("--ticks") { Description = "Overrides the config's durationTicks." };
        var scenarios = new Option<DirectoryInfo?>("--scenarios") { Description = "The scenario defaults directory (default: found next to the tool or above the working directory)." };
        var command = new Command(Name, "Runs one scenario and writes its manifest, metrics, events and summary.")
        {
            config,
            output,
            seed,
            ticks,
            scenarios,
        };

        command.SetAction(parse => Execute(
            context,
            parse.GetValue(config)!,
            parse.GetValue(output)!,
            new RunConfigOverrides(parse.GetValue(seed), parse.GetValue(ticks)),
            parse.GetValue(scenarios)));
        return command;
    }

    private static int Execute(CliContext context, FileInfo configFile, DirectoryInfo output, RunConfigOverrides overrides, DirectoryInfo? scenarios)
    {
        var load = RunConfigLoader.LoadFile(configFile.FullName, overrides, context.EnvironmentName, scenarios?.FullName);
        if (!load.Succeeded)
        {
            return context.InvalidConfig(load.Errors);
        }

        try
        {
            var options = context.RunOptions with { Logger = context.Logger, EnvironmentName = context.EnvironmentName };
            var result = SimulationRunner.Run(load.Loaded!, output.FullName, options);
            var line = new JsonObject
            {
                ["runId"] = result.RunId,
                ["exitCode"] = result.ExitCode,
                ["completed"] = result.Completed,
                ["ticks"] = result.TicksRun,
                ["finalHash"] = RunFolder.FormatHash(result.FinalHash),
                ["summary"] = result.SummaryPath,
                ["firstViolation"] = result.FirstViolation is null ? null : RunSummary.ToJson(result.FirstViolation),
            };
            context.Stdout.WriteLine(line.ToJsonString());
            return result.ExitCode;
        }
        catch (ConfigValidationException ex)
        {
            // An --out folder that is not a run folder: refused before anything is written or deleted.
            return context.InvalidConfig(ex.Errors);
        }
        catch (Exception ex)
        {
            context.Logger.Fatal(ex, "Run failed: {Message}", ex.Message);
            return ExitCodes.Error;
        }
    }
}
