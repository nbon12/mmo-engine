using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Analysis.Memo;
using Xunit;
using static MmoEngine.Regionizer.Simulator.Tests.Analysis.MemoResultSet;
using static MmoEngine.Regionizer.Simulator.Tests.Analysis.SweepFixture;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// The decision memo (US6.4, FR-032, research D15) from small synthetic sweep folders that follow the output
/// contracts, read with the committed <c>simulator/sweeps/memo-rules.json</c>.
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
    // curve it was read from, its margin, its seeds and its run IDs. Each row records the population it was
    // read at; the open-decision-1 rows are labeled recommendations recorded under Principle VI and the target
    // fight size a reading; and the measured options are reported side by side.
    [Fact]
    public void MemoHasARowPerValueWithEvidence()
    {
        Write(Results);
        var values = Values();
        var options = Options();
        var outPath = Path.Combine(_root, "memo", "decision-memo.md");

        var result = MemoGenerator.Write(Results, MemoRules.Load(RulesPath()), outPath);

        var markdown = File.ReadAllText(outPath);
        Assert.Equal(result.Markdown, markdown);
        Assert.True(result.Success);
        Assert.Contains($"All {values.Count} values and {options.Count} measured options were read.", markdown, StringComparison.Ordinal);

        // One summary row per value, in order: title, population, recommended value, label, status and margin.
        var table = SummaryRows(markdown);
        Assert.Equal(values.Select(c => c.Title), table.Select(r => r[0]));
        foreach (var (value, row) in values.Zip(table))
        {
            Assert.Equal(value.Population.ToString(System.Globalization.CultureInfo.InvariantCulture), row[1]);
            Assert.Equal(value.Recommended, row[2]);
            Assert.EndsWith(MemoGenerator.MeasurementLabel, row[2], StringComparison.Ordinal);
            Assert.Equal(value.Label, row[3]);
            Assert.Equal(value.Status, row[4]);
            Assert.False(string.IsNullOrWhiteSpace(row[5]), $"{value.Title}: no margin");

            // Each value's own section: the value, its label, its population, its margin, its seeds, its run IDs and its curve.
            var section = Section(markdown, value.Title);
            Assert.Contains($"- **Recommended**: {value.Recommended}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Label**: {value.Label}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Population**: {value.Population} (`population.total`, as every run's manifest records)\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Margin**: {row[5]}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Seeds**: {string.Join(", ", Seeds)}\n", section, StringComparison.Ordinal);
            var runIds = value.Sweeps.SelectMany(s => s.RunIds()).Select(id => $"`{id}`");
            Assert.Contains($"- **Runs**: {string.Join(", ", runIds)}\n", section, StringComparison.Ordinal);
            Assert.Equal(value.CurveRows, Tables(section)[0]);
        }

        // D15 populations: section size at the 5,000-entity stress bound, every other row at the 2,000-player cap.
        Assert.Equal(
            ["2000", "2000", "2000", "5000", "2000", "2000", "2000", "2000", "2000", "2000"],
            table.Select(r => r[1]));
        Assert.Equal([2000, 2000, 2000, 5000, 2000, 2000, 2000, 2000, 2000, 2000], result.Rows.Select(r => r.Population));

        // Open decision 1 values are recommendations recorded under Principle VI; the fight size is a reading.
        foreach (var title in new[] { "Claim radius, infantry", "Claim radius, ground vehicle", "Claim radius, aircraft", "Section size" })
        {
            var row = Assert.Single(result.Rows, r => r.Title == title);
            Assert.Equal(MemoLabel.Recommendation, row.Label);
            Assert.Equal(OpenDecision1, row.LabelText);
            Assert.Contains("- **Principle VI**: A recommendation for open decision 1", Section(markdown, title), StringComparison.Ordinal);
        }

        var fightSize = Assert.Single(result.Rows, r => r.Id == "target-fight-size");
        Assert.Equal(MemoLabel.Reading, fightSize.Label);
        Assert.Equal(TargetFightSizeLabel, fightSize.LabelText);
        Assert.Contains("open decision 2 (target fight size) is set by load tests", Section(markdown, "Target fight size"), StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Rows.Where(r => r.Id != "target-fight-size"), r => Assert.Equal(MemoLabel.Recommendation, r.Label));

        // The measured options, side by side, after the values, each with its population, seeds and runs.
        var measured = Section(markdown, "Measured options");
        Assert.True(markdown.IndexOf("\n## Measured options\n", StringComparison.Ordinal) > markdown.IndexOf("\n## Target fight size\n", StringComparison.Ordinal));
        Assert.Equal(options.Select(o => o.Id), result.MeasuredOptions.Select(o => o.Id));
        Assert.All(result.MeasuredOptions, o => Assert.True(o.Readable, o.Problem));
        foreach (var option in options)
        {
            var section = OptionSection(measured, option.Title);
            Assert.Contains("- **Status**: Read\n", section, StringComparison.Ordinal);
            Assert.Contains("- **Population**: 2000 (`population.total`, as every run's manifest records)\n", section, StringComparison.Ordinal);
            Assert.Contains("Interleaved scheduling in every run's manifest", section, StringComparison.Ordinal);
            Assert.Contains($"- **Seeds**: {string.Join(", ", Seeds)}\n", section, StringComparison.Ordinal);
            Assert.Contains($"- **Runs**: {string.Join(", ", option.Sweep.RunIds().Select(id => $"`{id}`"))}\n", section, StringComparison.Ordinal);
            Assert.Equal(option.TableRows, Tables(section)[0]);
        }

        Assert.Contains("| Metric | regionizer.mergeGap = 0 | regionizer.mergeGap = 1 |\n", measured, StringComparison.Ordinal);
        Assert.Contains("| Metric | regionizer.timerMode = TickCounter | regionizer.timerMode = Absolute |\n", measured, StringComparison.Ordinal);
    }

    // D15: the claim-radius and spacing rows record the region-count and largest-share curves next to the
    // fused-fight-pairs curve, and note where region count collapses.
    [Fact]
    public void ClaimRadiusAndSpacingRowsRecordTheConstitutionReading()
    {
        Write(Results);

        var result = MemoGenerator.Generate(Results, MemoRules.Load(RulesPath()));

        foreach (var value in Values())
        {
            var row = Assert.Single(result.Rows, r => r.Id == value.Id);
            var section = Section(result.Markdown, value.Title);
            if (value.ReadingRows.Count == 0)
            {
                Assert.Empty(row.Reading);
                Assert.Null(row.RegionCountNote);
                Assert.Single(Tables(section));
                continue;
            }

            Assert.Contains("| x | Fused fight pairs | Region count | Largest share |\n", section, StringComparison.Ordinal);
            Assert.Equal(value.ReadingRows, Tables(section)[1]);
            Assert.Contains($"- **Region count**: {row.RegionCountNote}\n", section, StringComparison.Ordinal);
        }

        const string Condition = $"(mean {FusedPath} ≥ 1)";
        static string At(string x, string regions, string share) =>
            $"at {x} the mean {RegionCountPath} is {regions} and the mean {SharePath} is {share}";
        Assert.Equal(
            $"Region count collapses at 20 m and above {Condition}: {At("20 m", "39", "0.31")}; one step below, {At("16 m", "49", "0.16")}.",
            Note(result, "claim-radius-infantry"));
        Assert.Equal(
            $"Region count collapses at 32 m and above {Condition}: {At("32 m", "34", "0.36")}; one step below, {At("24 m", "51", "0.12")}.",
            Note(result, "claim-radius-ground-vehicle"));
        Assert.Equal(
            $"Region count does not collapse in the swept range: no value from 8 to 20 m has mean {FusedPath} ≥ 1; {At("8 m", "60", "0.1")}, and {At("20 m", "57", "0.1")}.",
            Note(result, "claim-radius-aircraft"));
        Assert.Equal(
            $"Region count collapses at 400 m and below {Condition}: {At("400 m", "19", "0.41")}; one step above, {At("600 m", "30", "0.3")}.",
            Note(result, "minimum-base-spacing"));
    }

    [Theory]
    [InlineData("every", "Region count has collapsed at every swept value (mean fusedFightPairs.p50 ≥ 1 from 8 to 16 m)")]
    [InlineData("middle", "Region count collapses at 12 m (mean fusedFightPairs.p50 ≥ 1), not at one end of the sweep")]
    public void RegionCountNoteSaysWhenCollapseIsEverywhereOrInTheMiddle(string shape, string note)
    {
        double[] fused = shape == "every" ? [1, 2, 3] : [0, 2, 0];
        var sweep = new SweepFixture("radius", "Baseline");
        foreach (var (x, i) in new[] { (8, 0), (12, 1), (16, 2) })
        {
            sweep.Cell(P(("r", x)), S((FusedPath, fused[i]), (RegionCountPath, 10), (SharePath, 0.5)), S((FusedPath, fused[i]), (RegionCountPath, 10), (SharePath, 0.5)));
        }

        sweep.Write(Results);
        var rules = OneRule("""
            "rule": "SmallestWithin", "parameter": "r", "unit": "m", "threshold": 100,
            "constitutionReading": {
              "fusedFightPairs": { "paths": ["fusedFightPairs.p50"] }, "regionCount": { "paths": ["metrics.regionCount.p50"] },
              "largestShare": { "paths": ["metrics.largestRegionShare.p50"] }, "collapseAt": 1 }
            """);

        var row = Assert.Single(MemoGenerator.Generate(Results, rules).Rows);

        Assert.StartsWith(note, row.RegionCountNote, StringComparison.Ordinal);
        Assert.Equal(3, row.Reading.Count);
    }

    // D16: merge rates read real merges, not absorptions. When the results report absorptions (in summary.json
    // or cells.csv) the row says they are left out; when they do not, the rate is read as written, with a note.
    [Fact]
    public void MergeRatesSayHowAbsorptionsAreCounted()
    {
        Write(Results);

        var result = MemoGenerator.Generate(Results, MemoRules.Load(RulesPath()));

        Assert.True(result.Success);
        var damping = Assert.Single(result.Rows, r => r.Id == "split-damping");
        Assert.Equal(
            "Merge rates count real merges only, where both sides held an entity. Absorptions of entity-less regions are reported "
                + "separately (summary.json `totals.absorptions`: mean 5 per run, 4 to 6) and are not in the rate.",
            damping.MergeCounting);
        Assert.Contains($"- **Merge counting**: {damping.MergeCounting}\n", Section(result.Markdown, "Split damping"), StringComparison.Ordinal);

        var air = Assert.Single(result.Rows, r => r.Id == "air-layer");
        Assert.StartsWith(
            "The results report no absorptions (no `totals.absorptions` in summary.json or cells.csv column `absorptions`), so the merge rate is read as written",
            air.MergeCounting,
            StringComparison.Ordinal);
        Assert.Equal(MemoRowStatus.Recommended, air.Status);

        var gap = Assert.Single(result.MeasuredOptions, o => o.Id == "merge-gap");
        Assert.Contains("(cells.csv `absorptions`: mean 3 per run, 3 to 3)", gap.MergeCounting, StringComparison.Ordinal);

        // The stopped timer run is left out of the clean runs whose absorptions are read.
        var timers = Assert.Single(result.MeasuredOptions, o => o.Id == "timer-mode");
        Assert.Contains("(summary.json `totals.absorptions`: mean 2 per run, 2 to 2)", timers.MergeCounting, StringComparison.Ordinal);

        Assert.All(result.Rows.Where(r => r.Id is not ("split-damping" or "air-layer")), r => Assert.Null(r.MergeCounting));
    }

    // cells.csv is optional evidence: a missing, empty or unkeyed file, or a malformed row, gives no absorptions and
    // the rate is read as written; a summary field that is not a number falls back to cells.csv.
    [Theory]
    [InlineData("cells-present", true)]
    [InlineData("cells-missing", false)]
    [InlineData("cells-empty", false)]
    [InlineData("cells-no-key-columns", false)]
    [InlineData("cells-malformed-rows", false)]
    [InlineData("summary-absorptions-not-number", true)]
    public void AbsorptionsAreReadFromTheSummaryOrCellsCsvWhenPresent(string variant, bool reported)
    {
        var absorptions = variant == "summary-absorptions-not-number" ? "x" : null;
        new SweepFixture("gap", "Migration")
            .CellsColumn("absorptions", (_, _) => "3")
            .Cell(P(("g", 0)), S(("fusedFightPairs.p50", 1), ("totals.absorptions", absorptions)), S(("fusedFightPairs.p50", 2), ("totals.absorptions", absorptions)))
            .Write(Results);
        var cells = Path.Combine(Results, "gap", SweepResults.CellsFile);
        switch (variant)
        {
            case "cells-missing":
                File.Delete(cells);
                break;
            case "cells-empty":
                File.WriteAllText(cells, string.Empty);
                break;
            case "cells-no-key-columns":
                File.WriteAllLines(cells, ["cell,seed,absorptions", "c000,11,3", "c000,22,3"]);
                break;
            case "cells-malformed-rows":
                File.WriteAllLines(cells, ["cellId,seedIndex,absorptions", "c000,0", "c000,one,3"]);
                break;
        }

        var row = Assert.Single(MemoGenerator.Generate(
            Results,
            OneRule(""" "rule": "Lowest", "absorptions": { "paths": ["totals.absorptions"], "cellsColumn": "absorptions" } """, "gap")).Rows);

        Assert.Equal(MemoRowStatus.Recommended, row.Status);
        if (reported)
        {
            Assert.Contains("(cells.csv `absorptions`: mean 3 per run, 3 to 3)", row.MergeCounting, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("The results report no absorptions", row.MergeCounting, StringComparison.Ordinal);
        }
    }

    // A value read from two sweeps records its constitution reading per sweep, as series.
    [Fact]
    public void ConstitutionReadingFromSeveralSweepsIsBySeries()
    {
        foreach (var name in new[] { "a", "b" })
        {
            new SweepFixture(name, "Spacing")
                .Cell(P(("x", 100)), S((FusedPath, 1), (RegionCountPath, 5), (SharePath, 0.5)), S((FusedPath, 1), (RegionCountPath, 5), (SharePath, 0.5)))
                .Cell(P(("x", 200)), S((FusedPath, 0), (RegionCountPath, 9), (SharePath, 0.2)), S((FusedPath, 0), (RegionCountPath, 9), (SharePath, 0.2)))
                .Write(Results);
        }

        var rules = MemoRules.Parse("""
            { "formatVersion": 2, "values": [ {
              "id": "spacing", "rule": "SmallestWithin", "sweeps": ["a", "b"], "unit": "m", "population": 2000, "threshold": 1,
              "metric": { "paths": ["fusedFightPairs.p50"] },
              "constitutionReading": {
                "fusedFightPairs": { "paths": ["fusedFightPairs.p50"] }, "regionCount": { "paths": ["metrics.regionCount.p50"] },
                "largestShare": { "paths": ["metrics.largestRegionShare.p50"] }, "collapseAt": 1 } } ] }
            """);

        var result = MemoGenerator.Generate(Results, rules);

        var row = Assert.Single(result.Rows);
        Assert.Equal(["a", "a", "b", "b"], row.Reading.Select(p => p.Series));
        Assert.Contains("| Series | x | Fused fight pairs | Region count | Largest share |\n", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("| a | 100 | 1 (1 to 1) | 5 (5 to 5) | 0.5 (0.5 to 0.5) |\n", result.Markdown, StringComparison.Ordinal);
        Assert.StartsWith("Region count collapses at 100 m and below", row.RegionCountNote, StringComparison.Ordinal);
    }

    [Fact]
    public void SomeRunsReportingAbsorptionsIsSaid()
    {
        new SweepFixture("one", "Migration")
            .Cell(P(), S(("fusedFightPairs.p50", 1), ("totals.absorptions", 7)), S(("fusedFightPairs.p50", 1)))
            .Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Lowest", "absorptions": { "paths": ["totals.absorptions"] } """, "one")).Rows);

        Assert.Equal(
            "1 of 2 runs report absorptions separately (summary.json `totals.absorptions`: mean 7 per run, 7 to 7), and their merge rates "
                + "count real merges only; the other runs' merge rates are read as written.",
            row.MergeCounting);
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
    [InlineData("manifest-population-differs", "claim-radius-infantry", "ran at population.total 5000, but the rules read this value at 2000")]
    [InlineData("manifest-population-missing", "claim-radius-infantry", "manifest.json does not record parameters.population.total")]
    [InlineData("manifest-population-not-whole", "claim-radius-infantry", "'parameters.population.total' is not a whole number")]
    [InlineData("section-size-at-cap", "section-size", "ran at population.total 2000, but the rules read this value at 5000")]
    [InlineData("summary-missing", "claim-radius-infantry", "summary.json: cannot be read")]
    [InlineData("summary-run-id-differs", "claim-radius-infantry", "differs from the manifest's")]
    [InlineData("summary-not-completed", "claim-radius-infantry", "the run did not complete")]
    [InlineData("summary-violation", "claim-radius-infantry", "stopped at a violation")]
    [InlineData("metric-missing", "claim-radius-infantry", "has no 'fusedFightPairs.p50'")]
    [InlineData("metric-parent-is-number", "claim-radius-infantry", "has no 'fusedFightPairs.p50'")]
    [InlineData("metric-not-number", "claim-radius-infantry", "'fusedFightPairs.p50' is not a number")]
    [InlineData("reading-metric-missing", "claim-radius-infantry", "has no 'metrics.regionCount.p50'")]
    [InlineData("metric-class-not-number", "claim-anchoring", "'rates.fusionsPerSecondByClass.Infantry' is not a number")]
    [InlineData("metric-divisor-zero", "minimum-base-spacing", "'ticks' is 0, not positive")]
    [InlineData("x-missing", "claim-radius-infantry", "does not set 'regionizer.claimRadius.infantry'")]
    [InlineData("x-not-number", "claim-radius-infantry", "needs a number")]
    [InlineData("x-not-inferable", "minimum-base-spacing", "names no 'parameter'")]
    [InlineData("adopt-candidate-missing", "air-layer", "no cell with airLayer.enabled = true")]
    [InlineData("reading-two-cells", "target-fight-size", "exactly one cell, found 2")]
    public void UnreadableResultsMarkTheRowAndFailTheMemo(string mutation, string valueId, string reason)
    {
        Write(Results);

        Mutate(mutation);

        var result = MemoGenerator.Generate(Results, MemoRules.Load(RulesPath()));

        Assert.False(result.Success);
        var row = Assert.Single(result.Rows, r => r.Status != MemoRowStatus.Recommended);
        Assert.Equal(valueId, row.Id);
        Assert.Equal(MemoRowStatus.Unreadable, row.Status);
        Assert.Contains(reason, row.Problem, StringComparison.Ordinal);
        Assert.Empty(row.Curve);
        Assert.All(result.MeasuredOptions, o => Assert.True(o.Readable, o.Problem));
        Assert.Contains($"**1 of {result.Rows.Count} values and 0 of {result.MeasuredOptions.Count} measured options could not be read.**", result.Markdown, StringComparison.Ordinal);
        var tableRow = Assert.Single(SummaryRows(result.Markdown), r => r[0] == row.Title);
        Assert.Equal("**not read**", tableRow[2]);
        Assert.Equal("**UNREADABLE**", tableRow[4]);
        var section = Section(result.Markdown, row.Title);
        Assert.Contains($"- **Why**: {row.Problem}\n", section, StringComparison.Ordinal);
        Assert.Contains("(`population.total` the rules read it at; not confirmed, the results could not be read)", section, StringComparison.Ordinal);
        Assert.Contains("No curve: the results could not be read.", section, StringComparison.Ordinal);
        Assert.Contains("- **Seeds**: none\n", section, StringComparison.Ordinal);
        Assert.Contains("- **Runs**: none\n", section, StringComparison.Ordinal);
    }

    // A measured option that cannot be read is marked, and the memo does not succeed (the CLI exits 4).
    [Theory]
    [InlineData("option-missing", "timer-mode", "no cell with regionizer.timerMode = Absolute")]
    [InlineData("option-extra-cell", "merge-gap", "cell c002 is not one of the options 0, 1")]
    [InlineData("option-duplicate-cell", "merge-gap", "2 cells set regionizer.mergeGap = 1; an option needs exactly one")]
    [InlineData("option-wrong-scenario", "merge-gap", "the sweep runs Baseline, but the option is measured in Migration")]
    [InlineData("option-wrong-scheduling", "merge-gap", "scheduling Lockstep, but the option is measured with Interleaved")]
    [InlineData("option-wrong-population", "timer-mode", "ran at population.total 5000, but the rules read this value at 2000")]
    [InlineData("option-interrupted-run", "timer-mode", "the run did not complete")]
    [InlineData("option-missing-sweep", "merge-gap", "merge-gap/sweep.json: cannot be read")]
    public void UnreadableMeasuredOptionIsMarkedAndFailsTheMemo(string mutation, string optionId, string reason)
    {
        Write(Results);

        Mutate(mutation);

        var result = MemoGenerator.Generate(Results, MemoRules.Load(RulesPath()));

        Assert.False(result.Success);
        Assert.All(result.Rows, r => Assert.NotEqual(MemoRowStatus.Unreadable, r.Status));
        var option = Assert.Single(result.MeasuredOptions, o => !o.Readable);
        Assert.Equal(optionId, option.Id);
        Assert.Contains(reason, option.Problem, StringComparison.Ordinal);
        Assert.Empty(option.Table);
        Assert.Contains($"**0 of {result.Rows.Count} values and 1 of {result.MeasuredOptions.Count} measured options could not be read.**", result.Markdown, StringComparison.Ordinal);
        var section = OptionSection(Section(result.Markdown, "Measured options"), option.Title);
        Assert.Contains("- **Status**: **UNREADABLE**\n", section, StringComparison.Ordinal);
        Assert.Contains($"- **Why**: {option.Problem}\n", section, StringComparison.Ordinal);
        Assert.Contains("No table: the results could not be read.", section, StringComparison.Ordinal);
    }

    // A run that stopped at an invariant violation is counted against its option, not read as a metric.
    [Fact]
    public void MeasuredOptionCountsRunsStoppedByAnInvariantViolation()
    {
        new SweepFixture("gap", "Migration")
            .Cell(P(("g", 0)), S(("rates.splitsPerSecond", 0.5)), S(("rates.splitsPerSecond", 0.7)))
            .Cell(P(("g", 1)), Join(S(("rates.splitsPerSecond", 9)), Stopped("Violation")), Join(S(("rates.splitsPerSecond", 9)), Stopped("Violation")))
            .Write(Results);
        var rules = MemoRules.Parse("""
            { "formatVersion": 2, "values": [ { "id": "v", "rule": "Reading", "sweep": "gap", "population": 2000, "parameter": "g", "metric": { "paths": ["x"] } } ],
              "measuredOptions": [ { "id": "o", "title": "Gap", "sweep": "gap", "parameter": "g", "options": [0, 1], "population": 2000,
                "metrics": [ { "label": "Splits", "paths": ["rates.splitsPerSecond"] } ] } ] }
            """);

        var option = Assert.Single(MemoGenerator.Generate(Results, rules).MeasuredOptions);

        Assert.True(option.Readable, option.Problem);
        Assert.Equal(["g = 0", "g = 1"], option.Options);
        Assert.Equal(
            [
                new OptionTableRow("Splits", ["0.6 (0.5 to 0.7)", "no clean run"]),
                new OptionTableRow("Runs stopped by a timer fault", ["0 of 2", "0 of 2"]),
                new OptionTableRow("Runs stopped by an invariant violation", ["0 of 2", "2 of 2"]),
            ],
            option.Table,
            new TableRowComparer());
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
        var summary = Assert.Single(SummaryRows(result.Markdown));
        Assert.Equal(["2000", "none", "Recommendation", "**FLAGGED**", row.Problem], summary[1..]);
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

    // A value read from two sweeps (Baseline and One base as separate sweeps) must qualify in both.
    [Fact]
    public void ValueReadFromSeveralSweepsMustQualifyInEach()
    {
        new SweepFixture("size-baseline", "Baseline", population: 5000)
            .Cell(P(("s", 8)), S(("budget.bookkeepingP99Micros", 900)), S(("budget.bookkeepingP99Micros", 800)))
            .Cell(P(("s", 16)), S(("budget.bookkeepingP99Micros", 500)), S(("budget.bookkeepingP99Micros", 400)))
            .Write(Results);
        new SweepFixture("size-one-base", "OneBase", population: 5000)
            .Cell(P(("s", 8)), S(("budget.bookkeepingP99Micros", 1200)), S(("budget.bookkeepingP99Micros", 800)))
            .Cell(P(("s", 16)), S(("budget.bookkeepingP99Micros", 700)), S(("budget.bookkeepingP99Micros", 600)))
            .Write(Results);
        var rules = MemoRules.Parse("""
            { "formatVersion": 2, "values": [ {
              "id": "size", "rule": "SmallestWithin", "sweeps": ["size-baseline", "size-one-base"], "unit": "m", "population": 5000,
              "metric": { "paths": ["budget.bookkeepingP99Micros"] }, "threshold": 1000, "inclusive": true } ] }
            """);

        var row = Assert.Single(MemoGenerator.Generate(Results, rules).Rows);

        Assert.Equal($"16 m {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Equal(5000, row.Population);
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
        Assert.Equal("Recommendation", row.LabelText);
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

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Lowest", "openDecision": 8 """, "one")).Rows);

        Assert.Equal($"base {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.StartsWith("Bears on open decision 8", row.Note, StringComparison.Ordinal);
        Assert.Equal("Recommendation, recorded under Principle VI (open decision 8)", row.LabelText);
    }

    [Fact]
    public void ReadingForAnotherOpenDecisionIsARecordedReading()
    {
        new SweepFixture("one", "Baseline").Cell(P(), S(("fusedFightPairs.p50", 2)), S(("fusedFightPairs.p50", 4))).Write(Results);

        var row = Assert.Single(MemoGenerator.Generate(Results, OneRule(""" "rule": "Reading", "openDecision": 8 """, "one")).Rows);

        Assert.Equal($"3 {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Equal("Reading, recorded under Principle VI (open decision 8)", row.LabelText);
        Assert.StartsWith("Bears on open decision 8", row.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingWithoutAnOpenDecisionIsLabeledAReading()
    {
        new SweepFixture("one", "Baseline").Cell(P(), S(("fusedFightPairs.p50", 2)), S(("fusedFightPairs.p50", 4))).Write(Results);

        var result = MemoGenerator.Generate(Results, OneRule(""" "rule": "Reading" """, "one"));

        var row = Assert.Single(result.Rows);
        Assert.Equal($"3 {MemoGenerator.MeasurementLabel}", row.Recommended);
        Assert.Equal(MemoLabel.Reading, row.Label);
        Assert.Equal("Reading", row.LabelText);
        Assert.Equal(["Reading", "Read"], Assert.Single(SummaryRows(result.Markdown))[3..5]);
    }

    [Fact]
    public void CommittedRulesListEveryValueAndMeasuredOptionTheSimulatorSets()
    {
        var rules = MemoRules.Load(RulesPath());

        Assert.Equal(
            [
                "claim-radius-infantry", "claim-radius-ground-vehicle", "claim-radius-aircraft", "section-size", "claim-anchoring",
                "minimum-base-spacing", "lane-bridging-limit", "split-damping", "air-layer", "target-fight-size",
            ],
            rules.Values.Select(v => v.Id));
        Assert.Equal(["merge-gap", "timer-mode"], rules.MeasuredOptions.Select(o => o.Id));

        // D15 sweep names, in memo order.
        Assert.Equal(
            [
                "infantry-radius", "vehicle-radius", "aircraft-radius", "section-size", "section-size-onebase", "anchoring", "spacing",
                "lane-traffic", "split-damping", "air-layer", "baseline-final", "merge-gap", "timer-mode",
            ],
            rules.Values.SelectMany(v => v.Sweeps).Concat(rules.MeasuredOptions.Select(o => o.Sweep)));

        // D15 populations: every row reads the 2,000-player cap except section size, which reads 5,000.
        Assert.Equal([2000, 2000, 2000, 5000, 2000, 2000, 2000, 2000, 2000, 2000], rules.Values.Select(v => v.Population));
        Assert.All(rules.MeasuredOptions, o => Assert.Equal(2000, o.Population));

        // The claim-radius and spacing rows record the constitution reading; merge-rate rows say how absorptions count.
        Assert.Equal(
            ["claim-radius-infantry", "claim-radius-ground-vehicle", "claim-radius-aircraft", "minimum-base-spacing"],
            rules.Values.Where(v => v.ConstitutionReading is not null).Select(v => v.Id));
        Assert.Equal(["split-damping", "air-layer"], rules.Values.Where(v => v.Absorptions is not null).Select(v => v.Id));

        // Open decision 1 rows are recommendations; the target fight size (open decision 2) is the only reading.
        Assert.Equal(
            ["claim-radius-infantry", "claim-radius-ground-vehicle", "claim-radius-aircraft", "section-size"],
            rules.Values.Where(v => v.OpenDecision == 1).Select(v => v.Id));
        Assert.Equal(["target-fight-size"], rules.Values.Where(v => v.Label == MemoLabel.Reading).Select(v => v.Id));

        // The measured options: merge gap 0 vs 1 and tick-counter vs absolute timers, from Migration in interleaved mode.
        Assert.Equal(["0", "1"], rules.MeasuredOptions[0].Options);
        Assert.Equal(["TickCounter", "Absolute"], rules.MeasuredOptions[1].Options);
        Assert.All(rules.MeasuredOptions, o =>
        {
            Assert.Equal("Migration", o.Scenario);
            Assert.Equal("Interleaved", o.Scheduling);
            Assert.NotNull(o.Absorptions);
        });
        Assert.All(rules.Values, v => Assert.False(string.IsNullOrWhiteSpace(v.Description)));
    }

    [Theory]
    [InlineData("{", "not valid JSON")]
    [InlineData("[]", "$: must be an object")]
    [InlineData("""{ "values": [] }""", "formatVersion: is required")]
    [InlineData("""{ "formatVersion": 1, "values": [] }""", "formatVersion: must be 2")]
    [InlineData("""{ "formatVersion": "2", "values": [] }""", "formatVersion: must be a number")]
    [InlineData("""{ "formatVersion": 2 }""", "values: is required")]
    [InlineData("""{ "formatVersion": 2, "values": {} }""", "values: must be an array")]
    [InlineData("""{ "formatVersion": 2, "values": [] }""", "values: must list at least one value")]
    [InlineData("""{ "formatVersion": 2, "values": [ 3 ] }""", "values[0]: must be an object")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "rule": "Reading" } ] }""", "values[0].id: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": " ", "rule": "Reading" } ] }""", "values[0].id: must be a non-empty string")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Median", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].rule: unknown rule 'Median'")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "metric": { "paths": ["x"] } } ] }""", "values[0].sweeps: a 'sweep' or 'sweeps' is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweeps": [], "metric": { "paths": ["x"] } } ] }""", "values[0].sweeps: must name at least one sweep")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s" } ] }""", "values[0].metric: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": "x" } ] }""", "values[0].metric: must be an object with 'paths'")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": [] } } ] }""", "values[0].metric.paths: must list at least one summary path")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"], "absentAsZero": 1 } } ] }""", "values[0].metric.absentAsZero: must be true or false")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"] } } ] }""", "values[0].population: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "population": 0, "metric": { "paths": ["x"] } } ] }""", "values[0].population: must be at least 1")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "population": 2000.5, "metric": { "paths": ["x"] } } ] }""", "values[0].population: must be a whole number")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "population": "2000", "metric": { "paths": ["x"] } } ] }""", "values[0].population: must be a number")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "SmallestWithin", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } } ] }""", "values[0].threshold: is required for SmallestWithin")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "LargestWithin", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "threshold": "1" } ] }""", "values[0].threshold: must be a number")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } } ] }""", "values[0].collapseAt: is required for ClaimRadius")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "collapseAt": 1 } ] }""", "values[0].floor: is required for ClaimRadius")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": 1 } ] }""", "values[0].floor: must be an object")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": { "interactionRangeMetres": -1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30 } } ] }""", "values[0].floor: Must be finite and non-negative.")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "collapseAt": 1, "stepsBelowCollapse": 0, "floor": { "interactionRangeMetres": 1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30 } } ] }""", "values[0].stepsBelowCollapse: must be at least 1")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "collapseAt": 1, "stepsBelowCollapse": 1.5 } ] }""", "values[0].stepsBelowCollapse: must be a whole number")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constraint": 1 } ] }""", "values[0].constraint: must be an object")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constraint": { "threshold": 1 } } ] }""", "values[0].constraint.metric: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "inclusive": "yes", "threshold": 1 } ] }""", "values[0].inclusive: must be true or false")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } } ] }""", "values[0].parameter: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "population": 1, "parameter": "p", "metric": { "paths": ["x"] } } ] }""", "values[0].baseline: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "population": 1, "parameter": "p", "baseline": false, "metric": { "paths": ["x"] } } ] }""", "values[0].candidate: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "population": 1, "parameter": "p", "baseline": false, "candidate": true, "metric": { "paths": ["x"] } } ] }""", "values[0].minimumDropPercent: is required for AdoptIfDrop")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "AdoptIfDrop", "sweep": "s", "population": 1, "parameter": "p", "baseline": [], "metric": { "paths": ["x"] } } ] }""", "values[0].baseline: must be a string, number or boolean")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "openDecision": 1 } ] }""", "values[0].openDecision: open decision 1 values are recommendations recorded under Principle VI")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "openDecision": 2 } ] }""", "values[0].openDecision: open decision 2 (target fight size) is set by load tests")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constitutionReading": { "fusedFightPairs": { "paths": ["f"] }, "regionCount": { "paths": ["r"] }, "largestShare": { "paths": ["l"] }, "collapseAt": 1 } } ] }""", "values[0].constitutionReading: needs a rule with a numeric x")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "SmallestWithin", "threshold": 1, "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constitutionReading": 1 } ] }""", "values[0].constitutionReading: must be an object")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "SmallestWithin", "threshold": 1, "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constitutionReading": { "fusedFightPairs": { "paths": ["f"] }, "largestShare": { "paths": ["l"] }, "collapseAt": 1 } } ] }""", "values[0].constitutionReading.regionCount: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "SmallestWithin", "threshold": 1, "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constitutionReading": { "fusedFightPairs": { "paths": ["f"] }, "regionCount": { "paths": ["r"] }, "largestShare": { "paths": ["l"] } } } ] }""", "values[0].constitutionReading.collapseAt: is required")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "SmallestWithin", "threshold": 1, "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "constitutionReading": { "regionsCount": 1 } } ] }""", "values[0].constitutionReading.regionsCount: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "absorptions": "totals.absorptions" } ] }""", "values[0].absorptions: must be an object with 'paths'")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "absorptions": { "paths": [] } } ] }""", "values[0].absorptions.paths: must list at least one summary path")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "population": 1, "metric": { "paths": ["x"] }, "absorptions": { "paths": ["a"], "column": "b" } } ] }""", "values[0].absorptions.column: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } }, { "id": "a", "rule": "Reading", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } } ] }""", "values[1].id: duplicate id 'a'")]
    [InlineData("""{ "formatVersion": 2, "valeus": [] }""", "valeus: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"] }, "inclusiv": true } ] }""", "values[0].inclusiv: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Reading", "sweep": "s", "metric": { "paths": ["x"], "divide": "ticks" } } ] }""", "values[0].metric.divide: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "Lowest", "sweep": "s", "metric": { "paths": ["x"] }, "constraint": { "metric": { "paths": ["y"] }, "threshold": 1, "max": 2 } } ] }""", "values[0].constraint.max: is not a known field")]
    [InlineData("""{ "formatVersion": 2, "values": [ { "id": "a", "rule": "ClaimRadius", "sweep": "s", "metric": { "paths": ["x"] }, "collapseAt": 1, "floor": { "interactionRangeMetres": 1, "closingSpeedMetresPerSecond": 1, "tickRateHz": 30, "dt": 1 } } ] }""", "values[0].floor.dt: is not a known field")]
    public void InvalidRulesNameTheBadField(string json, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => MemoRules.Parse(json));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""3""", "measuredOptions: must be an array")]
    [InlineData("""[ 3 ]""", "measuredOptions[0]: must be an object")]
    [InlineData("""[ { "title": "t" } ]""", "measuredOptions[0].id: is required")]
    [InlineData("""[ { "id": "o", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].sweep: is required")]
    [InlineData("""[ { "id": "o", "sweep": "s", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].parameter: is required")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].options: is required")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].options: must list at least two options to compare")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 0], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].options[1]: duplicate option '0'")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, {}], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].options[1]: must be a string, number or boolean")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].population: is required")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [] } ]""", "measuredOptions[0].metrics: must list at least one metric")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "paths": ["x"] } ] } ]""", "measuredOptions[0].metrics[0].label: is required")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": [] } ] } ]""", "measuredOptions[0].metrics[0].paths: must list at least one summary path")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": ["x"], "unit": "s" } ] } ]""", "measuredOptions[0].metrics[0].unit: is not a known field")]
    [InlineData("""[ { "id": "o", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ], "recommend": true } ]""", "measuredOptions[0].recommend: is not a known field")]
    [InlineData("""[ { "id": "v", "sweep": "s", "parameter": "p", "options": [0, 1], "population": 1, "metrics": [ { "label": "m", "paths": ["x"] } ] } ]""", "measuredOptions[0].id: duplicate id 'v'")]
    public void InvalidMeasuredOptionsNameTheBadField(string measuredOptions, string message)
    {
        var json = $$"""
            { "formatVersion": 2, "values": [ { "id": "v", "rule": "Reading", "sweep": "s", "population": 1, "metric": { "paths": ["x"] } } ],
              "measuredOptions": {{measuredOptions}} }
            """;

        var error = Assert.Throws<InvalidDataException>(() => MemoRules.Parse(json));

        Assert.Contains($"memo rules: {message}", error.Message, StringComparison.Ordinal);
    }

    // A rules file with no measured options is valid: the memo then has none.
    [Fact]
    public void RulesWithoutMeasuredOptionsGiveAMemoWithoutThem()
    {
        new SweepFixture("one", "Baseline").Cell(P(), S(("fusedFightPairs.p50", 2)), S(("fusedFightPairs.p50", 4))).Write(Results);

        var result = MemoGenerator.Generate(Results, OneRule(""" "rule": "Lowest" """, "one"));

        Assert.Empty(result.MeasuredOptions);
        Assert.True(result.Success);
        Assert.Contains("All 1 values were read.", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Measured options", result.Markdown, StringComparison.Ordinal);
    }

    private static string? Note(MemoResult result, string id) => Assert.Single(result.Rows, r => r.Id == id).RegionCountNote;

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
            case "manifest-population-differs":
                EditJson(Run("infantry-radius", "manifest.json"), j => j["parameters"]!["population"]!["total"] = 5000);
                break;
            case "manifest-population-missing":
                EditJson(Run("infantry-radius", "manifest.json"), j => j["parameters"] = new JsonObject());
                break;
            case "manifest-population-not-whole":
                EditJson(Run("infantry-radius", "manifest.json"), j => j["parameters"]!["population"]!["total"] = 2000.5);
                break;
            case "section-size-at-cap":
                EditJson(Run("section-size-onebase", "manifest.json"), j => j["parameters"]!["population"]!["total"] = 2000);
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
            case "reading-metric-missing":
                EditJson(Run("infantry-radius", "summary.json"), j => j["metrics"]!["regionCount"]!.AsObject().Remove("p50"));
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
            case "option-missing":
                Rewrite(new SweepFixture("timer-mode", "Migration", scheduling: "Interleaved").Cell(P(("regionizer.timerMode", "TickCounter")), S(), S()));
                break;
            case "option-extra-cell":
                EditJson(Sweep("merge-gap"), j => j["cells"]!.AsArray().Add(new JsonObject
                {
                    ["cellId"] = "c002",
                    ["parameters"] = new JsonObject { ["regionizer.mergeGap"] = 2 },
                    ["runs"] = j["cells"]![0]!["runs"]!.DeepClone(),
                }));
                break;
            case "option-duplicate-cell":
                EditJson(Sweep("merge-gap"), j => j["cells"]!.AsArray().Add(new JsonObject
                {
                    ["cellId"] = "c002",
                    ["parameters"] = new JsonObject { ["regionizer.mergeGap"] = 1 },
                    ["runs"] = j["cells"]![1]!["runs"]!.DeepClone(),
                }));
                break;
            case "option-wrong-scenario":
                EditJson(Sweep("merge-gap"), j => j["scenario"] = "Baseline");
                break;
            case "option-wrong-scheduling":
                EditJson(Run("merge-gap", "manifest.json"), j => j["scheduling"] = "Lockstep");
                break;
            case "option-wrong-population":
                EditJson(Run("timer-mode", "manifest.json"), j => j["parameters"]!["population"]!["total"] = 5000);
                break;
            case "option-interrupted-run":
                EditJson(Run("timer-mode", "summary.json"), j => j["completed"] = false);
                break;
            case "option-missing-sweep":
                Directory.Delete(Path.Combine(Results, "merge-gap"), recursive: true);
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

    /// <summary>A rules file holding just the committed rules with these ids, and no measured options.</summary>
    private static MemoRules RulesFor(params string[] ids)
    {
        var json = JsonNode.Parse(File.ReadAllText(RulesPath()))!;
        var kept = json["values"]!.AsArray().Where(v => ids.Contains((string)v!["id"]!)).Select(v => v!.DeepClone()).ToArray();
        json["values"] = new JsonArray(kept);
        json.AsObject().Remove("measuredOptions");
        return MemoRules.Parse(json.ToJsonString());
    }

    /// <summary>A one-value rules file reading <c>fusedFightPairs.p50</c> at population 2,000 from a sweep, with the given rule fields.</summary>
    private static MemoRules OneRule(string fields, string sweep = "radius") => MemoRules.Parse(
        $$"""{ "formatVersion": 2, "values": [ { "id": "value", "sweep": "{{sweep}}", "population": 2000, "metric": { "paths": ["fusedFightPairs.p50"] }, {{fields}} } ] }""");

    /// <summary>The summary table's rows, each split into its six cells.</summary>
    private static List<string[]> SummaryRows(string markdown) =>
        [.. markdown.Split('\n')
            .SkipWhile(line => !line.StartsWith("| Value | Population | Recommended | Label | Status | Margin |", StringComparison.Ordinal))
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

    /// <summary>A measured option's subsection, from its heading to the next.</summary>
    private static string OptionSection(string measured, string title)
    {
        var start = measured.IndexOf($"\n### {title}\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no measured option {title}");
        var end = measured.IndexOf("\n### ", start + 1, StringComparison.Ordinal);
        return end < 0 ? measured[start..] : measured[start..end];
    }

    /// <summary>A section's tables, each as its rows without the header and separator.</summary>
    private static List<List<string>> Tables(string section)
    {
        var tables = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in section.Split('\n'))
        {
            if (!line.StartsWith('|'))
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = [];
                tables.Add(current);
            }

            current.Add(line);
        }

        return [.. tables.Select(t => t.Skip(2).ToList())];
    }

    /// <summary>Compares table rows by value (records holding lists compare their lists by reference).</summary>
    private sealed class TableRowComparer : IEqualityComparer<OptionTableRow>
    {
        public bool Equals(OptionTableRow? x, OptionTableRow? y) =>
            x is not null && y is not null && x.Metric == y.Metric && x.Cells.SequenceEqual(y.Cells);

        public int GetHashCode(OptionTableRow obj) => obj.Metric.GetHashCode(StringComparison.Ordinal);
    }
}
