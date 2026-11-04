using System.Diagnostics;
using System.Threading.Channels;
using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Bots;

/// <summary>One bot frame: a <c>bot_frames</c> row (data-model.md).</summary>
public readonly record struct BotFrame(
    string RunId,
    string HostId,
    uint BotId,
    ulong Frame,
    bool DeadlineMissed,
    double CorrectionM,
    bool Snap,
    bool Underrun,
    double RemotePositionErrorM,
    uint SnapshotsReceived);

/// <summary>Where bot frames go: the store in real runs, memory in tests.</summary>
public interface IFrameSink
{
    /// <summary>Writes one batch.</summary>
    Task WriteAsync(IReadOnlyList<BotFrame> batch, CancellationToken cancellationToken);

    /// <summary>Marks every row of a host invalid (FR-029).</summary>
    Task MarkHostInvalidAsync(string runId, string hostId, CancellationToken cancellationToken);
}

/// <summary>Writes frames to <c>bot_frames</c>.</summary>
public sealed class StoreFrameSink(IHarnessStore store) : IFrameSink
{
    public static IReadOnlyList<string> Columns { get; } =
    [
        "run_id", "host_id", "bot_id", "frame", "deadline_missed", "correction_m", "snap", "underrun",
        "remote_position_error_m", "snapshots_received", "valid",
    ];

    public Task WriteAsync(IReadOnlyList<BotFrame> batch, CancellationToken cancellationToken) =>
        store.InsertAsync("bot_frames", Columns, batch.Select(f => new object?[]
        {
            f.RunId, f.HostId, f.BotId, f.Frame, Flag(f.DeadlineMissed), f.CorrectionM, Flag(f.Snap), Flag(f.Underrun),
            f.RemotePositionErrorM, f.SnapshotsReceived, (byte)1,
        }), cancellationToken);

    public Task MarkHostInvalidAsync(string runId, string hostId, CancellationToken cancellationToken) =>
        store.MarkBotHostInvalidAsync(runId, hostId, cancellationToken);

    private static byte Flag(bool value) => (byte)(value ? 1 : 0);
}

/// <summary>Keeps frames in memory, for tests.</summary>
public sealed class MemoryFrameSink : IFrameSink
{
    private readonly List<IReadOnlyList<BotFrame>> batches = [];
    private readonly HashSet<string> invalidHosts = [];

    public IReadOnlyList<IReadOnlyList<BotFrame>> Batches
    {
        get
        {
            lock (batches)
            {
                return [.. batches];
            }
        }
    }

    public IReadOnlyList<BotFrame> Frames => [.. Batches.SelectMany(b => b)];

    public bool IsInvalid(string hostId)
    {
        lock (batches)
        {
            return invalidHosts.Contains(hostId);
        }
    }

    public Task WriteAsync(IReadOnlyList<BotFrame> batch, CancellationToken cancellationToken)
    {
        lock (batches)
        {
            batches.Add([.. batch]);
        }

        return Task.CompletedTask;
    }

    public Task MarkHostInvalidAsync(string runId, string hostId, CancellationToken cancellationToken)
    {
        lock (batches)
        {
            invalidHosts.Add(hostId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Batches frames off the frame loop (research R8). It flushes at <see cref="BatchRows"/> rows, or once
/// <see cref="FlushInterval"/> has passed since the last flush, whichever comes first, and once more at
/// completion (FR-027).
/// </summary>
public sealed class BatchWriter : IAsyncDisposable
{
    public const int BatchRows = 5000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<BotFrame> channel = Channel.CreateUnbounded<BotFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IFrameSink sink;
    private readonly Task pump;

    public BatchWriter(IFrameSink sink)
    {
        this.sink = sink;
        pump = Task.Run(PumpAsync);
    }

    /// <summary>Queues a frame; never blocks the frame loop.</summary>
    public void Add(BotFrame frame) => channel.Writer.TryWrite(frame);

    /// <summary>Stops accepting frames and waits for the final flush.</summary>
    public async Task CompleteAsync()
    {
        channel.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CompleteAsync().ConfigureAwait(false);

    private async Task PumpAsync()
    {
        var buffer = new List<BotFrame>(BatchRows);
        var sinceFlush = Stopwatch.StartNew();
        var reader = channel.Reader;
        while (true)
        {
            var remaining = FlushInterval - sinceFlush.Elapsed;
            var more = true;
            if (remaining > TimeSpan.Zero)
            {
                using var timeout = new CancellationTokenSource(remaining);
                try
                {
                    more = await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The flush interval passed with nothing new.
                }
            }

            while (buffer.Count < BatchRows && reader.TryRead(out var frame))
            {
                buffer.Add(frame);
                if (buffer.Count == BatchRows)
                {
                    await FlushAsync(buffer, sinceFlush).ConfigureAwait(false);
                }
            }

            if (sinceFlush.Elapsed >= FlushInterval && buffer.Count > 0)
            {
                await FlushAsync(buffer, sinceFlush).ConfigureAwait(false);
            }
            else if (sinceFlush.Elapsed >= FlushInterval)
            {
                sinceFlush.Restart();
            }

            if (!more || (reader.Completion.IsCompleted && !reader.TryPeek(out _)))
            {
                break;
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(buffer, sinceFlush).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<BotFrame> buffer, Stopwatch sinceFlush)
    {
        await sink.WriteAsync([.. buffer], CancellationToken.None).ConfigureAwait(false);
        buffer.Clear();
        sinceFlush.Restart();
    }
}
