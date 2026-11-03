# Implementation Plan: Regionizer Simulator

**Branch**: `013-regionizer-simulator` | **Date**: 2026-11-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/013-regionizer-simulator/spec.md`

## Summary

Build the regionizer core library that sub-spec 10 will run inside the continent server, and an
offline, deterministic simulator that drives it with synthetic movement. The core keeps the four
invariants with Folia-style merge-later and tick-end processing, per-class claims (section- or
position-anchored), a 0/1-section merge gap, split damping, islands, layers and portals, and two
timer modes. The simulator adds:

- a seeded PRNG with named streams;
- lockstep and interleaved scheduling on an integer 60 Hz time base;
- invariant and timer monitors after every step;
- a per-tick state hash;
- nine scenarios, a parallel sweep driver and versioned local result files.

A standalone portal validator checks map files. An analysis step checks the simulator against the
issue's three formulas and writes the decision memo. Design decisions are in
[research.md](research.md).

## Technical Context

**Language/Version**: C# 13 on .NET 9 (`global.json` 9.0.300, `rollForward: latestPatch`); see
research D14 for the LTS follow-up

**Primary Dependencies**: FluentValidation 12.2.0 (Apache-2.0), System.IO.Hashing 10.0.14 (MIT),
System.CommandLine 2.0.14 (MIT), Serilog 4.4.0 + Console 6.1.1 + Compact 3.0.0 (Apache-2.0). The core
library has no package dependencies.

**Storage**: Local files only (run folders, map files, traces). No database.

**Testing**: xunit.v3 4.0.2, CsCheck 4.9.1 (property tests), coverlet.collector 10.1.0,
BenchmarkDotNet 0.15.8

**Target Platform**: Any 64-bit .NET 9 host (macOS arm64 locally, Linux x64 in CI)

**Project Type**: Regionizer core library + simulator tooling (two CLIs)

**Components Touched**: Continent server (its regionizer core library only); no other component

**Roadmap Phase**: Simulator (Pre-Phase 1, sub-spec 1)

**Performance Goals**: Regionizer bookkeeping under 1 ms p99 per tick at 5,000 entities on one thread
(estimate; measured by this feature). A Baseline run (18,000 ticks) completes in minutes, not hours,
so sweeps fit on one machine (estimate).

**Constraints**: Bookkeeping allocates nothing on ticks with no region created, merged or split. No
locks. Deterministic, with ID-ordered iteration and no platform-dependent math. Tuning values come
only from configuration.

**Scale/Scope**: Up to 5,000 entities per run on maps of 6–10 km per side, with section sizes from
8 to 64 m.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **Component and stack fit** (I, XII): Pass. Only the continent server's regionizer core is touched.
  It is C#/.NET and has no web stack.
- **Region ownership and single writer** (II, III): Pass. Each region owns its sections, entities and
  timers, and changes them only through the regionizer. Teleports use the two-step location-queue
  flow. Work from several regions applies in ID order. The core takes no locks (it is single-threaded
  engine infrastructure); multithreaded ticking is out of scope.
- **Interaction classes and claims** (III): Pass. Claims model instant interactions only. The
  simulator measures bridging and the largest region's share, which is the point of the feature.
- **Regionizer invariants**: Pass. Invariants 1–4 are implemented and checked after every step.
  Merge and split callbacks cover entities, timers and an extension hook for future region data.
  Timers survive merges and splits in both modes.
- **Tick budget and performance** (VI): Pass. See Tick Budget and Evidence. Benchmarks and
  zero-allocation tests are planned. The 20 Hz drop is modelled.
- **Authority, netcode and trust** (IV): N/A: offline tool with no clients.
- **Persistence and ownership** (V): N/A: no database.
- **Contracts and protocols**: Pass. The output, map, trace, configuration and CLI formats are
  versioned contracts in [contracts/](contracts/). No wire protocol or HTTP API.
- **Licensing and clean room** (VII): Pass. All dependencies are MIT or Apache-2.0 (research D13).
  Folia is used through its published docs only (research, sources table).
- **Evidence and open decisions**: Pass. Tuning values are configuration, recorded with each run.
  Open decision 1 is measured and recommended under Principle VI, not settled in code. Decision 2 is
  reported as a reading only. No other open decision is touched.
- **Operations**: Pass, scoped to a CLI tool. Configuration is validated with FluentValidation and
  fails fast with exit code 2, including the environment name. There are no secrets. Logs are
  Serilog compact JSON on stderr. Sentry, OpenTelemetry and health endpoints are N/A for an offline
  tool.
- **Security**: N/A: no network surface. Map and configuration files are untrusted input and are
  validated.
- **Testing discipline** (VIII): Pass. Tests are written first. Simulation tests use the integer time
  base, seeds and explicit stepping, and report the seed on failure. CsCheck property tests cover
  merges, splits and timer offsets. Theories cover classes, modes and scenarios. Every test writes to
  a unique temp folder. Parallel equivalence (parallel phases) is N/A because region ticks are
  single-threaded here.
- **CI/CD and documentation**: Partial; see Complexity Tracking. A GitHub Actions workflow builds,
  checks formatting and runs the tests with coverage, and fails if any changed `src/` file is under
  95% line coverage. Sonar and Codecov need third-party accounts, which no agent can create (XIII,
  unavoidable human actions). The work ships without them, and the maintainer is told once in the
  patch notes. A `docs/patch-notes.md` entry is included.
- **Executable and living specs** (IX): Pass. Each acceptance scenario and FR maps to a named test
  (tasks.md). No earlier spec covers this area.
- **Spec independence** (X): Pass. Completable alone. Sub-spec 2 will read this feature's output
  contracts, which is a dependency of sub-spec 2, not of this feature.
- **Incremental delivery** (XI): Pass. This is the simulator, the first deliverable.
- **Autonomous delivery** (XIII): Pass. No human steps, gates or manual checks.

## Tick Budget and Evidence

| Work item or tuning value | Estimate or measurement | Source | Share of the region tick budget |
| --- | --- | --- | --- |
| Regionizer bookkeeping, 5,000 entities, one thread | estimate: under 1 ms p99 | estimate (issue #13's proposed budget) | ~3% of 33 ms |
| Serialized part (merges, splits, cross-region claims) | estimate: under 0.2 ms p99 | estimate | ~0.6% |
| Section size, claim radius per class, merge gap, split damping, minimum region lifetime | to be measured | sweeps in this feature | — |

- **Allocations**: Each entity's claim list is a pooled buffer sized from its class radius when the
  entity is added. Region section, entity and timer lists keep their capacity. Observer callbacks take
  struct arguments. A zero-allocation test warms up, then asserts zero bytes from
  `GC.GetAllocatedBytesForCurrentThread` across steady-state steps (FR-028).
- **Degradation**: Interleaved mode drops regions to 20 Hz. Timers keep their deadlines in both modes,
  and merges pending on a slowed region still happen at its tick end.
- **Metrics**: Region count, largest region size and share, fusions, merge and split rates by class,
  split latency, section churn, region lifetime, cross-region pairs, bookkeeping cost (total and
  serialized), oversized fusions.

## Project Structure

### Documentation (this feature)

```text
specs/013-regionizer-simulator/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── regionizer-api.md     # core library public API
│   ├── run-output.md         # run folder: manifest, ticks, events, summary
│   ├── map-format.md         # map file read by the simulator and the portal validator
│   ├── config-and-cli.md     # run/sweep config files and both CLIs
│   └── trace-format.md       # recorded position traces
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
global.json
Directory.Build.props            # net9.0, nullable, warnings as errors, analyzers
Directory.Packages.props         # central package versions
.editorconfig
MmoEngine.sln
src/
├── MmoEngine.Regionizer/                    # core library (no package deps)
│   ├── Sections/        # SectionKey, LayerGrid, SectionTopology, claim geometry
│   ├── Regions/         # Region, RegionState, transitions, merge/split, islands
│   ├── Entities/        # EntityTable (SoA), EntityClass, claim buffers
│   ├── Timers/          # tick-counter and absolute timer modes
│   ├── Invariants/      # InvariantChecker, Violation
│   └── Regionizer.cs    # public façade (contracts/regionizer-api.md)
├── MmoEngine.Maps/                          # map model, JSON loader, PortalValidator
├── MmoEngine.Regionizer.Simulator/          # PRNG, config, scenarios, movement, scheduling,
│   │                                        # monitors, metrics, output, trace, sweep, analysis
├── MmoEngine.Regionizer.Simulator.Cli/      # `regionizer-sim` (run, sweep, replay, check, memo)
└── MmoEngine.PortalValidator.Cli/           # `portal-validator`
tests/
├── MmoEngine.Regionizer.Tests/              # unit, simulation and property tests of the core
├── MmoEngine.Maps.Tests/                    # map loader and validator tests
├── MmoEngine.Regionizer.Simulator.Tests/    # determinism, scenarios, sweeps, analysis, CLI
└── workflows/                               # (existing) Python workflow tests
benchmarks/
└── MmoEngine.Regionizer.Benchmarks/         # BenchmarkDotNet
simulator/
├── scenarios/           # default scenario configs, one per scenario
├── sweeps/              # sweep specs for the decision memo
└── maps/                # stacked-areas map and validator test maps (clean + seeded leaks)
docs/simulator/
├── decision-memo.md     # generated by `regionizer-sim memo`
└── results/             # per-sweep summary tables that the memo cites (raw rows stay local)
.github/workflows/dotnet.yml                 # build, format, test, coverage gate
```

**Structure Decision**: One solution with the core library separate from everything simulator-specific,
so the continent server can reference `MmoEngine.Regionizer` alone (FR-015, SC-007). An architecture
test asserts that the core references no other project or package. Maps are their own library because
the simulator and the validator CLI share them.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| No Sonar or Codecov in CI | Both need third-party accounts and tokens, which no agent can create (XIII, unavoidable human action 1) | Faking either would be a gate that checks nothing. CI enforces the 95% changed-file coverage rule itself until the accounts exist. |
| SDK pinned to .NET 9, not the current LTS | The .NET 10 SDK isn't installed on the machine running the work (research D14) | It's a SHOULD. Moving later is one `global.json` change and a TFM bump. |
