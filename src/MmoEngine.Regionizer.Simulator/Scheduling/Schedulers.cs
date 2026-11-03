using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;

namespace MmoEngine.Regionizer.Simulator.Scheduling;

/// <summary>What a scheduler drives: a region's tick start (with its body) and its tick end.</summary>
public interface ITickHost
{
    /// <summary>
    /// Starts <paramref name="regionId"/>'s tick and runs its body (arrivals, due timers, movement,
    /// synthetic timers). False when the region cannot start: it died, or it is transient because it
    /// owes a merge to a ticking region.
    /// </summary>
    bool StartTick(long regionId, long step, int periodSteps);

    /// <summary>Ends a tick that <see cref="StartTick"/> started.</summary>
    TickEndResult EndTick(long regionId);
}

/// <summary>Counts of what a scheduler did.</summary>
public sealed class SchedulingStats
{
    /// <summary>Region ticks started.</summary>
    public long Ticks { get; internal set; }

    /// <summary>Region ticks run at the degraded rate (20 Hz by default: 3 base steps to the region's next tick).</summary>
    public long TwentyHzTicks { get; internal set; }

    /// <summary>Times a region dropped to the degraded rate.</summary>
    public long Drops { get; internal set; }
}

/// <summary>Decides which region ticks start and end at each base step, and in what order (research D7).</summary>
public interface ITickScheduler
{
    SchedulingMode Mode { get; }

    SchedulingStats Stats { get; }

    /// <summary>The run's time base: the tick periods region ticks are spaced by.</summary>
    TimeBase Time { get; }

    /// <summary>Region bookkeeping the scheduler needs from the core, or null.</summary>
    IRegionizerObserver? Observer { get; }

    /// <summary>Called before a step's boundary events, so regions created there are scheduled for this step.</summary>
    void BeginStep(long step);

    /// <summary>Runs the step's region ticks. <paramref name="regionIds"/> is the core's live region list.</summary>
    void RunStep(long step, ReadOnlySpan<long> regionIds, ITickHost host);
}

/// <summary>Creates the scheduler a configuration names.</summary>
public static class TickSchedulers
{
    public static ITickScheduler Create(SchedulingConfig config, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Mode switch
        {
            SchedulingMode.Lockstep => new LockstepScheduler(TimeBase.From(config)),
            SchedulingMode.Interleaved => new InterleavedScheduler(config, DeterministicRandom.ForStream(seed, "scheduling")),
            _ => throw new ArgumentOutOfRangeException(nameof(config), $"Unknown scheduling mode {config.Mode}."),
        };
    }
}

/// <summary>
/// Lockstep: on the first base step of every continent tick, each region alive at the step's start
/// ticks in ascending ID order, start to end, before the next one starts. No region ticks while
/// another changes, so merges never land on a ticking region and every region keeps the tick rate.
/// </summary>
public sealed class LockstepScheduler : ITickScheduler
{
    private long[] _due = new long[8];

    /// <summary>A lockstep scheduler at the default rates.</summary>
    public LockstepScheduler()
        : this(TimeBase.Default)
    {
    }

    public LockstepScheduler(TimeBase time)
    {
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
    }

    public SchedulingMode Mode => SchedulingMode.Lockstep;

    public SchedulingStats Stats { get; } = new();

    public TimeBase Time { get; }

    public IRegionizerObserver? Observer => null;

    public void BeginStep(long step)
    {
    }

    public void RunStep(long step, ReadOnlySpan<long> regionIds, ITickHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (step % Time.TickPeriodSteps != 0)
        {
            return;
        }

        var count = Snapshot(regionIds, ref _due);
        for (var i = 0; i < count; i++)
        {
            if (host.StartTick(_due[i], step, Time.TickPeriodSteps))
            {
                Stats.Ticks++;
                host.EndTick(_due[i]);
            }
        }
    }

    internal static int Snapshot(ReadOnlySpan<long> regionIds, ref long[] into)
    {
        if (into.Length < regionIds.Length)
        {
            into = new long[Math.Max(regionIds.Length, into.Length * 2)];
        }

        regionIds.CopyTo(into);
        return regionIds.Length;
    }
}

/// <summary>
/// Interleaved: at each base step the due regions' start and end events are shuffled into a random
/// valid order by the "scheduling" stream, and each body runs at its start event, so regions move
/// while others are ticking. At each tick end a region not already slowed drops to the degraded rate
/// with the configured probability for U[dropTicksMin, dropTicksMax] ticks (research D7). Schedules
/// follow the core's regions: split children copy their parent's, an absorber keeps its own. The
/// observer methods run inside the core's timed calls, so they only update the schedule table: O(1),
/// allocating only when a region is created and the table grows (FR-027).
/// </summary>
public sealed class InterleavedScheduler : ITickScheduler, IRegionizerObserver
{
    private readonly SchedulingConfig _config;
    private readonly DeterministicRandom _random;
    private readonly Dictionary<long, Schedule> _schedules = [];
    private readonly Dictionary<long, bool> _started = [];
    private readonly List<long> _due = [];
    private long[] _tokens = new long[16];
    private long _step;
    private bool _inTicks;

    public InterleavedScheduler(SchedulingConfig config, DeterministicRandom random)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(random);
        _config = config;
        _random = random;
        Time = TimeBase.From(config);
    }

    public SchedulingMode Mode => SchedulingMode.Interleaved;

    public SchedulingStats Stats { get; } = new();

    public TimeBase Time { get; }

    public IRegionizerObserver Observer => this;

    /// <summary>The base step at which a live region is next due, for tests and diagnostics.</summary>
    public long NextDueOf(long regionId) => _schedules.TryGetValue(regionId, out var s) ? s.NextDue : -1;

    public void BeginStep(long step) => _step = step;

    public void RunStep(long step, ReadOnlySpan<long> regionIds, ITickHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _step = step;
        _due.Clear();
        foreach (var id in regionIds)
        {
            if (!_schedules.TryGetValue(id, out var schedule) || schedule.NextDue <= step)
            {
                _due.Add(id);
            }
        }

        if (_due.Count == 0)
        {
            return;
        }

        var n = _due.Count * 2;
        if (_tokens.Length < n)
        {
            _tokens = new long[Math.Max(n, _tokens.Length * 2)];
        }

        for (var i = 0; i < _due.Count; i++)
        {
            _tokens[2 * i] = _due[i];
            _tokens[(2 * i) + 1] = _due[i];
        }

        var tokens = _tokens.AsSpan(0, n);
        _random.Shuffle(tokens);

        _inTicks = true;
        _started.Clear();
        foreach (var id in tokens)
        {
            if (!_started.TryGetValue(id, out var started))
            {
                _started[id] = Start(id, step, host);
            }
            else if (started)
            {
                End(id, host);
            }
        }

        _inTicks = false;
    }

    private bool Start(long id, long step, ITickHost host)
    {
        var schedule = _schedules.TryGetValue(id, out var s) ? s : new Schedule(step, 0, Time.TickPeriodSteps);
        var slow = schedule.SlowLeft > 0;
        var period = slow ? Time.DegradedPeriodSteps : Time.TickPeriodSteps;
        if (!host.StartTick(id, step, period))
        {
            return false;
        }

        Stats.Ticks++;
        if (slow)
        {
            Stats.TwentyHzTicks++;
        }

        _schedules[id] = new Schedule(step + period, slow ? schedule.SlowLeft - 1 : 0, period);
        return true;
    }

    private void End(long id, ITickHost host)
    {
        var result = host.EndTick(id);
        if (result.State == RegionState.Dead || !_schedules.TryGetValue(id, out var schedule) || schedule.SlowLeft > 0)
        {
            return;
        }

        if (_config.DropTo20HzProbability > 0 && _random.Chance(_config.DropTo20HzProbability))
        {
            Stats.Drops++;
            _schedules[id] = schedule with { SlowLeft = _random.NextInt(_config.DropTicksMin, _config.DropTicksMax) };
        }
    }

    void IRegionizerObserver.OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
    {
        if (reason == RegionCreationReason.Split && _schedules.TryGetValue(parentId, out var parent))
        {
            _schedules[regionId] = parent;
            return;
        }

        _schedules[regionId] = new Schedule(_inTicks ? _step + 1 : _step, 0, Time.TickPeriodSteps);
    }

    void IRegionizerObserver.OnRegionDied(long regionId, long ageSteps) => _schedules.Remove(regionId);

    void IRegionizerObserver.OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    void IRegionizerObserver.OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    void IRegionizerObserver.OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
    }

    void IRegionizerObserver.OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
    }

    void IRegionizerObserver.OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    void IRegionizerObserver.OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
    }

    void IRegionizerObserver.OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
    }

    void IRegionizerObserver.OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
    }

    /// <summary>A region's next due step, its remaining degraded ticks and its current period.</summary>
    private readonly record struct Schedule(long NextDue, int SlowLeft, int Period);
}
