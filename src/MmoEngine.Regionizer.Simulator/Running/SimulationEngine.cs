using System.Diagnostics;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Running;

/// <summary>
/// Runs region tick bodies and step boundaries against the core (research D7). A tick body, run at the
/// region's start event, delivers the region's location-queue arrivals, fires its due timers, moves
/// its entities in ascending ID through the movement source and sets synthetic timers from the
/// "timers" stream. Every core call is wrapped in <see cref="Stopwatch.GetTimestamp"/> for the
/// bookkeeping cost, less the calibrated cost of the timestamp pair (research D9).
/// </summary>
internal sealed class SimulationEngine : ITickHost, IStepBoundaryActions
{
    private readonly RunConfig _config;
    private readonly TimeBase _time;
    private readonly IMovementSource _movement;
    private readonly TimerMonitor _timers;
    private readonly DeterministicRandom _timerRandom;
    private readonly IReadOnlyList<IRunListener> _listeners;
    private readonly LocationQueue _queue = new();
    private readonly Func<Arrival, long> _ownerOf;
    private readonly Action<Arrival> _complete;
    private readonly double _overheadTicks;
    private int[] _entities = new int[16];
    private double _bookkeeping;
    private long _bookkeepingCalls;

    /// <param name="regionizer">The core.</param>
    /// <param name="state">The simulator's mirror of entity state.</param>
    /// <param name="movement">The per-entity update source.</param>
    /// <param name="config">The run's validated configuration; its <c>scheduling</c> gives the time base.</param>
    /// <param name="timers">The timer shadow monitor.</param>
    /// <param name="listeners">Run listeners.</param>
    /// <param name="timerOverheadTicks">The calibrated cost of a timestamp pair, subtracted from each timed call.</param>
    public SimulationEngine(
        Regionizer regionizer,
        WorldState state,
        IMovementSource movement,
        RunConfig config,
        TimerMonitor timers,
        IReadOnlyList<IRunListener> listeners,
        double timerOverheadTicks = 0)
    {
        Regionizer = regionizer;
        State = state;
        _movement = movement;
        _config = config;
        _time = TimeBase.From(config.Scheduling);
        _timers = timers;
        _listeners = listeners;
        _overheadTicks = timerOverheadTicks;
        _timerRandom = DeterministicRandom.ForStream(config.Seed, "timers");
        _ownerOf = OwnerOf;
        _complete = Complete;
    }

    public Regionizer Regionizer { get; }

    public WorldState State { get; }

    public long Step { get; private set; }

    /// <summary>Returns and resets the Stopwatch ticks spent in core calls since the last call, net of the timestamp overhead.</summary>
    public double TakeBookkeeping()
    {
        var value = _bookkeeping;
        _bookkeeping = 0;
        return value;
    }

    /// <summary>Places the scenario's entities at step 0, in ascending ID.</summary>
    public void AddEntities(IReadOnlyList<Scenarios.EntitySpec> entities)
    {
        foreach (var e in entities)
        {
            var t0 = Stopwatch.GetTimestamp();
            Regionizer.AddEntity(e.Id, e.Class, e.Layer, e.X, e.Y, e.Z, e.FightTag);
            Measured(t0);
            foreach (var listener in _listeners)
            {
                listener.OnEntityAdded(0, e);
            }
        }
    }

    /// <summary>
    /// The step boundary: scenario events (portal toggles, fight moves), then teleports whose
    /// destination nobody owns. Returns true when anything was applied.
    /// </summary>
    public bool RunBoundary(long step, IReadOnlyList<IStepEventSource> events)
    {
        Step = step;
        var before = _bookkeepingCalls;
        foreach (var source in events)
        {
            source.OnStepBoundary(step, this);
        }

        var completed = _queue.CompleteUnowned(step, _ownerOf, _complete);
        return completed > 0 || _bookkeepingCalls != before;
    }

    public void SetPortalOpen(int portalId, bool open)
    {
        if (Regionizer.IsPortalOpen(portalId) == open)
        {
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        Regionizer.SetPortalOpen(portalId, open);
        Measured(t0);
        _bookkeepingCalls++;
        foreach (var listener in _listeners)
        {
            listener.OnPortalChanged(Step, portalId, open);
        }
    }

    public bool StartTick(long regionId, long step, int periodSteps)
    {
        Step = step;
        var t0 = Stopwatch.GetTimestamp();
        var started = Regionizer.TryMarkTicking(regionId);
        Measured(t0);
        if (!started)
        {
            return false;
        }

        _queue.CompleteForRegion(regionId, _ownerOf, _complete);

        t0 = Stopwatch.GetTimestamp();
        Regionizer.RunDueTimers(regionId);
        Measured(t0);

        var members = Regionizer.GetRegion(regionId).Entities;
        if (_entities.Length < members.Length)
        {
            _entities = new int[Math.Max(members.Length, _entities.Length * 2)];
        }

        var count = members.Length;
        members.CopyTo(_entities);
        for (var i = 0; i < count; i++)
        {
            var id = _entities[i];
            var update = _movement.Next(id, step);
            if (update.Op != MovementOp.None)
            {
                Apply(id, update, step);
            }
        }

        SetSyntheticTimers(regionId, count, periodSteps, step);
        return true;
    }

    public TickEndResult EndTick(long regionId)
    {
        var t0 = Stopwatch.GetTimestamp();
        var result = Regionizer.MarkNotTicking(regionId);
        Measured(t0);
        return result;
    }

    /// <summary>Adds the time since <paramref name="t0"/>, less the timestamp pair's cost, to the bookkeeping.</summary>
    private void Measured(long t0) => _bookkeeping += TimestampOverhead.Net(Stopwatch.GetTimestamp() - t0, _overheadTicks);

    private void Apply(int id, in MovementUpdate update, long step)
    {
        var t0 = Stopwatch.GetTimestamp();
        switch (update.Op)
        {
            case MovementOp.Move:
                Regionizer.MoveEntity(id, update.Layer, update.X, update.Y, update.Z);
                break;
            case MovementOp.TeleportBegin:
                Regionizer.BeginTeleport(id, update.Layer, update.X, update.Y, update.Z);
                _queue.Enqueue(new Arrival(id, step, update.Layer, update.X, update.Y));
                break;
            case MovementOp.Occupy:
                Regionizer.SetCapturePointOccupied(id, true);
                break;
            case MovementOp.Vacate:
                Regionizer.SetCapturePointOccupied(id, false);
                break;
        }

        Measured(t0);
        State.Apply(id, update);
        foreach (var listener in _listeners)
        {
            listener.OnUpdateApplied(step, id, update);
        }
    }

    /// <summary>
    /// Each entity still in the region gets a timer with probability p·(the tick's period in seconds),
    /// its delay drawn from U[min, max] ticks: owner ticks in tick-counter mode, or ticks × baseStepHz /
    /// tickHz base steps in absolute mode (data-model.md, Timer).
    /// </summary>
    private void SetSyntheticTimers(long regionId, int count, int periodSteps, long step)
    {
        var timers = _config.Timers;
        if (timers.SetProbabilityPerEntityPerSecond <= 0)
        {
            return;
        }

        var chance = timers.SetProbabilityPerEntityPerSecond * _time.ToSeconds(periodSteps);
        var absolute = _config.Regionizer.TimerMode == TimerMode.Absolute;
        for (var i = 0; i < count; i++)
        {
            var id = _entities[i];
            if (!_timerRandom.Chance(chance))
            {
                continue;
            }

            var ticks = (long)_timerRandom.NextInt(timers.DelayTicksMin, timers.DelayTicksMax);
            var delay = absolute ? _time.TicksToSteps(ticks) : ticks;
            if (State.InTransit[id] || Regionizer.GetEntity(id).RegionId != regionId)
            {
                continue;
            }

            var t0 = Stopwatch.GetTimestamp();
            var timerId = Regionizer.AddTimer(id, delay);
            Measured(t0);
            _timers.OnTimerSet(timerId, id, regionId, delay, step);
        }
    }

    private long OwnerOf(Arrival arrival)
    {
        var t0 = Stopwatch.GetTimestamp();
        var owner = Regionizer.OwnerOfLocation(arrival.Layer, arrival.X, arrival.Y);
        Measured(t0);
        return owner;
    }

    private void Complete(Arrival arrival)
    {
        var t0 = Stopwatch.GetTimestamp();
        Regionizer.CompleteTeleport(arrival.EntityId);
        Measured(t0);
        State.CompleteTeleport(arrival.EntityId);
        _movement.OnTeleportCompleted(arrival.EntityId, Step);
        foreach (var listener in _listeners)
        {
            listener.OnTeleportCompleted(Step, arrival.EntityId);
        }
    }
}
