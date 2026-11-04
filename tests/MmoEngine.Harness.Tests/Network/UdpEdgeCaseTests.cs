using System.Net;
using System.Net.Sockets;
using MmoEngine.Harness.Bots;
using MmoEngine.Harness.Network;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>UDP edge cases: departed peers, foreign datagrams, duplicates and stopping (spec 014 edge cases).</summary>
public sealed class UdpEdgeCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A loopback port with nothing listening on it.</summary>
    private static IPEndPoint ClosedPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (IPEndPoint)probe.Client.LocalEndPoint!;
    }

    /// <summary>
    /// On a connected socket, Linux reports the ICMP "port unreachable" for an earlier send as a SocketException
    /// on the next receive. The receive skips it and keeps waiting, here until its token is cancelled.
    /// </summary>
    [Fact]
    public async Task ReceiveSkipsPortUnreachableAndKeepsWaiting()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Connect(ClosedPort());
        socket.Send([1], 1);
        await Task.Delay(50, Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var received = await UdpIo.ReceiveAsync(socket, timeout.Token);

        Assert.Null(received);
        Assert.True(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task ReceiveReturnsNullWhenCancelledOrDisposed()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Null(await UdpIo.ReceiveAsync(socket, cancelled.Token));
        socket.Dispose();
        Assert.Null(await UdpIo.ReceiveAsync(socket, Ct));
    }

    [Fact]
    public async Task SendIgnoresSocketErrorsAndStopsWhenDisposed()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

        // A datagram over UDP's 65,507-byte limit fails with a SocketException (message too long), which is ignored.
        Assert.True(await UdpIo.SendAsync(socket, new byte[70_000], new IPEndPoint(IPAddress.Loopback, 9), Ct));
        socket.Dispose();
        Assert.False(await UdpIo.SendAsync(socket, [1], new IPEndPoint(IPAddress.Loopback, 9), Ct));
    }

    /// <summary>A server that answers with foreign, out-of-range and duplicate echoes, and never with a report.</summary>
    [Fact]
    public async Task ClientIgnoresForeignAndOutOfRangeEchoesCountsDuplicatesAndFlagsAMissingReport()
    {
        using var fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var server = (IPEndPoint)fake.Client.LocalEndPoint!;
        var answering = Task.Run(async () =>
        {
            var probe = await fake.ReceiveAsync(Ct);
            ProbeDatagram.TryRead(probe.Buffer, out _, out var probeId);
            byte[] Echo(Guid id, long seq)
            {
                var echo = ProbeDatagram.Header(ProbeDatagram.Echo, id, 32);
                ProbeDatagram.WriteInt64(echo, 0, seq);
                ProbeDatagram.WriteInt64(echo, 8, ProbeDatagram.ReadInt64(probe.Buffer, 8));
                ProbeDatagram.WriteInt64(echo, 16, MonotonicClock.NowNs());
                ProbeDatagram.WriteInt64(echo, 24, MonotonicClock.NowNs());
                return echo;
            }

            await fake.SendAsync("junk"u8.ToArray(), probe.RemoteEndPoint, Ct);
            await fake.SendAsync(Echo(Guid.NewGuid(), 0), probe.RemoteEndPoint, Ct);
            await fake.SendAsync(Echo(probeId, 1_000_000), probe.RemoteEndPoint, Ct);
            await fake.SendAsync(Echo(probeId, 0), probe.RemoteEndPoint, Ct);
            await fake.SendAsync(Echo(probeId, 0), probe.RemoteEndPoint, Ct);
        }, Ct);

        var run = await ProbeClient.RunAsync(server, 10, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), Ct);
        await answering;

        Assert.Single(run.Records);
        Assert.True(run.Records[0].Echoed);
        Assert.Equal(1, run.DuplicateEchoes);
        Assert.False(run.ReportComplete);
    }

    [Fact]
    public async Task ServerAnswersAReportForAnUnknownProbeWithAnEmptyBitmap()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));

        Assert.Empty(server.ReceivedFor(Guid.NewGuid()));
    }

    /// <summary>A probe client that leaves before its echo arrives makes the server's next receive fail; the server keeps serving.</summary>
    [Fact]
    public async Task ServerKeepsServingAfterAClientLeaves()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));
        using (var leaving = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var probe = ProbeDatagram.Header(ProbeDatagram.Probe, Guid.NewGuid(), 16);
            leaving.Client.SendTo(probe, server.LocalEndPoint);
        }

        await Task.Delay(50, Ct);
        var run = await ProbeClient.RunAsync(server.LocalEndPoint, 30, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200), Ct);

        Assert.All(run.Records, r => Assert.True(r.Echoed));
    }

    /// <summary>The stub target ignores foreign datagrams, and keeps serving after a bot leaves.</summary>
    [Fact]
    public async Task StubTargetIgnoresForeignDatagramsAndDepartedBots()
    {
        await using var target = new StubTarget(new IPEndPoint(IPAddress.Loopback, 0));
        using (var leaving = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            leaving.Client.SendTo("junk"u8.ToArray(), target.LocalEndPoint);
            leaving.Client.SendTo(StubDatagram.Write(StubDatagram.Snapshot, 9, 0, 0, 0), target.LocalEndPoint);
            leaving.Client.SendTo(StubDatagram.Write(StubDatagram.Input, 9, 0, 0, 0), target.LocalEndPoint);
        }

        await Task.Delay(150, Ct);

        Assert.Equal(1, target.InputsReceived);
        Assert.True(target.SnapshotsSent > 0);
    }

    [Fact]
    public void StubDatagramRoundTripsAndRejectsWrongSizes()
    {
        var datagram = StubDatagram.Write(StubDatagram.Snapshot, 7, 11, 13, 1.5);

        Assert.True(StubDatagram.TryRead(datagram, out var type, out var bot, out var a, out var b, out var position));
        Assert.Equal((StubDatagram.Snapshot, 7u, 11ul, 13ul, 1.5), (type, bot, a, b, position));
        Assert.False(StubDatagram.TryRead(datagram.AsSpan(1), out _, out _, out _, out _, out _));
    }

    /// <summary>Underruns against a target that isn't there: every frame after the first few is an underrun.</summary>
    [Fact]
    public async Task BotsAgainstAClosedPortRecordUnderrunsAndNoSnapshots()
    {
        var sink = new MemoryFrameSink();

        await BotHost.RunAsync(ClosedPort(), new BotHostOptions { RunId = RunFolders.UniqueId("bot"), Bots = 1, Duration = TimeSpan.FromMilliseconds(300) }, sink, Ct);

        Assert.All(sink.Frames.Where(f => f.Frame >= 2), f => Assert.True(f.Underrun));
        Assert.All(sink.Frames, f => Assert.Equal(0u, f.SnapshotsReceived));
    }

    [Fact]
    public async Task MemorySinkRecordsInvalidHosts()
    {
        var sink = new MemoryFrameSink();

        await sink.MarkHostInvalidAsync("r", "h1", Ct);

        Assert.True(sink.IsInvalid("h1"));
        Assert.False(sink.IsInvalid("h2"));
    }

    [Fact]
    public async Task IdleWriterFlushesNothing()
    {
        var sink = new MemoryFrameSink();
        var writer = new BatchWriter(sink);

        await Task.Delay(TimeSpan.FromMilliseconds(1200), Ct);
        await writer.CompleteAsync();

        Assert.Empty(sink.Batches);
    }

    [Fact]
    public void ReorderedArrivalsAreFlaggedInNetSamples()
    {
        ProbeRecord[] records =
        [
            new(0, 0, true, 10, 11, 50),
            new(1, 5, true, 12, 13, 40),
        ];

        var rows = NetSamples.ToRows("r", new ProbeRunResult(Guid.NewGuid(), records, 0, true)).ToList();

        var down0 = rows.Single(r => (string)r[2]! == "down" && (ulong)r[3]! == 0);
        Assert.Equal((byte)1, down0[8]);
        Assert.Equal((byte)0, rows.Single(r => (string)r[2]! == "down" && (ulong)r[3]! == 1)[8]);
    }
}
