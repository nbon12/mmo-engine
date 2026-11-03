using System.CommandLine;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Sweep;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim sweep &lt;sweep.json&gt; --out &lt;dir&gt; [--parallelism N]</c>: every cell and seed
/// of a sweep, in parallel, into <c>&lt;out&gt;/&lt;sweep&gt;/</c>. Exit codes: 0 all clean, 3 any
/// violation or timer fault, 2 invalid specification or arguments, 1 any other error
/// (contracts/config-and-cli.md).
/// </summary>
internal sealed class SweepCommand : ICliCommand
{
    public string Name => "sweep";

    public Command Create(CliContext context)
    {
        var spec = new Argument<FileInfo>("sweep") { Description = "The sweep specification JSON (contracts/config-and-cli.md)." };
        var output = new Option<DirectoryInfo>("--out") { Description = "The results directory; the sweep writes <out>/<name>/.", Required = true };
        var parallelism = new Option<int?>("--parallelism") { Description = "Runs at once (default: the processor count)." };
        var scenarios = new Option<DirectoryInfo?>("--scenarios") { Description = "The scenario defaults directory (default: found next to the tool or above the working directory)." };
        var command = new Command(Name, "Runs a sweep: every cell and seed, in parallel, with sweep.json and cells.csv.")
        {
            spec,
            output,
            parallelism,
            scenarios,
        };

        command.SetAction(parse => Execute(
            context,
            parse.GetValue(spec)!,
            parse.GetValue(output)!,
            parse.GetValue(parallelism),
            parse.GetValue(scenarios)));
        return command;
    }

    private static int Execute(CliContext context, FileInfo specFile, DirectoryInfo output, int? parallelism, DirectoryInfo? scenarios)
    {
        if (parallelism is < 1)
        {
            context.Logger.Error("Invalid arguments: {Error}", $"--parallelism must be at least 1, was {parallelism}");
            return ExitCodes.InvalidConfig;
        }

        var load = SweepSpecLoader.LoadFile(specFile.FullName, context.EnvironmentName, scenarios?.FullName);
        if (!load.Succeeded)
        {
            return context.InvalidConfig(load.Errors);
        }

        try
        {
            var options = new SweepRunOptions
            {
                Parallelism = parallelism,
                RunOptions = context.RunOptions with { Logger = null, EnvironmentName = context.EnvironmentName },
                Logger = context.Logger,
            };
            var outcome = SweepDriver.Run(load.Plan!, output.FullName, options);
            var line = new JsonObject
            {
                ["sweep"] = load.Plan!.Spec.Name,
                ["exitCode"] = outcome.ExitCode,
                ["runs"] = outcome.Runs.Count,
                ["completed"] = outcome.Completed,
                ["violations"] = outcome.Violations,
                ["errors"] = outcome.Errors,
                ["index"] = outcome.IndexPath,
                ["cells"] = outcome.CellsPath,
            };
            context.Stdout.WriteLine(line.ToJsonString());
            return outcome.ExitCode;
        }
        catch (Exception ex)
        {
            context.Logger.Fatal(ex, "Sweep failed: {Message}", ex.Message);
            return ExitCodes.Error;
        }
    }
}
