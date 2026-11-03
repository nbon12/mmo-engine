using System.Text.Json.Nodes;
using CsCheck;
using MmoEngine.Maps.Tests.Support;
using MmoEngine.Regionizer;
using Xunit;
using static MmoEngine.Maps.Tests.Support.TestMaps;
using CliProgram = MmoEngine.PortalValidator.Cli.Program;

namespace MmoEngine.Maps.Tests;

/// <summary>The portal validator (T054, FR-030, user story 5).</summary>
public sealed class PortalValidatorTests
{
    /// <summary>US5.1: a map with seeded leaks reports exactly those leaks with their locations, and the CLI exits non-zero.</summary>
    [Fact]
    public void SeededLeakMapReportsExactlyTheSeededLeaks()
    {
        var path = TestFiles.Map("test/seeded-leaks.json");
        var map = MapDocument.Load(path);
        var seeded = PortalFindingJson.Deserialize(File.ReadAllText(TestFiles.Map("test/seeded-leaks.expected.json")));

        // The Given: at least four seeded leaks, among them an undeclared walkway between two areas of
        // one layer, an undeclared ramp between layers, and a window opening that is not a portal.
        Assert.True(seeded.Count >= 4, $"Only {seeded.Count} leaks are seeded.");
        Assert.All(seeded, f => Assert.Equal(PortalFindingCode.Leak, f.Code));
        var seededEntries = map.Adjacency.Concat(map.Openings).Where(e => seeded.Any(f => f.Location == e.Location)).ToList();
        Assert.Equal(seeded.Count, seededEntries.Count);
        Assert.Contains(seededEntries, e => e is { Type: MapConnectionType.Adjacency, Kind: "walkway" } && e.A.Layer == e.B.Layer && map.AreaIdAt(e.A) != map.AreaIdAt(e.B));
        Assert.Contains(seededEntries, e => e is { Type: MapConnectionType.Adjacency, Kind: "ramp" } && e.A.Layer != e.B.Layer);
        Assert.Contains(seededEntries, e => e is { Type: MapConnectionType.Opening, Kind: "window" });

        var findings = PortalValidator.Validate(map);
        var (exitCode, stdout) = RunCli(path);

        Assert.Equal(seeded, findings);
        Assert.NotEqual(0, exitCode);
        Assert.Equal(seeded.Select(f => $"{f.Code} {f.Location} {f.Message}"), Lines(stdout));
    }

    /// <summary>US5.2: a clean map reports no findings and the CLI exits 0.</summary>
    [Theory]
    [InlineData("test/clean.json")]
    [InlineData("stacked-base.json")]
    public void CleanMapReportsNothing(string mapFile)
    {
        var path = TestFiles.Map(mapFile);

        var findings = PortalValidator.Validate(MapDocument.Load(path));
        var (exitCode, stdout) = RunCli(path);

        Assert.Empty(findings);
        Assert.Equal(0, exitCode);
        Assert.Empty(stdout);
    }

    /// <summary>US5.3: a portal whose far side is not a navigable section is reported as dead.</summary>
    [Theory]
    [InlineData(1, "endpoint b [0,8,3] is blocked")]
    [InlineData(2, "endpoint b [1,9,9] is outside layer 1")]
    [InlineData(3, "endpoint a [3,1,1] is on layer 3, which does not exist")]
    [InlineData(5, "endpoint a [0,8,10] is blocked; endpoint b [0,8,11] is blocked")]
    public void PortalToNonNavigableSectionIsDead(int portalId, string expectedMessage)
    {
        var map = MapDocument.Load(TestFiles.Map("test/dead-portal.json"));

        var findings = PortalValidator.Validate(map);

        Assert.Equal(
            [new PortalFinding(PortalFindingCode.DeadPortal, $"portals:{portalId}", expectedMessage)],
            findings.Where(f => f.Location == $"portals:{portalId}"));
    }

    [Fact]
    public void PortalBetweenTwoNavigableSectionsIsNotDead()
    {
        var map = MapDocument.Load(TestFiles.Map("test/dead-portal.json"));

        var findings = PortalValidator.Validate(map);

        Assert.All(findings, f => Assert.Equal(PortalFindingCode.DeadPortal, f.Code));
        Assert.Equal(["portals:1", "portals:2", "portals:3", "portals:5"], findings.Select(f => f.Location));
    }

    /// <summary>US5.4: a portal that declares no closed-state policy, or only part of one, is reported.</summary>
    [Theory]
    [InlineData(1, "has no closedPolicy")]
    [InlineData(5, "has no closedPolicy")]
    [InlineData(2, "closedPolicy lacks deferredProjectiles")]
    [InlineData(6, "closedPolicy lacks entityTransfers")]
    [InlineData(3, "closedPolicy lacks entityTransfers, doorAndObjectiveEvents, deferredProjectiles")]
    public void PortalWithoutClosedPolicyIsReported(int portalId, string expectedMessage)
    {
        var map = MapDocument.Load(TestFiles.Map("test/missing-policy.json"));

        var findings = PortalValidator.Validate(map);

        Assert.Equal(
            [new PortalFinding(PortalFindingCode.MissingPolicy, $"portals:{portalId}", expectedMessage)],
            findings.Where(f => f.Location == $"portals:{portalId}"));
    }

    [Fact]
    public void PortalWithACompleteClosedPolicyIsNotReported()
    {
        var map = MapDocument.Load(TestFiles.Map("test/missing-policy.json"));

        var findings = PortalValidator.Validate(map);

        Assert.All(findings, f => Assert.Equal(PortalFindingCode.MissingPolicy, f.Code));
        Assert.Equal(["portals:1", "portals:2", "portals:3", "portals:5", "portals:6"], findings.Select(f => f.Location));
    }

    /// <summary>
    /// US5.5: a sealed area that navigable adjacency still reaches with all portals closed is
    /// reported, naming the area and the first foreign section reached. The crypt's door is declared
    /// open, but the walk closes every portal, so the crypt is sealed.
    /// </summary>
    [Fact]
    public void SealedAreaReachableWithPortalsClosedIsReported()
    {
        var map = MapDocument.Load(TestFiles.Map("test/unsealed.json"));

        var findings = PortalValidator.Validate(map);

        Assert.Equal(
            [
                new PortalFinding(
                    PortalFindingCode.SealingFailure,
                    "sealedAreas:vault",
                    "with all portals closed, sealed area vault reaches [0,10,7] (open) through openings:arrow-slit"),
            ],
            findings.Where(f => f.Code == PortalFindingCode.SealingFailure));
        Assert.Equal(
            ["openings:vault-vent", "openings:arrow-slit"],
            findings.Where(f => f.Code == PortalFindingCode.Leak).Select(f => f.Location));
    }

    [Fact]
    public void SealingWalkFollowsUndeclaredEntriesThroughOtherLayersOfTheSameArea()
    {
        // The vault spans layers 1 and 2. Undeclared stairs join them, and a hatch on layer 2 opens
        // onto the surface: the walk from layer 1 crosses the stairs, then floods layer 2 to the hatch.
        var map = Map(Layer(0, 0, 0, 16, 16, "open"), Layer(1, 0, 0, 4, 4, "vault"), Layer(2, 8, 8, 4, 4, "vault"));
        map["sealedAreas"] = new JsonArray("vault");
        map["adjacency"] = new JsonArray(Entry("stairs", "stairs", Key(1, 3, 3), Key(2, 8, 8)));
        map["openings"] = new JsonArray(Entry("hatch", "hatch", Key(2, 11, 11), Key(0, 11, 12)));

        var findings = PortalValidator.Validate(Parse(map));

        Assert.Equal(
            [
                new PortalFinding(PortalFindingCode.Leak, "adjacency:stairs", "stairs from [1,3,3] (vault) to [2,8,8] (vault) crosses a layer boundary and matches no portal"),
                new PortalFinding(PortalFindingCode.Leak, "openings:hatch", "hatch from [2,11,11] (vault) to [0,11,12] (open) crosses layer and area boundaries and matches no portal"),
                new PortalFinding(PortalFindingCode.SealingFailure, "sealedAreas:vault", "with all portals closed, sealed area vault reaches [0,11,12] (open) through openings:hatch"),
            ],
            findings);
    }

    [Theory]
    [InlineData("adjacency", false)]
    [InlineData("adjacency", true)]
    [InlineData("openings", false)]
    [InlineData("openings", true)]
    public void EntryWhoseEndpointsMatchAPortalInEitherOrderIsThatPortal(string list, bool reversed)
    {
        // A cross-layer, cross-area entry into a sealed cellar, declared as a closed portal.
        var map = Map(Layer(0, 0, 0, 8, 8, "open"), Layer(1, 0, 0, 4, 4, "cellar"));
        map["sealedAreas"] = new JsonArray("cellar");
        map[list] = new JsonArray(Entry("door", "door", Key(0, 2, 2), Key(1, 2, 2)));
        map["portals"] = new JsonArray(reversed ? Portal(7, Key(1, 2, 2), Key(0, 2, 2)) : Portal(7, Key(0, 2, 2), Key(1, 2, 2)));

        Assert.Empty(PortalValidator.Validate(Parse(map)));
    }

    [Theory]
    [InlineData("adjacency", "walkway", 0, 1, 1, 0, 3, 3, "walkway from [0,1,1] (open) to [0,3,3] (courtyard) crosses an area boundary and matches no portal")]
    [InlineData("adjacency", null, 0, 1, 1, 1, 1, 1, "adjacency from [0,1,1] (open) to [1,1,1] (open) crosses a layer boundary and matches no portal")]
    [InlineData("openings", null, 2, 1, 1, 0, 1, 1, "opening from [2,1,1] (cellar) to [0,1,1] (open) crosses layer and area boundaries and matches no portal")]
    [InlineData("openings", "window", 0, 3, 3, 0, 5, 3, "window from [0,3,3] (courtyard) to [0,5,3] (open) crosses an area boundary and matches no portal")]
    public void CrossingEntryThatMatchesNoPortalIsALeak(string list, string? kind, int la, int xa, int ya, int lb, int xb, int yb, string expectedMessage)
    {
        // Layer 0 is open with a courtyard [2,2]–[4,4]; layer 1 is open too; layer 2 is a cellar.
        var surface = Layer(0, 0, 0, 8, 8, "open");
        surface["areas"] = new JsonArray(Area("courtyard", Rect(2, 2, 4, 4)));
        var map = Map(surface, Layer(1, 0, 0, 8, 8, "open"), Layer(2, 0, 0, 4, 4, "cellar"));
        map[list] = new JsonArray(Entry(null, kind, Key(la, xa, ya), Key(lb, xb, yb)));

        var findings = PortalValidator.Validate(Parse(map));

        Assert.Equal([new PortalFinding(PortalFindingCode.Leak, $"{list}[0]", expectedMessage)], findings);
    }

    [Fact]
    public void LeaksAreExactlyTheCrossingEntriesThatNoPortalDeclares()
    {
        // Layer 0 is 8 × 4: "west" (x 0–3, the default) and "east" (x 4–7). Layer 1 is 8 × 4 "under".
        // Each generated entry is built inside one area or across a boundary, and may be declared as
        // a portal in either order. Its expected leak follows from how it was built.
        var entries = Gen.UInt.Array[1, 12].Select(values => values.Select(GeneratedEntry.From).ToArray());

        Property.ForAll(entries, seed: 20261103, iterations: 400, generated =>
        {
            var surface = Layer(0, 0, 0, 8, 4, "west");
            surface["areas"] = new JsonArray(Area("east", Rect(4, 0, 7, 3)));
            var map = Map(surface, Layer(1, 0, 0, 8, 4, "under"));
            var adjacency = new JsonArray();
            var openings = new JsonArray();
            var portals = new JsonArray();
            var locations = new string[generated.Length];
            for (var i = 0; i < generated.Length; i++)
            {
                var e = generated[i];
                var list = e.IsOpening ? openings : adjacency;
                var name = e.IsOpening ? "openings" : "adjacency";
                locations[i] = e.HasId ? $"{name}:e{i}" : $"{name}[{list.Count}]";
                list.Add(Entry(e.HasId ? $"e{i}" : null, "link", Key(e.A.Layer, e.A.X, e.A.Y), Key(e.B.Layer, e.B.X, e.B.Y)));
                if (e.Declared)
                {
                    var (a, b) = e.PortalReversed ? (e.B, e.A) : (e.A, e.B);
                    portals.Add(Portal(i + 1, Key(a.Layer, a.X, a.Y), Key(b.Layer, b.X, b.Y)));
                }
            }

            map["adjacency"] = adjacency;
            map["openings"] = openings;
            map["portals"] = portals;
            var declared = generated.Where(e => e.Declared).SelectMany(e => new[] { (e.A, e.B), (e.B, e.A) }).ToHashSet();
            var expected = Enumerable.Range(0, generated.Length)
                .Where(i => generated[i].Crossing && !declared.Contains((generated[i].A, generated[i].B)))
                .OrderBy(i => generated[i].IsOpening)
                .Select(i => locations[i]);

            var findings = PortalValidator.Validate(Parse(map));

            Assert.All(findings, f => Assert.Equal(PortalFindingCode.Leak, f.Code));
            Assert.Equal(expected, findings.Select(f => f.Location));
        });
    }

    private static (int ExitCode, string Stdout) RunCli(string path)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = CliProgram.Run([path], stdout, stderr);
        return (exitCode, stdout.ToString());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>An entry decoded from 32 random bits: where its endpoints lie, its list, and whether a portal declares it.</summary>
    private sealed record GeneratedEntry(SectionKey A, SectionKey B, bool Crossing, bool IsOpening, bool HasId, bool Declared, bool PortalReversed)
    {
        public static GeneratedEntry From(uint bits)
        {
            int Field(int shift, int range) => (int)((bits >> shift) % (uint)range);
            var (x1, y1, x2, y2) = (Field(2, 4), Field(4, 4), Field(6, 4), Field(8, 4));
            var portalMode = Field(17, 3);
            var (a, b, crossing) = Field(0, 4) switch
            {
                0 => (new SectionKey(0, x1, y1), new SectionKey(0, x2 == x1 && y2 == y1 ? (x2 + 1) % 4 : x2, y2), false),
                1 => (new SectionKey(0, 4 + x1, y1), new SectionKey(0, 4 + (x2 == x1 && y2 == y1 ? (x2 + 1) % 4 : x2), y2), false),
                2 => (new SectionKey(0, x1, y1), new SectionKey(0, 4 + x2, y2), true),
                _ => (new SectionKey(0, x1 + (4 * Field(14, 2)), y1), new SectionKey(1, x2 + (4 * Field(15, 2)), y2), true),
            };
            var swap = Field(16, 2) == 1;
            return new GeneratedEntry(swap ? b : a, swap ? a : b, crossing, Field(10, 2) == 1, Field(11, 2) == 1, portalMode != 0, portalMode == 2);
        }
    }
}
