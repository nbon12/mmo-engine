# Contract: Network profiles, probe and stub-target datagrams

## Profile file (`harness/profiles/*.json`)

```json
{
  "name": "Typical",
  "version": 1,
  "roundTripMs": 80,
  "jitterMs": 10,
  "lossPercent": 0.5,
  "allowReordering": false,
  "tolerances": { "relativeFraction": 0.1, "absoluteMs": null, "lossPoints": 0.25 },
  "calibrated": {
    "perDirection": { "delayMs": 40, "jitterMs": 7.071, "lossPercent": 0.5 },
    "runnerClass": "<id>", "kernel": "<uname>", "measuredAt": "2026-11-04T00:00:00Z", "sourceRunId": "<probe run>"
  }
}
```

`calibrated` is optional. Unknown fields are refused. The catalogue is every file, plus each profile's
reordering variant, named `<name>-reorder`. A variant has the same targets and `allowReordering: true`,
and has no calibrated settings of its own.

The definitions (FR-021):

- **Round trip**: the median of client receive minus client send, both on the client's monotonic
  clock.
- **Jitter**: the population standard deviation of the round-trip times.
- **Loss**: per packet, in each direction.
  - Up loss is the share of sent packets that the server never received.
  - Down loss is the share of server-received packets whose echo never came back within the grace
    period.
- **Reordering**: an arrival whose sequence number is lower than one already received in that
  direction.

RFC 3550 interarrival jitter is reported for each direction.

**Per-direction settings**: if the profile has calibrated settings, those are used. Otherwise each
direction gets half the round trip as delay, jitter divided by √2, and the full loss figure.

**netem** (`harness profile netem`):

```text
tc qdisc replace dev <dev> root handle 1: netem delay <d>ms [<j>ms distribution normal] [loss random <l>%] rate 10gbit [reorder 25% 50%] [seed <seed>]
```

- `<j>` is a standard deviation, so the jitter uses netem's normal table.
- `rate 10gbit` is always present. With a rate, netem never schedules a delayed packet before the one queued
  ahead of it, so packets stay in order.
- `reorder 25% 50%` is present only for a reordering variant: it sends a share of packets at once, ahead of the
  delayed ones. A variant applies its base profile's per-direction settings, calibrated ones included.
- `seed <seed>` is present whenever jitter, reordering or loss is (each is random), and is dropped only on a `tc`
  that has no `seed` option, such as GitHub's ubuntu-latest runner.
- `--delay-ms`, `--jitter-ms` and `--loss-percent` replace the per-direction settings, as calibration's search
  does. `harness calibrate` takes the same options and stores those settings when the run passes.
- `harness profile list` reports each profile's `perDirection` settings (what it applies).

With `--tcp-ingress`, the netem qdisc goes on `ifb0`, and the device's ingress is redirected to it.

**Chaos Mesh** (`harness profile chaos`): a `NetworkChaos` with:

- `action: netem`, `mode: all`, `direction: to`;
- the namespace, and label selectors;
- `delay.latency`, `delay.jitter` and `loss.loss`;
- `delay.reorder` only for a reordering variant.

Chaos Mesh has no in-order rate or normal distribution, so its settings aren't calibrated yet.

## Probe datagrams (UDP, little-endian)

Every probe datagram starts with this header, and datagrams over 1,200 bytes are dropped:
`"MMOP"`, then version `1` (u8), then the type (u8), then the probe ID (16 bytes).

| Type | Body |
| --- | --- |
| 1 Probe | seq i64, client send ns i64 |
| 2 Echo | seq, client send ns, server receive ns, server send ns |
| 3 Report request | offset i64 |
| 4 Report response | offset i64, then an 8,192-bit receive bitmap starting at `offset` |

The client sends at the configured rate (30 Hz by default) for the configured duration (300 s by
default). It then waits a 1 s grace period, and fetches the bitmap chunk by chunk, with up to 20
retries per chunk.

## net_samples rows

One `up` row per packet. One `down` row per packet the server received. A `down` row whose echo was
lost has `lost = 1`, and no send or receive time.

## Stub-target datagrams (UDP)

Each is 34 bytes: `"MMOB"`, version `1`, the type (u8), then bot ID (u32), `a` (u64), `b` (u64) and
position (f64).

| Type | a | b | position |
| --- | --- | --- | --- |
| 1 Input (bot to target) | frame | frame | the bot's predicted position |
| 2 Input echo | frame | frame | as received |
| 3 Snapshot (30 Hz) | tick | last acknowledged frame | the server's position |
