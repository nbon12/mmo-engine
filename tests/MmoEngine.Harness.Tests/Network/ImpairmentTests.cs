using MmoEngine.Harness.Network;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>US4 scenarios 7 and 8: the netem and NetworkChaos generators (FR-022, FR-023).</summary>
public sealed class ImpairmentTests
{
    private static readonly NetworkProfile Typical = new() { Name = "Typical", Version = 1, RoundTripMs = 80, JitterMs = 10, LossPercent = 0.5 };

    /// <summary>US4 scenario 7: per-direction delay, jitter and loss, a loss seed, and a FIFO child unless reordering is allowed.</summary>
    [Fact]
    public void NetemSplitsPerDirectionSeedsLossAndAddsAFifoChild()
    {
        var commands = NetemScript.Commands(Typical, "eth0", seed: 42);

        Assert.Equal(2, commands.Count);
        Assert.Equal("tc qdisc replace dev eth0 root handle 1: netem delay 40ms 7.071ms loss random 0.5% seed 42", commands[0]);
        Assert.Equal("tc qdisc replace dev eth0 parent 1:1 handle 10: pfifo limit 1000", commands[1]);
    }

    [Fact]
    public void NetemReorderingVariantAddsReorderAndNoFifoChild()
    {
        var commands = NetemScript.Commands(Typical.ReorderingVariant(), "eth0", seed: 7);

        Assert.Single(commands);
        Assert.Contains("reorder 25% 50%", commands[0], StringComparison.Ordinal);
        Assert.DoesNotContain(commands, c => c.Contains("pfifo", StringComparison.Ordinal));
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
        Assert.Equal("netem delay 0.5ms seed 3", NetemScript.NetemArguments(lan, 3));

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
        Assert.Equal("netem delay 38ms 6ms loss random 0.4% seed 3", NetemScript.NetemArguments(calibrated, 3));
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
