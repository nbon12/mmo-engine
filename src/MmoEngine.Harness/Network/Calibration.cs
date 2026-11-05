using System.Globalization;

namespace MmoEngine.Harness.Network;

/// <summary>
/// One probe packet's life (research R7). The client stamps the send time, the server stamps its receive
/// time and its echo's send time, and the client stamps the echo's receive time. Server times are on the
/// server's clock, and client times on the client's clock.
/// </summary>
/// <param name="UpReceived">The server received the packet (from its receive bitmap or the echo).</param>
/// <param name="ServerRecvNs">The server's receive time, known only when the echo came back.</param>
public readonly record struct ProbeRecord(long Seq, long ClientSendNs, bool UpReceived, long? ServerRecvNs, long? ServerSendNs, long? ClientRecvNs)
{
    /// <summary>The client received the echo.</summary>
    public bool Echoed => ClientRecvNs is not null && ServerSendNs is not null;
}

/// <summary>What a calibration measured, and its verdict.</summary>
public sealed record CalibrationResult
{
    public required string Profile { get; init; }
    public required bool Passed { get; init; }
    public required IReadOnlyList<string> Failures { get; init; }
    public required int Sent { get; init; }
    public required int Echoed { get; init; }
    public required double MedianRoundTripMs { get; init; }
    public required double JitterMs { get; init; }
    public required double UpLossPercent { get; init; }
    public required double DownLossPercent { get; init; }
    public required int UpReordered { get; init; }
    public required int DownReordered { get; init; }
    public required double UpInterarrivalJitterMs { get; init; }
    public required double DownInterarrivalJitterMs { get; init; }
}

/// <summary>
/// Decides whether measured samples match a network profile within tolerance (FR-025). Round trip and
/// jitter use only the client's clock; RFC 3550 interarrival jitter uses differences of transit time, in
/// which the offset between the two clocks cancels.
/// </summary>
public static class Calibration
{
    public static CalibrationResult Evaluate(NetworkProfile profile, IReadOnlyList<ProbeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(records);
        var sent = records.Count;
        var upReceived = records.Where(r => r.UpReceived).ToList();
        var echoed = records.Where(r => r.Echoed).ToList();
        var rtts = echoed.Select(r => (r.ClientRecvNs!.Value - r.ClientSendNs) / 1e6).Order().ToList();
        var median = Median(rtts);
        var jitter = StandardDeviation(rtts);
        var upLoss = sent == 0 ? 0 : 100.0 * (sent - upReceived.Count) / sent;
        var downLoss = upReceived.Count == 0 ? 0 : 100.0 * (upReceived.Count - echoed.Count) / upReceived.Count;

        var upOrder = upReceived.Where(r => r.ServerRecvNs is not null).OrderBy(r => r.ServerRecvNs).ThenBy(r => r.Seq).ToList();
        var downOrder = echoed.OrderBy(r => r.ClientRecvNs).ThenBy(r => r.Seq).ToList();
        var upReordered = Reordered(upOrder.Select(r => r.Seq));
        var downReordered = Reordered(downOrder.Select(r => r.Seq));
        var upJitter = InterarrivalJitterMs(upOrder.Select(r => (r.ClientSendNs, r.ServerRecvNs!.Value)));
        var downJitter = InterarrivalJitterMs(downOrder.Select(r => (r.ServerSendNs!.Value, r.ClientRecvNs!.Value)));

        var failures = new List<string>();
        if (echoed.Count == 0)
        {
            failures.Add("no echoed packets: round trip and jitter can't be measured");
        }
        else
        {
            CheckTiming("round trip", median, profile.RoundTripMs, profile.Tolerances, failures);
            CheckTiming("jitter", jitter, profile.JitterMs, profile.Tolerances, failures);
        }

        CheckLoss("loss up", upLoss, sent, profile, failures);
        CheckLoss("loss down", downLoss, upReceived.Count, profile, failures);
        if (!profile.AllowReordering && (upReordered > 0 || downReordered > 0))
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"reordering: {upReordered} up and {downReordered} down, but the profile allows none"));
        }

        return new CalibrationResult
        {
            Profile = profile.Name,
            Passed = failures.Count == 0,
            Failures = failures,
            Sent = sent,
            Echoed = echoed.Count,
            MedianRoundTripMs = median,
            JitterMs = jitter,
            UpLossPercent = upLoss,
            DownLossPercent = downLoss,
            UpReordered = upReordered,
            DownReordered = downReordered,
            UpInterarrivalJitterMs = upJitter,
            DownInterarrivalJitterMs = downJitter,
        };
    }

    /// <summary>Packets that arrived after a higher sequence number had already arrived.</summary>
    public static int Reordered(IEnumerable<long> arrivalOrder)
    {
        ArgumentNullException.ThrowIfNull(arrivalOrder);
        var highest = long.MinValue;
        var count = 0;
        foreach (var seq in arrivalOrder)
        {
            if (seq < highest)
            {
                count++;
            }
            else
            {
                highest = seq;
            }
        }

        return count;
    }

    /// <summary>
    /// RFC 3550 interarrival jitter (§6.4.1, Appendix A.8), in ms: for packets in arrival order,
    /// D = transit_i − transit_{i−1} and J += (|D| − J) / 16.
    /// </summary>
    public static double InterarrivalJitterMs(IEnumerable<(long SendNs, long RecvNs)> arrivals)
    {
        ArgumentNullException.ThrowIfNull(arrivals);
        double j = 0;
        long? previousTransit = null;
        foreach (var (send, recv) in arrivals)
        {
            var transit = recv - send;
            if (previousTransit is { } p)
            {
                var d = Math.Abs(transit - p) / 1e6;
                j += (d - j) / 16;
            }

            previousTransit = transit;
        }

        return j;
    }

    internal static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    internal static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    private static void CheckTiming(string name, double measured, double target, Tolerances tolerances, List<string> failures)
    {
        var allowed = tolerances.AbsoluteMs ?? tolerances.RelativeFraction * target;
        if (Math.Abs(measured - target) > allowed + 1e-9)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: measured {measured:0.###} ms, target {target:0.###} ms ± {allowed:0.###} ms"));
        }
    }

    /// <summary>
    /// The loss tolerance in percentage points for a sample of <paramref name="packets"/>: the profile's
    /// tolerance, widened to three binomial standard deviations when the sample can't resolve it. At 3% loss
    /// over 9,000 packets one standard deviation is 0.18 points, so a fixed ±0.25 would fail a correct link
    /// about a third of the time (spec 014 research R7).
    /// </summary>
    public static double LossTolerancePoints(NetworkProfile profile, int packets)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var p = profile.LossPercent / 100;
        var sampling = packets > 0 ? 3 * 100 * Math.Sqrt(p * (1 - p) / packets) : 0;
        return Math.Max(profile.Tolerances.LossPoints, sampling);
    }

    private static void CheckLoss(string name, double measured, int packets, NetworkProfile profile, List<string> failures)
    {
        var tolerance = LossTolerancePoints(profile, packets);
        if (Math.Abs(measured - profile.LossPercent) > tolerance + 1e-9)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: measured {measured:0.###}%, target {profile.LossPercent:0.###}% ± {tolerance:0.###} points"));
        }
    }
}
