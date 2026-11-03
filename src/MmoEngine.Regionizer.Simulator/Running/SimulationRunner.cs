using System.Diagnostics;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Running;

/// <summary>
/// Runs one scenario (US2): builds the world, drives the core step by step on the 60 Hz time base with
/// the configured scheduler, checks the invariants after every step in which anything changed and
/// watches every synthetic timer, hashes the state after every continent tick, and writes the run
/// folder. Single-threaded and deterministic: the same configuration and seed give the same hash
/// sequence. The run stops at the first violation or timer fault (FR-020).
/// </summary>
public static class SimulationRunner
{
    /// <summary>Runs <paramref name="config"/> into <paramref name="outputDir"/>.</summary>
    /// <exception cref="ConfigValidationException">The configuration is invalid (exit code 2).</exception>
    public static RunResult Run(RunConfig config, string outputDir, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        options ??= new RunOptions();
        return Run(RunConfigLoader.FromConfig(config, options.EnvironmentName), outputDir, options);
    }

    /// <summary>Runs a configuration already loaded and validated by <see cref="RunConfigLoader"/>.</summary>
    public static RunResult Run(LoadedRunConfig loaded, string outputDir, RunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        options ??= new RunOptions();
        var config = loaded.Config;
        var log = options.Logger ?? Serilog.Core.Logger.None;
        var wall = Stopwatch.StartNew();
        var started = DateTime.UtcNow;

        var world = ScenarioRegistry.Create(config.Scenario).Build(config);
        var movement = options.MovementSourceFactory?.Invoke(world, config) ?? world.Movement;
        var runId = RunManifest.RunId(loaded);
        var gitSha = options.GitSha ?? RunManifest.GitSha();

        using var folder = RunFolder.Create(outputDir);
        folder.WriteManifest(RunManifest.Build(loaded, runId, gitSha, started, null, 0));
        log.Information(
            "Run {RunId} started: scenario {Scenario}, seed {Seed}, {Scheduling}, {Ticks} ticks, {Entities} entities, config {ConfigHash}",
            runId,
            config.Scenario,
            config.Seed,
            config.Scheduling.Mode,
            config.DurationTicks,
            world.Entities.Count,
            loaded.ConfigHash);

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
        var regionizer = new Regionizer(world.Topology, config.Regionizer.ToRegionizerOptions(), new CompositeObserver(observers));
        var engine = new SimulationEngine(regionizer, world.State, movement, config, timerMonitor, listeners);
        var hasher = new StateHasher();
        var hashes = options.KeepTickHashes ? new List<ulong>((int)Math.Min(config.DurationTicks, 1 << 24)) : null;

        metrics.SetClock(0, 0);
        scheduler.BeginStep(0);
        engine.AddEntities(world.Entities);

        var totalSteps = config.DurationTicks * TimeBase.StepsPerTick;
        var progressEvery = Math.Max(1, config.DurationTicks / 10);
        var lastSerialized = regionizer.Stats.SerializedTimestampTicks;
        var faultsSeen = 0;
        long ticksRun = 0;
        RunFailure? failure = null;
        for (var step = 0L; step < totalSteps; step++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            var tick = TimeBase.TickOf(step);
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

            if (TimeBase.EndsTick(step) || failure is not null)
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
        folder.WriteSummary(RunSummary.Build(runId, completed, ticksRun, failure, metrics, timerMonitor, config.Metrics, wall.Elapsed.TotalSeconds));
        folder.WriteManifest(RunManifest.Build(loaded, runId, gitSha, started, DateTime.UtcNow, ticksRun));
        folder.Complete();

        log.Information(
            "Run {RunId} finished: completed {Completed}, {TicksRun} ticks, hash {FinalHash}, {Merges} merges, {Splits} splits, {Fusions} fusions, {TwentyHzTicks} 20 Hz ticks, {MergesAgainstTicking} merges requested against ticking regions, {Seconds:F1} s",
            runId,
            completed,
            ticksRun,
            RunFolder.FormatHash(hasher.Current),
            metrics.Merges,
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
            Splits = metrics.Splits,
            Fusions = metrics.Fusions,
            TimersSet = timerMonitor.TimersSet,
            TimersFired = timerMonitor.TimersFired,
            InvariantChecks = invariants.Checks,
            TimerFaults = [.. timerMonitor.Faults],
            TickHashes = hashes is null ? [] : hashes,
        };
    }
}
