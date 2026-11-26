# Implementation Plan: Vehicle Physics Engine Selection

**Branch**: `016-vehicle-physics-selection` | **Date**: 2026-11-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/016-vehicle-physics-selection/spec.md`

## Summary

This builds Phase 1 sub-spec 3 (nbon12/mmo-engine#12), vehicle physics slice: choose the vehicle physics
engine among BepuPhysics2, Jolt and Rapier with an agreement-and-cost test, and record the result as gate
9's "Vehicle physics engine" decision. It reuses feature 015's shared core, formats, analyzer, agreement
runners, `simcore` CLI and `SimCore` workflow.

- **One vehicle model, owned by the core.** A raycast-wheel model in `src/MmoEngine.SimCore/Vehicles/`,
  under the full analyzer: command mapping, steering, wheel rays through the core's `SegmentQuery`,
  suspension and tire impulses, contact events, the bounds fault and the canonical state. Each engine
  holds only one rigid chassis box per vehicle and resolves its contacts (research R2). The engines'
  own vehicle layers were measured to be different vehicles, and Jolt's carries hidden state.
- **History-free stepping.** Each adapter runs its engine so that nothing but the canonical state
  carries over between steps: Bepu clears and rebuilds every step, Jolt writes the canonical state into
  persistent bodies with warm starting and the pair cache off, Rapier would build a fresh world (R5).
  Reset equivalence is then a property of the engine-neutral canonical state.
- **Formats.** Canonical state version 2 (version 1's bytes, then vehicles), stream version 2 (engine
  identity, vehicle rules revision, a vehicle config block, both hashes per step) and step record
  version 2; version 1 and its 19 streams are untouched and pinned by a committed SHA-256 manifest.
- **Adapters and builds.** Bepu runs in process on the .NET runtime in both builds, its server pinned to
  128-bit vectors. Jolt is our C++ shim over pinned Jolt v5.7.0 with cross-platform determinism and a
  no-lock single-threaded platform header: a `.so` through `[LibraryImport]` on the server, and its own
  Emscripten module through `[JSImport]` with byte MemoryViews in the browser, in all three build modes.
  Rapier fails the license scan at its latest release (foldhash, Zlib), so it is NotTested
  (`FitCheckFailed`, c) and its shim is not built (R12).
- **A fixture engine** with a committed fault table tests every check, status, the divergence measurement
  and the orchestration on `main`, whatever the real engines do (FR-035).
- **The selection** (`simcore vehicles select run`, a 120-minute CI job) evaluates every candidate on
  every required build of every mode, measures costs and download sizes, and writes a validated report;
  `simcore vehicles decide` applies FR-026 per mode and writes the decision record, which an agent
  commits; a staleness check and a per-mode vehicle agreement gate keep it true.

The six research threads prototyped every hard part ([research.md](research.md)): cross-build agreement
for Bepu (interpreted modes) and Jolt (all modes), the Bepu AOT crash, history-free reset equivalence in
all three engines, the vector-width settings, Jolt's lock count with and without the platform header,
the `[JSImport]` crossing and engine-module loading in every mode, the vehicle model's flat checks, and
the license scan's collectors and classifier.

## Technical Context

**Language/Version**:

- C# on .NET 10 (`global.json` 10.0.100, `rollForward: latestPatch`).
- C++17 for the Jolt shim: clang 18 (server), Emscripten 3.1.56 from the .NET 10.0.15 packs (browser).
- JavaScript ES modules on Node 22 (runners, engine glue). Bash for build and CI scripts.
- Python 3 for the workflow tests and the Emscripten license-table generator.
- Rust 1.97.0 is pinned in Rapier's manifest-only crate; no Rust is compiled (research R12).

**Primary Dependencies**:

- New, shipped: `BepuPhysics` and `BepuUtilities` **2.5.0-beta.33** (Apache-2.0), referenced only by the
  Bepu adapter (research R1); **Jolt Physics v5.7.0** (MIT), built in CI from a pinned commit, never
  vendored.
- New, tooling only: `Mono.Cecil` **0.11.6** (MIT), referenced only by `MmoEngine.SimCore.Vehicles.Selection`
  for the managed lock test (R16); the Emscripten 3.1.56 packs `Microsoft.NET.Runtime.Emscripten.3.1.56.{Sdk,Cache,Node}.linux-x64`
  **10.0.15** (MIT, from nuget.org), a toolchain whose linked libraries are scanned (R18, R19).
- Declared, never built: `rapier3d` =0.36.1 (Apache-2.0) in a manifest whose graph includes `foldhash
  0.2.0` (Zlib), which Principle VII does not allow without a PATCH amendment.
- Existing: `System.IO.Hashing`, FluentValidation, System.CommandLine, Serilog, xunit.v3, CsCheck,
  coverlet, BenchmarkDotNet 0.15.8, `Microsoft.CodeAnalysis.CSharp` 5.0.0, `@playwright/test` 1.63.0.

**Storage**: N/A. Committed files under `simcore/vehicles/` and `simcore/baseline/`: the vehicle
configuration, selection configuration, engine pin files, the vehicle test world, each engine's streams,
fixtures, the selection report and the decision record.

**Testing**:

- xUnit v3 Theories over each candidate, scenario, step rate (20, 30, 60 Hz), required build, build
  mode, status row, fixture fault and each branch and band boundary of the decision rule.
- Golden-fixture round trips and refusals for the version 2 formats; the version 1 baseline manifest.
- Zero-allocation tests for every candidate's vehicle step (FR-025); the lock test (FR-034), native and
  managed, each with its own self-test.
- Roslyn backstop compiles of each adapter with its declared exemption only.
- Native: `ctest` with clang source-based coverage (LCOV) for the Jolt shim; `tests/MmoEngine.SimCore.Vehicles.Native.Tests`
  (outside the solution) in the `vehicle-engines` job.
- `node:test` and Playwright agreement runs per build and mode; Python `unittest` for the workflow.
- Every test is in process except the fresh-process runs, the native and wasm builds and the browser runs,
  which use unique temporary directories and dynamic ports (Principle VIII).

**Target Platform**: The server build in process on Linux x64 (.NET 10, the continent server's
platform); the browser build as .NET WebAssembly in a dedicated Web Worker and in Node 22, in the three
modes `interp-prebuilt`, `interp-relinked` and `aot`; CI on GitHub-hosted `ubuntu-latest`
(`gha-ubuntu-latest-x64`).

**Project Type**: Shared gameplay library extensions (core vehicle model and formats), engine adapters
and browser hosts, and tooling (CLI, selection, runners, benchmarks, CI jobs).

**Components Touched**: The shared simulation core and its tooling. No running component changes; the
continent server (sub-spec 5), combat netcode (sub-spec 6) and the browser client (sub-spec 8) consume
the chosen engine later, and gate 9 (sub-spec 9) reads the record.

**Roadmap Phase**: Phase 1 (one region per server), sub-spec 3, vehicle physics slice.

**Performance Goals**:

- The vehicle step stays well inside the region tick (Tick Budget below): with 64 vehicles an estimated
  0.5–2 ms of the 33 ms budget, depending on the engine.
- The deciding number, one predicted vehicle per browser frame in a Chromium Worker, is measured and
  recorded, never gated (Principle VI).
- The chosen engine's vehicle agreement job finishes in under 15 minutes per mode, and the selection run
  in under 120 minutes (SC-007); each job's `timeout-minutes` enforces it.

**Constraints**:

- 015's determinism rules and analyzer for every managed line of the vehicle step; the engines' own
  determinism settings (Bepu single-threaded with the 128-bit pin, Jolt cross-platform deterministic,
  `-ffp-contract=off`) for the rest, checked by the agreement suite.
- Zero managed allocation per step after warm-up (FR-025); the native shims allocate from a per-world
  arena (R17).
- No lock or blocking synchronization in any candidate's step (Principle II, FR-034), shown by the lock
  test.
- Engines step on the calling thread, sleeping off, history-free (R5); the chassis is an unrounded box;
  statics are exactly the core's triangles (FR-006, R4).
- Version 1 formats, streams and `RulesRevision` unchanged (FR-015); 015's `agreement` and `measure`
  jobs unchanged (FR-029).

**Scale/Scope**:

- Up to 256 vehicles per world (`vehicles.maxVehicles`); streams use up to 8, benchmarks 1, 16, 64 and
  256.
- Per engine: 14 committed named streams plus 50 generated seeds of 8 vehicles for 20 s.
- Three real candidates and the fixture engine (plus its perturbed browser variant) in the selection.
- New projects: 9 in `src/`, 3 test projects, 1 benchmark project; one C++ shim; four folders in
  `tools/vehicles/`.

## Constitution Check

*GATE: passed before Phase 0 research; re-checked after Phase 1 design (below, all pass; the justified
items are in Complexity Tracking).*

- **Component and stack fit** (I, XII, Web Surfaces): Passes. The work extends the shared simulation
  library that continent servers and the browser client run: C#/.NET on the server, .NET WebAssembly in a
  Web Worker in the browser. Jolt is a C++ library linked in process through P/Invoke and run as its own
  WebAssembly module beside the .NET runtime, which keeps the one-process-per-continent model (I): no
  service, no network hop. No web surface is added (XII), and the real-time path adopts no web-hosting
  choice.
- **Region ownership and single writer** (II, III): Passes, with the native engines' mechanical check in
  Complexity Tracking. The core still owns no state between steps; a vehicle world, the engine's world
  included, has one writer: the region that owns the vehicles (sub-spec 5) or the client Worker that
  predicts its own vehicle. Effects within a step apply in a fixed order: vehicles in ascending ID, wheels
  in fixed order, contact events by ascending pair (data-model.md, World step order). No lock: MMO1006
  holds in the core, the fixture and every adapter with no exemption; the engines step on the calling
  thread with no job system; Jolt's platform header removes its 31 lock calls per step without changing
  its sources (R10); the lock test counts what the native and Bepu code actually takes (R16). No
  cross-region work is added.
- **Interaction classes and claims** (III, Architecture Constraints): Passes. Vehicle-to-vehicle and
  vehicle-to-terrain collisions are instant interactions resolved by the owning region inside its tick.
  No claim, claim radius, portal or bridging change; the ground-vehicle claim radius stays the
  simulator's (013).
- **Regionizer invariants** (Architecture Constraints): N/A for this slice, which defines no region
  data: `SimWorld` is the composition for tests, the agreement suite and the client Worker. Phase 2's
  merge and split callbacks will need to load a vehicle exactly into another world; every winner must
  pass server reset equivalence (US1 scenario 10), the record states it (FR-027), and Jolt's body IDs are
  a function of the vehicle ID so a merge keeps them (R5, R11).
- **Tick budget and performance** (VI): Passes. Costs are stated and labelled in Tick Budget and Evidence.
  The vehicle step allocates nothing after warm-up, tested per candidate; buffers are world-owned and
  sized from the configured maxima; the per-vehicle state is fixed-size arrays that a region's
  structure-of-arrays storage can hold. Benchmarks (FR-023) and zero-allocation tests (FR-025) are
  planned. No blocking I/O. Degradation: the step runs at the configured rate, 20 Hz included; a live
  rate change is out of scope, as in 015.
- **Authority, netcode and trust** (IV): Passes. The server stays authoritative; the client runs the same
  engine to predict its own vehicle. Every vehicle command is decoded with 015's strict length and
  version checks and validated by 015's rules before it affects state (FR-008); streams, states,
  configurations and collision files are untrusted, decoded with bounded counts and no partial result;
  the native and JavaScript boundaries check every length and count before anything crosses. Wire size
  and rate limits stay with sub-specs 4 and 7.
- **Persistence and ownership** (V): N/A. Nothing is persisted; every artifact is a committed file.
- **Contracts and protocols** (Engineering Standards): Passes. The version 2 formats begin with the
  container's identifier and version, have golden fixtures and refusal fixtures
  ([contracts/vehicle-formats.md](contracts/vehicle-formats.md)), and version 1 stays readable beside
  them (version tolerance). The input command and collision formats stay at version 1. The engine ABI,
  the selection report, the decision record, the selection configuration and the status table have
  documented, validated shapes ([contracts/](contracts/)).
- **Licensing and clean room** (VII): Passes, with the platform-runtime reading in Complexity Tracking.
  Every new shipped dependency is MIT or Apache-2.0 (Bepu, Jolt, Mono.Cecil as a tool); every component a
  candidate adds is scanned (FR-031, [contracts/license-scan.md](contracts/license-scan.md)); Rapier's
  graph includes foldhash (Zlib), so Rapier is never built or linked and foldhash is reported as a PATCH
  amendment candidate (R12). Jolt is fetched by commit and only `Jolt/` is compiled; its LGPL-3.0 assets
  are never copied. No GPL, LGPL or AGPL code was read; design sources are in
  [research.md](research.md#design-sources).
- **Evidence and open decisions** (VI, Architecture Constraints): Passes. No tuning value is set: vehicle
  values are placeholders in the validated vehicle configuration, thresholds are placeholders in the
  validated selection configuration, and every cost is a measurement with its runner class, build mode
  and engine identity, or a labelled estimate. Open decision 3 (tick rate) stays open: vehicles step at
  the configured rate, tested at 20, 30 and 60 Hz. Open decision 5 is untouched (projectiles do not hit
  vehicles); 4 and 9 are untouched. Gate 9's WebAssembly build mode stays open (the rule runs per mode
  and a mixed result is conditional), and so do parallel phases (engines are single-threaded; the record
  carries FR-027's evidence). The vehicle physics engine is not an open decision (spec Clarifications).
- **Operations** (Engineering Standards): Passes for a library and tools. The vehicle configuration is
  validated by the core's rule table on every build and by FluentValidation on the server (fail fast);
  the selection configuration by FluentValidation; `simcore` validates its options and
  `DOTNET_ENVIRONMENT` as in 015, logs Serilog compact JSON to stderr, and refuses to start a Bepu
  command without the vector pin. Sentry, OpenTelemetry, Agones and health endpoints are N/A: no service
  runs. The vehicle step logs nothing.
- **Security** (Engineering Standards): Passes. Decoders bound every count before allocating and never
  return a partial result; engine sources and toolchains are pinned by commit, version and checksum
  (FR-002); the CI jobs run with `contents: read` and SHA-pinned actions; the engine module is checked
  against its SHA-256 before it is instantiated (R14). Authentication and authorization are N/A for a
  library and tools.
- **Testing discipline** (VIII, Testing Standards): Passes. Tests are written first (tasks.md orders each
  red test before its green task). Simulation tests step a fixed rate and seed and advance the steps each
  scenario implies (the contact event's step, the landing's window, the window restarts). Data variations
  are Theories. Tests are parallel- and rerun-safe (unique temporary directories, dynamic ports, child
  processes with their own work directories). Persistence and Testcontainers are N/A. Parallel
  equivalence is N/A: no parallel phase is added; FR-027's thread runs are evidence, never a status.
- **CI/CD and documentation**: Passes. The .NET workflow builds, formats and tests every new project in
  the solution, with the 95% changed-file coverage gate; the Jolt shim's C++ is covered by its own
  toolchain's coverage in the `vehicle-engines` job, which reads LCOV through an additive change to
  `tools/coverage_gate.py` (R28). The `SimCore` workflow gains six jobs and keeps 015's two unchanged
  ([contracts/ci-jobs.md](contracts/ci-jobs.md)). A patch-notes entry ships with each pull request. Sonar
  and Codecov still wait on the owner's accounts, as before.
- **Executable and living specs** (IX): Passes at each merge. Every acceptance scenario and Independent
  Test maps to a named automated test in `tasks.md`; real engines' verdicts are evidence in the committed
  report, and the checks, statuses and orchestration are tested on the fixture engine and fixture
  inputs, with the winner's checks a real test on `main` (US1 scenario 14, the `vehicle-winner-checks`
  job). Slice 1's spec.md lists the slice-2-only items under "Planned for slice 2: not yet built, no test
  on main", as 015 did, and slice 2 moves them back. The implementing pull requests update spec.md where
  this plan departs from its text (Decisions below) and 015's spec.md where its text names only
  version 1.
- **Spec independence and parallelism** (X): Passes. One hard dependency, already met: 015's core,
  formats, runners, CLI and workflow, merged on `main`. No sibling of sub-specs 4 to 9 must land first.
  Sub-spec 6 depends on this record only if it is NonExact (the provisional flag names its missing
  correction-distance measurement); that dependency is documented here and must be documented in
  sub-spec 6's `plan.md` when it is written. Gate 9 reads the record.
- **Incremental delivery** (XI): Passes. A focused Phase 1 slice in two pull requests (Delivery below),
  with region-ready code: the vehicle step's state in and state out, one writer, no locks.
- **Autonomous delivery** (XIII): Passes. Every check is a test or a CI job; the selection runs in CI;
  the decision is computed by a command and committed by an agent; expected hashes change only through
  `simcore streams record --reason`; nothing is assigned to a person. Questions only the maintainer can
  settle (the platform-runtime reading of Principle VII, a foldhash amendment, upstream bug reports) are
  patch-notes follow-ups that never block the merge.

## Tick Budget and Evidence

Sandbox numbers are from the research threads (4-vCPU Xeon at 2.1 GHz, .NET SDK 10.0.115, Node 22),
labelled as measurements on the sandbox, not on the runner class. The selection run's report replaces
them with runner-class measurements (FR-023, FR-024).

| Work item | Estimate or measurement | Source | Share of the region tick budget (33 ms) or frame (16.7 ms) |
|-----------|-------------------------|--------|------------------------------------------------------------|
| Core wheel model per vehicle per substep (4 segment queries, impulses, 4 tire passes) | estimate: 1–3 µs x64 | estimate, from 015's segment-query class of cost | 64 vehicles: 64–192 µs, 0.2–0.6% |
| Bepu engine step, clear-rebuild | measurement (sandbox): one box persistent 20 µs; two demo cars (10 bodies, 32 joints) clear-rebuild 245 µs. estimate for one chassis body per vehicle: 30–60 µs at 1 vehicle, 0.5–1.5 ms at 64 | research R5, R9 | 64 vehicles: 1.5–4.5% (estimate) |
| Jolt engine step (rigid chassis only) | measurement (sandbox): 7.1 µs mean for 1 vehicle with Jolt's own vehicle constraint, 212–225 µs for 16 through P/Invoke; estimate without the constraint: lower | research R10, R24 | 64 vehicles: estimate 0.3–0.9 ms, 1–3% |
| Rapier history-free step (follow-up only) | measurement (sandbox): 11.4 µs at 1 vehicle, 301 µs at 64, with its raycast controller | research R5 | not built |
| Server crossing (P/Invoke) per substep | measurement (sandbox): about 30 ns per call | research R14 (N2) | negligible |
| Contact derivation, all pairs | estimate: bounding-sphere reject then a box SAT test; 64 vehicles (2,016 pairs) about 10 µs, 256 (32,640 pairs) about 150 µs | estimate | 256: 0.5% |
| State hash, version 2 | estimate: 015's 129 ns per 512 B, so +30 ns per vehicle | estimate from 015 R13 | negligible |
| Browser: one predicted vehicle per frame | measurement (sandbox, engine step only, no core wheel model): Jolt 16–20 µs in Node prebuilt; Bepu 330 µs (one box, persistent) in Node prebuilt and 470 µs in a Chromium Worker | research R14, R9 | Jolt about 0.1%, Bepu about 2–3% of a frame, before the wheel model |
| Browser crossing (`[JSImport]`, two MemoryViews) | measurement (sandbox): 1.5–2.0 µs per call | research R14 | about 0.01% |

- **Allocations**: every buffer is world-owned and sized at world creation from `vehicles.maxVehicles`
  and the static triangles: the vehicle arrays, the next-state copy, the body crossing buffers, the
  contact and rejection event buffers (capacity 8 × and 1 × the maximum), Bepu's `BufferPool`, and the
  native arena. `Load` and `Begin` may allocate; `Step` never does. Zero-allocation tests run ten
  windows of 1,000 steps after warm-up for each candidate's vehicle step, each window 0 bytes, with
  015's one rerun rule (FR-025, SC-005).
- **Degradation**: no Delivery work. The step runs at the configured rate, 20 Hz included, with the
  configured substeps; a live 30 to 20 Hz change needs a state conversion, out of scope as in 015
  (sub-spec 5).
- **Metrics**: none added. The step returns its vehicle contacts, rejections and faults in its events and
  report for sub-spec 5 to count and export. Contact derivation is O(n²) over a world's vehicles; a
  spatial index for large regions is sub-spec 5's follow-up.

## Project Structure

### Documentation (this feature)

```text
specs/016-vehicle-physics-selection/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── vehicle-formats.md        # state v2, stream v2, step record v2, golden fixtures, baseline manifest
│   ├── vehicle-config.md         # the vehicle configuration's rule table and block
│   ├── engine-adapter.md         # IVehicleEngine, adapters, native ABI, hosts, runners, identity, analyzer
│   ├── selection-config.md       # tolerances, window, tie band, window check
│   ├── selection-report.md       # the report, statuses, measurements, fit check, validation
│   ├── decision-record.md        # the rule, the record, staleness, gate plan, fixture reports
│   ├── license-scan.md           # collectors, boundary, classifier
│   ├── vehicles-cli.md           # simcore command changes and the vehicles commands
│   └── ci-jobs.md                # the SimCore workflow's vehicle jobs
├── checklists/requirements.md
└── tasks.md                      # /speckit-tasks; not created by this plan
```

### Source Code (repository root)

```text
src/MmoEngine.SimCore/                         # the core (analyzed); additions only
├── SimCoreVersion.cs                          # + VehicleRulesRevision = 1; RulesRevision stays 1
├── Vehicles/
│   ├── Config/                                # VehicleConfig, VehicleConfigFields, VehicleConfigRelations,
│   │                                          #   VehicleConfigGuard, VehicleConfigBlock, CompiledVehicleConfig
│   ├── Engine/                                # IVehicleEngine, IVehicleEngineFactory, EngineIdentity, EngineCode,
│   │                                          #   VehicleBody, StaticTriangle, EngineResult
│   ├── VehicleState.cs, WheelState.cs, Quat.cs
│   ├── VehicleCommands.cs                     # command mapping, steering rate, reverse rule
│   ├── WheelModel.cs                          # rays, suspension, tire impulses, Gauss-Seidel passes
│   ├── ChassisBox.cs                          # oriented-box separation and penetration (SAT)
│   ├── VehicleContacts.cs                     # contact-event derivation
│   └── VehicleStep.cs                         # stages V1-V5 (data-model.md)
├── Collision/CollisionWorld.StaticTriangles.cs   # CopyStaticTriangles for the adapters
├── Formats/                                   # RecordedStream v1+v2, StateSerializer v2 layout rows, ClearEvents,
│                                              #   FormatErrorCode + VehicleRulesRevisionMismatch, EngineIdentityMismatch
├── World/                                     # SimWorld with vehicles; StepFaultCode + VehicleOutOfBounds, EngineFault
└── Batch/                                     # BatchStepper.LoadVehicleConfig, StreamRunner.Run/RunWindow, StepRecord v2
src/MmoEngine.SimCore.Analyzers/               # + the per-assembly exemption (build_property.MmoDeterminismExemption)
src/MmoEngine.SimCore.Vehicles.Fixture/        # FixtureEngine, FaultTable model (analyzed, no exemption)
src/MmoEngine.SimCore.Vehicles.Bepu/           # BepuEngine, callbacks, no-op refinement scheduler (analyzed, MMO1001)
src/MmoEngine.SimCore.Vehicles.Jolt/           # JoltEngine, JoltCalls, JoltNative, JoltJs (analyzed, MMO1008 interop)
│   └── native/                                # CMakeLists.txt, build.sh, src/*.cpp (shim, ExactTriangleGridShape, arena),
│                                              #   include/Jolt/Core/PlatformBlue.h, exports.map, glue/jolt-glue.mjs, tests/
src/MmoEngine.SimCore.Vehicles.Rapier/         # RapierEngine and bindings over the shared ABI (analyzed, MMO1008 interop)
│   └── native/                                # Cargo.toml, Cargo.lock, rust-toolchain.toml, src/lib.rs (empty): manifest only
src/MmoEngine.SimCore.Vehicles.Fixture.Wasm/   # browser hosts (not analyzed, as 015's host): VehicleExports, Program.cs
src/MmoEngine.SimCore.Vehicles.Bepu.Wasm/
src/MmoEngine.SimCore.Vehicles.Jolt.Wasm/      #   + wwwroot/engine/ from the vehicle-engines artifact
src/MmoEngine.SimCore.Vehicles.Server/         # server-only: EngineCatalog, VectorWidthGuard, EngineIdentityFingerprint,
│                                              #   VehicleWorldGenerator, VehicleScenarioCatalog, VehicleStreamGenerator,
│                                              #   Checks/ (oracles, US1.1-11), DivergenceMeasurement, Coverage
src/MmoEngine.SimCore.Vehicles.Selection/      # Config/ (SelectionConfig, validator, WindowCheck), License/, FitCheck/,
│                                              #   Locks/ (BepuLockSites via Mono.Cecil), Orchestration/ (SelectionRun,
│                                              #   StatusTable), Report/ (model, validator), Decision/ (rule, record,
│                                              #   staleness, gate plan)
src/MmoEngine.SimCore.Cli/Commands/            # + VehiclesCommand and sub-commands; --engine on streams and agreement
src/MmoEngine.SimCore.Cli/Agreement/           # BuildNames + 3 builds and modes; manifest v2; windows compare
tests/MmoEngine.SimCore.Tests/                 # + Vehicles/ (config, wheel model, box SAT, contacts), Formats/ (v2 golden,
│                                              #   baseline manifest), World/, Batch/ (v2 records, RunWindow)
tests/MmoEngine.SimCore.Vehicles.Tests/        # fixture checks and faults, Bepu server tests and allowlist, zero-allocation,
│                                              #   Selection/, License/, FitCheck/, Decision/, Cli/ (QuickstartTests);
│                                              #   vehicles.runsettings (DOTNET_PreferredVectorBitWidth=128)
tests/MmoEngine.SimCore.Vehicles.Wasm.Tests/   # the hosts' exports called as plain methods (coverage)
tests/MmoEngine.SimCore.Vehicles.Native.Tests/ # outside MmoEngine.sln; real Jolt, lock-counter self-test (vehicle-engines job)
tests/MmoEngine.SimCore.Analyzers.Tests/       # + AdapterBackstopTests, exemption Theories
tests/workflows/test_simcore_workflow.py       # + the vehicle jobs; fixtures/simcore-015-jobs.json
benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks/  # world steps at 1, 16, 64, 256 vehicles per engine
tools/agreement/                               # runtime.mjs, server.mjs, node/agreement.test.mjs, specs/worker.spec.mjs
│                                              #   (additive changes), lib/measure-vehicles.mjs (new)
tools/vehicles/
├── ci/                                        # changed-paths.sh, publish-host.sh
├── emsdk/emsdk.proj                           # PackageDownload of the pinned Emscripten packs
├── lockcount/                                 # lockcount.c (interposer), locktestlib.c, build.sh
└── license/gen_emscripten_table.py
tools/coverage_gate.py                         # + LCOV input (additive)
simcore/baseline/015-65112ab.sha256            # FR-015's manifest
simcore/golden/stream/                         # + v2-* cases; unsupported-version moves to version 3
simcore/vehicles/
├── config/default.json                        # 015's placeholders + the vehicles section
├── selection.json                             # the selection configuration
├── content/vehicle-world.mmcd
├── engines/{bepu,jolt,rapier,fixture}.json    # pin files (engine identity)
├── streams/{fixture,bepu,jolt}/*.mmrs, REGENERATIONS.md
├── license/                                   # boundary.json, overrides.json, emscripten-3.1.56.json
├── fixtures/                                  # fault-table.json, decision/, fitcheck/, license/, statuses/,
│                                              #   divergence/ (record pairs with known offsets, US5)
└── selection/                                 # report.json, decision.json (slice 2)
.github/workflows/simcore.yml                  # + six vehicle jobs, workflow_dispatch (slice 2)
Directory.Packages.props                       # + BepuPhysics, BepuUtilities, Mono.Cecil
```

**Structure Decision**: The vehicle model and formats are core code, so they live in
`src/MmoEngine.SimCore/` under the full analyzer and ship in every build; the core still references only
the BCL and `System.IO.Hashing` (015's architecture test), and engines are reached only through
`IVehicleEngine`. Each engine's adapter is its own analyzed assembly with one declared exemption, so an
engine's API never leaks into the core and the license scan can attribute every added file to one
candidate. Each engine has its own browser host, so its download is measured alone and 015's host and
`agreement` job stay unchanged. Server-only tools are split in two: `Vehicles.Server` (what sub-spec 5 can
reuse: the engine catalog, content, scenarios, checks) and `Vehicles.Selection` (the selection's own
machinery, with Mono.Cecil confined to it). Native shims live inside their adapter's folder, so 015's
`src/MmoEngine.SimCore*/**` workflow filter and the changed-file coverage rule reach them.

### Decisions made during design

The spec leaves these open, or the research changed a premise. Each is decided here (with its research
item) so `tasks.md` and the implementation follow one answer.

**Integration architecture, per engine and per build mode**

| Engine | Server build | `interp-prebuilt` | `interp-relinked` | `aot` | Expected status [estimate] |
| --- | --- | --- | --- | --- | --- |
| Fixture | in process, managed | in the .NET runtime | in the .NET runtime | in the .NET runtime | Qualified (no fault) |
| Bepu 2.5.0-beta.33 | in process, managed, `DOTNET_PreferredVectorBitWidth=128` | in the .NET runtime | in the .NET runtime | NotBuilt (`BuildFailed`: mono-aot-cross assertion) | Qualified or Diverged in the interpreted modes |
| Jolt v5.7.0 | `.so` via `[LibraryImport]`, BLUE, CPD | own Emscripten module via `[JSImport]` | the same module | the same module | Qualified |
| Rapier 0.36.1 | not built | not built | not built | not built | NotTested (`FitCheckFailed`: c) |

- **Vehicle model**: one core-owned raycast-wheel model (R2); engines hold one rigid chassis body per
  vehicle. FR-005's "solver substeps" is the core's K (R3). The four wheels are solved with
  `tireSolverIterations` Gauss–Seidel passes.
- **History-free stepping** per engine is part of its identity (R5).
- **Statics**: exactly the core's triangles in every engine; Jolt through an exact-triangle user shape,
  so FR-006 holds without patching Jolt (R4).
- **One byte ABI** for native engines, one adapter with two bindings chosen by `OperatingSystem.IsBrowser()`
  (R13); a separate engine module per native engine in the browser, loaded by the runner and registered
  with `setModuleImports` (R14).
- **Bepu's AOT failure is reported, not worked around**: no Bepu patch, no internal force-interpret item
  (R9).

**Spec readings**

- **FR-034, Bepu**: "the methods the step reaches" are the methods the step executes, measured on an
  instrumented copy of Bepu's assemblies over the 1,000-step window; the static site list is evidence
  (R16). A static reachability scan would fail Bepu by construction.
- **FR-034, native allocation**: the shims allocate from per-world arenas whose own tests require zero
  system-allocator fallbacks; the lock test's allocator count is evidence, not a status (R17).
- **FR-030, the fit check**: criteria in the order c, b, d, f, a, e; a criterion that cannot be evaluated
  without linking a component (c) rejected is a fail with `notEvaluated` and its reason (R12). FR-001's
  "C API" for Rapier is our shim's C ABI, and FR-030 (a)'s "published C API" is Rapier's published crate
  interfaces.
- **FR-031, the boundary**: read at the component level, with glibc, the GCC runtime and libstdc++ as
  boundary packages listed in the report (R18).
- **FR-013 and US2 scenario 4**: the browser restarts at every window start and steps one window; the
  windows tile the stream, and the same runs feed the divergence measurement. The server restarts at
  every window start and steps to the end (R23).
- **FR-020**: `StepQueued` drives the first vehicle (lowest ID) when the state has vehicles, else the first
  character, as in 015.
- **FR-018**: Firefox and WebKit, optional in 015, are not run for vehicles; the required builds are the
  spec's eight.
- **FR-026**: costs are decimals with three places (µs) and bytes are integers, compared in
  `System.Decimal` (R25). FR-027's gate 9 statement: the vehicle exit criterion is met only when every
  mode is BitExact, because the build mode is open.
- **FR-027, parallel phases**: an evidence run at 1 and 4 threads for every engine that can step with
  threads and its determinism setting on, with whether it documents thread-count independence (R27).
- **US1 scenario 6's first clause** tests the core's contact derivation, not the engine; the report says
  so (R20).
- **US1 scenario 9** ("stepped twice") reloads one engine instance for the second run; every other check
  uses a fresh instance, so a fault aimed at one check reaches no other (R22).
- **The fixture pair** (`fixture`, `fixture-perturbed`) runs through the real harness in every selection
  run and is checked by `select check-fixtures`; it never takes part in the real decision (R22).

**Formats and versioning**

- Canonical state version 2 is version 1's bytes followed by the vehicle part, so the version 1 part hash
  is a hash of the prefix (R6). Step record version 2 appends `v1PartHash`.
- Stream version 2 has a 116-byte header with the engine identity (`engine`, `release`, `fingerprint`)
  and `vehicleRulesRevision`, 015's config block unchanged, a separate vehicle config block (sections 8
  and 9), and both expected hashes per step.
- `SimCoreVersion.RulesRevision` stays 1; `VehicleRulesRevision` starts at 1; each engine's
  `adapterRevision` in its pin file is bumped by any adapter or shim change that can change results; any
  identity change makes that engine's streams NotComparable until recorded again with a reason.
- The agreement manifest goes to version 2 for vehicle work directories only; 015's work directories
  stay version 1 and `compare` prints exactly 015's lines for them.
- 015's `unsupported-version` refusal fixture moves to version 3 (FR-016).
- Vehicle IDs are in [1, 65535]; a step's begun contacts are capped at 8 × `maxVehicles`, beyond which
  the step faults with `QueryCapacityExceeded`.

**CI shape** ([contracts/ci-jobs.md](contracts/ci-jobs.md)): `vehicle-changes` (job-level path
selection), `vehicle-engines` (pinned native builds, each bounded at 15 minutes, cached by identity;
shim tests and coverage; native .NET tests), `vehicle-agreement-plan` (staleness and matrix),
`vehicle-agreement` (one 15-minute leg per mode the record names a winner for; the prebuilt leg installs
no workload), `vehicle-winner-checks` (US1 scenario 14 on `main`) and `vehicle-selection` (120 minutes,
every candidate and the fixture pair in one job on one runner class). 015's jobs are pinned unchanged by
the workflow test.

**Deviations from the spec's text, fixed in the implementing pull requests**

- "Engine releases: latest stable release": Bepu is pinned to 2.5.0-beta.33, its latest release, a
  prerelease, because 2.4.0 lacks the refinement-schedule hook (R1). The spec's assumption gains that
  sentence.
- "License scan boundary": one sentence naming the system linker's startup objects and
  header-instantiated runtime-library code as part of the boundary (R18).
- "Browser integration of Jolt and Rapier": Rapier cannot be linked statically with .NET 10's
  Emscripten on stable Rust at all (R14); the conclusion, a separate module, is unchanged.
- 015's `spec.md`: where it names only version 1 (FR-016, FR-020, the unknown-version examples).

**Follow-ups for the patch notes** (the spec's four, plus what the research found; none blocks a
merge):

1. How Principle VII treats platform and toolchain runtime libraries and licenses with exceptions
   (PATCH amendment, the maintainer's call).
2. For a NonExact record: measuring vehicle correction distances under the network profiles (sub-spec 6's
   dependency on this record).
3. For a NonExact record: the cost to sub-specs 5 and 6 once vehicles and characters interact.
4. Static linking of a native engine into the .NET module, if the crossing decided the outcome.
5. A PATCH amendment naming `foldhash` (Zlib) would make Rapier a candidate; the follow-up then writes
   Rapier's shim (R12, R13) and reruns the selection.
6. `DOTNET_EnableAVX512F=0` is a no-op on .NET 10 (R8); `DOTNET_EnableAVX512=0` is the effective knob for
   015's and 014's workflows, a change to their runner-class definition.
7. Candidate upstream reports: Bepu's `Simulation.Clear()` pair-cache bug (R5) and Mono AOT's
   `TriangleCylinderTester` assertion (R9).

## Delivery

The work ships as two vertical slices, each a pull request that leaves `main` green.

1. **Formats, the vehicle model, the fixture engine, Bepu on the server, and the selection machinery** on
   `016-vehicle-physics-selection`: the spec artifacts; US7 (formats, golden fixtures, baseline manifest);
   the core vehicle model; the vehicle world and scenario catalog; the fixture engine with its fault table
   and streams; US1 on the fixture engine and on Bepu's server build (with the vector guard, the member
   allowlist, Bepu's streams, the managed lock test and the zero-allocation tests); the selection
   configuration and window check (US5 scenario 6); the status table, decision rule, record, staleness
   check and gate plan on fixture reports (US3 scenarios 1 to 8, 10, 11); the license classifier and the
   NuGet collector (US2 scenario 10 on fixture inputs); the fit check on fixtures and the Rapier C#
   adapter and manifest (US6 scenarios 2 and 3); the divergence measurement on committed record pairs (US5
   scenarios 2 to 5); and the CLI commands. The existing .NET workflow runs it all; no guarded path is
   touched. spec.md lists the slice-2-only items under "Planned for slice 2: not yet built, no test on
   main".
2. **Jolt, the browser builds, the CI jobs and the decision**, on a new branch off `main` after slice 1
   merges: Jolt's adapter, shim, builds and native tests; the three browser hosts and the runner changes;
   the browser halves of US2, US4 and US5; the server benchmarks; the cargo, source, Emscripten and
   OS-toolchain collectors on real builds; the six `SimCore` jobs and their workflow tests; the selection
   run in CI; the committed report and decision record (US3 scenario 9, US1 scenario 14, SC-008); the
   patch-notes entry; and the spec.md updates above. It touches `.github/`, so it gets the independent
   automated review before the session merges it.

`tasks.md` covers both slices.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| **Analyzer exemption: MMO1001 in the Bepu adapter** (FR-004) for the `System.Numerics` vector types | Bepu's API and its callbacks (`INarrowPhaseCallbacks`, `IPoseIntegratorCallbacks`) take and return `Vector3`, `Quaternion` and `Vector<float>`-wide bundles; the adapter cannot call Bepu without them | Converting at the core boundary still needs the types in the adapter; a reflection wrapper breaks MMO1008. Every other rule, MMO1006 included, stays at Error, the backstop compiles the adapter with only this exemption, and a member allowlist test bans Bepu's vendor-dependent and double-trig members (R7) |
| **Analyzer exemption: MMO1008's native-interop members in the Jolt and Rapier adapters** (FR-004) | The adapters must call native code (`[LibraryImport]`, `NativeLibrary`) on the server and `[JSImport]` in the browser, and choose the binding with `OperatingSystem.IsBrowser()` | Putting the interop in an unanalyzed assembly would move the marshalling, which must be deterministic, outside every rule. The exemption admits only the interop members; time, randomness, hash order, `double`, FMA and locks stay at Error |
| **Native engine code on the tick path is not analyzed** (Principle II asks gameplay assemblies to enforce the no-lock rule mechanically) | Jolt is C++ and no Roslyn analyzer can read it | The mechanical check is the lock test (FR-034): an interposer counts every pthread lock, condition-variable, semaphore and futex call from the engine library over 1,000 steps, its own count is tested on a library that takes one mutex per step, and the shipped library's imported lock symbols must be none. Bepu's assemblies are checked the same way, by instrumented execution (R16) |
| **Jolt built for a single-threaded platform (`JPH_PLATFORM_BLUE` with our no-op lock header)** | Stock Jolt takes 31 uncontended locks per step in `PhysicsSystem::Update` whatever the job system, which Principle II forbids (R10) | Patching Jolt changes its sources and identity; accepting Failed (`TakesLock`) would remove the fallback engine on a technicality the header removes; interposing no-op locks would hide locks from the test. The header is Jolt's documented hook, recorded in the identity, and the lock test plus the linked-symbol check prove it |
| **Licensing: the platform-runtime reading of Principle VII** | Every `.so` the system linker builds contains GCC's `crtbeginS.o`/`crtendS.o` and glibc's startup objects, and the Jolt shim contains header-instantiated libstdc++ code (`GPL-3.0-or-later WITH GCC-exception-3.1`); the core-only .NET server already loads those libraries (R18) | A file-level reading would make every native candidate LicenseBlocked by the platform's own linker, which the spec rejects as "None by construction". A static libc++ build (Apache-2.0 WITH LLVM-exception) would remove the libstdc++ header code but not the startup objects, and is unverified. The component-level reading is the spec's; it decides nothing about Principle VII, and the question goes to the maintainer as patch-notes follow-up 1 |
