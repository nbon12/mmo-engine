using System.CommandLine;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Trace;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim replay &lt;trace.csv&gt; --config &lt;config.json&gt; --out &lt;dir&gt;</c>: a run whose
/// movement comes from a recorded position trace (FR-022) instead of the synthetic models. The
/// trace's seed replaces the config's, so the scheduling and timer streams match the recorded run,
/// and <c>trace.replay</c> is set to the trace so the manifest records it. A <c>trace.record</c> in
/// the config records the replayed run. Exit codes as <c>run</c>: 0 clean, 3 violation or timer
/// fault, 2 invalid configuration, trace or arguments, 1 any other error.
/// </summary>
internal sealed class ReplayCommand : ICliCommand
{
    public string Name => "replay";

    public Command Create(CliContext context)
    {
        var trace = new Argument<FileInfo>("trace") { Description = "The position trace CSV (contracts/trace-format.md)." };
        var config = new Option<FileInfo>("--config") { Description = "The run config JSON; its scenario must place the trace's entities.", Required = true };
        var output = new Option<DirectoryInfo>("--out") { Description = "The run folder; emptied first.", Required = true };
        var scenarios = new Option<DirectoryInfo?>("--scenarios") { Description = "The scenario defaults directory (default: found next to the tool or above the working directory)." };
        var command = new Command(Name, "Runs a scenario with its movement replayed from a recorded position trace.")
        {
            trace,
            config,
            output,
            scenarios,
        };

        command.SetAction(parse => Execute(
            context,
            parse.GetValue(trace)!,
            parse.GetValue(config)!,
            parse.GetValue(output)!,
            parse.GetValue(scenarios)));
        return command;
    }

    private static int Execute(CliContext context, FileInfo traceFile, FileInfo configFile, DirectoryInfo output, DirectoryInfo? scenarios)
    {
        TraceHeader header;
        try
        {
            header = TraceReader.ReadHeader(traceFile.FullName);
        }
        catch (TraceFormatException ex)
        {
            return InvalidTrace(context, ex.Message);
        }

        var overrides = new RunConfigOverrides(header.Seed, null, new Dictionary<string, JsonNode?> { [TraceRunOptions.ReplayPath] = traceFile.FullName });
        var load = RunConfigLoader.LoadFile(configFile.FullName, overrides, context.EnvironmentName, scenarios?.FullName);
        if (!load.Succeeded)
        {
            return context.InvalidConfig(load.Errors);
        }

        var loaded = load.Loaded!;
        if (loaded.Config.Scenario != header.Scenario || loaded.Config.Scheduling.Mode != header.Scheduling)
        {
            context.Logger.Warning(
                "The trace was recorded from {TraceScenario} in {TraceScheduling} mode; replaying in {Scenario} in {Scheduling} mode, so hashes will not match the recorded run",
                header.Scenario,
                header.Scheduling,
                loaded.Config.Scenario,
                loaded.Config.Scheduling.Mode);
        }

        TraceAttachment attachment;
        try
        {
            attachment = TraceRunOptions.Attach(loaded, context.RunOptions with { Logger = context.Logger, EnvironmentName = context.EnvironmentName });
        }
        catch (ConfigValidationException ex)
        {
            return context.InvalidConfig(ex.Errors);
        }

        using (attachment)
        {
            try
            {
                var result = SimulationRunner.Run(loaded, output.FullName, attachment.Options);
                var replay = attachment.Replay!;
                var line = new JsonObject
                {
                    ["runId"] = result.RunId,
                    ["exitCode"] = result.ExitCode,
                    ["completed"] = result.Completed,
                    ["ticks"] = result.TicksRun,
                    ["finalHash"] = RunFolder.FormatHash(result.FinalHash),
                    ["summary"] = result.SummaryPath,
                    ["firstViolation"] = result.FirstViolation is null ? null : RunSummary.ToJson(result.FirstViolation),
                    ["trace"] = attachment.ReplayPath,
                    ["updatesServed"] = replay.UpdatesServed,
                    ["updatesCollapsed"] = replay.UpdatesCollapsed,
                };
                context.Stdout.WriteLine(line.ToJsonString());
                return result.ExitCode;
            }
            catch (Exception ex) when (ex is TraceMismatchException or TraceFormatException)
            {
                return InvalidTrace(context, ex.Message);
            }
            catch (Exception ex)
            {
                context.Logger.Fatal(ex, "Replay failed: {Message}", ex.Message);
                return ExitCodes.Error;
            }
        }
    }

    private static int InvalidTrace(CliContext context, string message)
    {
        context.Logger.Error("Invalid trace: {Path}: {Error}", TraceRunOptions.ReplayPath, message);
        return ExitCodes.InvalidConfig;
    }
}
