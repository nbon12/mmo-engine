# Research: Telemetry and Test Harness

Each decision records what was chosen, why, and what else was considered. External sources are listed
at the end (Principle VII).

## R1. ClickHouse client for .NET

**Decision**: `ClickHouse.Driver` 1.5.0, ClickHouse's official C# client
(github.com/ClickHouse/clickhouse-cs), over HTTP. Its NuGet license expression is **MIT** (checked on
nuget.org on 2026-11-04). Bulk inserts go through its bulk-copy API in batches.

**Why**: The issue recommends the official HTTP client "after confirming its license", and MIT
satisfies Principle VII. HTTP is the client's stable transport.

**Alternatives**: Raw HTTP through `HttpClient` with `FORMAT TSV`. No dependency, but we'd write and
test our own encoding. Kept as the fallback if the driver blocks something. The native TCP client is
experimental (the issue), so it's considered only if insert throughput limits.

## R2. Rule files are YAML

**Decision**: YAML, in the shape the issue shows, parsed with YamlDotNet (MIT) into a typed model and
validated with FluentValidation.

**Why**: It matches the issue's example, and a multi-line SQL block is readable in YAML.

**Alternatives**: JSON is already used for configs, but multi-line SQL in JSON strings is hard to read
and review.

## R3. Idempotent loads in ClickHouse

**Decision**: The `runs` table is a `ReplacingMergeTree(version)`, and readers use `FINAL`. A load
works like this:

1. If a `runs` row with `loaded = 1` exists for the run ID, the load is a no-op.
2. Otherwise, any partial rows for the run ID are deleted with a lightweight `DELETE FROM ... WHERE
   run_id = ?`, which is synchronous for the issuing query.
3. The ticks and events are inserted in batches.
4. The `runs` row is inserted last, with `loaded = 1`.

The `runs` row works as the commit marker, so an interrupted load is redone in full next time.

**Why**: ClickHouse has no multi-statement transactions. A commit-marker row plus delete-before-insert
gives exactly one copy without them.

**Alternatives**:
- `ReplacingMergeTree` on the raw tables: it dedupes only at merge time, so counts would be wrong
  until the merge happened.
- Insert deduplication tokens: they only cover retried identical blocks.

## R4. Retention

**Decision**: Raw tables have `TTL loaded_at + INTERVAL 14 DAY`. The `*_retained` tables have the
same columns and no TTL. The rule runner copies a failing run's rows into them with `INSERT INTO
x_retained SELECT * FROM x WHERE run_id = ...`. Golden runs are marked retained by the golden check.
`run_aggregates` holds per-run percentiles and has no TTL. The 14 days are a constant in the DDL. The
issue proposes that number, and changing it is a DDL change.

## R5. Runner class

**Decision**: A runner class is a fingerprint built from:

- the processor architecture;
- the CPU model (from `/proc/cpuinfo` on Linux, `sysctl machdep.cpu.brand_string` on macOS, or
  "unknown");
- the .NET major version;
- each pinned `DOTNET_Enable*` and `DOTNET_PreferredVectorBitWidth` variable;
- the supported-ISA flags the simulator's manifest already records.

Its ID is the first 16 hex digits of the XxHash64 of the canonical JSON of those fields, prefixed with
the name in `MMO_RUNNER_CLASS` when that is set. The simulator's manifest gains `runtime.cpuModel`
and `runtime.runnerClass`. Both are additive fields, so the format version stays 1.

The OS description (kernel version) is left out of the fingerprint, because GitHub-hosted runner
images update their kernel often, and integer section maths doesn't depend on it.

**Why**: The issue fixes "one runner class, a single CPU model". Making the class a fingerprint means
a change of hardware is detected, never silently compared.

## R6. Golden runs compare run folders

**Decision**: A golden run is a folder in `harness/golden/<name>/` with:

- `config.json`, the run config;
- `golden.json`, holding the format version, name, runner class, tick count, config hash and source
  git SHA;
- `ticks.csv`, the deterministic columns of the recorded run, with the wall-clock columns
  `bookkeepingMicros` and `serializedMicros` left out.

CI runs `regionizer-sim run config.json`, then `harness golden check`, which compares the new folder's
`ticks.csv` to the golden one row by row. It reports the first row whose `stateHash` differs, with
both rows.

**Why**: The harness stays independent of simulator internals. The comparison covers exactly what
the run-output contract publishes, and the stored metrics rows make the "state diff" readable without
re-running anything.

## R7. Network profile definitions and calibration maths

**Decision**:

- **Round trip**: the median of `client_receive − client_send` over the echoed packets. Both
  timestamps come from the client's monotonic clock, so clock offset between hosts doesn't matter.
- **Jitter**: the population standard deviation of the round-trip times.
- **Loss**: per direction.
  - Up loss: the sequence numbers the server never saw.
  - Down loss: the sequence numbers the server saw but whose echo never came back.
  - The server writes its own receive log, so the two directions can be told apart.
- **Reordering**: a packet is reordered if it arrives with a sequence number lower than one already
  received in that direction.
- **RFC 3550 interarrival jitter**: computed per direction with J += (|D| − J) / 16, where D is the
  difference in transit time between consecutive packets.
  - Down direction: from the client's own receive times against the server's echo send times. The
    offset between the clocks cancels in D.
  - Up direction: from the server's receive times against the client's send times.
- **Tolerances**: median and jitter within 10% of target, with LAN as an absolute 0.5 ms; loss within
  0.25 percentage points per direction; reordering zero unless the variant allows it. These come from
  the issue, and live in the profile file.

**netem**: netem impairs egress, so a round trip of R splits into R/2 of delay per direction, and
jitter J splits into J/√2 per direction, so the round-trip standard deviation comes out at J. Loss per
direction equals the profile's per-direction loss. Without reordering, the `delay` qdisc gets a
`pfifo` child (`tc qdisc add ... parent 1:1 handle 10: pfifo`), as the tc-netem manual shows.
Reordering variants use the manual's `reorder` with a 25% probability and a 50% correlation. The
`seed` option fixes loss sequences. The generator writes a script, and doesn't run `tc` itself. The
calibration step stores the per-direction settings that measured within tolerance, which may differ
from the arithmetic above.

**NetworkChaos**: `action: netem`, with `delay.latency`, `delay.jitter`, `loss.loss` and, for a
reordering variant, `delay.reorder`; `direction: both` and a label selector; `mode: all`. Chaos Mesh
applies the delay on each selected pod's egress, so the same per-direction split applies.

## R8. Bot frame loop

**Decision**: Each bot host runs one frame loop on a dedicated thread with a `Stopwatch` deadline
schedule. Frame *n* is due at *t*₀ + *n*/60 s. A frame that starts more than one frame period late is
a deadline miss, and the loop doesn't try to catch up. Missed frames are counted, not stretched.
Rows go to an in-memory channel. A writer task flushes them in batches of 5,000 rows, or every
second, plus once at shutdown. The writer targets an `IFrameSink`: ClickHouse in real runs, memory in
tests.

**Why**: Deadline misses must show up as data (the issue: "Rows from a host that misses deadlines are
invalid"). A catch-up loop would hide overload.

## R9. Local cluster

**Decision**: kind is the default and k3d the alternative (`HARNESS_CLUSTER=k3d`). Both run in
Docker. The core is a kustomize base with:

- ClickHouse as a single-replica StatefulSet with `emptyDir`;
- the OpenTelemetry Collector contrib as a Deployment;
- Chaos Mesh, installed from its official Helm chart with a pinned version.

An `observability` overlay adds Prometheus and Grafana. `up.sh` is idempotent:

- it reuses an existing cluster;
- it applies the manifests with `kubectl apply -k`;
- it waits for rollout;
- it port-forwards ClickHouse to `localhost:18123`;
- it runs `harness schema apply`.

CI runs `up.sh` on `ubuntu-latest`. kind and kubectl are installed at pinned versions.

**Why**: The issue moves the local Kubernetes cluster into this sub-spec, because Chaos Mesh needs
Kubernetes. kind is the most common CI choice.

**As built (slice 3)**:

- **Versions**: Chaos Mesh chart 2.8.4, the collector contrib 0.161.0 (the version the issue cites), Prometheus
  v3.5.0 and Grafana 12.1.0.
- **Credentials**: the ClickHouse secret is created by `up.sh` with a random password, so nothing secret is
  committed.
- **Collector tables**: the collector's ClickHouse tables were generated by running exporter v0.161.0 with
  `create_schema: true` against a scratch server, and committed as DDL with the 14-day TTL added. Re-generate
  them when the collector version changes.
- **Calibration**: it runs on the CI runner's loopback with `tc netem`, where one qdisc on `lo` impairs both
  directions with the per-direction settings. The runner may need `linux-modules-extra` for `sch_netem`.
- **Golden runs**: GitHub's hosted runners are not one CPU model, so a golden run recorded on one class can be
  "not comparable" on the next run. The `golden` job then records a candidate for the new class instead of
  passing silently. A self-hosted runner of one fixed CPU model would remove this, and only changes
  `MMO_RUNNER_CLASS`.

## R10. Replay viewer

**Decision**: `regionizer-sim view <run-folder> --tick N --out tick.svg`. It reloads the run's config
from its manifest and re-runs the simulation to tick N with the same seed. Then it writes an SVG:
sections are filled by region (a stable colour from the region ID), portals are drawn as lines
(open green, closed red), and entities are drawn as dots, with their claim boxes outlined. The tests
check the SVG's elements against the regionizer state at that tick, not pixels.

**Why**: Runs are deterministic, so re-running is cheaper than recording every tick's full state, and
SVG needs no dependency.

## Sources

- ClickHouse C# client docs: https://clickhouse.com/docs/integrations/language-clients/csharp/overview
- ClickHouse.Driver on NuGet (license MIT): https://www.nuget.org/packages/ClickHouse.Driver
- OpenTelemetry Collector ClickHouse exporter:
  https://pkg.go.dev/github.com/open-telemetry/opentelemetry-collector-contrib/exporter/clickhouseexporter
- tc-netem(8): https://man7.org/linux/man-pages/man8/tc-netem.8.html
- Chaos Mesh, Simulate Network Faults: https://chaos-mesh.org/docs/simulate-network-chaos-on-kubernetes/
- RFC 3550 §6.4.1 and Appendix A.8 (interarrival jitter): https://www.rfc-editor.org/rfc/rfc3550
- .NET vectorization guidelines:
  https://github.com/dotnet/runtime/blob/8323e58d7b587deceb7ef39e61725bce7bfe86f8/docs/coding-guidelines/vectorization-guidelines.md
- kind: https://kind.sigs.k8s.io/ ; k3d: https://k3d.io/
