using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Sections;

/// <summary>
/// The section grid (FR-001): power-of-two square sections keyed by layer and grid position in one
/// dense index space, with positions mapped to sections by floor(x / s) (spec Edge Cases, Assumptions).
/// </summary>
public sealed class SectionTopologyTests
{
    /// <summary>Layer layouts: one open-world layer; several layers with offset and negative origins, listed out of id order.</summary>
    public static TheoryData<string, LayerSpec[]> Layouts => new()
    {
        { "single open layer", [Kit1Fixtures.UniformLayer(0, 0, 0, 7, 5)] },
        {
            "three offset layers",
            [
                Kit1Fixtures.UniformLayer(0, 0, 0, 6, 4),
                Kit1Fixtures.UniformLayer(5, -3, 2, 4, 7, area: 2),
                Kit1Fixtures.UniformLayer(2, 10, -6, 3, 3, area: 1),
            ]
        },
        {
            "layers out of id order",
            [
                Kit1Fixtures.UniformLayer(9, -8, -8, 2, 9),
                Kit1Fixtures.UniformLayer(1, 0, 0, 5, 5),
                Kit1Fixtures.UniformLayer(4, 100, 200, 1, 1),
            ]
        },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void KeyAndIndexRoundTripOverEveryLayer(string layout, LayerSpec[] layers)
    {
        var topology = SectionTopology.Build(16, layers, []);

        Assert.Equal(layers.Sum(l => l.Width * l.Height), topology.SectionCount);

        var seen = new HashSet<int>();
        foreach (var layer in layers)
        {
            for (var y = layer.OriginY; y < layer.OriginY + layer.Height; y++)
            {
                for (var x = layer.OriginX; x < layer.OriginX + layer.Width; x++)
                {
                    var key = new SectionKey(layer.LayerId, x, y);
                    Assert.True(topology.TryIndexOf(key, out var index), $"{layout}: {key} has no index");
                    Assert.InRange(index, 0, topology.SectionCount - 1);
                    Assert.True(seen.Add(index), $"{layout}: index {index} is used twice");
                    Assert.Equal(key, topology.KeyOf(index));
                }
            }
        }

        // Dense: every index in 0..SectionCount-1 belongs to exactly one key, and maps back to itself.
        Assert.Equal(topology.SectionCount, seen.Count);
        for (var i = 0; i < topology.SectionCount; i++)
        {
            Assert.True(topology.TryIndexOf(topology.KeyOf(i), out var back), $"{layout}: key of index {i} has no index");
            Assert.Equal(i, back);
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void KeysOutsideEveryLayerHaveNoIndex(string layout, LayerSpec[] layers)
    {
        var topology = SectionTopology.Build(16, layers, []);

        foreach (var layer in layers)
        {
            SectionKey[] outside =
            [
                new(layer.LayerId, layer.OriginX - 1, layer.OriginY),
                new(layer.LayerId, layer.OriginX, layer.OriginY - 1),
                new(layer.LayerId, layer.OriginX + layer.Width, layer.OriginY),
                new(layer.LayerId, layer.OriginX, layer.OriginY + layer.Height),
            ];
            foreach (var key in outside)
            {
                Assert.False(topology.TryIndexOf(key, out var index), $"{layout}: {key} is outside its layer but has index {index}");
                Assert.Equal(-1, index);
            }
        }

        Assert.False(topology.TryIndexOf(new SectionKey(12345, 0, 0), out _), $"{layout}: an unknown layer has an index");
    }

    /// <summary>A layer from section (-4, -4) to (7, 7) at each section size; a position maps to floor(x / s), floor(y / s).</summary>
    [Theory]
    [InlineData(16, 8.0, 8.0, 0, 0)]
    [InlineData(16, 0.0, 0.0, 0, 0)]
    [InlineData(16, 16.0, 5.0, 1, 0)]
    [InlineData(16, 5.0, 16.0, 0, 1)]
    [InlineData(16, 32.0, 48.0, 2, 3)]
    [InlineData(16, 15.999999, 47.999999, 0, 2)]
    [InlineData(16, -0.000001, 3.0, -1, 0)]
    [InlineData(16, -16.0, -16.0, -1, -1)]
    [InlineData(16, -16.000001, -32.0, -2, -2)]
    [InlineData(16, -64.0, 127.999, -4, 7)]
    [InlineData(1, 3.0, -2.0, 3, -2)]
    [InlineData(1, 2.5, -2.5, 2, -3)]
    [InlineData(1024, 1024.0, -1024.0, 1, -1)]
    [InlineData(1024, 2047.5, 0.0, 1, 0)]
    public void PositionsMapToTheFlooredSectionOnEdgesAndCorners(int sectionSize, double x, double y, int expectedX, int expectedY)
    {
        var topology = SectionTopology.Build(sectionSize, [Kit1Fixtures.UniformLayer(3, -4, -4, 12, 12)], []);

        Assert.True(topology.TrySectionAt(3, x, y, out var index), $"({x}, {y}) at s = {sectionSize} was rejected");
        Assert.Equal(new SectionKey(3, expectedX, expectedY), topology.KeyOf(index));
    }

    /// <summary>The same layer from section (-4, -4) to (7, 7) at s = 16, so it covers [-64, 128) metres on each axis.</summary>
    [Theory]
    [InlineData(3, 128.0, 0.0)]
    [InlineData(3, 0.0, 128.0)]
    [InlineData(3, -64.000001, 0.0)]
    [InlineData(3, 0.0, -64.5)]
    [InlineData(3, 1e12, 0.0)]
    [InlineData(3, double.NaN, 0.0)]
    [InlineData(3, 0.0, double.NaN)]
    [InlineData(3, double.PositiveInfinity, 0.0)]
    [InlineData(3, 0.0, double.NegativeInfinity)]
    [InlineData(4, 0.0, 0.0)]
    public void PositionsOutsideALayerAreRejected(int layer, double x, double y)
    {
        var topology = SectionTopology.Build(16, [Kit1Fixtures.UniformLayer(3, -4, -4, 12, 12)], []);

        Assert.False(topology.TrySectionAt(layer, x, y, out var index), $"({x}, {y}) on layer {layer} was accepted as section {index}");
        Assert.Equal(-1, index);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-16)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void SectionSizeThatIsNotAPowerOfTwoIsRejected(int sectionSize)
    {
        var open = Assert.Throws<ArgumentException>(() => SectionTopology.OpenWorld(sectionSize, 1000, 1000));
        var built = Assert.Throws<ArgumentException>(() => SectionTopology.Build(sectionSize, [Kit1Fixtures.UniformLayer(0, 0, 0, 4, 4)], []));
        var options = Assert.Throws<ArgumentException>(
            () => new Regionizer(SectionTopology.OpenWorld(16, 64, 64), Kit1Fixtures.Options(sectionSize, ClaimAnchoring.Section, Kit1Fixtures.Radii(8))));

        Assert.Contains("power of two", open.Message, StringComparison.Ordinal);
        Assert.Contains("power of two", built.Message, StringComparison.Ordinal);
        Assert.Contains("power of two", options.Message, StringComparison.Ordinal);
    }

    /// <summary>OpenWorld covers the map with ceil(side / s) sections per axis on layer 0 and each extra layer.</summary>
    [Theory]
    [InlineData(1, 10.0, 3.0, 10, 3)]
    [InlineData(16, 100.0, 50.0, 7, 4)]
    [InlineData(16, 96.0, 16.0, 6, 1)]
    [InlineData(64, 8000.0, 8000.0, 125, 125)]
    [InlineData(1024, 1.0, 2049.0, 1, 3)]
    public void OpenWorldCoversTheMapWithPowerOfTwoSections(int sectionSize, double width, double height, int columns, int rows)
    {
        var topology = SectionTopology.OpenWorld(sectionSize, width, height, 7, 8);

        Assert.Equal(sectionSize, topology.SectionSize);
        Assert.Equal(3 * columns * rows, topology.SectionCount);
        foreach (var layer in new[] { 0, 7, 8 })
        {
            Assert.True(topology.TryIndexOf(new SectionKey(layer, columns - 1, rows - 1), out var last), $"layer {layer} lacks its far corner");
            Assert.Equal(0, topology.AreaOf(last));
            Assert.False(topology.TryIndexOf(new SectionKey(layer, columns, 0), out _), $"layer {layer} extends past {columns} columns");
            Assert.False(topology.TryIndexOf(new SectionKey(layer, 0, rows), out _), $"layer {layer} extends past {rows} rows");
        }
    }

    [Fact]
    public void AreaTagsAreReadPerSection()
    {
        // Row-major tags: area = 10 * local y + local x.
        var layer = Kit1Fixtures.Layer(2, 5, -3, 4, 3, (x, y) => (ushort)((10 * y) + x));
        var topology = SectionTopology.Build(8, [Kit1Fixtures.UniformLayer(0, 0, 0, 2, 2, area: 99), layer], []);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var index = Kit1Fixtures.IndexOf(topology, 2, 5 + x, -3 + y);
                Assert.Equal((ushort)((10 * y) + x), topology.AreaOf(index));
            }
        }

        Assert.Equal(99, topology.AreaOf(Kit1Fixtures.IndexOf(topology, 0, 1, 1)));
    }

    /// <summary>Topologies that cannot exist are refused when built.</summary>
    public static TheoryData<string, LayerSpec[], PortalSpec[]> InvalidTopologies => new()
    {
        { "no layers", [], [] },
        { "duplicate layer id", [Kit1Fixtures.UniformLayer(1, 0, 0, 2, 2), Kit1Fixtures.UniformLayer(1, 5, 5, 2, 2)], [] },
        { "zero width", [new LayerSpec(0, 0, 0, 0, 2, [])], [] },
        { "negative height", [new LayerSpec(0, 0, 0, 2, -1, [])], [] },
        { "too few area tags", [new LayerSpec(0, 0, 0, 2, 2, new ushort[3])], [] },
        { "portal endpoint outside every layer", [Kit1Fixtures.UniformLayer(0, 0, 0, 2, 2)], [new PortalSpec(1, new SectionKey(0, 0, 0), new SectionKey(0, 2, 0), true)] },
        { "portal endpoint on an unknown layer", [Kit1Fixtures.UniformLayer(0, 0, 0, 2, 2)], [new PortalSpec(1, new SectionKey(0, 0, 0), new SectionKey(3, 0, 0), true)] },
        { "portal joining a section to itself", [Kit1Fixtures.UniformLayer(0, 0, 0, 2, 2)], [new PortalSpec(1, new SectionKey(0, 1, 1), new SectionKey(0, 1, 1), false)] },
        {
            "duplicate portal id",
            [Kit1Fixtures.UniformLayer(0, 0, 0, 2, 2)],
            [new PortalSpec(4, new SectionKey(0, 0, 0), new SectionKey(0, 1, 0), true), new PortalSpec(4, new SectionKey(0, 0, 1), new SectionKey(0, 1, 1), true)]
        },
    };

    [Theory]
    [MemberData(nameof(InvalidTopologies))]
    public void InvalidTopologyIsRejected(string reason, LayerSpec[] layers, PortalSpec[] portals)
    {
        var error = Assert.Throws<ArgumentException>(() => SectionTopology.Build(16, layers, portals));

        Assert.False(string.IsNullOrWhiteSpace(error.Message), $"{reason}: the error names no cause");
    }

    [Fact]
    public void BuiltTopologyIsNotChangedByLaterEditsToItsInputs()
    {
        var tags = new ushort[] { 1, 1, 1, 1 };
        var topology = SectionTopology.Build(16, [new LayerSpec(0, 0, 0, 2, 2, tags)], []);

        tags[3] = 7;

        Assert.Equal(1, topology.AreaOf(Kit1Fixtures.IndexOf(topology, 0, 1, 1)));
    }
}
