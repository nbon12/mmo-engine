using System.Text.Json;
using MmoEngine.Regionizer;

namespace MmoEngine.Maps;

/// <summary>
/// Reads map JSON into a <see cref="MapDocument"/> and runs the schema checks of
/// contracts/map-format.md. Map files are untrusted input: every field is checked, unknown fields
/// are rejected, and the work stays linear in the file size plus the number of sections.
/// </summary>
internal static class MapReader
{
    /// <summary>The largest section size the simulator accepts (data-model.md).</summary>
    private const int MaxSectionSize = 1024;

    /// <summary>A bound on the sections over all layers, so a hostile file can't exhaust memory.</summary>
    private const long MaxSections = 1L << 24;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static MapDocument Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new MapFormatException($"invalid map JSON: {ex.Message}", ex);
        }

        MapJson map;
        using (document)
        {
            CheckFormatVersion(document.RootElement);
            try
            {
                map = document.RootElement.Deserialize<MapJson>(Options)!;
            }
            catch (JsonException ex)
            {
                var line = ex.LineNumber is { } l ? $" (line {l + 1})" : string.Empty;
                throw new MapFormatException($"{FieldPath(ex.Path)}: has the wrong type of value{line}", ex);
            }
        }

        return Build(map);
    }

    /// <summary>A section key as findings and errors print it: <c>[layer,x,y]</c>.</summary>
    internal static string Format(SectionKey key) => $"[{key.Layer},{key.X},{key.Y}]";

    private static string Format(MapRect rect) => $"[{rect.X0},{rect.Y0},{rect.X1},{rect.Y1}]";

    /// <summary>The version is checked before anything else, so a newer file fails on its version rather than on a new field.</summary>
    private static void CheckFormatVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new MapFormatException($"a map must be a JSON object, was {root.ValueKind}");
        }

        if (!root.TryGetProperty("formatVersion", out var version))
        {
            throw Error("formatVersion", "is required");
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value))
        {
            throw Error("formatVersion", $"must be an integer, was {version.GetRawText()}");
        }

        if (value != MapDocument.SupportedFormatVersion)
        {
            throw Error("formatVersion", $"unsupported version {value}; this reader supports version {MapDocument.SupportedFormatVersion}");
        }
    }

    private static MapDocument Build(MapJson map)
    {
        RejectUnknown(map.Unknown, null);
        var name = RequireText(map.Name, "name");
        var sectionSize = Require(map.SectionSize, "sectionSize");
        if (sectionSize < 1 || sectionSize > MaxSectionSize || (sectionSize & (sectionSize - 1)) != 0)
        {
            throw Error("sectionSize", $"must be a power of two from 1 to {MaxSectionSize}, was {sectionSize}");
        }

        if (map.Layers is null || map.Layers.Count == 0)
        {
            throw Error("layers", "at least one layer is required");
        }

        var headers = ReadLayerHeaders(map.Layers, out var tags);
        if (tags.Count > ushort.MaxValue + 1)
        {
            throw Error("layers", $"at most {ushort.MaxValue + 1} area tags are allowed, found {tags.Count}");
        }

        var areaNames = tags.ToArray();
        var areaIds = new Dictionary<string, ushort>(areaNames.Length, StringComparer.Ordinal);
        for (var i = 0; i < areaNames.Length; i++)
        {
            areaIds.Add(areaNames[i], (ushort)i);
        }

        var layers = new MapLayer[headers.Count];
        for (var i = 0; i < layers.Length; i++)
        {
            layers[i] = BuildLayer(headers[i], areaIds);
        }

        var layerById = layers.ToDictionary(l => l.Id);
        var sealedAreas = ReadSealedAreas(map.SealedAreas, areaIds);
        var adjacency = ReadConnections(map.Adjacency, MapConnectionType.Adjacency, "adjacency", layerById);
        var openings = ReadConnections(map.Openings, MapConnectionType.Opening, "openings", layerById);
        var portals = ReadPortals(map.Portals);
        var schedule = ReadSchedule(map.PortalSchedule, portals);
        return new MapDocument(name, sectionSize, layers, areaNames, sealedAreas, adjacency, openings, portals, schedule);
    }

    private static List<LayerHeader> ReadLayerHeaders(List<LayerJson?> layers, out SortedSet<string> tags)
    {
        tags = new SortedSet<string>(StringComparer.Ordinal);
        var headers = new List<LayerHeader>(layers.Count);
        var ids = new HashSet<int>();
        long sections = 0;
        for (var i = 0; i < layers.Count; i++)
        {
            var path = $"layers[{i}]";
            var layer = layers[i] ?? throw Error(path, "must be an object");
            RejectUnknown(layer.Unknown, path);
            var id = Require(layer.Id, $"{path}.id");
            if (!ids.Add(id))
            {
                throw Error($"{path}.id", $"duplicate layer id {id}");
            }

            var originX = layer.OriginX ?? 0;
            var originY = layer.OriginY ?? 0;
            var width = Require(layer.Width, $"{path}.width");
            var height = Require(layer.Height, $"{path}.height");
            CheckExtent(width, originX, $"{path}.width");
            CheckExtent(height, originY, $"{path}.height");
            sections += (long)width * height;
            if (sections > MaxSections)
            {
                throw Error(path, $"the map has more than {MaxSections} sections");
            }

            var defaultArea = RequireToken(layer.DefaultArea, $"{path}.defaultArea");
            tags.Add(defaultArea);
            var bounds = new MapRect(originX, originY, originX + width - 1, originY + height - 1);
            var areas = new MapArea[layer.Areas?.Count ?? 0];
            for (var j = 0; j < areas.Length; j++)
            {
                var areaPath = $"{path}.areas[{j}]";
                var area = layer.Areas![j] ?? throw Error(areaPath, "must be an object");
                RejectUnknown(area.Unknown, areaPath);
                var tag = RequireToken(area.Tag, $"{areaPath}.tag");
                if (area.Rects is null || area.Rects.Count == 0)
                {
                    throw Error($"{areaPath}.rects", "at least one rectangle is required");
                }

                var rects = new MapRect[area.Rects.Count];
                for (var k = 0; k < rects.Length; k++)
                {
                    rects[k] = ReadRect(area.Rects[k], $"{areaPath}.rects[{k}]", bounds, id);
                }

                tags.Add(tag);
                areas[j] = new MapArea(tag, rects);
            }

            var blocked = new MapRect[layer.Blocked?.Count ?? 0];
            for (var k = 0; k < blocked.Length; k++)
            {
                blocked[k] = ReadRect(layer.Blocked![k], $"{path}.blocked[{k}]", bounds, id);
            }

            headers.Add(new LayerHeader(id, layer.Name, originX, originY, width, height, defaultArea, areas, blocked, path));
        }

        return headers;
    }

    private static void CheckExtent(int size, int origin, string path)
    {
        if (size < 1)
        {
            throw Error(path, $"must be at least 1, was {size}");
        }

        if ((long)origin + size - 1 > int.MaxValue)
        {
            throw Error(path, "the layer extends past the largest section coordinate");
        }
    }

    private static MapRect ReadRect(int[]? values, string path, MapRect bounds, int layerId)
    {
        if (values is null || values.Length != 4)
        {
            throw Error(path, "must be [x0, y0, x1, y1]");
        }

        var rect = new MapRect(values[0], values[1], values[2], values[3]);
        if (rect.X1 < rect.X0 || rect.Y1 < rect.Y0)
        {
            throw Error(path, $"x1 and y1 must not be less than x0 and y0, was {Format(rect)}");
        }

        if (!bounds.Contains(rect.X0, rect.Y0) || !bounds.Contains(rect.X1, rect.Y1))
        {
            throw Error(path, $"{Format(rect)} is not inside layer {layerId}, which covers {Format(bounds)}");
        }

        return rect;
    }

    /// <summary>
    /// Paints the default area, then each area rectangle. A section that two rectangles cover is an
    /// error, found at the first shared section, so painting never exceeds the layer's size.
    /// </summary>
    private static MapLayer BuildLayer(LayerHeader header, Dictionary<string, ushort> areaIds)
    {
        var width = header.Width;
        var ids = new ushort[width * header.Height];
        Array.Fill(ids, areaIds[header.DefaultArea]);
        var paintedBy = new int[ids.Length];
        var painters = new List<(int Area, int Rect)>();
        for (var j = 0; j < header.Areas.Length; j++)
        {
            var area = header.Areas[j];
            var tag = areaIds[area.Tag];
            for (var k = 0; k < area.Rects.Count; k++)
            {
                painters.Add((j, k));
                var rect = area.Rects[k];
                for (var y = rect.Y0; y <= rect.Y1; y++)
                {
                    for (var x = rect.X0; x <= rect.X1; x++)
                    {
                        var local = ((y - header.OriginY) * width) + (x - header.OriginX);
                        if (paintedBy[local] != 0)
                        {
                            var (otherArea, otherRect) = painters[paintedBy[local] - 1];
                            throw Error(
                                $"{header.Path}.areas[{j}].rects[{k}]",
                                $"overlaps {header.Path}.areas[{otherArea}].rects[{otherRect}] at section {Format(new SectionKey(header.Id, x, y))}");
                        }

                        paintedBy[local] = painters.Count;
                        ids[local] = tag;
                    }
                }
            }
        }

        return new MapLayer(
            header.Id,
            header.Name,
            header.OriginX,
            header.OriginY,
            header.Width,
            header.Height,
            header.DefaultArea,
            header.Areas,
            header.Blocked,
            ids,
            BlockedCells(header));
    }

    /// <summary>
    /// Marks blocked sections with a two-dimensional difference array, so the cost is linear in the
    /// rectangles plus the sections however much the blocked rectangles overlap.
    /// </summary>
    private static bool[] BlockedCells(LayerHeader header)
    {
        var width = header.Width;
        var height = header.Height;
        var cells = new bool[width * height];
        if (header.Blocked.Length == 0)
        {
            return cells;
        }

        var stride = width + 1;
        var counts = new int[stride * (height + 1)];
        foreach (var rect in header.Blocked)
        {
            var x0 = rect.X0 - header.OriginX;
            var y0 = rect.Y0 - header.OriginY;
            var x1 = rect.X1 - header.OriginX + 1;
            var y1 = rect.Y1 - header.OriginY + 1;
            counts[(y0 * stride) + x0]++;
            counts[(y0 * stride) + x1]--;
            counts[(y1 * stride) + x0]--;
            counts[(y1 * stride) + x1]++;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + x;
                var left = x > 0 ? counts[i - 1] : 0;
                var below = y > 0 ? counts[i - stride] : 0;
                var corner = x > 0 && y > 0 ? counts[i - stride - 1] : 0;
                counts[i] += left + below - corner;
                cells[(y * width) + x] = counts[i] > 0;
            }
        }

        return cells;
    }

    private static string[] ReadSealedAreas(List<string?>? sealedAreas, Dictionary<string, ushort> areaIds)
    {
        if (sealedAreas is null)
        {
            return [];
        }

        var result = new string[sealedAreas.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            var path = $"sealedAreas[{i}]";
            var tag = RequireToken(sealedAreas[i], path);
            if (!areaIds.ContainsKey(tag))
            {
                throw Error(path, $"no layer uses area {tag}");
            }

            if (!seen.Add(tag))
            {
                throw Error(path, $"duplicate sealed area {tag}");
            }

            result[i] = tag;
        }

        return result;
    }

    private static MapConnection[] ReadConnections(
        List<ConnectionJson?>? entries,
        MapConnectionType type,
        string list,
        Dictionary<int, MapLayer> layers)
    {
        if (entries is null)
        {
            return [];
        }

        var result = new MapConnection[entries.Count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < result.Length; i++)
        {
            var path = $"{list}[{i}]";
            var entry = entries[i] ?? throw Error(path, "must be an object");
            RejectUnknown(entry.Unknown, path);
            string? id = null;
            if (entry.Id is not null)
            {
                id = RequireToken(entry.Id, $"{path}.id");
                if (!ids.Add(id))
                {
                    throw Error($"{path}.id", $"duplicate id {id}");
                }
            }

            var kind = entry.Kind is null ? null : RequireText(entry.Kind, $"{path}.kind");
            var a = ReadSectionInLayer(entry.A, $"{path}.a", layers);
            var b = ReadSectionInLayer(entry.B, $"{path}.b", layers);
            if (a == b)
            {
                throw Error(path, $"a and b are the same section {Format(a)}");
            }

            result[i] = new MapConnection(type, i, id, kind, a, b);
        }

        return result;
    }

    /// <summary>Portal endpoints are not checked against the layers here: one outside its layer is a DeadPortal finding.</summary>
    private static MapPortal[] ReadPortals(List<PortalJson?>? entries)
    {
        if (entries is null)
        {
            return [];
        }

        var result = new MapPortal[entries.Count];
        var ids = new HashSet<int>();
        for (var i = 0; i < result.Length; i++)
        {
            var path = $"portals[{i}]";
            var entry = entries[i] ?? throw Error(path, "must be an object");
            RejectUnknown(entry.Unknown, path);
            var id = Require(entry.Id, $"{path}.id");
            if (!ids.Add(id))
            {
                throw Error($"{path}.id", $"duplicate portal id {id}");
            }

            var a = ReadSection(entry.A, $"{path}.a");
            var b = ReadSection(entry.B, $"{path}.b");
            if (a == b)
            {
                throw Error(path, $"a and b are the same section {Format(a)}");
            }

            ClosedPolicy? policy = null;
            if (entry.ClosedPolicy is { } p)
            {
                RejectUnknown(p.Unknown, $"{path}.closedPolicy");
                policy = new ClosedPolicy(p.EntityTransfers, p.DoorAndObjectiveEvents, p.DeferredProjectiles);
            }

            result[i] = new MapPortal(id, entry.Name, a, b, entry.Open ?? false, policy);
        }

        return result;
    }

    private static PortalScheduleEntry[] ReadSchedule(List<ScheduleJson?>? entries, MapPortal[] portals)
    {
        if (entries is null)
        {
            return [];
        }

        var portalIds = portals.Select(p => p.Id).ToHashSet();
        var scheduled = new HashSet<int>();
        var result = new PortalScheduleEntry[entries.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var path = $"portalSchedule[{i}]";
            var entry = entries[i] ?? throw Error(path, "must be an object");
            RejectUnknown(entry.Unknown, path);
            var portal = Require(entry.Portal, $"{path}.portal");
            if (!portalIds.Contains(portal))
            {
                throw Error($"{path}.portal", $"no portal has id {portal}");
            }

            if (!scheduled.Add(portal))
            {
                throw Error($"{path}.portal", $"portal {portal} already has a schedule");
            }

            var every = Require(entry.EverySeconds, $"{path}.everySeconds");
            if (!double.IsFinite(every) || every <= 0)
            {
                throw Error($"{path}.everySeconds", $"must be greater than 0, was {every}");
            }

            var open = Require(entry.OpenSeconds, $"{path}.openSeconds");
            if (!double.IsFinite(open) || open < 0 || open > every)
            {
                throw Error($"{path}.openSeconds", $"must be from 0 to everySeconds ({every}), was {open}");
            }

            result[i] = new PortalScheduleEntry(portal, every, open);
        }

        return result;
    }

    private static SectionKey ReadSection(int[]? values, string path)
    {
        if (values is null)
        {
            throw Error(path, "is required");
        }

        if (values.Length != 3)
        {
            throw Error(path, "must be [layer, x, y]");
        }

        return new SectionKey(values[0], values[1], values[2]);
    }

    private static SectionKey ReadSectionInLayer(int[]? values, string path, Dictionary<int, MapLayer> layers)
    {
        var key = ReadSection(values, path);
        if (!layers.TryGetValue(key.Layer, out var layer))
        {
            throw Error(path, $"section {Format(key)} is on layer {key.Layer}, which does not exist");
        }

        if (!layer.Contains(key.X, key.Y))
        {
            throw Error(path, $"section {Format(key)} is outside layer {key.Layer}");
        }

        return key;
    }

    private static T Require<T>(T? value, string path)
        where T : struct => value ?? throw Error(path, "is required");

    private static string RequireText(string? value, string path)
    {
        if (value is null)
        {
            throw Error(path, "is required");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error(path, "must not be blank");
        }

        return value;
    }

    /// <summary>An area tag or entry ID: findings print it as a single token, so it may not contain whitespace.</summary>
    private static string RequireToken(string? value, string path)
    {
        var text = RequireText(value, path);
        if (text.Any(char.IsWhiteSpace))
        {
            throw Error(path, $"must not contain whitespace, was \"{text}\"");
        }

        return text;
    }

    /// <summary>A field the format doesn't define is an error, so a misspelt field never passes silently.</summary>
    private static void RejectUnknown(Dictionary<string, JsonElement>? unknown, string? path)
    {
        if (unknown is { Count: > 0 })
        {
            var field = unknown.Keys.First();
            throw Error(path is null ? field : $"{path}.{field}", $"is not a field of map format version {MapDocument.SupportedFormatVersion}");
        }
    }

    /// <summary>A serializer path such as <c>$.layers[0].width</c> as the reader's errors write it: <c>layers[0].width</c>.</summary>
    private static string FieldPath(string? jsonPath) =>
        jsonPath is null || jsonPath == "$" ? "map" : jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath;

    private static MapFormatException Error(string path, string message) => new($"{path}: {message}");

    private sealed record LayerHeader(
        int Id,
        string? Name,
        int OriginX,
        int OriginY,
        int Width,
        int Height,
        string DefaultArea,
        MapArea[] Areas,
        MapRect[] Blocked,
        string Path);
}
