using System.Globalization;
using System.Text;

namespace MmoEngine.Harness.Network;

/// <summary>
/// Generates <c>tc</c> commands that apply a profile with netem on one machine (FR-022, research R7, the
/// tc-netem manual). netem delays packets as they leave an interface, so each command impairs one
/// direction's egress with the profile's per-direction settings. The generator writes a script; it never
/// runs <c>tc</c> itself.
/// </summary>
public static class NetemScript
{
    /// <summary>
    /// The rate every profile gets. With a rate set, netem schedules each delayed packet no earlier than the one
    /// queued before it, so jitter can't reorder packets (a variant's netem reorder still sends a share ahead). A pfifo child, the manual's older
    /// recipe, no longer does this: current kernels hand packets to the child only once they are due, already
    /// reordered (measured on GitHub's ubuntu-latest, spec 014 research R7). The rate is high enough that
    /// serialization time is negligible.
    /// </summary>
    public const string InOrderRate = "10gbit";

    /// <summary>
    /// Commands impairing egress on <paramref name="device"/>, one direction's settings. Give each direction its
    /// own device (calibrate.sh uses the two ends of a veth pair): on a device that carries both directions,
    /// such as loopback, keeping packets in order couples them. With <paramref name="tcpIngress"/>, the
    /// impairment sits on the receiver's ingress through an IFB device instead, as the manual requires for
    /// realistic TCP results.
    /// </summary>
    public static IReadOnlyList<string> Commands(NetworkProfile profile, string device, ulong seed, bool tcpIngress = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        var target = tcpIngress ? "ifb0" : device;
        var lines = new List<string>();
        if (tcpIngress)
        {
            lines.Add("modprobe ifb numifbs=1");
            lines.Add("ip link set dev ifb0 up");
            lines.Add($"tc qdisc replace dev {device} ingress");
            lines.Add($"tc filter replace dev {device} parent ffff: protocol all u32 match u32 0 0 flowid 1:1 action mirred egress redirect dev ifb0");
        }

        lines.Add($"tc qdisc replace dev {target} root handle 1: {NetemArguments(profile, seed)}");
        return lines;
    }

    /// <summary>The <c>netem ...</c> arguments for one direction.</summary>
    public static string NetemArguments(NetworkProfile profile, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var d = profile.PerDirection();
        var text = new StringBuilder("netem");
        text.Append(CultureInfo.InvariantCulture, $" delay {Ms(d.DelayMs)}");
        if (d.JitterMs > 0)
        {
            // netem's default jitter is uniform on ±jitter, whose standard deviation is jitter/√3. The profile's
            // jitter is a standard deviation (FR-021), so the normal table, whose parameter is sigma, is used.
            text.Append(CultureInfo.InvariantCulture, $" {Ms(d.JitterMs)} distribution normal");
        }

        if (d.LossPercent > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" loss random {Number(d.LossPercent)}%");
        }

        // The in-order rate keeps every delayed packet behind the one before it, for a variant too, so its delayed
        // packets queue exactly as its base profile's do and the base's calibrated settings apply to it.
        text.Append(" rate ").Append(InOrderRate);
        if (profile.AllowReordering)
        {
            // Jitter alone didn't reorder packets in the calibration runs, so a variant uses netem's reorder: it
            // puts a share of packets at the head of the queue, sent at once, ahead of the delayed ones. That
            // changes the delay distribution, so a variant's jitter isn't checked against its base's (FR-025).
            text.Append(CultureInfo.InvariantCulture,
                $" reorder {Number(NetworkProfile.ReorderPercent)}% {Number(NetworkProfile.ReorderCorrelationPercent)}%");
        }

        // netem's seeded PRNG drives jitter, reorder and loss (Linux 6.3+), so the seed is set whenever any of
        // them is random. A fixed delay has nothing to repeat.
        if (d.JitterMs > 0 || d.LossPercent > 0 || profile.AllowReordering)
        {
            text.Append(CultureInfo.InvariantCulture, $" seed {seed}");
        }

        return text.ToString();
    }

    /// <summary>The commands as a bash script, with a header naming the profile and seed.</summary>
    public static string Script(NetworkProfile profile, string device, ulong seed, bool tcpIngress = false)
    {
        var body = string.Join('\n', Commands(profile, device, seed, tcpIngress));
        return $"#!/usr/bin/env bash\n# Network profile {profile.Name} v{profile.Version.ToString(CultureInfo.InvariantCulture)}, seed {seed.ToString(CultureInfo.InvariantCulture)} (generated by harness profile netem).\nset -euo pipefail\n{body}\n";
    }

    internal static string Ms(double value) => Number(value) + "ms";

    internal static string Number(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Generates a Chaos Mesh NetworkChaos resource for a profile (FR-023). Chaos Mesh applies netem on each
/// selected pod's egress, so the same per-direction settings apply. Selecting both probe pods impairs both
/// directions.
/// </summary>
public static class NetworkChaosManifest
{
    public static string Yaml(NetworkProfile profile, string @namespace, IReadOnlyDictionary<string, string> podLabels)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentNullException.ThrowIfNull(podLabels);
        if (podLabels.Count == 0)
        {
            throw new ArgumentException("At least one pod label is required to select the target pods.", nameof(podLabels));
        }

        var d = profile.PerDirection();
        var yaml = new StringBuilder();
        yaml.Append("apiVersion: chaos-mesh.org/v1alpha1\n");
        yaml.Append("kind: NetworkChaos\n");
        yaml.Append("metadata:\n");
        yaml.Append(CultureInfo.InvariantCulture, $"  name: profile-{profile.Name.ToLowerInvariant()}\n");
        yaml.Append(CultureInfo.InvariantCulture, $"  namespace: {@namespace}\n");
        yaml.Append("spec:\n");
        yaml.Append("  action: netem\n");
        yaml.Append("  mode: all\n");
        yaml.Append("  direction: to\n");
        yaml.Append("  selector:\n");
        yaml.Append("    namespaces:\n");
        yaml.Append(CultureInfo.InvariantCulture, $"      - {@namespace}\n");
        yaml.Append("    labelSelectors:\n");
        foreach (var (key, value) in podLabels.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yaml.Append(CultureInfo.InvariantCulture, $"      {key}: \"{value}\"\n");
        }

        yaml.Append("  delay:\n");
        yaml.Append(CultureInfo.InvariantCulture, $"    latency: \"{NetemScript.Ms(d.DelayMs)}\"\n");
        yaml.Append(CultureInfo.InvariantCulture, $"    jitter: \"{NetemScript.Ms(d.JitterMs)}\"\n");
        yaml.Append("    correlation: \"0\"\n");
        if (profile.AllowReordering)
        {
            yaml.Append("    reorder:\n");
            yaml.Append(CultureInfo.InvariantCulture, $"      reorder: \"{NetemScript.Number(NetworkProfile.ReorderPercent)}\"\n");
            yaml.Append(CultureInfo.InvariantCulture, $"      correlation: \"{NetemScript.Number(NetworkProfile.ReorderCorrelationPercent)}\"\n");
            yaml.Append("      gap: 0\n");
        }

        yaml.Append("  loss:\n");
        yaml.Append(CultureInfo.InvariantCulture, $"    loss: \"{NetemScript.Number(d.LossPercent)}\"\n");
        yaml.Append("    correlation: \"0\"\n");
        return yaml.ToString();
    }
}
