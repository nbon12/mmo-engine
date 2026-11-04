using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MmoEngine.Harness.Network;

namespace MmoEngine.Harness.Bots;

/// <summary>
/// The stub datagrams (contracts/network-profile.md, "Stub target"). Every datagram starts with "MMOB",
/// version 1 and a type byte. Inputs and their echoes carry a bot ID, a frame number and a position.
/// Snapshots carry a tick, the bot's last acknowledged input frame and the server's position for it.
/// </summary>
public static class StubDatagram
{
    public const int MaxDatagramBytes = 1200;
    public const byte Version = 1;
    public const byte Input = 1;
    public const byte InputEcho = 2;
    public const byte Snapshot = 3;

    /// <summary>The body size of every message: bot ID, a u64 (frame or tick), a u64 (frame), and a double (position).</summary>
    internal const int BodyBytes = 4 + 8 + 8 + 8;
    internal const int HeaderBytes = 6;
    private static ReadOnlySpan<byte> Magic => "MMOB"u8;

    internal static byte[] Write(byte type, uint botId, ulong a, ulong b, double position)
    {
        var buffer = new byte[HeaderBytes + BodyBytes];
        Magic.CopyTo(buffer);
        buffer[4] = Version;
        buffer[5] = type;
        var body = buffer.AsSpan(HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(body, botId);
        BinaryPrimitives.WriteUInt64LittleEndian(body[4..], a);
        BinaryPrimitives.WriteUInt64LittleEndian(body[12..], b);
        BinaryPrimitives.WriteDoubleLittleEndian(body[20..], position);
        return buffer;
    }

    internal static bool TryRead(ReadOnlySpan<byte> datagram, out byte type, out uint botId, out ulong a, out ulong b, out double position)
    {
        type = 0;
        botId = 0;
        a = 0;
        b = 0;
        position = 0;
        if (datagram.Length != HeaderBytes + BodyBytes || datagram.Length > MaxDatagramBytes || !datagram[..4].SequenceEqual(Magic) || datagram[4] != Version)
        {
            return false;
        }

        type = datagram[5];
        var body = datagram[HeaderBytes..];
        botId = BinaryPrimitives.ReadUInt32LittleEndian(body);
        a = BinaryPrimitives.ReadUInt64LittleEndian(body[4..]);
        b = BinaryPrimitives.ReadUInt64LittleEndian(body[12..]);
        position = BinaryPrimitives.ReadDoubleLittleEndian(body[20..]);
        return true;
    }
}

/// <summary>
/// A small server that stands in for the game until Phase 1 (FR-028). It echoes every input it receives,
/// and sends each connected bot a fake 30 Hz snapshot holding the bot's last acknowledged input and
/// position. It binds to the address it is given and drops foreign or oversized datagrams.
/// </summary>
public sealed class StubTarget : IAsyncDisposable
{
    /// <summary>The snapshot rate.</summary>
    public const double SnapshotHz = 30;

    private readonly UdpClient socket;
    private readonly ConcurrentDictionary<uint, (IPEndPoint EndPoint, ulong Frame, double Position)> bots = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task receiver;
    private readonly Task sender;
    private long inputsReceived;
    private long inputsEchoed;
    private long snapshotsSent;

    public StubTarget(IPEndPoint bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        socket = new UdpClient(bind);
        LocalEndPoint = (IPEndPoint)socket.Client.LocalEndPoint!;
        receiver = Task.Run(() => ReceiveAsync(stop.Token));
        sender = Task.Run(() => SnapshotsAsync(stop.Token));
    }

    public IPEndPoint LocalEndPoint { get; }

    public long InputsReceived => Interlocked.Read(ref inputsReceived);

    public long InputsEchoed => Interlocked.Read(ref inputsEchoed);

    public long SnapshotsSent => Interlocked.Read(ref snapshotsSent);

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        socket.Dispose();
        await Task.WhenAll(receiver, sender).ConfigureAwait(false);
        stop.Dispose();
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        while (await UdpIo.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false) is { } result)
        {
            if (!StubDatagram.TryRead(result.Buffer, out var type, out var botId, out var frame, out _, out var position) || type != StubDatagram.Input)
            {
                continue;
            }

            Interlocked.Increment(ref inputsReceived);
            bots.AddOrUpdate(botId, (result.RemoteEndPoint, frame, position),
                (_, old) => frame >= old.Frame ? (result.RemoteEndPoint, frame, position) : old);
            if (!await UdpIo.SendAsync(socket, StubDatagram.Write(StubDatagram.InputEcho, botId, frame, frame, position), result.RemoteEndPoint, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            Interlocked.Increment(ref inputsEchoed);
        }
    }

    private async Task SnapshotsAsync(CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        for (ulong tick = 0; !cancellationToken.IsCancellationRequested; tick++)
        {
            var due = start + (long)(tick / SnapshotHz * Stopwatch.Frequency);
            var wait = due - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                // Completes without throwing when cancelled; the loop condition then stops it.
                await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), cancellationToken)
                    .ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).ConfigureAwait(false);
            }

            foreach (var (botId, state) in bots)
            {
                if (!await UdpIo.SendAsync(socket, StubDatagram.Write(StubDatagram.Snapshot, botId, tick, state.Frame, state.Position), state.EndPoint, cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                Interlocked.Increment(ref snapshotsSent);
            }
        }
    }
}
