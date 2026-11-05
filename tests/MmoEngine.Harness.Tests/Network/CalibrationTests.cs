using MmoEngine.Harness.Network;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>US4 scenarios 2 to 6: calibration verdicts on synthetic sample sets (FR-025), one per profile and failure reason.</summary>
public sealed class CalibrationTests
{
    private const long Ms = 1_000_000;

    private static NetworkProfile Profile(string name) => name switch
    {
        "LAN" => new NetworkProfile { Name = "LAN", Version = 1, RoundTripMs = 1, JitterMs = 0, LossPercent = 0, Tolerances = new Tolerances { AbsoluteMs = 0.5 } },
        "Typical" => new NetworkProfile { Name = "Typical", Version = 1, RoundTripMs = 80, JitterMs = 10, LossPercent = 0.5 },
        "Cross-region" => new NetworkProfile { Name = "Cross-region", Version = 1, RoundTripMs = 160, JitterMs = 25, LossPercent = 1 },
        "Poor" => new NetworkProfile { Name = "Poor", Version = 1, RoundTripMs = 250, JitterMs = 50, LossPercent = 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>
    /// Synthetic samples: n packets at 4 Hz with the given per-direction loss. The echoed packets' round trips
    /// alternate target + jitter and target − jitter, so the median is the target and the population standard
    /// deviation is the jitter (an odd count gets one packet exactly on target). Each way's transit is half
    /// the round trip. <paramref name="reorderedDown"/> echoes arrive just after the next one.
    /// </summary>
    private static List<ProbeRecord> Samples(double rttMs, double jitterMs, double upLossPercent, double downLossPercent, int reorderedDown = 0, int n = 4000)
    {
        var upLost = (int)Math.Round(n * upLossPercent / 100);
        var downLost = (int)Math.Round((n - upLost) * downLossPercent / 100);
        // 4 Hz, so even Poor's jitter can't reorder consecutive packets (netem's in-order rate keeps order in real runs).
        var period = 250 * Ms;
        var echoedCount = n - upLost - downLost;
        var records = new List<ProbeRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var send = i * period;
            var up = i >= upLost;
            var ordinal = i - upLost - downLost;
            if (ordinal < 0)
            {
                records.Add(new ProbeRecord(i, send, up, null, null, null));
                continue;
            }

            var deviation = echoedCount % 2 == 1 && ordinal == echoedCount - 1 ? 0 : (ordinal % 2 == 0 ? jitterMs : -jitterMs);
            var rtt = (long)((rttMs + deviation) * Ms);
            records.Add(new ProbeRecord(i, send, true, send + (rtt / 2), send + (rtt / 2), send + rtt));
        }

        // Reorder on the way down: a high-round-trip echo (even ordinal) arrives just after the next packet's
        // echo. Its round trip only grows, so it stays above the median.
        for (var k = 0; k < reorderedDown; k++)
        {
            var a = upLost + downLost + ((echoedCount - 2 - (2 * k)) & ~1);
            records[a] = records[a] with { ClientRecvNs = records[a + 1].ClientRecvNs + Ms };
        }

        return records;
    }

    /// <summary>US4 scenario 2: samples within tolerance of each profile pass.</summary>
    [Theory]
    [InlineData("LAN", 1.2, 0.3, 0d)]
    [InlineData("Typical", 85d, 9.5, 0.5)]
    [InlineData("Cross-region", 150d, 26d, 1.1)]
    [InlineData("Poor", 270d, 54d, 2.8)]
    public void SamplesWithinToleranceOfTheProfilePass(string name, double rtt, double jitter, double loss)
    {
        var result = Calibration.Evaluate(Profile(name), Samples(rtt, jitter, loss, loss));

        Assert.True(result.Passed, string.Join("; ", result.Failures));
        Assert.Equal(rtt, result.MedianRoundTripMs, 3);
        Assert.Equal(jitter, result.JitterMs, 1);
        Assert.Equal(loss, result.UpLossPercent, 1);
        Assert.Equal(loss, result.DownLossPercent, 1);
    }

    /// <summary>US4 scenario 3: jitter 12% above target fails and names jitter.</summary>
    [Theory]
    [InlineData("Typical", 80d, 11.2, 0.5)]
    [InlineData("Cross-region", 160d, 28d, 1d)]
    [InlineData("Poor", 250d, 56d, 3d)]
    [InlineData("LAN", 1d, 0.6, 0d)]
    public void JitterBeyondToleranceFailsNamingJitter(string name, double rtt, double jitter, double loss)
    {
        var result = Calibration.Evaluate(Profile(name), Samples(rtt, jitter, loss, loss));

        Assert.False(result.Passed);
        Assert.True(result.Failures.Count == 1, string.Join("; ", result.Failures));
        Assert.StartsWith("jitter:", result.Failures[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Typical", 90d)]
    [InlineData("LAN", 1.6)]
    public void RoundTripBeyondToleranceFailsNamingRoundTrip(string name, double rtt)
    {
        var profile = Profile(name);
        var result = Calibration.Evaluate(profile, Samples(rtt, profile.JitterMs, profile.LossPercent, profile.LossPercent));

        Assert.False(result.Passed);
        Assert.StartsWith("round trip:", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    /// <summary>US4 scenario 4: 1% loss in one direction against Typical's 0.5% fails and names that direction.</summary>
    [Theory]
    [InlineData(1d, 0.5, "loss up")]
    [InlineData(0.5, 1d, "loss down")]
    public void LossBeyondToleranceInOneDirectionFailsNamingThatDirection(double upLoss, double downLoss, string reason)
    {
        var result = Calibration.Evaluate(Profile("Typical"), Samples(80, 10, upLoss, downLoss));

        Assert.False(result.Passed);
        Assert.StartsWith(reason + ":", Assert.Single(result.Failures), StringComparison.Ordinal);
    }

    /// <summary>
    /// The loss tolerance widens to three binomial standard deviations when the sample can't resolve the profile's:
    /// Poor's 3% over 9,000 packets gets ±0.539 points; Typical's 0.5% keeps its ±0.25.
    /// </summary>
    [Theory]
    [InlineData("Poor", 9000, 0.539)]
    [InlineData("Typical", 9000, 0.25)]
    [InlineData("LAN", 9000, 0.25)]
    [InlineData("Poor", 0, 0.25)]
    public void LossToleranceAccountsForTheSampleSize(string name, int packets, double expected) =>
        Assert.Equal(expected, Calibration.LossTolerancePoints(Profile(name), packets), 3);

    /// <summary>The CI run that measured 3.32% up loss on Poor over 9,000 packets is within sampling error, and 4% is not.</summary>
    [Theory]
    [InlineData(3.32, true)]
    [InlineData(4.0, false)]
    public void PoorLossIsJudgedAgainstItsSamplingError(double upLoss, bool passes)
    {
        var result = Calibration.Evaluate(Profile("Poor"), Samples(250, 50, upLoss, 3, n: 9000));

        Assert.Equal(passes, result.Passed);
        Assert.Equal(passes, !result.Failures.Any(f => f.StartsWith("loss up:", StringComparison.Ordinal)));
    }

    /// <summary>US4 scenario 5: any reordered packet fails a profile that allows none, and passes its reordering variant.</summary>
    [Fact]
    public void ReorderingFailsUnlessTheProfileAllowsIt()
    {
        var samples = Samples(80, 10, 0.5, 0.5, reorderedDown: 1);
        var typical = Profile("Typical");

        var strict = Calibration.Evaluate(typical, samples);
        var variant = Calibration.Evaluate(typical.ReorderingVariant(), samples);

        Assert.False(strict.Passed);
        Assert.Equal(1, strict.DownReordered);
        Assert.StartsWith("reordering:", Assert.Single(strict.Failures), StringComparison.Ordinal);
        Assert.True(variant.Passed, string.Join("; ", variant.Failures));
    }

    /// <summary>US4 scenario 6: RFC 3550 interarrival jitter is reported for each direction.</summary>
    [Fact]
    public void InterarrivalJitterIsReportedForEachDirection()
    {
        var result = Calibration.Evaluate(Profile("Typical"), Samples(80, 10, 0.5, 0.5));

        // Transit alternates ±10 ms around 40 ms each way, so |D| is 10 ms per packet and J converges to 10 ms.
        Assert.Equal(10, result.UpInterarrivalJitterMs, 1);
        Assert.Equal(10, result.DownInterarrivalJitterMs, 1);
    }

    [Fact]
    public void InterarrivalJitterFollowsTheRfcRecurrence()
    {
        // Transits 0, 16, 16 ms: D = 16 then 0, so J = 16/16 = 1, then 1 + (0 - 1)/16 = 0.9375.
        var j = Calibration.InterarrivalJitterMs([(0, 0), (0, 16 * Ms), (0, 16 * Ms)]);

        Assert.Equal(0.9375, j, 12);
    }

    [Fact]
    public void NoEchoesFailsWithoutAMeasurement()
    {
        var records = Enumerable.Range(0, 10).Select(i => new ProbeRecord(i, i * Ms, false, null, null, null)).ToList();

        var result = Calibration.Evaluate(Profile("Typical"), records);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("no echoed packets", StringComparison.Ordinal));
        Assert.Equal(100, result.UpLossPercent);
        Assert.True(double.IsNaN(result.MedianRoundTripMs));
    }

    [Theory]
    [InlineData(new long[] { 0, 1, 2, 3 }, 0)]
    [InlineData(new long[] { 0, 2, 1, 3 }, 1)]
    [InlineData(new long[] { 3, 0, 1, 2 }, 3)]
    public void ReorderedCountsLateArrivals(long[] order, int expected) => Assert.Equal(expected, Calibration.Reordered(order));

    [Fact]
    public void MedianOfAnEvenCountAveragesTheMiddlePair() => Assert.Equal(2.5, Calibration.Median([1, 2, 3, 4]));
}
