using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Portals;

/// <summary>
/// Portals opening and closing (FR-005, FR-013, research D2, D3 and D7). The map has a surface layer
/// and an underground layer of 32×32 sections, joined by one portal between section (8, 8) on each.
/// Most tests toggle at step boundaries, when no region ticks, where a toggle applies at once; one
/// toggles while regions tick, where the library queues it until the last tick end.
/// </summary>
public sealed class PortalToggleTests
{
    private const int Surface = 0;
    private const int Underground = 1;
    private const int PortalId = 7;
    private const int PortalColumn = 8;
    private const int Row = 8;
    private const int SurfaceEntity = 1;
    private const int UndergroundEntity = 2;
    private const int FarEntity = 3;

    /// <summary>
    /// US1.5: with the portal closed, an entity beside it on each side never claims across it, even
    /// standing on the portal's own section, and the two stay in separate regions. Once the portal
    /// opens, the claims reach through it and the regions are merged by the next tick end.
    /// </summary>
    [Theory]
    [InlineData(0, 5101)]
    [InlineData(1, 5102)]
    public void ClosedPortalSeparatesRegions_OpeningMergesAtNextTickEnd(int mergeGap, int seed)
    {
        // Given a closed portal with an entity beside it on each side (surface column 7, underground column 9).
        var h = new Kit2Harness(nameof(ClosedPortalSeparatesRegions_OpeningMergesAtNextTickEnd), seed, Kit2Harness.Options(mergeGap: mergeGap), TwoLayers(open: false));
        h.Add(SurfaceEntity, 7, Row, layer: Surface);
        h.Add(UndergroundEntity, 9, Row, layer: Underground);
        h.AssertClean();
        var surfaceRegion = h.RegionOf(SurfaceEntity);
        var undergroundRegion = h.RegionOf(UndergroundEntity);
        h.True(surfaceRegion != undergroundRegion, "the two entities start in separate regions");

        // When ticks run, each entity stepping onto the portal's section on its side and back.
        for (var tick = 0; tick < 6; tick++)
        {
            var onPortal = tick % 2 == 0;
            h.Lockstep(() =>
            {
                h.MoveInTick(SurfaceEntity, onPortal ? PortalColumn : 7, Row, Surface);
                h.MoveInTick(UndergroundEntity, onPortal ? PortalColumn : 9, Row, Underground);
            });

            // Then neither claim includes a section on the far side, and the regions stay separate.
            h.SameSections(h.Footprint(SurfaceEntity), h.ClaimOf(SurfaceEntity), "surface claim with the portal closed (its own layer only)");
            h.SameSections(h.Footprint(UndergroundEntity), h.ClaimOf(UndergroundEntity), "underground claim with the portal closed (its own layer only)");
            h.Equal(surfaceRegion, h.RegionOf(SurfaceEntity), "region of the surface entity");
            h.Equal(undergroundRegion, h.RegionOf(UndergroundEntity), "region of the underground entity");
        }

        // When the portal opens, at a step boundary.
        var mark = h.Events.Log.Count;
        var openedAt = h.R.Now;
        h.R.SetPortalOpen(PortalId, true);
        h.AssertClean();

        // Then the claims reach through it: each covers its footprint on both layers (D1 and D2).
        h.Equal(true, h.R.IsPortalOpen(PortalId), "portal state right after opening at a boundary");
        var s = h.SectionOf(SurfaceEntity);
        var u = h.SectionOf(UndergroundEntity);
        h.SameSections(h.Footprint(Surface, s.X, s.Y).Union(h.Footprint(Underground, s.X, s.Y)), h.ClaimOf(SurfaceEntity), "surface claim with the portal open");
        h.SameSections(h.Footprint(Underground, u.X, u.Y).Union(h.Footprint(Surface, u.X, u.Y)), h.ClaimOf(UndergroundEntity), "underground claim with the portal open");

        // ... and, opened at a boundary, it takes effect at once (FR-013): the regions are one at that same step.
        h.Equal(h.RegionOf(SurfaceEntity), h.RegionOf(UndergroundEntity), "one region at the step the portal opened");
        h.Equal(Math.Min(surfaceRegion, undergroundRegion), h.RegionOf(SurfaceEntity), "the lower id absorbed");
        var merge = h.Events.MergesBetween(surfaceRegion, undergroundRegion).Single();
        h.True(h.Events.Log.IndexOf(merge) >= mark, "the merge followed the opening");
        h.Equal(openedAt, merge.Step, "step of the merge (the opening's own step)");
        h.Equal(openedAt, h.R.Now, "no step has passed since the opening");

        // ... so by the next tick end they are still one region, merged exactly once.
        h.Lockstep();
        h.Equal(h.RegionOf(SurfaceEntity), h.RegionOf(UndergroundEntity), "one region after the next tick end");
        h.Equal(Math.Min(surfaceRegion, undergroundRegion), h.RegionOf(SurfaceEntity), "the surviving region after the next tick end");
        h.Equal(1, h.Events.MergesBetween(surfaceRegion, undergroundRegion).Count(), "merges between the two regions by the next tick end");
    }

    /// <summary>The cases of <see cref="PortalToggledDuringTickTakesEffectAtTickEnd"/>.</summary>
    public static TheoryData<bool, int, bool, int> ToggleDuringTickCases()
    {
        var data = new TheoryData<bool, int, bool, int>();
        var seed = 5300;
        foreach (var open in new[] { true, false })
        {
            foreach (var mergeGap in new[] { 0, 1 })
            {
                foreach (var lowerEndsFirst in new[] { true, false })
                {
                    data.Add(open, mergeGap, lowerEndsFirst, ++seed);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// FR-013 and research D7: a toggle made while regions tick is queued. While any region ticks,
    /// <see cref="Regionizer.IsPortalOpen"/> and every claim through the portal stay as they were, even
    /// after the first of the two ticking regions ends its tick. When the last one's tick ends, the
    /// toggle applies: opening lets the claims reach through and merges the two regions; closing
    /// pulls each claim back to its own layer.
    /// </summary>
    /// <param name="open">True to open a closed portal, false to close an open one.</param>
    /// <param name="mergeGap">The merge gap.</param>
    /// <param name="lowerEndsFirst">Whether the lower-id ticking region ends its tick first.</param>
    /// <param name="seed">Seed of the harness spots.</param>
    [Theory]
    [MemberData(nameof(ToggleDuringTickCases))]
    public void PortalToggledDuringTickTakesEffectAtTickEnd(bool open, int mergeGap, bool lowerEndsFirst, int seed)
    {
        // Given an entity beside the portal on each side, and two regions ready to tick: the two
        // entities' regions when the portal starts closed; their joined region and a far one when open.
        var h = new Kit2Harness(nameof(PortalToggledDuringTickTakesEffectAtTickEnd), seed, Kit2Harness.Options(mergeGap: mergeGap), TwoLayers(open: !open));
        h.Add(SurfaceEntity, 7, Row, layer: Surface);
        h.Add(UndergroundEntity, 9, Row, layer: Underground);
        if (!open)
        {
            h.Add(FarEntity, 24, 24, layer: Surface);
        }

        h.AssertClean();
        var ticking = h.R.RegionIds.ToArray();
        h.Equal(2, ticking.Length, "regions that will tick during the toggle");
        h.Equal(open, h.RegionOf(SurfaceEntity) != h.RegionOf(UndergroundEntity), "the two entities are in separate regions before the toggle");
        var surfaceRegion = h.RegionOf(SurfaceEntity);
        var undergroundRegion = h.RegionOf(UndergroundEntity);
        var surfaceClaim = h.ClaimOf(SurfaceEntity);
        var undergroundClaim = h.ClaimOf(UndergroundEntity);
        var s = h.SectionOf(SurfaceEntity);
        var u = h.SectionOf(UndergroundEntity);
        var surfaceThrough = h.Footprint(Surface, s.X, s.Y).Union(h.Footprint(Underground, s.X, s.Y)).ToArray();
        var undergroundThrough = h.Footprint(Underground, u.X, u.Y).Union(h.Footprint(Surface, u.X, u.Y)).ToArray();
        h.SameSections(open ? h.Footprint(SurfaceEntity) : surfaceThrough, surfaceClaim, "surface claim before the toggle");
        h.SameSections(open ? h.Footprint(UndergroundEntity) : undergroundThrough, undergroundClaim, "underground claim before the toggle");

        void AssertNotYetApplied(string when)
        {
            h.Equal(!open, h.R.IsPortalOpen(PortalId), $"portal state {when}");
            h.Equal(string.Join(",", surfaceClaim), string.Join(",", h.ClaimOf(SurfaceEntity)), $"surface claim {when}");
            h.Equal(string.Join(",", undergroundClaim), string.Join(",", h.ClaimOf(UndergroundEntity)), $"underground claim {when}");
            h.Equal(surfaceRegion, h.RegionOf(SurfaceEntity), $"region of the surface entity {when}");
            h.Equal(undergroundRegion, h.RegionOf(UndergroundEntity), $"region of the underground entity {when}");
        }

        // When the portal is toggled while both regions tick.
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        foreach (var id in ticking)
        {
            h.Start(id);
        }

        var mark = h.Events.Log.Count;
        h.R.SetPortalOpen(PortalId, open);

        // Then nothing changes while a region ticks: not at the toggle ...
        AssertNotYetApplied("right after the toggle, both regions ticking");
        h.Equal(0, h.Events.Log.Count - mark, "events raised by the toggle while regions tick");

        // ... and not when the first of the two ends its tick.
        var (first, last) = lowerEndsFirst ? (ticking[0], ticking[1]) : (ticking[1], ticking[0]);
        h.Equal(RegionState.Ready, h.End(first).State, $"tick-end state of region {first}");
        AssertNotYetApplied($"after region {first}'s tick end, region {last} still ticking");
        h.Equal(0, h.Events.Of<Kit2Merged>(mark).Count(), "merges before the last tick end");

        // When the last ticking region's tick ends, the toggle applies.
        h.End(last);
        h.Equal(open, h.R.IsPortalOpen(PortalId), "portal state after the last tick end");
        if (open)
        {
            h.SameSections(surfaceThrough, h.ClaimOf(SurfaceEntity), "surface claim once the opening applies");
            h.SameSections(undergroundThrough, h.ClaimOf(UndergroundEntity), "underground claim once the opening applies");
            var merge = h.Events.Of<Kit2Merged>(mark).Single();
            h.Equal(Math.Min(surfaceRegion, undergroundRegion), merge.Absorber, "absorber of the merge the opening caused");
            h.Equal(Math.Max(surfaceRegion, undergroundRegion), merge.Absorbed, "region absorbed by the merge the opening caused");
            h.Equal(h.R.Now, merge.Step, "step of the merge");
            h.Equal(merge.Absorber, h.RegionOf(SurfaceEntity), "region of the surface entity after the opening");
            h.Equal(merge.Absorber, h.RegionOf(UndergroundEntity), "region of the underground entity after the opening");
        }
        else
        {
            h.SameSections(h.Footprint(SurfaceEntity), h.ClaimOf(SurfaceEntity), "surface claim once the closing applies (its own layer only)");
            h.SameSections(h.Footprint(UndergroundEntity), h.ClaimOf(UndergroundEntity), "underground claim once the closing applies (its own layer only)");
            h.Equal(0, h.Events.Of<Kit2Merged>(mark).Count(), "merges caused by the closing");
            h.Equal(surfaceRegion, h.RegionOf(UndergroundEntity), "the joined region keeps both entities until a split check allows a split");
        }

        h.AssertClean();
    }

    /// <summary>
    /// FR-005 and FR-013, research D3: radius-0 entities stand on the two endpoints of a closed portal,
    /// each claiming only its own section, in two regions. Opening the portal at a boundary leaves both
    /// claims as they are; with a merge gap of 1 the open portal makes the two owned endpoints
    /// adjacent, so their regions merge at once, caused by the lowest-id claimant. With a gap of 0 they
    /// stay two regions.
    /// </summary>
    [Theory]
    [InlineData(1, ClaimAnchoring.Section, 5401)]
    [InlineData(1, ClaimAnchoring.Position, 5402)]
    [InlineData(0, ClaimAnchoring.Section, 5403)]
    [InlineData(0, ClaimAnchoring.Position, 5404)]
    public void OpeningPortalBetweenOwnedEndpointsMergesOnlyWithGap1(int mergeGap, ClaimAnchoring anchoring, int seed)
    {
        // Given radius-0 entities on the two endpoints of a closed portal, in different regions.
        var h = new Kit2Harness(nameof(OpeningPortalBetweenOwnedEndpointsMergesOnlyWithGap1), seed, Kit2Harness.Options(anchoring, mergeGap: mergeGap, radius: 0), TwoLayers(open: false));
        h.Add(SurfaceEntity, PortalColumn, Row, layer: Surface);
        h.Add(UndergroundEntity, PortalColumn, Row, layer: Underground);
        h.Lockstep();
        var a = h.Index(Surface, PortalColumn, Row);
        var b = h.Index(Underground, PortalColumn, Row);
        var surfaceRegion = h.RegionOf(SurfaceEntity);
        var undergroundRegion = h.RegionOf(UndergroundEntity);
        h.True(surfaceRegion != undergroundRegion, "the two endpoint entities start in separate regions");
        h.Equal(surfaceRegion, h.R.OwnerOf(a), "owner of the surface endpoint");
        h.Equal(undergroundRegion, h.R.OwnerOf(b), "owner of the underground endpoint");

        // When the portal opens at a step boundary.
        var mark = h.Events.Log.Count;
        var openedAt = h.R.Now;
        h.R.SetPortalOpen(PortalId, true);

        // Then the claims are unchanged (radius 0 claims only the own section) ...
        h.Equal(true, h.R.IsPortalOpen(PortalId), "portal state after opening");
        h.SameSections([a], h.ClaimOf(SurfaceEntity), "surface claim with the portal open");
        h.SameSections([b], h.ClaimOf(UndergroundEntity), "underground claim with the portal open");
        h.AssertClean();

        // ... and only a merge gap of 1 merges the endpoints' owners, at once.
        var merges = h.Events.Of<Kit2Merged>(mark).ToList();
        if (mergeGap == 1)
        {
            var survivor = Math.Min(surfaceRegion, undergroundRegion);
            h.Equal(1, h.R.RegionIds.Length, "live regions after opening with gap 1");
            h.Equal(survivor, h.RegionOf(SurfaceEntity), "region of the surface entity");
            h.Equal(survivor, h.RegionOf(UndergroundEntity), "region of the underground entity");
            h.Equal(survivor, h.R.OwnerOf(b), "owner of the underground endpoint after the merge");
            h.Equal(1, merges.Count, "merges after opening with gap 1");
            h.Equal(survivor, merges[0].Absorber, "absorber");
            h.Equal(Math.Max(surfaceRegion, undergroundRegion), merges[0].Absorbed, "absorbed region");
            h.Equal(SurfaceEntity, merges[0].CauseEntity, "cause: the lowest-id entity claiming an endpoint");
            h.Equal(EntityClass.Infantry, merges[0].Cause, "cause class");
            h.Equal(openedAt, merges[0].Step, "step of the merge (the opening's own step)");
        }
        else
        {
            h.Equal(2, h.R.RegionIds.Length, "live regions after opening with gap 0");
            h.Equal(surfaceRegion, h.RegionOf(SurfaceEntity), "region of the surface entity");
            h.Equal(undergroundRegion, h.RegionOf(UndergroundEntity), "region of the underground entity");
            h.Equal(0, merges.Count, "merges after opening with gap 0");
        }

        // The next tick end changes nothing more.
        h.Lockstep();
        h.Equal(mergeGap == 1 ? 1 : 2, h.R.RegionIds.Length, "live regions after the next tick end");
    }

    /// <summary>
    /// Setting a portal to the state it is already in is not a toggle: at a boundary, or queued during
    /// a tick, it raises no event and changes no claim.
    /// </summary>
    [Theory]
    [InlineData(true, false, 5501)]
    [InlineData(false, false, 5502)]
    [InlineData(true, true, 5503)]
    [InlineData(false, true, 5504)]
    public void SettingAPortalToItsCurrentStateChangesNothing(bool open, bool duringTick, int seed)
    {
        var h = new Kit2Harness(nameof(SettingAPortalToItsCurrentStateChangesNothing), seed, Kit2Harness.Options(), TwoLayers(open));
        h.Add(SurfaceEntity, 7, Row, layer: Surface);
        h.Add(UndergroundEntity, 9, Row, layer: Underground);
        h.AssertClean();
        var surfaceClaim = string.Join(",", h.ClaimOf(SurfaceEntity));
        var undergroundClaim = string.Join(",", h.ClaimOf(UndergroundEntity));
        var regions = string.Join(",", h.R.RegionIds.ToArray());
        h.AdvanceTo(Kit2Harness.LockstepPeriod);
        var mark = h.Events.Log.Count;

        if (duringTick)
        {
            var ids = h.R.RegionIds.ToArray();
            foreach (var id in ids)
            {
                h.Start(id);
            }

            mark = h.Events.Log.Count;
            h.R.SetPortalOpen(PortalId, open);
            foreach (var id in ids)
            {
                h.End(id);
            }

            h.Equal(0, h.Events.Log.Skip(mark).Count(e => e is not Kit2StateChanged), "events other than the tick ends' state changes");
        }
        else
        {
            h.R.SetPortalOpen(PortalId, open);
            h.Equal(0, h.Events.Log.Count - mark, "events raised by setting the current state");
        }

        h.Equal(open, h.R.IsPortalOpen(PortalId), "portal state");
        h.Equal(surfaceClaim, string.Join(",", h.ClaimOf(SurfaceEntity)), "surface claim");
        h.Equal(undergroundClaim, string.Join(",", h.ClaimOf(UndergroundEntity)), "underground claim");
        h.Equal(regions, string.Join(",", h.R.RegionIds.ToArray()), "live regions");
        h.AssertClean();
    }

    /// <summary>
    /// Closing a portal stops claims crossing it, which leaves the region joined through it with two
    /// islands; it splits at the first tick end where damping holds (here, past T_min). With the
    /// portal left open, the same region never splits.
    /// </summary>
    [Theory]
    [InlineData(true, 0, 5201)]
    [InlineData(true, 1, 5202)]
    [InlineData(false, 0, 5203)]
    [InlineData(false, 1, 5204)]
    public void ClosingAPortalAllowsASplitOnceDampingHolds(bool close, int mergeGap, int seed)
    {
        const long minAge = 15;
        var h = new Kit2Harness(
            nameof(ClosingAPortalAllowsASplitOnceDampingHolds),
            seed,
            Kit2Harness.Options(mergeGap: mergeGap, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: minAge),
            TwoLayers(open: true));
        h.Add(SurfaceEntity, 7, Row, layer: Surface);
        h.Add(UndergroundEntity, 9, Row, layer: Underground);
        h.AssertClean();
        var region = h.RegionOf(SurfaceEntity);
        h.Equal(region, h.RegionOf(UndergroundEntity), "the open portal joins the two claims into one region");
        var createdAt = h.R.GetRegion(region).CreatedAt;
        h.Lockstep();
        h.Lockstep();

        if (close)
        {
            h.R.SetPortalOpen(PortalId, false);
            h.AssertClean();
            h.SameSections(h.Footprint(SurfaceEntity), h.ClaimOf(SurfaceEntity), "surface claim after closing");
            h.SameSections(h.Footprint(UndergroundEntity), h.ClaimOf(UndergroundEntity), "underground claim after closing");
        }

        // While the region is younger than T_min, damping does not hold and it stays whole.
        while (h.R.Now + Kit2Harness.LockstepPeriod - createdAt < minAge)
        {
            h.Equal(0, h.Lockstep()[region].SplitChildren, $"split children at age {h.R.Now - createdAt}");
            h.Equal(1, h.R.RegionIds.Length, "live regions before damping holds");
        }

        var result = h.Lockstep()[region];

        h.Equal(close ? 2 : 0, result.SplitChildren, $"split children at age {h.R.Now - createdAt}, portal {(close ? "closed" : "open")}");
        h.Equal(close, h.RegionOf(SurfaceEntity) != h.RegionOf(UndergroundEntity), "the two entities are in separate regions");
        if (close)
        {
            h.SameSections(h.Footprint(SurfaceEntity), h.SectionsOf(h.RegionOf(SurfaceEntity)), "sections of the surface region");
            h.SameSections(h.Footprint(UndergroundEntity), h.SectionsOf(h.RegionOf(UndergroundEntity)), "sections of the underground region");
        }
    }

    private static SectionTopology TwoLayers(bool open)
    {
        const int n = Kit2Harness.MapSections;
        return SectionTopology.Build(
            Kit2Harness.SectionSize,
            [new LayerSpec(Surface, 0, 0, n, n, new ushort[n * n]), new LayerSpec(Underground, 0, 0, n, n, new ushort[n * n])],
            [new PortalSpec(PortalId, new SectionKey(Surface, PortalColumn, Row), new SectionKey(Underground, PortalColumn, Row), open)]);
    }
}
