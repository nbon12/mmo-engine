using Xunit;

namespace MmoEngine.Regionizer.Tests.Regressions;

/// <summary>Regressions for findings of the independent review of PR #47.</summary>
public sealed class ReviewFixTests
{
    private const int S = 16;

    private static RegionizerOptions Options(int mergeGap) =>
        new(S, ClaimAnchoring.Section, mergeGap, new ClaimRadii(0, 0, 0, 0, 0), 1000, 100, 1_000_000, TimerMode.TickCounter);

    /// <summary>
    /// Entity ids are compact and index dense arrays, so an id outside 0..MaxEntityId is refused
    /// before anything is allocated or placed.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(Regionizer.MaxEntityId + 1)]
    [InlineData(int.MaxValue)]
    public void EntityIdsOutsideTheCompactRangeAreRefused(int id)
    {
        var regionizer = new Regionizer(SectionTopology.OpenWorld(S, 256, 256), Options(0));

        Assert.Throws<ArgumentOutOfRangeException>(() => regionizer.AddEntity(id, EntityClass.Infantry, 0, 8, 8));
        Assert.False(regionizer.HasEntity(id));
        Assert.Equal(0, regionizer.RegionIds.Length);
    }

    /// <summary>
    /// FR-005 and FR-013 with a merge gap of 1: opening a portal between two owned endpoints merges
    /// their owners even when nobody claims either endpoint and the first owner holds no entity. The
    /// merge's cause is then taken from the second owner.
    /// </summary>
    [Fact]
    public void OpeningAPortalMergesOwnersWhenOnlyTheSecondHoldsEntities()
    {
        // Two areas on layer 0 split at x = 10, joined by a closed door between (9,5) and (10,5).
        var tags = new ushort[20 * 10];
        for (var y = 0; y < 10; y++)
        {
            for (var x = 10; x < 20; x++)
            {
                tags[(y * 20) + x] = 1;
            }
        }

        var topology = SectionTopology.Build(
            S,
            [new LayerSpec(0, 0, 0, 20, 10, tags)],
            [new PortalSpec(7, new SectionKey(0, 9, 5), new SectionKey(0, 10, 5), Open: false)]);
        var regionizer = new Regionizer(topology, Options(1));
        regionizer.AddEntity(1, EntityClass.Infantry, 0, (9 * S) + 8, (5 * S) + 8);
        regionizer.AddEntity(2, EntityClass.Infantry, 0, (10 * S) + 8, (5 * S) + 8);
        topology.TryIndexOf(new SectionKey(0, 9, 5), out var a);
        topology.TryIndexOf(new SectionKey(0, 10, 5), out var b);
        var regionA = regionizer.OwnerOf(a);
        var regionB = regionizer.OwnerOf(b);
        Assert.NotEqual(regionA, regionB);

        // B's entity walks off its endpoint during B's tick; the endpoint stays B's, dead.
        regionizer.AdvanceTo(2);
        Assert.True(regionizer.TryMarkTicking(regionB));
        regionizer.MoveEntity(2, 0, (13 * S) + 8, (5 * S) + 8);
        regionizer.MarkNotTicking(regionB);

        // A's only entity leaves by teleport, so A holds no entity and owns its endpoint, dead.
        regionizer.BeginTeleport(1, 0, (2 * S) + 8, (1 * S) + 8);
        Assert.Equal(regionA, regionizer.OwnerOf(a));
        Assert.Equal(regionB, regionizer.OwnerOf(b));
        Assert.Equal(0, regionizer.ClaimCountOf(a));
        Assert.Equal(0, regionizer.ClaimCountOf(b));

        regionizer.SetPortalOpen(7, true);

        Assert.Equal(regionizer.OwnerOf(a), regionizer.OwnerOf(b));
        Assert.Equal(Math.Min(regionA, regionB), regionizer.OwnerOf(a));
    }
}
