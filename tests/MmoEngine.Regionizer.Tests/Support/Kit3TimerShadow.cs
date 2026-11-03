using Xunit;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>
/// An independent model of when each timer is due (research D6, FR-012). Per timer it counts the
/// ticks its entity's owning region starts while holding the entity. In tick-counter mode a timer is
/// due on the owner tick that brings the count to its delay; in absolute mode on the first owner tick
/// at or after set time + delay. Callers fire due timers right after a tick starts, so membership at
/// tick start decides ownership in both modes.
/// </summary>
internal sealed class Kit3TimerShadow(TimerMode mode)
{
    private readonly SortedDictionary<int, Entry> _timers = [];
    private readonly Dictionary<int, List<Entry>> _byEntity = [];

    /// <summary>Records a timer just set with <see cref="Regionizer.AddTimer"/> at time <paramref name="now"/>.</summary>
    public void Add(int timerId, int entityId, long delay, long now)
    {
        var entry = new Entry(timerId, entityId, delay, now);
        _timers.Add(timerId, entry);
        if (!_byEntity.TryGetValue(entityId, out var list))
        {
            list = [];
            _byEntity.Add(entityId, list);
        }

        list.Add(entry);
    }

    /// <summary>The entity was removed: its pending timers must never fire.</summary>
    public void RemoveEntity(int entityId)
    {
        if (_byEntity.Remove(entityId, out var list))
        {
            foreach (var entry in list)
            {
                entry.Cancelled = true;
            }
        }
    }

    /// <summary>
    /// A region started a tick holding <paramref name="entities"/>: counts one owner tick for each of
    /// their pending timers and returns the IDs due on this tick, ascending.
    /// </summary>
    public List<int> TickStarted(ReadOnlySpan<int> entities, long now)
    {
        var due = new List<int>();
        foreach (var entityId in entities)
        {
            if (!_byEntity.TryGetValue(entityId, out var list))
            {
                continue;
            }

            foreach (var entry in list)
            {
                if (!entry.Pending)
                {
                    continue;
                }

                entry.OwnerTicks++;
                if (mode == TimerMode.TickCounter ? entry.OwnerTicks >= entry.Delay : now >= entry.SetNow + entry.Delay)
                {
                    due.Add(entry.TimerId);
                }
            }
        }

        due.Sort();
        return due;
    }

    /// <summary>Asserts the timers fired by one RunDueTimers call are exactly the due ones, and marks them fired.</summary>
    public void VerifyFired(long regionId, long now, List<int> due, IReadOnlyList<Kit3TimerFiring> fired, string context)
    {
        var firedIds = fired.Select(f => f.TimerId).Order().ToList();
        Assert.True(
            due.SequenceEqual(firedIds),
            $"{context}: region {regionId} at {now} fired [{string.Join(", ", firedIds)}], the shadow expected [{string.Join(", ", due)}]");
        foreach (var firing in fired)
        {
            var entry = _timers[firing.TimerId];
            Assert.True(
                firing.EntityId == entry.EntityId && firing.RegionId == regionId && firing.Now == now,
                $"{context}: timer {firing.TimerId} fired as {firing}, expected entity {entry.EntityId} in region {regionId} at {now}");
            entry.Fired = true;
        }
    }

    /// <summary>
    /// Asserts where the regionizer holds the pending timers at a tick boundary: each listed timer is
    /// pending, sits in the region holding its entity, and keeps its deadline (tick-counter mode: the
    /// ticks it still needs; absolute mode: set time + delay). Each pending timer of an entity that has
    /// an owner is listed exactly once.
    /// </summary>
    public void VerifyPlacement(Regionizer regionizer, string context)
    {
        var listed = new Dictionary<int, long>();
        foreach (var regionId in regionizer.RegionIds.ToArray())
        {
            var region = regionizer.GetRegion(regionId);
            foreach (var timer in region.Timers)
            {
                Assert.True(listed.TryAdd(timer.TimerId, regionId), $"{context}: timer {timer.TimerId} is listed by regions {listed.GetValueOrDefault(timer.TimerId)} and {regionId}");
                Assert.True(_timers.TryGetValue(timer.TimerId, out var entry) && entry.Pending, $"{context}: region {regionId} lists timer {timer.TimerId}, which is not pending");
                var holder = regionizer.HasEntity(entry.EntityId) ? regionizer.GetEntity(entry.EntityId).RegionId : -1;
                Assert.True(holder == regionId, $"{context}: region {regionId} lists timer {timer.TimerId} of entity {entry.EntityId}, which region {holder} holds");
                if (mode == TimerMode.TickCounter)
                {
                    var remaining = timer.Deadline - region.TickCounter;
                    Assert.True(remaining == entry.Delay - entry.OwnerTicks, $"{context}: timer {timer.TimerId} has {remaining} ticks left in region {regionId}, the shadow expected {entry.Delay - entry.OwnerTicks}");
                }
                else
                {
                    Assert.True(timer.Deadline == entry.SetNow + entry.Delay, $"{context}: timer {timer.TimerId} has deadline {timer.Deadline}, expected {entry.SetNow + entry.Delay}");
                }
            }
        }

        foreach (var entry in _timers.Values)
        {
            if (!entry.Pending || !regionizer.HasEntity(entry.EntityId))
            {
                continue;
            }

            var entity = regionizer.GetEntity(entry.EntityId);
            if (!entity.InTransit && entity.RegionId != 0)
            {
                Assert.True(listed.ContainsKey(entry.TimerId), $"{context}: pending timer {entry.TimerId} of entity {entry.EntityId} is held by no region");
            }
        }
    }

    private sealed class Entry(int timerId, int entityId, long delay, long setNow)
    {
        public int TimerId { get; } = timerId;

        public int EntityId { get; } = entityId;

        public long Delay { get; } = delay;

        public long SetNow { get; } = setNow;

        public long OwnerTicks { get; set; }

        public bool Fired { get; set; }

        public bool Cancelled { get; set; }

        public bool Pending => !Fired && !Cancelled;
    }
}
