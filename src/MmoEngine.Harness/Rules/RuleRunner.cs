using System.Text.Json;
using System.Text.Json.Nodes;
using ClickHouse.Driver;
using MmoEngine.Harness.Store;
using Serilog;

namespace MmoEngine.Harness.Rules;

/// <summary>The rule runner's report for one run.</summary>
public sealed record RuleRunReport(string RunId, string Verdict, int ExitCode, IReadOnlyList<RuleResult> Results, string? ArtifactsFolder);

/// <summary>A run the runner can't evaluate: it isn't loaded.</summary>
public sealed class RunNotLoadedException(string runId) : Exception($"Run '{runId}' is not loaded in the store.");

/// <summary>
/// Runs every rule that applies to a run's kind, writes one <c>rule_results</c> row per rule, sets the
/// run's verdict, and keeps a run that doesn't pass: its rows go to the retained tables, and its
/// manifest, seed and inputs are written to the artifacts folder (US2, FR-011 to FR-016).
/// </summary>
public sealed class RuleRunner(IHarnessStore store, ILogger logger)
{
    private static readonly string[] ResultColumns = ["run_id", "rule_id", "value", "threshold", "samples", "outcome", "note"];

    public async Task<RuleRunReport> RunAsync(
        string runId,
        IReadOnlyList<RuleDefinition> rules,
        IReadOnlyList<Baseline> baselines,
        string? artifactsFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(baselines);
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is not { Loaded: true })
        {
            throw new RunNotLoadedException(runId);
        }

        var results = new List<RuleResult>();
        foreach (var rule in rules.Where(r => r.AppliesToKind(run.Kind)))
        {
            RuleResult result;
            try
            {
                var query = await store.QueryRuleAsync(rule.Query, runId, cancellationToken).ConfigureAwait(false);
                result = RuleEvaluator.Evaluate(rule, query, BaselineFile.Find(baselines, rule.Id, run.Profile));
            }
            catch (ClickHouseServerException ex)
            {
                result = new RuleResult(rule.Id, RuleOutcome.Error, null, null, 0, $"query failed: {FirstLine(ex.Message)}");
            }

            logger.Information("Rule {RuleId} on {RunId}: {Outcome} ({Note})", rule.Id, runId, result.OutcomeText, result.Note);
            results.Add(result);
        }

        await store.InsertAsync("rule_results", ResultColumns,
            results.Select(r => new object?[] { runId, r.RuleId, r.Value, r.Threshold, r.Samples, r.OutcomeText, r.Note }),
            cancellationToken).ConfigureAwait(false);

        var verdict = RuleEvaluator.Verdict(results);
        var exitCode = RuleEvaluator.ExitCode(results);
        var keep = exitCode != HarnessExitCodes.Ok;
        string? kept = null;
        if (keep)
        {
            await store.RetainAsync(runId, cancellationToken).ConfigureAwait(false);
            if (artifactsFolder is not null)
            {
                kept = WriteArtifacts(artifactsFolder, run, results);
            }
        }

        await store.UpsertRunAsync(run with { Verdict = verdict, Retained = run.Retained || keep }, cancellationToken).ConfigureAwait(false);
        logger.Information("Run {RunId} verdict {Verdict} (exit {ExitCode})", runId, verdict, exitCode);
        return new RuleRunReport(runId, verdict, exitCode, results, kept);
    }

    /// <summary>Writes the failing run's manifest, seed, inputs and rule results to <c>&lt;folder&gt;/&lt;run_id&gt;/</c> (FR-016).</summary>
    internal static string WriteArtifacts(string folder, RunRow run, IReadOnlyList<RuleResult> results)
    {
        var target = Path.Combine(folder, Sanitize(run.RunId));
        Directory.CreateDirectory(target);
        var options = new JsonSerializerOptions { WriteIndented = true };
        JsonNode? manifest;
        try
        {
            manifest = JsonNode.Parse(run.Manifest);
        }
        catch (JsonException)
        {
            manifest = JsonValue.Create(run.Manifest);
        }

        File.WriteAllText(Path.Combine(target, "manifest.json"), manifest?.ToJsonString(options) ?? "{}");

        // The simulator's inputs are its seed and its effective configuration (the manifest's parameters).
        var inputs = new JsonObject
        {
            ["runId"] = run.RunId,
            ["kind"] = run.Kind,
            ["seed"] = run.Seed,
            ["scenario"] = run.Scenario,
            ["configHash"] = run.ConfigHash,
            ["gitSha"] = run.GitSha,
            ["runnerClass"] = run.RunnerClass,
            ["profile"] = run.Profile,
            ["parameters"] = (manifest as JsonObject)?["parameters"]?.DeepClone(),
        };
        File.WriteAllText(Path.Combine(target, "inputs.json"), inputs.ToJsonString(options));

        var array = new JsonArray([.. results.Select(r => (JsonNode)new JsonObject
        {
            ["ruleId"] = r.RuleId,
            ["outcome"] = r.OutcomeText,
            ["value"] = r.Value,
            ["threshold"] = r.Threshold,
            ["samples"] = r.Samples,
            ["note"] = r.Note,
        })]);
        File.WriteAllText(Path.Combine(target, "rule-results.json"), array.ToJsonString(options));
        return target;
    }

    private static string Sanitize(string runId) =>
        new([.. runId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')]);

    private static string FirstLine(string message)
    {
        var newline = message.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? message : message[..newline];
    }
}
