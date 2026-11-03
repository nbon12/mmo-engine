using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>A base on a lane, with its objectives (where its capture points stand).</summary>
/// <param name="Index">Global index, lane-major: lane <c>l</c>, base <c>b</c> is <c>l * basesPerLane + b</c>.</param>
/// <param name="Lane">The lane's index.</param>
/// <param name="IndexOnLane">Position along the lane, from its start.</param>
/// <param name="X">Centre, metres.</param>
/// <param name="Y">Centre, metres.</param>
/// <param name="Objectives">Objective positions, metres.</param>
public sealed record Base(int Index, int Lane, int IndexOnLane, double X, double Y, IReadOnlyList<(double X, double Y)> Objectives);

/// <summary>A straight lane at height <paramref name="Y"/> from <paramref name="StartX"/> to <paramref name="EndX"/>, joining its bases.</summary>
public sealed record Lane(int Index, double Y, double StartX, double EndX, IReadOnlyList<Base> Bases)
{
    public double Length => EndX - StartX;
}

/// <summary>
/// The generated open-world map (data-model.md, Scenario layout): straight, parallel lanes across the
/// map, each with evenly spaced bases. The default is 8 × 8 km with 3 lanes of 4 bases.
/// </summary>
public sealed class MapLayout
{
    private MapLayout(double width, double height, IReadOnlyList<Lane> lanes)
    {
        WidthMetres = width;
        HeightMetres = height;
        Lanes = lanes;
        Bases = [.. lanes.SelectMany(l => l.Bases).OrderBy(b => b.Index)];
    }

    public double WidthMetres { get; }

    public double HeightMetres { get; }

    public IReadOnlyList<Lane> Lanes { get; }

    /// <summary>Every base, by global index.</summary>
    public IReadOnlyList<Base> Bases { get; }

    /// <summary>
    /// Lanes at heights <c>H·(l + 1)/(lanes + 1)</c>; bases at <c>x = m + b·(W − 2m)/(n − 1)</c> with a
    /// margin <c>m = W/(2n)</c> (the centre when a lane has one base). Each base's objectives are
    /// uniform points within <paramref name="objectiveSpreadMetres"/> of its centre, drawn from
    /// <paramref name="layoutRandom"/>.
    /// </summary>
    public static MapLayout Generate(
        double widthMetres,
        double heightMetres,
        int lanes,
        int basesPerLane,
        int objectivesPerBase,
        double objectiveSpreadMetres,
        DeterministicRandom layoutRandom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(basesPerLane, 1);
        ArgumentNullException.ThrowIfNull(layoutRandom);
        var margin = widthMetres / (2.0 * basesPerLane);
        var xs = new double[basesPerLane];
        for (var b = 0; b < basesPerLane; b++)
        {
            xs[b] = basesPerLane == 1 ? widthMetres / 2 : margin + (b * (widthMetres - (2 * margin)) / (basesPerLane - 1));
        }

        var built = new List<Lane>(lanes);
        for (var l = 0; l < lanes; l++)
        {
            var y = heightMetres * (l + 1) / (lanes + 1);
            built.Add(BuildLane(l, y, xs, l * basesPerLane, widthMetres, heightMetres, objectivesPerBase, objectiveSpreadMetres, layoutRandom));
        }

        return new MapLayout(widthMetres, heightMetres, built);
    }

    /// <summary>One lane across the middle of the map with bases at the given x positions.</summary>
    public static MapLayout SingleLane(
        double widthMetres,
        double heightMetres,
        IReadOnlyList<double> baseXs,
        int objectivesPerBase,
        double objectiveSpreadMetres,
        DeterministicRandom layoutRandom)
    {
        ArgumentNullException.ThrowIfNull(baseXs);
        ArgumentNullException.ThrowIfNull(layoutRandom);
        var lane = BuildLane(0, heightMetres / 2, [.. baseXs], 0, widthMetres, heightMetres, objectivesPerBase, objectiveSpreadMetres, layoutRandom);
        return new MapLayout(widthMetres, heightMetres, [lane]);
    }

    /// <summary>The base after <paramref name="current"/> on its lane in <paramref name="direction"/> (+1 or −1), bouncing at the ends.</summary>
    public (Base Next, int Direction) NextOnLane(Base current, int direction)
    {
        ArgumentNullException.ThrowIfNull(current);
        var lane = Lanes[current.Lane];
        if (lane.Bases.Count == 1)
        {
            return (current, direction);
        }

        var next = current.IndexOnLane + direction;
        if (next < 0 || next >= lane.Bases.Count)
        {
            direction = -direction;
            next = current.IndexOnLane + direction;
        }

        return (lane.Bases[next], direction);
    }

    /// <summary>Clamps a point into the map, keeping it strictly inside the far edges.</summary>
    public (double X, double Y) Clamp(double x, double y) =>
        (Math.Min(Math.Max(x, 0), WidthMetres - EdgeMargin), Math.Min(Math.Max(y, 0), HeightMetres - EdgeMargin));

    private const double EdgeMargin = 1e-3;

    private static Lane BuildLane(
        int laneIndex,
        double y,
        double[] xs,
        int firstIndex,
        double width,
        double height,
        int objectivesPerBase,
        double spread,
        DeterministicRandom random)
    {
        var bases = new List<Base>(xs.Length);
        for (var b = 0; b < xs.Length; b++)
        {
            var objectives = new List<(double X, double Y)>(objectivesPerBase);
            for (var o = 0; o < objectivesPerBase; o++)
            {
                var (dx, dy) = random.UnitDisc();
                var ox = Math.Min(Math.Max(xs[b] + (dx * spread), 0), width - EdgeMargin);
                var oy = Math.Min(Math.Max(y + (dy * spread), 0), height - EdgeMargin);
                objectives.Add((ox, oy));
            }

            bases.Add(new Base(firstIndex + b, laneIndex, b, xs[b], y, objectives));
        }

        return new Lane(laneIndex, y, xs.Min(), xs.Max(), bases);
    }
}
