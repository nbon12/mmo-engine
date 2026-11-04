using System.CommandLine;
using System.Net.Http;
using System.Text.Json.Nodes;
using ClickHouse.Driver;
using MmoEngine.Harness.Golden;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Rules;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Cli;

/// <summary>The <c>harness</c> subcommands (contracts/harness-cli.md).</summary>
internal static class Commands
{
    public static IEnumerable<Command> All(CliContext context)
    {
        yield return Schema(context);
        yield return Load(context);
        yield return RulesCommand(context);
        yield return GoldenCommand(context);
        foreach (var command in NetworkCommands.All(context))
        {
            yield return command;
        }
    }

    private static Option<string?> ClickHouseOption() =>
        new("--clickhouse") { Description = $"ClickHouse connection string; default: ${HarnessSettings.ClickHouseVariable}." };

    private static Command Schema(CliContext context)
    {
        var clickHouse = ClickHouseOption();
        var apply = new Command("apply", "Applies the store's DDL (idempotent).") { clickHouse };
        apply.SetAction(parse => WithStore(context, parse.GetValue(clickHouse), async store =>
        {
            await store.ApplySchemaAsync().ConfigureAwait(false);
            var tables = SchemaScripts.Tables();
            context.Logger.Information("Schema applied: {Count} tables", tables.Count);
            context.Stdout.WriteLine(new JsonObject { ["tables"] = new JsonArray([.. tables.Select(t => (JsonNode)t)]) }.ToJsonString());
            return HarnessExitCodes.Ok;
        }));
        return new Command("schema", "The store's schema.") { apply };
    }

    private static Command Load(CliContext context)
    {
        var folder = new Argument<DirectoryInfo>("folder") { Description = "A run folder (manifest.json) or a sweep folder (sweep.json)." };
        var clickHouse = ClickHouseOption();
        var command = new Command("load", "Loads a run folder or a sweep folder into the store (idempotent).") { folder, clickHouse };
        command.SetAction(parse => WithStore(context, parse.GetValue(clickHouse), async store =>
        {
            var path = parse.GetValue(folder)!.FullName;
            var loader = new RunLoader(store, context.Logger);
            try
            {
                if (File.Exists(Path.Combine(path, "sweep.json")))
                {
                    var sweep = await loader.LoadSweepAsync(path).ConfigureAwait(false);
                    context.Stdout.WriteLine(new JsonObject
                    {
                        ["sweep"] = sweep.Sweep,
                        ["cellRows"] = sweep.CellRows,
                        ["runsLoaded"] = sweep.Runs.Count(r => !r.AlreadyLoaded),
                        ["runsAlreadyLoaded"] = sweep.Runs.Count(r => r.AlreadyLoaded),
                        ["runFoldersMissing"] = sweep.RunFoldersMissing,
                    }.ToJsonString());
                }
                else
                {
                    var run = await loader.LoadRunAsync(path).ConfigureAwait(false);
                    context.Stdout.WriteLine(new JsonObject
                    {
                        ["runId"] = run.RunId,
                        ["alreadyLoaded"] = run.AlreadyLoaded,
                        ["tickRows"] = run.TickRows,
                        ["eventRows"] = run.EventRows,
                    }.ToJsonString());
                }

                return HarnessExitCodes.Ok;
            }
            catch (RunFolderException ex)
            {
                context.Logger.Error("Refused: {Error}", ex.Message);
                return HarnessExitCodes.Invalid;
            }
        }));
        return command;
    }

    private static Command RulesCommand(CliContext context)
    {
        var runId = new Option<string>("--run-id") { Description = "The loaded run to evaluate.", Required = true };
        var rules = new Option<DirectoryInfo>("--rules") { Description = "The rules folder.", DefaultValueFactory = _ => new DirectoryInfo("harness/rules") };
        var baselines = new Option<DirectoryInfo?>("--baselines") { Description = "The baselines folder; default harness/baselines." };
        var artifacts = new Option<DirectoryInfo?>("--artifacts") { Description = "Where a run that doesn't pass keeps its manifest, seed and inputs." };
        var clickHouse = ClickHouseOption();
        var run = new Command("run", "Runs every rule that applies to the run; exit 0 pass, 5 failed, 6 inconclusive, 2 invalid rule.")
        {
            runId, rules, baselines, artifacts, clickHouse,
        };
        run.SetAction(parse =>
        {
            IReadOnlyList<RuleDefinition> definitions;
            IReadOnlyList<Baseline> accepted;
            try
            {
                definitions = RuleFileParser.LoadFolder(parse.GetValue(rules)!.FullName);
                accepted = BaselineFile.LoadFolder(parse.GetValue(baselines)?.FullName ?? "harness/baselines");
            }
            catch (RuleFileException ex)
            {
                context.Logger.Error("Invalid rule or baseline file: {Error}", ex.Message);
                return HarnessExitCodes.Invalid;
            }

            return WithStore(context, parse.GetValue(clickHouse), async store =>
            {
                try
                {
                    var report = await new RuleRunner(store, context.Logger)
                        .RunAsync(parse.GetValue(runId)!, definitions, accepted, parse.GetValue(artifacts)?.FullName).ConfigureAwait(false);
                    context.Stdout.WriteLine(new JsonObject
                    {
                        ["runId"] = report.RunId,
                        ["verdict"] = report.Verdict,
                        ["artifacts"] = report.ArtifactsFolder,
                        ["results"] = new JsonArray([.. report.Results.Select(r => (JsonNode)new JsonObject
                        {
                            ["ruleId"] = r.RuleId,
                            ["outcome"] = r.OutcomeText,
                            ["value"] = r.Value,
                            ["threshold"] = r.Threshold,
                            ["samples"] = r.Samples,
                            ["note"] = r.Note,
                        })]),
                    }.ToJsonString());
                    return report.ExitCode;
                }
                catch (RunNotLoadedException ex)
                {
                    context.Logger.Error("{Error}", ex.Message);
                    return HarnessExitCodes.Invalid;
                }
            });
        });
        var syncFolder = new Option<DirectoryInfo>("--baselines")
        {
            Description = "The baselines folder.",
            DefaultValueFactory = _ => new DirectoryInfo("harness/baselines"),
        };
        var syncStore = ClickHouseOption();
        var sync = new Command("sync", "Mirrors the committed baselines into the baselines table (the runner reads the files, never the table).")
        {
            syncFolder, syncStore,
        };
        sync.SetAction(parse =>
        {
            IReadOnlyList<Baseline> committed;
            try
            {
                committed = BaselineFile.LoadFolder(parse.GetValue(syncFolder)!.FullName);
            }
            catch (RuleFileException ex)
            {
                context.Logger.Error("Invalid baseline file: {Error}", ex.Message);
                return HarnessExitCodes.Invalid;
            }

            return WithStore(context, parse.GetValue(syncStore), async store =>
            {
                var rows = await BaselineFile.SyncAsync(store, committed).ConfigureAwait(false);
                context.Stdout.WriteLine(new JsonObject { ["baselines"] = rows }.ToJsonString());
                return HarnessExitCodes.Ok;
            });
        });
        return new Command("rules", "Pass rules and their baselines.") { run, new Command("baselines", "Baselines.") { sync } };
    }

    private static Command GoldenCommand(CliContext context)
    {
        var runFolder = new Option<DirectoryInfo>("--run") { Description = "A completed run folder.", Required = true };
        var config = new Option<FileInfo>("--config") { Description = "The run config the run was made from.", Required = true };
        var output = new Option<DirectoryInfo>("--out") { Description = "The golden folder to write.", Required = true };
        var name = new Option<string?>("--name") { Description = "The golden run's name; default: the folder name." };
        var record = new Command("record", "Records a golden run from a completed run folder. Its output is committed; CI never records.")
        {
            runFolder, config, output, name,
        };
        record.SetAction(parse => Guard(context, () =>
        {
            var target = parse.GetValue(output)!.FullName;
            var golden = GoldenRuns.Record(parse.GetValue(runFolder)!.FullName, parse.GetValue(config)!.FullName, target,
                parse.GetValue(name) ?? Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)));
            context.Stdout.WriteLine(golden.ToJsonString());
            return HarnessExitCodes.Ok;
        }));

        var goldenFolder = new Option<DirectoryInfo>("--golden") { Description = "The golden folder.", Required = true };
        var replay = new Option<DirectoryInfo>("--run") { Description = "The replayed run folder.", Required = true };
        var check = new Command("check", "Compares a replayed run with a golden run tick by tick; exit 0 match, 7 diverged, 8 not comparable.")
        {
            goldenFolder, replay,
        };
        check.SetAction(parse => Guard(context, () =>
        {
            var comparison = GoldenRuns.Compare(parse.GetValue(goldenFolder)!.FullName, parse.GetValue(replay)!.FullName);
            if (comparison.Outcome == GoldenOutcome.Match)
            {
                context.Logger.Information("Golden run {Name}: {Message}", comparison.Name, comparison.Message);
            }
            else
            {
                context.Logger.Error("Golden run {Name}: {Outcome}: {Message}", comparison.Name, comparison.Outcome, comparison.Message);
            }

            context.Stdout.WriteLine(comparison.ToJson().ToJsonString());
            return comparison.ExitCode;
        }));
        return new Command("golden", "Golden runs: per-tick state hashes replayed on the fixed runner class.") { record, check };
    }

    /// <summary>Runs a file-only action, mapping a refused input to exit 2 and an I/O failure to exit 1.</summary>
    internal static int Guard(CliContext context, Func<int> action)
    {
        try
        {
            return action();
        }
        catch (RunFolderException ex)
        {
            context.Logger.Error("Refused: {Error}", ex.Message);
            return HarnessExitCodes.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.Error("I/O error: {Error}", ex.Message);
            return HarnessExitCodes.Error;
        }
    }

    /// <summary>An exception that means the store couldn't be reached or failed (exit 1).</summary>
    internal static bool IsStoreFailure(Exception ex) =>
        ex is HttpRequestException or ClickHouseServerException or TaskCanceledException or IOException;

    /// <summary>Validates the settings, opens the store and runs an action; infrastructure failures exit 1.</summary>
    private static int WithStore(CliContext context, string? clickHouse, Func<IHarnessStore, Task<int>> action)
    {
        var settings = context.Settings(clickHouse);
        if (settings is null)
        {
            return HarnessExitCodes.Invalid;
        }

        var store = context.Options.CreateStore(settings.ClickHouseConnectionString);
        try
        {
            return action(store).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            context.Logger.Error("Store error: {Error}", ex.Message);
            return HarnessExitCodes.Error;
        }
        finally
        {
            (store as IDisposable)?.Dispose();
        }
    }
}
