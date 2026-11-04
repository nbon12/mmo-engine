using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MmoEngine.Harness.Network;

/// <summary>A monotonic clock in nanoseconds (<see cref="Stopwatch"/>), so a wall-clock jump can't distort a round trip.</summary>
public static class MonotonicClock
{
    public static long NowNs() => (long)(Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency));
}

/// <summary>
/// The probe datagrams (contracts/network-profile.md). Every datagram starts with "MMOP", version 1, a type
/// byte and the 16-byte probe ID. Datagrams over <see cref="MaxDatagramBytes"/> are dropped.
/// </summary>
public static class ProbeDatagram
{
    public const int MaxDatagramBytes = 1200;
    public const byte Version = 1;
    public const byte Probe = 1;
    public const byte Echo = 2;
    public const byte ReportRequest = 3;
    public const byte ReportResponse = 4;

    /// <summary>The receive bitmap's size per report chunk, in sequence numbers.</summary>
    public const int ReportChunkBits = 8192;

    internal const int HeaderBytes = 4 + 1 + 1 + 16;
    private static ReadOnlySpan<byte> Magic => "MMOP"u8;

    internal static byte[] Header(byte type, Guid probeId, int bodyBytes)
    {
        var buffer = new byte[HeaderBytes + bodyBytes];
        Magic.CopyTo(buffer);
        buffer[4] = Version;
        buffer[5] = type;
        probeId.TryWriteBytes(buffer.AsSpan(6, 16));
        return buffer;
    }

    /// <summary>Reads a header; false for a foreign, oversized or truncated datagram.</summary>
    internal static bool TryRead(ReadOnlySpan<byte> datagram, out byte type, out Guid probeId)
    {
        type = 0;
        probeId = Guid.Empty;
        if (datagram.Length < HeaderBytes || datagram.Length > MaxDatagramBytes || !datagram[..4].SequenceEqual(Magic) || datagram[4] != Version)
        {
            return false;
        }

        type = datagram[5];
        probeId = new Guid(datagram.Slice(6, 16));
        return true;
    }

    internal static long ReadInt64(ReadOnlySpan<byte> datagram, int offset) => BinaryPrimitives.ReadInt64LittleEndian(datagram[(HeaderBytes + offset)..]);

    internal static void WriteInt64(byte[] datagram, int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(HeaderBytes + offset), value);
}

/// <summary>
/// The probe server (FR-024). It echoes every probe with its receive and send times, and it answers report
/// requests with a bitmap of the sequence numbers it received. That bitmap is how the client tells loss on
/// the way up from loss on the way down. It binds to the address it is given (localhost or the cluster
/// network) and drops datagrams over 1,200 bytes.
/// </summary>
public sealed class ProbeServer : IAsyncDisposable
{
    private readonly UdpClient socket;
    private readonly ConcurrentDictionary<Guid, HashSet<long>> received = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    public ProbeServer(IPEndPoint bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        socket = new UdpClient(bind);
        LocalEndPoint = (IPEndPoint)socket.Client.LocalEndPoint!;
        loop = Task.Run(() => RunAsync(stop.Token));
    }

    /// <summary>The bound endpoint (with the actual port when bound to port 0).</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Datagrams dropped as foreign, truncated or oversized.</summary>
    public int Dropped => dropped;

    private int dropped;

    /// <summary>The sequence numbers received for a probe.</summary>
    public IReadOnlyCollection<long> ReceivedFor(Guid probeId)
    {
        if (!received.TryGetValue(probeId, out var set))
        {
            return [];
        }

        lock (set)
        {
            return [.. set];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        socket.Dispose();
        await loop.ConfigureAwait(false);
        stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await UdpIo.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false) is { } result)
        {
            var recvNs = MonotonicClock.NowNs();
            if (!ProbeDatagram.TryRead(result.Buffer, out var type, out var probeId))
            {
                Interlocked.Increment(ref dropped);
                continue;
            }

            byte[]? reply = type switch
            {
                ProbeDatagram.Probe when result.Buffer.Length >= ProbeDatagram.HeaderBytes + 16 => EchoFor(result.Buffer, probeId, recvNs),
                ProbeDatagram.ReportRequest when result.Buffer.Length >= ProbeDatagram.HeaderBytes + 8 => ReportFor(result.Buffer, probeId),
                _ => null,
            };
            if (reply is null)
            {
                Interlocked.Increment(ref dropped);
                continue;
            }

            // A lost reply is what the probe measures.
            if (!await UdpIo.SendAsync(socket, reply, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private byte[] EchoFor(byte[] probe, Guid probeId, long recvNs)
    {
        var seq = ProbeDatagram.ReadInt64(probe, 0);
        var set = received.GetOrAdd(probeId, _ => []);
        lock (set)
        {
            set.Add(seq);
        }

        var echo = ProbeDatagram.Header(ProbeDatagram.Echo, probeId, 32);
        ProbeDatagram.WriteInt64(echo, 0, seq);
        ProbeDatagram.WriteInt64(echo, 8, ProbeDatagram.ReadInt64(probe, 8));
        ProbeDatagram.WriteInt64(echo, 16, recvNs);
        ProbeDatagram.WriteInt64(echo, 24, MonotonicClock.NowNs());
        return echo;
    }

    private byte[] ReportFor(byte[] request, Guid probeId)
    {
        var offset = ProbeDatagram.ReadInt64(request, 0);
        var bitmap = new byte[ProbeDatagram.ReportChunkBits / 8];
        if (received.TryGetValue(probeId, out var set))
        {
            lock (set)
            {
                foreach (var seq in set)
                {
                    var bit = seq - offset;
                    if (bit >= 0 && bit < ProbeDatagram.ReportChunkBits)
                    {
                        bitmap[bit / 8] |= (byte)(1 << (int)(bit % 8));
                    }
                }
            }
        }

        var response = ProbeDatagram.Header(ProbeDatagram.ReportResponse, probeId, 8 + bitmap.Length);
        ProbeDatagram.WriteInt64(response, 0, offset);
        bitmap.CopyTo(response, ProbeDatagram.HeaderBytes + 8);
        return response;
    }
}

/// <summary>The outcome of a probe run.</summary>
public sealed record ProbeRunResult(Guid ProbeId, IReadOnlyList<ProbeRecord> Records, int DuplicateEchoes, bool ReportComplete);

/// <summary>
/// The probe client (FR-024). It sends sequenced probes at a fixed rate (30 Hz by default) for a duration,
/// collects echoes until a grace period after the last send, and then fetches the server's receive bitmap.
/// An echo that arrives after the grace period counts as lost.
/// </summary>
public static class ProbeClient
{
    public static async Task<ProbeRunResult> RunAsync(IPEndPoint server, double rateHz, TimeSpan duration, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rateHz);
        var probeId = Guid.NewGuid();
        var count = (long)Math.Floor(duration.TotalSeconds * rateHz);
        var clientSend = new long[count];
        var echoes = new (long ServerRecv, long ServerSend, long ClientRecv)?[count];
        var duplicates = 0;
        using var socket = new UdpClient(new IPEndPoint(server.Address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reports = new ConcurrentDictionary<long, byte[]>();

        var receiver = Task.Run(async () =>
        {
            while (await UdpIo.ReceiveAsync(socket, receiving.Token).ConfigureAwait(false) is { } result)
            {
                var now = MonotonicClock.NowNs();
                if (!ProbeDatagram.TryRead(result.Buffer, out var type, out var id) || id != probeId)
                {
                    continue;
                }

                if (type == ProbeDatagram.Echo && result.Buffer.Length >= ProbeDatagram.HeaderBytes + 32)
                {
                    var seq = ProbeDatagram.ReadInt64(result.Buffer, 0);
                    if (seq < 0 || seq >= count)
                    {
                        continue;
                    }

                    if (echoes[seq] is null)
                    {
                        echoes[seq] = (ProbeDatagram.ReadInt64(result.Buffer, 16), ProbeDatagram.ReadInt64(result.Buffer, 24), now);
                    }
                    else
                    {
                        Interlocked.Increment(ref duplicates);
                    }
                }
                else if (type == ProbeDatagram.ReportResponse && result.Buffer.Length >= ProbeDatagram.HeaderBytes + 8 + (ProbeDatagram.ReportChunkBits / 8))
                {
                    reports[ProbeDatagram.ReadInt64(result.Buffer, 0)] = result.Buffer[(ProbeDatagram.HeaderBytes + 8)..];
                }
            }
        }, CancellationToken.None);

        var start = Stopwatch.GetTimestamp();
        for (long seq = 0; seq < count; seq++)
        {
            var due = start + (long)(seq / rateHz * Stopwatch.Frequency);
            var wait = due - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken).ConfigureAwait(false);
            }

            var datagram = ProbeDatagram.Header(ProbeDatagram.Probe, probeId, 16);
            ProbeDatagram.WriteInt64(datagram, 0, seq);
            clientSend[seq] = MonotonicClock.NowNs();
            ProbeDatagram.WriteInt64(datagram, 8, clientSend[seq]);
            await UdpIo.SendAsync(socket, datagram, server, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(grace, cancellationToken).ConfigureAwait(false);
        var lateCutoff = MonotonicClock.NowNs();

        // Fetch the server's receive bitmap, one chunk at a time, retrying lost requests.
        var complete = true;
        for (long offset = 0; offset < count; offset += ProbeDatagram.ReportChunkBits)
        {
            var request = ProbeDatagram.Header(ProbeDatagram.ReportRequest, probeId, 8);
            ProbeDatagram.WriteInt64(request, 0, offset);
            var got = false;
            for (var attempt = 0; attempt < 20 && !got; attempt++)
            {
                await UdpIo.SendAsync(socket, request, server, cancellationToken).ConfigureAwait(false);
                var deadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 5);
                while (!(got = reports.ContainsKey(offset)) && Stopwatch.GetTimestamp() < deadline)
                {
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                }
            }

            complete &= got;
        }

        await receiving.CancelAsync().ConfigureAwait(false);
        socket.Dispose();
        await receiver.ConfigureAwait(false);

        var records = new ProbeRecord[count];
        for (long seq = 0; seq < count; seq++)
        {
            var echo = echoes[seq] is { } e && e.ClientRecv <= lateCutoff ? e : ((long, long, long)?)null;
            var chunk = seq - (seq % ProbeDatagram.ReportChunkBits);
            var bit = seq - chunk;
            var inReport = reports.TryGetValue(chunk, out var bitmap) && (bitmap[bit / 8] & (1 << (int)(bit % 8))) != 0;
            // An echo proves the way up even if the report chunk was lost.
            records[seq] = new ProbeRecord(seq, clientSend[seq], inReport || echo is not null, echo?.Item1, echo?.Item2, echo?.Item3);
        }

        return new ProbeRunResult(probeId, records, duplicates, complete);
    }
}
