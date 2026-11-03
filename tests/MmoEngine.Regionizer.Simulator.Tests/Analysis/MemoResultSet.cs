using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using static MmoEngine.Regionizer.Simulator.Tests.Analysis.SweepFixture;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>One value of the committed rules, the sweeps written for it and what the memo must show.</summary>
/// <param name="ReadingRows">The constitution-reading table rows, empty when the rule records none.</param>
internal sealed record ValueCase(
    string Id,
    string Title,
    string Recommended,
    int Population,
    string Label,
    string Status,
    IReadOnlyList<SweepFixture> Sweeps,
    IReadOnlyList<string> CurveRows,
    IReadOnlyList<string> ReadingRows);

/// <summary>One measured option of the committed rules, its sweep and the side-by-side table rows the memo must show.</summary>
internal sealed record OptionCase(string Id, string Title, SweepFixture Sweep, IReadOnlyList<string> TableRows);

/// <summary>
/// A small result set for the committed <c>simulator/sweeps/memo-rules.json</c>: one sweep per value and
/// measured option, designed so each rule reads one known value. Expected rows are computed from the same
/// per-seed numbers the fixtures write. Holds no memo rule.
/// </summary>
internal static class MemoResultSet
{
    public const string OpenDecision1 = "Recommendation, recorded under Principle VI (open decision 1)";
    public const string TargetFightSizeLabel = "Reading (open decision 2 is set by load tests)";
    public const string FusedPath = "fusedFightPairs.p50";
    public const string RegionCountPath = "metrics.regionCount.p50";
    public const string SharePath = "metrics.largestRegionShare.p50";

    /// <summary>The committed rules file.</summary>
    public static string RulesPath() => Path.Combine(Support.TestRuns.RepositoryRoot(), "simulator", "sweeps", "memo-rules.json");

    /// <summary>Writes every value's and option's sweeps under <paramref name="results"/>.</summary>
    public static void Write(string results)
    {
        foreach (var sweep in Values().SelectMany(v => v.Sweeps).Concat(Options().Select(o => o.Sweep)))
        {
            sweep.Write(results);
        }
    }

    public static List<ValueCase> Values()
    {
        var cases = new List<ValueCase>
        {
            // Collapse (mean p50 fused fights ≥ 1) at 20 m; one step below is 16 m, above the 10.47 m floor.
            Radius("claim-radius-infantry", "Claim radius, infantry", "16 m", "infantry-radius", "regionizer.claimRadius.infantry",
                (8, [0, 0], [60, 58], [0.1, 0.12]),
                (12, [0, 0], [56, 54], [0.12, 0.14]),
                (16, [0, 1], [50, 48], [0.15, 0.17]),
                (20, [1, 1], [40, 38], [0.3, 0.32]),
                (24, [2, 3], [30, 28], [0.45, 0.5])),

            // Collapse at 32 m; one step below is 24 m, above the 17 m floor.
            Radius("claim-radius-ground-vehicle", "Claim radius, ground vehicle", "24 m", "vehicle-radius", "regionizer.claimRadius.groundVehicle",
                (16, [0, 0], [55, 53], [0.1, 0.1]),
                (24, [0, 0], [52, 50], [0.12, 0.12]),
                (32, [1, 2], [35, 33], [0.35, 0.37]),
                (40, [3, 3], [25, 25], [0.5, 0.5])),

            // No collapse in the sweep: the top radius, 20 m, above the 16 m floor.
            Radius("claim-radius-aircraft", "Claim radius, aircraft", "20 m", "aircraft-radius", "regionizer.claimRadius.aircraft",
                (8, [0, 0], [60, 60], [0.1, 0.1]),
                (12, [0, 0], [59, 59], [0.1, 0.1]),
                (16, [0, 0], [58, 58], [0.1, 0.1]),
                (20, [0, 0], [57, 57], [0.1, 0.1])),
        };

        // Baseline and One base at 5,000 entities: within the 1,000 µs budget in every seed of both first at 32 m.
        var sizeBaseline = new SweepFixture("section-size", "Baseline", population: 5000);
        var sizeOneBase = new SweepFixture("section-size-onebase", "OneBase", population: 5000);
        var sizeRows = new List<string>();
        foreach (var (sweep, s, p99) in new (SweepFixture, int, double[])[]
        {
            (sizeBaseline, 8, [1500, 1400]), (sizeBaseline, 16, [700, 900]), (sizeBaseline, 32, [400, 500]),
            (sizeOneBase, 8, [2500, 2600]), (sizeOneBase, 16, [1100, 950]), (sizeOneBase, 32, [600, 700]),
        })
        {
            sweep.Cell(P(("regionizer.sectionSize", s)), [.. p99.Select(v => S(("budget.bookkeepingP99Micros", v), ("budget.withinBudget", v <= 1000)))]);
            sizeRows.Add(CurveRow(sweep.Name, N(s), p99));
        }

        cases.Add(new ValueCase("section-size", "Section size", Measured("32 m"), 5000, OpenDecision1, "Recommended", [sizeBaseline, sizeOneBase], sizeRows, []));

        // Fusion rates summed over classes: Section 0.04 and 0.03, Position 0.01 and 0.02.
        var anchoring = new SweepFixture("anchoring", "Baseline")
            .Cell(
                P(("regionizer.anchoring", "Section")),
                S(("rates.fusionsPerSecondByClass.Infantry", 0.03), ("rates.fusionsPerSecondByClass.GroundVehicle", 0.01)),
                S(("rates.fusionsPerSecondByClass.Infantry", 0.02), ("rates.fusionsPerSecondByClass.GroundVehicle", 0.01)))
            .Cell(
                P(("regionizer.anchoring", "Position")),
                S(("rates.fusionsPerSecondByClass.Infantry", 0.01)),
                S(("rates.fusionsPerSecondByClass.Infantry", 0.02)));
        cases.Add(new ValueCase(
            "claim-anchoring", "Claim anchoring", Measured("Position"), 2000, "Recommendation", "Recommended", [anchoring],
            [CurveRow(null, "Section", 0.03 + 0.01, 0.02 + 0.01), CurveRow(null, "Position", 0.01, 0.02)], []));

        // Oversized-fusion ticks out of 1,000: under 1% in every seed first at 600 m, plus the 100 m sprawl margin.
        // Fights fuse (mean fused pairs ≥ 1) at 400 m and below.
        var spacing = new SweepFixture("spacing", "Spacing");
        var spacingRows = new List<string>();
        var spacingReading = new List<string>();
        foreach (var (x, oversized, fused, regions, share) in new (int, double[], double[], double[], double[])[]
        {
            (200, [300, 250], [2, 2], [10, 10], [0.6, 0.6]),
            (400, [40, 5], [1, 1], [20, 18], [0.4, 0.42]),
            (600, [9, 3], [0, 1], [30, 30], [0.3, 0.3]),
            (800, [0, 0], [0, 0], [32, 32], [0.28, 0.28]),
        })
        {
            spacing.Cell(
                P(("scenarioParameters.spacingMetres", x)),
                [.. Enumerable.Range(0, 2).Select(i => S(
                    ("totals.oversizedFusionTicks", oversized[i]), (FusedPath, fused[i]), (RegionCountPath, regions[i]), (SharePath, share[i])))]);
            spacingRows.Add(CurveRow(null, N(x), [.. oversized.Select(v => v / Ticks)]));
            spacingReading.Add(ReadingRow(N(x), fused, regions, share));
        }

        cases.Add(new ValueCase("minimum-base-spacing", "Minimum base spacing", Measured("700 m"), 2000, "Recommendation", "Recommended", [spacing], spacingRows, spacingReading));

        // Bridged ticks out of 1,000: under 1% in every seed up to 30 travellers.
        var lane = new SweepFixture("lane-traffic", "LaneTraffic");
        var laneRows = new List<string>();
        foreach (var (x, bridged) in new (int, double[])[] { (10, [0, 0]), (30, [5, 2]), (60, [30, 10]), (120, [400, 500]) })
        {
            lane.Cell(P(("scenarioParameters.travellers", x)), [.. bridged.Select(v => S(("totals.oversizedFusionTicks", v)))]);
            laneRows.Add(CurveRow(null, N(x), [.. bridged.Select(v => v / Ticks)]));
        }

        cases.Add(new ValueCase("lane-bridging-limit", "Lane bridging limit", Measured("30 travellers"), 2000, "Recommendation", "Recommended", [lane], laneRows, []));

        // Merge-plus-split rates; minSections 64 is lowest but its split latency p99 reaches 200 ticks (over 150).
        // Every run reports its absorptions separately (4 and 6), so merge rates are real merges.
        var damping = new SweepFixture("split-damping", "Migration");
        var dampingRows = new List<string>();
        foreach (var (path, value, merges, splits, latency) in new (string, int, double, double, double[])[]
        {
            ("regionizer.split.minSections", 16, 0.5, 0.4, [100, 120]),
            ("regionizer.split.minSections", 64, 0.3, 0.2, [200, 140]),
            ("regionizer.split.minDeadPercent", 10, 0.4, 0.3, [90, 100]),
            ("regionizer.split.minAgeSeconds", 4, 0.6, 0.5, [50, 60]),
        })
        {
            damping.Cell(
                P((path, value)),
                [.. latency.Select((l, i) => S(
                    ("rates.mergesPerSecondByClass.Infantry", merges), ("rates.splitsPerSecond", splits), ("metrics.splitLatencyTicks.p99", l),
                    ("totals.absorptions", 4 + (2 * i))))]);
            dampingRows.Add(CurveRow(null, $"{path}={value}", merges + splits, merges + splits));
        }

        cases.Add(new ValueCase(
            "split-damping", "Split damping", Measured("regionizer.split.minDeadPercent=10"), 2000, "Recommendation", "Recommended", [damping], dampingRows, []));

        // Aircraft merges 0.9 → 0.2 (a 78% drop; one run writes no Aircraft rate at all), fusions 0.1 → 0.075.
        // No run reports absorptions, so the merge rate is read as written.
        var air = new SweepFixture("air-layer", "AirTraffic")
            .Cell(
                P(("airLayer.enabled", false)),
                S(("rates.mergesPerSecondByClass.Aircraft", 0.8), ("rates.fusionsPerSecondByClass.Infantry", 0.1)),
                S(("rates.mergesPerSecondByClass.Aircraft", 1.0), ("rates.fusionsPerSecondByClass.Infantry", 0.1)))
            .Cell(
                P(("airLayer.enabled", true)),
                S(("rates.mergesPerSecondByClass.Aircraft", null), ("rates.fusionsPerSecondByClass.Infantry", 0.1)),
                S(("rates.mergesPerSecondByClass.Aircraft", 0.4), ("rates.fusionsPerSecondByClass.Infantry", 0.05)));
        cases.Add(new ValueCase(
            "air-layer", "Air layer", Measured("adopt: airLayer.enabled = true"), 2000, "Recommendation", "Recommended", [air],
            [CurveRow(null, "false", 0.8, 1.0), CurveRow(null, "true", 0, 0.4)], []));

        // One cell at the chosen values: the reading is the mean over seeds of the largest region's p99.
        var final = new SweepFixture("baseline-final", "Baseline")
            .Cell(P(("regionizer.claimRadius.infantry", 16)), S(("metrics.largestRegionPlayers.p99", 1180)), S(("metrics.largestRegionPlayers.p99", 1220)));
        cases.Add(new ValueCase(
            "target-fight-size", "Target fight size", Measured("1200 players"), 2000, TargetFightSizeLabel, "Read", [final],
            [CurveRow(null, "regionizer.claimRadius.infantry=16", 1180, 1220)], []));
        return cases;
    }

    public static List<OptionCase> Options()
    {
        // Merge gap 0 vs 1 in Migration, interleaved. Absorptions are reported only in cells.csv.
        var gap = new SweepFixture("merge-gap", "Migration", scheduling: "Interleaved")
            .CellsColumn("absorptions", (_, _) => "3");
        var gapRows = new List<string>();
        var merges = new Dictionary<int, double[]> { [0] = [0.5, 0.7], [1] = [0.9, 1.1] };
        var splits = new Dictionary<int, double[]> { [0] = [0.4, 0.4], [1] = [0.3, 0.5] };
        var fusionRates = new Dictionary<int, double[]> { [0] = [0.02, 0.04], [1] = [0.05, 0.05] };
        var fusions = new Dictionary<int, double[]> { [0] = [20, 40], [1] = [50, 50] };
        foreach (var value in new[] { 0, 1 })
        {
            gap.Cell(
                P(("regionizer.mergeGap", value)),
                [.. Enumerable.Range(0, 2).Select(i => S(
                    ("rates.mergesPerSecondByClass.Infantry", merges[value][i]), ("rates.splitsPerSecond", splits[value][i]),
                    ("rates.fusionsPerSecondByClass.Infantry", fusionRates[value][i]), ("totals.fusions", fusions[value][i])))]);
        }

        gapRows.Add(OptionRow("Merges per second, every causing class", merges[0], merges[1]));
        gapRows.Add(OptionRow("Splits per second", splits[0], splits[1]));
        gapRows.Add(OptionRow("Fusions per second, every causing class", fusionRates[0], fusionRates[1]));
        gapRows.Add(OptionRow("Fusions", fusions[0], fusions[1]));
        gapRows.Add("| Runs stopped by a timer fault | 0 of 2 | 0 of 2 |");
        gapRows.Add("| Runs stopped by an invariant violation | 0 of 2 | 0 of 2 |");

        // Tick-counter vs absolute timers; the second absolute run stopped at a timer fault, so it is counted
        // there and left out of the metrics.
        var timers = new SweepFixture("timer-mode", "Migration", scheduling: "Interleaved")
            .Cell(
                P(("regionizer.timerMode", "TickCounter")),
                TimerRun(0.5, 0.4, 0.02, 100, 100),
                TimerRun(0.5, 0.4, 0.02, 120, 120))
            .Cell(
                P(("regionizer.timerMode", "Absolute")),
                TimerRun(0.6, 0.3, 0.03, 110, 110),
                Join(TimerRun(0.9, 0.9, 0.9, 50, 49), Stopped("TimerFault")));
        List<string> timerRows =
        [
            OptionRow("Merges per second, every causing class", [0.5, 0.5], [0.6]),
            OptionRow("Splits per second", [0.4, 0.4], [0.3]),
            OptionRow("Fusions per second, every causing class", [0.02, 0.02], [0.03]),
            OptionRow("Timers set", [100, 120], [110]),
            OptionRow("Timers fired", [100, 120], [110]),
            "| Runs stopped by a timer fault | 0 of 2 | 1 of 2 |",
            "| Runs stopped by an invariant violation | 0 of 2 | 0 of 2 |",
        ];

        return
        [
            new OptionCase("merge-gap", "Merge gap 0 vs 1", gap, gapRows),
            new OptionCase("timer-mode", "Tick-counter vs absolute timers", timers, timerRows),
        ];
    }

    /// <summary>A summary that stopped at a violation of the given kind, as the runner writes it.</summary>
    public static Action<JsonObject> Stopped(string kind) => S(
        ("completed", false),
        ("firstViolation", new JsonObject { ["kind"] = kind, ["rule"] = null, ["tick"] = 40, ["seed"] = 22, ["detail"] = "late" }));

    public static Action<JsonObject> Join(params Action<JsonObject>[] setters) => summary =>
    {
        foreach (var set in setters)
        {
            set(summary);
        }
    };

    public static string Measured(string value) => $"{value} {MemoGenerator.MeasurementLabel}";

    public static string CurveRow(string? series, string x, params double[] perSeed)
    {
        var columns = new List<string>();
        if (series is not null)
        {
            columns.Add(series);
        }

        columns.AddRange([x, N(perSeed.Average()), N(perSeed.Min()), N(perSeed.Max()), perSeed.Length.ToString(CultureInfo.InvariantCulture)]);
        return $"| {string.Join(" | ", columns)} |";
    }

    public static string ReadingRow(string x, double[] fused, double[] regions, double[] share) =>
        $"| {x} | {Stat(fused)} | {Stat(regions)} | {Stat(share)} |";

    public static string Stat(double[] values) => $"{N(values.Average())} ({N(values.Min())} to {N(values.Max())})";

    public static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static Action<JsonObject> TimerRun(double merges, double splits, double fusions, int set, int fired) => S(
        ("rates.mergesPerSecondByClass.Infantry", merges), ("rates.splitsPerSecond", splits), ("rates.fusionsPerSecondByClass.Infantry", fusions),
        ("totals.timersSet", set), ("totals.timersFired", fired), ("totals.absorptions", 2));

    private static string OptionRow(string metric, double[] first, double[] second) => $"| {metric} | {Stat(first)} | {Stat(second)} |";

    private static ValueCase Radius(
        string id, string title, string recommended, string sweepName, string parameter, params (int X, double[] Fused, double[] Regions, double[] Share)[] cells)
    {
        var sweep = new SweepFixture(sweepName, "Baseline");
        var rows = new List<string>();
        var reading = new List<string>();
        foreach (var (x, fused, regions, share) in cells)
        {
            sweep.Cell(P((parameter, x)), [.. Enumerable.Range(0, fused.Length).Select(i => S((FusedPath, fused[i]), (RegionCountPath, regions[i]), (SharePath, share[i])))]);
            rows.Add(CurveRow(null, N(x), fused));
            reading.Add(ReadingRow(N(x), fused, regions, share));
        }

        return new ValueCase(id, title, Measured(recommended), 2000, OpenDecision1, "Recommended", [sweep], rows, reading);
    }
}
