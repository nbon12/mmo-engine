using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Running;

/// <summary>
/// Runs one scenario (US2): builds the world, drives the core step by step on the run's own time base
/// (<c>scheduling.baseStepHz</c>) with the configured scheduler, checks the invariants after every
/// step in which anything changed and watches every synthetic timer, hashes the state after every
/// continent tick, and writes the run folder. Single-threaded and deterministic: the same
/// configuration and seed give the same hash sequence. Runs share no mutable state, so a sweep can
/// run many on parallel threads. The run stops at the first violation or timer fault (FR-020).
/// <para>
/// Wall-clock reads go through <see cref="WallClock"/> and <see cref="BookkeepingMeter"/> in <c>Output/</c>,
/// off the simulated path. The core calls the observers inside the timed core calls, where they only
/// queue work; after each base step the runner flushes it (the timer monitor's shadow model and the
/// events written to <c>events.csv</c>), so neither is counted as bookkeeping (FR-027).
/// </para>
/// </summary>
public static class SimulationRunner
{
    /// <summary>Runs <paramref name="config"/> into <paramref name="outputDir"/>.</summary>
    /// <exception cref="ConfigValidationException">The configuration is invalid, or the folder is not a run folder (exit code 2).</exception>
    public static RunResult Run(RunConfig config, string outputDir, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new RunOptions();
        return Run(RunConfigLoader.FromConfig(config, options.EnvironmentName), outputDir, options);
    }

    /// <summary>Runs a configuration already loaded and validated by <see cref="RunConfigLoader"/>.</summary>
    /// <exception cref="ConfigValidationException">The output folder is not empty and not a run folder (exit code 2); nothing is deleted.</exception>
    public static RunResult Run(LoadedRunConfig loaded, string outputDir, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        options ??= new RunOptions();
        if (options.TimerOverheadNanos is { } injected && !(injected >= 0 && double.IsFinite(injected)))
        {
            throw new ArgumentOutOfRangeException(nameof(options), injected, "The timer overhead must be a finite number of nanoseconds, at least 0.");
        }

        RunFolder.EnsureUsable(outputDir);
        var config = loaded.Config;
        var time = TimeBase.From(config.Scheduling);
        var log = options.Logger ?? Serilog.Core.Logger.None;
        var wall = WallClock.StartTimer();
        var started = WallClock.UtcNow();
        var overheadTicks = options.TimerOverheadNanos is { } nanos ? TimestampOverhead.NanosToTicks(nanos) : TimestampOverhead.CalibrateTicks();
        var overheadNanos = TimestampOverhead.TicksToNanos(overheadTicks);

        var world = ScenarioRegistry.Create(config.Scenario).Build(config);
        var movement = options.MovementSourceFactory?.Invoke(world, config) ?? world.Movement;
        var runId = RunManifest.RunId(loaded);
        var gitSha = options.GitSha ?? RunManifest.GitSha();

        using var folder = RunFolder.Create(outputDir);
        folder.WriteManifest(RunManifest.Build(loaded, runId, gitSha, started, null, 0));
        log.Information(
            "Run {RunId} started: scenario {Scenario}, seed {Seed}, {Scheduling} at {TimeBase}, {Ticks} ticks, {Entities} entities, config {ConfigHash}, timer overhead {TimerOverheadNanos:F1} ns",
            runId,
            config.Scenario,
            config.Seed,
            config.Scheduling.Mode,
            time.ToString(),
            config.DurationTicks,
            world.Entities.Count,
            loaded.ConfigHash,
            overheadNanos);

        var listeners = options.Listeners;
        void Emit(RunEvent e)
        {
            folder.WriteEvent(e);
            foreach (var listener in listeners)
            {
                listener.OnEvent(e);
            }
        }

        var scheduler = TickSchedulers.Create(config.Scheduling, config.Seed);
        var timerMonitor = new TimerMonitor(config.Regionizer.TimerMode);
        var invariants = new InvariantMonitor();
        var metrics = new MetricsCollector(world.State, config.Metrics, Emit);
        var observers = new List<IRegionizerObserver> { metrics, timerMonitor };
        if (scheduler.Observer is { } schedulerObserver)
        {
            observers.Add(schedulerObserver);
        }

        observers.AddRange(options.Observers);
        var regionizer = new Regionizer(world.Topology, config.ToRegionizerOptions(), new CompositeObserver(observers));
        var meter = options.Meter ?? new BookkeepingMeter();
        meter.OverheadTicks = overheadTicks;
        var engine = new SimulationEngine(regionizer, world.State, movement, config, timerMonitor, listeners, meter);
        var hasher = new StateHasher();
        var hashes = options.KeepTickHashes ? new List<ulong>((int)Math.Min(config.DurationTicks, 1 << 24)) : null;

        metrics.SetClock(0, 0);
        scheduler.BeginStep(0);
        engine.AddEntities(world.Entities);

        var totalSteps = time.TicksToSteps(config.DurationTicks);
        var progressEvery = Math.Max(1, config.DurationTicks / 10);
        var lastSerialized = regionizer.Stats.SerializedTimestampTicks;
        var faultsSeen = 0;
        long ticksRun = 0;
        RunFailure? failure = null;
        for (var step = 0L; step < totalSteps; step++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var tick = time.TickAt(step);
            metrics.SetClock(tick, step);
            regionizer.AdvanceTo(step);
            scheduler.BeginStep(step);
            var changed = engine.RunBoundary(step, world.StepEvents) || step == 0;
            var ticksBefore = scheduler.Stats.Ticks;
            scheduler.RunStep(step, regionizer.RegionIds, engine);
            changed |= scheduler.Stats.Ticks != ticksBefore;

            if (options.AfterStep is { } hook)
            {
                hook(new StepHookContext(regionizer, world.State, step, tick, config.Seed));
                changed = true;
            }

            // The observers queued their work inside the timed core calls; it runs now, outside them.
            timerMonitor.Flush();
            metrics.FlushEvents();

            if (changed && invariants.Check(regionizer) is { } violation)
            {
                failure = new RunFailure(RunFailureKind.Violation, violation.Rule, tick, step, config.Seed, violation.Detail, invariants.LastViolations.Count);
                foreach (var v in invariants.LastViolations)
                {
                    metrics.Emit(new RunEvent(tick, step, RunEventType.Violation, 0, Detail: $"rule {(int)v.Rule} {v.Rule}: {v.Detail}"));
                }
            }

            if (timerMonitor.Faults.Count > faultsSeen)
            {
                var faults = timerMonitor.Faults;
                for (var i = faultsSeen; i < faults.Count; i++)
                {
                    var f = faults[i];
                    metrics.Emit(new RunEvent(tick, step, RunEventType.TimerFault, f.RegionId, Detail: $"timer {f.TimerId} expected {f.Expected} actual {f.Actual}: {f.Detail}"));
                }

                failure ??= new RunFailure(RunFailureKind.TimerFault, null, tick, step, config.Seed, faults[faultsSeen].Detail, faults.Count - faultsSeen);
                faultsSeen = faults.Count;
            }

            if (time.IsLastStepOfTick(step) || failure is not null)
            {
                var hash = hasher.Next(regionizer);
                hashes?.Add(hash);
                var serialized = regionizer.Stats.SerializedTimestampTicks;
                var record = tick % config.Metrics.MetricsEveryTicks == 0 || failure is not null;
                var row = metrics.EndTick(
                    regionizer,
                    tick,
                    step,
                    MetricsCollector.TimestampToMicros(engine.TakeBookkeeping()),
                    MetricsCollector.TimestampToMicros(serialized - lastSerialized),
                    failure?.Count ?? 0,
                    hash,
                    record);
                lastSerialized = serialized;
                if (record)
                {
                    folder.WriteTick(row);
                    foreach (var listener in listeners)
                    {
                        listener.OnTick(row);
                    }
                }

                ticksRun = tick + 1;
                if (failure is null && (tick + 1) % progressEvery == 0)
                {
                    log.Debug("Run {RunId}: tick {Tick} of {Ticks}, {Regions} regions", runId, tick + 1, config.DurationTicks, regionizer.RegionIds.Length);
                }
            }

            if (failure is not null)
            {
                log.Error(
                    "Run {RunId} stopped: rule {Rule} at tick {Tick}, seed {Seed}: {Detail}",
                    runId,
                    failure.RuleNumber,
                    failure.Tick,
                    failure.Seed,
                    failure.Detail);
                break;
            }
        }

        var completed = failure is null && ticksRun == config.DurationTicks;
        folder.WriteSummary(RunSummary.Build(runId, completed, ticksRun, failure, metrics, timerMonitor, config.Metrics, time, overheadNanos, wall.Elapsed.TotalSeconds));
        folder.WriteManifest(RunManifest.Build(loaded, runId, gitSha, started, WallClock.UtcNow(), ticksRun));
        folder.Complete();

        log.Information(
            "Run {RunId} finished: completed {Completed}, {TicksRun} ticks, hash {FinalHash}, {Merges} merges, {Absorptions} absorptions, {Splits} splits, {Fusions} fusions, {TwentyHzTicks} degraded ticks, {MergesAgainstTicking} merges requested against ticking regions, {Seconds:F1} s",
            runId,
            completed,
            ticksRun,
            RunFolder.FormatHash(hasher.Current),
            metrics.Merges,
            metrics.Absorptions,
            metrics.Splits,
            metrics.Fusions,
            scheduler.Stats.TwentyHzTicks,
            metrics.MergesRequestedAgainstTicking,
            wall.Elapsed.TotalSeconds);

        return new RunResult
        {
            Completed = completed,
            FirstViolation = failure,
            FinalHash = hasher.Current,
            SummaryPath = folder.SummaryPath,
            OutputDirectory = folder.Path,
            ExitCode = failure is null ? ExitCodes.Clean : ExitCodes.Violation,
            TicksRun = ticksRun,
            RunId = runId,
            ConfigHash = loaded.ConfigHash,
            Scheduling = scheduler.Stats,
            MergesRequestedAgainstTicking = metrics.MergesRequestedAgainstTicking,
            Merges = metrics.Merges,
            Absorptions = metrics.Absorptions,
            Splits = metrics.Splits,
            Fusions = metrics.Fusions,
            TimersSet = timerMonitor.TimersSet,
            TimersFired = timerMonitor.TimersFired,
            InvariantChecks = invariants.Checks,
            TimerFaults = [.. timerMonitor.Faults],
            TickHashes = hashes is null ? [] : hashes,
            Time = time,
            TimerOverheadNanos = overheadNanos,
        };
    }
}
