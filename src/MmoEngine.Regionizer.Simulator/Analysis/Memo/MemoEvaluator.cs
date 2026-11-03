using System.Globalization;
using System.Text.Json;

namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>Applies one research D15 rule to its sweeps' curves and builds the memo row.</summary>
internal static class MemoEvaluator
{
    public static MemoRow Evaluate(string resultsDirectory, ValueRule rule)
    {
        var sweeps = new List<SweepResult>();
        try
        {
            foreach (var name in rule.Sweeps)
            {
                sweeps.Add(SweepResults.Read(resultsDirectory, name));
            }

            var points = BuildPoints(rule, sweeps);
            var outcome = rule.Rule switch
            {
                MemoRuleKind.ClaimRadius => ClaimRadius(rule, points),
                MemoRuleKind.SmallestWithin => Within(rule, points, smallest: true),
                MemoRuleKind.LargestWithin => Within(rule, points, smallest: false),
                MemoRuleKind.Lowest => Lowest(rule, points),
                MemoRuleKind.AdoptIfDrop => AdoptIfDrop(rule, points),
                _ => Reading(rule, points),
            };
            return Row(rule, outcome, points, sweeps);
        }
        catch (UnreadableException e)
        {
            return Row(rule, new Outcome(MemoRowStatus.Unreadable, "not read", string.Empty, e.Message), [], sweeps);
        }
    }

    private static List<Point> BuildPoints(ValueRule rule, List<SweepResult> sweeps)
    {
        var numeric = rule.Rule is MemoRuleKind.ClaimRadius or MemoRuleKind.SmallestWithin or MemoRuleKind.LargestWithin;
        var wholeCell = rule.Parameter is null && rule.Rule is MemoRuleKind.Lowest or MemoRuleKind.Reading;
        var xPath = wholeCell ? null : rule.Parameter ?? InferParameter(sweeps);
        var points = new List<Point>();
        foreach (var sweep in sweeps)
        {
            var prefix = sweeps.Count > 1 ? sweep.Name : null;
            foreach (var cell in sweep.Cells)
            {
                string xLabel;
                double? x = null;
                IEnumerable<KeyValuePair<string, JsonElement>> others;
                if (xPath is null)
                {
                    xLabel = cell.Parameters.Count == 0 ? "base" : Join(cell.Parameters);
                    others = [];
                }
                else
                {
                    var found = cell.Parameters.Where(p => p.Key == xPath).ToList();
                    if (found.Count == 0)
                    {
                        throw new UnreadableException($"{sweep.Name}/sweep.json: cell {cell.CellId} does not set '{xPath}'");
                    }

                    xLabel = SweepResults.Label(found[0].Value);
                    x = found[0].Value.ValueKind == JsonValueKind.Number ? found[0].Value.GetDouble() : null;
                    others = cell.Parameters.Where(p => p.Key != xPath);
                }

                if (numeric && x is null)
                {
                    throw new UnreadableException($"{sweep.Name}/sweep.json: cell {cell.CellId} sets '{xPath}' to {xLabel}, but {rule.Rule} needs a number");
                }

                var series = string.Join(", ", new[] { prefix, Join(others) }.Where(s => !string.IsNullOrEmpty(s)));
                var values = cell.Runs.Select(run => MetricValue(run, rule.Metric)).ToArray();
                points.Add(new Point(series, xLabel, x, cell.Runs, values));
            }
        }

        return numeric ? [.. points.OrderBy(p => p.Series, StringComparer.Ordinal).ThenBy(p => p.X)] : points;
    }

    private static string InferParameter(List<SweepResult> sweeps)
    {
        var keys = sweeps.SelectMany(s => s.Cells).SelectMany(c => c.Parameters.Select(p => p.Key)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (keys.Count == 1 && sweeps.SelectMany(s => s.Cells).All(c => c.Parameters.Count == 1))
        {
            return keys[0];
        }

        var listed = keys.Count == 0 ? "none" : string.Join(", ", keys);
        throw new UnreadableException($"the rule names no 'parameter' and the cells do not sweep exactly one (they set: {listed})");
    }

    private static Outcome ClaimRadius(ValueRule rule, List<Point> points)
    {
        var floorInputs = rule.Floor!;
        var floor = floorInputs.Floor;
        var collapseAt = rule.CollapseAt!.Value;
        var steps = rule.StepsBelowCollapse;
        var xs = DistinctX(points);
        var collapse = xs.FindIndex(x => points.Any(p => p.X == x && p.Mean >= collapseAt));
        var floorText = $"the analytic floor {N(floor)} {rule.Unit} ({N(floorInputs.InteractionRangeMetres)} m range + "
            + $"{N(floorInputs.ClosingSpeedMetresPerSecond)} m/s closing at {N(floorInputs.TickRateHz)} Hz)";
        var collapseText = collapse < 0
            ? $"no collapse up to {N(xs[^1])} {rule.Unit}, the top of the sweep"
            : $"collapse at {N(xs[collapse])} {rule.Unit} (mean {rule.Metric.Label} {N(points.Where(p => p.X == xs[collapse]).Max(p => p.Mean))} ≥ {N(collapseAt)})";
        var limit = collapse < 0 ? xs.Count - 1 : collapse - steps;
        if (limit < 0)
        {
            return Flagged($"conflict: {collapseText}, and no swept radius is {steps} step(s) below it");
        }

        var safe = xs[limit];
        if (safe < floor)
        {
            return Flagged($"conflict: {floorText} is above {N(safe)} {rule.Unit}, the largest radius {steps} step(s) below collapse; {collapseText}");
        }

        var below = collapse < 0
            ? $"{collapseText}"
            : $"{collapseText}; {N(safe)} {rule.Unit} is {collapse - limit} sweep step(s) and {N(xs[collapse] - safe)} {rule.Unit} below it";
        return Recommended(rule, N(safe), $"{below}; {N(safe - floor)} {rule.Unit} above {floorText}");
    }

    private static Outcome Within(ValueRule rule, List<Point> points, bool smallest)
    {
        var limit = rule.Limit!;
        var xs = DistinctX(points);
        double Worst(double x) => points.Where(p => p.X == x).Max(p => p.Max);
        bool Ok(double x) => points.Where(p => p.X == x).All(p => limit.Allows(p.Max));
        var ok = xs.Where(Ok).ToList();
        var condition = $"{rule.Metric.Label} {limit.Operator} {N(limit.Threshold)} in every seed";
        if (ok.Count == 0)
        {
            var best = xs.MinBy(Worst);
            return Flagged($"no swept value has {condition}; the best worst seed is {N(Worst(best))} at {N(best)} {rule.Unit}");
        }

        var read = smallest ? ok[0] : ok[^1];
        var at = xs.IndexOf(read);
        var neighbourAt = smallest ? at - 1 : at + 1;
        var neighbour = neighbourAt >= 0 && neighbourAt < xs.Count
            ? $"{N(xs[neighbourAt])} {rule.Unit} measures {N(Worst(xs[neighbourAt]))}{(Ok(xs[neighbourAt]) ? string.Empty : " (fails)")}"
            : $"{N(read)} {rule.Unit} is the {(smallest ? "smallest" : "largest")} swept value";
        var margin = $"worst seed {rule.Metric.Label} {N(Worst(read))} {limit.Operator} {N(limit.Threshold)} at {N(read)} {rule.Unit}; {neighbour}";
        if (rule.AddMargin != 0)
        {
            margin += $"; read {N(read)} {rule.Unit}, plus a margin of {N(rule.AddMargin)} {rule.Unit}";
        }

        return Recommended(rule, N(read + rule.AddMargin), margin);
    }

    private static Outcome Lowest(ValueRule rule, List<Point> points)
    {
        var constraint = rule.Constraint;
        var worst = points.ToDictionary(p => p, p => constraint is null ? 0 : p.Runs.Max(r => MetricValue(r, constraint.Metric)));
        var candidates = points.Where(p => constraint is null || constraint.Allows(worst[p])).ToList();
        if (candidates.Count == 0)
        {
            return Flagged($"no cell has {constraint!.Metric.Label} {constraint.Operator} {N(constraint.Threshold)} in every seed");
        }

        var best = candidates.MinBy(p => p.Mean)!;
        var second = candidates.Where(p => p != best).MinBy(p => p.Mean);
        var margin = second is null
            ? $"mean {N(best.Mean)}, the only candidate"
            : $"mean {N(best.Mean)} against {N(second.Mean)} for {second.Label}"
                + (second.Mean > 0 ? $" ({P((second.Mean - best.Mean) / second.Mean * 100)}% lower)" : string.Empty);
        if (constraint is not null)
        {
            margin += $"; worst seed {constraint.Metric.Label} {N(worst[best])} {constraint.Operator} {N(constraint.Threshold)}"
                + $" ({points.Count - candidates.Count} of {points.Count} cells fail it)";
        }

        return Recommended(rule, best.Label, margin);
    }

    private static Outcome AdoptIfDrop(ValueRule rule, List<Point> points)
    {
        var minimum = rule.MinimumDropPercent!.Value;
        var parts = new List<string>();
        var adopt = true;
        foreach (var series in points.Select(p => p.Series).Distinct())
        {
            var where = series.Length == 0 ? string.Empty : $" ({series})";
            var baseline = points.FirstOrDefault(p => p.Series == series && p.XLabel == rule.Baseline)
                ?? throw new UnreadableException($"no cell with {rule.Parameter} = {rule.Baseline}{where}");
            var candidate = points.FirstOrDefault(p => p.Series == series && p.XLabel == rule.Candidate)
                ?? throw new UnreadableException($"no cell with {rule.Parameter} = {rule.Candidate}{where}");
            var drop = baseline.Mean > 0 ? (baseline.Mean - candidate.Mean) / baseline.Mean * 100 : 0;
            var part = $"{series}{(series.Length == 0 ? string.Empty : ": ")}{rule.Metric.Label} {N(baseline.Mean)} → {N(candidate.Mean)}, "
                + $"a {P(drop)}% drop (needs ≥ {N(minimum)}%)";
            var ok = drop >= minimum;
            if (rule.Guard is not null)
            {
                var guardBaseline = baseline.Runs.Average(r => MetricValue(r, rule.Guard));
                var guardCandidate = candidate.Runs.Average(r => MetricValue(r, rule.Guard));
                var rises = guardCandidate > guardBaseline;
                part += $"; {rule.Guard.Label} {N(guardBaseline)} → {N(guardCandidate)}{(rises ? " (rises)" : string.Empty)}";
                ok &= !rises;
            }

            adopt &= ok;
            parts.Add(part);
        }

        var value = adopt ? $"adopt: {rule.Parameter} = {rule.Candidate}" : $"do not adopt: {rule.Parameter} = {rule.Baseline}";
        return new Outcome(MemoRowStatus.Recommended, $"{value} {MemoGenerator.MeasurementLabel}", string.Join("; ", parts), null);
    }

    private static Outcome Reading(ValueRule rule, List<Point> points)
    {
        if (points.Count != 1)
        {
            throw new UnreadableException($"a reading needs exactly one cell, found {points.Count}");
        }

        var point = points[0];
        return Recommended(
            rule,
            N(point.Mean),
            $"a reading, not a threshold: the mean over {point.Values.Length} seeds, which range {N(point.Min)} to {N(point.Max)} {rule.Unit}");
    }

    private static double MetricValue(RunResult run, MetricSpec metric)
    {
        var total = metric.Paths.Sum(path => Resolve(run, path, metric.AbsentAsZero));
        if (metric.DivideBy is null)
        {
            return total;
        }

        var divisor = Resolve(run, metric.DivideBy, absentAsZero: false);
        return divisor > 0 ? total / divisor : throw new UnreadableException($"run {run.RunId}: '{metric.DivideBy}' is {N(divisor)}, not positive");
    }

    /// <summary>The number at a dotted path of a run's summary; an object sums its numeric properties.</summary>
    private static double Resolve(RunResult run, string path, bool absentAsZero)
    {
        var segments = path.Split('.');
        var current = run.Summary;
        for (var i = 0; i < segments.Length; i++)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                throw new UnreadableException($"run {run.RunId}: summary.json has no '{path}'");
            }

            if (!current.TryGetProperty(segments[i], out current))
            {
                return absentAsZero && i == segments.Length - 1 ? 0 : throw new UnreadableException($"run {run.RunId}: summary.json has no '{path}'");
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.Object => current.EnumerateObject().Sum(p => p.Value.ValueKind == JsonValueKind.Number
                ? p.Value.GetDouble()
                : throw new UnreadableException($"run {run.RunId}: summary.json '{path}.{p.Name}' is not a number")),
            _ => throw new UnreadableException($"run {run.RunId}: summary.json '{path}' is not a number"),
        };
    }

    private static MemoRow Row(ValueRule rule, Outcome outcome, List<Point> points, List<SweepResult> sweeps)
    {
        var runs = points.SelectMany(p => p.Runs).ToList();
        var sweepText = rule.Sweeps.Select(name =>
            sweeps.FirstOrDefault(s => s.Name == name) is { } read ? $"`{name}` ({read.Scenario})" : $"`{name}`");
        var source = $"sweep {string.Join(" and ", sweepText)}; metric `{rule.Metric.Label}` from each run's summary.json; "
            + $"x {(rule.Parameter is null ? "the cells' swept parameters" : $"`{rule.Parameter}`")}";
        return new MemoRow(
            rule.Id,
            rule.Title,
            outcome.Status,
            outcome.Recommended,
            outcome.Margin,
            rule.Description,
            source,
            [.. points.Select(p => new CurvePoint(p.Series, p.XLabel, p.Mean, p.Min, p.Max, p.Values.Length))],
            [.. runs.OrderBy(r => r.SeedIndex).Select(r => r.Seed).Distinct()],
            [.. runs.Select(r => r.RunId)],
            Note(rule.OpenDecision),
            outcome.Problem);
    }

    private static string? Note(int? openDecision) => openDecision switch
    {
        null => null,
        1 => "A recommendation for open decision 1 (section size and claim radius per class), recorded under Principle VI with its curve, seeds and runs. The value stays in configuration.",
        2 => "A reading for the Phase 1 sweep, recorded under Principle VI. Open decision 2 (target fight size) is set by load tests.",
        var other => $"Bears on open decision {other}: a recommendation recorded under Principle VI. The value stays in configuration.",
    };

    private static Outcome Recommended(ValueRule rule, string value, string margin)
    {
        var withUnit = rule.Unit.Length == 0 ? value : $"{value} {rule.Unit}";
        return new Outcome(MemoRowStatus.Recommended, $"{withUnit} {MemoGenerator.MeasurementLabel}", margin, null);
    }

    private static Outcome Flagged(string problem) => new(MemoRowStatus.Flagged, "none", string.Empty, problem);

    private static List<double> DistinctX(List<Point> points) => [.. points.Select(p => p.X!.Value).Distinct().Order()];

    private static string Join(IEnumerable<KeyValuePair<string, JsonElement>> parameters) =>
        string.Join(", ", parameters.Select(p => $"{p.Key}={SweepResults.Label(p.Value)}"));

    private static string N(double value) => SweepResults.Number(value);

    private static string P(double percent) => percent.ToString("0.#", CultureInfo.InvariantCulture);

    private sealed record Outcome(MemoRowStatus Status, string Recommended, string Margin, string? Problem);

    private sealed class Point(string series, string xLabel, double? x, IReadOnlyList<RunResult> runs, double[] values)
    {
        public string Series { get; } = series;

        public string XLabel { get; } = xLabel;

        public double? X { get; } = x;

        public IReadOnlyList<RunResult> Runs { get; } = runs;

        public double[] Values { get; } = values;

        public double Mean => Values.Average();

        public double Min => Values.Min();

        public double Max => Values.Max();

        public string Label => Series.Length == 0 ? XLabel : $"{XLabel} ({Series})";
    }
}
