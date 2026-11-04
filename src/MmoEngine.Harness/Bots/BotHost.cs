using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MmoEngine.Harness.Bots;

/// <summary>The outcome of a bot host run.</summary>
public sealed record BotHostResult(string HostId, long Frames, long DeadlineMisses, bool Valid, IReadOnlyDictionary<uint, long> FramesPerBot);

/// <summary>Bot host options.</summary>
public sealed record BotHostOptions
{
    public required string RunId { get; init; }
    public string HostId { get; init; } = Environment.MachineName;
    public required int Bots { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>The frame rate (FR-027: 60 Hz).</summary>
    public double FrameHz { get; init; } = 60;

    /// <summary>The share of frames that may miss their deadline before the host's rows are invalid (FR-029: 1%).</summary>
    public double MissAllowance { get; init; } = 0.01;

    /// <summary>Distance beyond which a correction is a snap, in metres.</summary>
    public double SnapThresholdM { get; init; } = 1.0;

    /// <summary>Frames without a new snapshot before a frame counts as an underrun (snapshots are 30 Hz, frames 60 Hz).</summary>
    public int UnderrunFrames { get; init; } = 3;

    /// <summary>Test hook: extra work done at the start of each frame, to make a host miss deadlines.</summary>
    public Action<long>? FrameWork { get; init; }
}

/// <summary>
/// Runs many headless bots against a target on one fixed frame loop (FR-027, research R8). Each frame,
/// every bot sends one input and reads what arrived. The host writes one <c>bot_frames</c> row per bot per
/// frame through a <see cref="BatchWriter"/>.
/// <para>
/// A frame that starts more than one frame period late is a deadline miss. The loop re-anchors after a
/// miss instead of catching up, so overload shows as misses, not as stretched frames. If misses exceed the
/// allowance, the host marks its rows invalid (FR-029).
/// </para>
/// <para>
/// The bot logic is a stub until Phase 1, when the real client netcode replaces it. Each bot moves at
/// 1 m/s and predicts its position. Correction is the distance between the prediction for the
/// acknowledged frame and the snapshot's position.
/// </para>
/// </summary>
public static class BotHost
{
    public static async Task<BotHostResult> RunAsync(IPEndPoint target, BotHostOptions options, IFrameSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Bots);
        var bots = Enumerable.Range(1, options.Bots).Select(i => new Bot((uint)i, target)).ToList();
        var writer = new BatchWriter(sink);
        var period = Stopwatch.Frequency / options.FrameHz;
        var end = Stopwatch.GetTimestamp() + (long)(options.Duration.TotalSeconds * Stopwatch.Frequency);
        long frames = 0;
        long misses = 0;
        var anchor = Stopwatch.GetTimestamp();
        long sinceAnchor = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var due = anchor + (long)(sinceAnchor * period);
                var wait = due - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken).ConfigureAwait(false);
                }

                var start = Stopwatch.GetTimestamp();
                if (start >= end)
                {
                    break;
                }

                var missed = start - due > period;
                if (missed)
                {
                    misses++;
                }

                options.FrameWork?.Invoke(frames);
                foreach (var bot in bots)
                {
                    writer.Add(bot.Step((ulong)frames, missed, options));
                }

                frames++;
                if (missed)
                {
                    // Re-anchor: don't catch up on the frames lost to the overrun.
                    anchor = Stopwatch.GetTimestamp();
                    sinceAnchor = 1;
                }
                else
                {
                    sinceAnchor++;
                }
            }
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
            foreach (var bot in bots)
            {
                bot.Dispose();
            }
        }

        var valid = frames > 0 && misses <= options.MissAllowance * frames;
        if (!valid)
        {
            await sink.MarkHostInvalidAsync(options.RunId, options.HostId, CancellationToken.None).ConfigureAwait(false);
        }

        return new BotHostResult(options.HostId, frames, misses, valid, bots.ToDictionary(b => b.Id, b => b.Frames));
    }

    private sealed class Bot : IDisposable
    {
        private const double SpeedMps = 1.0;
        private readonly UdpClient socket;
        private readonly IPEndPoint target;
        private readonly Dictionary<ulong, double> predicted = [];
        private int framesSinceSnapshot;

        public Bot(uint id, IPEndPoint target)
        {
            Id = id;
            this.target = target;
            socket = new UdpClient(new IPEndPoint(target.Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            socket.Client.Blocking = false;
        }

        public uint Id { get; }

        public long Frames { get; private set; }

        public BotFrame Step(ulong frame, bool missed, BotHostOptions options)
        {
            var position = frame * SpeedMps / options.FrameHz;
            predicted[frame] = position;
            try
            {
                socket.Send(StubDatagram.Write(StubDatagram.Input, Id, frame, frame, position), target);
            }
            catch (SocketException)
            {
                // A lost input is the network's business.
            }

            uint snapshots = 0;
            double correction = 0;
            var buffer = new byte[StubDatagram.MaxDatagramBytes];
            while (socket.Available > 0)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int read;
                try
                {
                    read = socket.Client.ReceiveFrom(buffer, ref from);
                }
                catch (SocketException)
                {
                    break;
                }

                if (StubDatagram.TryRead(buffer.AsSpan(0, read), out var type, out var botId, out _, out var ackFrame, out var serverPosition)
                    && type == StubDatagram.Snapshot && botId == Id)
                {
                    snapshots++;
                    if (predicted.TryGetValue(ackFrame, out var mine))
                    {
                        correction = Math.Max(correction, Math.Abs(mine - serverPosition));
                    }

                    foreach (var old in predicted.Keys.Where(k => k < ackFrame).ToList())
                    {
                        predicted.Remove(old);
                    }
                }
            }

            framesSinceSnapshot = snapshots > 0 ? 0 : framesSinceSnapshot + 1;
            Frames++;
            return new BotFrame(options.RunId, options.HostId, Id, frame, missed, correction, correction > options.SnapThresholdM,
                framesSinceSnapshot >= options.UnderrunFrames, 0, snapshots);
        }

        public void Dispose() => socket.Dispose();
    }
}
