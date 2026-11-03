namespace MmoEngine.Regionizer.Simulator.Monitors;

/// <summary>A timer that fired early, late, twice or unknown.</summary>
/// <param name="TimerId">The timer.</param>
/// <param name="EntityId">Its entity.</param>
/// <param name="RegionId">Its owner when the fault was seen.</param>
/// <param name="Step">The base step the fault was seen at.</param>
/// <param name="Expected">Owner ticks (tick-counter mode) or the deadline step (absolute mode) expected.</param>
/// <param name="Actual">What happened instead.</param>
/// <param name="Detail">A description.</param>
public sealed record TimerFault(int TimerId, int EntityId, long RegionId, long Step, long Expected, long Actual, string Detail);

/// <summary>
/// The timer shadow monitor (research D6). It keeps its own model of every synthetic timer, independent
/// of the core's deadline arithmetic: in tick-counter mode it counts the tick starts of the timer's
/// current owner (owners follow the timer's entity through merges, splits and teleports); in absolute
/// mode it watches the owner's tick times. A timer must fire exactly once: after exactly its delay in
/// owner ticks, or at the first owner tick at or after its deadline (FR-012, FR-020).
/// <para>
/// The core calls the observer methods inside its timed calls, so they only queue the notification:
/// O(1), and no allocation once the queue has grown to a step's worth (FR-027). The shadow model runs
/// at <see cref="Flush"/>, which the runner calls after each base step, outside the timed calls. Every
/// other member flushes first, so what it reads or changes is always current and in order.
/// </para>
/// </summary>
public sealed class TimerMonitor : IRegionizerObserver
{
    private readonly TimerMode _mode;
    private readonly Dictionary<int, Shadow> _timers = [];
    private readonly Dictionary<long, List<Shadow>> _byRegion = [];
    private readonly Dictionary<int, List<Shadow>> _byEntity = [];
    private readonly List<TimerFault> _faults = [];
    private readonly HashSet<int> _fired = [];
    private Notice[] _queue = new Notice[64];
    private int _queued;
    private long _now;
    private long _timersFired;

    public TimerMonitor(TimerMode mode) => _mode = mode;

    public long TimersSet { get; private set; }

    public long TimersFired
    {
        get
        {
            Flush();
            return _timersFired;
        }
    }

    /// <summary>Timers set and not yet fired.</summary>
    public int Pending
    {
        get
        {
            Flush();
            return _timers.Count;
        }
    }

    /// <summary>Every fault seen so far, in order.</summary>
    public IReadOnlyList<TimerFault> Faults
    {
        get
        {
            Flush();
            return _faults;
        }
    }

    /// <summary>Core notifications queued and not yet applied to the shadow model.</summary>
    public int Queued => _queued;

    /// <summary>Applies the queued core notifications to the shadow model, in the order they arrived.</summary>
    public void Flush()
    {
        for (var i = 0; i < _queued; i++)
        {
            ref readonly var n = ref _queue[i];
            switch (n.Kind)
            {
                case NoticeKind.OwnerChanged:
                    ApplyOwnerChanged(n.EntityId, n.RegionId, n.OtherRegionId);
                    break;
                case NoticeKind.TickStarted:
                    ApplyTickStarted(n.RegionId, n.Now);
                    break;
                case NoticeKind.Fired:
                    ApplyFired(n.TimerId, n.EntityId, n.RegionId, n.Now);
                    break;
                default:
                    ApplyRegionDied(n.RegionId);
                    break;
            }
        }

        _queued = 0;
    }

    /// <summary>Records a timer just set with <c>Regionizer.AddTimer</c> on an entity of <paramref name="regionId"/>.</summary>
    public void OnTimerSet(int timerId, int entityId, long regionId, long delay, long now)
    {
        Flush();
        TimersSet++;
        var shadow = new Shadow(timerId, entityId, delay, now + delay) { Owner = regionId };
        _timers.Add(timerId, shadow);
        ListFor(_byRegion, regionId).Add(shadow);
        ListFor(_byEntity, entityId).Add(shadow);
    }

    /// <summary>Forgets an entity's timers; the core removes them with the entity.</summary>
    public void OnEntityRemoved(int entityId)
    {
        Flush();
        if (!_byEntity.Remove(entityId, out var list))
        {
            return;
        }

        foreach (var shadow in list)
        {
            _timers.Remove(shadow.TimerId);
            if (shadow.Owner != 0)
            {
                _byRegion[shadow.Owner].Remove(shadow);
            }
        }
    }

    /// <summary>
    /// Queues an owner change. An entity with no timer is skipped: queued notifications only ever take
    /// timers away (timers are added by <see cref="OnTimerSet"/>, which flushes first), so it has none
    /// when the queue is applied either.
    /// </summary>
    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
        if (_byEntity.ContainsKey(entityId))
        {
            Enqueue(new Notice(NoticeKind.OwnerChanged, 0, entityId, oldOwner, newOwner, 0));
        }
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now) =>
        Enqueue(new Notice(NoticeKind.TickStarted, 0, 0, regionId, 0, now));

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick) =>
        Enqueue(new Notice(NoticeKind.Fired, timerId, entityId, regionId, 0, now));

    public void OnRegionDied(long regionId, long ageSteps) =>
        Enqueue(new Notice(NoticeKind.RegionDied, 0, 0, regionId, 0, 0));

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
    {
    }

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
    }

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    private void Enqueue(in Notice notice)
    {
        if (_queued == _queue.Length)
        {
            Array.Resize(ref _queue, _queue.Length * 2);
        }

        _queue[_queued++] = notice;
    }

    private void ApplyOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
        if (!_byEntity.TryGetValue(entityId, out var list))
        {
            return;
        }

        foreach (var shadow in list)
        {
            if (shadow.Owner != 0 && _byRegion.TryGetValue(shadow.Owner, out var old))
            {
                old.Remove(shadow);
            }

            shadow.Owner = newOwner;
            if (newOwner != 0)
            {
                ListFor(_byRegion, newOwner).Add(shadow);
            }
        }
    }

    private void ApplyTickStarted(long regionId, long now)
    {
        _now = now;
        if (!_byRegion.TryGetValue(regionId, out var list))
        {
            return;
        }

        foreach (var shadow in list)
        {
            if (_mode == TimerMode.TickCounter)
            {
                shadow.Elapsed++;
                if (shadow.Elapsed > shadow.Delay && !shadow.Faulted)
                {
                    shadow.Faulted = true;
                    Fault(shadow, regionId, shadow.Delay, shadow.Elapsed, $"timer {shadow.TimerId} is late: still pending at owner tick {shadow.Elapsed}, delay {shadow.Delay}");
                }
            }
            else if (now >= shadow.Deadline)
            {
                if (shadow.DueTickSeen && !shadow.Faulted)
                {
                    shadow.Faulted = true;
                    Fault(shadow, regionId, shadow.Deadline, now, $"timer {shadow.TimerId} is late: region {regionId} ticked at step {now} after deadline {shadow.Deadline} and it did not fire");
                }

                shadow.DueTickSeen = true;
            }
        }
    }

    private void ApplyFired(int timerId, int entityId, long regionId, long now)
    {
        _now = now;
        if (!_timers.Remove(timerId, out var shadow))
        {
            var detail = _fired.Contains(timerId) ? $"timer {timerId} fired a second time" : $"timer {timerId} fired but was never set";
            _faults.Add(new TimerFault(timerId, entityId, regionId, now, 1, 2, detail));
            return;
        }

        _timersFired++;
        _fired.Add(timerId);
        if (_byRegion.TryGetValue(shadow.Owner, out var list))
        {
            list.Remove(shadow);
        }

        if (_byEntity.TryGetValue(shadow.EntityId, out var mine))
        {
            mine.Remove(shadow);
            if (mine.Count == 0)
            {
                _byEntity.Remove(shadow.EntityId);
            }
        }

        if (shadow.Faulted)
        {
            return;
        }

        if (_mode == TimerMode.TickCounter && shadow.Elapsed != shadow.Delay)
        {
            Fault(shadow, regionId, shadow.Delay, shadow.Elapsed, $"timer {timerId} fired after {shadow.Elapsed} owner ticks, expected exactly {shadow.Delay}");
        }
        else if (_mode == TimerMode.Absolute && now < shadow.Deadline)
        {
            Fault(shadow, regionId, shadow.Deadline, now, $"timer {timerId} fired at step {now}, before its deadline {shadow.Deadline}");
        }
    }

    private void ApplyRegionDied(long regionId)
    {
        if (_byRegion.Remove(regionId, out var list) && list.Count > 0)
        {
            foreach (var shadow in list)
            {
                Fault(shadow, regionId, 0, 0, $"timer {shadow.TimerId} was still owned by region {regionId} when it died");
            }
        }
    }

    private void Fault(Shadow shadow, long regionId, long expected, long actual, string detail) =>
        _faults.Add(new TimerFault(shadow.TimerId, shadow.EntityId, regionId, _now, expected, actual, detail));

    private static List<Shadow> ListFor<TKey>(Dictionary<TKey, List<Shadow>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map.Add(key, list);
        }

        return list;
    }

    private enum NoticeKind : byte
    {
        OwnerChanged,
        TickStarted,
        Fired,
        RegionDied,
    }

    /// <summary>
    /// One queued core notification. Owner changes use <see cref="RegionId"/> as the old owner and
    /// <see cref="OtherRegionId"/> as the new one; the others use only the fields their kind names.
    /// </summary>
    private readonly record struct Notice(NoticeKind Kind, int TimerId, int EntityId, long RegionId, long OtherRegionId, long Now);

    private sealed class Shadow(int timerId, int entityId, long delay, long deadline)
    {
        public int TimerId { get; } = timerId;

        public int EntityId { get; } = entityId;

        public long Delay { get; } = delay;

        /// <summary>Absolute mode: the step at or after which it must fire.</summary>
        public long Deadline { get; } = deadline;

        /// <summary>The region holding it, or 0 while its entity is in transit.</summary>
        public long Owner { get; set; }

        /// <summary>Tick-counter mode: tick starts of its owners since it was set.</summary>
        public long Elapsed { get; set; }

        /// <summary>Absolute mode: an owner tick at or after the deadline has started.</summary>
        public bool DueTickSeen { get; set; }

        public bool Faulted { get; set; }
    }
}
