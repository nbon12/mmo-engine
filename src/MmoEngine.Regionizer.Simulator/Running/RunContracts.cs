using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Running;

/// <summary>Process exit codes shared by the simulator's commands (contracts/config-and-cli.md).</summary>
public static class ExitCodes
{
    public const int Clean = 0;
    public const int Error = 1;
    public const int InvalidConfig = 2;
    public const int Violation = 3;
    public const int OutsideTolerance = 4;
}

/// <summary>What a step hook sees: the state right after a base step's ticks, before the monitors check it.</summary>
public readonly record struct StepHookContext(Regionizer Regionizer, WorldState State, long Step, long Tick, ulong Seed);

/// <summary>Optional inputs to <see cref="SimulationRunner.Run"/>: test hooks, extra observers, listeners and replacements.</summary>
public sealed record RunOptions
{
    /// <summary>
    /// Called after each base step's ticks and before the monitors run. Tests use it to corrupt the
    /// regionizer through its internal test hooks at a chosen tick (US3.2).
    /// </summary>
    public Action<StepHookContext>? AfterStep { get; init; }

    /// <summary>More core observers, called after the simulator's own (metrics, timer monitor, scheduler).</summary>
    public IReadOnlyList<IRegionizerObserver> Observers { get; init; } = [];

    /// <summary>Listeners for everything the run applies and records (trace recording, tests).</summary>
    public IReadOnlyList<IRunListener> Listeners { get; init; } = [];

    /// <summary>Replaces the scenario's movement source, for example with a trace replay (FR-022).</summary>
    public Func<ScenarioWorld, RunConfig, IMovementSource>? MovementSourceFactory { get; init; }

    /// <summary>Keep every tick's state hash in <see cref="RunResult.TickHashes"/>.</summary>
    public bool KeepTickHashes { get; init; }

    /// <summary>Structured logs; none when null.</summary>
    public Serilog.ILogger? Logger { get; init; }

    /// <summary>The raw <c>DOTNET_ENVIRONMENT</c> value to validate; null means unset (Development).</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>The source revision for the manifest; when null, <c>git rev-parse HEAD</c> is run.</summary>
    public string? GitSha { get; init; }

    /// <summary>Stops the run between steps; the folder keeps its <c>_incomplete</c> marker.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>The outcome of one run.</summary>
public sealed record RunResult
{
    /// <summary>Every configured tick ran with no violation or timer fault.</summary>
    public required bool Completed { get; init; }

    /// <summary>The first violation or timer fault, with rule, tick and seed; null when clean.</summary>
    public required RunFailure? FirstViolation { get; init; }

    /// <summary>The chained state hash after the last tick run.</summary>
    public required ulong FinalHash { get; init; }

    public required string SummaryPath { get; init; }

    public required string OutputDirectory { get; init; }

    /// <summary><see cref="ExitCodes.Clean"/>, or <see cref="ExitCodes.Violation"/> on a violation or timer fault.</summary>
    public required int ExitCode { get; init; }

    public required long TicksRun { get; init; }

    public required string RunId { get; init; }

    public required string ConfigHash { get; init; }

    public required SchedulingStats Scheduling { get; init; }

    /// <summary>Merge requests whose other region was ticking (merge-later against a ticking region).</summary>
    public required long MergesRequestedAgainstTicking { get; init; }

    public required long Merges { get; init; }

    public required long Splits { get; init; }

    public required long Fusions { get; init; }

    public required long TimersSet { get; init; }

    public required long TimersFired { get; init; }

    /// <summary>Invariant checks run (one per base step in which anything changed).</summary>
    public required long InvariantChecks { get; init; }

    public required IReadOnlyList<TimerFault> TimerFaults { get; init; }

    /// <summary>Every tick's state hash, when <see cref="RunOptions.KeepTickHashes"/> is set.</summary>
    public IReadOnlyList<ulong> TickHashes { get; init; } = [];
}

/// <summary>
/// Hears everything a run applies and records, in order. A trace recorder implements it (op names
/// follow contracts/trace-format.md); tests use it to watch movement.
/// </summary>
public interface IRunListener
{
    /// <summary>An entity placed at setup (trace op <c>add</c>).</summary>
    void OnEntityAdded(long step, in EntitySpec entity);

    /// <summary>A movement update applied in the entity's region tick (ops <c>move</c>, <c>teleportBegin</c>, <c>occupied</c>, <c>vacated</c>).</summary>
    void OnUpdateApplied(long step, int entityId, in MovementUpdate update);

    /// <summary>The second step of a teleport.</summary>
    void OnTeleportCompleted(long step, int entityId);

    /// <summary>A portal toggled at a step boundary (ops <c>portalOpen</c>, <c>portalClose</c>).</summary>
    void OnPortalChanged(long step, int portalId, bool open);

    /// <summary>A recorded <c>ticks.csv</c> row.</summary>
    void OnTick(in TickRow row);

    /// <summary>An <c>events.csv</c> row.</summary>
    void OnEvent(in RunEvent runEvent);
}

/// <summary>An <see cref="IRunListener"/> whose methods do nothing; override what you need.</summary>
public abstract class RunListener : IRunListener
{
    public virtual void OnEntityAdded(long step, in EntitySpec entity)
    {
    }

    public virtual void OnUpdateApplied(long step, int entityId, in MovementUpdate update)
    {
    }

    public virtual void OnTeleportCompleted(long step, int entityId)
    {
    }

    public virtual void OnPortalChanged(long step, int portalId, bool open)
    {
    }

    public virtual void OnTick(in TickRow row)
    {
    }

    public virtual void OnEvent(in RunEvent runEvent)
    {
    }
}

/// <summary>Fans every observer call out to several observers, in list order.</summary>
public sealed class CompositeObserver(IReadOnlyList<IRegionizerObserver> observers) : IRegionizerObserver
{
    private readonly IRegionizerObserver[] _observers = [.. observers];

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
    {
        foreach (var o in _observers)
        {
            o.OnRegionCreated(regionId, reason, parentId);
        }
    }

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
        foreach (var o in _observers)
        {
            o.OnStateChanged(regionId, from, to);
        }
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
        foreach (var o in _observers)
        {
            o.OnMergeRequested(a, b, causeClass, causeEntityId);
        }
    }

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
        foreach (var o in _observers)
        {
            o.OnMerged(absorberId, absorbedId, causeClass, causeEntityId);
        }
    }

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
        foreach (var o in _observers)
        {
            o.OnSplit(parentId, childIds);
        }
    }

    public void OnRegionDied(long regionId, long ageSteps)
    {
        foreach (var o in _observers)
        {
            o.OnRegionDied(regionId, ageSteps);
        }
    }

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
        foreach (var o in _observers)
        {
            o.OnSectionOwnerChanged(sectionIndex, oldOwner, newOwner);
        }
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
        foreach (var o in _observers)
        {
            o.OnEntityOwnerChanged(entityId, oldOwner, newOwner);
        }
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
        foreach (var o in _observers)
        {
            o.OnRegionTickStarted(regionId, tickCounter, now);
        }
    }

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
        foreach (var o in _observers)
        {
            o.OnTimerFired(timerId, entityId, regionId, now, regionTick);
        }
    }
}
