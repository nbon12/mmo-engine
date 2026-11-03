using System.Text.Json.Nodes;

namespace MmoEngine.Maps.Tests.Support;

/// <summary>Map JSON built from explicit values, for tests. It declares data only and holds no validation rules.</summary>
internal static class TestMaps
{
    /// <summary>A map named "test" with 16 m sections and the given layers.</summary>
    public static JsonObject Map(params JsonObject[] layers) => new()
    {
        ["formatVersion"] = 1,
        ["name"] = "test",
        ["sectionSize"] = 16,
        ["layers"] = new JsonArray(layers),
    };

    public static JsonObject Layer(int id, int originX, int originY, int width, int height, string defaultArea) => new()
    {
        ["id"] = id,
        ["originX"] = originX,
        ["originY"] = originY,
        ["width"] = width,
        ["height"] = height,
        ["defaultArea"] = defaultArea,
    };

    public static JsonObject Area(string tag, params JsonArray[] rects) => new()
    {
        ["tag"] = tag,
        ["rects"] = new JsonArray(rects),
    };

    public static JsonArray Rect(int x0, int y0, int x1, int y1) => new(x0, y0, x1, y1);

    public static JsonArray Key(int layer, int x, int y) => new(layer, x, y);

    /// <summary>An adjacency or openings entry; a null ID or kind is left out.</summary>
    public static JsonObject Entry(string? id, string? kind, JsonArray a, JsonArray b)
    {
        var entry = new JsonObject();
        if (id is not null)
        {
            entry["id"] = id;
        }

        if (kind is not null)
        {
            entry["kind"] = kind;
        }

        entry["a"] = a;
        entry["b"] = b;
        return entry;
    }

    /// <summary>A closed portal with all three closed-state policy fields.</summary>
    public static JsonObject Portal(int id, JsonArray a, JsonArray b) => new()
    {
        ["id"] = id,
        ["a"] = a,
        ["b"] = b,
        ["open"] = false,
        ["closedPolicy"] = new JsonObject
        {
            ["entityTransfers"] = false,
            ["doorAndObjectiveEvents"] = true,
            ["deferredProjectiles"] = false,
        },
    };

    public static JsonObject Schedule(int portal, double everySeconds, double openSeconds) => new()
    {
        ["portal"] = portal,
        ["everySeconds"] = everySeconds,
        ["openSeconds"] = openSeconds,
    };

    /// <summary>
    /// A valid map that uses every field: layer 0 is 16 × 16 "open" sections with a "courtyard"
    /// [2,2]–[5,5] and a blocked wall [8,0]–[8,3]; layer 1 is a 4 × 4 sealed "cellar". Stairs
    /// (portal 1) join them, and a window (portal 2) joins the courtyard to the open area.
    /// </summary>
    public static JsonObject Full()
    {
        var surface = Layer(0, 0, 0, 16, 16, "open");
        surface["areas"] = new JsonArray(Area("courtyard", Rect(2, 2, 5, 5)));
        surface["blocked"] = new JsonArray(Rect(8, 0, 8, 3));
        var map = Map(surface, Layer(1, 0, 0, 4, 4, "cellar"));
        map["sealedAreas"] = new JsonArray("cellar");
        map["adjacency"] = new JsonArray(Entry("stairs", "stairs", Key(0, 1, 1), Key(1, 1, 1)));
        map["openings"] = new JsonArray(Entry("window", "window", Key(0, 5, 3), Key(0, 6, 3)));
        map["portals"] = new JsonArray(Portal(1, Key(0, 1, 1), Key(1, 1, 1)), Portal(2, Key(0, 5, 3), Key(0, 6, 3)));
        map["portalSchedule"] = new JsonArray(Schedule(1, 60, 20));
        return map;
    }

    public static MapDocument Parse(JsonObject map) => MapDocument.Parse(map.ToJsonString());
}
