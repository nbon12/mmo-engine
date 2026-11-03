using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Running;

namespace MmoEngine.Regionizer.Simulator.Trace;

/// <summary>
/// The run options a configuration's <c>trace</c> section asks for, and the trace files they hold
/// open. Dispose it after the run to flush the recorded trace and close the replayed one.
/// </summary>
public sealed class TraceAttachment : IDisposable
{
    internal TraceAttachment(RunOptions options, TraceRecorder? recorder, string? replayPath)
    {
        Recorder = recorder;
        ReplayPath = replayPath;
        Options = replayPath is null
            ? options
            : options with { MovementSourceFactory = ReplayMovementSource.Factory(replayPath, source => Replay = source) };
    }

    /// <summary>The caller's options with the recorder and the replay source added.</summary>
    public RunOptions Options { get; }

    /// <summary>The recorder, when <c>trace.record</c> is set.</summary>
    public TraceRecorder? Recorder { get; }

    /// <summary>The replayed trace, when <c>trace.replay</c> is set.</summary>
    public string? ReplayPath { get; }

    /// <summary>The replay source, once the run has created it.</summary>
    public ReplayMovementSource? Replay { get; private set; }

    public void Dispose()
    {
        Recorder?.Dispose();
        Replay?.Dispose();
    }
}

/// <summary>
/// Wires the <c>trace</c> configuration into a run (FR-022, contracts/config-and-cli.md): with
/// <c>trace.record</c> set, a <see cref="TraceRecorder"/> joins the run's listeners; with
/// <c>trace.replay</c> set, a <see cref="ReplayMovementSource"/> replaces the scenario's movement.
/// Relative paths are resolved against the working directory.
/// </summary>
public static class TraceRunOptions
{
    public const string RecordPath = "trace.record";

    public const string ReplayPath = "trace.replay";

    /// <summary>Checks the trace settings of <paramref name="loaded"/> and attaches them to <paramref name="options"/>.</summary>
    /// <exception cref="ConfigValidationException">
    /// The replayed trace is missing or invalid, the recorded trace's path is a directory, or both
    /// name the same file (exit code 2).
    /// </exception>
    public static TraceAttachment Attach(LoadedRunConfig loaded, RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(options);
        var trace = loaded.Config.Trace;
        var record = Resolve(trace.Record);
        var replay = Resolve(trace.Replay);
        var errors = new List<ConfigError>();
        if (replay is not null)
        {
            try
            {
                TraceReader.Validate(replay);
            }
            catch (TraceFormatException ex)
            {
                errors.Add(new ConfigError(ReplayPath, $"'{replay}' is not a valid trace: {ex.Message}"));
            }
        }

        if (record is not null)
        {
            if (Directory.Exists(record))
            {
                errors.Add(new ConfigError(RecordPath, $"'{record}' is a directory; it must name a file"));
            }
            else if (string.Equals(record, replay, StringComparison.Ordinal))
            {
                errors.Add(new ConfigError(RecordPath, "must name a different file than trace.replay"));
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigValidationException(errors);
        }

        var recorder = record is null ? null : TraceRecorder.For(loaded, record);
        var withRecorder = recorder is null ? options : options with { Listeners = [.. options.Listeners, recorder] };
        return new TraceAttachment(withRecorder, recorder, replay);
    }

    private static string? Resolve(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
