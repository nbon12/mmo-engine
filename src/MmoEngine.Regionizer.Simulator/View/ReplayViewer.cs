using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.View;

/// <summary>One owned section at the viewed tick.</summary>
public readonly record struct FrameSection(int Index, SectionKey Key, long Owner, int Claims);

/// <summary>One portal at the viewed tick.</summary>
public readonly record struct FramePortal(int Id, SectionKey A, SectionKey B, bool Open);

/// <summary>One entity at the viewed tick, with the sections it claims.</summary>
public sealed record FrameEntity(int Id, EntityClass Class, int Layer, double X, double Y, long Region, IReadOnlyList<SectionKey> Claims);

/// <summary>
/// The regionizer's state at the end of one tick: owned sections, portals and entities. This is what the
/// replay viewer draws (spec 014, US7, FR-033).
/// </summary>
public sealed record ReplayFrame(long Tick, int SectionSize, string StateHash, IReadOnlyList<FrameSection> Sections, IReadOnlyList<FramePortal> Portals, IReadOnlyList<FrameEntity> Entities)
{
    /// <summary>Captures the frame from a regionizer.</summary>
    public static ReplayFrame Capture(Regionizer regionizer, long tick, string stateHash)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        var topology = regionizer.Topology;
        var sections = new List<FrameSection>();
        for (var i = 0; i < topology.SectionCount; i++)
        {
            var owner = regionizer.OwnerOf(i);
            if (owner != 0)
            {
                sections.Add(new FrameSection(i, topology.KeyOf(i), owner, regionizer.ClaimCountOf(i)));
            }
        }

        var portals = new List<FramePortal>(topology.PortalCount);
        for (var slot = 0; slot < topology.PortalCount; slot++)
        {
            var spec = topology.PortalAt(slot);
            portals.Add(new FramePortal(spec.PortalId, spec.A, spec.B, regionizer.IsPortalOpen(spec.PortalId)));
        }

        var entities = new List<FrameEntity>();
        foreach (var id in regionizer.EntityIds)
        {
            var e = regionizer.GetEntity(id);
            var claims = new List<SectionKey>(e.ClaimedSections.Length);
            foreach (var index in e.ClaimedSections)
            {
                claims.Add(topology.KeyOf(index));
            }

            entities.Add(new FrameEntity(e.Id, e.Class, e.Layer, e.X, e.Y, e.RegionId, claims));
        }

        return new ReplayFrame(tick, topology.SectionSize, stateHash, sections, portals, entities);
    }
}

/// <summary>A run the viewer can't replay, or a replay that doesn't match its recording (exit 7).</summary>
public sealed class ReplayException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// The replay viewer (spec 014, US7, research R10). It re-runs a recorded run from its manifest's effective
/// configuration and seed, up to the requested tick. It checks that the replay's state hash at that tick
/// equals the recorded one, so the picture shows exactly what the recorded run did, and then draws the
/// tick as SVG.
/// </summary>
public static class ReplayViewer
{
    /// <summary>The exit code when the replay's state hash differs from the recording.</summary>
    public const int Diverged = 7;

    /// <summary>Replays <paramref name="runFolder"/> to <paramref name="tick"/> and returns that tick's frame.</summary>
    public static ReplayFrame Replay(string runFolder, long tick, string? scenariosDirectory = null, string? environmentName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runFolder);
        var manifestPath = Path.Combine(runFolder, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new ReplayException($"{runFolder} has no manifest.json.", ExitCodes.InvalidConfig);
        }

        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            throw new ReplayException($"{manifestPath} is not valid JSON: {ex.Message}", ExitCodes.InvalidConfig);
        }

        if (manifest["parameters"] is not JsonObject parameters)
        {
            throw new ReplayException($"{manifestPath} has no parameters.", ExitCodes.InvalidConfig);
        }

        var recorded = RecordedHash(runFolder, tick)
            ?? throw new ReplayException(string.Create(CultureInfo.InvariantCulture, $"tick {tick} is not in {runFolder}/ticks.csv."), ExitCodes.InvalidConfig);
        var load = RunConfigLoader.Load(parameters.DeepClone().AsObject(), new RunConfigOverrides(DurationTicks: tick + 1), environmentName, scenariosDirectory);
        if (!load.Succeeded)
        {
            throw new ReplayException("The manifest's parameters don't load: " + string.Join("; ", load.Errors), ExitCodes.InvalidConfig);
        }

        ReplayFrame? frame = null;
        var scratch = Path.Combine(Path.GetTempPath(), "regionizer-view", Guid.NewGuid().ToString("N"));
        try
        {
            var result = SimulationRunner.Run(load.Loaded!, scratch, new RunOptions
            {
                GitSha = manifest["gitSha"]?.GetValue<string>(),
                AfterStep = context =>
                {
                    if (context.Tick == tick)
                    {
                        frame = ReplayFrame.Capture(context.Regionizer, tick, "");
                    }
                },
            });
            var replayed = RecordedHash(scratch, tick);
            if (frame is null || replayed is null || result.TicksRun <= tick)
            {
                throw new ReplayException(string.Create(CultureInfo.InvariantCulture, $"the replay stopped before tick {tick}."), Diverged);
            }

            if (!string.Equals(replayed, recorded, StringComparison.Ordinal))
            {
                throw new ReplayException(
                    string.Create(CultureInfo.InvariantCulture, $"the replay's state hash at tick {tick} is {replayed}, but the recording has {recorded}."),
                    Diverged);
            }

            return frame with { StateHash = replayed };
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>The <c>stateHash</c> of a tick in a run folder's ticks.csv, or null.</summary>
    internal static string? RecordedHash(string runFolder, long tick)
    {
        var path = Path.Combine(runFolder, "ticks.csv");
        if (!File.Exists(path))
        {
            return null;
        }

        string[]? header = null;
        var wanted = tick.ToString(CultureInfo.InvariantCulture);
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split(',');
            if (header is null)
            {
                header = fields;
                continue;
            }

            if (fields[Array.IndexOf(header, "tick")] == wanted)
            {
                return fields[Array.IndexOf(header, "stateHash")];
            }
        }

        return null;
    }

    /// <summary>
    /// Draws one layer of a frame as SVG, in metres. Each owned section is filled with its region's colour,
    /// each claimed section is outlined, each portal is a line (green open, red closed), and each entity is
    /// a dot. Elements carry data attributes (region, entity, portal, section) so tests and tools can read
    /// the picture back.
    /// </summary>
    public static string Svg(ReplayFrame frame, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var s = frame.SectionSize;
        var sections = frame.Sections.Where(x => x.Key.Layer == layer).ToList();
        var entities = frame.Entities.Where(e => e.Layer == layer).ToList();
        var minX = sections.Count == 0 ? 0 : sections.Min(x => x.Key.X) * s;
        var minY = sections.Count == 0 ? 0 : sections.Min(x => x.Key.Y) * s;
        var maxX = sections.Count == 0 ? s : (sections.Max(x => x.Key.X) + 1) * s;
        var maxY = sections.Count == 0 ? s : (sections.Max(x => x.Key.Y) + 1) * s;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{minX} {minY} {maxX - minX} {maxY - minY}\" data-tick=\"{frame.Tick}\" data-layer=\"{layer}\" data-state-hash=\"{frame.StateHash}\">\n");
        svg.Append("<g class=\"sections\">\n");
        foreach (var section in sections)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<rect class=\"section\" data-section=\"{section.Index}\" data-region=\"{section.Owner}\" x=\"{section.Key.X * s}\" y=\"{section.Key.Y * s}\" width=\"{s}\" height=\"{s}\" fill=\"{Colour(section.Owner)}\" fill-opacity=\"0.45\"/>\n");
        }

        svg.Append("</g>\n<g class=\"claims\" fill=\"none\" stroke=\"#333\" stroke-width=\"0.5\">\n");
        foreach (var entity in entities)
        {
            foreach (var key in entity.Claims)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<rect class=\"claim\" data-entity=\"{entity.Id}\" data-layer=\"{key.Layer}\" data-x=\"{key.X}\" data-y=\"{key.Y}\" x=\"{key.X * s}\" y=\"{key.Y * s}\" width=\"{s}\" height=\"{s}\"/>\n");
            }
        }

        svg.Append("</g>\n<g class=\"portals\" stroke-width=\"2\">\n");
        foreach (var portal in frame.Portals.Where(p => p.A.Layer == layer || p.B.Layer == layer))
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<line class=\"portal {(portal.Open ? "open" : "closed")}\" data-portal=\"{portal.Id}\" x1=\"{(portal.A.X + 0.5) * s}\" y1=\"{(portal.A.Y + 0.5) * s}\" x2=\"{(portal.B.X + 0.5) * s}\" y2=\"{(portal.B.Y + 0.5) * s}\" stroke=\"{(portal.Open ? "#2a2" : "#c22")}\"/>\n");
        }

        svg.Append("</g>\n<g class=\"entities\">\n");
        foreach (var entity in entities)
        {
            svg.Append(CultureInfo.InvariantCulture,
                $"<circle class=\"entity\" data-entity=\"{entity.Id}\" data-class=\"{entity.Class}\" data-region=\"{entity.Region}\" cx=\"{entity.X:R}\" cy=\"{entity.Y:R}\" r=\"{Math.Max(1, s / 8)}\" fill=\"{Colour(entity.Region)}\"/>\n");
        }

        svg.Append("</g>\n</svg>\n");
        return svg.ToString();
    }

    /// <summary>A stable colour per region: the golden-angle hue of its ID.</summary>
    public static string Colour(long regionId) =>
        regionId == 0 ? "#888888" : string.Create(CultureInfo.InvariantCulture, $"hsl({regionId * 137 % 360},70%,45%)");
}
