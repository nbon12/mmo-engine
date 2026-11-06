using MmoEngine.Harness.Network;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>US4 scenarios 7 and 8: the netem and NetworkChaos generators (FR-022, FR-023).</summary>
public sealed class ImpairmentTests
{
    private static readonly NetworkProfile Typical = new() { Name = "Typical", Version = 1, RoundTripMs = 80, JitterMs = 10, LossPercent = 0.5 };

    /// <summary>
    /// US4 scenario 7: per-direction delay, normally distributed jitter (its parameter is the standard deviation),
    /// loss with a seed, and an in-order rate because the profile doesn't allow reordering.
    /// </summary>
    [Fact]
    public void NetemSplitsPerDirectionSeedsLossAndKeepsPacketsInOrder()
    {
        var commands = NetemScript.Commands(Typical, "eth0", seed: 42);

        Assert.Equal(
            ["tc qdisc replace dev eth0 root handle 1: netem delay 40ms 7.071ms distribution normal loss random 0.5% rate 10gbit seed 42"],
            commands);
    }

    /// <summary>netem's seeded PRNG drives jitter, reorder and loss, so any of them gets the seed; a fixed delay doesn't.</summary>
    [Theory]
    [InlineData(1d, 0d, 0d, false, false)]
    [InlineData(1d, 2d, 0d, false, true)]
    [InlineData(1d, 0d, 1d, false, true)]
    [InlineData(1d, 0d, 0d, true, true)]
    [InlineData(1d, 2d, 0d, true, true)]
    public void SeedIsSetWheneverJitterReorderOrLossIsRandom(double rtt, double jitter, double loss, bool reorder, bool seeded)
    {
        var profile = new NetworkProfile { Name = "P", Version = 1, RoundTripMs = rtt, JitterMs = jitter, LossPercent = loss, AllowReordering = reorder };

        Assert.Equal(seeded, NetemScript.NetemArguments(profile, 9).EndsWith(" seed 9", StringComparison.Ordinal));
    }

    /// <summary>
    /// US4 scenario 7: a reordering variant keeps its base's settings and in-order rate, so its delayed packets queue
    /// as the base's do, and adds netem's reorder, which sends a share of packets at once, ahead of them.
    /// </summary>
    [Fact]
    public void NetemReorderingVariantKeepsTheBaseSettingsAndAddsReorder()
    {
        var calibrated = Typical.WithPerDirection(new DirectionSettings(39, 8, 0.5));

        Assert.Equal(
            ["tc qdisc replace dev eth0 root handle 1: netem delay 40ms 7.071ms distribution normal loss random 0.5% rate 10gbit reorder 25% 50% seed 7"],
            NetemScript.Commands(Typical.ReorderingVariant(), "eth0", seed: 7));
        Assert.Equal(
            "netem delay 39ms 8ms distribution normal loss random 0.5% rate 10gbit reorder 25% 50% seed 7",
            NetemScript.NetemArguments(calibrated.ReorderingVariant(), 7));
    }

    [Fact]
    public void WithPerDirectionReplacesTheSettingsAndKeepsTheTargets()
    {
        var tried = Typical.WithPerDirection(new DirectionSettings(37.5, 9.25, 0.5));

        Assert.Equal("netem delay 37.5ms 9.25ms distribution normal loss random 0.5% rate 10gbit seed 1", NetemScript.NetemArguments(tried, 1));
        Assert.Equal((80d, 10d, 0.5), (tried.RoundTripMs, tried.JitterMs, tried.LossPercent));
    }

    /// <summary>FR-022: for TCP, the impairment sits on the receiver's ingress through an IFB device.</summary>
    [Fact]
    public void TcpIngressRedirectsThroughIfb()
    {
        var commands = NetemScript.Commands(Typical, "eth0", seed: 1, tcpIngress: true);

        Assert.Contains("tc qdisc replace dev eth0 ingress", commands);
        Assert.Contains(commands, c => c.Contains("mirred egress redirect dev ifb0", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.StartsWith("tc qdisc replace dev ifb0 root handle 1: netem", StringComparison.Ordinal));
    }

    [Fact]
    public void LanHasNoJitterOrLossArgumentsAndCalibratedSettingsWin()
    {
        var lan = new NetworkProfile { Name = "LAN", Version = 1, RoundTripMs = 1, JitterMs = 0, LossPercent = 0 };
        Assert.Equal("netem delay 0.5ms rate 10gbit", NetemScript.NetemArguments(lan, 3));

        var calibrated = Typical with
        {
            Calibrated = new CalibratedSettings
            {
                PerDirection = new DirectionSettings(38, 6, 0.4),
                RunnerClass = "c",
                Kernel = "k",
                MeasuredAt = DateTime.UnixEpoch,
            },
        };
        Assert.Equal("netem delay 38ms 6ms distribution normal loss random 0.4% rate 10gbit seed 3", NetemScript.NetemArguments(calibrated, 3));
    }

    [Fact]
    public void ScriptNamesTheProfileAndSeed()
    {
        var script = NetemScript.Script(Typical, "lo", 9);

        Assert.StartsWith("#!/usr/bin/env bash\n# Network profile Typical v1, seed 9", script, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", script, StringComparison.Ordinal);
    }

    /// <summary>US4 scenario 8: delay, jitter, loss, direction and target pods; reordering only in the variant.</summary>
    [Fact]
    public void NetworkChaosSetsDelayJitterLossDirectionAndTargetPods()
    {
        var yaml = NetworkChaosManifest.Yaml(Typical, "harness", new Dictionary<string, string> { ["app.kubernetes.io/part-of"] = "harness-probe" });

        Assert.Contains("kind: NetworkChaos", yaml, StringComparison.Ordinal);
        Assert.Contains("  action: netem\n", yaml, StringComparison.Ordinal);
        Assert.Contains("  direction: to\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    latency: \"40ms\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    jitter: \"7.071ms\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    loss: \"0.5\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      app.kubernetes.io/part-of: \"harness-probe\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      - harness\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reorder", yaml, StringComparison.Ordinal);

        var variant = NetworkChaosManifest.Yaml(Typical.ReorderingVariant(), "harness", new Dictionary<string, string> { ["a"] = "b" });
        Assert.Contains("    reorder:\n      reorder: \"25\"\n      correlation: \"50\"\n", variant, StringComparison.Ordinal);
        Assert.Contains("name: profile-typical-reorder", variant, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkChaosNeedsATargetSelector() =>
        Assert.Throws<ArgumentException>(() => NetworkChaosManifest.Yaml(Typical, "harness", new Dictionary<string, string>()));
}
