using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using SimulatorCli = MmoEngine.Regionizer.Simulator.Cli.Program;

namespace MmoEngine.Regionizer.Simulator.Tests.Scenarios;

/// <summary>Every scenario runs from its defaults (US4, FR-023), the air layer (US4.5) and the stacked base's doors.</summary>
public sealed class ScenarioTests
{
    private const ulong Seed = 20261103;

    public static TheoryData<string> AllScenarios => [.. ScenarioNames.All];

    /// <summary>
    /// US4.1: Given each of the nine scenarios, When it runs with its defaults for a short duration
    /// (and a reduced population), Then it ends with zero invariant violations and writes its manifest,
    /// metrics and summary.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void EachScenarioRunsCleanAndWritesOutput(string scenario)
    {
        const long Ticks = 60;
        var loaded = TestRuns.Load(ScenarioRuns.Short(scenario, Seed, Ticks, population: 150));

        var result = TestRuns.Run(loaded);

        Assert.True(result.FirstViolation is null, $"{scenario} seed {Seed}: {result.FirstViolation}");
        Assert.True(result.Completed && result.ExitCode == ExitCodes.Clean && result.TicksRun == Ticks, $"{scenario} seed {Seed}: ran {result.TicksRun} ticks, exit {result.ExitCode}");
        var folder = result.OutputDirectory;
        Assert.False(File.Exists(Path.Combine(folder, "_incomplete")), $"{scenario} seed {Seed}: the run folder is still marked incomplete");
        Assert.True(File.Exists(Path.Combine(folder, "events.csv")), $"{scenario} seed {Seed}: no events.csv");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")))!;
        Assert.Equal(scenario, manifest["scenario"]!.GetValue<string>());
        Assert.Equal(Seed, manifest["seed"]!.GetValue<ulong>());
        var summary = JsonNode.Parse(File.ReadAllText(result.SummaryPath))!;
        Assert.True(summary["completed"]!.GetValue<bool>(), $"{scenario} seed {Seed}: summary says incomplete");
        Assert.Equal(Ticks, summary["ticks"]!.GetValue<long>());
        var every = loaded.Config.Metrics.MetricsEveryTicks;
        var rows = TestRuns.CsvRows(Path.Combine(folder, "ticks.csv"));
        Assert.Equal(((Ticks - 1) / every) + 1, rows.Count);
    }

    /// <summary>US4.6: Given each scenario, When it runs twice with the same seed for a short duration, Then the per-tick state hashes are identical (SC-001).</summary>
    [Theory]
    [MemberData(nameof(AllScenarios))]
    public void EachScenarioRerunGivesIdenticalHashes(string scenario)
    {
        const long Ticks = 45;
        var loaded = TestRuns.Load(ScenarioRuns.Short(scenario, Seed, Ticks, population: 120));

        var first = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true });
        var rerun = TestRuns.Run(loaded, new RunOptions { KeepTickHashes = true });

        Assert.True(first.Completed && rerun.Completed, $"{scenario} seed {Seed}: {first.FirstViolation ?? rerun.FirstViolation}");
        Assert.Equal(Ticks, first.TickHashes.Count);
        Assert.True(first.TickHashes.SequenceEqual(rerun.TickHashes), $"{scenario} seed {Seed}: rerun hashes differ");
        Assert.Equal(first.FinalHash, rerun.FinalHash);
    }

    /// <summary>
    /// US4.5: Given the Air traffic scenario with and without the air layer, When each runs, Then the
    /// run with the air layer has aircraft above the altitude threshold in their own section layer, and
    /// both runs record more than zero merges caused by aircraft.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AirLayerPutsHighAircraftOnTheirOwnLayer(bool enabled)
    {
        var loaded = TestRuns.Load(TestRuns.Small("AirTraffic", Seed, 240, population: 240).With("airLayer.enabled", enabled));
        var threshold = loaded.Config.AirLayer.AltitudeMetres;
        var probe = new AircraftLayerProbe(threshold);
        var merges = new ScenarioRuns.MergeCounter();

        var result = TestRuns.Run(loaded, new RunOptions { AfterStep = probe.Check, Listeners = [merges] });

        var label = $"air layer {(enabled ? "on" : "off")}, seed {Seed}";
        Assert.True(result.Completed, $"{label}: {result.FirstViolation}");
        Assert.True(merges.ByClass.GetValueOrDefault(EntityClass.Aircraft) > 0, $"{label}: no merge was caused by an aircraft");
        Assert.True(probe.Steps == 480, $"{label}: the probe ran {probe.Steps} times");
        if (enabled)
        {
            Assert.True(probe.AirLayerExists, $"{label}: the topology has no air layer");
            Assert.True(probe.HighOnAirLayer > 0, $"{label}: no aircraft above {threshold} m was seen on the air layer");
            Assert.True(probe.HighOffAirLayer == 0, $"{label}: {probe.HighOffAirLayer} aircraft sightings above {threshold} m were not on the air layer");
            Assert.True(probe.LowOnGround > 0, $"{label}: no aircraft came down to the ground layer over a fight");
        }
        else
        {
            Assert.False(probe.AirLayerExists, $"{label}: the topology has an air layer");
            Assert.True(probe.OffGround == 0, $"{label}: {probe.OffGround} aircraft sightings were not on layer 0");
            Assert.True(probe.HighOnGround > 0, $"{label}: no aircraft above {threshold} m was seen on layer 0");
        }
    }

    /// <summary>
    /// Stacked areas: while both bunker doors are closed no merge can join the surface fight's region
    /// with the bunker fight's, so the fights stay apart from the start until a door first opens, and
    /// every step at which they first share a region has a door open; an open door does merge them.
    /// </summary>
    [Fact]
    public void ClosedDoorsKeepSurfaceAndBunkerFightsApart_OpenDoorMergesThem()
    {
        // Doors start closed (as in the map), open after 4 s for 2 s, every 6 s.
        var map = ScenarioRuns.StackedBaseWithSchedule(everySeconds: 6, openSeconds: 2);
        var runConfig = ScenarioRuns.Short("StackedAreas", Seed, 450, population: 300)
            .With("map.file", map)
            .With("scenarioParameters.fightRadiusPerSqrtPlayer", 16);
        var loaded = TestRuns.Load(runConfig);
        var probe = new DoorProbe(surfaceTag: 0, bunkerTag: 1);

        var result = TestRuns.Run(loaded, new RunOptions { AfterStep = probe.Check });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        var firstOpen = TimeBase.SecondsToSteps(4);
        Assert.Equal(firstOpen, probe.FirstOpenStep);
        Assert.True(probe.JoinedBeforeFirstOpen == 0, $"seed {Seed}: the fights shared a region at {probe.JoinedBeforeFirstOpen} steps before any door opened");
        Assert.True(probe.JoinedWhileClosed.Count == 0, $"seed {Seed}: the fights came to share a region with both doors closed at steps {string.Join(", ", probe.JoinedWhileClosed)}");
        Assert.True(probe.JoinedWhileOpen > 0, $"seed {Seed}: an open door never merged the surface and bunker fights");
    }

    /// <summary>A map file the portal validator rejects is refused before the run, naming <c>map.file</c> (FR-026, FR-030).</summary>
    [Theory]
    [InlineData("StackedAreas")]
    [InlineData("Soak")]
    public void MapFileWithPortalFindingsIsRefusedNamingMapFile(string scenario)
    {
        var runConfig = new JsonObject { ["scenario"] = scenario }.With("map.file", ScenarioRuns.MapPath("test/seeded-leaks.json"));

        var result = RunConfigLoader.Load(runConfig);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("map.file", error.Path);
        Assert.Contains("portal validator", error.Message, StringComparison.Ordinal);
        Assert.Contains("Leak", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunCommandExitsTwoForAMapThePortalValidatorRejects()
    {
        var config = TestRuns.WriteConfig(new JsonObject { ["scenario"] = "StackedAreas", ["durationTicks"] = 5 }
            .With("map.file", ScenarioRuns.MapPath("test/seeded-leaks.json")));
        var output = TestRuns.TempDir();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = SimulatorCli.Run(["run", config, "--out", output], stdout, stderr, new RunOptions { GitSha = "unknown" });

        Assert.Equal(ExitCodes.InvalidConfig, code);
        Assert.Contains("map.file", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    /// <summary>Watches every aircraft's layer and altitude after each step.</summary>
    private sealed class AircraftLayerProbe(double threshold)
    {
        private const int AirLayer = 1;

        public int Steps { get; private set; }

        public bool AirLayerExists { get; private set; }

        public int HighOnAirLayer { get; private set; }

        public int HighOffAirLayer { get; private set; }

        public int HighOnGround { get; private set; }

        public int LowOnGround { get; private set; }

        public int OffGround { get; private set; }

        public void Check(StepHookContext context)
        {
            Steps++;
            var regionizer = context.Regionizer;
            AirLayerExists = regionizer.Topology.HasLayer(AirLayer);
            var state = context.State;
            for (var id = 0; id < state.Count; id++)
            {
                if (state.Class[id] != EntityClass.Aircraft)
                {
                    continue;
                }

                var entity = regionizer.GetEntity(id);
                var high = entity.Z > threshold;
                var onAir = AirLayerExists && entity.Layer == AirLayer;
                HighOnAirLayer += high && onAir ? 1 : 0;
                HighOffAirLayer += high && !onAir ? 1 : 0;
                HighOnGround += high && entity.Layer == 0 ? 1 : 0;
                LowOnGround += !high && entity.Layer == 0 ? 1 : 0;
                OffGround += entity.Layer != 0 ? 1 : 0;
            }
        }
    }

    /// <summary>After each step: are the doors open, and does any region hold players of both fights?</summary>
    private sealed class DoorProbe(int surfaceTag, int bunkerTag)
    {
        private bool _joined;

        public long FirstOpenStep { get; private set; } = -1;

        public int JoinedBeforeFirstOpen { get; private set; }

        public int JoinedWhileOpen { get; private set; }

        public List<long> JoinedWhileClosed { get; } = [];

        public void Check(StepHookContext context)
        {
            var regionizer = context.Regionizer;
            var open = regionizer.IsPortalOpen(1) || regionizer.IsPortalOpen(2);
            if (open && FirstOpenStep < 0)
            {
                FirstOpenStep = context.Step;
            }

            var joined = false;
            foreach (var id in regionizer.RegionIds)
            {
                var surface = false;
                var bunker = false;
                foreach (var entity in regionizer.GetRegion(id).Entities)
                {
                    surface |= context.State.FightTag[entity] == surfaceTag;
                    bunker |= context.State.FightTag[entity] == bunkerTag;
                }

                joined |= surface && bunker;
            }

            if (joined && FirstOpenStep < 0)
            {
                JoinedBeforeFirstOpen++;
            }

            if (joined && !_joined)
            {
                if (open)
                {
                    JoinedWhileOpen++;
                }
                else
                {
                    JoinedWhileClosed.Add(context.Step);
                }
            }

            _joined = joined;
        }
    }
}
