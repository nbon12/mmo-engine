using System.CommandLine;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Cli.Commands;

/// <summary>
/// <c>regionizer-sim memo &lt;results-dir&gt; --rules &lt;memo-rules.json&gt; --out &lt;memo.md&gt;</c>: writes the
/// decision memo from sweep results (FR-032, research D15). Exit codes: 0 when every value and measured
/// option was read, 4 when any could not be (its entry is marked UNREADABLE and the memo is still written),
/// 2 for invalid arguments or rules, 1 for any other error (contracts/config-and-cli.md).
/// </summary>
internal sealed class MemoCommand : ICliCommand
{
    /// <summary>The contract's exit code for a value that cannot be read (shared with the checks' "outside tolerance").</summary>
    private const int Unreadable = ExitCodes.OutsideTolerance;

    public string Name => "memo";

    public Command Create(CliContext context)
    {
        var results = new Argument<DirectoryInfo>("results-dir") { Description = "The folder holding one folder per sweep (<sweep>/sweep.json)." };
        var rules = new Option<FileInfo>("--rules") { Description = "The memo rules, for example simulator/sweeps/memo-rules.json.", Required = true };
        var output = new Option<FileInfo>("--out") { Description = "The memo Markdown file to write; its folder is created if needed.", Required = true };
        var command = new Command(Name, "Writes the decision memo from sweep results.")
        {
            results,
            rules,
            output,
        };

        command.SetAction(parse => Execute(context, parse.GetValue(results)!, parse.GetValue(rules)!, parse.GetValue(output)!));
        return command;
    }

    private static int Execute(CliContext context, DirectoryInfo results, FileInfo rulesFile, FileInfo output)
    {
        MemoRules rules;
        try
        {
            rules = MemoRules.Load(rulesFile.FullName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            context.Logger.Error("Invalid memo rules {File}: {Error}", rulesFile.FullName, ex.Message);
            return ExitCodes.InvalidConfig;
        }

        MemoResult memo;
        try
        {
            memo = MemoGenerator.Write(results.FullName, rules, output.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.Fatal(ex, "Could not write the memo {File}: {Message}", output.FullName, ex.Message);
            return ExitCodes.Error;
        }

        foreach (var row in memo.Rows.Where(r => r.Status != MemoRowStatus.Recommended))
        {
            context.Logger.Warning("Memo value {Value} is {Status}: {Problem}", row.Id, row.Status, row.Problem);
        }

        foreach (var option in memo.MeasuredOptions.Where(o => !o.Readable))
        {
            context.Logger.Warning("Measured option {Option} is unreadable: {Problem}", option.Id, option.Problem);
        }

        context.Logger.Information("Decision memo written: {Path}", output.FullName);
        var line = new JsonObject
        {
            ["memo"] = output.FullName,
            ["success"] = memo.Success,
            ["values"] = memo.Rows.Count,
            ["flagged"] = new JsonArray([.. memo.Rows.Where(r => r.Status == MemoRowStatus.Flagged).Select(r => (JsonNode)r.Id)]),
            ["unreadable"] = new JsonArray([
                .. memo.Rows.Where(r => r.Status == MemoRowStatus.Unreadable).Select(r => (JsonNode)r.Id),
                .. memo.MeasuredOptions.Where(o => !o.Readable).Select(o => (JsonNode)o.Id),
            ]),
            ["measuredOptions"] = memo.MeasuredOptions.Count,
        };
        context.Stdout.WriteLine(line.ToJsonString());
        return memo.Success ? ExitCodes.Clean : Unreadable;
    }
}
