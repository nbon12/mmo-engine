using CsCheck;
using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Properties;

/// <summary>
/// Property tests for merges, splits and timer offsets (T019; constitution, Testing Standards). CsCheck
/// generates random operation sequences: add, move, remove, two-step teleport, portal toggles,
/// capture-point occupancy, timers, time steps, and tick starts and ends of random regions. They run
/// in every anchoring mode, merge gap and timer mode. At every tick boundary the invariant checker is
/// clean, every pending timer sits with its entity's region and keeps its deadline, and with a merge
/// gap of 1 no two regions own neighbouring sections of one area (FR-005). Every timer fires exactly
/// when an independent shadow model says it is due, and only then.
/// </summary>
public sealed class RegionizerPropertyTests
{
    private const ulong BaseSeed = 13_019;
    private const uint SequencesPerCase = 150;

    // CORE-BUG: fails while a heavy projectile's timers stay behind when it changes region or lands on unowned ground (minimal repros in Timers/TimerTests.cs).
    [Theory]
    [InlineData(ClaimAnchoring.Section, 0, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Section, 0, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Section, 1, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Section, 1, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Position, 0, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Position, 0, TimerMode.Absolute)]
    [InlineData(ClaimAnchoring.Position, 1, TimerMode.TickCounter)]
    [InlineData(ClaimAnchoring.Position, 1, TimerMode.Absolute)]
    public void RandomOperationSequencesKeepInvariantsAndTimerDeadlines(ClaimAnchoring anchoring, int mergeGap, TimerMode timerMode)
    {
        var coverage = new Kit3Coverage();
        for (var i = 0u; i < SequencesPerCase; i++)
        {
            // CsCheck draws every iteration after the first from a fresh random seed, so each sequence
            // runs alone (one iteration, one thread) from its own fixed seed to stay reproducible.
            var seed = new PCG(i, BaseSeed).ToString();
            Kit3Sequence.Generator.Sample(
                sequence => new Kit3PropertyRun(anchoring, mergeGap, timerMode, sequence, seed).Run(coverage),
                seed: seed,
                iter: 1,
                threads: 1,
                print: sequence => sequence.ToString());
        }

        // The sequences must reach the hard cases, or a clean result would mean nothing.
        TestContext.Current.TestOutputHelper?.WriteLine(coverage.ToString());
        Assert.True(coverage.Merges > 0, $"No merge: {coverage}");
        Assert.True(coverage.MergesOwedAtTickEnd > 0, $"No region ended its tick still owing a merge to a ticking region: {coverage}");
        Assert.True(coverage.TransientRegions > 0, $"No transient region: {coverage}");
        Assert.True(coverage.Splits > 0, $"No split: {coverage}");
        Assert.True(coverage.TimersFired > 0, $"No timer fired: {coverage}");
        Assert.True(coverage.TeleportsCompleted > 0, $"No teleport completed: {coverage}");
        Assert.True(coverage.PortalToggles > 0, $"No portal toggled: {coverage}");
    }
}

/// <summary>What the sequences of one case exercised, summed over its runs.</summary>
internal sealed class Kit3Coverage
{
    public int Boundaries { get; set; }

    public int Merges { get; set; }

    public int MergesOwedAtTickEnd { get; set; }

    public int TransientRegions { get; set; }

    public int Splits { get; set; }

    public int TimersAdded { get; set; }

    public int TimersFired { get; set; }

    public int TeleportsCompleted { get; set; }

    public int PortalToggles { get; set; }

    public override string ToString() =>
        $"{Boundaries} boundaries, {Merges} merges, {MergesOwedAtTickEnd} merges owed at tick end, {TransientRegions} transient regions, " +
        $"{Splits} splits, {TimersAdded} timers set, {TimersFired} fired, {TeleportsCompleted} teleports, {PortalToggles} portal toggles";
}

/// <summary>The kinds of operation a generated sequence applies.</summary>
internal enum Kit3OpKind
{
    Add,
    Move,
    Remove,
    BeginTeleport,
    CompleteTeleport,
    TogglePortal,
    SetOccupied,
    StartTick,
    EndTick,
    AddTimer,
    Advance,
}

/// <summary>One generated operation. A, B and C are raw draws the run turns into picks and positions.</summary>
internal readonly record struct Kit3Op(Kit3OpKind Kind, int A, int B, int C)
{
    public override string ToString() => $"{Kind}({A},{B},{C})";
}

/// <summary>A generated case: split damping thresholds and the operations to apply.</summary>
internal sealed record Kit3Sequence(int SplitMinSections, int SplitMinDeadPercent, int SplitMinAgeSteps, Kit3Op[] Ops)
{
    private static readonly Gen<Kit3OpKind> KindGenerator = Gen.FrequencyConst(
        (14, Kit3OpKind.Add),
        (30, Kit3OpKind.Move),
        (3, Kit3OpKind.Remove),
        (4, Kit3OpKind.BeginTeleport),
        (6, Kit3OpKind.CompleteTeleport),
        (3, Kit3OpKind.TogglePortal),
        (2, Kit3OpKind.SetOccupied),
        (14, Kit3OpKind.StartTick),
        (12, Kit3OpKind.EndTick),
        (10, Kit3OpKind.AddTimer),
        (4, Kit3OpKind.Advance));

    private static readonly Gen<Kit3Op> OpGenerator = Gen.Select(
        KindGenerator,
        Gen.Int[0, 999_999],
        Gen.Int[0, 999_999],
        Gen.Int[0, 999_999],
        (kind, a, b, c) => new Kit3Op(kind, a, b, c));

    public static readonly Gen<Kit3Sequence> Generator = Gen.Select(
        Gen.Int[1, 8],
        Gen.Int[0, 60],
        Gen.Int[0, 8],
        OpGenerator.Array[40, 140],
        (minSections, deadPercent, minAge, ops) => new Kit3Sequence(minSections, deadPercent, minAge, ops));

    public override string ToString() =>
        $"N_min {SplitMinSections}, p_dead {SplitMinDeadPercent}, T_min {SplitMinAgeSteps}; {string.Join(" ", Ops)}";
}

/// <summary>
/// Applies one generated sequence to a regionizer the way a scheduler may: entities move, arrive,
/// leave and get timers during their own region's tick or at a tick boundary (no region ticking);
/// unowned arrivals, portal toggles and time steps happen only at a boundary. A started region fires
/// its due timers first, so membership at tick start decides which timers count the tick.
/// </summary>
internal sealed class Kit3PropertyRun
{
    private const int BuildingPortal = 1;
    private const int UndergroundPortal = 2;
    private const int DrainSteps = 12;

    private readonly Regionizer _regionizer;
    private readonly Kit3RecordingObserver _observer = new();
    private readonly Kit3TimerShadow _shadow;
    private readonly Kit3Sequence _sequence;
    private readonly TimerMode _timerMode;
    private readonly string _label;
    private readonly Dictionary<int, (int Layer, double X, double Y)> _transit = [];
    private int _nextEntityId = 1;
    private long _now;
    private int _boundaries;
    private int _teleportsCompleted;
    private int _portalToggles;
    private int _mergesOwedAtTickEnd;
    private int _timersAdded;
    private string _context;

    public Kit3PropertyRun(ClaimAnchoring anchoring, int mergeGap, TimerMode timerMode, Kit3Sequence sequence, string seed)
    {
        var options = new RegionizerOptions(
            Kit3World.SectionSize,
            anchoring,
            mergeGap,
            Kit3World.Radii,
            sequence.SplitMinSections,
            sequence.SplitMinDeadPercent,
            sequence.SplitMinAgeSteps,
            timerMode);
        _regionizer = new Regionizer(Topology(), options, _observer);
        _shadow = new Kit3TimerShadow(timerMode);
        _sequence = sequence;
        _timerMode = timerMode;
        _label = $"seed {seed} ({anchoring}, gap {mergeGap}, {timerMode})";
        _context = _label;
    }

    private bool AtBoundary => Ticking().Length == 0;

    /// <summary>Applies the sequence, closes the open ticks, drains timers, and adds what it exercised to <paramref name="coverage"/>.</summary>
    public void Run(Kit3Coverage coverage)
    {
        for (var i = 0; i < _sequence.Ops.Length; i++)
        {
            _context = $"{_label}, op {i} {_sequence.Ops[i]}";
            Apply(_sequence.Ops[i]);
            if (AtBoundary)
            {
                CheckBoundary();
            }
        }

        _context = $"{_label}, closing ticks";
        foreach (var id in Ticking())
        {
            if (_regionizer.MarkNotTicking(id).State == RegionState.Transient)
            {
                _mergesOwedAtTickEnd++;
            }
        }

        CheckBoundary();

        // Lockstep steps at 30 Hz give every pending timer of an owned entity its chance to fire.
        for (var step = 0; step < DrainSteps; step++)
        {
            _context = $"{_label}, drain step {step}";
            AdvanceBy(2);
            var started = new List<long>();
            foreach (var id in _regionizer.RegionIds.ToArray())
            {
                if (StartTick(id))
                {
                    started.Add(id);
                }
            }

            foreach (var id in started)
            {
                _regionizer.MarkNotTicking(id);
            }

            CheckBoundary();
        }

        coverage.Boundaries += _boundaries;
        coverage.Merges += _observer.Merges.Count;
        coverage.MergesOwedAtTickEnd += _mergesOwedAtTickEnd;
        coverage.TransientRegions += _observer.Created.Count(c => c.Reason == RegionCreationReason.Transient);
        coverage.Splits += _observer.Splits.Count;
        coverage.TimersAdded += _timersAdded;
        coverage.TimersFired += _observer.TimersFired.Count;
        coverage.TeleportsCompleted += _teleportsCompleted;
        coverage.PortalToggles += _portalToggles;
    }

    /// <summary>
    /// Layer 0: 16 × 16 sections, with a 4 × 4 building (area 1) whose doorway is portal 1, initially
    /// closed. Layer 1: a 6 × 6 underground area under sections (2–7, 2–7), reached through portal 2,
    /// initially open.
    /// </summary>
    private static SectionTopology Topology()
    {
        var surface = new ushort[16 * 16];
        for (var y = 10; y < 14; y++)
        {
            for (var x = 10; x < 14; x++)
            {
                surface[(y * 16) + x] = 1;
            }
        }

        return SectionTopology.Build(
            Kit3World.SectionSize,
            [new LayerSpec(0, 0, 0, 16, 16, surface), new LayerSpec(1, 2, 2, 6, 6, new ushort[36])],
            [
                new PortalSpec(BuildingPortal, new SectionKey(0, 9, 11), new SectionKey(0, 10, 11), Open: false),
                new PortalSpec(UndergroundPortal, new SectionKey(0, 4, 4), new SectionKey(1, 4, 4), Open: true),
            ]);
    }

    private static (double Min, double Size) Bounds(int layer) => layer == 0 ? (0, 256) : (32, 96);

    private static (int Layer, double X, double Y) Location(int layerDraw, int a, int b)
    {
        var layer = layerDraw % 4 == 0 ? 1 : 0;
        return LocationOn(layer, a, b);
    }

    /// <summary>A position on a quarter-metre grid, so section edges and corners come up.</summary>
    private static (int Layer, double X, double Y) LocationOn(int layer, int a, int b)
    {
        var (min, size) = Bounds(layer);
        var cells = (int)(size * 4);
        return (layer, min + ((a % cells) / 4.0), min + ((b % cells) / 4.0));
    }

    /// <summary>A step of up to 20 m on each axis, clamped to the layer.</summary>
    private static (int Layer, double X, double Y) StepFrom(int layer, double x, double y, int a, int b)
    {
        var (min, size) = Bounds(layer);
        var max = min + size - 0.25;
        return (layer, Math.Clamp(x + (((a % 81) - 40) / 2.0), min, max), Math.Clamp(y + (((b % 81) - 40) / 2.0), min, max));
    }

    private static bool TryPick<T>(IReadOnlyList<T> items, int draw, out T item)
    {
        if (items.Count == 0)
        {
            item = default!;
            return false;
        }

        item = items[draw % items.Count];
        return true;
    }

    private void Apply(Kit3Op op)
    {
        var boundary = AtBoundary;
        switch (op.Kind)
        {
            case Kit3OpKind.Add:
                AddEntity(op, boundary);
                break;
            case Kit3OpKind.Move:
                if (TryPick(Movable(boundary), op.A, out var mover))
                {
                    var from = _regionizer.GetEntity(mover);
                    var to = op.B % 3 == 0 ? LocationOn(from.Layer, op.C, op.A) : StepFrom(from.Layer, from.X, from.Y, op.B, op.C);
                    _regionizer.MoveEntity(mover, to.Layer, to.X, to.Y);
                }

                break;
            case Kit3OpKind.Remove:
                if (TryPick(boundary ? _regionizer.EntityIds.ToArray() : Movable(false), op.A, out var removed))
                {
                    _regionizer.RemoveEntity(removed);
                    _shadow.RemoveEntity(removed);
                    _transit.Remove(removed);
                }

                break;
            case Kit3OpKind.BeginTeleport:
                if (TryPick(Movable(boundary), op.A, out var traveller))
                {
                    var destination = Location(op.A / 7, op.B, op.C);
                    _regionizer.BeginTeleport(traveller, destination.Layer, destination.X, destination.Y);
                    _transit[traveller] = destination;
                }

                break;
            case Kit3OpKind.CompleteTeleport:
                if (TryPick(_transit.Keys.Order().ToArray(), op.A, out var arrival))
                {
                    // Into an owned location only during its owner's tick; into unowned ground only at a boundary.
                    var (layer, x, y) = _transit[arrival];
                    var owner = _regionizer.OwnerOfLocation(layer, x, y);
                    if (owner == 0 ? boundary : IsTicking(owner))
                    {
                        _regionizer.CompleteTeleport(arrival);
                        _transit.Remove(arrival);
                        _teleportsCompleted++;
                    }
                }

                break;
            case Kit3OpKind.TogglePortal:
                if (boundary)
                {
                    var portal = op.A % 2 == 0 ? BuildingPortal : UndergroundPortal;
                    _regionizer.SetPortalOpen(portal, !_regionizer.IsPortalOpen(portal));
                    _portalToggles++;
                }

                break;
            case Kit3OpKind.SetOccupied:
                var points = Movable(boundary).Where(id => _regionizer.GetEntity(id).Class == EntityClass.CapturePoint).ToArray();
                if (TryPick(points, op.A, out var point))
                {
                    _regionizer.SetCapturePointOccupied(point, op.B % 2 == 0);
                }

                break;
            case Kit3OpKind.StartTick:
                if (TryPick(InState(RegionState.Ready), op.A, out var starting))
                {
                    StartTick(starting);
                }

                break;
            case Kit3OpKind.EndTick:
                if (TryPick(Ticking(), op.A, out var ending) && _regionizer.MarkNotTicking(ending).State == RegionState.Transient)
                {
                    _mergesOwedAtTickEnd++;
                }

                break;
            case Kit3OpKind.AddTimer:
                var owned = Movable(boundary).Where(id => _regionizer.GetEntity(id).RegionId != 0).ToArray();
                if (TryPick(owned, op.A, out var holder))
                {
                    var delay = _timerMode == TimerMode.TickCounter ? 1 + (op.B % 8) : 1 + (op.B % 20);
                    var timerId = _regionizer.AddTimer(holder, delay);
                    _shadow.Add(timerId, holder, delay, _now);
                    _timersAdded++;
                }

                break;
            case Kit3OpKind.Advance:
                if (boundary)
                {
                    AdvanceBy(1 + (op.A % 3));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op.Kind, "Unknown operation.");
        }
    }

    /// <summary>
    /// At a boundary, anywhere. During ticks, an arrival next to an entity of a ticking region, applied
    /// only if a ticking region owns the spot.
    /// </summary>
    private void AddEntity(Kit3Op op, bool boundary)
    {
        (int Layer, double X, double Y) at;
        if (boundary)
        {
            at = Location(op.A / 6, op.B, op.C);
        }
        else
        {
            if (!TryPick(Movable(false), op.A / 6, out var near))
            {
                return;
            }

            var anchor = _regionizer.GetEntity(near);
            at = StepFrom(anchor.Layer, anchor.X, anchor.Y, op.B, op.C);
            if (!IsTicking(_regionizer.OwnerOfLocation(at.Layer, at.X, at.Y)))
            {
                return;
            }
        }

        _regionizer.AddEntity(_nextEntityId++, (EntityClass)(op.A % 6), at.Layer, at.X, at.Y, 0, fightTag: (op.C % 3) - 1);
    }

    /// <summary>Starts a ready region, fires its due timers and checks them against the shadow.</summary>
    private bool StartTick(long regionId)
    {
        if (!_regionizer.TryMarkTicking(regionId))
        {
            return false;
        }

        var due = _shadow.TickStarted(_regionizer.GetRegion(regionId).Entities, _now);
        var before = _observer.TimersFired.Count;
        _regionizer.RunDueTimers(regionId);
        _shadow.VerifyFired(regionId, _now, due, _observer.TimersFired.GetRange(before, _observer.TimersFired.Count - before), _context);
        return true;
    }

    private void AdvanceBy(long steps)
    {
        _now += steps;
        _regionizer.AdvanceTo(_now);
    }

    private void CheckBoundary()
    {
        _boundaries++;
        Kit3Invariants.AssertClean(_regionizer, _context);
        _shadow.VerifyPlacement(_regionizer, _context);
        if (_regionizer.Options.MergeGap == 1)
        {
            AssertNoNeighbouringRegions();
        }
    }

    /// <summary>With a merge gap of 1, owned eight-neighbours in one area and layer share a region.</summary>
    private void AssertNoNeighbouringRegions()
    {
        var topology = _regionizer.Topology;
        for (var s = 0; s < topology.SectionCount; s++)
        {
            var owner = _regionizer.OwnerOf(s);
            if (owner == 0)
            {
                continue;
            }

            var key = topology.KeyOf(s);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if ((dx != 0 || dy != 0)
                        && topology.TryIndexOf(new SectionKey(key.Layer, key.X + dx, key.Y + dy), out var n)
                        && topology.AreaOf(n) == topology.AreaOf(s))
                    {
                        var other = _regionizer.OwnerOf(n);
                        Assert.True(other == 0 || other == owner, $"{_context}: merge gap 1, but neighbours {key} (region {owner}) and {topology.KeyOf(n)} (region {other}) are in different regions");
                    }
                }
            }
        }
    }

    /// <summary>Entities that may act now: not in transit, and at a boundary or in a ticking region.</summary>
    private int[] Movable(bool boundary) =>
        _regionizer.EntityIds.ToArray()
            .Where(id =>
            {
                var entity = _regionizer.GetEntity(id);
                return !entity.InTransit && (boundary || IsTicking(entity.RegionId));
            })
            .ToArray();

    private long[] Ticking() => InState(RegionState.Ticking);

    private long[] InState(RegionState state) =>
        _regionizer.RegionIds.ToArray().Where(id => _regionizer.TryGetState(id, out var s) && s == state).ToArray();

    private bool IsTicking(long regionId) => _regionizer.TryGetState(regionId, out var state) && state == RegionState.Ticking;
}
