using BenchmarkDotNet.Attributes;
using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Benchmarks;

/// <summary>
/// Regionizer bookkeeping cost (spec 013, US7, T068), driving the core library directly:
/// <list type="bullet">
/// <item>one move within a section, and one across a section edge;</item>
/// <item>a tick start and end with no merge;</item>
/// <item>a full lockstep step of 5,000 entities in seven fights on an 8 km open-world map.</item>
/// </list>
/// Every case runs at section sizes 8, 16, 32 and 64 m, in both anchoring modes. The world is seeded,
/// and its invariants are checked before and after measuring.
/// </summary>
[MemoryDiagnoser]
public class BookkeepingBenchmarks
{
    private const ulong Seed = 20261103;
    private const double MapMetres = 8000;
    private const int EntityCount = 5000;
    private const int ProbeId = EntityCount;

    // Regions tick at 30 Hz: every 2 steps of the 60 Hz time base (research D7).
    private const int StepsPerTick = 2;
    private const double TickSeconds = StepsPerTick / 60.0;

    // Each entity walks back and forth on a straight line that it repeats every 120 ticks (4 s).
    private const int PeriodTicks = 120;
    private const int WarmUpTicks = 2 * PeriodTicks;

    // Baseline's fights and composition (data-model.md), scaled to 5,000 entities.
    private static readonly int[] BaselineFights = [1200, 800, 800, 500, 400, 300, 300];

    // Seven of the twelve bases on three lanes, 1.6 km apart, so fights never touch.
    private static readonly (double X, double Y)[] FightCentres =
    [
        (1600, 2000), (3200, 2000), (4800, 2000), (6400, 2000),
        (1600, 4000), (3200, 4000), (4800, 4000),
    ];

    private Regionizer _regionizer = null!;
    private double[] _homeX = [];
    private double[] _homeY = [];
    private double[] _homeZ = [];
    private double[] _stepX = [];
    private double[] _stepY = [];
    private int[] _phase = [];
    private long[] _tickRegions = new long[64];
    private int[] _members = [];
    private long _tick;
    private double _probeLeft;
    private double _probeY;
    private long _probeRegion;

    /// <summary>Section side in metres.</summary>
    [Params(8, 16, 32, 64)]
    public int SectionSize { get; set; }

    [ParamsAllValues]
    public ClaimAnchoring Anchoring { get; set; }

    /// <summary>The seeded world, stepped past split damping's minimum age into its steady state.</summary>
    [GlobalSetup(Target = nameof(FullLockstepStep))]
    public void SetUpWorld() => BuildWorld();

    /// <summary>The world plus a probe infantry entity at the centre of the largest fight.</summary>
    [GlobalSetup(Targets = new[] { nameof(MoveWithinSection), nameof(MoveAcrossSectionEdge), nameof(TickStartAndEndWithNoMerge) })]
    public void SetUpProbe()
    {
        BuildWorld();
        var s = SectionSize;
        _probeLeft = Math.Floor(FightCentres[0].X / s) * s;
        _probeY = (Math.Floor(FightCentres[0].Y / s) * s) + (0.5 * s);
        _regionizer.AddEntity(ProbeId, EntityClass.Infantry, 0, _probeLeft + (0.25 * s), _probeY, fightTag: 0);
        _probeRegion = _regionizer.GetEntity(ProbeId).RegionId;

        // The probe's region must tick and end with no merge or split, or the tick case measures something else.
        var result = TickStartAndEndWithNoMerge();
        if (result != new TickEndResult(RegionState.Ready, 0, 0))
        {
            throw new InvalidOperationException($"{Describe()}: the probe region's tick end was {result}, not a plain tick end.");
        }

        AssertClean("after adding the probe");
    }

    [GlobalCleanup]
    public void CheckInvariantsAfterMeasuring() => AssertClean("after measuring");

    /// <summary>The probe moves half a section within its section, there and back.</summary>
    [Benchmark(OperationsPerInvoke = 2)]
    public void MoveWithinSection()
    {
        _regionizer.MoveEntity(ProbeId, 0, _probeLeft + (0.75 * SectionSize), _probeY);
        _regionizer.MoveEntity(ProbeId, 0, _probeLeft + (0.25 * SectionSize), _probeY);
    }

    /// <summary>The probe moves half a section across a section edge, there and back.</summary>
    [Benchmark(OperationsPerInvoke = 2)]
    public void MoveAcrossSectionEdge()
    {
        _regionizer.MoveEntity(ProbeId, 0, _probeLeft + (1.25 * SectionSize), _probeY);
        _regionizer.MoveEntity(ProbeId, 0, _probeLeft + (0.75 * SectionSize), _probeY);
    }

    /// <summary><see cref="Regionizer.TryMarkTicking"/> then <see cref="Regionizer.MarkNotTicking"/> on the probe's region.</summary>
    [Benchmark]
    public TickEndResult TickStartAndEndWithNoMerge()
    {
        _regionizer.TryMarkTicking(_probeRegion);
        return _regionizer.MarkNotTicking(_probeRegion);
    }

    /// <summary>One 30 Hz lockstep step: every region starts in ID order, moves its entities, runs its timers, then all end in ID order.</summary>
    [Benchmark]
    public void FullLockstepStep() => Step();

    private void BuildWorld()
    {
        var topology = SectionTopology.OpenWorld(SectionSize, MapMetres, MapMetres);

        // The run-config defaults (contracts/config-and-cli.md); T_min is 2 s of 60 Hz steps.
        var options = new RegionizerOptions(
            SectionSize,
            Anchoring,
            MergeGap: 0,
            new ClaimRadii(Infantry: 16, GroundVehicle: 24, Aircraft: 16, Traveller: 16, CapturePointZone: 20),
            SplitMinSections: 32,
            SplitMinDeadPercent: 25,
            SplitMinAgeSteps: 120,
            TimerMode.TickCounter);
        _regionizer = new Regionizer(topology, options);

        _homeX = new double[EntityCount];
        _homeY = new double[EntityCount];
        _homeZ = new double[EntityCount];
        _stepX = new double[EntityCount];
        _stepY = new double[EntityCount];
        _phase = new int[EntityCount];
        _members = new int[EntityCount + 1];
        _tick = 0;

        var random = DeterministicRandom.ForStream(Seed, "benchmark-placement");
        var fights = ScaleFights(BaselineFights, EntityCount);
        var id = 0;
        for (var f = 0; f < fights.Length; f++)
        {
            var (cx, cy) = FightCentres[f];
            var fightRadius = 8 * Math.Sqrt(fights[f]);
            for (var k = 0; k < fights[f]; k++, id++)
            {
                var draw = random.NextDouble();
                var (cls, ceiling) = draw < 0.80 ? (EntityClass.Infantry, 7.0)
                    : draw < 0.95 ? (EntityClass.GroundVehicle, 30.0)
                    : (EntityClass.Aircraft, 120.0);
                var (dx, dy) = random.UnitDisc();
                var (ux, uy) = random.UnitVector();
                var speed = random.NextDouble(0.5, 1) * ceiling;
                _homeX[id] = cx + (fightRadius * dx);
                _homeY[id] = cy + (fightRadius * dy);
                _homeZ[id] = cls == EntityClass.Aircraft ? 50 : 0;
                _stepX[id] = ux * speed * TickSeconds;
                _stepY[id] = uy * speed * TickSeconds;
                _phase[id] = random.NextInt(0, PeriodTicks - 1);
                var (x, y) = PositionAt(id, 0);
                _regionizer.AddEntity(id, cls, 0, x, y, _homeZ[id], fightTag: f);
            }
        }

        AssertClean("after placing the entities");
        for (var t = 0; t < WarmUpTicks; t++)
        {
            Step();
        }

        AssertClean("after warming up");
    }

    private void Step()
    {
        _tick++;
        _regionizer.AdvanceTo(_regionizer.Now + StepsPerTick);

        // Snapshot the live regions: tick ends can absorb regions and add transient ones.
        var live = _regionizer.RegionIds;
        if (_tickRegions.Length < live.Length)
        {
            _tickRegions = new long[live.Length * 2];
        }

        var count = live.Length;
        live.CopyTo(_tickRegions);
        var ticking = 0;
        for (var i = 0; i < count; i++)
        {
            if (_regionizer.TryMarkTicking(_tickRegions[i]))
            {
                _tickRegions[ticking++] = _tickRegions[i];
            }
        }

        for (var i = 0; i < ticking; i++)
        {
            var members = _regionizer.GetRegion(_tickRegions[i]).Entities;
            var n = members.Length;
            members.CopyTo(_members);
            for (var e = 0; e < n; e++)
            {
                var entity = _members[e];
                var (x, y) = PositionAt(entity, _tick);
                _regionizer.MoveEntity(entity, 0, x, y, _homeZ[entity]);
            }

            _regionizer.RunDueTimers(_tickRegions[i]);
        }

        for (var i = 0; i < ticking; i++)
        {
            _regionizer.MarkNotTicking(_tickRegions[i]);
        }
    }

    /// <summary>A triangle wave along the entity's line: from -P/4 to +P/4 steps and back, every P ticks.</summary>
    private (double X, double Y) PositionAt(int id, long tick)
    {
        var f = (int)((tick + _phase[id]) % PeriodTicks);
        var offset = f < PeriodTicks / 2 ? f - (PeriodTicks / 4) : (3 * PeriodTicks / 4) - f;
        return (_homeX[id] + (_stepX[id] * offset), _homeY[id] + (_stepY[id] * offset));
    }

    /// <summary>Scales fight sizes to a total: each rounds to the nearest integer and the first takes the remainder (data-model.md).</summary>
    private static int[] ScaleFights(int[] fights, int total)
    {
        var sum = fights.Sum();
        var scaled = new int[fights.Length];
        for (var i = 0; i < fights.Length; i++)
        {
            scaled[i] = (int)Math.Round((double)fights[i] * total / sum, MidpointRounding.AwayFromZero);
        }

        scaled[0] += total - scaled.Sum();
        return scaled;
    }

    private void AssertClean(string when)
    {
        var violations = new List<Violation>();
        if (InvariantChecker.Check(_regionizer, violations) > 0)
        {
            throw new InvalidOperationException($"{Describe()}: {violations.Count} invariant violation(s) {when}; first: {violations[0]}");
        }
    }

    private string Describe() => $"seed {Seed}, section size {SectionSize}, {Anchoring} anchoring";
}
