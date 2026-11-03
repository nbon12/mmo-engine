using MmoEngine.Regionizer.Simulator.Movement;

namespace MmoEngine.Regionizer.Simulator.Scheduling;

/// <summary>What a step-boundary event may do. No region is ticking while it runs (research D7).</summary>
public interface IStepBoundaryActions
{
    /// <summary>The base step about to run.</summary>
    long Step { get; }

    /// <summary>The regionizer, for reads. Changes go through the methods below so they are timed and recorded.</summary>
    Regionizer Regionizer { get; }

    /// <summary>The simulator's mirror of entity state.</summary>
    WorldState State { get; }

    /// <summary>Opens or closes a portal; claims follow at once, before any region ticks this step.</summary>
    void SetPortalOpen(int portalId, bool open);
}

/// <summary>An event source applied at every step boundary, before the step's region ticks.</summary>
public interface IStepEventSource
{
    void OnStepBoundary(long step, IStepBoundaryActions actions);
}

/// <summary>
/// Opens a portal at the start of each cycle and closes it <paramref name="openSteps"/> later
/// (a map's <c>portalSchedule</c>: every 60 s, open 20 s).
/// </summary>
public sealed class PortalCycleEvents : IStepEventSource
{
    private readonly long _everySteps;
    private readonly long _openSteps;

    public PortalCycleEvents(int portalId, long everySteps, long openSteps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(everySteps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(openSteps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(openSteps, everySteps);
        PortalId = portalId;
        _everySteps = everySteps;
        _openSteps = openSteps;
    }

    public int PortalId { get; }

    public void OnStepBoundary(long step, IStepBoundaryActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var phase = step % _everySteps;
        if (phase == 0)
        {
            actions.SetPortalOpen(PortalId, true);
        }
        else if (phase == _openSteps)
        {
            actions.SetPortalOpen(PortalId, false);
        }
    }
}
