using System.CommandLine;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.View;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim view &lt;run-folder&gt; --tick N --out &lt;file.svg&gt; [--layer L] [--scenarios dir]</c>: the replay
/// viewer (spec 014, US7, FR-033). It replays the run to tick N, checks the state hash against the recording,
/// and draws that tick. Exit codes: 0 drawn, 7 the replay's hash differs from the recording, 2 invalid run
/// folder or arguments, 1 any other error.
/// </summary>
internal sealed class ViewCommand : ICliCommand
{
    public string Name => "view";

    public Command Create(CliContext context)
    {
        var folder = new Argument<DirectoryInfo>("run-folder") { Description = "A recorded run folder." };
        var tick = new Option<long>("--tick") { Description = "The tick to draw.", Required = true };
        var output = new Option<FileInfo>("--out") { Description = "The SVG file to write.", Required = true };
        var layer = new Option<int>("--layer") { Description = "The section layer to draw (default 0, the open world)." };
        var scenarios = new Option<DirectoryInfo?>("--scenarios") { Description = "The scenario defaults directory." };
        var command = new Command(Name, "Replays a recorded run to a tick and draws sections by region, claims, portals and entities as SVG.")
        {
            folder,
            tick,
            output,
            layer,
            scenarios,
        };
        command.SetAction(parse => Execute(context, parse.GetValue(folder)!, parse.GetValue(tick), parse.GetValue(output)!, parse.GetValue(layer), parse.GetValue(scenarios)));
        return command;
    }

    private static int Execute(CliContext context, DirectoryInfo folder, long tick, FileInfo output, int layer, DirectoryInfo? scenarios)
    {
        try
        {
            var frame = ReplayViewer.Replay(folder.FullName, tick, scenarios?.FullName, context.EnvironmentName);
            Directory.CreateDirectory(output.DirectoryName!);
            File.WriteAllText(output.FullName, ReplayViewer.Svg(frame, layer));
            context.Stdout.WriteLine(new JsonObject
            {
                ["svg"] = output.FullName,
                ["tick"] = tick,
                ["layer"] = layer,
                ["stateHash"] = frame.StateHash,
                ["sections"] = frame.Sections.Count(s => s.Key.Layer == layer),
                ["entities"] = frame.Entities.Count(e => e.Layer == layer),
            }.ToJsonString());
            return ExitCodes.Clean;
        }
        catch (ReplayException ex)
        {
            context.Logger.Error("View refused: {Error}", ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.Error("View failed: {Error}", ex.Message);
            return ExitCodes.Error;
        }
    }
}
