using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MmoEngine.Harness.Network;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>US4 scenario 9: the probe pair on unimpaired loopback (FR-024).</summary>
public sealed class ProbePairTests(ClickHouseFixture clickHouse)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>US4 scenario 9: every sequence number sent is in the samples with send and receive times, and none is lost.</summary>
    [Fact]
    public async Task LoopbackProbeRunRecordsEverySentPacketAndLosesNone()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));

        var run = await ProbeClient.RunAsync(server.LocalEndPoint, 30, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), Ct);

        Assert.Equal(60, run.Records.Count);
        Assert.Equal(Enumerable.Range(0, 60).Select(i => (long)i), run.Records.Select(r => r.Seq));
        Assert.True(run.ReportComplete);
        Assert.All(run.Records, r =>
        {
            Assert.True(r.UpReceived);
            Assert.True(r.Echoed);
            Assert.True(r.ClientRecvNs > r.ClientSendNs);
        });
        Assert.Equal(60, server.ReceivedFor(run.ProbeId).Count);

        var lan = new NetworkProfile { Name = "LAN", Version = 1, RoundTripMs = 1, JitterMs = 0, LossPercent = 0, Tolerances = new Tolerances { AbsoluteMs = 0.5 } };
        var calibration = Calibration.Evaluate(lan, run.Records);
        Assert.Equal(0, calibration.UpLossPercent);
        Assert.Equal(0, calibration.DownLossPercent);
        Assert.Equal(0, calibration.UpReordered + calibration.DownReordered);
    }

    /// <summary>FR-024 through the store: every packet becomes net_samples rows, and calibration reads them back.</summary>
    [Fact]
    public async Task ProbeRowsRoundTripThroughNetSamples()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));
        var run = await ProbeClient.RunAsync(server.LocalEndPoint, 30, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300), Ct);
        var runId = RunFolders.UniqueId("probe");
        using var store = clickHouse.CreateStore();

        await store.InsertAsync("net_samples", NetSamples.Columns, NetSamples.ToRows(runId, run), Ct);
        var back = NetSamples.FromRows(await store.QueryRowsAsync(NetSamples.Query, runId, Ct));

        Assert.Equal(30, Convert.ToInt64(await store.ScalarAsync("SELECT countIf(direction = 'up') FROM net_samples WHERE run_id = {run_id:String}", runId, Ct), CultureInfo.InvariantCulture));
        Assert.Equal(30, Convert.ToInt64(await store.ScalarAsync("SELECT countIf(direction = 'down') FROM net_samples WHERE run_id = {run_id:String}", runId, Ct), CultureInfo.InvariantCulture));
        Assert.Equal(run.Records, back);
    }

    /// <summary>Loss split by direction: lost echoes and lost probes map to the right rows and back.</summary>
    [Fact]
    public void LostPacketsMapToTheirDirection()
    {
        ProbeRecord[] records =
        [
            new(0, 10, true, 15, 16, 30),
            new(1, 20, false, null, null, null),
            new(2, 30, true, null, null, null),
        ];
        var rows = NetSamples.ToRows("r", new ProbeRunResult(Guid.NewGuid(), records, 0, true)).ToList();

        Assert.Equal(5, rows.Count);
        Assert.Equal((byte)1, rows.Single(r => (string)r[2]! == "up" && (ulong)r[3]! == 1)[6]);
        Assert.Equal((byte)1, rows.Single(r => (string)r[2]! == "down" && (ulong)r[3]! == 2)[6]);
        Assert.DoesNotContain(rows, r => (string)r[2]! == "down" && (ulong)r[3]! == 1);

        var back = NetSamples.FromRows(rows.Select(r => new[] { r[2], r[3], r[4], r[5], r[6] }));
        Assert.Equal(records, back);
    }

    /// <summary>The server drops foreign and oversized datagrams and keeps serving.</summary>
    [Fact]
    public async Task ServerDropsForeignAndOversizedDatagrams()
    {
        await using var server = new ProbeServer(new IPEndPoint(IPAddress.Loopback, 0));
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await socket.SendAsync(new byte[ProbeDatagram.MaxDatagramBytes + 1], server.LocalEndPoint, Ct);
        await socket.SendAsync("hello"u8.ToArray(), server.LocalEndPoint, Ct);
        await socket.SendAsync(ProbeDatagram.Header(99, Guid.NewGuid(), 0), server.LocalEndPoint, Ct);
        var run = await ProbeClient.RunAsync(server.LocalEndPoint, 30, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(300), Ct);

        Assert.True(server.Dropped >= 3);
        Assert.All(run.Records, r => Assert.True(r.Echoed));
    }

    /// <summary>Edge case: an echo after the grace period counts as lost; nothing listening means every packet is lost up.</summary>
    [Fact]
    public async Task NoServerMeansEveryPacketIsLostAndTheReportIsIncomplete()
    {
        using var unused = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var silent = (IPEndPoint)unused.Client.LocalEndPoint!;

        var run = await ProbeClient.RunAsync(silent, 30, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100), Ct);

        Assert.False(run.ReportComplete);
        Assert.All(run.Records, r => Assert.False(r.UpReceived));
    }
}
