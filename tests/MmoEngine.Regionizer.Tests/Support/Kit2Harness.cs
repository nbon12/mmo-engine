using Xunit;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>
/// Drives a regionizer on a fixed 60 Hz time base for the region and portal tests. It places
/// entities at seeded spots inside named sections, starts and ends region ticks, and runs the
/// invariant checker at tick boundaries. Every failure message carries the test name, seed and step.
/// </summary>
/// <remarks>
/// The claim radii are chosen so the expected claim is easy to state from research D1:
/// section-anchored claims use 8 m, under the 16 m section side, so an entity claims the 3×3 block
/// around its section (edge neighbours are at distance 0 from its square, the next ring at 16 m);
/// position-anchored claims use 4 m and every spot is at least 5 m from each section edge, so an
/// entity claims only its own section. <see cref="Footprint"/> states that oracle.
/// </remarks>
internal sealed class Kit2Harness
{
    /// <summary>Section side in metres.</summary>
    public const int SectionSize = 16;

    /// <summary>Sections per side of the default open world.</summary>
    public const int MapSections = 32;

    /// <summary>Claim radius in section-anchored mode: the 3×3 block.</summary>
    public const double SectionModeRadius = 8;

    /// <summary>Claim radius in position-anchored mode: the own section only.</summary>
    public const double PositionModeRadius = 4;

    /// <summary>Lockstep ticks run at 30 Hz: every 2 base steps (research D7).</summary>
    public const long LockstepPeriod = 2;

    private const double Margin = 5;

    private readonly string _name;
    private ulong _state;

    public Kit2Harness(string name, int seed, RegionizerOptions options, SectionTopology? topology = null)
    {
        _name = name;
        Seed = seed;
        _state = (ulong)seed;
        Topology = topology ?? OpenWorld();
        Events = new Kit2Observer();
        R = new Regionizer(Topology, options, Events);
    }

    public Regionizer R { get; }

    public Kit2Observer Events { get; }

    public SectionTopology Topology { get; }

    public int Seed { get; }

    /// <summary>Test name, seed and step, for failure messages.</summary>
    public string Context => $"[{_name}, seed {Seed}, step {R.Now}]";

    /// <summary>A 512 m open world: one layer of 32×32 sections of 16 m.</summary>
    public static SectionTopology OpenWorld() =>
        SectionTopology.OpenWorld(SectionSize, SectionSize * MapSections, SectionSize * MapSections);

    /// <summary>
    /// Options with the harness radii for every class, or <paramref name="radius"/> when given.
    /// Splits are off unless the test sets the damping thresholds (N_min defaults to
    /// <see cref="int.MaxValue"/>).
    /// </summary>
    public static RegionizerOptions Options(
        ClaimAnchoring anchoring = ClaimAnchoring.Section,
        int mergeGap = 0,
        int splitMinSections = int.MaxValue,
        double splitMinDeadPercent = 0,
        long splitMinAgeSteps = 0,
        TimerMode timerMode = TimerMode.TickCounter,
        double? radius = null)
    {
        var r = radius ?? (anchoring == ClaimAnchoring.Section ? SectionModeRadius : PositionModeRadius);
        return new RegionizerOptions(
            SectionSize,
            anchoring,
            mergeGap,
            new ClaimRadii(r, r, r, r, r),
            splitMinSections,
            splitMinDeadPercent,
            splitMinAgeSteps,
            timerMode);
    }

    // ---------------------------------------------------------------- positions and entities

    /// <summary>A seeded spot inside section (<paramref name="sx"/>, <paramref name="sy"/>), at least 5 m from each edge.</summary>
    public (double X, double Y) Spot(int sx, int sy) =>
        ((sx * SectionSize) + Margin + (NextUnit() * (SectionSize - (2 * Margin))),
         (sy * SectionSize) + Margin + (NextUnit() * (SectionSize - (2 * Margin))));

    /// <summary>Adds an entity at a seeded spot in a section.</summary>
    public void Add(int entityId, int sx, int sy, EntityClass cls = EntityClass.Infantry, int layer = 0)
    {
        var (x, y) = Spot(sx, sy);
        R.AddEntity(entityId, cls, layer, x, y);
    }

    /// <summary>Moves an entity to a seeded spot in a section, during its region's tick.</summary>
    public void MoveInTick(int entityId, int sx, int sy, int layer = 0)
    {
        var (x, y) = Spot(sx, sy);
        MoveInTick(entityId, x, y, layer);
    }

    /// <summary>Moves an entity to an exact position, during its region's tick.</summary>
    public void MoveInTick(int entityId, double x, double y, int layer = 0)
    {
        var region = RegionOf(entityId);
        True(StateOf(region) == RegionState.Ticking, $"entity {entityId} moves only while its region {region} ticks");
        R.MoveEntity(entityId, layer, x, y);
    }

    /// <summary>Moves an entity to a seeded spot in a section at a tick boundary.</summary>
    public void MoveAtBoundary(int entityId, int sx, int sy, int layer = 0)
    {
        foreach (var id in R.RegionIds)
        {
            True(StateOf(id) != RegionState.Ticking, $"no region ticks at a boundary, but {id} does");
        }

        var (x, y) = Spot(sx, sy);
        R.MoveEntity(entityId, layer, x, y);
    }

    public long RegionOf(int entityId) => R.GetEntity(entityId).RegionId;

    /// <summary>A region's state, or <see cref="RegionState.Dead"/> once it is gone.</summary>
    public RegionState StateOf(long regionId) => R.TryGetState(regionId, out var state) ? state : RegionState.Dead;

    public int[] SectionsOf(long regionId) => R.GetRegion(regionId).Sections.ToArray();

    public int[] EntitiesOf(long regionId) => R.GetRegion(regionId).Entities.ToArray();

    public long[] PendingOf(long regionId) => R.GetRegion(regionId).PendingMergePartners.ToArray();

    public int[] ClaimOf(int entityId) => R.GetEntity(entityId).ClaimedSections.ToArray();

    public int Index(int layer, int sx, int sy) =>
        Topology.TryIndexOf(new SectionKey(layer, sx, sy), out var index) ? index : throw new ArgumentOutOfRangeException(nameof(sx));

    /// <summary>The section an entity stands in.</summary>
    public SectionKey SectionOf(int entityId)
    {
        var e = R.GetEntity(entityId);
        Assert.True(Topology.TrySectionAt(e.Layer, e.X, e.Y, out var index), $"{Context} entity {entityId} is off the map");
        return Topology.KeyOf(index);
    }

    /// <summary>
    /// The claim oracle (research D1) for the harness radii on an open layer: the 3×3 block clipped
    /// to the map in section mode, the own section in position mode.
    /// </summary>
    public HashSet<int> Footprint(int layer, int sx, int sy)
    {
        var half = R.Options.Anchoring == ClaimAnchoring.Section ? 1 : 0;
        var claim = new HashSet<int>();
        for (var y = sy - half; y <= sy + half; y++)
        {
            for (var x = sx - half; x <= sx + half; x++)
            {
                if (Topology.TryIndexOf(new SectionKey(layer, x, y), out var index))
                {
                    claim.Add(index);
                }
            }
        }

        return claim;
    }

    /// <summary>The claim oracle for an entity where it stands.</summary>
    public HashSet<int> Footprint(int entityId)
    {
        var key = SectionOf(entityId);
        return Footprint(key.Layer, key.X, key.Y);
    }

    // ---------------------------------------------------------------- ticks

    public void AdvanceTo(long step)
    {
        R.AdvanceTo(step);
        Events.Step = step;
    }

    /// <summary>Starts a region's tick and runs its due timers, as a tick body would first.</summary>
    public void Start(long regionId)
    {
        if (!R.TryMarkTicking(regionId))
        {
            Assert.Fail($"{Context} region {regionId} ({StateOf(regionId)}) could not start ticking");
        }

        R.RunDueTimers(regionId);
    }

    public TickEndResult End(long regionId) => R.MarkNotTicking(regionId);

    /// <summary>
    /// One lockstep tick (research D7): advance two base steps, start every live region in id order,
    /// run <paramref name="body"/>, end the same regions in id order, then check the invariants.
    /// </summary>
    public Dictionary<long, TickEndResult> Lockstep(Action? body = null)
    {
        AdvanceTo(R.Now + LockstepPeriod);
        var ids = R.RegionIds.ToArray();
        foreach (var id in ids)
        {
            Start(id);
        }

        body?.Invoke();
        var results = new Dictionary<long, TickEndResult>();
        foreach (var id in ids)
        {
            results[id] = End(id);
        }

        AssertClean();
        return results;
    }

    /// <summary>Runs the invariant checker and returns what it found.</summary>
    public List<Violation> Check()
    {
        var violations = new List<Violation>();
        InvariantChecker.Check(R, violations);
        return violations;
    }

    /// <summary>Runs the invariant checker at a tick boundary and fails on any violation.</summary>
    public void AssertClean()
    {
        var violations = Check();
        if (violations.Count > 0)
        {
            Assert.Fail($"{Context} invariant violations: {string.Join("; ", violations)}");
        }
    }

    // ---------------------------------------------------------------- assertions

    public void True(bool condition, string what) => Assert.True(condition, $"{Context} {what}");

    public void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{Context} {what}: expected {expected}, actual {actual}");
        }
    }

    /// <summary>Asserts two section sets are equal, naming the sections by key on failure.</summary>
    public void SameSections(IEnumerable<int> expected, IEnumerable<int> actual, string what)
    {
        var e = expected.Distinct().Order().ToArray();
        var a = actual.Distinct().Order().ToArray();
        if (!e.SequenceEqual(a))
        {
            Assert.Fail($"{Context} {what}: expected {{{Keys(e)}}}, actual {{{Keys(a)}}}");
        }
    }

    /// <summary>Section keys for a message.</summary>
    public string Keys(IEnumerable<int> sections) =>
        string.Join(" ", sections.Select(s => { var k = Topology.KeyOf(s); return $"({k.Layer},{k.X},{k.Y})"; }));

    // SplitMix64: a seeded, platform-independent stream for spots inside sections.
    private double NextUnit()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
