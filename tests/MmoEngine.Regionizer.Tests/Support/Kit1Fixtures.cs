using Xunit;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>
/// Builders for the section, claim, entity and architecture tests (workstream kit 1). They declare
/// valid data from explicit values and hold no business rules.
/// </summary>
internal static class Kit1Fixtures
{
    /// <summary>The same radius for every class.</summary>
    public static ClaimRadii Radii(double radius) => new(radius, radius, radius, radius, radius);

    /// <summary>Regionizer options from explicit values. The defaults never block a split.</summary>
    public static RegionizerOptions Options(
        int sectionSize,
        ClaimAnchoring anchoring,
        ClaimRadii radii,
        int mergeGap = 0,
        int splitMinSections = 1,
        double splitMinDeadPercent = 0,
        long splitMinAgeSteps = 0,
        TimerMode timerMode = TimerMode.TickCounter) =>
        new(sectionSize, anchoring, mergeGap, radii, splitMinSections, splitMinDeadPercent, splitMinAgeSteps, timerMode);

    /// <summary>A layer whose every section carries <paramref name="area"/>.</summary>
    public static LayerSpec UniformLayer(int layerId, int originX, int originY, int width, int height, ushort area = 0)
    {
        var tags = new ushort[width * height];
        Array.Fill(tags, area);
        return new LayerSpec(layerId, originX, originY, width, height, tags);
    }

    /// <summary>A layer whose area tags come from <paramref name="areaAt"/>, given layer-local section coordinates.</summary>
    public static LayerSpec Layer(int layerId, int originX, int originY, int width, int height, Func<int, int, ushort> areaAt)
    {
        var tags = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                tags[(y * width) + x] = areaAt(x, y);
            }
        }

        return new LayerSpec(layerId, originX, originY, width, height, tags);
    }

    /// <summary>The dense index of a key that must exist.</summary>
    public static int IndexOf(SectionTopology topology, int layer, int x, int y)
    {
        Assert.True(topology.TryIndexOf(new SectionKey(layer, x, y), out var index), $"section ({layer}, {x}, {y}) is outside the topology");
        return index;
    }

    /// <summary>The keys of an entity's stored claim, sorted.</summary>
    public static SectionKey[] ClaimKeys(Regionizer regionizer, int entityId)
    {
        var claim = regionizer.GetEntity(entityId).ClaimedSections;
        var keys = new SectionKey[claim.Length];
        for (var i = 0; i < claim.Length; i++)
        {
            keys[i] = regionizer.Topology.KeyOf(claim[i]);
        }

        return [.. keys.Order(SectionKeyOrder.Instance)];
    }

    /// <summary>The keys of a set of section indices, sorted.</summary>
    public static SectionKey[] Keys(SectionTopology topology, IEnumerable<int> sections) =>
        [.. sections.Select(topology.KeyOf).Order(SectionKeyOrder.Instance)];

    /// <summary>The ids of the live regions whose entity list holds <paramref name="entityId"/>.</summary>
    public static long[] RegionsListing(Regionizer regionizer, int entityId)
    {
        var found = new List<long>();
        foreach (var id in regionizer.RegionIds.ToArray())
        {
            if (regionizer.GetRegion(id).Entities.Contains(entityId))
            {
                found.Add(id);
            }
        }

        return [.. found];
    }

    /// <summary>The sections a live region owns, sorted by index.</summary>
    public static int[] SectionsOf(Regionizer regionizer, long regionId) =>
        [.. regionizer.GetRegion(regionId).Sections.ToArray().Order()];

    /// <summary>The sum of every section's claim count.</summary>
    public static long TotalClaimCount(Regionizer regionizer)
    {
        long total = 0;
        for (var s = 0; s < regionizer.Topology.SectionCount; s++)
        {
            total += regionizer.ClaimCountOf(s);
        }

        return total;
    }

    /// <summary>Runs the invariant checker at a tick boundary and fails with every violation and the context.</summary>
    public static void AssertInvariantsHold(Regionizer regionizer, string context)
    {
        var violations = new List<Violation>();
        var count = InvariantChecker.Check(regionizer, violations);
        Assert.True(count == 0, $"{context}: {count} invariant violation(s): {string.Join("; ", violations.Select(v => $"rule {(int)v.Rule} {v.Rule}: {v.Detail}"))}");
    }

    /// <summary>Orders section keys by layer, then Y, then X.</summary>
    public sealed class SectionKeyOrder : IComparer<SectionKey>
    {
        public static readonly SectionKeyOrder Instance = new();

        public int Compare(SectionKey a, SectionKey b)
        {
            var c = a.Layer.CompareTo(b.Layer);
            if (c != 0)
            {
                return c;
            }

            c = a.Y.CompareTo(b.Y);
            return c != 0 ? c : a.X.CompareTo(b.X);
        }
    }
}
