using MmoEngine.Harness.Store;

namespace MmoEngine.Harness.Network;

/// <summary>
/// Maps probe records to <c>net_samples</c> rows and back (FR-024, data-model.md). Each packet gives an
/// "up" row: client send to server receive. A packet the server received also gives a "down" row: server
/// send to client receive. A down row with a lost echo has no send time.
/// </summary>
public static class NetSamples
{
    public static IReadOnlyList<string> Columns { get; } =
        ["run_id", "probe_id", "direction", "seq", "send_ns", "recv_ns", "lost", "duplicate", "reordered"];

    /// <summary>The rows for one probe run.</summary>
    public static IEnumerable<object?[]> ToRows(string runId, ProbeRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var probeId = run.ProbeId.ToString("N");
        var upReordered = ReorderedSet(run.Records.Where(r => r.ServerRecvNs is not null).OrderBy(r => r.ServerRecvNs).ThenBy(r => r.Seq).Select(r => r.Seq));
        var downReordered = ReorderedSet(run.Records.Where(r => r.Echoed).OrderBy(r => r.ClientRecvNs).ThenBy(r => r.Seq).Select(r => r.Seq));
        foreach (var r in run.Records)
        {
            yield return [runId, probeId, "up", (ulong)r.Seq, r.ClientSendNs, r.ServerRecvNs, Flag(!r.UpReceived), (byte)0, Flag(upReordered.Contains(r.Seq))];
            if (r.UpReceived)
            {
                yield return [runId, probeId, "down", (ulong)r.Seq, r.ServerSendNs, r.ClientRecvNs, Flag(!r.Echoed), (byte)0, Flag(downReordered.Contains(r.Seq))];
            }
        }
    }

    /// <summary>
    /// Rebuilds the probe records of a run from its rows. A row is (direction, seq, send_ns, recv_ns, lost),
    /// as <see cref="Query"/> returns them.
    /// </summary>
    public static IReadOnlyList<ProbeRecord> FromRows(IEnumerable<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var bySeq = new SortedDictionary<long, ProbeRecord>();
        foreach (var row in rows)
        {
            var direction = (string)row[0]!;
            var seq = Convert.ToInt64(row[1], System.Globalization.CultureInfo.InvariantCulture);
            long? send = row[2] is null ? null : Convert.ToInt64(row[2], System.Globalization.CultureInfo.InvariantCulture);
            long? recv = row[3] is null ? null : Convert.ToInt64(row[3], System.Globalization.CultureInfo.InvariantCulture);
            var lost = Convert.ToByte(row[4], System.Globalization.CultureInfo.InvariantCulture) == 1;
            var current = bySeq.GetValueOrDefault(seq, new ProbeRecord(seq, 0, false, null, null, null));
            bySeq[seq] = direction == "up"
                ? current with { ClientSendNs = send ?? 0, UpReceived = !lost, ServerRecvNs = recv }
                : current with { ServerSendNs = lost ? null : send, ClientRecvNs = lost ? null : recv };
        }

        return [.. bySeq.Values];
    }

    /// <summary>The query <see cref="FromRows"/> reads.</summary>
    public const string Query =
        "SELECT direction, seq, send_ns, recv_ns, lost FROM net_samples WHERE run_id = {run_id:String} ORDER BY seq, direction";

    private static HashSet<long> ReorderedSet(IEnumerable<long> arrivalOrder)
    {
        var set = new HashSet<long>();
        var highest = long.MinValue;
        foreach (var seq in arrivalOrder)
        {
            if (seq < highest)
            {
                set.Add(seq);
            }
            else
            {
                highest = seq;
            }
        }

        return set;
    }

    private static byte Flag(bool value) => (byte)(value ? 1 : 0);
}
