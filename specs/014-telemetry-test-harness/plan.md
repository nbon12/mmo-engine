# Implementation Plan: Telemetry and Test Harness

**Branch**: `014-telemetry-test-harness` | **Date**: 2026-11-04 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/014-telemetry-test-harness/spec.md`

## Summary

This builds Pre-Phase 1 sub-spec 2 (nbon12/mmo-engine#13): the harness that turns every run into
ClickHouse rows and every test into a SQL rule with a threshold.

- **Library and CLI**. A new library, `MmoEngine.Harness`, and a CLI, `harness`, provide:
  - the store's DDL and an idempotent schema apply;
  - a loader for sub-spec 1's run folders and sweep indexes;
  - the pass-rule runner;
  - the golden-run recorder and comparer;
  - the network profiles, with their netem and NetworkChaos generators, the calibration maths and the
    UDP probe pair;
  - the bot host and the stub target.
- **Local stack**. `infra/harness/up.sh` brings up a kind (or k3d) cluster with ClickHouse, the
  OpenTelemetry Collector, Chaos Mesh, and optionally Prometheus and Grafana. A CI workflow runs the
  same script.
- **Replay viewer**. It is a `view` command in the existing simulator CLI. It re-runs a recorded run
  deterministically up to the requested tick and writes an SVG.

Everything except the cluster has in-process tests. Store tests run against a real ClickHouse
through Testcontainers.

## Technical Context

**Language/Version**: C# on .NET 10 (`global.json` 10.0.100, `rollForward: latestPatch`; the move from
.NET 9 is PR #48). Shell (bash) for the one-command stack.

**Primary Dependencies**:

- `ClickHouse.Driver` 1.5.0, ClickHouse's official .NET client (MIT, research R1).
- `YamlDotNet` 16.x for rule files (MIT).
- `Testcontainers.ClickHouse` 4.x for tests (MIT).
- Existing packages: FluentValidation (Apache-2.0), System.CommandLine (MIT), Serilog (Apache-2.0)
  and xunit.v3 (Apache-2.0).
- Separate processes, not code dependencies:
  - ClickHouse server (Apache-2.0);
  - OpenTelemetry Collector contrib (Apache-2.0);
  - Prometheus (Apache-2.0);
  - Chaos Mesh (Apache-2.0);
  - kind (Apache-2.0);
  - Grafana (AGPLv3), which runs unmodified, in local development and CI only (Principle VII).

**Storage**: ClickHouse is the test-data store. It isn't the game database (open decision 10 is
untouched).

**Testing**: xUnit v3, with Theories for data variations. Testcontainers runs ClickHouse 25.8 for the
store, loader and rule-runner tests. Everything else is tested in process. A CI workflow job runs the
one-command stack on kind.

**Target Platform**: Developer laptops (Linux or macOS with Docker) and GitHub-hosted `ubuntu-latest`
runners.

**Project Type**: Tooling and test infrastructure.

**Components Touched**: Tooling only, plus an additive field in the simulator's manifest. No game
component.

**Roadmap Phase**: Pre-Phase 1 (simulator first).

**Performance Goals**: Load an 18,000-tick run in under 30 s (SC-001). The bot host keeps 60 Hz for
4 bots on a CI runner (SC-006). Both are estimates until the tests measure them.

**Constraints**: Inserts are batched, at least 5,000 rows or once a second (FR-008, FR-027). No
committed secrets. Tests are parallel- and rerun-safe, so every store test uses a unique run ID.

**Scale/Scope**: Seven tables plus retained copies. About 20 rule, profile and golden files. One
library, one CLI, one test project and one infra folder.

## Constitution Check

- **Component and stack fit** (I, XII): Passes. This is offline tooling. ClickHouse, the Collector,
  Prometheus, Grafana and Chaos Mesh are the components the issue's harness table names. No
  real-time path or web surface is touched.
- **Region ownership and single writer** (II, III): N/A. There is no gameplay state. Each run's rows
  have one writer (its loader or tool), and rule results have one writer (the rule runner).
- **Interaction classes and claims**: N/A. No interactions.
- **Regionizer invariants**: N/A. The regionizer core is unchanged. The viewer only reads its state.
- **Tick budget and performance** (VI): N/A. No tick-path code changes. Timings loaded from runs keep
  their wall-clock label and runner class.
- **Authority, netcode and trust** (IV): N/A for the game. The stub target and the probe server bind to
  localhost or the cluster network, and drop datagrams over 1,200 bytes.
- **Persistence and ownership** (V): N/A for the game database. The ClickHouse DDL is versioned,
  idempotent (`CREATE ... IF NOT EXISTS`) and applied by the `schema apply` command.
- **Contracts and protocols**: These contracts live in `contracts/`:
  - the store DDL;
  - the rule-file format;
  - the golden-run format;
  - the network-profile format;
  - the probe datagram;
  - the stub-target datagrams.

  None is a game wire protocol.
- **Licensing and clean room** (VII): Passes. Every new code dependency is MIT. Grafana stays a
  separate, unmodified process in local development and CI. Sources are in `research.md`.
- **Evidence and open decisions**: Passes. No tuning value is set, and no open decision is settled
  (spec, Open decisions).
- **Operations**: Each tool validates its options with FluentValidation and fails fast with exit
  code 2, including the environment name. Connection settings come from `MMO_CLICKHOUSE_URL` (or
  `--clickhouse`), with no default credentials in committed files except the local stack's
  throwaway user. Tools log Serilog compact JSON to stderr. Sentry, the Agones SDK and health
  endpoints are N/A: these are offline tools, not services.
- **Security**: The local stack exposes services only on localhost. The stub target and the probe pair
  enforce a datagram size limit.
- **Testing discipline** (VIII): Tests come first. Store tests use a unique run ID per test and never
  assert global counts. Each test class shares one ClickHouse container (a class fixture), with
  isolation by unique keys, the approved equivalent for code that opens its own connections. Data
  variations use Theories.
- **CI/CD and documentation**: The existing .NET workflow builds and tests the new projects, and its
  coverage gate applies. A new workflow runs the one-command stack. A patch-notes entry is included.
  Sonar and Codecov still wait on the owner's accounts (the 2026-11-03 patch notes).
- **Executable and living specs** (IX): Every acceptance scenario maps to a named test in
  `tasks.md`.
- **Spec independence** (X): The only hard dependency is sub-spec 1's run-output contract, which is
  already merged.
- **Incremental delivery** (XI): Pre-Phase 1, as the issue's build order lays it out (steps 1, 4, 6,
  8 and 9).
- **Autonomous delivery** (XIII): No human step. Golden-run recording is a CI job whose artifact is
  committed by an agent.

## Tick Budget and Evidence

N/A: no tick-path, Delivery or tuning changes.

## Project Structure

### Documentation (this feature)

```text
specs/014-telemetry-test-harness/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── store-ddl.md
│   ├── rule-file.md
│   ├── golden-run.md
│   ├── network-profile.md
│   └── harness-cli.md
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/MmoEngine.Harness/                 # library
├── Store/                             # DDL resources, SchemaApplier, ClickHouseStore (IHarnessStore)
│   └── Schema/*.sql                   # embedded DDL, applied in file-name order
├── Loading/                           # RunFolderReader, SweepIndexReader, RunLoader
├── Rules/                             # RuleFile, RuleFileParser, RuleValidator, RuleEvaluator, RuleRunner, BaselineFile
├── Golden/                            # GoldenRun, GoldenRecorder, GoldenComparer
├── Network/                           # NetworkProfile, ProfileCatalog, Calibration, NetemCommand, NetworkChaosManifest, ProbeServer, ProbeClient
├── Bots/                              # BotHost, Bot, FrameLoop, StubTarget, BatchWriter
├── RunnerClass.cs                     # runner-class fingerprint from a manifest or the current process
└── HarnessExitCodes.cs
src/MmoEngine.Harness.Cli/             # `harness` executable (System.CommandLine)
tests/MmoEngine.Harness.Tests/         # unit, in-process and Testcontainers tests
harness/
├── rules/*.yaml                       # pass rules
├── baselines/*.yaml                   # baseline values (committed, reviewed)
├── profiles/*.json                    # network profiles
└── golden/<name>/                     # golden runs: config.json, golden.json, ticks.csv
infra/harness/
├── up.sh                              # the one command
├── kind-config.yaml
└── manifests/                         # kustomize base + observability overlay
.github/workflows/harness.yml          # the stack job and the golden-run job
src/MmoEngine.Regionizer.Simulator/View/  # replay viewer (SVG), `regionizer-sim view`
```

**Structure Decision**: The harness is one library plus one CLI, the same shape as the simulator, so
the bot host, rule runner and loader share the store client and validation code. The library doesn't
reference the simulator. The golden-run check compares run folders, so the harness never depends on
simulator internals, and the regionizer core stays dependency-free. The viewer lives in the simulator,
because it needs the regionizer state at a tick.

## Delivery

The work ships as vertical slices, each a PR that leaves `main` green:

1. Spec artifacts, then US1 (store and loader), US2 (rules) and US3 (golden runs), with the
   sweep-result load.
2. US4 (profiles, calibration and probe pair) and US5 (bots and stub target).
3. US6 (the one-command stack and its CI workflow). This slice touches guarded paths (`infra/`,
   `.github/`), so it gets the independent review.
4. US7 (the replay viewer).

## Complexity Tracking

No violations.
