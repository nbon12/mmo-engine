using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Scheduling;

/// <summary>The engine's step boundaries and tick bodies (research D7): portal toggles, the location queue and movement.</summary>
public sealed class StepBoundaryTests
{
    [Fact]
    public void PortalTogglesApplyAtStepBoundariesAndAreHeard()
    {
        var listener = new Recorder();
        var (engine, regionizer) = Engine([], new ScriptedMovement(), listener);
        var cycle = new PortalCycleEvents(1, 10, 4);

        Assert.True(engine.RunBoundary(0, [cycle]));
        Assert.True(regionizer.IsPortalOpen(1));
        Assert.False(engine.RunBoundary(1, [cycle]));
        Assert.True(engine.RunBoundary(4, [cycle]));
        Assert.False(regionizer.IsPortalOpen(1));
        engine.SetPortalOpen(1, false);

        Assert.Equal([(0L, 1, true), (4L, 1, false)], listener.Portals);
    }

    [Fact]
    public void ATeleportCompletesAtTheNextBoundaryWhenNobodyOwnsItsDestination()
    {
        var listener = new Recorder();
        var movement = new ScriptedMovement { [0] = MovementUpdate.TeleportTo(0, 100, 100, 0) };
        var (engine, regionizer) = Engine([new EntitySpec(0, EntityClass.Infantry, 0, 8, 8, 0, 0)], movement, listener);
        var region = regionizer.RegionIds[0];

        Assert.True(engine.StartTick(region, 0, TimeBase.StepsPerTick));
        engine.EndTick(region);
        Assert.True(engine.State.InTransit[0]);
        Assert.True(regionizer.GetEntity(0).InTransit);

        Assert.False(engine.RunBoundary(0, []));
        Assert.True(engine.RunBoundary(1, []));

        Assert.False(engine.State.InTransit[0]);
        Assert.False(regionizer.GetEntity(0).InTransit);
        Assert.Equal([(1L, 0)], listener.Teleports);
        Assert.Equal(1L, movement.Completed[0]);
        Assert.NotEqual(0, regionizer.GetEntity(0).RegionId);
    }

    [Fact]
    public void ATickMovesEveryEntityOfTheRegionInIdOrder()
    {
        var specs = Enumerable.Range(0, 40).Select(i => new EntitySpec(i, EntityClass.Infantry, 0, 8 + (i % 4), 8, 0, 0)).ToList();
        var listener = new Recorder();
        var movement = new ScriptedMovement();
        for (var i = 0; i < 40; i++)
        {
            movement[i] = MovementUpdate.MoveTo(0, 9, 9, 0);
        }

        var (engine, regionizer) = Engine(specs, movement, listener);
        var region = Assert.Single(regionizer.RegionIds.ToArray());

        Assert.True(engine.StartTick(region, 0, TimeBase.StepsPerTick));
        engine.EndTick(region);

        Assert.Equal(Enumerable.Range(0, 40), listener.Moves);
        Assert.All(specs, s => Assert.Equal(9, engine.State.X[s.Id]));
        Assert.True(engine.TakeBookkeeping() > 0);
        Assert.Equal(0, engine.TakeBookkeeping());
    }

    [Fact]
    public void ARegionThatIsNotReadyDoesNotStart()
    {
        var (engine, regionizer) = Engine([new EntitySpec(0, EntityClass.Infantry, 0, 8, 8, 0, 0)], new ScriptedMovement(), new Recorder());

        Assert.False(engine.StartTick(999, 0, TimeBase.StepsPerTick));
        Assert.Single(regionizer.RegionIds.ToArray());
    }

    [Theory]
    [InlineData(MovementOp.Move, 0, 5.0, false, false)]
    [InlineData(MovementOp.TeleportBegin, 1, 6.0, true, false)]
    [InlineData(MovementOp.Occupy, 0, 0.0, false, true)]
    [InlineData(MovementOp.Vacate, 0, 0.0, false, false)]
    [InlineData(MovementOp.None, 0, 0.0, false, false)]
    public void TheWorldMirrorRecordsEachAppliedUpdate(MovementOp op, int layer, double x, bool inTransit, bool occupied)
    {
        var state = new WorldState([new EntitySpec(0, EntityClass.CapturePoint, 0, 0, 0, 0, -1)]);
        state.Occupied[0] = op == MovementOp.Vacate;

        state.Apply(0, new MovementUpdate(op, layer, x, x, x));

        Assert.Equal((layer, x, inTransit, occupied), (state.Layer[0], state.X[0], state.InTransit[0], state.Occupied[0]));
    }

    [Fact]
    public void TheWorldMirrorNeedsCompactIds()
    {
        Assert.Throws<ArgumentException>(() => new WorldState([new EntitySpec(1, EntityClass.Infantry, 0, 0, 0, 0, -1)]));
    }

    /// <summary>A two-layer map with one closed portal; the engine runs a validated Baseline config with timers off.</summary>
    private static (SimulationEngine Engine, Regionizer Regionizer) Engine(IReadOnlyList<EntitySpec> entities, IMovementSource movement, Recorder listener)
    {
        var config = TestRuns.Load(TestRuns.Small("Baseline", 1, 10).With("timers.setProbabilityPerEntityPerSecond", 0)).Config;
        var layers = new[] { new LayerSpec(0, 0, 0, 16, 16, new ushort[256]), new LayerSpec(1, 0, 0, 16, 16, new ushort[256]) };
        var topology = SectionTopology.Build(16, layers, [new PortalSpec(1, new SectionKey(0, 2, 2), new SectionKey(1, 2, 2), false)]);
        var regionizer = new Regionizer(topology, config.Regionizer.ToRegionizerOptions());
        var engine = new SimulationEngine(regionizer, new WorldState(entities), movement, config, new TimerMonitor(TimerMode.TickCounter), [listener]);
        engine.AddEntities(entities);
        return (engine, regionizer);
    }

    /// <summary>Returns a fixed update per entity, once; records completed teleports.</summary>
    private sealed class ScriptedMovement : IMovementSource
    {
        private readonly Dictionary<int, MovementUpdate> _updates = [];

        public Dictionary<int, long> Completed { get; } = [];

        public MovementUpdate this[int entityId]
        {
            set => _updates[entityId] = value;
        }

        public MovementUpdate Next(int entityId, long now) => _updates.Remove(entityId, out var update) ? update : MovementUpdate.None;

        public void OnTeleportCompleted(int entityId, long now) => Completed[entityId] = now;
    }

    private sealed class Recorder : RunListener
    {
        public List<(long Step, int Portal, bool Open)> Portals { get; } = [];

        public List<(long Step, int Entity)> Teleports { get; } = [];

        public List<int> Moves { get; } = [];

        public override void OnPortalChanged(long step, int portalId, bool open) => Portals.Add((step, portalId, open));

        public override void OnTeleportCompleted(long step, int entityId) => Teleports.Add((step, entityId));

        public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update) => Moves.Add(entityId);
    }
}
