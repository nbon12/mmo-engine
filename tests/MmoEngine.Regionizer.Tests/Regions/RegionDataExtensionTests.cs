using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Regions;

/// <summary>
/// The region data extension point (constitution: every new kind of region data defines how it
/// merges and splits; contracts/regionizer-api.md). A recording extension sees one merge-then-split
/// story, driven in lockstep with the invariant checker at every tick boundary: three entities on
/// row 8 (columns 4, 10 and 16) start in three regions; the outer two walk into column 10, so all
/// three regions merge; then they walk back, and the merged region splits into three islands.
/// </summary>
public sealed class RegionDataExtensionTests
{
    private const int Row = 8;
    private const int West = 1;
    private const int Middle = 2;
    private const int East = 3;

    /// <summary>Both anchoring modes and both merge gaps, each with its own seed.</summary>
    public static TheoryData<ClaimAnchoring, int, int> Cases() => new()
    {
        { ClaimAnchoring.Section, 0, 8101 },
        { ClaimAnchoring.Section, 1, 8102 },
        { ClaimAnchoring.Position, 0, 8103 },
        { ClaimAnchoring.Position, 1, 8104 },
    };

    /// <summary>
    /// <see cref="IRegionDataExtension.OnMerge"/> fires exactly once per merge, matching the
    /// observer's merges one for one and in order, and the lower id always absorbs.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void OnMergeFiresOncePerMergeWithTheLowerIdAbsorbing(ClaimAnchoring anchoring, int mergeGap, int seed)
    {
        var (h, extension, regions) = MergeThenSplit(nameof(OnMergeFiresOncePerMergeWithTheLowerIdAbsorbing), anchoring, mergeGap, seed);
        var (west, middle, east) = regions;

        h.Equal($"{west}<-{middle}, {west}<-{east}", string.Join(", ", extension.Merges.Select(m => $"{m.Absorber}<-{m.Absorbed}")), "merges the extension saw");
        h.Equal(
            string.Join(", ", h.Events.Of<Kit2Merged>().Select(m => $"{m.Absorber}<-{m.Absorbed}")),
            string.Join(", ", extension.Merges.Select(m => $"{m.Absorber}<-{m.Absorbed}")),
            "the extension's merges against the observer's");
        h.Equal(2L, h.R.Stats.Merges, "merges counted by the regionizer");
        h.True(extension.Merges.All(m => m.Absorber < m.Absorbed), "the lower id absorbs in every merge");
    }

    /// <summary>
    /// <see cref="IRegionDataExtension.OnSplit"/> fires exactly once per split with the parent's id
    /// among the child ids, matching the observer's split, and the children are the live regions.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void OnSplitFiresOncePerSplitWithTheParentAmongItsChildren(ClaimAnchoring anchoring, int mergeGap, int seed)
    {
        var (h, extension, regions) = MergeThenSplit(nameof(OnSplitFiresOncePerSplitWithTheParentAmongItsChildren), anchoring, mergeGap, seed);
        var parent = regions.West;

        h.Equal(1, extension.Splits.Count, "splits the extension saw");
        h.Equal(1L, h.R.Stats.Splits, "splits counted by the regionizer");
        var split = extension.Splits[0];
        var observed = h.Events.Of<Kit2Split>().Single();
        h.Equal(parent, split.Parent, "parent of the split");
        h.Equal(observed.Parent, split.Parent, "parent the observer saw");
        h.Equal(string.Join(",", observed.Children), string.Join(",", split.Children), "child ids against the observer's");
        h.Equal(3, split.Children.Length, "child ids, one per island");
        h.Equal(parent, split.Children[0], "the parent's id is among the child ids (first)");
        h.Equal(string.Join(",", h.R.RegionIds.ToArray()), string.Join(",", split.Children.Order()), "the child ids are the live regions");
    }

    /// <summary>
    /// The <c>ownerOfEntity</c> function handed to <see cref="IRegionDataExtension.OnSplit"/> maps each
    /// entity to the child that owns its section: the west island keeps the parent (a tie on size,
    /// broken by the lowest section), the middle and east islands get the two new children in order.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void OwnerOfEntityMapsEachEntityToTheChildOwningItsSection(ClaimAnchoring anchoring, int mergeGap, int seed)
    {
        var (h, extension, regions) = MergeThenSplit(nameof(OwnerOfEntityMapsEachEntityToTheChildOwningItsSection), anchoring, mergeGap, seed);
        var split = extension.Splits.Single();
        var children = split.Children;

        var expected = new Dictionary<int, long> { [West] = regions.West, [Middle] = children[1], [East] = children[2] };
        foreach (var (entity, owner) in expected)
        {
            var key = h.SectionOf(entity);
            h.Equal(owner, split.OwnerOfEntity[entity], $"ownerOfEntity({entity})");
            h.Equal(h.R.OwnerOf(h.Index(key.Layer, key.X, key.Y)), split.OwnerOfEntity[entity], $"ownerOfEntity({entity}) against the owner of its section");
            h.Equal(h.RegionOf(entity), split.OwnerOfEntity[entity], $"ownerOfEntity({entity}) against the entity's region");
        }

        h.Equal(3, split.OwnerOfEntity.Values.Distinct().Count(), "each entity maps to its own child");
    }

    /// <summary>
    /// Runs the story and returns the harness, the recording extension and the three starting regions.
    /// Splits are allowed at every tick end (N_min 1, p_dead 0, T_min 0).
    /// </summary>
    private static (Kit2Harness H, RecordingExtension Extension, (long West, long Middle, long East) Regions) MergeThenSplit(
        string name, ClaimAnchoring anchoring, int mergeGap, int seed)
    {
        var extension = new RecordingExtension([West, Middle, East]);
        var h = new Kit2Harness(
            name,
            seed,
            Kit2Harness.Options(anchoring, mergeGap: mergeGap, splitMinSections: 1, splitMinDeadPercent: 0, splitMinAgeSteps: 0),
            extensions: [extension]);

        // Three regions whose claims are three sections apart, so neither gap joins them.
        h.Add(West, 4, Row);
        h.Add(Middle, 10, Row);
        h.Add(East, 16, Row);
        h.AssertClean();
        var regions = (h.RegionOf(West), h.RegionOf(Middle), h.RegionOf(East));
        h.Equal(3, h.R.RegionIds.Length, "regions at the start");
        h.True(regions.Item1 < regions.Item2 && regions.Item2 < regions.Item3, "region ids ascend west to east");

        // The outer two walk into the middle section: all three regions merge by the tick ends.
        h.Lockstep(() =>
        {
            h.MoveInTick(West, 10, Row);
            h.MoveInTick(East, 10, Row);
        });
        h.Equal(regions.Item1.ToString(), string.Join(",", h.R.RegionIds.ToArray()), "live regions after the merges");
        h.Equal(0, extension.Splits.Count, "splits before the walk back");

        // They walk back out: at the merged region's next tick end it splits into three islands.
        var result = h.Lockstep(() =>
        {
            h.MoveInTick(West, 4, Row);
            h.MoveInTick(East, 16, Row);
        })[regions.Item1];
        h.Equal(3, result.SplitChildren, "regions after the split");
        return (h, extension, regions);
    }

    /// <summary>Records every merge and split, asking <c>ownerOfEntity</c> for each probed entity during the split.</summary>
    private sealed class RecordingExtension(int[] probedEntities) : IRegionDataExtension
    {
        public List<(long Absorber, long Absorbed)> Merges { get; } = [];

        public List<RecordedSplit> Splits { get; } = [];

        public void OnMerge(long absorberId, long absorbedId) => Merges.Add((absorberId, absorbedId));

        public void OnSplit(long parentId, ReadOnlySpan<long> childIds, Func<int, long> ownerOfEntity) =>
            Splits.Add(new RecordedSplit(parentId, childIds.ToArray(), probedEntities.ToDictionary(e => e, ownerOfEntity)));
    }

    /// <summary>One <see cref="IRegionDataExtension.OnSplit"/> call and the owners it reported.</summary>
    private sealed record RecordedSplit(long Parent, long[] Children, Dictionary<int, long> OwnerOfEntity);
}
