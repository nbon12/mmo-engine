using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Config;

/// <summary>Scenario defaults turned into a world: the registry, the lane map, population scaling and fights (FR-023, data-model.md).</summary>
public sealed class ScenarioSetupTests
{
    public static TheoryData<string> Scenarios => [.. ScenarioNames.All];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryScenarioNameIsRegisteredUnderItsName(string name)
    {
        Assert.Equal(name, ScenarioRegistry.Create(name).Name);
    }

    [Fact]
    public void UnknownScenarioNamesAreNotRegistered()
    {
        Assert.Throws<KeyNotFoundException>(() => ScenarioRegistry.Create("Skirmish"));
        Assert.False(ScenarioRegistry.TryCreate(null, out _));
        Assert.False(ScenarioNames.IsKnown("baseline"));
    }

    [Fact]
    public void AScenarioNotYetImplementedCannotBeBuilt()
    {
        var config = TestRuns.Load(new JsonObject { ["scenario"] = "OneBase" }).Config;

        Assert.Throws<NotImplementedException>(() => ScenarioRegistry.Create("OneBase").Build(config));
    }

    /// <summary>
    /// Population scaling: each count is rounded half away from zero to count × total / sum, and the
    /// remainder goes to the first fight (or, with no fights, the first other count).
    /// </summary>
    [Theory]
    [InlineData(new[] { 1200, 800, 800, 500, 400, 300, 300 }, new[] { 500, 200 }, 2000, new[] { 480, 320, 320, 200, 160, 120, 120 }, new[] { 200, 80 })]
    [InlineData(new[] { 3, 3 }, new[] { 3 }, 10, new[] { 4, 3 }, new[] { 3 })]
    [InlineData(new[] { 0, 0 }, new[] { 0 }, 5, new[] { 0, 0 }, new[] { 0 })]
    [InlineData(new int[0], new[] { 5, 5 }, 3, new int[0], new[] { 1, 2 })]
    [InlineData(new[] { 5000 }, new int[0], 5000, new[] { 5000 }, new int[0])]
    public void PopulationTotalScalesEveryCountInProportion(int[] fights, int[] others, int total, int[] expectedFights, int[] expectedOthers)
    {
        var (scaledFights, scaledOthers) = PopulationScaler.Scale(fights, others, total);

        Assert.Equal(expectedFights, scaledFights);
        Assert.Equal(expectedOthers, scaledOthers);
    }

    [Theory]
    [InlineData(58, 0.8, 0.15, 0.05, 46, 9, 3)]
    [InlineData(10, 0.5, 0.5, 0.0, 5, 5, 0)]
    [InlineData(1, 0.5, 0.5, 0.0, 1, 0, 0)]
    [InlineData(0, 0.8, 0.15, 0.05, 0, 0, 0)]
    public void FightCompositionRoundsInfantryAndVehiclesAndGivesTheRestToAircraft(int players, double infantry, double vehicles, double aircraft, int expectedInfantry, int expectedVehicles, int expectedAircraft)
    {
        var composition = new CompositionParameters { Infantry = infantry, GroundVehicle = vehicles, Aircraft = aircraft };

        Assert.Equal((expectedInfantry, expectedVehicles, expectedAircraft), LaneMapWorld.Compose(players, composition));
    }

    [Fact]
    public void DefaultMapHasThreeLanesOfFourEvenlySpacedBases()
    {
        var layout = MapLayout.Generate(8000, 8000, 3, 4, 3, 100, new DeterministicRandom(1));

        Assert.Equal([2000.0, 4000, 6000], layout.Lanes.Select(l => l.Y));
        Assert.All(layout.Lanes, l => Assert.Equal([1000.0, 3000, 5000, 7000], l.Bases.Select(b => b.X)));
        Assert.Equal(Enumerable.Range(0, 12), layout.Bases.Select(b => b.Index));
        Assert.Equal(6000, layout.Lanes[0].Length);
        Assert.All(layout.Bases, b =>
        {
            Assert.Equal(3, b.Objectives.Count);
            Assert.All(b.Objectives, o => Assert.True(((o.X - b.X) * (o.X - b.X)) + ((o.Y - b.Y) * (o.Y - b.Y)) <= 100 * 100));
        });
    }

    [Fact]
    public void SingleLaneLayoutPlacesBasesWhereAsked()
    {
        var layout = MapLayout.SingleLane(2000, 1000, [700, 1200], 1, 0, new DeterministicRandom(1));

        var lane = Assert.Single(layout.Lanes);
        Assert.Equal(500, lane.Y);
        Assert.Equal([700.0, 1200], lane.Bases.Select(b => b.X));
        Assert.Equal((700.0, 500.0), lane.Bases[0].Objectives[0]);
        Assert.Equal((700.0, 1200.0), (lane.StartX, lane.EndX));
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(3, 1, 2, -1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(2, -1, 1, -1)]
    public void FightsMoveAlongTheirLaneAndBounceAtItsEnds(int from, int direction, int expectedBase, int expectedDirection)
    {
        var layout = MapLayout.Generate(8000, 8000, 3, 4, 0, 0, new DeterministicRandom(1));

        var (next, newDirection) = layout.NextOnLane(layout.Lanes[1].Bases[from], direction);

        Assert.Equal(layout.Lanes[1].Bases[expectedBase], next);
        Assert.Equal(expectedDirection, newDirection);
    }

    [Fact]
    public void ALaneWithOneBaseKeepsItsFightThere()
    {
        var layout = MapLayout.Generate(1000, 1000, 1, 1, 0, 0, new DeterministicRandom(1));

        Assert.Equal((layout.Bases[0], 1), layout.NextOnLane(layout.Bases[0], 1));
        Assert.Equal(500, layout.Bases[0].X);
    }

    [Fact]
    public void BaselineWorldPlacesTheScaledPopulationAndOneCapturePointPerObjective()
    {
        var config = TestRuns.Load(TestRuns.Small("Baseline", 3, 10, population: 500)).Config;

        var world = ScenarioRegistry.Create("Baseline").Build(config);

        Assert.Equal(Enumerable.Range(0, world.Entities.Count), world.Entities.Select(e => e.Id));
        Assert.Equal(500, world.Entities.Count(e => e.IsPlayer));
        Assert.Equal(12 * 3, world.Entities.Count(e => e.Class == EntityClass.CapturePoint));
        Assert.Equal([120, 80, 80, 50, 40, 30, 30], world.Fights.Fights.Select(f => f.Players));
        Assert.Equal(50, world.Entities.Count(e => e.Class == EntityClass.Traveller));
        Assert.All(world.Fights.Fights, f =>
        {
            Assert.Equal(f.Players, f.Infantry.Count + f.GroundVehicles.Count + f.Aircraft.Count);
            Assert.Equal(8 * Math.Sqrt(f.Players), f.Radius);
            Assert.All(f.Infantry.Concat(f.GroundVehicles), id => Assert.Equal(f.Tag, world.Entities[id].FightTag));
            Assert.All(f.Aircraft, id => Assert.Equal(-1, world.Entities[id].FightTag));
        });
        Assert.Same(world.Fights.Fights[0], world.Fights.AtBase(0));
        Assert.Null(world.Fights.AtBase(2));
    }

    [Fact]
    public void FightsAreAddedInIndexOrder()
    {
        var layout = MapLayout.Generate(1000, 1000, 1, 2, 0, 0, new DeterministicRandom(1));
        var table = new FightTable();

        Assert.Throws<ArgumentException>(() => table.Add(new Fight(1, 1, layout.Bases[0], 10, 5)));
    }

    [Theory]
    [InlineData(EntityClass.Infantry, true)]
    [InlineData(EntityClass.GroundVehicle, true)]
    [InlineData(EntityClass.Aircraft, true)]
    [InlineData(EntityClass.Traveller, true)]
    [InlineData(EntityClass.CapturePoint, false)]
    [InlineData(EntityClass.HeavyProjectile, false)]
    public void PlayersAreInfantryVehiclesAircraftAndTravellers(EntityClass cls, bool player)
    {
        Assert.Equal(player, new EntitySpec(0, cls, 0, 0, 0, 0, -1).IsPlayer);
    }
}
