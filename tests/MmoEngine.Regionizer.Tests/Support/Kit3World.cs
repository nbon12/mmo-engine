using Xunit;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>Explicit options, topologies and coordinates for the kit-3 tests. Values only, no rules.</summary>
internal static class Kit3World
{
    /// <summary>Section side in metres.</summary>
    public const int SectionSize = 16;

    /// <summary>
    /// Claim radii in metres. Infantry and travellers claim 8 m: in section mode that is the 3 × 3
    /// block around the entity's section.
    /// </summary>
    public static readonly ClaimRadii Radii = new(Infantry: 8, GroundVehicle: 24, Aircraft: 40, Traveller: 8, CapturePointZone: 20);

    /// <summary>Options with explicit damping. The defaults never allow a split.</summary>
    public static RegionizerOptions Options(
        TimerMode timerMode,
        ClaimAnchoring anchoring = ClaimAnchoring.Section,
        int mergeGap = 0,
        int splitMinSections = int.MaxValue,
        double splitMinDeadPercent = 100,
        long splitMinAgeSteps = 0) =>
        new(SectionSize, anchoring, mergeGap, Radii, splitMinSections, splitMinDeadPercent, splitMinAgeSteps, timerMode);

    /// <summary>A single open-world layer of <paramref name="widthSections"/> × <paramref name="heightSections"/> sections.</summary>
    public static SectionTopology OpenWorld(int widthSections, int heightSections) =>
        SectionTopology.OpenWorld(SectionSize, widthSections * SectionSize, heightSections * SectionSize);

    /// <summary>
    /// One layer of <paramref name="widthSections"/> × <paramref name="heightSections"/> sections: area 0
    /// left of column <paramref name="edgeColumn"/>, area 1 from it on, and no portals.
    /// </summary>
    public static SectionTopology TwoAreas(int widthSections, int heightSections, int edgeColumn)
    {
        var tags = new ushort[widthSections * heightSections];
        for (var y = 0; y < heightSections; y++)
        {
            for (var x = edgeColumn; x < widthSections; x++)
            {
                tags[(y * widthSections) + x] = 1;
            }
        }

        return SectionTopology.Build(SectionSize, [new LayerSpec(0, 0, 0, widthSections, heightSections, tags)], []);
    }

    /// <summary>
    /// Layers 0 and 1, each <paramref name="sideSections"/> square at the same origin, joined by portal
    /// <paramref name="portalId"/> between (0, x, y) and (1, x, y).
    /// </summary>
    public static SectionTopology TwoLayers(int sideSections, int portalId, int x, int y, bool open) =>
        SectionTopology.Build(
            SectionSize,
            [
                new LayerSpec(0, 0, 0, sideSections, sideSections, new ushort[sideSections * sideSections]),
                new LayerSpec(1, 0, 0, sideSections, sideSections, new ushort[sideSections * sideSections]),
            ],
            [new PortalSpec(portalId, new SectionKey(0, x, y), new SectionKey(1, x, y), open)]);

    /// <summary>The centre of section coordinate <paramref name="section"/>, in metres.</summary>
    public static double Centre(int section) => (section * SectionSize) + (SectionSize / 2.0);

    /// <summary>The dense index of a section key that must exist.</summary>
    public static int Index(SectionTopology topology, int layer, int x, int y) =>
        topology.TryIndexOf(new SectionKey(layer, x, y), out var index)
            ? index
            : throw new ArgumentOutOfRangeException(nameof(x), $"No section ({layer}, {x}, {y}).");
}

/// <summary>Asserts the invariant checker is clean, naming the context (seed, tick) on failure.</summary>
internal static class Kit3Invariants
{
    public static void AssertClean(Regionizer regionizer, string context)
    {
        var violations = new List<Violation>();
        var found = InvariantChecker.Check(regionizer, violations);
        Assert.True(found == 0, $"{context}: {found} invariant violation(s): {string.Join("; ", violations)}");
    }
}

/// <summary>
/// Drives region ticks on an explicit time base, like the lockstep scheduler: it starts the due ready
/// regions in ID order and fires each one's due timers first, lets the test run the tick bodies, then
/// ends the started regions in ID order and asserts the invariant checker is clean at the boundary.
/// </summary>
internal sealed class Kit3Driver(Regionizer regionizer)
{
    private readonly List<long> _started = [];

    /// <summary>The regions started by the current step, ascending.</summary>
    public IReadOnlyList<long> Started => _started;

    /// <summary>Advances to <paramref name="now"/> and starts every ready region <paramref name="due"/> selects.</summary>
    public void Begin(long now, Func<long, bool>? due = null)
    {
        if (_started.Count != 0)
        {
            throw new InvalidOperationException("The previous step has not ended.");
        }

        regionizer.AdvanceTo(now);
        foreach (var id in regionizer.RegionIds.ToArray())
        {
            if ((due is null || due(id)) && regionizer.TryMarkTicking(id))
            {
                _started.Add(id);
                regionizer.RunDueTimers(id);
            }
        }
    }

    /// <summary>Ends every started region in ID order, then checks the invariants.</summary>
    public void End(string context)
    {
        foreach (var id in _started)
        {
            regionizer.MarkNotTicking(id);
        }

        _started.Clear();
        Kit3Invariants.AssertClean(regionizer, context);
    }

    /// <summary>One whole step: begin, run <paramref name="body"/> for each started region, end.</summary>
    public void Step(long now, Func<long, bool>? due = null, Action<long>? body = null, string context = "")
    {
        Begin(now, due);
        if (body is not null)
        {
            foreach (var id in _started.ToArray())
            {
                body(id);
            }
        }

        End(string.IsNullOrEmpty(context) ? $"step at {now}" : context);
    }
}
