using System.Collections.Concurrent;
using System.Globalization;
using MmoEngine.Regionizer.Simulator.Monitors;
using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Sweep;

/// <summary>How a sweep runs.</summary>
public sealed record SweepRunOptions
{
    /// <summary>Runs at once; the processor count when null.</summary>
    public int? Parallelism { get; init; }

    /// <summary>
    /// The options every run gets. Hooks, listeners, observers and the logger are shared by runs on
    /// several threads, so they must be thread-safe. When <see cref="RunOptions.GitSha"/> is null it
    /// is read once for the sweep.
    /// </summary>
    public RunOptions RunOptions { get; init; } = new();

    /// <summary>The sweep's own log, written only from the calling thread; none when null.</summary>
    public Serilog.ILogger? Logger { get; init; }
}

/// <summary>The outcome of one run of a sweep.</summary>
/// <param name="Run">The run.</param>
/// <param name="FolderPath">Its run folder.</param>
/// <param name="RunId">Its run ID.</param>
/// <param name="Completed">Every tick ran with no violation or timer fault.</param>
/// <param name="ExitCode">As <c>regionizer-sim run</c>: 0 clean, 3 violation or timer fault, 1 the run failed.</param>
/// <param name="FinalHash">The chained state hash after the last tick, or null when the run failed.</param>
/// <param name="FirstViolation">The first violation or timer fault, or null.</param>
/// <param name="Error">Why the run failed, or null.</param>
public sealed record SweepRunOutcome(
    SweepRun Run,
    string FolderPath,
    string RunId,
    bool Completed,
    int ExitCode,
    ulong? FinalHash,
    RunFailure? FirstViolation,
    string? Error);

/// <summary>The outcome of a sweep.</summary>
/// <param name="ExitCode">0 when every run is clean, 3 when any run violated, 1 when any other run failed.</param>
/// <param name="SweepDirectory"><c>&lt;out&gt;/&lt;sweep&gt;</c>.</param>
/// <param name="IndexPath">sweep.json.</param>
/// <param name="CellsPath">cells.csv.</param>
/// <param name="Runs">Every run's outcome, in run order.</param>
public sealed record SweepOutcome(int ExitCode, string SweepDirectory, string IndexPath, string CellsPath, IReadOnlyList<SweepRunOutcome> Runs)
{
    public int Completed => Runs.Count(r => r.Completed);

    public int Violations => Runs.Count(r => r.ExitCode == ExitCodes.Violation);

    public int Errors => Runs.Count(r => r.ExitCode == ExitCodes.Error);
}

/// <summary>
/// Runs a sweep (FR-025): every (cell, seed) run of a <see cref="SweepPlan"/>, up to
/// <see cref="SweepRunOptions.Parallelism"/> at once, each in its own folder
/// <c>&lt;out&gt;/&lt;sweep&gt;/&lt;cellId&gt;/seed-&lt;i&gt;/</c>. Runs are independent and single-threaded,
/// so each run's result is the same whatever the parallelism. <c>sweep.json</c> is written before
/// the first run and rewritten, atomically, as runs complete; <c>cells.csv</c> is written at the end.
/// </summary>
public static class SweepDriver
{
    /// <summary>Runs <paramref name="plan"/> under <paramref name="outputDirectory"/>.</summary>
    public static SweepOutcome Run(SweepPlan plan, string outputDirectory, SweepRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new SweepRunOptions();
        var parallelism = options.Parallelism ?? Environment.ProcessorCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1, nameof(options));
        var log = options.Logger ?? Serilog.Core.Logger.None;
        var runOptions = options.RunOptions with { GitSha = options.RunOptions.GitSha ?? RunManifest.GitSha() };

        var sweepDir = Path.GetFullPath(Path.Combine(outputDirectory, plan.Spec.Name));
        Directory.CreateDirectory(sweepDir);
        var index = new SweepIndex(plan, sweepDir);
        var outcomes = new SweepRunOutcome?[plan.Runs.Count];
        index.Write(outcomes);
        log.Information(
            "Sweep {Sweep} started: {Cells} cells, {Seeds} seeds, {Runs} runs, parallelism {Parallelism}, into {Directory}",
            plan.Spec.Name,
            plan.Cells.Count,
            plan.Seeds.Count,
            plan.Runs.Count,
            parallelism,
            sweepDir);

        using (var finished = new BlockingCollection<int>())
        {
            var work = Task.Run(() =>
            {
                try
                {
                    // One run at a time per worker: runs can differ a lot in length, so no worker holds a backlog.
                    Parallel.ForEach(
                        Partitioner.Create(plan.Runs, EnumerablePartitionerOptions.NoBuffering),
                        new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = runOptions.CancellationToken },
                        run =>
                        {
                            Volatile.Write(ref outcomes[run.Index], RunOne(run, sweepDir, runOptions));
                            finished.Add(run.Index);
                        });
                }
                finally
                {
                    finished.CompleteAdding();
                }
            });

            try
            {
                foreach (var first in finished.GetConsumingEnumerable())
                {
                    var done = first;
                    do
                    {
                        LogOutcome(log, Volatile.Read(ref outcomes[done])!);
                    }
                    while (finished.TryTake(out done));

                    index.Write(outcomes);
                }
            }
            finally
            {
                // Runs in progress finish before the sweep returns, even if writing the index failed.
                Task.WaitAny(work);
                index.Write(outcomes);
            }

            work.GetAwaiter().GetResult();
        }

        var results = outcomes.Select(o => o!).ToArray();
        var cellsPath = Path.Combine(sweepDir, CellsCsv.FileName);
        CellsCsv.Write(cellsPath, plan, results);
        var outcome = new SweepOutcome(ExitCode(results), sweepDir, index.Path, cellsPath, results);
        log.Information(
            "Sweep {Sweep} finished: exit code {ExitCode}, {Completed} of {Runs} runs completed, {Violations} violated, {Errors} failed",
            plan.Spec.Name,
            outcome.ExitCode,
            outcome.Completed,
            results.Length,
            outcome.Violations,
            outcome.Errors);
        return outcome;
    }

    /// <summary>3 when any run violated, otherwise 1 when any run failed, otherwise 0.</summary>
    public static int ExitCode(IReadOnlyList<SweepRunOutcome> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Any(r => r.ExitCode == ExitCodes.Violation))
        {
            return ExitCodes.Violation;
        }

        return runs.Any(r => r.ExitCode != ExitCodes.Clean) ? ExitCodes.Error : ExitCodes.Clean;
    }

    private static SweepRunOutcome RunOne(SweepRun run, string sweepDir, RunOptions options)
    {
        var folder = Path.Combine(sweepDir, run.Cell.CellId, "seed-" + run.SeedIndex.ToString(CultureInfo.InvariantCulture));
        var runId = RunManifest.RunId(run.Loaded);
        try
        {
            var result = SimulationRunner.Run(run.Loaded, folder, options);
            return new SweepRunOutcome(run, folder, result.RunId, result.Completed, result.ExitCode, result.FinalHash, result.FirstViolation, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SweepRunOutcome(run, folder, runId, false, ExitCodes.Error, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogOutcome(Serilog.ILogger log, SweepRunOutcome o)
    {
        if (o.Error is not null)
        {
            log.Error("Run {RunId} ({CellId}, seed {SeedIndex} = {Seed}) failed: {Error}", o.RunId, o.Run.Cell.CellId, o.Run.SeedIndex, o.Run.Seed, o.Error);
        }
        else if (o.FirstViolation is { } v)
        {
            log.Error(
                "Run {RunId} ({CellId}, seed {SeedIndex} = {Seed}) stopped: {Kind} rule {Rule} at tick {Tick}: {Detail}",
                o.RunId,
                o.Run.Cell.CellId,
                o.Run.SeedIndex,
                v.Seed,
                v.Kind,
                v.RuleNumber,
                v.Tick,
                v.Detail);
        }
        else
        {
            log.Information(
                "Run {RunId} ({CellId}, seed {SeedIndex} = {Seed}) finished: hash {FinalHash}",
                o.RunId,
                o.Run.Cell.CellId,
                o.Run.SeedIndex,
                o.Run.Seed,
                RunFolder.FormatHash(o.FinalHash ?? 0));
        }
    }
}
