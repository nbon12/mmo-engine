using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CsCheck;
using MmoEngine.Maps.Tests.Support;
using MmoEngine.Regionizer;
using Xunit;
using static MmoEngine.Maps.Tests.Support.TestMaps;

namespace MmoEngine.Maps.Tests;

/// <summary>The map loader and its conversion to the core topology (T053, FR-029, contracts/map-format.md).</summary>
public sealed class MapLoaderTests
{
    private static readonly SchemaCase[] SchemaCases =
    [
        new("name missing", m => m.Remove("name"), "name: is required"),
        new("name blank", m => m["name"] = " ", "name: must not be blank"),
        new("section size missing", m => m.Remove("sectionSize"), "sectionSize: is required"),
        new("section size not a power of two", m => m["sectionSize"] = 24, "sectionSize: must be a power of two from 1 to 1024, was 24"),
        new("section size zero", m => m["sectionSize"] = 0, "sectionSize: must be a power of two from 1 to 1024, was 0"),
        new("section size over 1024", m => m["sectionSize"] = 2048, "sectionSize: must be a power of two from 1 to 1024, was 2048"),
        new("layers missing", m => m.Remove("layers"), "layers: at least one layer is required"),
        new("layers empty", m => m["layers"] = new JsonArray(), "layers: at least one layer is required"),
        new("layer null", m => m["layers"]![0] = null, "layers[0]: must be an object"),
        new("layer id missing", m => LayerAt(m, 0).Remove("id"), "layers[0].id: is required"),
        new("layer id twice", m => LayerAt(m, 1)["id"] = 0, "layers[1].id: duplicate layer id 0"),
        new("width missing", m => LayerAt(m, 0).Remove("width"), "layers[0].width: is required"),
        new("width zero", m => LayerAt(m, 0)["width"] = 0, "layers[0].width: must be at least 1, was 0"),
        new("height negative", m => LayerAt(m, 0)["height"] = -3, "layers[0].height: must be at least 1, was -3"),
        new("width past the largest coordinate", m => LayerAt(m, 1)["originX"] = int.MaxValue - 2, "layers[1].width: the layer extends past the largest section coordinate"),
        new("height past the largest coordinate", m => LayerAt(m, 1)["originY"] = int.MaxValue, "layers[1].height: the layer extends past the largest section coordinate"),
        new("more sections than the limit", m => { LayerAt(m, 0)["width"] = 4096; LayerAt(m, 0)["height"] = 4096; }, "layers[1]: the map has more than 16777216 sections"),
        new("default area missing", m => LayerAt(m, 0).Remove("defaultArea"), "layers[0].defaultArea: is required"),
        new("default area with whitespace", m => LayerAt(m, 0)["defaultArea"] = "open field", "layers[0].defaultArea: must not contain whitespace, was \"open field\""),
        new("area null", m => LayerAt(m, 0)["areas"]![0] = null, "layers[0].areas[0]: must be an object"),
        new("area tag missing", m => AreaAt(m).Remove("tag"), "layers[0].areas[0].tag: is required"),
        new("area without rectangles", m => AreaAt(m)["rects"] = new JsonArray(), "layers[0].areas[0].rects: at least one rectangle is required"),
        new("rectangle of three numbers", m => AreaAt(m)["rects"] = new JsonArray(new JsonArray(1, 2, 3)), "layers[0].areas[0].rects[0]: must be [x0, y0, x1, y1]"),
        new("rectangle null", m => AreaAt(m)["rects"] = new JsonArray((JsonNode?)null), "layers[0].areas[0].rects[0]: must be [x0, y0, x1, y1]"),
        new("rectangle inverted in x", m => AreaAt(m)["rects"] = new JsonArray(Rect(5, 5, 4, 6)), "layers[0].areas[0].rects[0]: x1 and y1 must not be less than x0 and y0, was [5,5,4,6]"),
        new("rectangle inverted in y", m => AreaAt(m)["rects"] = new JsonArray(Rect(5, 5, 6, 4)), "layers[0].areas[0].rects[0]: x1 and y1 must not be less than x0 and y0, was [5,5,6,4]"),
        new("rectangle past the layer edge", m => AreaAt(m)["rects"] = new JsonArray(Rect(10, 10, 16, 12)), "layers[0].areas[0].rects[0]: [10,10,16,12] is not inside layer 0, which covers [0,0,15,15]"),
        new("blocked rectangle before the layer origin", m => LayerAt(m, 0)["blocked"]![0] = Rect(-1, 0, 0, 0), "layers[0].blocked[0]: [-1,0,0,0] is not inside layer 0, which covers [0,0,15,15]"),
        new("sealed area unknown", m => m["sealedAreas"] = new JsonArray("bunker"), "sealedAreas[0]: no layer uses area bunker"),
        new("sealed area twice", m => m["sealedAreas"] = new JsonArray("cellar", "cellar"), "sealedAreas[1]: duplicate sealed area cellar"),
        new("sealed area null", m => m["sealedAreas"] = new JsonArray((JsonNode?)null), "sealedAreas[0]: is required"),
        new("adjacency entry null", m => m["adjacency"]![0] = null, "adjacency[0]: must be an object"),
        new("adjacency endpoint missing", m => EntryAt(m, "adjacency").Remove("b"), "adjacency[0].b: is required"),
        new("adjacency endpoint of two numbers", m => EntryAt(m, "adjacency")["a"] = new JsonArray(0, 1), "adjacency[0].a: must be [layer, x, y]"),
        new("adjacency endpoint outside its layer", m => EntryAt(m, "adjacency")["b"] = Key(1, 4, 1), "adjacency[0].b: section [1,4,1] is outside layer 1"),
        new("adjacency endpoint on a missing layer", m => EntryAt(m, "adjacency")["a"] = Key(4, 1, 1), "adjacency[0].a: section [4,1,1] is on layer 4, which does not exist"),
        new("adjacency joining a section to itself", m => EntryAt(m, "adjacency")["b"] = Key(0, 1, 1), "adjacency[0]: a and b are the same section [0,1,1]"),
        new("adjacency kind blank", m => EntryAt(m, "adjacency")["kind"] = "", "adjacency[0].kind: must not be blank"),
        new("opening id blank", m => EntryAt(m, "openings")["id"] = "", "openings[0].id: must not be blank"),
        new("opening id with whitespace", m => EntryAt(m, "openings")["id"] = "north window", "openings[0].id: must not contain whitespace, was \"north window\""),
        new("opening id twice", m => m["openings"]!.AsArray().Add(Entry("window", "window", Key(0, 7, 3), Key(0, 7, 4))), "openings[1].id: duplicate id window"),
        new("portal null", m => m["portals"]![1] = null, "portals[1]: must be an object"),
        new("portal id missing", m => PortalAt(m, 0).Remove("id"), "portals[0].id: is required"),
        new("portal id twice", m => PortalAt(m, 1)["id"] = 1, "portals[1].id: duplicate portal id 1"),
        new("portal endpoint missing", m => PortalAt(m, 0).Remove("a"), "portals[0].a: is required"),
        new("portal endpoint of four numbers", m => PortalAt(m, 0)["b"] = new JsonArray(1, 1, 1, 1), "portals[0].b: must be [layer, x, y]"),
        new("portal joining a section to itself", m => PortalAt(m, 0)["b"] = Key(0, 1, 1), "portals[0]: a and b are the same section [0,1,1]"),
        new("schedule entry null", m => m["portalSchedule"]![0] = null, "portalSchedule[0]: must be an object"),
        new("schedule portal missing", m => ScheduleAt(m).Remove("portal"), "portalSchedule[0].portal: is required"),
        new("schedule for an unknown portal", m => ScheduleAt(m)["portal"] = 9, "portalSchedule[0].portal: no portal has id 9"),
        new("schedule twice for one portal", m => m["portalSchedule"]!.AsArray().Add(Schedule(1, 30, 10)), "portalSchedule[1].portal: portal 1 already has a schedule"),
        new("schedule period missing", m => ScheduleAt(m).Remove("everySeconds"), "portalSchedule[0].everySeconds: is required"),
        new("schedule period zero", m => ScheduleAt(m)["everySeconds"] = 0, "portalSchedule[0].everySeconds: must be greater than 0, was 0"),
        new("schedule open time missing", m => ScheduleAt(m).Remove("openSeconds"), "portalSchedule[0].openSeconds: is required"),
        new("schedule open time negative", m => ScheduleAt(m)["openSeconds"] = -1, "portalSchedule[0].openSeconds: must be from 0 to everySeconds (60), was -1"),
        new("schedule open longer than its period", m => ScheduleAt(m)["openSeconds"] = 61, "portalSchedule[0].openSeconds: must be from 0 to everySeconds (60), was 61"),
        new("unknown top-level field", m => m["description"] = "x", "description: is not a field of map format version 1"),
        new("unknown layer field", m => LayerAt(m, 0)["colour"] = "red", "layers[0].colour: is not a field of map format version 1"),
        new("unknown area field", m => AreaAt(m)["floor"] = 1, "layers[0].areas[0].floor: is not a field of map format version 1"),
        new("unknown adjacency field", m => EntryAt(m, "adjacency")["width"] = 2, "adjacency[0].width: is not a field of map format version 1"),
        new("unknown portal field", m => PortalAt(m, 1)["locked"] = true, "portals[1].locked: is not a field of map format version 1"),
        new("misspelt policy field", m => PortalAt(m, 0)["closedPolicy"]!["entityTransfer"] = false, "portals[0].closedPolicy.entityTransfer: is not a field of map format version 1"),
        new("unknown schedule field", m => ScheduleAt(m)["offsetSeconds"] = 5, "portalSchedule[0].offsetSeconds: is not a field of map format version 1"),
        new("field name in the wrong case", m => m["SectionSize"] = 16, "SectionSize: is not a field of map format version 1"),
        new("layers as an object", m => m["layers"] = new JsonObject(), "layers: has the wrong type of value (line 1)"),
        new("width as a string", m => LayerAt(m, 0)["width"] = "16", "layers[0].width: has the wrong type of value (line 1)"),
        new("fractional rectangle corner", m => AreaAt(m)["rects"] = new JsonArray(new JsonArray(1, 1, 2.5, 2)), "layers[0].areas[0].rects[0][2]: has the wrong type of value (line 1)"),
        new("null section coordinate", m => EntryAt(m, "adjacency")["a"] = new JsonArray(0, null, 1), "adjacency[0].a[1]: has the wrong type of value (line 1)"),
        new("open as a string", m => PortalAt(m, 0)["open"] = "yes", "portals[0].open: has the wrong type of value (line 1)"),
        new("allows direct entry as a string", m => LayerAt(m, 2)["allowsDirectEntry"] = "yes", "layers[2].allowsDirectEntry: has the wrong type of value (line 1)"),
        new("allows direct entry as a number", m => LayerAt(m, 0)["allowsDirectEntry"] = 1, "layers[0].allowsDirectEntry: has the wrong type of value (line 1)"),
        new("allows direct entry in the wrong case", m => LayerAt(m, 0)["AllowsDirectEntry"] = true, "layers[0].AllowsDirectEntry: is not a field of map format version 1"),
    ];

    public static TheoryData<string> SchemaCaseNames => Names(SchemaCases);

    [Fact]
    public void FullTestMapLoadsAndHasNoFindings()
    {
        var map = Parse(Full());

        Assert.Empty(PortalValidator.Validate(map));
    }

    [Theory]
    [InlineData(3, 4, "courtyard")]
    [InlineData(5, 6, "courtyard")]
    [InlineData(6, 6, "open")]
    [InlineData(2, 3, "open")]
    [InlineData(11, 12, "market")]
    [InlineData(2, 12, "market")]
    [InlineData(4, 12, "open")]
    public void RectanglesIncludeBothCornersAndTheDefaultAreaCoversTheRest(int x, int y, string expectedTag)
    {
        // A 10 × 10 layer at origin (2, 3): courtyard [3,4]–[5,6], market [8,8]–[11,12] and [2,12]–[3,12].
        var layer = Layer(0, 2, 3, 10, 10, "open");
        layer["areas"] = new JsonArray(
            Area("courtyard", Rect(3, 4, 5, 6)),
            Area("market", Rect(8, 8, 11, 12), Rect(2, 12, 3, 12)));
        var map = Parse(Map(layer));

        Assert.Equal(expectedTag, map.AreaNames[map.AreaIdAt(new SectionKey(0, x, y))]);
    }

    [Theory]
    [InlineData("[{\"tag\":\"a\",\"rects\":[[2,2,5,5]]},{\"tag\":\"b\",\"rects\":[[4,4,8,8]]}]", "layers[0].areas[1].rects[0]: overlaps layers[0].areas[0].rects[0] at section [0,4,4]")]
    [InlineData("[{\"tag\":\"a\",\"rects\":[[0,0,3,3],[3,3,6,6]]}]", "layers[0].areas[0].rects[1]: overlaps layers[0].areas[0].rects[0] at section [0,3,3]")]
    [InlineData("[{\"tag\":\"a\",\"rects\":[[1,1,1,1]]},{\"tag\":\"a\",\"rects\":[[1,1,1,1]]}]", "layers[0].areas[1].rects[0]: overlaps layers[0].areas[0].rects[0] at section [0,1,1]")]
    [InlineData("[{\"tag\":\"a\",\"rects\":[[0,0,9,9]]},{\"tag\":\"b\",\"rects\":[[7,7,8,8]]},{\"tag\":\"c\",\"rects\":[[5,6,5,6]]}]", "layers[0].areas[1].rects[0]: overlaps layers[0].areas[0].rects[0] at section [0,7,7]")]
    public void OverlappingRectanglesAreAnError(string areasJson, string expectedMessage)
    {
        var layer = Layer(0, 0, 0, 10, 10, "open");
        layer["areas"] = JsonNode.Parse(areasJson);

        var ex = Assert.Throws<MapFormatException>(() => Parse(Map(layer)));

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Fact]
    public void RectanglesThatOnlyTouchDoNotOverlap()
    {
        var layer = Layer(0, 0, 0, 10, 10, "open");
        layer["areas"] = new JsonArray(Area("west", Rect(0, 0, 3, 3)), Area("east", Rect(4, 0, 7, 3)), Area("south", Rect(0, 4, 7, 4)));

        var map = Parse(Map(layer));

        Assert.Equal(["east", "open", "south", "west"], map.AreaNames);
        Assert.Equal(map.AreaIdOf("west"), map.AreaIdAt(new SectionKey(0, 3, 3)));
        Assert.Equal(map.AreaIdOf("east"), map.AreaIdAt(new SectionKey(0, 4, 3)));
        Assert.Equal(map.AreaIdOf("south"), map.AreaIdAt(new SectionKey(0, 3, 4)));
    }

    [Theory]
    [InlineData("0", "formatVersion: unsupported version 0; this reader supports version 1")]
    [InlineData("2", "formatVersion: unsupported version 2; this reader supports version 1")]
    [InlineData("\"1\"", "formatVersion: must be an integer, was \"1\"")]
    [InlineData("1.5", "formatVersion: must be an integer, was 1.5")]
    [InlineData(null, "formatVersion: is required")]
    public void UnknownFormatVersionIsAnError(string? version, string expectedMessage)
    {
        // The map also carries a field version 1 doesn't define: the version is checked first.
        var map = Full();
        map.Remove("formatVersion");
        if (version is not null)
        {
            map["formatVersion"] = JsonNode.Parse(version);
        }

        map["futureField"] = true;

        var ex = Assert.Throws<MapFormatException>(() => Parse(map));

        Assert.Equal(expectedMessage, ex.Message);
    }

    [Theory]
    [MemberData(nameof(SchemaCaseNames))]
    public void SchemaErrorNamesTheFieldAndTheReason(string caseName)
    {
        var schemaCase = SchemaCases.Single(c => c.Name == caseName);
        var map = Full();
        schemaCase.Mutate(map);

        var ex = Assert.Throws<MapFormatException>(() => Parse(map));

        Assert.Equal(schemaCase.Expected, ex.Message);
    }

    [Theory]
    [InlineData("", "invalid map JSON: ")]
    [InlineData("{", "invalid map JSON: ")]
    [InlineData("{\"formatVersion\": 1,}", "invalid map JSON: ")]
    [InlineData("// a comment\n{\"formatVersion\": 1}", "invalid map JSON: ")]
    [InlineData("[1, 2]", "a map must be a JSON object, was Array")]
    [InlineData("null", "a map must be a JSON object, was Null")]
    [InlineData("\"map\"", "a map must be a JSON object, was String")]
    public void TextThatIsNotAMapObjectIsAnError(string json, string expectedStart)
    {
        var ex = Assert.Throws<MapFormatException>(() => MapDocument.Parse(json));

        Assert.StartsWith(expectedStart, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseKeepsEveryFieldOfTheFile()
    {
        var map = MapDocument.Load(TestFiles.Map("test/clean.json"));

        Assert.Equal("clean", map.Name);
        Assert.Equal(16, map.SectionSize);
        Assert.Equal(["bunker"], map.SealedAreas);

        var surface = map.Layers[0];
        Assert.Equal((0, "surface", 0, 0, 32, 32, "open"), (surface.Id, surface.Name, surface.OriginX, surface.OriginY, surface.Width, surface.Height, surface.DefaultArea));
        var courtyard = Assert.Single(surface.Areas);
        Assert.Equal("courtyard", courtyard.Tag);
        Assert.Equal([new MapRect(4, 4, 11, 11)], courtyard.Rects);
        Assert.Equal([new MapRect(16, 4, 16, 20), new MapRect(4, 12, 11, 12)], surface.Blocked);
        var bunker = map.Layers[1];
        Assert.Equal((1, "bunker", 20, 20, 8, 8, "bunker"), (bunker.Id, bunker.Name, bunker.OriginX, bunker.OriginY, bunker.Width, bunker.Height, bunker.DefaultArea));
        Assert.Empty(bunker.Areas);

        Assert.Equal(
            [
                new MapConnection(MapConnectionType.Adjacency, 0, "courtyard-gate", "walkway", new SectionKey(0, 11, 8), new SectionKey(0, 12, 8)),
                new MapConnection(MapConnectionType.Adjacency, 1, "bunker-stairs", "stairs", new SectionKey(1, 22, 22), new SectionKey(0, 22, 22)),
                new MapConnection(MapConnectionType.Adjacency, 2, "wall-ladder", "ladder", new SectionKey(0, 15, 10), new SectionKey(0, 17, 10)),
                new MapConnection(MapConnectionType.Adjacency, 3, null, "walkway", new SectionKey(1, 21, 26), new SectionKey(1, 26, 21)),
            ],
            map.Adjacency);
        Assert.Equal(["adjacency:courtyard-gate", "adjacency:bunker-stairs", "adjacency:wall-ladder", "adjacency[3]"], map.Adjacency.Select(a => a.Location));
        var window = Assert.Single(map.Openings);
        Assert.Equal(new MapConnection(MapConnectionType.Opening, 0, "courtyard-window", "window", new SectionKey(0, 11, 5), new SectionKey(0, 12, 5)), window);
        Assert.Equal("openings:courtyard-window", window.Location);

        Assert.Equal(
            [
                new MapPortal(1, "bunker-door", new SectionKey(0, 22, 22), new SectionKey(1, 22, 22), false, new ClosedPolicy(false, true, false)),
                new MapPortal(2, "courtyard-gate", new SectionKey(0, 11, 8), new SectionKey(0, 12, 8), true, new ClosedPolicy(false, true, true)),
                new MapPortal(3, "courtyard-window", new SectionKey(0, 12, 5), new SectionKey(0, 11, 5), true, new ClosedPolicy(false, false, true)),
            ],
            map.Portals);
        Assert.Equal("portals:3", map.Portals[2].Location);
        Assert.Equal([new PortalScheduleEntry(1, 60, 20)], map.PortalSchedule);
    }

    [Fact]
    public void PortalOpenDefaultsToClosedAndConnectionsNeedNoIdOrKind()
    {
        var map = Full();
        PortalAt(map, 0).Remove("open");
        EntryAt(map, "openings").Remove("id");
        EntryAt(map, "openings").Remove("kind");

        var parsed = Parse(map);

        Assert.False(parsed.Portals[0].Open);
        Assert.Equal((null, null, "openings[0]"), (parsed.Openings[0].Id, parsed.Openings[0].Kind, parsed.Openings[0].Location));
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(4, 4, true)]
    [InlineData(3, 3, true)]
    [InlineData(5, 3, true)]
    [InlineData(6, 3, true)]
    [InlineData(10, 10, true)]
    [InlineData(7, 3, false)]
    [InlineData(5, 4, false)]
    [InlineData(5, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(9, 10, false)]
    public void BlockedRectanglesMarkExactlyTheSectionsTheyCover(int x, int y, bool expectedBlocked)
    {
        // A 10 × 10 layer at origin (1, 1); the first two blocked rectangles overlap at [3,3]–[4,3].
        var layer = Layer(0, 1, 1, 10, 10, "open");
        layer["blocked"] = new JsonArray(Rect(2, 2, 4, 4), Rect(3, 3, 6, 3), Rect(10, 10, 10, 10));
        var map = Parse(Map(layer));

        Assert.Equal(expectedBlocked, map.IsBlocked(new SectionKey(0, x, y)));
        Assert.Equal(expectedBlocked, map.Layers[0].IsBlocked(x, y));
    }

    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(0, 16, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(1, 4, 4)]
    public void SectionQueriesOutsideEveryLayerAreRejected(int layer, int x, int y)
    {
        var map = Parse(Full());
        var key = new SectionKey(layer, x, y);

        Assert.False(map.Contains(key));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.AreaIdAt(key));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.IsBlocked(key));
    }

    [Fact]
    public void AreaIdOfAnUnknownTagIsRejected()
    {
        var map = Parse(Full());

        Assert.False(map.TryGetAreaId("bunker", out _));
        var ex = Assert.Throws<ArgumentException>(() => map.AreaIdOf("bunker"));
        Assert.Equal("tag", ex.ParamName);
    }

    [Fact]
    public void AreaIdsFollowOrdinalTagOrderWhateverTheOrderTheFileMentionsThem()
    {
        // Distinct tags in generated (declaration) order; tag i covers column i of a one-row layer.
        var tags = Gen.String[Gen.Char["aAzZ09-_."], 1, 3].ArrayUnique[1, 8];

        Property.ForAll(tags, seed: 20261103, iterations: 300, declared =>
        {
            var layer = Layer(0, 0, 0, declared.Length, 1, declared[0]);
            layer["areas"] = new JsonArray(declared.Skip(1).Select((tag, i) => (JsonNode)Area(tag, Rect(i + 1, 0, i + 1, 0))).ToArray());
            var map = Parse(Map(layer));
            var topology = map.ToTopology();

            Assert.Equal(declared.Order(StringComparer.Ordinal), map.AreaNames);
            for (var x = 0; x < declared.Length; x++)
            {
                Assert.True(topology.TryIndexOf(new SectionKey(0, x, 0), out var index));
                Assert.Equal(declared[x], map.AreaNames[topology.AreaOf(index)]);
            }
        });
    }

    [Fact]
    public void AreaTagsCanFillTheWholeUshortIdRange()
    {
        var map = MapDocument.Parse(OneSectionPerTag(ushort.MaxValue + 1));

        Assert.Equal(ushort.MaxValue + 1, map.AreaNames.Count);
        Assert.Equal(ushort.MaxValue, map.AreaIdAt(new SectionKey(0, ushort.MaxValue, 0)));
        Assert.Equal("t65535", map.AreaNames[ushort.MaxValue]);
    }

    [Fact]
    public void MoreAreaTagsThanUshortIdsAreAnError()
    {
        var ex = Assert.Throws<MapFormatException>(() => MapDocument.Parse(OneSectionPerTag(ushort.MaxValue + 2)));

        Assert.Equal("layers: at most 65536 area tags are allowed, found 65537", ex.Message);
    }

    [Fact]
    public void ToTopologyKeepsLayersAreasAndPortals()
    {
        var map = MapDocument.Load(TestFiles.Map("test/clean.json"));

        var topology = map.ToTopology();

        Assert.Equal(16, topology.SectionSize);
        Assert.Equal((32 * 32) + (8 * 8), topology.SectionCount);
        Assert.Equal(map.AreaIdOf("courtyard"), AreaOf(topology, new SectionKey(0, 4, 4)));
        Assert.Equal(map.AreaIdOf("open"), AreaOf(topology, new SectionKey(0, 12, 4)));
        Assert.Equal(map.AreaIdOf("bunker"), AreaOf(topology, new SectionKey(1, 27, 27)));
        Assert.False(topology.TryIndexOf(new SectionKey(1, 19, 20), out _));
        foreach (var layer in map.Layers)
        {
            for (var y = layer.OriginY; y < layer.OriginY + layer.Height; y++)
            {
                for (var x = layer.OriginX; x < layer.OriginX + layer.Width; x++)
                {
                    var key = new SectionKey(layer.Id, x, y);
                    Assert.True(topology.TryIndexOf(key, out var index));
                    Assert.Equal(key, topology.KeyOf(index));
                    Assert.Equal(layer.AreaIdAt(x, y), topology.AreaOf(index));
                }
            }
        }

        Assert.Equal(3, topology.PortalCount);
        Assert.Equal(new PortalSpec(1, new SectionKey(0, 22, 22), new SectionKey(1, 22, 22), false), topology.PortalAt(0));
        Assert.Equal(new PortalSpec(2, new SectionKey(0, 11, 8), new SectionKey(0, 12, 8), true), topology.PortalAt(1));
        Assert.Equal(new PortalSpec(3, new SectionKey(0, 12, 5), new SectionKey(0, 11, 5), true), topology.PortalAt(2));
    }

    [Fact]
    public void ToTopologyRefusesAPortalOutsideEveryLayer()
    {
        var map = MapDocument.Load(TestFiles.Map("test/dead-portal.json"));

        var ex = Assert.Throws<ArgumentException>(map.ToTopology);

        Assert.Contains("outside every layer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowsDirectEntryIsReadFromTheLayer(bool value)
    {
        // The full map's sky (layer 2) sets the field to true and the surface (layer 0) leaves it out.
        var map = Full();
        LayerAt(map, 1)["allowsDirectEntry"] = value;

        var parsed = Parse(map);

        Assert.Equal([0, 1, 2], parsed.Layers.Select(l => l.Id));
        Assert.Equal([false, value, true], parsed.Layers.Select(l => l.AllowsDirectEntry));
    }

    [Fact]
    public void AllowsDirectEntryDefaultsToFalse()
    {
        var map = Parse(Map(Layer(0, 0, 0, 4, 4, "open"), Layer(1, 0, 0, 2, 2, "cellar")));

        var topology = map.ToTopology();

        Assert.Equal([false, false], map.Layers.Select(l => l.AllowsDirectEntry));
        Assert.False(topology.AllowsDirectEntry(0));
        Assert.False(topology.AllowsDirectEntry(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ToTopologyCarriesAllowsDirectEntryIntoTheCoreLayer(int directEntryLayer)
    {
        // Only one layer sets the field; the others leave it out.
        var map = Full();
        for (var i = 0; i < 3; i++)
        {
            LayerAt(map, i).Remove("allowsDirectEntry");
        }

        LayerAt(map, directEntryLayer)["allowsDirectEntry"] = true;

        var topology = Parse(map).ToTopology();

        Assert.Equal(Enumerable.Range(0, 3).Select(id => id == directEntryLayer), Enumerable.Range(0, 3).Select(topology.AllowsDirectEntry));
        Assert.False(topology.AllowsDirectEntry(3), "layer 3 does not exist");
    }

    [Fact]
    public void StackedBaseMapHasTheStackedAreasLayout()
    {
        var map = MapDocument.Load(TestFiles.Map("stacked-base.json"));

        Assert.Equal(16, map.SectionSize);
        Assert.True(map.TryGetLayer(0, out var surface));
        Assert.True(map.TryGetLayer(1, out var bunker));
        Assert.Equal((0, 0, 2000, 2000, "open"), (surface.OriginX, surface.OriginY, surface.Width * map.SectionSize, surface.Height * map.SectionSize, surface.DefaultArea));
        Assert.Empty(surface.Areas);

        // The bunker is about 20 × 20 sections, sealed, and centred under the surface's centre.
        Assert.InRange(bunker.Width, 18, 22);
        Assert.InRange(bunker.Height, 18, 22);
        Assert.Equal("bunker", bunker.DefaultArea);
        Assert.Equal(["bunker"], map.SealedAreas);
        var centre = surface.Width * map.SectionSize / 2.0;
        Assert.InRange(((bunker.OriginX * map.SectionSize) + (bunker.Width * map.SectionSize / 2.0)) - centre, -map.SectionSize, map.SectionSize);
        Assert.InRange(((bunker.OriginY * map.SectionSize) + (bunker.Height * map.SectionSize / 2.0)) - centre, -map.SectionSize, map.SectionSize);

        // Two doors join the surface and the bunker, each with a complete closed-state policy and a schedule.
        Assert.Equal(2, map.Portals.Count);
        Assert.All(map.Portals, p =>
        {
            Assert.Equal((0, 1), (Math.Min(p.A.Layer, p.B.Layer), Math.Max(p.A.Layer, p.B.Layer)));
            Assert.NotNull(p.ClosedPolicy);
            Assert.NotNull(p.ClosedPolicy.EntityTransfers);
            Assert.NotNull(p.ClosedPolicy.DoorAndObjectiveEvents);
            Assert.NotNull(p.ClosedPolicy.DeferredProjectiles);
        });
        Assert.Equal(map.Portals.Select(p => p.Id).Order(), map.PortalSchedule.Select(s => s.PortalId).Order());
        Assert.Equal((125 * 125) + (bunker.Width * bunker.Height), map.ToTopology().SectionCount);
    }

    [Fact]
    public void LoadNamesTheFileInSchemaErrors()
    {
        var path = Path.Combine(TestFiles.NewTempDirectory(), "bad.json");
        var map = Full();
        map["sectionSize"] = 3;
        File.WriteAllText(path, map.ToJsonString());

        var ex = Assert.Throws<MapFormatException>(() => MapDocument.Load(path));

        Assert.Equal($"{path}: sectionSize: must be a power of two from 1 to 1024, was 3", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadOfAMissingFileOrAFolderIsAMapFormatError(bool folder)
    {
        var directory = TestFiles.NewTempDirectory();
        var path = folder ? directory : Path.Combine(directory, "missing.json");

        var ex = Assert.Throws<MapFormatException>(() => MapDocument.Load(path));

        Assert.StartsWith($"{path}: could not be read: ", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A one-row layer with one tag per section: "t00000" is the default, and each other section is its own area.</summary>
    private static string OneSectionPerTag(int tagCount)
    {
        var json = new StringBuilder();
        json.Append(CultureInfo.InvariantCulture, $"{{\"formatVersion\":1,\"name\":\"tags\",\"sectionSize\":16,\"layers\":[{{\"id\":0,\"width\":{tagCount},\"height\":1,\"defaultArea\":\"t00000\",\"areas\":[");
        for (var i = 1; i < tagCount; i++)
        {
            json.Append(i > 1 ? "," : string.Empty);
            json.Append(CultureInfo.InvariantCulture, $"{{\"tag\":\"t{i:D5}\",\"rects\":[[{i},0,{i},0]]}}");
        }

        return json.Append("]}]}").ToString();
    }

    private static ushort AreaOf(SectionTopology topology, SectionKey key)
    {
        Assert.True(topology.TryIndexOf(key, out var index));
        return topology.AreaOf(index);
    }

    private static TheoryData<string> Names(SchemaCase[] cases)
    {
        var names = new TheoryData<string>();
        foreach (var c in cases)
        {
            names.Add(c.Name);
        }

        return names;
    }

    private static JsonObject LayerAt(JsonObject map, int index) => map["layers"]![index]!.AsObject();

    private static JsonObject AreaAt(JsonObject map) => LayerAt(map, 0)["areas"]![0]!.AsObject();

    private static JsonObject EntryAt(JsonObject map, string list) => map[list]![0]!.AsObject();

    private static JsonObject PortalAt(JsonObject map, int index) => map["portals"]![index]!.AsObject();

    private static JsonObject ScheduleAt(JsonObject map) => map["portalSchedule"]![0]!.AsObject();

    /// <summary>A named edit to the full test map and what it should produce.</summary>
    private sealed record SchemaCase(string Name, Action<JsonObject> Mutate, string Expected);
}
