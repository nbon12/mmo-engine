using MmoEngine.Regionizer.Simulator.Analysis;
using Xunit;

namespace MmoEngine.Regionizer.Simulator.Tests.Analysis;

/// <summary>
/// How the agreement checks classify what they observe (FR-031). A correct core never produces a
/// violation, so these feed the tallies observations directly to show each violation is counted and the
/// first one is reported with its seed.
/// </summary>
public sealed class CheckTalliesTests
{
    private const ulong Seed = 7;
    private const double LowerBound = 40;
    private const double MergeDistance = 107.9;

    [Theory]
    [InlineData(10.0, false, 1, 0)]
    [InlineData(10.0, true, 0, 0)]
    [InlineData(80.0, false, 0, 0)]
    [InlineData(80.0, true, 0, 0)]
    [InlineData(200.0, false, 0, 0)]
    [InlineData(200.0, true, 0, 1)]
    public void MergeDistanceTallyCountsOnlyBreachesOfTheBounds(double distance, bool shared, int lowerViolations, int upperViolations)
    {
        var log = new FailureLog(Seed);
        var tally = new MergeDistanceTally(LowerBound, MergeDistance, log);

        tally.Observe(trial: 3, step: 5, 0, 0, distance, 0, shared);

        Assert.Equal(1, tally.Observations);
        Assert.Equal(distance <= LowerBound ? 1 : 0, tally.WithinLowerBound);
        Assert.Equal(distance > MergeDistance ? 1 : 0, tally.BeyondMergeDistance);
        Assert.Equal(lowerViolations, tally.LowerBoundViolations);
        Assert.Equal(upperViolations, tally.UpperBoundViolations);
        Assert.Equal(shared ? distance : null, tally.FarthestShared);
        Assert.Equal(shared ? null : distance, tally.ClosestSeparated);
        if (lowerViolations + upperViolations == 0)
        {
            Assert.Null(log.First);
        }
        else
        {
            Assert.StartsWith($"seed {Seed}, trial 3, step 5: ", log.First, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FloorTallyFlagsAnInteractionWithATargetThatWasInAnotherRegionAtThePreviousTickEnd()
    {
        var log = new FailureLog(Seed);
        var tally = new FloorTally(interactionRange: 8, log);

        tally.Observe(0, 0, 0, 0, 30, 0, shared: false);
        tally.Observe(0, 1, 0, 0, 22, 0, shared: false);
        tally.Observe(0, 2, 0, 0, 14, 0, shared: false);
        tally.Observe(0, 3, 0, 0, 6, 0, shared: true);

        Assert.Equal(1, tally.InteractionTicks);
        Assert.Equal(1, tally.InteractionsWhileSeparated);
        Assert.Equal(8, tally.MaxClosingPerTick);
        Assert.StartsWith($"seed {Seed}, trial 0, step 3: ", log.First, StringComparison.Ordinal);
    }

    // A pair placed already within range has no earlier tick end, so it must share a region at its first one.
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void FloorTallyChecksAPairPlacedInRangeAtItsFirstTickEnd(bool shared, int violations)
    {
        var log = new FailureLog(Seed);
        var tally = new FloorTally(interactionRange: 8, log);

        tally.Observe(0, 0, 0, 0, 5, 0, shared);

        Assert.Equal(1, tally.InteractionTicks);
        Assert.Equal(violations, tally.InteractionsWhileSeparated);
        Assert.Equal(0, tally.MaxClosingPerTick);
    }

    [Fact]
    public void FailureLogCountsEveryInvariantViolationAndKeepsTheFirst()
    {
        var log = new FailureLog(Seed);

        log.Invariants(1, 2, []);
        log.Invariants(1, 3, [new Violation(InvariantRule.SectionSingleOwner, "first"), new Violation(InvariantRule.ClaimsInRegion, "second")]);
        log.Invariants(1, 4, [new Violation(InvariantRule.LegalState, "later")]);

        Assert.Equal(3, log.InvariantViolations);
        Assert.Equal($"seed {Seed}, trial 1, step 3: invariant SectionSingleOwner broken: first", log.First);
    }
}
