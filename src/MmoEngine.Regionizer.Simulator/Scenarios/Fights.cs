namespace MmoEngine.Regionizer.Simulator.Scenarios;

/// <summary>
/// A fight at a base. Its players carry its tag (infantry and ground vehicles; aircraft carry none,
/// per the spec's clarifications). Scenarios such as Migration move it between bases at step boundaries.
/// A fight in a designed area (a bunker) stands on that area's layer and stays inside its bounds.
/// </summary>
public sealed class Fight
{
    public Fight(int index, int tag, Base atBase, int players, double radius, MetreBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(atBase);
        Index = index;
        Tag = tag;
        Players = players;
        Radius = radius;
        Base = atBase;
        Bounds = bounds;
    }

    /// <summary>The section layer its players stand on: its base's layer.</summary>
    public int Layer => Base.Layer;

    /// <summary>Where its players may go, when narrower than the map (a bunker); null means the whole map.</summary>
    public MetreBounds? Bounds { get; }

    public int Index { get; }

    /// <summary>The fight tag its players carry (≥ 0).</summary>
    public int Tag { get; }

    /// <summary>Players placed in the fight (infantry, ground vehicles and aircraft).</summary>
    public int Players { get; }

    /// <summary>Metres: the fight radius per player count times √players.</summary>
    public double Radius { get; }

    /// <summary>The base it is at; its centre and objectives are the base's.</summary>
    public Base Base { get; private set; }

    public double CenterX => Base.X;

    public double CenterY => Base.Y;

    /// <summary>Direction along its lane for the next move (+1 or −1).</summary>
    public int Direction { get; set; } = 1;

    /// <summary>Infantry entity IDs, ascending.</summary>
    public List<int> Infantry { get; } = [];

    /// <summary>Ground vehicle entity IDs, ascending.</summary>
    public List<int> GroundVehicles { get; } = [];

    /// <summary>Aircraft entity IDs, ascending.</summary>
    public List<int> Aircraft { get; } = [];

    /// <summary>Moves the fight to another base.</summary>
    public void MoveTo(Base target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Base = target;
    }
}

/// <summary>The fights of a run and which fight is at each base.</summary>
public sealed class FightTable
{
    private readonly List<Fight> _fights = [];

    public IReadOnlyList<Fight> Fights => _fights;

    public void Add(Fight fight)
    {
        ArgumentNullException.ThrowIfNull(fight);
        if (fight.Index != _fights.Count)
        {
            throw new ArgumentException("Fights are added in index order.", nameof(fight));
        }

        _fights.Add(fight);
    }

    /// <summary>The fight at a base, lowest index first, or null.</summary>
    public Fight? AtBase(int baseIndex)
    {
        foreach (var fight in _fights)
        {
            if (fight.Base.Index == baseIndex)
            {
                return fight;
            }
        }

        return null;
    }
}

/// <summary>
/// Population scaling (data-model.md): <c>population.total</c> scales every count in proportion;
/// each count is rounded to the nearest integer (half away from zero) and the remainder goes to the
/// first fight. Integer arithmetic only.
/// </summary>
public static class PopulationScaler
{
    /// <summary>Scales <paramref name="fights"/> and <paramref name="others"/> so that together they sum to <paramref name="total"/>.</summary>
    public static (int[] Fights, int[] Others) Scale(IReadOnlyList<int> fights, IReadOnlyList<int> others, int total)
    {
        ArgumentNullException.ThrowIfNull(fights);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        long sum = 0;
        foreach (var c in fights.Concat(others))
        {
            ArgumentOutOfRangeException.ThrowIfNegative(c);
            sum += c;
        }

        var scaledFights = new int[fights.Count];
        var scaledOthers = new int[others.Count];
        if (sum == 0)
        {
            return (scaledFights, scaledOthers);
        }

        long assigned = 0;
        for (var i = 0; i < fights.Count; i++)
        {
            scaledFights[i] = Round(fights[i], total, sum);
            assigned += scaledFights[i];
        }

        for (var i = 0; i < others.Count; i++)
        {
            scaledOthers[i] = Round(others[i], total, sum);
            assigned += scaledOthers[i];
        }

        var remainder = total - assigned;
        if (scaledFights.Length > 0)
        {
            scaledFights[0] = (int)Math.Max(0, scaledFights[0] + remainder);
        }
        else if (scaledOthers.Length > 0)
        {
            scaledOthers[0] = (int)Math.Max(0, scaledOthers[0] + remainder);
        }

        return (scaledFights, scaledOthers);
    }

    /// <summary>round(count × total / sum), half away from zero.</summary>
    private static int Round(int count, int total, long sum) => (int)(((2L * count * total) + sum) / (2 * sum));
}

/// <summary>An entity as a scenario places it at step 0.</summary>
/// <param name="Id">Compact, ascending from 0.</param>
/// <param name="Class">Its class.</param>
/// <param name="Layer">Section layer.</param>
/// <param name="X">Metres.</param>
/// <param name="Y">Metres.</param>
/// <param name="Z">Altitude in metres (aircraft only).</param>
/// <param name="FightTag">The fight tag for players in a fight, or −1.</param>
public readonly record struct EntitySpec(int Id, EntityClass Class, int Layer, double X, double Y, double Z, int FightTag)
{
    /// <summary>Infantry, ground vehicles, aircraft and travellers are players; capture points and heavy projectiles are not.</summary>
    public bool IsPlayer => IsPlayerClass(Class);

    public static bool IsPlayerClass(EntityClass cls) =>
        cls is EntityClass.Infantry or EntityClass.GroundVehicle or EntityClass.Aircraft or EntityClass.Traveller;
}
