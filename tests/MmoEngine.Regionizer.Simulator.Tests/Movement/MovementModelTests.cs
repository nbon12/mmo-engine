using CsCheck;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Movement;

/// <summary>The synthetic movement models (FR-021, data-model.md), observed through a real run.</summary>
public sealed class MovementModelTests
{
    private const ulong Seed = 21;
    private const int Ticks = 450;

    /// <summary>Speed never exceeds the class's configured ceiling (travellers use the infantry or ground-vehicle ceiling).</summary>
    [Theory]
    [InlineData(EntityClass.Infantry, "Lockstep")]
    [InlineData(EntityClass.GroundVehicle, "Lockstep")]
    [InlineData(EntityClass.Aircraft, "Lockstep")]
    [InlineData(EntityClass.Traveller, "Lockstep")]
    [InlineData(EntityClass.Infantry, "Interleaved")]
    [InlineData(EntityClass.Aircraft, "Interleaved")]
    public void SpeedNeverExceedsTheConfiguredCeiling(EntityClass cls, string scheduling)
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, Ticks, population: 240).With("scheduling.mode", scheduling));
        var ceilings = loaded.Config.Movement.SpeedCeiling;
        var ceiling = cls switch
        {
            EntityClass.Infantry => ceilings.Infantry,
            EntityClass.GroundVehicle => ceilings.GroundVehicle,
            EntityClass.Aircraft => ceilings.Aircraft,
            _ => Math.Max(ceilings.Infantry, ceilings.GroundVehicle),
        };
        var recorder = new MoveRecorder();

        var result = TestRuns.Run(loaded, new RunOptions { Listeners = [recorder] });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        var world = ScenarioRegistry.Create("Baseline").Build(loaded.Config);
        var ids = world.Entities.Where(e => e.Class == cls).Select(e => e.Id).ToHashSet();
        var checkedSteps = 0;
        foreach (var (id, moves) in recorder.Moves.Where(kv => ids.Contains(kv.Key)))
        {
            for (var i = 1; i < moves.Count; i++)
            {
                if (moves[i].AfterTeleport)
                {
                    continue;
                }

                var dt = TimeBase.StepsToSeconds(moves[i].Step - moves[i - 1].Step);
                var distance = Math.Sqrt(Sq(moves[i].X - moves[i - 1].X) + Sq(moves[i].Y - moves[i - 1].Y));
                Assert.True(distance <= (ceiling * dt * (1 + 1e-12)) + 1e-9, $"seed {Seed}: {cls} {id} moved {distance} m in {dt} s at step {moves[i].Step}, over {ceiling} m/s");
                checkedSteps++;
            }
        }

        Assert.True(checkedSteps > 100, $"seed {Seed}: only {checkedSteps} {cls} moves were checked");
    }

    [Fact]
    public void InfantryStayWithinTheirFightRadiusPlusOneStep()
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, Ticks, population: 240));
        var world = ScenarioRegistry.Create("Baseline").Build(loaded.Config);
        var recorder = new MoveRecorder();

        TestRuns.Run(loaded, new RunOptions { Listeners = [recorder] });

        var step = loaded.Config.Movement.SpeedCeiling.Infantry * TimeBase.StepsToSeconds(TimeBase.StepsPerTick);
        var checkedMoves = 0;
        foreach (var fight in world.Fights.Fights)
        {
            foreach (var id in fight.Infantry)
            {
                foreach (var move in recorder.Moves.GetValueOrDefault(id, []))
                {
                    var d = Math.Sqrt(Sq(move.X - fight.CenterX) + Sq(move.Y - fight.CenterY));
                    Assert.True(d <= fight.Radius + step, $"seed {Seed}: infantry {id} of fight {fight.Tag} is {d} m from its centre, radius {fight.Radius}");
                    checkedMoves++;
                }
            }
        }

        Assert.True(checkedMoves > 1000, $"seed {Seed}: only {checkedMoves} infantry moves were checked");
    }

    [Fact]
    public void TravellersStayOnTheirLane()
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, Ticks, population: 240));
        var world = ScenarioRegistry.Create("Baseline").Build(loaded.Config);
        var lanes = world.Layout!.Lanes;
        var travellers = world.Entities.Where(e => e.Class == EntityClass.Traveller).Select(e => e.Id).ToList();
        var recorder = new MoveRecorder();

        TestRuns.Run(loaded, new RunOptions { Listeners = [recorder] });

        Assert.NotEmpty(travellers);
        for (var k = 0; k < travellers.Count; k++)
        {
            var lane = lanes[k % lanes.Count];
            var moves = recorder.Moves.GetValueOrDefault(travellers[k], []);
            Assert.NotEmpty(moves);
            Assert.All(moves, m =>
            {
                Assert.True(m.Y == lane.Y, $"seed {Seed}: traveller {travellers[k]} left lane {lane.Index} (y {m.Y} vs {lane.Y})");
                Assert.InRange(m.X, lane.StartX, lane.EndX);
            });
        }
    }

    [Fact]
    public void AircraftFlyAtTheFightOrCruiseAltitude()
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, Ticks, population: 240));
        var world = ScenarioRegistry.Create("Baseline").Build(loaded.Config);
        var models = loaded.Config.Movement.Models;
        var aircraft = world.Entities.Where(e => e.Class == EntityClass.Aircraft).Select(e => e.Id).ToHashSet();
        var recorder = new MoveRecorder();

        TestRuns.Run(loaded, new RunOptions { Listeners = [recorder] });

        var altitudes = recorder.Moves.Where(kv => aircraft.Contains(kv.Key)).SelectMany(kv => kv.Value).Select(m => m.Z).ToList();
        Assert.True(altitudes.Count > 100, $"seed {Seed}: too few aircraft moves");
        Assert.All(altitudes, z => Assert.True(z == models.AircraftFightAltitudeMetres || z == models.AircraftCruiseAltitudeMetres, $"seed {Seed}: altitude {z}"));
        Assert.Contains(models.AircraftFightAltitudeMetres, altitudes);
        Assert.Contains(models.AircraftCruiseAltitudeMetres, altitudes);
        Assert.All(world.Entities.Where(e => e.Class == EntityClass.Aircraft), e => Assert.True(e.Z == 50 || e.Z == 300));
    }

    [Fact]
    public void CapturePointsAreOccupiedOnlyWhileFightInfantryStandInTheirZone()
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, 300, population: 240));
        var zone = loaded.Config.Regionizer.ClaimRadius.CapturePointZone;
        var checker = new OccupancyChecker(zone);

        var result = TestRuns.Run(loaded, new RunOptions { Listeners = [checker], AfterStep = checker.Capture });

        Assert.True(result.Completed, $"seed {Seed}: {result.FirstViolation}");
        Assert.True(checker.Occupations > 0, $"seed {Seed}: no capture point was ever occupied");
        Assert.Empty(checker.Failures);
    }

    [Fact]
    public void MoveTowardNeverStepsFurtherThanAllowed()
    {
        Gen.Select(Gen.Double[-5000, 5000], Gen.Double[-5000, 5000], Gen.Double[-5000, 5000], Gen.Double[-5000, 5000], Gen.Double[0, 200]).Sample(
            t =>
            {
                var (x, y, tx, ty, max) = t;
                var (nx, ny, arrived) = Planar.MoveToward(x, y, tx, ty, max);
                var moved = Math.Sqrt(Sq(nx - x) + Sq(ny - y));
                Assert.True(moved <= (max * (1 + 1e-12)) + 1e-9, $"moved {moved} > {max}");
                if (arrived)
                {
                    Assert.Equal((tx, ty), (nx, ny));
                }
            },
            seed: TestRuns.PropertySeed,
            iter: 2000,
            threads: 1);
    }

    [Fact]
    public void InDiscAndAnnulusPointsStayInTheirShapes()
    {
        var random = new DeterministicRandom(4);
        for (var i = 0; i < 2000; i++)
        {
            var (x, y) = Planar.InDisc(random, 100, 200, 50);
            Assert.True(Sq(x - 100) + Sq(y - 200) <= (50 * 50) + 1e-6);
            var (ax, ay) = Planar.OnAnnulus(random, 100, 200, 50, 80);
            var r = Math.Sqrt(Sq(ax - 100) + Sq(ay - 200));
            Assert.InRange(r, 50 - 1e-9, 80 + 1e-9);
        }
    }

    [Fact]
    public void ZeroElapsedTimeMovesNothing()
    {
        var loaded = Load(TestRuns.Small("Baseline", Seed, 1, population: 40));
        var world = ScenarioRegistry.Create("Baseline").Build(loaded.Config);
        var infantry = world.Fights.Fights[0].Infantry[0];

        var update = world.Movement.Next(infantry, 0);

        Assert.Equal(MovementOp.Move, update.Op);
        Assert.Equal((world.State.X[infantry], world.State.Y[infantry]), (update.X, update.Y));
    }

    private static LoadedRunConfig Load(System.Text.Json.Nodes.JsonObject runConfig) => TestRuns.Load(runConfig);

    private static double Sq(double v) => v * v;

    private readonly record struct Move(long Step, double X, double Y, double Z, bool AfterTeleport);

    /// <summary>Every applied move per entity, with teleports marked so speed is not measured across them.</summary>
    private sealed class MoveRecorder : RunListener
    {
        private readonly HashSet<int> _teleported = [];

        public Dictionary<int, List<Move>> Moves { get; } = [];

        public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update)
        {
            if (update.Op == MovementOp.TeleportBegin)
            {
                _teleported.Add(entityId);
                return;
            }

            if (update.Op != MovementOp.Move)
            {
                return;
            }

            if (!Moves.TryGetValue(entityId, out var list))
            {
                list = [];
                Moves.Add(entityId, list);
            }

            list.Add(new Move(step, update.X, update.Y, update.Z, _teleported.Remove(entityId)));
        }
    }

    /// <summary>Checks each capture point occupancy change against the world mirror at the end of that step.</summary>
    private sealed class OccupancyChecker(double zone) : RunListener
    {
        private readonly List<(int Entity, bool Occupied, long Step)> _changes = [];

        public int Occupations { get; private set; }

        public List<string> Failures { get; } = [];

        public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update)
        {
            if (update.Op is MovementOp.Occupy or MovementOp.Vacate)
            {
                _changes.Add((entityId, update.Op == MovementOp.Occupy, step));
            }
        }

        public void Capture(StepHookContext context)
        {
            // An occupancy change is decided in the capture point's own tick from positions at that moment;
            // check occupied ones had fight infantry within the zone of their position at least once this step.
            foreach (var (entity, occupied, step) in _changes)
            {
                if (!occupied)
                {
                    continue;
                }

                Occupations++;
                var state = context.State;
                var near = false;
                for (var e = 0; e < state.Count; e++)
                {
                    if (state.Class[e] == EntityClass.Infantry && state.FightTag[e] >= 0 && !state.InTransit[e]
                        && Sq(state.X[e] - state.X[entity]) + Sq(state.Y[e] - state.Y[entity]) <= Sq(zone + 1))
                    {
                        near = true;
                        break;
                    }
                }

                if (!near)
                {
                    Failures.Add($"capture point {entity} occupied at step {step} with no fight infantry within its zone");
                }
            }

            _changes.Clear();
        }
    }
}
