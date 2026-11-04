# Feature Specification: Telemetry and Test Harness

**Feature Branch**: `014-telemetry-test-harness`

**Created**: 2026-11-04

**Status**: Draft

**Input**: User description: "Pre-Phase 1 sub-spec 2: Telemetry and test harness (nbon12/mmo-engine#13,
section 'Sub-spec 2: Telemetry and test harness'). Turns every run into rows in ClickHouse and every
test into a SQL query with a threshold, and comes up the same on a laptop and in CI with one command."

## Clarifications

### Session 2026-11-04

Answered by the agent with the recommended option. The run was unattended (Principle XIII), started
by the Done story sweep.

- Q: Which of the issue's open questions does this spec follow? → A: All of the issue's recommendations
  for its sub-spec 2 rows:
  - the official ClickHouse .NET HTTP client with batched inserts, after confirming its license;
  - 14 days of raw rows, with failing and golden runs kept and per-run aggregates kept indefinitely;
  - native .NET bots, with the browser clients deferred to Phase 1;
  - the network-profile definitions and reordering variants from the issue's Proposed changes table;
  - the local Kubernetes cluster brought forward into this sub-spec.
- Q: What is the "fixed runner class" before CI has a dedicated machine? → A: A named runner class
  recorded in every manifest: an OS, an architecture, a CPU model string and the pinned
  hardware-intrinsic settings. Golden-run hashes are compared only between runs of the same class. A
  replay on a different class reports "not comparable" instead of a pass or fail. The CI class is
  GitHub's `ubuntu-latest` x64 runner, with the intrinsic settings pinned by environment variables.
  It is identified by its CPU model string as recorded, so a change of hardware shows up as a new
  class rather than a false divergence.
- Q: Which tests need a real cluster or real ClickHouse? → A: Store, loader and rule-runner tests use a
  real ClickHouse through Testcontainers, the same way the constitution requires a real PostgreSQL for
  persistence tests. Rules, profiles, calibration maths, golden-run comparison, the bot host, the stub
  target and the probe pair are also tested in process without any infrastructure. One CI job brings
  the cluster up with the one command and checks that it is healthy.

## User Scenarios & Testing *(mandatory)*

The users are the engineers and agents who run the simulator, and later the continent server and
bots. They need every run stored and queryable, and every test result decided by a rule in the repo
rather than by someone reading a chart.

### User Story 1 - Every run becomes rows in the store (Priority: P1)

An engineer finishes a simulator run or a sweep and loads its folder into the store with one command.
The run's manifest becomes one row in `runs`. Its per-tick metrics become rows in `sim_ticks`, and its
merges, splits, fusions and violations become rows in `sim_events`. Loading the same folder again
changes nothing. A sweep loads every run it lists, so the decision memo's evidence sits in the store
next to the memo.

**Why this priority**: Every other story reads from the store. Without loaded runs, pass rules have
nothing to query and the memo's numbers can't be re-checked.

**Independent Test**: Load a small recorded run folder and a two-cell sweep into a fresh store, then
query them. Row counts and values must match the files exactly, and a second load must add no rows.

**Acceptance Scenarios**:

1. **Given** a completed run folder (format version 1), **When** it is loaded, **Then** `runs` holds
   one row with its run ID, kind, git SHA, config hash, scenario, seed, runner class and start and end
   times, `sim_ticks` holds one row per `ticks.csv` row with equal values, and `sim_events` holds one
   row per `events.csv` row.
2. **Given** a run that is already loaded, **When** the same folder is loaded again, **Then** the
   row counts for that run ID are unchanged.
3. **Given** a run folder that still has its `_incomplete` marker, **When** it is loaded, **Then** the
   loader refuses it with a non-zero exit code and loads no rows.
4. **Given** a run folder whose `formatVersion` is not 1, **When** it is loaded, **Then** the loader
   refuses it with a message that names the version, and loads no rows.
5. **Given** a sweep folder with a `sweep.json` index, **When** it is loaded, **Then** every completed
   run in the index is loaded, and each run row records its sweep name and cell ID.
6. **Given** the schema files in the repo, **When** they are applied twice to the same store, **Then**
   the second apply succeeds and changes nothing.

---

### User Story 2 - A SQL rule with a threshold decides pass or fail (Priority: P1)

After a run is loaded, the rule runner executes every rule file that applies to the run's kind. Each
rule's result is written to `rule_results`. The runner exits non-zero if any rule fails or is
inconclusive, so CI fails. A failing run's manifest, seed and inputs are kept: they are copied to the
retained tables and written out as files for CI to keep.

**Why this priority**: This is the exit criterion "a pass rule written as SQL plus a threshold can fail
CI". Every later gate (9, 11, 12) is expressed this way.

**Independent Test**: Load a run with a seeded violation, run a fixed-mode rule "zero violations", and
check that the runner exits with the failure code, writes a failing `rule_results` row and keeps the
run's manifest and inputs.

**Acceptance Scenarios**:

1. **Given** a fixed-mode rule `sim.violations` with threshold `= 0` and a loaded run with one
   violation, **When** the runner runs, **Then** it writes a `rule_results` row with value 1, threshold
   0 and passed false, and exits with code 5 (rule failed).
2. **Given** the same rule and a clean run, **When** the runner runs, **Then** the row has passed true
   and the runner exits 0.
3. **Given** a rule with `min_samples: 1000` and a run with 900 samples, **When** the runner runs,
   **Then** the result is recorded as inconclusive, not passed, and the runner exits with code 6
   (inconclusive).
4. **Given** a baseline-mode rule with no baseline recorded for the run's profile, **When** the runner
   runs, **Then** the result is recorded as passed with the note "no baseline" (report only), and it
   doesn't fail the run.
5. **Given** a baseline-mode rule with `max_regression: 10%`, a baseline of 100 and lower values being
   better, **When** a run measures 111, **Then** the rule fails. **When** a run measures 110, **Then**
   it passes.
6. **Given** a failing run, **When** the runner finishes, **Then** the run's rows are copied to the
   retained tables, and the run's manifest, seed and input files are written to the artifacts
   directory the runner was given.
7. **Given** a rule file with a missing field, an unknown mode or a query without the `{run_id:String}`
   parameter, **When** the runner loads its rules, **Then** it refuses to run with exit code 2 and
   names the file and field.
8. **Given** a rule that applies to another run kind, **When** the runner runs, **Then** that rule is
   skipped and writes no result.

---

### User Story 3 - Golden runs prove determinism in CI (Priority: P2)

A fixed set of golden runs is recorded with per-tick state hashes. CI replays each one on the fixed
runner class and compares the hashes tick by tick. On a mismatch it reports the first divergent tick
and the differing state, not just "final hash differs".

**Why this priority**: This is the exit criterion "the same seed gives the same per-tick state hash on
the fixed runner class". Sub-spec 1 proved it on one machine and build. This story proves it across
machines of one class.

**Independent Test**: Record a golden run, replay it, and get a match. Then replay with one changed
parameter and check that the report names the first divergent tick.

**Acceptance Scenarios**:

1. **Given** a recorded golden run and the same build on the same runner class, **When** it is
   replayed, **Then** every tick's hash matches and the check exits 0.
2. **Given** a golden run and a replay whose state differs from tick *t* onwards, **When** they are
   compared, **Then** the report names tick *t* as the first divergent tick, with both hashes, and
   exits with code 7 (diverged).
3. **Given** a golden run recorded on another runner class, **When** it is compared, **Then** the
   report says "not comparable", names both classes and exits with code 8, without claiming a
   divergence.
4. **Given** a run started by the harness, **When** its manifest is written, **Then** it records the
   runner class and every pinned hardware-intrinsic environment variable, set or unset.

---

### User Story 4 - Network profiles are defined, applied and calibrated (Priority: P2)

Each named network profile (LAN, Typical, Cross-region, Poor, and a reordering variant of each) has an
exact definition. The harness turns a profile into netem settings for one machine and a NetworkChaos
resource for Kubernetes. A probe pair exchanges sequenced UDP packets at 30 Hz, writes every packet to
`net_samples`, and the calibration decides whether the measured round trip, jitter and loss match the
profile within tolerance.

**Why this priority**: This is the exit criterion "each network profile is calibrated". Impaired runs
are only meaningful once their impairment is proven.

**Independent Test**: Feed the calibration known sample sets (a perfect match, too much jitter, too
much loss in one direction, reordering) and check each verdict. Then run the probe pair over loopback
for a few seconds and check that every sent packet appears in the samples.

**Acceptance Scenarios**:

1. **Given** the profile catalogue, **When** it is loaded, **Then** it holds LAN (1 ms, 0 ms, 0%),
   Typical (80 ms, 10 ms, 0.5%), Cross-region (160 ms, 25 ms, 1%) and Poor (250 ms, 50 ms, 3%), and a
   reordering variant of each.
2. **Given** samples whose median round trip, round-trip standard deviation and per-direction loss all
   sit within tolerance of the Typical profile, **When** calibration runs, **Then** the profile passes.
3. **Given** samples whose jitter is 12% above the target, **When** calibration runs, **Then** it fails
   and names jitter as the reason.
4. **Given** samples with 1% loss in one direction against the Typical profile's 0.5%, **When**
   calibration runs, **Then** it fails and names that direction's loss.
5. **Given** samples with any reordered packet and a profile that doesn't allow reordering, **When**
   calibration runs, **Then** it fails and names reordering.
6. **Given** a calibration, **When** it reports, **Then** it also reports RFC 3550 interarrival jitter
   for each direction.
7. **Given** a profile, **When** its netem settings are generated, **Then** the delay, jitter and loss
   are split per direction, a seed is set for the loss, and a plain FIFO child queue is added unless the
   profile allows reordering.
8. **Given** a profile, **When** its NetworkChaos resource is generated, **Then** it sets delay,
   jitter, loss, the direction and the target pods, and adds reordering only for a reordering variant.
9. **Given** the probe pair on loopback with no impairment for a short run, **When** it finishes,
   **Then** every sequence number sent is in the samples with send and receive timestamps, and none is
   reported lost.

---

### User Story 5 - Bots and a stub target prove the measurement path (Priority: P3)

A bot host runs many headless bots against a stub target that sends fake 30 Hz snapshots and echoes
inputs. Each bot runs a fixed 60 Hz frame loop and writes one `bot_frames` row per frame, in batches.
The host records its own frame-deadline misses, and a run from a host that missed deadlines fails as
inconclusive.

**Why this priority**: The path from bots to store to rules must work before Phase 1 depends on it.
The stub stands in for the game.

**Independent Test**: Start the stub target and a bot host with a few bots for a short run, then check
the frame counts per bot, the batching, and the host-health verdict.

**Acceptance Scenarios**:

1. **Given** a stub target and a bot host with 4 bots running for 2 seconds, **When** the run ends,
   **Then** each bot has written between 110 and 130 frames, each frame numbered once and in order.
2. **Given** a bot, **When** the stub target's snapshots arrive, **Then** the bot records
   snapshot-receipt counts and underruns per frame, and the stub target has echoed every input it
   received.
3. **Given** a bot host whose frame loop is made to miss more deadlines than its allowance, **When** the
   run ends, **Then** the host marks its rows invalid, and the run's host-health rule result is
   inconclusive.
4. **Given** frames to write, **When** the writer flushes, **Then** it writes in batches of at least
   5,000 rows or once a second, whichever comes first, plus a final flush at shutdown.

---

### User Story 6 - The harness comes up with one command, locally and in CI (Priority: P3)

One command brings up a local kind or k3d cluster with ClickHouse, the OpenTelemetry Collector and
Chaos Mesh, with Prometheus and Grafana as optional profiles. It applies the schemas. CI runs the same
command with the same manifests.

**Why this priority**: This is the exit criterion "the harness comes up locally on kind or k3d and in
CI with one command". The rest of the harness also runs without the cluster, so this story doesn't
block the others.

**Independent Test**: A CI job runs the one command on a fresh runner, waits for every component to
report ready, applies the schemas, loads a small run and runs the rules against it.

**Acceptance Scenarios**:

1. **Given** a machine with Docker and either kind or k3d, **When** the one command runs, **Then** it
   creates or reuses a cluster named for the harness, installs every core component, and exits 0 once
   all of them are ready.
2. **Given** the cluster is already up, **When** the one command runs again, **Then** it succeeds
   without recreating anything.
3. **Given** the optional observability profile is not requested, **When** the command runs, **Then**
   Prometheus and Grafana are not installed. **When** it is requested, **Then** they are.
4. **Given** the cluster is up, **When** the schema step runs, **Then** every table in the data model
   exists in ClickHouse.
5. **Given** the manifests, **When** they are rendered, **Then** the OpenTelemetry Collector sends logs
   and traces to ClickHouse with `create_schema: false`, and sends metrics to Prometheus.

---

### User Story 7 - The replay viewer shows any tick of a recorded run (Priority: P4)

An engineer opens a recorded run in the replay viewer and steps to any tick. The viewer draws sections
coloured by region, claims, portals and entities. It is built from the run's trace and config, so it
shows exactly what the regionizer did.

**Why this priority**: The viewer speeds up debugging, but no exit criterion depends on it.

**Independent Test**: Render a known tick of a small recorded run to an image description, and check
that the sections, regions and entities match the regionizer's state at that tick.

**Acceptance Scenarios**:

1. **Given** a recorded run with a trace, **When** the viewer renders tick *t*, **Then** every owned
   section is drawn with its region's colour, and every entity is drawn at its position at tick *t*.
2. **Given** a tick where a portal is closed, **When** it is rendered, **Then** the portal is drawn
   closed, and no claim is drawn crossing it.

### Edge Cases

- A run folder from a crashed run (with `_incomplete`) is refused, never half-loaded.
- Loading is interrupted part-way: a re-run completes the load without duplicating rows. Loads are
  idempotent by run ID.
- ClickHouse is unreachable: the loader and rule runner exit with code 1 and a message, and never
  report a pass.
- A rule's query returns no rows, or a NULL value: the result is inconclusive.
- A rule's query errors (bad SQL): the runner records an error result and exits with code 2 for the
  broken rule, never a pass.
- Two runs share a seed but have different config hashes: they are distinct runs. The run ID includes
  the config hash.
- A probe packet arrives twice: counted once, flagged as a duplicate.
- A probe packet arrives after the run ends: counted as lost.
- The clock jumps during calibration: round trip uses one host's monotonic clock, so a jump doesn't
  distort it.
- The bot host is overloaded: frames that miss their deadline are counted, not silently stretched.
- A golden run's format version is older than the replayer's: reported as not comparable, never as a
  divergence.

## Requirements *(mandatory)*

### Functional Requirements

**Store and schemas**

- **FR-001**: The repo MUST hold the store's schema as DDL files. They define `runs`, `sim_ticks`,
  `sim_events`, `net_samples`, `bot_frames`, `rule_results` and `baselines`, plus retained copies of
  the raw tables and a per-run aggregates table. Applying them MUST be idempotent.
- **FR-002**: Every raw table MUST order its rows by run ID, then by tick, frame or sequence number.
  Raw rows MUST expire after 14 days. Retained tables and per-run aggregates MUST NOT expire.
- **FR-003**: Every run row MUST record run ID, kind, git SHA, config hash, scenario, seed, network
  profile, runner class, sweep name and cell ID (when part of a sweep), start, end and verdict.

**Loader**

- **FR-004**: The loader MUST load a run folder that follows sub-spec 1's run-output contract (format
  version 1) into `runs`, `sim_ticks` and `sim_events`, with values equal to the files.
- **FR-005**: The loader MUST refuse an incomplete run folder or an unknown format version, and load
  nothing from it.
- **FR-006**: Loading MUST be idempotent by run ID: loading a run again leaves exactly one copy of its
  rows.
- **FR-007**: The loader MUST load every completed run listed in a sweep's index, and record the sweep
  name and cell ID on each run row.
- **FR-008**: The loader MUST insert rows in batches of at least 5,000 rows, or the whole remaining
  file when fewer are left.

**Pass rules**

- **FR-009**: A rule MUST be a file in the repo with an ID, the run kinds it applies to, a SQL query
  taking a `{run_id:String}` parameter and returning a value and a sample count, and either a fixed
  threshold with a comparison, or baseline mode with a maximum regression and a direction (lower or
  higher is better). A rule MAY state a minimum sample count.
- **FR-010**: Rule files MUST be validated before any rule runs. An invalid file MUST stop the runner
  with exit code 2 and name the file and field.
- **FR-011**: The runner MUST run every rule that applies to the run's kind, write one `rule_results`
  row per rule (value, threshold, sample count, outcome) and set the run's verdict.
- **FR-012**: A result MUST be inconclusive when the sample count is below the rule's floor, or the
  query returns no row or a NULL value. An inconclusive result MUST fail the run as a broken test.
- **FR-013**: A baseline-mode rule MUST only report (pass, noted "no baseline") until a baseline exists
  for its rule and profile. After that, it MUST fail when the run is worse than the baseline by more
  than the allowed regression.
- **FR-014**: Baselines MUST change only through a committed baseline file that names its source run.
  The runner MUST NOT write baselines itself.
- **FR-015**: The runner's exit code MUST be 0 when every rule passes, 5 when any rule fails, 6 when
  none fails but any is inconclusive, 2 for an invalid rule or query, and 1 for an infrastructure
  error.
- **FR-016**: For a run that doesn't pass, the runner MUST copy its raw rows to the retained tables,
  and write the run's manifest, seed and input files to a given artifacts directory.

**Determinism and golden runs**

- **FR-017**: Every run's manifest MUST record the runner class: OS, architecture, CPU model and the
  hardware-intrinsic environment variables (`DOTNET_EnableAVX2`, `DOTNET_EnableAVX512F`,
  `DOTNET_EnableHWIntrinsic` and the others the plan lists), set or unset.
- **FR-018**: The repo MUST hold a set of golden runs, each a run config plus its per-tick hashes and
  the runner class it was recorded on.
- **FR-019**: The golden-run check MUST replay each golden run and compare hashes tick by tick. On a
  mismatch, it MUST report the first divergent tick with both hashes and the metrics row from each
  side, and exit 7. Against a golden run from another runner class or format version, it MUST report
  "not comparable" and exit 8.
- **FR-020**: Re-recording a golden run MUST be an explicit command whose output is committed. CI MUST
  never re-record.

**Network profiles and calibration**

- **FR-021**: Network profiles MUST be named, versioned configuration with these definitions: round
  trip is the median; jitter is the standard deviation of round-trip time; loss is per packet in each
  direction. Each profile has a reordering variant.
- **FR-022**: The harness MUST generate netem settings for a profile: per-direction delay and jitter,
  per-direction loss with a recorded seed, and a plain FIFO child queue when reordering is not
  allowed. For TCP runs, the impairment sits on the receiver's ingress.
- **FR-023**: The harness MUST generate a Chaos Mesh NetworkChaos resource for a profile, with delay,
  jitter, loss, direction and pod selector, and reordering only in a reordering variant.
- **FR-024**: The probe pair MUST exchange sequenced UDP packets at 30 Hz for a configured duration (5
  minutes by default), and write every packet's sequence number, direction, and send and receive
  timestamps to `net_samples`.
- **FR-025**: Calibration MUST pass a profile only when the median round trip and the jitter are within
  10% of target (LAN: within 0.5 ms), loss is within 0.25 percentage points in each direction, and
  reordering is zero unless the profile allows it. It MUST also report RFC 3550 interarrival jitter for
  each direction.
- **FR-026**: A profile's calibrated settings MUST be stored with the profile, along with the runner
  class and kernel they were measured on.

**Bots and stub target**

- **FR-027**: The bot host MUST run many headless bots in one process, each on a fixed 60 Hz frame
  loop, writing one `bot_frames` row per frame in batches.
- **FR-028**: The stub target MUST send fake 30 Hz snapshots to every connected bot and echo every
  input it receives.
- **FR-029**: The bot host MUST count its own frame-deadline misses. When misses exceed the allowance
  (1% of frames by default), its rows MUST be marked invalid and the host-health rule MUST be
  inconclusive.

**Local environment**

- **FR-030**: One command MUST bring up the harness on a local kind or k3d cluster: ClickHouse, the
  OpenTelemetry Collector (contrib) and Chaos Mesh, with Prometheus and Grafana as an optional profile.
  It MUST be idempotent. CI MUST use the same command and manifests.
- **FR-031**: The OpenTelemetry Collector MUST send logs and traces to ClickHouse, with schema creation
  turned off and the tables created from the repo's DDL, and send metrics to Prometheus.
- **FR-032**: The simulator MUST stay runnable as a plain process without the cluster, and sweeps MUST
  be runnable as Kubernetes Jobs.

**Replay viewer**

- **FR-033**: The replay viewer MUST render any tick of a recorded run, showing sections coloured by
  region, claims, portals (open or closed) and entities.

**Configuration**

- **FR-034**: Every harness tool MUST validate its configuration at startup and refuse to start on an
  invalid value, naming the field. Connection settings MUST come from environment variables or
  arguments, never from committed secrets.

### Key Entities

- **Run**: one execution of the simulator, a probe pair, a bot load or a golden replay. It has a
  manifest (identity, inputs, environment) and a verdict.
- **Rule**: a file with a query, a threshold or baseline, and a sample floor. Each result is a rule
  result.
- **Rule result**: one rule's outcome for one run: value, threshold, sample count, and passed, failed
  or inconclusive.
- **Baseline**: the accepted value of a baseline-mode rule for one network profile, with its source
  run.
- **Network profile**: a named, versioned set of round trip, jitter, loss and reordering targets, plus
  its calibrated settings.
- **Golden run**: a run config, its runner class, and its per-tick state hashes.
- **Runner class**: the OS, architecture, CPU model and pinned intrinsic settings that make hashes
  comparable.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: Offline tooling and test infrastructure only: the store, loader, rule
  runner, golden-run check, probe pair, bot host, stub target, local cluster manifests and replay
  viewer. No game component, web surface or player data.
- **State ownership**: Each run's rows have one writer, the loader or tool that produced the run.
  Rule results have one writer, the rule runner. Baselines are written only by committing a
  baseline file.
- **Interaction class**: N/A. This feature adds no gameplay interactions.
- **Authority and disclosure**: N/A. No client inputs reach the game. The stub target accepts only
  bot traffic on a local or cluster network.
- **Persistence and write policy**: Test data goes to ClickHouse, in typed tables, in batches. Raw rows
  expire after 14 days. Failing and golden runs are retained, and per-run aggregates are kept. The
  store is test infrastructure, not the game database (open decision 10 is untouched), and PostgreSQL
  isn't involved.
- **Contracts**: The store's DDL, the rule-file format, the golden-run format, the profile format and
  the `net_samples` and `bot_frames` row shapes are contracts, versioned under `contracts/`. Sub-spec
  1's run-output contract is read without change.
- **Performance and evidence**: Bookkeeping and other timings loaded from runs keep their
  wall-clock labels and runner class. Calibration results are measurements and are recorded with
  their runner class and kernel. This feature sets no tuning value.
- **Security and abuse controls**: Store credentials come from environment variables. The local
  cluster exposes services only on localhost. The stub target binds to localhost or the cluster
  network, and rejects datagrams over a size limit.
- **Observability**: Harness tools log structured JSON through Serilog to stderr. Operational logs and
  traces go through the OpenTelemetry Collector, and metrics to Prometheus. Sentry is out of scope for
  offline tools, since they aren't services.
- **Licensing and design sources**: New code dependencies must be MIT or Apache-2.0. ClickHouse,
  the OpenTelemetry Collector, Prometheus and Chaos Mesh are Apache-2.0 separate processes. Grafana is
  AGPLv3: it runs only unmodified, as a separate process, in local development and CI, and never in a
  hosted environment (Principle VII). Design sources (the tc-netem manual, the Chaos Mesh docs, RFC
  3550, the ClickHouse docs and the .NET vectorization guidelines) are recorded in `research.md`.
- **Accessibility**: N/A. No web surface.
- **Quality gates**: 95% line coverage for changed `src/` files. Theories cover each profile, each rule
  mode and comparison, and each calibration failure reason. Store tests use unique run IDs and never
  assert global counts, so they are safe in parallel and against leftovers. Each test class's
  ClickHouse container is reaped by Testcontainers. No tick-path code changes.
- **Executable and living spec**: Every acceptance scenario maps to an automated test that is named in
  `tasks.md`. This spec and `tasks.md` are updated before the PR.
- **Spec independence and parallelism**: The only hard dependency is sub-spec 1's run-output contract,
  which is merged. The stories are independent of each other except that rules (US2) read rows loaded
  by US1. Each story has in-process tests that need no other story's infrastructure.
- **Open decisions**: None is settled. Open decisions 1 and 2 are settled by measurements this harness
  stores, but setting their values isn't part of this feature. Open decision 4 (browser transport)
  is untouched: the probe pair and stub target use UDP because they only measure the network, and the
  TCP note in FR-022 covers a WebSocket fallback if one is chosen.
- **Autonomous delivery (XIII)**: Every check is an automated test or a CI job. Nothing is assigned to
  a person. Running the full-scale calibration on dedicated hardware and replacing the stub with the
  continent server are system behavior for later phases, not steps for a person.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A completed simulator run of 18,000 ticks loads into the store in under 30 seconds on a
  CI runner, with row counts equal to the files.
- **SC-002**: A rule written as SQL plus a threshold turns a CI job red when it fails, and the failing
  run's manifest, seed and inputs are available as job artifacts.
- **SC-003**: Every golden run replays with zero divergent ticks on its runner class. A one-parameter
  change is reported at its first divergent tick, never only at the end.
- **SC-004**: Calibration gives the documented verdict for 100% of the synthetic sample sets in the
  test suite, one set per profile per failure reason.
- **SC-005**: The harness reaches ready on a fresh CI runner with one command in under 15 minutes.
- **SC-006**: A bot host with 4 bots keeps every frame deadline on an idle CI runner over a 2-second
  run, and writes each frame exactly once.

## Assumptions

- The store is ClickHouse. The issue's component table chose it, and the constitution doesn't settle
  it differently. Tests run it through Testcontainers, so they need Docker.
- The .NET client is ClickHouse's official `ClickHouse.Driver` package over HTTP, if its license is MIT
  or Apache-2.0 (checked in research). If not, the fallback is plain HTTP with ClickHouse's own
  formats, through `HttpClient`.
- The fixed runner class in CI is GitHub's hosted `ubuntu-latest` x64 runner, identified by the CPU
  model it reports. Golden runs are recorded on that class by a CI job, and committed from its
  artifact. A dedicated self-hosted runner can replace it later. That only changes the class name.
- The 24-hour soak and the full-scale sweeps keep running outside CI, and their folders are loaded with
  the loader. This feature doesn't re-run them.
- The decision memo's first-pass sweep results are loaded from the folders sub-spec 1 committed, where
  they exist in the repo, or from a re-run of the committed sweep specs.
- Full 5-minute calibrations of the impaired profiles need netem, which needs root on the host or
  Chaos Mesh in the cluster. CI's cluster job runs them. In-process tests cover calibration maths with
  synthetic samples and the probe pair over unimpaired loopback.
- Playwright browser clients join load runs from Phase 1, when there is a browser client. Here, bots
  are native .NET only.
- The proposed tolerances (10% for round trip and jitter, 0.5 ms for LAN, 0.25 percentage points for
  loss) and the 14-day retention are the issue's proposals. They are configuration, not code.
- Exit codes 5 to 8 are new and don't collide with sub-spec 1's 0 to 4.
