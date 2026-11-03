using System.Globalization;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using Xunit;
using static MmoEngine.Regionizer.Simulator.Tests.Analysis.SweepFixture;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// The decision memo (US6.4, FR-032) from small synthetic sweep folders that follow the output contracts,
/// read with the committed <c>simulator/sweeps/memo-rules.json</c>.
/// </summary>
public sealed class MemoGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mmo-tests", Guid.NewGuid().ToString("N"));

    public MemoGeneratorTests() => Results = Path.Combine(_root, "results");

    private string Results { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp folder never affects another test.
        }
    }

    // US6.4: Given a set of sweep results, When the memo generator runs, Then the memo has a row for each of
    // claim radius per class, section size, claim anchoring, minimum base spacing, lane bridging limit, split
    // damping, air layer and target fight size, each with a recommended value labeled as a measurement, the
    // curve it was read from, its margin, its seeds and its run IDs.
    [Fact]
    public void MemoHasARowPerValueWithEvidence()
    {
        var cases = FullResultSet();
        foreach (var value in cases)
        {
            value.Sweep.Write(Results);
        }

        var outPath = Path.Combine(_root, "memo", "decision-memo.md");

        var result = MemoGenerator.Write(Results, MemoRules.Load(RulesPath()), outPath);

        var markdown = File.ReadAllText(outPath);
        Assert.Equal(result.Markdown, markdown);
        Assert.True(result.Success);
        Assert.Contains($"All {cases.Count} values were read.", markdown, StringComparison.Ordinal);

        // One summary row per value, in order.
        var table = SummaryRows(markdown);
        Assert.Equal(cases.Select(c => c.Title), table.Select(r => r[0]));
        foreach (var (value, row) in cases.Zip(table))
        {
            Assert.Equal(value.Recommended, row[1]);
            Assert.EndsWith(MemoGenerator.MeasurementLabel, row[1], StringComparison.Ordinal);
            Assert.Equal("Recommended", row[2]);
            Assert.False(string.IsNullOrWhiteSpace(row[3]), $"{value.Title}: no margin");

            // Each value's own section: the value, its margin, its seeds, its run IDs and its curve.
            var section = Section(markdown, value.Title);
            Assert.Contains($"- **Recommended**: {value.Recommended}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Margin**: {row[3]}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Seeds**: {string.Join(", ", Seeds)}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Runs**: {string.Join(", ", value.Sweep.RunIds().Select(id => $"`{id}`"))}\n", section, StringComparison.Ordinal);
            Assert.Equal(value.CurveRows, CurveRows(section));
        }

        // Open decision 1 values are recommendations recorded under Principle VI; the fight size is a reading.
        foreach (var title in new[] { "Claim radius, infantry", "Claim radius, ground vehicle", "Claim radius, aircraft", "Section size" })
        {
            Assert.Contains("- **Principle VI**: A recommendation for open decision 1", Section(markdown, title), StringComparison.Ordinal);
        }

        Assert.Contains("open decision 2 (target fight size) is set by load tests", Section(markdown, "Target fight size"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-sweep", "target-fight-size", "baseline-final/sweep.json: cannot be read")]
    [InlineData("sweep-invalid-json", "claim-radius-infantry", "not valid JSON")]
    [InlineData("sweep-format-version", "claim-radius-infantry", "formatVersion 2 is not supported")]
    [InlineData("sweep-field-missing", "claim-radius-infantry", "'scenario' is missing")]
    [InlineData("sweep-field-wrong-type", "claim-radius-infantry", "'cells' has the wrong type")]
    [InlineData("sweep-no-cells", "claim-radius-infantry", "no cells")]
    [InlineData("cell-no-runs", "claim-radius-infantry", "has no runs")]
    [InlineData("run-not-completed", "claim-radius-infantry", "lists the run as not completed")]
    [InlineData("run-completed-not-bool", "claim-radius-infantry", "'completed' has the wrong type")]
    [InlineData("run-seed-index-fraction", "claim-radius-infantry", "'seedIndex' is not a whole number")]
    [InlineData("run-seed-negative", "claim-radius-infantry", "'seed' is not a 64-bit unsigned seed")]
    [InlineData("run-seed-differs-from-index", "claim-radius-infantry", "differs from the sweep's seed")]
    [InlineData("run-folder-escapes", "claim-radius-infantry", "outside the sweep folder")]
    [InlineData("run-incomplete-marker", "claim-radius-infantry", "still marked _incomplete")]
    [InlineData("manifest-seed-differs", "claim-radius-infantry", "manifest.json: seed 99 differs")]
    [InlineData("summary-missing", "claim-radius-infantry", "summary.json: cannot be read")]
    [InlineData("summary-run-id-differs", "claim-radius-infantry", "differs from the manifest's")]
    [InlineData("summary-not-completed", "claim-radius-infantry", "the run did not complete")]
    [InlineData("summary-violation", "claim-radius-infantry", "stopped at a violation")]
    [InlineData("metric-missing", "claim-radius-infantry", "has no 'fusedFightPairs.p50'")]
    [InlineData("metric-parent-is-number", "claim-radius-infantry", "has no 'fusedFightPairs.p50'")]
    [InlineData("metric-not-number", "claim-radius-infantry", "'fusedFightPairs.p50' is not a number")]
    [InlineData("metric-class-not-number", "claim-anchoring", "'rates.fusionsPerSecondByClass.Infantry' is not a number")]
    [InlineData("metric-divisor-zero", "minimum-base-spacing", "'ticks' is 0, not positive")]
    [InlineData("x-missing", "claim-radius-infantry", "does not set 'regionizer.claimRadius.infantry'")]
    [InlineData("x-not-number", "claim-radius-infantry", "needs a number")]
    [InlineData("x-not-inferable", "minimum-base-spacing", "names no 'parameter'")]
    [InlineData("adopt-candidate-missing", "air-layer", "no cell with airLayer.enabled = true")]
    [InlineData("reading-two-cells", "target-fight-size", "exactly one cell, found 2")]
    public void UnreadableResultsMarkTheRowAndFailTheMemo(string mutation, string valueId, string reason)
    {
        foreach (var value in FullResultSet())
        {
            value.Sweep.Write(Results);
        }

        Mutate(mutation);

        var result = MemoGenerator.Generate(Results, MemoRules.Load(RulesPath()));

        Assert.False(result.Success);
        var row = Assert.Single(result.Rows, r => r.Status != MemoRowStatus.Recommended);
        Assert.Equal(valueId, row.Id);
        Assert.Equal(MemoRowStatus.Unreadable, row.Status);
        Assert.Contains(reason, row.Problem, StringComparison.Ordinal);
        Assert.Empty(row.Curve);
        Assert.Contains($"**1 of {result.Rows.Count} values could not be read.**", result.Markdown, StringComparison.Ordinal);
        var tableRow = Assert.Single(SummaryRows(result.Markdown), r => r[0] == row.Title);
        Assert.Equal(["**not read**", "**UNREADABLE**"], tableRow[1..3]);
        var section = Section(result.Markdown, row.Title);
        Assert.Contains($"- **Why**: {row.Problem}\n", section, StringComparison.Ordinal);
        Assert.Contains("No curve: the results could not be read.", section, StringComparison.Ordinal);
        Assert.Contains("- **Seeds**: none\n", section, StringComparison.Ordinal);
        Assert.Contains("- **Runs**: none\n", section, StringComparison.Ordinal);
    }

    // D15: a claim radius whose floor is above the largest collapse-safe radius is flagged as a conflict, and
    // the other rules flag a sweep in which nothing qualifies. A flagged row is read, so the memo succeeds.
    [Theory]
    [InlineData("floor-above-safe-radius", "conflict: the analytic floor 38 m")]
    [InlineData("collapse-at-smallest-radius", "conflict: collapse at 8 m")]
    [InlineData("nothing-within-threshold", "no swept value has fusedFightPairs.p50 < 0 in every seed")]
    [InlineData("constraint-excludes-every-cell", "no cell has fusedFightPairs.p95 < 0 in every seed")]
    public void RuleWithNoQualifyingValueIsFlagged(string flag, string problem)
    {
        new SweepFixture("radius", "Baseline")
            .Cell(P(("r", 8)), S(("fusedFightPairs.p50", 0), ("fusedFightPairs.p95", 1)), S(("fusedFightPairs.p50", 2), ("fusedFightPairs.p95", 1)))
            .Cell(P(("r", 12)), S(("fusedFightPairs.p50", 1), ("fusedFightPairs.p95", 2)), S(("fusedFightPairs.p50", 1), ("fusedFightPairs.p95", 2)))
            .Cell(P(("r", 16)), S(("fusedFightPairs.p50", 3), ("fusedFightPairs.p95", 4)), S(("fusedFightPairs.p50", 3), ("fusedFightPairs.p95", 4)))
            .Write(Results);
        var rule = flag switch
        {
            "floor-above-safe-radius" => """
                "rule": "ClaimRadius", "parameter": "r", "unit": "m", "collapseAt": 1.5,
                "floor": { "interactionRangeMetres": 30, "closingSpeedMetresPerSecond": 240, "tickRateHz": 30 }
                """,
            "collapse-at-smallest-radius" => """
                "rule": "ClaimRadius", "parameter": "r", "unit": "m", "collapseAt": 1,
                "floor": { "interactionRangeMetres": 1, "closingSpeedMetresPerSecond": 0, "tickRateHz": 30 }
                """,
            "nothing-within-threshold" => """ "rule": "SmallestWithin", "parameter": "r", "threshold": 0 """,
            _ => """
                "rule": "Lowest",
                "constraint": { "metric": { "paths": ["fusedFightPairs.p95"] }, "threshold": 0 }
                """,
        };

        var result = MemoGenerator.Generate(Results, OneRule(rule));

        var row = Assert.Single(result.Rows);
        Assert.True(result.Success);
        Assert.Equal(MemoRowStatus.Flagged, row.Status);
        Assert.Equal("none", row.Recommended);
        Assert.StartsWith(problem, row.Problem, StringComparison.Ordinal);
        Assert.Equal(3, row.Curve.Count);
        Assert.Equal(["none", "**FLAGGED**", row.Problem], Assert.Single(SummaryRows(result.Markdown))[1..]);
    }

    // D15: adopt the air layer only if aircraft-caused merges drop by at least 25% with no rise in fusion events.
    [Theory]
    [InlineData(1.0, 0.70, 0.10, 0.10, true)]
    [InlineData(1.0, 0.80, 0.10, 0.10, false)]
    [InlineData(1.0, 0.50, 0.10, 0.20, false)]
    [InlineData(0.0, 0.00, 0.10, 0.10, false)]
    public void AirLayerIsAdoptedOnlyForAQuarterFewerAircraftMergesAndNoMoreFusions(
        double mergesOff, double mergesOn, double fusionsOff, double fusionsOn, bool adopt)
    {
        new SweepFixture("air-layer", "AirTraffic")
            .Cell(
                P(("airLayer.enabled", false)),
                S(("rates.mergesPerSecondByClass.Aircraft", mergesOff), ("rates.fusionsPerSecondByClass.Infantry", fusionsOff)),
                S(("rates.mergesPerSecondByClass.Aircraft", mergesOff), ("rates.fusionsPerSecondByClass.Infantry", fusionsOff)))
            .Cell(
                P(("airLayer.enabled", true)),
                S(("rates.mergesPerSecondByClass.Aircraft", mergesOn), ("rates.fusionsPerSecondByClass.Infantry", fusionsOn)),
                S(("rates.mergesPerSecondByClass.Aircraft", mergesOn), ("rates.fusionsPerSecondByClass.Infantry", fusionsOn)))
            .Write(Results);

        var result = MemoGenerator.Generate(Results, RulesFor("air-layer"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(MemoRowStatus.Recommended, row.Status);
        var expected = adopt ? "adopt: airLayer.enabled = true" : "do not adopt: airLayer.enabled = false";
        Assert.Equal($"{expected} {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Contains("needs ≥ 25%", row.Margin, StringComparison.Ordinal);
        Assert.Equal(fusionsOn > fusionsOff, row.Margin.Contains("(rises)", StringComparison.Ordinal));
    }

    // A value read from two sweeps (for example Baseline and One base as separate sweeps) must qualify in both.
    [Fact]
    public void ValueReadFromSeveralSweepsMustQualifyInEach()
    {
        new SweepFixture("size-baseline", "Baseline")
            .Cell(P(("s", 8)), S(("budget.bookkeepingP99Micros", 900)), S(("budget.bookkeepingP99Micros", 800)))
            .Cell(P(("s", 16)), S(("budget.bookkeepingP99Micros", 500)), S(("budget.bookkeepingP99Micros", 400)))
            .Write(Results);
        new SweepFixture("size-one-base", "OneBase")
            .Cell(P(("s", 8)), S(("budget.bookkeepingP99Micros", 1200)), S(("budget.bookkeepingP99Micros", 800)))
            .Cell(P(("s", 16)), S(("budget.bookkeepingP99Micros", 700)), S(("budget.bookkeepingP99Micros", 600)))
            .Write(Results);
        var rules = MemoRules.Parse("""
            { "formatVersion": 1, "values": [ {
              "id": "size", "rule": "SmallestWithin", "sweeps": ["size-baseline", "size-one-base"], "unit": "m",
              "metric": { "paths": ["budget.bookkeepingP99Micros"] }, "threshold": 1000, "inclusive": true } ] }
            """);

        var row = Assert.Single(MemoGenerator.Generate(Results, rules).Rows);

        Assert.Equal($"16 m {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Equal(["size-baseline", "size-baseline", "size-one-base", "size-one-base"], row.Curve.Select(p => p.Series));
        Assert.Contains("8 m measures 1200 (fails)", row.Margin, StringComparison.Ordinal);
        Assert.Contains("sweep `size-baseline` (Baseline) and `size-one-base` (OneBase)", row.Source, StringComparison.Ordinal);
        Assert.Equal("size", row.Title);
    }

    [Theory]
    [InlineData("LargestWithin", "16", "16 m is the largest swept value")]
    [InlineData("SmallestWithin", "8", "8 m is the smallest swept value")]
    public void ThresholdRuleAtTheEdgeOfTheSweepSaysSo(string kind, string value, string margin)
    {
        new SweepFixture("edge", "Baseline")
            .Cell(P(("x", 8)), S(("fusedFightPairs.p50", 0)), S(("fusedFightPairs.p50", 0)))
            .Cell(P(("x", 16)), S(("fusedFightPairs.p50", 0)), S(("fusedFightPairs.p50", 0)))
            .Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule($""" "rule": "{kind}", "unit": "m", "threshold": 1 """, "edge")).Rows);

        Assert.Equal($"{value} m {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Contains(margin, row.Margin, StringComparison.Ordinal);
    }

    [Fact]
    public void LowestWithOneCellIsTheOnlyCandidate()
    {
        new SweepFixture("one", "Baseline").Cell(P(), S(("fusedFightPairs.p50", 2)), S(("fusedFightPairs.p50", 4))).Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Lowest" """, "one")).Rows);

        Assert.Equal($"base {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Equal("mean 3, the only candidate", row.Margin);
        Assert.Null(row.Note);
    }

    [Theory]
    [InlineData("16", "p=16")]
    [InlineData("0.125", "p=0.125")]
    [InlineData("\"Section\"", "p=Section")]
    [InlineData("true", "p=true")]
    [InlineData("[1, 2]", "p=[1,2]")]
    [InlineData("{ \"a\": 1 }", "p={\"a\":1}")]
    [InlineData("null", "p=null")]
    public void CellsAreLabeledByTheirSweptValues(string value, string label)
    {
        new SweepFixture("one", "Baseline").Cell(P(("p", JsonNode.Parse(value))), S(("fusedFightPairs.p50", 1)), S(("fusedFightPairs.p50", 1))).Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Lowest" """, "one")).Rows);

        Assert.Equal(label, Assert.Single(row.Curve).X);
        Assert.Equal($"{label} {MemoGenerator.MeasurementLabel}", row.Recommended);
    }

    [Fact]
    public void ValueForAnotherOpenDecisionCarriesAPrincipleViNote()
    {
        new SweepFixture("one", "Baseline").Cell(P(), S(("fusedFightPairs.p50", 2)), S(("fusedFightPairs.p50", 4))).Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Reading", "openDecision": 8 """, "one")).Rows);

        Assert.Equal($"3 {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.StartsWith("Bears on open decision 8", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void CommittedRulesListEveryValueTheSimulatorSets()
    {
        var rules = MemoRules.Load(RulesPath());

        Assert.Equal(
            [
                "claim-radius-infantry", "claim-radius-ground-vehicle", "claim-radius-aircraft", "section-size", "claim-anchoring",
                "minimum-base-spacing", "lane-bridging-limit", "split-damping", "air-layer", "target-fight-size",
            ],
            rules.Values.Select(v => v.Id));
        Assert.Equal(
            ["infantry-radius", "vehicle-radius", "aircraft-radius", "section-size", "anchoring", "spacing", "lane-traffic", "split-damping", "air-layer", "baseline-final"],
            rules.Values.Select(v => Assert.Single(v.Sweeps)));
        Assert.All(rules.Values, v => Assert.False(string.IsNullOrWhiteSpace(v.Description)));
    }

    [Theory]
    [InlineData("{", "not valid JSON")]
    [InlineData("[]", "$: must be an object")]
    [InlineData("""{ "values": [] }""", "formatVersion: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [] }""", "formatVersion: must be 1")]
    [InlineData("""{ "formatVersion": "1", "values": [] }""", "formatVersion: must be a number")]
    [InlineData("""{ "formatVersion": 1 }""", "values: is required")]
    [InlineData("""{ "formatVersion": 1, "values": {} }""", "values: must be an array")]
    [InlineData("""{ "formatVersion": 1, "values": [] }""", "values: must list at least one value")]
    [InlineData("""{ "formatVersion": 1, "values": [ 3 ] }""", "values[0]: must be an object")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "rule": "Reading" } ] }""", "values[0].id: is required")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": " ", "rule": "Reading" } ] }""", "values[0].id: must be a non-empty string")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Median", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].rule: unknown rule 'Median'")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "metric": { "paths": ["x"] } } ] }""", "values[0].sweeps: a 'sweep' or 'sweeps' is required")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweeps": [], "metric": { "paths": ["x"] } } ] }""", "values[0].sweeps: must name at least one sweep")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s" } ] }""", "values[0].metric: is required")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": "x" } ] }""", "values[0].metric: must be an object with 'paths'")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": [] } } ] }""", "values[0].metric.paths: must list at least one summary path")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"], "absentAsZero": 1 } } ] }""", "values[0].metric.absentAsZero: must be true or false")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "SmallestWithin", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].threshold: is required for SmallestWithin")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "LargestWithin", "sweep": "s", "metric": { "paths": ["x"] }, "threshold": "1" } ] }""", "values[0].threshold: must be a number")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].collapseAt: is required for ClaimRadius")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1 } ] }""", "values[0].floor: is required for ClaimRadius")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": 1 } ] }""", "values[0].floor: must be an object")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": { "interactionRangeMetres": -1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30 } } ] }""", "values[0].floor: Must be finite and non-negative.")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "stepsBelowCollapse": 0, "floor": { "interactionRangeMetres": 1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30 } } ] }""", "values[0].stepsBelowCollapse: must be at least 1")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "stepsBelowCollapse": 1.5 } ] }""", "values[0].stepsBelowCollapse: must be a whole number")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "metric": { "paths": ["x"] }, "constraint": 1 } ] }""", "values[0].constraint: must be an object")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "metric": { "paths": ["x"] }, "constraint": { "threshold": 1 } } ] }""", "values[0].constraint.metric: is required")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "metric": { "paths": ["x"] }, "inclusive": "yes", "threshold": 1 } ] }""", "values[0].inclusive: must be true or false")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].parameter: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "parameter": "p", "metric": { "paths": ["x"] } } ] }""", "values[0].baseline: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "parameter": "p", "baseline": false, "metric": { "paths": ["x"] } } ] }""", "values[0].candidate: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "parameter": "p", "baseline": false, "candidate": true, "metric": { "paths": ["x"] } } ] }""", "values[0].minimumDropPercent: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "parameter": "p", "baseline": [], "metric": { "paths": ["x"] } } ] }""", "values[0].baseline: must be a string, number or boolean")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"] } }, { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[1].id: duplicate id 'a'")]
    [InlineData("""{ "formatVersion": 1, "valeus": [] }""", "valeus: is not a known field")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"] }, "inclusiv": true } ] }""", "values[0].inclusiv: is not a known field")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"], "divide": "ticks" } } ] }""", "values[0].metric.divide: is not a known field")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "metric": { "paths": ["x"] }, "constraint": { "metric": { "paths": ["y"] }, "threshold": 1, "max": 2 } } ] }""", "values[0].constraint.max: is not a known field")]
    [InlineData("""{ "formatVersion": 1, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": { "interactionRangeMetres": 1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30, "dt": 1 } } ] }""", "values[0].floor.dt: is not a known field")]
    public void InvalidRulesNameTheBadField(string json, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => MemoRules.Parse(json));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// One sweep per value of the committed rules, designed so each rule reads one known value. Each value's
    /// expected curve rows are computed from the same per-seed numbers the fixture writes.
    /// </summary>
    private static List<ValueCase> FullResultSet()
    {
        var cases = new List<ValueCase>
        {
            // Collapse (mean p50 fused fights ≥ 1) at 20 m; one step below is 16 m, above the 10.47 m floor.
            OneMetric("Claim radius, infantry", "16 m", new SweepFixture("infantry-radius", "Baseline"), "regionizer.claimRadius.infantry", "fusedFightPairs.p50",
                (8, [0, 0]), (12, [0, 0]), (16, [0, 1]), (20, [1, 1]), (24, [2, 3])),

            // Collapse at 32 m; one step below is 24 m, above the 17 m floor.
            OneMetric("Claim radius, ground vehicle", "24 m", new SweepFixture("vehicle-radius", "Baseline"), "regionizer.claimRadius.groundVehicle", "fusedFightPairs.p50",
                (16, [0, 0]), (24, [0, 0]), (32, [1, 2]), (40, [3, 3])),

            // No collapse in the sweep: the top radius, 20 m, above the 16 m floor.
            OneMetric("Claim radius, aircraft", "20 m", new SweepFixture("aircraft-radius", "Baseline"), "regionizer.claimRadius.aircraft", "fusedFightPairs.p50",
                (8, [0, 0]), (12, [0, 0]), (16, [0, 0]), (20, [0, 0])),
        };

        // Within the 1,000 µs budget in every seed of both scenarios first at 32 m (One base fails at 16 m).
        var size = new SweepFixture("section-size", "Baseline", "Grid");
        var sizeRows = new List<string>();
        foreach (var (scenario, s, p99) in new (string, int, double[])[]
        {
            ("Baseline", 8, [1500, 1400]), ("Baseline", 16, [700, 900]), ("Baseline", 32, [400, 500]),
            ("OneBase", 8, [2500, 2600]), ("OneBase", 16, [1100, 950]), ("OneBase", 32, [600, 700]),
        })
        {
            size.Cell(
                P(("scenario", scenario), ("regionizer.sectionSize", s)),
                [.. p99.Select(v => S(("budget.bookkeepingP99Micros", v), ("budget.withinBudget", v <= 1000)))]);
            sizeRows.Add(CurveRow($"scenario={scenario}", N(s), p99));
        }

        cases.Add(new ValueCase("Section size", $"32 m {MemoGenerator.MeasurementLabel}", size, sizeRows));

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
            "Claim anchoring", $"Position {MemoGenerator.MeasurementLabel}", anchoring,
            [CurveRow(null, "Section", 0.03 + 0.01, 0.02 + 0.01), CurveRow(null, "Position", 0.01, 0.02)]));

        // Oversized-fusion ticks out of 1,000: under 1% in every seed first at 600 m, plus the 100 m sprawl margin.
        cases.Add(TickShare("Minimum base spacing", "700 m", new SweepFixture("spacing", "Spacing"), "scenarioParameters.spacingMetres",
            (200, [300, 250]), (400, [40, 5]), (600, [9, 3]), (800, [0, 0])));

        // Bridged ticks out of 1,000: under 1% in every seed up to 30 travellers.
        cases.Add(TickShare("Lane bridging limit", "30 travellers", new SweepFixture("lane-traffic", "LaneTraffic"), "scenarioParameters.travellers",
            (10, [0, 0]), (30, [5, 2]), (60, [30, 10]), (120, [400, 500])));

        // Merge-plus-split rates; minSections 64 is lowest but its split latency p99 reaches 200 ticks (over 150).
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
                [.. latency.Select(l => S(("rates.mergesPerSecondByClass.Infantry", merges), ("rates.splitsPerSecond", splits), ("metrics.splitLatencyTicks.p99", l)))]);
            dampingRows.Add(CurveRow(null, $"{path}={value}", merges + splits, merges + splits));
        }

        cases.Add(new ValueCase("Split damping", $"regionizer.split.minDeadPercent=10 {MemoGenerator.MeasurementLabel}", damping, dampingRows));

        // Aircraft merges 0.9 → 0.2 (a 78% drop; one run writes no Aircraft rate at all), fusions 0.1 → 0.075.
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
            "Air layer", $"adopt: airLayer.enabled = true {MemoGenerator.MeasurementLabel}", air,
            [CurveRow(null, "false", 0.8, 1.0), CurveRow(null, "true", 0, 0.4)]));

        // One cell at the chosen values: the reading is the mean over seeds of the largest region's p99.
        cases.Add(OneMetric("Target fight size", "1200 players", new SweepFixture("baseline-final", "Baseline"), null, "metrics.largestRegionPlayers.p99",
            (16, [1180, 1220])));
        return cases;
    }

    private static ValueCase OneMetric(string title, string recommended, SweepFixture sweep, string? parameter, string metric, params (int X, double[] PerSeed)[] cells)
    {
        var rows = new List<string>();
        foreach (var (x, perSeed) in cells)
        {
            // The reading's single cell is labeled by its parameters, since its rule names no parameter.
            sweep.Cell(P((parameter ?? "regionizer.claimRadius.infantry", x)), [.. perSeed.Select(v => S((metric, v)))]);
            rows.Add(CurveRow(null, parameter is null ? $"regionizer.claimRadius.infantry={x}" : N(x), perSeed));
        }

        return new ValueCase(title, $"{recommended} {MemoGenerator.MeasurementLabel}", sweep, rows);
    }

    private static ValueCase TickShare(string title, string recommended, SweepFixture sweep, string parameter, params (int X, double[] OversizedTicks)[] cells)
    {
        var rows = new List<string>();
        foreach (var (x, oversized) in cells)
        {
            sweep.Cell(P((parameter, x)), [.. oversized.Select(v => S(("totals.oversizedFusionTicks", v)))]);
            rows.Add(CurveRow(null, N(x), [.. oversized.Select(v => v / Ticks)]));
        }

        return new ValueCase(title, $"{recommended} {MemoGenerator.MeasurementLabel}", sweep, rows);
    }

    private static string CurveRow(string? series, string x, params double[] perSeed)
    {
        var columns = new List<string>();
        if (series is not null)
        {
            columns.Add(series);
        }

        columns.AddRange([x, N(perSeed.Average()), N(perSeed.Min()), N(perSeed.Max()), perSeed.Length.ToString(CultureInfo.InvariantCulture)]);
        return $"| {string.Join(" | ", columns)} |";
    }

    private void Mutate(string mutation)
    {
        string Sweep(string name) => Path.Combine(Results, name, "sweep.json");
        string Run(string name, string file) => Path.Combine(Results, name, "c000", "seed-0", file);
        JsonNode FirstRun(JsonNode sweep) => sweep["cells"]![0]!["runs"]![0]!;
        void Rewrite(SweepFixture fixture)
        {
            Directory.Delete(Path.Combine(Results, fixture.Name), recursive: true);
            fixture.Write(Results);
        }

        switch (mutation)
        {
            case "missing-sweep":
                Directory.Delete(Path.Combine(Results, "baseline-final"), recursive: true);
                break;
            case "sweep-invalid-json":
                File.WriteAllText(Sweep("infantry-radius"), "{");
                break;
            case "sweep-format-version":
                EditJson(Sweep("infantry-radius"), j => j["formatVersion"] = 2);
                break;
            case "sweep-field-missing":
                EditJson(Sweep("infantry-radius"), j => j.AsObject().Remove("scenario"));
                break;
            case "sweep-field-wrong-type":
                EditJson(Sweep("infantry-radius"), j => j["cells"] = new JsonObject());
                break;
            case "sweep-no-cells":
                EditJson(Sweep("infantry-radius"), j => j["cells"] = new JsonArray());
                break;
            case "cell-no-runs":
                EditJson(Sweep("infantry-radius"), j => j["cells"]![0]!["runs"] = new JsonArray());
                break;
            case "run-not-completed":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["completed"] = false);
                break;
            case "run-completed-not-bool":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["completed"] = "yes");
                break;
            case "run-seed-index-fraction":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["seedIndex"] = 0.5);
                break;
            case "run-seed-negative":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["seed"] = -1);
                break;
            case "run-seed-differs-from-index":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["seed"] = 99);
                break;
            case "run-folder-escapes":
                EditJson(Sweep("infantry-radius"), j => FirstRun(j)["folder"] = "../vehicle-radius/c000/seed-0");
                break;
            case "run-incomplete-marker":
                File.WriteAllText(Run("infantry-radius", "_incomplete"), string.Empty);
                break;
            case "manifest-seed-differs":
                EditJson(Run("infantry-radius", "manifest.json"), j => j["seed"] = 99);
                break;
            case "summary-missing":
                File.Delete(Run("infantry-radius", "summary.json"));
                break;
            case "summary-run-id-differs":
                EditJson(Run("infantry-radius", "summary.json"), j => j["runId"] = "another-run");
                break;
            case "summary-not-completed":
                EditJson(Run("infantry-radius", "summary.json"), j => j["completed"] = false);
                break;
            case "summary-violation":
                EditJson(Run("infantry-radius", "summary.json"), j => j["firstViolation"] = new JsonObject { ["rule"] = 1, ["tick"] = 40 });
                break;
            case "metric-missing":
                EditJson(Run("infantry-radius", "summary.json"), j => j["fusedFightPairs"]!.AsObject().Remove("p50"));
                break;
            case "metric-parent-is-number":
                EditJson(Run("infantry-radius", "summary.json"), j => j["fusedFightPairs"] = 3);
                break;
            case "metric-not-number":
                EditJson(Run("infantry-radius", "summary.json"), j => j["fusedFightPairs"]!["p50"] = "many");
                break;
            case "metric-class-not-number":
                EditJson(Run("anchoring", "summary.json"), j => j["rates"]!["fusionsPerSecondByClass"]!["Infantry"] = "many");
                break;
            case "metric-divisor-zero":
                EditJson(Run("spacing", "summary.json"), j => j["ticks"] = 0);
                break;
            case "x-missing":
                EditJson(Sweep("infantry-radius"), j => j["cells"]![0]!["parameters"] = new JsonObject { ["regionizer.claimRadius.aircraft"] = 8 });
                break;
            case "x-not-number":
                EditJson(Sweep("infantry-radius"), j => j["cells"]![0]!["parameters"]!["regionizer.claimRadius.infantry"] = "wide");
                break;
            case "x-not-inferable":
                EditJson(Sweep("spacing"), j => j["cells"]![0]!["parameters"]!["scenarioParameters.lanes"] = 2);
                break;
            case "adopt-candidate-missing":
                Rewrite(new SweepFixture("air-layer", "AirTraffic").Cell(P(("airLayer.enabled", false)), S(), S()));
                break;
            case "reading-two-cells":
                Rewrite(new SweepFixture("baseline-final", "Baseline")
                    .Cell(P(("regionizer.claimRadius.infantry", 16)), S(), S())
                    .Cell(P(("regionizer.claimRadius.infantry", 20)), S(), S()));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unknown mutation");
        }
    }

    private static void EditJson(string path, Action<JsonNode> edit)
    {
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    /// <summary>A rules file holding just the committed rules with these ids.</summary>
    private static MemoRules RulesFor(params string[] ids)
    {
        var json = JsonNode.Parse(File.ReadAllText(RulesPath()))!;
        var kept = json["values"]!.AsArray().Where(v => ids.Contains((string)v!["id"]!)).Select(v => v!.DeepClone()).ToArray();
        json["values"] = new JsonArray(kept);
        return MemoRules.Parse(json.ToJsonString());
    }

    /// <summary>A one-value rules file reading <c>fusedFightPairs.p50</c> from a sweep, with the given rule fields.</summary>
    private static MemoRules OneRule(string fields, string sweep = "radius") => MemoRules.Parse(
        $$"""{ "formatVersion": 1, "values": [ { "id": "value", "sweep": "{{sweep}}", "metric": { "paths": ["fusedFightPairs.p50"] }, {{fields}} } ] }""");

    private static string RulesPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MmoEngine.sln")))
            {
                return Path.Combine(dir.FullName, "simulator", "sweeps", "memo-rules.json");
            }
        }

        throw new InvalidOperationException("MmoEngine.sln not found above the test output folder.");
    }

    /// <summary>The summary table's rows, each split into its four cells.</summary>
    private static List<string[]> SummaryRows(string markdown) =>
        [.. markdown.Split('\n')
            .SkipWhile(line => !line.StartsWith("| Value | Recommended | Status | Margin |", StringComparison.Ordinal))
            .Skip(2)
            .TakeWhile(line => line.StartsWith('|'))
            .Select(line => line[2..^2].Split(" | "))];

    /// <summary>A value's section, from its heading to the next.</summary>
    private static string Section(string markdown, string title)
    {
        var start = markdown.IndexOf($"\n## {title}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no section for {title}");
        var end = markdown.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    /// <summary>A section's curve rows, without the header and separator.</summary>
    private static List<string> CurveRows(string section) =>
        [.. section.Split('\n').Where(line => line.StartsWith('|')).Skip(2)];

    private static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private sealed record ValueCase(string Title, string Recommended, SweepFixture Sweep, IReadOnlyList<string> CurveRows);
}
