using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scenarios;

/// <summary>The worlds the scenarios build (data-model.md, Scenario layout and Movement models), checked before any step runs.</summary>
public sealed class ScenarioWorldTests
{
    private const ulong Seed = 7;

    /// <summary>Population scaling applies to every scenario: the players placed always sum to <c>population.total</c>.</summary>
    [Theory]
    [MemberData(nameof(ScenarioTests.AllScenarios), MemberType = typeof(ScenarioTests))]
    public void EveryScenarioPlacesExactlyThePopulationTotal(string scenario)
    {
        var config = TestRuns.Load(ScenarioRuns.Short(scenario, Seed, 1, population: 333)).Config;

        var world = ScenarioRegistry.Create(scenario).Build(config);

        Assert.Equal(333, world.Entities.Count(e => e.IsPlayer));
        Assert.Equal(Enumerable.Range(0, world.Entities.Count), world.Entities.Select(e => e.Id));
        Assert.All(world.Entities, e => Assert.True(world.Topology.TrySectionAt(e.Layer, e.X, e.Y, out _), $"{scenario}: entity {e.Id} placed outside layer {e.Layer}"));
    }

    [Fact]
    public void SpacingPutsTwoBasesSpacingApartAcrossTheMapCentre()
    {
        var config = TestRuns.Load(ScenarioRuns.Short("Spacing", Seed, 1, population: 200).With("scenarioParameters.spacingMetres", 640)).Config;

        var world = ScenarioRegistry.Create("Spacing").Build(config);

        Assert.Equal([3680.0, 4320], world.Layout!.Bases.Select(b => b.X));
        Assert.Equal([0, 1], world.Fights.Fights.Select(f => f.Base.Index));
        Assert.DoesNotContain(world.Entities, e => e.Class == EntityClass.Aircraft);
    }

    [Theory]
    [InlineData("Uniform")]
    [InlineData("Clumped")]
    public void LaneTrafficLeavesTheFreeLaneBetweenTheFightsEdges(string placement)
    {
        var runConfig = ScenarioRuns.Short("LaneTraffic", Seed, 1, population: 1060).With("scenarioParameters.travellerPlacement", placement);
        var config = TestRuns.Load(runConfig).Config;

        var world = ScenarioRegistry.Create("LaneTraffic").Build(config);

        // Fights of 500 have radius 8·√500; with the 150 m vehicle annulus each edge is that far from its base.
        var edge = (8 * Math.Sqrt(500)) + 150;
        var bases = world.Layout!.Bases;
        Assert.Equal(600 + (2 * edge), bases[1].X - bases[0].X, 9);
        var travellers = world.Entities.Where(e => e.Class == EntityClass.Traveller).ToList();
        Assert.Equal(60, travellers.Count);
        Assert.All(travellers, t =>
        {
            Assert.InRange(t.X, 3700, 4300);
            Assert.Equal(4000, t.Y);
        });
    }

    [Fact]
    public void ClumpedTravellersStartTogetherAndTravelTogether()
    {
        var runConfig = ScenarioRuns.Short("LaneTraffic", Seed, 1, population: 1060)
            .With("scenarioParameters.travellerPlacement", "Clumped")
            .With("scenarioParameters.clumpSize", 5)
            .With("scenarioParameters.clumpSpreadMetres", 30);
        var config = TestRuns.Load(runConfig).Config;

        var world = ScenarioRegistry.Create("LaneTraffic").Build(config);

        var travellers = world.Entities.Where(e => e.Class == EntityClass.Traveller).ToList();
        var ceilings = config.Movement.SpeedCeiling;
        for (var c = 0; c < travellers.Count / 5; c++)
        {
            var clump = travellers.Skip(c * 5).Take(5).ToList();
            Assert.True(clump.Max(t => t.X) - clump.Min(t => t.X) <= 60, $"seed {Seed}: clump {c} starts spread over more than 2 × 30 m");

            // One second later every member has moved the same way at the clump's speed (none is near a lane end).
            var moves = clump.Select(t => world.Movement.Next(t.Id, TimeBase.StepsPerSecond).X - t.X).ToList();
            var speed = c % 2 == 0 ? ceilings.Infantry : ceilings.GroundVehicle;
            if (clump.All(t => t.X > 3700 + speed && t.X < 4300 - speed))
            {
                Assert.All(moves, d => Assert.Equal(moves[0], d, 9));
                Assert.Equal(speed, Math.Abs(moves[0]), 9);
            }
        }
    }

    [Fact]
    public void VehicleColumnsDriveBetweenFightsSpacedApartAtTheirSpeed()
    {
        var config = TestRuns.Load(ScenarioRuns.Short("VehicleColumns", Seed, 1, population: 4348)).Config;

        var world = ScenarioRegistry.Create("VehicleColumns").Build(config);

        // The fights' players come first, then the four columns of twelve vehicles.
        var players = world.Fights.Fights.Sum(f => f.Players);
        var columns = world.Entities.Skip(players).Take(48).ToList();
        Assert.All(columns, v => Assert.Equal((EntityClass.GroundVehicle, -1), (v.Class, v.FightTag)));
        for (var c = 0; c < 4; c++)
        {
            var column = columns.Skip(c * 12).Take(12).ToList();
            for (var k = 1; k < column.Count; k++)
            {
                var gap = Math.Sqrt(Sq(column[k].X - column[k - 1].X) + Sq(column[k].Y - column[k - 1].Y));
                Assert.True(gap <= 20 + 1e-9, $"seed {Seed}: column {c} vehicles {k - 1} and {k} are {gap} m apart");
            }

            var leader = column[0];
            var next = world.Movement.Next(leader.Id, TimeBase.StepsPerSecond);
            var moved = Math.Sqrt(Sq(next.X - leader.X) + Sq(next.Y - leader.Y));
            Assert.True(moved <= 20 + 1e-9, $"seed {Seed}: column {c}'s leader moved {moved} m in one second");
        }
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(4, 0)]
    public void VehicleColumnsWithNoVehiclesLeaveOnlyTheFights(int columns, int vehiclesPerColumn)
    {
        var runConfig = ScenarioRuns.Short("VehicleColumns", Seed, 1, population: 430)
            .With("scenarioParameters.columns", columns)
            .With("scenarioParameters.vehiclesPerColumn", vehiclesPerColumn);
        var config = TestRuns.Load(runConfig).Config;

        var world = ScenarioRegistry.Create("VehicleColumns").Build(config);

        Assert.Equal(430, world.Fights.Fights.Sum(f => f.Players));
        Assert.DoesNotContain(world.Entities, e => e.Class == EntityClass.GroundVehicle && e.FightTag < 0);
    }

    [Fact]
    public void ColumnRouteRunsBackAndForthAlongItsLine()
    {
        var route = new ColumnRoute(0, 0, 100, 0, startArcMetres: 0, speedMetresPerSecond: 10, TimeBase.Default);

        Assert.Equal((0.0, 0.0), route.At(0, 0));
        Assert.Equal((10.0, 0.0), route.At(TimeBase.StepsPerSecond, 0));
        Assert.Equal((50.0, 0.0), route.At(15 * TimeBase.StepsPerSecond, 0));
        Assert.Equal((20.0, 0.0), route.At(0, 20));
        Assert.Equal((0.0, 0.0), route.At(20 * TimeBase.StepsPerSecond, 0));
        Assert.Equal((5.0, 5.0), new ColumnRoute(5, 5, 5, 5, 0, 10, TimeBase.Default).At(600, 0));

        // At a 120 Hz base step rate the same route covers the same ground per second of game time.
        var fast = new ColumnRoute(0, 0, 100, 0, startArcMetres: 0, speedMetresPerSecond: 10, new TimeBase(120, 30, 20));
        Assert.Equal((10.0, 0.0), fast.At(120, 0));
    }

    [Fact]
    public void StackedAreasPutsTheBunkerFightUnderTheSurfaceFight()
    {
        var config = TestRuns.Load(ScenarioRuns.Short("StackedAreas", Seed, 1, population: 900)).Config;

        var world = ScenarioRegistry.Create("StackedAreas").Build(config);

        // stacked-base.json: a 125 × 125 surface of 16 m sections over a 20 × 20 bunker from section 53.
        var (surface, bunker) = (world.Fights.Fights[0], world.Fights.Fights[1]);
        Assert.Equal((600, 300), (surface.Players, bunker.Players));
        Assert.Equal((0, 1), (surface.Layer, bunker.Layer));
        Assert.Equal((1008.0, 1008.0), (surface.CenterX, surface.CenterY));
        Assert.Equal((1008.0, 1008.0), (bunker.CenterX, bunker.CenterY));
        Assert.Empty(bunker.GroundVehicles);
        Assert.Empty(bunker.Aircraft);
        Assert.All(bunker.Infantry, id =>
        {
            var e = world.Entities[id];
            Assert.Equal(1, e.Layer);
            Assert.InRange(e.X, 848, 1168);
            Assert.InRange(e.Y, 848, 1168);
        });
        Assert.All(surface.Infantry.Concat(surface.GroundVehicles), id => Assert.Equal(0, world.Entities[id].Layer));
        var capturePoints = world.Entities.Where(e => e.Class == EntityClass.CapturePoint).ToList();
        Assert.Equal([0, 0, 0, 1, 1, 1], capturePoints.Select(e => e.Layer));
        Assert.Equal(2, world.Topology.PortalCount);
        Assert.Equal([1, 2], world.StepEvents.Cast<MapPortalCycle>().Select(e => e.PortalId));
        Assert.Empty(world.Layout!.Lanes);
    }

    [Fact]
    public void StackedAreasCentresTheMapInALargerWorld()
    {
        var config = TestRuns.Load(ScenarioRuns.Short("StackedAreas", Seed, 1, population: 90).With("map.widthMetres", 4000).With("map.heightMetres", 3000)).Config;

        var world = ScenarioRegistry.Create("StackedAreas").Build(config);

        // The 125-section map in a 250 × 188 section world is shifted by (62, 31) sections.
        var bunker = world.Fights.Fights[1];
        Assert.Equal((2000.0, 1504.0), (bunker.CenterX, bunker.CenterY));
        Assert.True(world.Topology.TryIndexOf(new SectionKey(1, 53 + 62, 53 + 31), out _));
        Assert.False(world.Topology.TryIndexOf(new SectionKey(1, 52 + 62, 53 + 31), out _));
        Assert.True(world.Topology.TryIndexOf(new SectionKey(0, 249, 187), out _));
    }

    [Fact]
    public void SoakCombinesMigrationTheStackedBaseAndTheAirLayer()
    {
        var config = TestRuns.Load(ScenarioRuns.Short("Soak", Seed, 1, population: 590)).Config;

        var world = ScenarioRegistry.Create("Soak").Build(config);

        Assert.Equal(9, world.Fights.Fights.Count);
        Assert.Equal(Enumerable.Range(0, 9), world.Fights.Fights.Select(f => f.Tag));
        Assert.Equal(1, world.Fights.Fights[8].Layer);
        Assert.Equal([12, 13], world.Fights.Fights.Skip(7).Select(f => f.Base.Index));
        Assert.True(world.Topology.AllowsDirectEntry(2), "the air layer takes the ID after the map's layers");
        Assert.All(world.Entities.Where(e => e.Class == EntityClass.Aircraft && e.Z > 150), e => Assert.Equal(2, e.Layer));
        Assert.Contains(world.Entities, e => e.Class == EntityClass.Aircraft && e.Layer == 2);
        Assert.IsType<MigrationEvents>(world.StepEvents[0]);
        Assert.Equal(3, world.StepEvents.Count);

        // A migration moves only the lane fights; the stacked base's fights stay put.
        var before = world.Fights.Fights.Select(f => f.Base).ToList();
        var actions = new RecordingActions(world.State);
        world.StepEvents[0].OnStepBoundary(TimeBase.SecondsToSteps(120), actions);
        var after = world.Fights.Fights.Select(f => f.Base).ToList();
        Assert.All(Enumerable.Range(0, 7), i => Assert.NotEqual(before[i], after[i]));
        Assert.Equal(before.Skip(7), after.Skip(7));
    }

    /// <summary>A door that starts closed opens for the last <c>open</c> steps of each cycle; one that starts open, for the first.</summary>
    [Theory]
    [InlineData(false, 10, 4, new[] { 6, 10, 16 }, new[] { true, false, true })]
    [InlineData(true, 10, 4, new[] { 4, 10, 14 }, new[] { false, true, false })]
    [InlineData(true, 10, 0, new[] { 0 }, new[] { false })]
    [InlineData(false, 10, 10, new[] { 0 }, new[] { true })]
    [InlineData(false, 10, 12, new[] { 0 }, new[] { true })]
    public void MapPortalCycleStartsInThePortalsInitialState(bool initiallyOpen, long every, long open, int[] expectedSteps, bool[] expectedOpen)
    {
        var cycle = new MapPortalCycle(5, every, open, initiallyOpen);
        var actions = new RecordingActions(null);

        for (var step = 0L; step < 2 * every; step++)
        {
            actions.Step = step;
            cycle.OnStepBoundary(step, actions);
        }

        // Toggling a portal to the state it already has is a no-op in the engine, so only changes count.
        var changes = actions.Toggles.Where((t, i) => i == 0 ? t.Open != initiallyOpen : t.Open != actions.Toggles[i - 1].Open).ToList();
        if (expectedSteps.Length == 1 && expectedOpen[0] == initiallyOpen)
        {
            Assert.Empty(changes);
            return;
        }

        Assert.Equal(expectedSteps.Select(s => (long)s), changes.Select(t => t.Step));
        Assert.Equal(expectedOpen, changes.Select(t => t.Open));
        Assert.All(actions.Toggles, t => Assert.Equal(5, t.Portal));
    }

    [Fact]
    public void MapPortalCycleNeedsACycleOfAtLeastOneStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapPortalCycle(1, 0, 0, false));
        Assert.Throws<ArgumentNullException>(() => new MapPortalCycle(1, 10, 4, false).OnStepBoundary(0, null!));
    }

    [Fact]
    public void MapFilesResolveFromTheWorkingDirectoryOrAboveIt()
    {
        var root = TestRuns.RepositoryRoot();
        var nested = Path.Combine(root, "simulator", "scenarios");
        var outside = TestRuns.TempDir();
        Directory.CreateDirectory(outside);
        var expected = Path.Combine(root, "simulator", "maps", "stacked-base.json");

        Assert.Equal(expected, MapFiles.Resolve("simulator/maps/stacked-base.json", nested, outside));
        Assert.Equal(expected, MapFiles.Resolve("simulator/maps/stacked-base.json", outside, nested));
        Assert.Equal(expected, MapFiles.Resolve(expected, outside, outside));
        Assert.Null(MapFiles.Resolve("simulator/maps/stacked-base.json", outside, outside));
        Assert.Null(MapFiles.Resolve(Path.Combine(outside, "none.json"), root, root));
        Assert.Equal(expected, Path.GetFullPath(MapFiles.Resolve(Path.GetRelativePath(Environment.CurrentDirectory, expected))!));
    }

    /// <summary>Above the threshold an aircraft is on the air layer; it leaves only below the threshold minus the hysteresis.</summary>
    [Theory]
    [InlineData(0, 151, 9)]
    [InlineData(0, 150, 0)]
    [InlineData(9, 140, 9)]
    [InlineData(9, 130, 9)]
    [InlineData(9, 129.9, 0)]
    [InlineData(9, 300, 9)]
    public void AirLayerSwitchesWithHysteresis(int currentLayer, double altitude, int expectedLayer)
    {
        var policy = new AirLayerPolicy(9, altitudeMetres: 150, hysteresisMetres: 20);

        Assert.Equal(expectedLayer, policy.LayerFor(currentLayer, 0, altitude));
        Assert.Equal(altitude > 150 ? 9 : 0, policy.InitialLayer(0, altitude));
        Assert.Equal(0, AirLayerPolicy.None.LayerFor(currentLayer, 0, altitude));
        Assert.Equal(0, AirLayerPolicy.None.InitialLayer(0, altitude));
    }

    [Fact]
    public void AirLayerPolicyFollowsTheConfiguration()
    {
        var on = new AirLayerConfig { Enabled = true, AltitudeMetres = 150, HysteresisMetres = 20 };

        Assert.True(AirLayerPolicy.From(on, 3).Enabled);
        Assert.Equal(3, AirLayerPolicy.From(on, 3).LayerId);
        Assert.Same(AirLayerPolicy.None, AirLayerPolicy.From(on with { Enabled = false }, 3));
        Assert.Throws<ArgumentNullException>(() => AirLayerPolicy.From(null!, 3));
    }

    [Fact]
    public void TravellerClumpsStartInsideTheLane()
    {
        var lane = new Lane(0, 100, 50, 150, []);
        var model = new TravellerModel(new DeterministicRandom(1), null!, lane, 7, new TravellerClump(52, 30, -1));

        for (var i = 0; i < 50; i++)
        {
            var (layer, x, y, _) = model.Place();
            Assert.Equal((0, 100.0), (layer, y));
            Assert.InRange(x, 50, 82);
        }
    }

    private static double Sq(double v) => v * v;

    /// <summary>Step-boundary actions that record portal toggles and expose a world mirror.</summary>
    private sealed class RecordingActions(WorldState? state) : IStepBoundaryActions
    {
        public long Step { get; set; }

        public Regionizer Regionizer => throw new NotSupportedException("not used by these events");

        public WorldState State => state ?? throw new NotSupportedException("no world mirror");

        public List<(long Step, int Portal, bool Open)> Toggles { get; } = [];

        public void SetPortalOpen(int portalId, bool open) => Toggles.Add((Step, portalId, open));
    }
}
