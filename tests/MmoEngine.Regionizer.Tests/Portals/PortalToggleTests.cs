using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Portals;

/// <summary>
/// Portals opening and closing (FR-013, research D2 and D7). The map has a surface layer and an
/// underground layer of 32×32 sections, joined by one portal between section (8, 8) on each. The
/// harness applies toggles at step boundaries, when no region ticks, as D7 requires of callers.
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
        var s = h.SectionOf(SurfaceEntity);
        var u = h.SectionOf(UndergroundEntity);
        h.SameSections(h.Footprint(Surface, s.X, s.Y).Union(h.Footprint(Underground, s.X, s.Y)), h.ClaimOf(SurfaceEntity), "surface claim with the portal open");
        h.SameSections(h.Footprint(Underground, u.X, u.Y).Union(h.Footprint(Surface, u.X, u.Y)), h.ClaimOf(UndergroundEntity), "underground claim with the portal open");

        // ... and the two regions are one by the next tick end, merged no earlier than the opening.
        h.Lockstep();
        h.Equal(h.RegionOf(SurfaceEntity), h.RegionOf(UndergroundEntity), "one region after the next tick end");
        h.Equal(Math.Min(surfaceRegion, undergroundRegion), h.RegionOf(SurfaceEntity), "the lower id absorbed");
        var merge = h.Events.MergesBetween(surfaceRegion, undergroundRegion).Single();
        h.True(h.Events.Log.IndexOf(merge) >= mark, "the merge followed the opening");
        h.True(merge.Step >= openedAt && merge.Step <= h.R.Now, $"the merge (step {merge.Step}) lies between the opening and the next tick end");
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
