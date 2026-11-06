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
        var shard = new Option<string?>("--shard") { Description = "Run only shard k of n (\"k/n\", 1-based): every n-th run, for a sweep split across machines." };
        var command = new Command(Name, "Runs a sweep: every cell and seed, in parallel, with sweep.json and cells.csv.")
        {
            spec,
            output,
            parallelism,
            scenarios,
            shard,
        };

        command.SetAction(parse => Execute(
            context,
            parse.GetValue(spec)!,
            parse.GetValue(output)!,
            parse.GetValue(parallelism),
            parse.GetValue(scenarios),
            parse.GetValue(shard)));
        return command;
    }

    /// <summary>Parses <c>k/n</c> with 1 ≤ k ≤ n.</summary>
    internal static bool TryParseShard(string text, out int shard, out int count)
    {
        shard = count = 0;
        var parts = text.Split('/');
        return parts.Length == 2
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out shard)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)
            && count >= 1 && shard >= 1 && shard <= count;
    }

    private static int Execute(CliContext context, FileInfo specFile, DirectoryInfo output, int? parallelism, DirectoryInfo? scenarios, string? shardText)
    {
        if (parallelism is < 1)
        {
            context.Logger.Error("Invalid arguments: {Error}", $"--parallelism must be at least 1, was {parallelism}");
            return ExitCodes.InvalidConfig;
        }

        var shard = 1;
        var shards = 1;
        if (shardText is not null && !TryParseShard(shardText, out shard, out shards))
        {
            context.Logger.Error("Invalid arguments: {Error}", $"--shard must be k/n with 1 <= k <= n, was {shardText}");
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
            var plan = shards == 1 ? load.Plan! : load.Plan!.Shard(shard, shards);
            var outcome = SweepDriver.Run(plan, output.FullName, options);
            var line = new JsonObject
            {
                ["sweep"] = load.Plan!.Spec.Name,
                ["exitCode"] = outcome.ExitCode,
                ["runs"] = outcome.Runs.Count,
                ["shard"] = shards == 1 ? null : $"{shard}/{shards}",
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
