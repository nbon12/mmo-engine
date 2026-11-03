using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Regions;

/// <summary>
/// The region state machine (FR-011, research D5), exercised over every pair of states through the
/// guarded transition (the internal test hook). Each from-state is reached the way the library
/// reaches it where it can: ready after a claim, ticking after a tick start, transient by owing a
/// merge to a ticking region. Dead is reached through the hook's legal ready-to-dead move.
/// </summary>
public sealed class RegionStateTests
{
    private const int Row = 8;

    /// <summary>FR-011's legal moves, as the spec lists them.</summary>
    private static readonly HashSet<(RegionState From, RegionState To)> LegalMoves =
    [
        (RegionState.Transient, RegionState.Ready),
        (RegionState.Ready, RegionState.Ticking),
        (RegionState.Ready, RegionState.Transient),
        (RegionState.Ticking, RegionState.Ready),
        (RegionState.Ticking, RegionState.Transient),
        (RegionState.Transient, RegionState.Dead),
        (RegionState.Ready, RegionState.Dead),
    ];

    /// <summary>Every pair of different states that FR-011 does not list.</summary>
    public static TheoryData<RegionState, RegionState> IllegalPairs()
    {
        var data = new TheoryData<RegionState, RegionState>();
        foreach (var (from, to) in AllPairs().Where(p => p.From != p.To && !LegalMoves.Contains(p)))
        {
            data.Add(from, to);
        }

        return data;
    }

    /// <summary>Every move FR-011 lists.</summary>
    public static TheoryData<RegionState, RegionState> LegalPairs()
    {
        var data = new TheoryData<RegionState, RegionState>();
        foreach (var (from, to) in AllPairs().Where(LegalMoves.Contains))
        {
            data.Add(from, to);
        }

        return data;
    }

    /// <summary>The pairs whose two states are the same.</summary>
    public static TheoryData<RegionState> SameStatePairs()
    {
        var data = new TheoryData<RegionState>();
        foreach (var (from, _) in AllPairs().Where(p => p.From == p.To))
        {
            data.Add(from);
        }

        return data;
    }

    /// <summary>
    /// US1.6: an illegal move (ticking straight to dead among them) is refused, the state stays as it
    /// was, no state change is observed, and the invariant checker reports rule LegalState. A region
    /// whose illegal move was refused still finishes its tick cleanly.
    /// </summary>
    [Theory]
    [MemberData(nameof(IllegalPairs))]
    public void IllegalTransitionIsRejectedAndStateUnchanged(RegionState from, RegionState to)
    {
        var seed = 6100 + ((int)from * 4) + (int)to;
        var h = new Kit2Harness(nameof(IllegalTransitionIsRejectedAndStateUnchanged), seed, Kit2Harness.Options());
        var (region, openTick) = Arrange(h, from);
        _ = h.Check();
        var changesBefore = h.Events.Of<Kit2StateChanged>().Count();

        var moved = h.R.TestHookTransition(region, to);

        h.Equal(false, moved, $"result of the move {from} -> {to}");
        h.Equal(from, h.R.GetRegion(region).State, $"state after the refused move {from} -> {to}");
        h.Equal(changesBefore, h.Events.Of<Kit2StateChanged>().Count(), "state changes observed");
        var violations = h.Check();
        h.True(
            violations.Any(v => v.Rule == InvariantRule.LegalState && v.Detail.Contains($"region {region}: illegal move {from} -> {to}", StringComparison.Ordinal)),
            $"the checker reports LegalState for the refused move {from} -> {to}; it reported: {string.Join("; ", violations)}");

        if (openTick != 0)
        {
            // The open tick ends normally: ticking returns to ready; an owing transient region is absorbed.
            h.Equal(RegionState.Ready, h.End(openTick).State, "state after the open tick ends");
            h.AssertClean();
        }
    }

    /// <summary>Every move FR-011 lists succeeds, is observed once, and records no LegalState breach.</summary>
    [Theory]
    [MemberData(nameof(LegalPairs))]
    public void LegalTransitionSucceeds(RegionState from, RegionState to)
    {
        var seed = 6200 + ((int)from * 4) + (int)to;
        var h = new Kit2Harness(nameof(LegalTransitionSucceeds), seed, Kit2Harness.Options());
        var (region, _) = Arrange(h, from);
        _ = h.Check();

        var moved = h.R.TestHookTransition(region, to);

        h.Equal(true, moved, $"result of the move {from} -> {to}");
        h.Equal(to, h.R.GetRegion(region).State, $"state after the move {from} -> {to}");
        h.Equal(1, h.Events.Of<Kit2StateChanged>().Count(c => c.Region == region && c.From == from && c.To == to), "state changes observed for the move");
        var refused = h.Check().Where(v => v.Detail.Contains("illegal move", StringComparison.Ordinal)).ToList();
        h.Equal(0, refused.Count, $"refused-move breaches recorded ({string.Join("; ", refused)})");
    }

    /// <summary>
    /// A pair of equal states is not a move (FR-011 lists only changes of state): the state stays,
    /// nothing is observed and no LegalState breach is recorded.
    /// </summary>
    [Theory]
    [MemberData(nameof(SameStatePairs))]
    public void SameStatePairChangesNothing(RegionState state)
    {
        var seed = 6300 + (int)state;
        var h = new Kit2Harness(nameof(SameStatePairChangesNothing), seed, Kit2Harness.Options());
        var (region, _) = Arrange(h, state);
        _ = h.Check();
        var changesBefore = h.Events.Of<Kit2StateChanged>().Count();

        _ = h.R.TestHookTransition(region, state);

        h.Equal(state, h.R.GetRegion(region).State, $"state after {state} -> {state}");
        h.Equal(changesBefore, h.Events.Of<Kit2StateChanged>().Count(), "state changes observed");
        var refused = h.Check().Where(v => v.Detail.Contains("illegal move", StringComparison.Ordinal)).ToList();
        h.Equal(0, refused.Count, $"refused-move breaches recorded ({string.Join("; ", refused)})");
    }

    private static IEnumerable<(RegionState From, RegionState To)> AllPairs() =>
        from f in Enum.GetValues<RegionState>()
        from t in Enum.GetValues<RegionState>()
        select (f, t);

    /// <summary>
    /// Puts a region into <paramref name="state"/>. Returns it, and the region whose tick is left open
    /// (0 when none): the region itself when ticking, its ticking partner when transient.
    /// </summary>
    private static (long Region, long OpenTick) Arrange(Kit2Harness h, RegionState state)
    {
        h.Add(1, 4, Row);
        var region = h.RegionOf(1);
        h.AssertClean();
        switch (state)
        {
            case RegionState.Ready:
                return (region, 0);
            case RegionState.Ticking:
                h.AdvanceTo(Kit2Harness.LockstepPeriod);
                h.Start(region);
                return (region, region);
            case RegionState.Transient:
                // A new entity's claim (columns 5–7) lands on the ticking region's column 5, so its own
                // new region owes the ticking one a merge and waits as transient.
                h.AdvanceTo(Kit2Harness.LockstepPeriod);
                h.Start(region);
                h.Add(2, 6, Row);
                var transient = h.RegionOf(2);
                h.Equal(RegionState.Transient, h.StateOf(transient), "state of the region owing a ticking region");
                return (transient, region);
            case RegionState.Dead:
                h.True(h.R.TestHookTransition(region, RegionState.Dead), "the legal move ready -> dead");
                return (region, 0);
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }
}
