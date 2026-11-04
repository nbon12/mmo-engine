using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MmoEngine.Regionizer.Simulator.Cli;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using MmoEngine.Regionizer.Simulator.View;

namespace MmoEngine.Regionizer.Simulator.Tests.View;

/// <summary>US7: the replay viewer shows any tick of a recorded run (spec 014, User Story 7, FR-033).</summary>
public sealed class ReplayViewerTests
{
    private const long ViewTick = 40;

    /// <summary>The regionizer's own state at the end of a tick, read directly from the core.</summary>
    private sealed record Truth(Dictionary<int, long> Owners, Dictionary<int, (double X, double Y, long Region)> Entities);

    /// <summary>Records a run into a folder, keeping the core's state at the end of <see cref="ViewTick"/>.</summary>
    private static (string Folder, Truth Truth) Record(JsonObject config)
    {
        var folder = TestRuns.TempDir();
        Truth? truth = null;
        var result = SimulationRunner.Run(TestRuns.Load(config), folder, new RunOptions
        {
            GitSha = "unknown",
            AfterStep = context =>
            {
                if (context.Tick != ViewTick)
                {
                    return;
                }

                var r = context.Regionizer;
                var owners = new Dictionary<int, long>();
                for (var i = 0; i < r.Topology.SectionCount; i++)
                {
                    if (r.OwnerOf(i) != 0)
                    {
                        owners[i] = r.OwnerOf(i);
                    }
                }

                var entities = new Dictionary<int, (double, double, long)>();
                foreach (var id in r.EntityIds)
                {
                    var e = r.GetEntity(id);
                    if (e.Layer == 0)
                    {
                        entities[id] = (e.X, e.Y, e.RegionId);
                    }
                }

                truth = new Truth(owners, entities);
            },
        });
        Assert.Equal(0, result.ExitCode);
        return (folder, truth!);
    }

    private static XElement Parse(string svg) => XElement.Parse(svg);

    private static IEnumerable<XElement> Elements(XElement svg, string cssClass) =>
        svg.Descendants().Where(e => ((string?)e.Attribute("class"))?.Split(' ').Contains(cssClass) == true);

    /// <summary>
    /// US7 scenario 1: every owned section is drawn with its region's colour, and every entity at its position
    /// at tick t, matching the core's own state at that tick in the recorded run.
    /// </summary>
    [Fact]
    public void EveryOwnedSectionAndEntityIsDrawnAsTheRecordedRunHadThem()
    {
        var (folder, truth) = Record(TestRuns.Small("Baseline", 31, 60));

        var frame = ReplayViewer.Replay(folder, ViewTick, environmentName: "Test");
        var svg = Parse(ReplayViewer.Svg(frame));

        var sections = Elements(svg, "section").ToDictionary(e => (int)e.Attribute("data-section")!, e => e);
        var layerZero = truth.Owners.Where(o => frame.Sections.Any(s => s.Index == o.Key && s.Key.Layer == 0)).ToDictionary();
        Assert.Equal(layerZero.Keys.Order(), sections.Keys.Order());
        foreach (var (index, owner) in layerZero)
        {
            Assert.Equal(owner, (long)sections[index].Attribute("data-region")!);
            Assert.Equal(ReplayViewer.Colour(owner), (string)sections[index].Attribute("fill")!);
        }

        var circles = Elements(svg, "entity").ToDictionary(e => (int)e.Attribute("data-entity")!, e => e);
        Assert.Equal(truth.Entities.Keys.Order(), circles.Keys.Order());
        foreach (var (id, (x, y, region)) in truth.Entities)
        {
            Assert.Equal(x, double.Parse((string)circles[id].Attribute("cx")!, CultureInfo.InvariantCulture));
            Assert.Equal(y, double.Parse((string)circles[id].Attribute("cy")!, CultureInfo.InvariantCulture));
            Assert.Equal(region, (long)circles[id].Attribute("data-region")!);
        }

        Assert.Equal(ViewTick.ToString(CultureInfo.InvariantCulture), (string)svg.Attribute("data-tick")!);
        Assert.Equal(ReplayViewer.RecordedHash(folder, ViewTick), (string)svg.Attribute("data-state-hash")!);
    }

    /// <summary>US7 scenario 2: a closed portal is drawn closed, and no entity's claim spans both of its sides.</summary>
    [Fact]
    public void ClosedPortalIsDrawnClosedAndNoClaimCrossesIt()
    {
        var config = new JsonObject { ["scenario"] = "StackedAreas", ["seed"] = 5, ["durationTicks"] = 60 };
        var (folder, _) = Record(config);

        var frame = ReplayViewer.Replay(folder, ViewTick, environmentName: "Test");

        var closed = frame.Portals.Where(p => !p.Open).ToList();
        Assert.NotEmpty(closed);
        foreach (var layer in closed.SelectMany(p => new[] { p.A.Layer, p.B.Layer }).Distinct())
        {
            var svg = Parse(ReplayViewer.Svg(frame, layer));
            var drawnClosed = Elements(svg, "portal").Where(e => ((string)e.Attribute("class")!).Contains("closed", StringComparison.Ordinal))
                .Select(e => (int)e.Attribute("data-portal")!).ToHashSet();
            var claims = Elements(svg, "claim")
                .GroupBy(e => (int)e.Attribute("data-entity")!)
                .ToDictionary(g => g.Key, g => g.Select(e => new SectionKey((int)e.Attribute("data-layer")!, (int)e.Attribute("data-x")!, (int)e.Attribute("data-y")!)).ToHashSet());
            foreach (var portal in closed.Where(p => p.A.Layer == layer || p.B.Layer == layer))
            {
                Assert.Contains(portal.Id, drawnClosed);
                Assert.DoesNotContain(claims, c => c.Value.Contains(portal.A) && c.Value.Contains(portal.B));
            }
        }
    }

    /// <summary>A recording whose hash doesn't match the replay is refused with exit 7, never drawn.</summary>
    [Fact]
    public void ReplayThatDoesNotMatchTheRecordingIsRefused()
    {
        var (folder, _) = Record(TestRuns.Small("Baseline", 32, 50));
        var path = Path.Combine(folder, "ticks.csv");
        var lines = File.ReadAllLines(path);
        var hash = Array.IndexOf(lines[0].Split(','), "stateHash");
        var row = lines[(int)ViewTick + 1].Split(',');
        row[hash] = "0123456789abcdef";
        lines[(int)ViewTick + 1] = string.Join(',', row);
        File.WriteAllLines(path, lines);

        var error = Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, ViewTick, environmentName: "Test"));

        Assert.Equal(ReplayViewer.Diverged, error.ExitCode);
        Assert.Contains("0123456789abcdef", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRunFoldersAreRefusedWithExitTwo()
    {
        var (folder, _) = Record(TestRuns.Small("Baseline", 33, 45));

        Assert.Equal(ExitCodes.InvalidConfig, Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, 999, environmentName: "Test")).ExitCode);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"formatVersion\":1}");
        Assert.Equal(ExitCodes.InvalidConfig, Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, ViewTick, environmentName: "Test")).ExitCode);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ broken");
        Assert.Equal(ExitCodes.InvalidConfig, Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, ViewTick, environmentName: "Test")).ExitCode);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"parameters\":{\"scenario\":\"Nowhere\"}}");
        Assert.Equal(ExitCodes.InvalidConfig, Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, ViewTick, environmentName: "Test")).ExitCode);
        File.Delete(Path.Combine(folder, "manifest.json"));
        Assert.Equal(ExitCodes.InvalidConfig, Assert.Throws<ReplayException>(() => ReplayViewer.Replay(folder, ViewTick, environmentName: "Test")).ExitCode);
        Assert.Null(ReplayViewer.RecordedHash(TestRuns.TempDir(), 0));
    }

    [Fact]
    public void ViewCommandWritesTheSvgAndReportsIt()
    {
        var (folder, _) = Record(TestRuns.Small("Baseline", 34, 45));
        var output = Path.Combine(TestRuns.TempDir(), "tick.svg");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = Program.Run(["view", folder, "--tick", "40", "--out", output], stdout, stderr, new RunOptions { EnvironmentName = "Test" });
        var bad = Program.Run(["view", TestRuns.TempDir(), "--tick", "1", "--out", output], TextWriter.Null, TextWriter.Null, new RunOptions { EnvironmentName = "Test" });

        Assert.Equal(0, code);
        var report = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(output, report["svg"]!.GetValue<string>());
        Assert.True(report["sections"]!.GetValue<int>() > 0);
        Assert.StartsWith("<svg ", File.ReadAllText(output), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.InvalidConfig, bad);

        // An output path that is a directory can't be written: exit 1.
        var directory = TestRuns.TempDir();
        Directory.CreateDirectory(directory);
        Assert.Equal(ExitCodes.Error, Program.Run(["view", folder, "--tick", "40", "--out", directory], TextWriter.Null, TextWriter.Null, new RunOptions { EnvironmentName = "Test" }));
    }

    [Fact]
    public void EmptyLayerAndUnownedRegionStillRender()
    {
        var frame = new ReplayFrame(0, 16, "h", [], [], [new FrameEntity(1, EntityClass.Infantry, 0, 1, 2, 0, [])]);

        var svg = Parse(ReplayViewer.Svg(frame));

        Assert.Equal("0 0 16 16", (string)svg.Attribute("viewBox")!);
        Assert.Equal("#888888", (string)Elements(svg, "entity").Single().Attribute("fill")!);
    }
}
