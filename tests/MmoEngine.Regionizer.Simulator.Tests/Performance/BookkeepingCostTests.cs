using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Performance;

/// <summary>The bookkeeping cost per tick against its budget (US7, FR-027, SC-004, contracts/run-output.md).</summary>
public sealed class BookkeepingCostTests
{
    private const ulong Seed = 13;
    private const int Ticks = 30;
    private const int Entities = 5000;

    /// <summary>
    /// US7.1: Given the One base and Baseline scenarios at 5,000 entities, When each runs, Then its summary
    /// records bookkeeping cost per tick at p50, p95, p99 and max, records the serialized part separately,
    /// and states whether p99 is within the configured budget. Each scenario runs 30 ticks once with a
    /// 1 ns budget that no tick meets and once with a budget that no tick misses, so the summary states
    /// both outcomes. The percentiles are recomputed by nearest rank from the ticks.csv columns.
    /// </summary>
    [Theory]
    [InlineData("OneBase", 0.001, false)]
    [InlineData("OneBase", 1e12, true)]
    [InlineData("Baseline", 0.001, false)]
    [InlineData("Baseline", 1e12, true)]
    public void SummaryRecordsBookkeepingPercentilesSerializedAndBudget(string scenario, double budgetMicros, bool withinBudget)
    {
        var config = new JsonObject
        {
            ["scenario"] = scenario,
            ["seed"] = Seed,
            ["durationTicks"] = Ticks,
            ["population"] = new JsonObject { ["total"] = Entities },
            ["metrics"] = new JsonObject { ["bookkeepingBudgetMicros"] = budgetMicros },
        };
        var label = $"{scenario} seed {Seed}, budget {budgetMicros:R} µs";

        var result = TestRuns.Run(TestRuns.Load(config));

        Assert.True(result.Completed && result.TicksRun == Ticks, $"{label}: completed {result.Completed} after {result.TicksRun} ticks, {result.FirstViolation}");
        var manifest = Read(Path.Combine(result.OutputDirectory, RunFolder.ManifestFile));
        var population = manifest["parameters"]!["population"]!["total"]!.GetValue<int>();
        Assert.True(population == Entities, $"{label}: the manifest's population.total is {population}");

        var ticks = Path.Combine(result.OutputDirectory, RunFolder.TicksFile);
        var bookkeeping = Column(ticks, "bookkeepingMicros");
        var serialized = Column(ticks, "serializedMicros");
        Assert.True(bookkeeping.Length == Ticks && serialized.Length == Ticks, $"{label}: {bookkeeping.Length} bookkeeping and {serialized.Length} serialized rows");
        Assert.All(bookkeeping, v => Assert.True(v > 0 && double.IsFinite(v), $"{label}: a tick recorded {v:R} µs of bookkeeping"));
        Assert.All(serialized, v => Assert.True(v >= 0 && double.IsFinite(v), $"{label}: a tick recorded {v:R} µs of serialized bookkeeping"));

        var summary = Read(result.SummaryPath);
        foreach (var (metric, values) in new[] { ("bookkeepingMicros", bookkeeping), ("serializedMicros", serialized) })
        {
            var recorded = summary["metrics"]![metric]!;
            var sorted = values.Order().ToArray();
            foreach (var (key, percent) in new[] { ("p50", 50), ("p95", 95), ("p99", 99), ("max", 100) })
            {
                var expected = NearestRank(sorted, percent);
                var actual = recorded[key]!.GetValue<double>();
                Assert.True(expected.Equals(actual), $"{label}: summary {metric}.{key} is {actual:R}, ticks.csv gives {expected:R}");
            }
        }

        var p99 = summary["metrics"]!["bookkeepingMicros"]!["p99"]!.GetValue<double>();
        var budget = summary["budget"]!;
        var budgetP99 = budget["bookkeepingP99Micros"]!.GetValue<double>();
        var configured = budget["budgetMicros"]!.GetValue<double>();
        var within = budget["withinBudget"]!.GetValue<bool>();
        Assert.True(budgetP99.Equals(p99), $"{label}: budget.bookkeepingP99Micros {budgetP99:R} differs from metrics.bookkeepingMicros.p99 {p99:R}");
        Assert.True(configured.Equals(budgetMicros), $"{label}: budget.budgetMicros is {configured:R}");
        Assert.True(within == (p99 <= budgetMicros), $"{label}: withinBudget {within} with p99 {p99:R} µs");
        Assert.True(within == withinBudget, $"{label}: withinBudget {within}, expected {withinBudget} with p99 {p99:R} µs");
    }

    /// <summary>The value of rank ⌈p·n/100⌉ (1-based) in ascending order: the nearest-rank percentile.</summary>
    private static double NearestRank(double[] sorted, int percent)
    {
        var rank = (int)Math.Ceiling(percent * sorted.Length / 100.0);
        return sorted[Math.Max(rank, 1) - 1];
    }

    private static double[] Column(string csv, string name) => [.. TestRuns.CsvColumn(csv, name).Select(TestRuns.ParseDouble)];

    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
}
