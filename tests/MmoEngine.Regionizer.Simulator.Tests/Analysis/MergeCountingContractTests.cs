using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Scenarios;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// What the memo's merge-rate rows rely on from the run output (FR-024, research D16, contracts/run-output.md):
/// merges count only joins where both sides held an entity, and absorptions are reported separately. These
/// test code outside the analysis folder; each failure is marked OUTSIDE-BUG and stays in the suite until fixed.
/// </summary>
public sealed class MergeCountingContractTests
{
    private const double Radius = 16;

    // OUTSIDE-BUG: src/MmoEngine.Regionizer.Simulator/Output/RunFolder.cs TicksHeader lacks the `absorptions`
    // column (after `merges`) and the `fusedFightPairs` column (after `fusions`) that contracts/run-output.md lists.
    [Fact]
    public void TicksHeaderHasTheContractColumns()
    {
        const string Contract =
            "tick,timeSteps,regionCount,largestRegionPlayers,largestRegionShare,merges,absorptions,splits,fusions,fusedFightPairs,"
            + "sectionChurn,crossPairsDeferred,crossPairsReadOnly,bookkeepingMicros,serializedMicros,oversizedFusion,violations,stateHash";

        Assert.Equal(Contract, RunFolder.TicksHeader);
    }

    // OUTSIDE-BUG: src/MmoEngine.Regionizer.Simulator/Metrics/MetricsCollector.cs OnMerged counts a merge whenever
    // neither side was created as a transient helper, but FR-024 counts a merge only when both sides hold at least one
    // entity. Here the absorbed region has lost its only entity (a two-step teleport) and still owns its now-dead
    // sections; another entity's claim lands on them, so the core absorbs it. That is an absorption, not a merge.
    [Fact]
    public void AbsorbingARegionWithNoEntityIsNotAMerge()
    {
        EntitySpec[] entities =
        [
            new(0, EntityClass.Infantry, 0, 100, 100, 0, 0),
            new(1, EntityClass.Infantry, 0, 300, 100, 0, 1),
        ];
        var metrics = new MetricsCollector(new WorldState(entities), Config(), _ => { });
        var options = new RegionizerOptions(16, ClaimAnchoring.Section, 0, new ClaimRadii(Radius, Radius, Radius, Radius, Radius), 1, 0, 0, TimerMode.TickCounter);
        var regionizer = new Regionizer(SectionTopology.OpenWorld(16, 1024, 1024), options, metrics);
        foreach (var e in entities)
        {
            regionizer.AddEntity(e.Id, e.Class, e.Layer, e.X, e.Y, e.Z, e.FightTag);
        }

        var emptied = regionizer.GetEntity(1).RegionId;
        regionizer.BeginTeleport(1, 0, 900, 900);
        Assert.True(regionizer.GetRegion(emptied).SectionCount > 0, "the emptied region keeps its dead sections until its tick end");
        Assert.Empty(regionizer.GetRegion(emptied).Entities.ToArray());

        regionizer.MoveEntity(0, 0, 290, 100);

        Assert.Equal(1, regionizer.Stats.Merges);
        Assert.False(regionizer.TryGetState(emptied, out _), "the core absorbed the emptied region");
        Assert.Equal(0, metrics.Merges);
        Assert.Equal(0, metrics.MergesCausedBy(EntityClass.Infantry));
    }

    private static MetricsConfig Config() => new()
    {
        MetricsEveryTicks = 1,
        IslandCheckEveryTicks = 1,
        CrossRegionPairsEveryTicks = 1,
        DeferredRangeMetres = 300,
        ReadOnlyRangeMetres = 1000,
        TargetFightSize = 1200,
        BookkeepingBudgetMicros = 1000,
    };
}
