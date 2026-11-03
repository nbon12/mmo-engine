namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>
/// Reads a measured option (research D15): one cell per option, each metric's mean, min and max over the
/// runs that completed cleanly, and how many runs stopped at a timer fault or an invariant violation. A
/// stopped run is evidence here, not an unreadable result, because timer faults are part of what is compared.
/// </summary>
internal static class MeasuredOptionEvaluator
{
    private const string TimerFault = "TimerFault";

    public static MeasuredOptionResult Evaluate(string resultsDirectory, MeasuredOptionRule rule)
    {
        SweepResult? sweep = null;
        try
        {
            sweep = SweepResults.Read(resultsDirectory, rule.Sweep, allowStoppedRuns: true);
            if (rule.Scenario is not null && sweep.Scenario != rule.Scenario)
            {
                throw new UnreadableException($"{rule.Sweep}/sweep.json: the sweep runs {sweep.Scenario}, but the option is measured in {rule.Scenario}");
            }

            MemoEvaluator.RequirePopulation(sweep, rule.Population);
            if (rule.Scheduling is not null
                && sweep.Cells.SelectMany(c => c.Runs).FirstOrDefault(r => r.Scheduling != rule.Scheduling) is { } other)
            {
                throw new UnreadableException($"run {other.RunId} ({rule.Sweep}): scheduling {other.Scheduling ?? "not recorded"}, but the option is measured with {rule.Scheduling}");
            }

            var cells = rule.Options.Select(option => OptionCell(rule, sweep, option)).ToList();
            var listed = cells.Select(c => c.CellId).ToHashSet(StringComparer.Ordinal);
            if (sweep.Cells.FirstOrDefault(c => !listed.Contains(c.CellId)) is { } extra)
            {
                throw new UnreadableException($"{rule.Sweep}/sweep.json: cell {extra.CellId} is not one of the options {string.Join(", ", rule.Options)}");
            }

            var table = new List<OptionTableRow>();
            foreach (var metric in rule.Metrics)
            {
                table.Add(new OptionTableRow(metric.Label, [.. cells.Select(c => MetricCell(c, metric.Metric))]));
            }

            table.Add(new OptionTableRow("Runs stopped by a timer fault", [.. cells.Select(c => Stopped(c, r => r.StoppedBy == TimerFault))]));
            table.Add(new OptionTableRow(
                "Runs stopped by an invariant violation",
                [.. cells.Select(c => Stopped(c, r => r.StoppedBy is not null && r.StoppedBy != TimerFault))]));

            var runs = cells.SelectMany(c => c.Runs).ToList();
            return Result(
                rule,
                sweep,
                table,
                rule.Absorptions is { } a ? MemoEvaluator.MergeCounting(a, runs.Where(r => r.StoppedBy is null)) : null,
                runs,
                null);
        }
        catch (UnreadableException e)
        {
            return Result(rule, sweep, [], null, [], e.Message);
        }
    }

    private static CellResult OptionCell(MeasuredOptionRule rule, SweepResult sweep, string option)
    {
        var matches = sweep.Cells
            .Where(c => c.Parameters.Any(p => p.Key == rule.Parameter && SweepResults.Label(p.Value) == option))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new UnreadableException($"{rule.Sweep}/sweep.json: no cell with {rule.Parameter} = {option}"),
            _ => throw new UnreadableException($"{rule.Sweep}/sweep.json: {matches.Count} cells set {rule.Parameter} = {option}; an option needs exactly one"),
        };
    }

    /// <summary>The metric's mean over the cell's clean runs, with its min and max, or a note when none completed cleanly.</summary>
    private static string MetricCell(CellResult cell, MetricSpec metric)
    {
        var values = cell.Runs.Where(r => r.StoppedBy is null).Select(r => MemoEvaluator.MetricValue(r, metric)).ToArray();
        return values.Length == 0
            ? "no clean run"
            : $"{N(values.Average())} ({N(values.Min())} to {N(values.Max())})";
    }

    private static string Stopped(CellResult cell, Func<RunResult, bool> stopped) => $"{cell.Runs.Count(stopped)} of {cell.Runs.Count}";

    private static MeasuredOptionResult Result(
        MeasuredOptionRule rule,
        SweepResult? sweep,
        List<OptionTableRow> table,
        string? mergeCounting,
        List<RunResult> runs,
        string? problem)
    {
        var scenario = sweep is null ? string.Empty : $" ({sweep.Scenario})";
        var scheduling = rule.Scheduling is null ? string.Empty : $"; {rule.Scheduling} scheduling in every run's manifest";
        var source = $"sweep `{rule.Sweep}`{scenario}; one cell per value of `{rule.Parameter}`{scheduling}";
        return new MeasuredOptionResult(
            rule.Id,
            rule.Title,
            problem is null,
            rule.Population,
            rule.Description,
            source,
            [.. rule.Options.Select(o => $"{rule.Parameter} = {o}")],
            table,
            mergeCounting,
            [.. runs.OrderBy(r => r.SeedIndex).Select(r => r.Seed).Distinct()],
            [.. runs.Select(r => r.RunId)],
            problem);
    }

    private static string N(double value) => SweepResults.Number(value);
}
