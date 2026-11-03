namespace MmoEngine.Regionizer.Simulator.Scheduling;

/// <summary>A teleport waiting for its second step.</summary>
/// <param name="EntityId">The entity in transit.</param>
/// <param name="BeganAt">The base step its teleport began.</param>
/// <param name="Layer">Destination layer.</param>
/// <param name="X">Destination, metres.</param>
/// <param name="Y">Destination, metres.</param>
public readonly record struct Arrival(int EntityId, long BeganAt, int Layer, double X, double Y);

/// <summary>
/// The location queue of the two-step teleport (Principle II, research D7). A teleport that began at
/// step <c>s</c> completes no earlier than step <c>s + 1</c>: in the tick of the region that owns its
/// destination then, or at a step boundary when nobody owns it (a new region is created). Arrivals
/// are kept in the order their teleports began.
/// </summary>
public sealed class LocationQueue
{
    private readonly List<Arrival> _waiting = [];
    private readonly Dictionary<long, List<Arrival>> _byOwner = [];
    private readonly List<Arrival> _scratch = [];

    /// <summary>Arrivals not yet completed.</summary>
    public int Count => _waiting.Count;

    public void Enqueue(in Arrival arrival) => _waiting.Add(arrival);

    /// <summary>
    /// At a step boundary: completes, in queue order, every arrival begun before <paramref name="step"/>
    /// whose destination nobody owns, and files the rest under their destination's current owner.
    /// </summary>
    /// <param name="step">The step about to run.</param>
    /// <param name="ownerOf">The region owning a destination, or 0.</param>
    /// <param name="complete">Completes one arrival.</param>
    /// <returns>The number of arrivals completed.</returns>
    public int CompleteUnowned(long step, Func<Arrival, long> ownerOf, Action<Arrival> complete)
    {
        ArgumentNullException.ThrowIfNull(ownerOf);
        ArgumentNullException.ThrowIfNull(complete);
        _byOwner.Clear();
        var completed = 0;
        _scratch.Clear();
        foreach (var arrival in _waiting)
        {
            if (arrival.BeganAt >= step)
            {
                _scratch.Add(arrival);
                continue;
            }

            var owner = ownerOf(arrival);
            if (owner == 0)
            {
                complete(arrival);
                completed++;
                continue;
            }

            _scratch.Add(arrival);
            if (!_byOwner.TryGetValue(owner, out var list))
            {
                list = [];
                _byOwner.Add(owner, list);
            }

            list.Add(arrival);
        }

        _waiting.Clear();
        _waiting.AddRange(_scratch);
        return completed;
    }

    /// <summary>
    /// In <paramref name="regionId"/>'s tick: completes, in queue order, the arrivals filed under it
    /// at the last boundary whose destination it still owns. Others wait for the next boundary.
    /// </summary>
    public int CompleteForRegion(long regionId, Func<Arrival, long> ownerOf, Action<Arrival> complete)
    {
        ArgumentNullException.ThrowIfNull(ownerOf);
        ArgumentNullException.ThrowIfNull(complete);
        if (!_byOwner.TryGetValue(regionId, out var list) || list.Count == 0)
        {
            return 0;
        }

        var completed = 0;
        foreach (var arrival in list)
        {
            if (ownerOf(arrival) != regionId)
            {
                continue;
            }

            complete(arrival);
            completed++;
            var at = _waiting.FindIndex(a => a.EntityId == arrival.EntityId);
            _waiting.RemoveAt(at);
        }

        list.Clear();
        return completed;
    }
}
