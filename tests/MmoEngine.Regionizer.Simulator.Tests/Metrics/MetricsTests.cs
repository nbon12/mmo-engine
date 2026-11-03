using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Tests.Metrics;

/// <summary>The run metrics (FR-024, contracts/run-output.md, spec clarifications).</summary>
public sealed class MetricsTests
{
    private const double Radius = 16;

    /// <summary>A merge is a fusion when each side holds a fight tag the other does not.</summary>
    [Theory]
    [InlineData(new[] { 0 }, new[] { 1 }, true)]
    [InlineData(new[] { 0, 2 }, new[] { 1, 2 }, true)]
    [InlineData(new[] { 0 }, new[] { 0 }, false)]
    [InlineData(new[] { 0, 1 }, new[] { 1 }, false)]
    [InlineData(new[] { 0 }, new int[0], false)]
    [InlineData(new int[0], new int[0], false)]
    public void FusionNeedsATagOnEachSideThatTheOtherLacks(int[] tagsA, int[] tagsB, bool fusion)
    {
        Assert.Equal(fusion, MetricsCollector.IsFusion(Counts(tagsA), Counts(tagsB)));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(0, -1, 0)]
    public void MergingTwoTaggedRegionsIsCountedAsAFusionWithPlayersAfter(int tagA, int tagB, int expectedFusions)
    {
        var events = new List<RunEvent>();
        var harness = new Harness(
            [Spec(0, EntityClass.Infantry, 100, 100, tagA), Spec(1, EntityClass.Infantry, 100, 100, tagA), Spec(2, EntityClass.Infantry, 300, 100, tagB)],
            events.Add);

        harness.Regionizer.MoveEntity(2, 0, 120, 100);

        Assert.Equal(1, harness.Metrics.Merges);
        Assert.Equal(expectedFusions, harness.Metrics.Fusions);
        var fusions = events.Where(e => e.Type == RunEventType.Fusion).ToList();
        Assert.Equal(expectedFusions, fusions.Count);
        if (expectedFusions == 1)
        {
            Assert.Equal(3.0, fusions[0].Value);
            Assert.Equal(EntityClass.Infantry, fusions[0].CauseClass);
            Assert.Equal(2, fusions[0].CauseEntity);
        }
    }

    [Fact]
    public void AbsorbingATransientHelperIsNotCountedAsAMerge()
    {
        var harness = new Harness([Spec(0, EntityClass.Infantry, 100, 100, 0)], _ => { });
        var region = harness.Regionizer.RegionIds[0];

        Assert.True(harness.Regionizer.TryMarkTicking(region));
        harness.Regionizer.MoveEntity(0, 0, 200, 100);
        harness.Regionizer.MarkNotTicking(region);

        Assert.True(harness.Regionizer.Stats.Merges >= 1, "the core absorbed its transient helper");
        Assert.Equal(0, harness.Metrics.Merges);
    }

    /// <summary>The region count counts regions holding at least one player.</summary>
    [Theory]
    [InlineData(EntityClass.Infantry, 1)]
    [InlineData(EntityClass.GroundVehicle, 1)]
    [InlineData(EntityClass.Aircraft, 1)]
    [InlineData(EntityClass.Traveller, 1)]
    [InlineData(EntityClass.CapturePoint, 0)]
    public void RegionCountCountsPlayersOnly(EntityClass cls, int expected)
    {
        var harness = new Harness([Spec(0, cls, 100, 100, -1)], _ => { });

        var row = harness.EndTick();

        Assert.Single(harness.Regionizer.RegionIds.ToArray());
        Assert.Equal(expected, row.RegionCount);
        Assert.Equal(expected, row.LargestRegionPlayers);
    }

    [Fact]
    public void LargestRegionShareIsItsPlayersOverAllPlayers()
    {
        var harness = new Harness(
            [Spec(0, EntityClass.Infantry, 100, 100, 0), Spec(1, EntityClass.Infantry, 104, 100, 0), Spec(2, EntityClass.Infantry, 104, 104, 0), Spec(3, EntityClass.Traveller, 400, 400, -1), Spec(4, EntityClass.CapturePoint, 300, 300, -1)],
            _ => { });

        var row = harness.EndTick();

        Assert.Equal(2, row.RegionCount);
        Assert.Equal(3, row.LargestRegionPlayers);
        Assert.Equal(0.75, row.LargestRegionShare);
    }

    /// <summary>Each pair in different regions counts once: deferred within the deferred range, otherwise read-only within the read-only range.</summary>
    [Theory]
    [InlineData(100, false, 1, 0)]
    [InlineData(300, false, 1, 0)]
    [InlineData(300.5, false, 0, 1)]
    [InlineData(999.5, false, 0, 1)]
    [InlineData(1000, false, 0, 1)]
    [InlineData(1000.5, false, 0, 0)]
    [InlineData(100, true, 0, 0)]
    public void CrossRegionPairsUseTheDeferredAndReadOnlyRanges(double distance, bool sameRegion, long deferred, long readOnly)
    {
        double[] x = [1990, 1990 + distance];
        double[] y = [500, 500];
        long[] owner = [1, sameRegion ? 1 : 2];

        var (d, r) = CrossRegionPairs.Count([0, 1], x, y, e => owner[e], 300, 1000);

        Assert.Equal(deferred, d);
        Assert.Equal(readOnly, r);
    }

    [Fact]
    public void CrossRegionPairsCountEachPairOnceAcrossGridCells()
    {
        // Four players around a grid corner at (1000, 1000), each in its own region and cell.
        double[] x = [999, 1001, 999, 1001, 5000];
        double[] y = [999, 999, 1001, 1001, 5000];
        long[] owner = [1, 2, 3, 4, 0];

        var (d, r) = CrossRegionPairs.Count([0, 1, 2, 3, 4], x, y, e => owner[e], 300, 1000);

        Assert.Equal(6, d);
        Assert.Equal(0, r);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(10, false)]
    public void OversizedFusionIsFlaggedWhenAMultiFightRegionExceedsTheTarget(int target, bool flagged)
    {
        var harness = new Harness(
            [Spec(0, EntityClass.Infantry, 100, 100, 0), Spec(1, EntityClass.Infantry, 104, 100, 0), Spec(2, EntityClass.Infantry, 300, 100, 1)],
            _ => { },
            target);
        harness.Regionizer.MoveEntity(2, 0, 110, 100);

        var row = harness.EndTick();

        Assert.Equal(flagged, row.OversizedFusion);
        Assert.Equal(1, row.FusedFightPairs);
        Assert.Equal(flagged ? 1 : 0, harness.Metrics.OversizedFusionTicks);
    }

    [Fact]
    public void SplitLatencyIsMeasuredFromTheFirstSampledTwoIslandTick()
    {
        var events = new List<RunEvent>();
        var harness = new Harness(
            [Spec(0, EntityClass.Infantry, 100, 100, 0), Spec(1, EntityClass.Infantry, 110, 100, 0)],
            events.Add,
            splitMinAgeSteps: 10);
        var region = harness.Regionizer.RegionIds[0];
        Assert.True(harness.Regionizer.TryMarkTicking(region));
        harness.Regionizer.MoveEntity(1, 0, 400, 400);
        harness.Regionizer.MarkNotTicking(region);

        // Ticks 0..4: still young (T_min = 10 steps), two islands sampled from tick 0.
        for (var tick = 0L; tick < 5; tick++)
        {
            harness.Regionizer.AdvanceTo(tick * 2);
            harness.EndTick(tick);
        }

        harness.Regionizer.AdvanceTo(10);
        harness.Metrics.SetClock(5, 10);
        Assert.True(harness.Regionizer.TryMarkTicking(region));
        var result = harness.Regionizer.MarkNotTicking(region);

        Assert.Equal(2, result.SplitChildren);
        var latency = Assert.Single(events, e => e.Type == RunEventType.SplitLatency);
        Assert.Equal(5.0, latency.Value);
        Assert.Equal([5.0], harness.Metrics.Samples("splitLatencyTicks"));
    }

    [Fact]
    public void RegionLifetimesExcludeTransientHelpers()
    {
        var harness = new Harness([Spec(0, EntityClass.Infantry, 100, 100, 0)], _ => { });
        var region = harness.Regionizer.RegionIds[0];
        harness.Regionizer.AdvanceTo(6);
        Assert.True(harness.Regionizer.TryMarkTicking(region));
        harness.Regionizer.MoveEntity(0, 0, 200, 100);
        harness.Regionizer.MarkNotTicking(region);

        Assert.Empty(harness.Metrics.Samples("regionLifetimeSteps"));
    }

    /// <summary>Percentiles by nearest rank (worked example: 15, 20, 35, 40, 50).</summary>
    [Theory]
    [InlineData(5, 15.0)]
    [InlineData(30, 20.0)]
    [InlineData(40, 20.0)]
    [InlineData(50, 35.0)]
    [InlineData(100, 50.0)]
    public void PercentilesUseNearestRank(int percent, double expected)
    {
        double[] sorted = [15, 20, 35, 40, 50];

        Assert.Equal(expected, Percentiles.NearestRank(sorted, percent));
    }

    [Fact]
    public void SummaryOfOneToTenUsesNearestRankAndTheMean()
    {
        var summary = Percentiles.Summarize([.. Enumerable.Range(1, 10).Select(i => (double)(11 - i))]);

        Assert.Equal(new MetricSummary(5, 10, 10, 10, 5.5, 10), summary);
        Assert.Equal(default, Percentiles.Summarize([]));
    }

    [Fact]
    public void StateHashIsChainedAndFollowsTheState()
    {
        var a = new Harness([Spec(0, EntityClass.Infantry, 100, 100, 0)], _ => { });
        var b = new Harness([Spec(0, EntityClass.Infantry, 100, 100, 0)], _ => { });
        var c = new Harness([Spec(0, EntityClass.Infantry, 300, 100, 0)], _ => { });
        var ha = new StateHasher();
        var hb = new StateHasher();
        var hc = new StateHasher();

        var first = ha.Next(a.Regionizer);
        Assert.Equal(first, hb.Next(b.Regionizer));
        Assert.NotEqual(first, hc.Next(c.Regionizer));
        Assert.NotEqual(first, ha.Next(a.Regionizer));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public void IslandsAreJoinedByTheMergeGap(int mergeGap, int islands)
    {
        // One region whose two radius-0 claims, from section interiors, sit in neighbouring sections:
        // two islands unless the gap joins them.
        var topology = SectionTopology.OpenWorld(16, 256, 256);
        var options = new RegionizerOptions(16, ClaimAnchoring.Position, mergeGap, new ClaimRadii(0, 0, 0, 0, 0), 1, 0, 0, TimerMode.TickCounter);
        var regionizer = new Regionizer(topology, options);
        regionizer.AddEntity(0, EntityClass.Infantry, 0, 8, 8);
        regionizer.AddEntity(1, EntityClass.Infantry, 0, 8, 8);
        regionizer.MoveEntity(1, 0, 24, 8);

        var region = Assert.Single(regionizer.RegionIds.ToArray());
        Assert.Equal(islands, new IslandDetector(topology).CountIslands(regionizer, region));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void IslandsAreJoinedThroughOpenPortalsWithAGapOfOne(bool open, int islands)
    {
        // A portal between sections that are far apart on two layers: each entity's radius-0 claim
        // stays on its own side, so only the gap rule through the open portal joins them.
        var layers = new[] { new LayerSpec(0, 0, 0, 8, 8, new ushort[64]), new LayerSpec(1, 0, 0, 8, 8, new ushort[64]) };
        var topology = SectionTopology.Build(16, layers, [new PortalSpec(1, new SectionKey(0, 2, 2), new SectionKey(1, 6, 6), true)]);
        var options = new RegionizerOptions(16, ClaimAnchoring.Position, 1, new ClaimRadii(0, 0, 0, 0, 0), 1, 0, 0, TimerMode.TickCounter);
        var regionizer = new Regionizer(topology, options);
        regionizer.AddEntity(0, EntityClass.Infantry, 0, 40, 40);
        regionizer.AddEntity(1, EntityClass.Infantry, 1, 104, 104);
        regionizer.SetPortalOpen(1, open);

        var region = Assert.Single(regionizer.RegionIds.ToArray());
        Assert.Equal(2, regionizer.GetRegion(region).SectionCount);
        Assert.Equal(islands, new IslandDetector(topology).CountIslands(regionizer, region));
    }

    private static int[] Counts(int[] tags)
    {
        var counts = new int[4];
        foreach (var t in tags)
        {
            counts[t]++;
        }

        return counts;
    }

    private static EntitySpec Spec(int id, EntityClass cls, double x, double y, int tag) => new(id, cls, 0, x, y, 0, tag);

    /// <summary>A core regionizer observed by a metrics collector, with entities placed by explicit values.</summary>
    private sealed class Harness
    {
        public Harness(IReadOnlyList<EntitySpec> entities, Action<RunEvent> emit, int targetFightSize = 1200, long splitMinAgeSteps = 0)
        {
            var state = new WorldState(entities);
            var config = new MetricsConfig
            {
                MetricsEveryTicks = 1,
                IslandCheckEveryTicks = 1,
                CrossRegionPairsEveryTicks = 1,
                DeferredRangeMetres = 300,
                ReadOnlyRangeMetres = 1000,
                TargetFightSize = targetFightSize,
                BookkeepingBudgetMicros = 1000,
            };
            Metrics = new MetricsCollector(state, config, emit);
            var options = new RegionizerOptions(16, ClaimAnchoring.Section, 0, new ClaimRadii(Radius, Radius, Radius, Radius, Radius), 1, 0, splitMinAgeSteps, TimerMode.TickCounter);
            Regionizer = new Regionizer(SectionTopology.OpenWorld(16, 1024, 1024), options, Metrics);
            foreach (var e in entities)
            {
                Regionizer.AddEntity(e.Id, e.Class, e.Layer, e.X, e.Y, e.Z, e.FightTag);
            }
        }

        public MetricsCollector Metrics { get; }

        public Regionizer Regionizer { get; }

        public TickRow EndTick(long tick = 0) => Metrics.EndTick(Regionizer, tick, (tick * 2) + 1, 0, 0, 0, 0, record: true);
    }
}
