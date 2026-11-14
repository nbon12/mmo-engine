# Implementation Plan: Shared Simulation Core

**Branch**: `015-shared-simulation-core` | **Date**: 2026-11-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/015-shared-simulation-core/spec.md`

## Summary

This builds Phase 1 sub-spec 3 (nbon12/mmo-engine#12), first slice: the shared simulation core. One
C# library steps character movement, character collision, weapon timing, projectile flight and
hitboxes. It gives the same bits on the server (.NET 10, Linux x64) and in the browser (.NET
WebAssembly, in a dedicated Web Worker).

- **Core library** `MmoEngine.SimCore`. It has pure step functions over float32 state, its own
  trigonometry, three versioned binary formats, a canonical state hash and one configuration rule
  table. Its only dependency is `System.IO.Hashing`.
- **Analyzer** `MmoEngine.SimCore.Analyzers`. Nine Roslyn rules (MMO1001–MMO1009) fail the core's
  build on any construct that can make the builds disagree, or that blocks.
- **Browser build** `MmoEngine.SimCore.Wasm`. A thin `[JSExport]` host over the core's batched
  stepper and stream runner. Only bytes cross the JavaScript boundary.
- **Agreement suite**. The `simcore` CLI runs every committed recorded stream, and streams from 50
  generated seeds, through the server build. The WebAssembly build then runs them in Node (with the
  jiterpreter on and off) and in Chromium, Firefox and WebKit Workers. The CLI compares every step's
  hash and reports the first divergent step field by field. CI gates on it.
- **Measurements**. BenchmarkDotNet covers the server steps. A measurement job records the
  WebAssembly download size, startup and step cost for the interpreted build, and for the
  ahead-of-time build where the toolchain produces one. The numbers never gate. The job's agreement
  run on the relinked interpreted and AOT builds does gate, whenever those builds are produced.

The research ([research.md](research.md)) prototyped every hard part and measured it in this
sandbox. The prototypes covered:

- the math, swept exhaustively against double references;
- the character controller, run against fuzz worlds;
- the analyzer, with 81 tests;
- the WebAssembly host and its Node and Chromium Worker runners;
- the formats, weapons, spread and ballistics.

Every one gave identical bits across x64, the interpreted WebAssembly build and the AOT build.

## Technical Context

**Language/Version**:

- C# on .NET 10 (`global.json` 10.0.100, `rollForward: latestPatch`).
- JavaScript ES modules on Node 22, for the agreement and measurement runners.
- Python 3 for the coefficient generators and the workflow tests.

**Primary Dependencies**:

- Core: `System.IO.Hashing` 10.0.14 (MIT, already used). No other package; an architecture test
  enforces it.
- Analyzer: `Microsoft.CodeAnalysis.CSharp` 5.0.0 (MIT, new). It is pinned to the SDK's compiler
  (research R5).
- Analyzer tests: `Basic.Reference.Assemblies.Net100` 1.8.14 (MIT, new, tests only).
- Browser build: the `Microsoft.NET.Sdk.WebAssembly` SDK and its runtime packs (MIT, from nuget.org).
  The `wasm-tools` workload (MIT) is optional, and only the measurement job installs it.
- Server helpers and CLI: FluentValidation 12.2.0 (Apache-2.0), System.CommandLine 2.0.14 (MIT),
  Serilog 4.4.0 and `Serilog.Formatting.Compact` 3.0.0 (Apache-2.0). All are already used.
- Tests and benchmarks: xunit.v3, CsCheck (Apache-2.0), coverlet (MIT) and BenchmarkDotNet 0.15.8
  (MIT). All are already used.
- Runners: `@playwright/test` 1.63.0 (Apache-2.0, a new dev dependency of `tools/agreement/` only,
  pinned exactly, lockfile committed). Node's built-in `node:test`.
- Offline tools that are never linked or shipped: mpmath 1.4.1 and numpy (BSD), for coefficient
  generation.

**Storage**: N/A. The committed content files under `simcore/` are test fixtures and content: the
configuration, the test collision world, the recorded streams and the golden fixtures.

**Testing**:

- xUnit v3. Theories cover:
  - step rates 20, 30 and 60 Hz;
  - stances and buttons;
  - every analyzer rule;
  - every format refusal;
  - each weapon and each projectile class.
- CsCheck properties with fixed seeds (no penetration, no stuck state, step-up bound).
- Table-driven math bound tests on every run. The exhaustive sweeps are the same tests with
  `MMO_EXHAUSTIVE_MATH=1`, which only the measurement job sets (research R3).
- Zero-allocation tests (`GC.GetAllocatedBytesForCurrentThread`, server build).
- Roslyn analyzer tests with a backstop compile of the real core sources.
- Golden-fixture round trips.
- The `node:test` and Playwright agreement runs.
- Python `unittest` for the new workflow.

**Target Platform**:

- The library runs in process on Linux x64, for the continent server and the bots. x64 is the
  reference.
- .NET WebAssembly runs in a dedicated Web Worker (Chromium, Firefox, WebKit) and in Node 22.
- CI runs on GitHub-hosted `ubuntu-latest`.

**Project Type**: A shared gameplay library with its analyzer and browser host, plus tooling (CLI,
runners, benchmarks).

**Components Touched**: The shared simulation core, which the continent server, the bots and the
browser client will use. No running component changes in this slice. Sub-specs 5, 6 and 8 consume
the core.

**Roadmap Phase**: Phase 1 (one region per server), sub-spec 3, first slice. The vehicle-physics slice
follows separately.

**Performance Goals**:

- The movement step stays well inside the region tick. The prototype measured 2.6–10.1 µs per
  character step on x64 (sandbox, research R8), which is 2.6–10.1 ms for 1,000 characters against
  33 ms at 30 Hz.
- One predicted character per browser frame: about 14 µs AOT and about 95 µs interpreted (prototype
  measurements), against a 16.7 ms frame.
- The agreement suite completes in under 10 minutes in CI (SC-006). The job's own
  `timeout-minutes: 10` enforces it.

**Constraints**:

- The determinism rules (FR-012 to FR-017), enforced by the analyzer and checked by the agreement
  suite.
- Zero managed allocation per step after warm-up (FR-032).
- No locks or blocking in the core (Principle II).
- Every loop has a fixed bound (FR-005).
- No I/O, logging or clock reads in the core.
- The WebAssembly download size is measured, not gated: the core adds tens of KiB to a runtime of
  about 0.9–1.4 MiB brotli.

**Scale/Scope**:

- World capacity is configurable up to 1,024 characters and 16,384 projectiles. Tests and streams use
  up to 64 characters and 1,024 projectiles.
- 18 committed named streams plus 50 generated seeds.
- Collision files up to 256 MiB, with at most 64 triangles in a grid cell.
- Five `src/` projects, three test projects, one benchmark project and one Node tool folder.

## Constitution Check

*GATE: passed before Phase 0 research; re-checked after Phase 1 design (below, all still pass).*

- **Component and stack fit** (I, XII): Passes. The core is the "shared simulation" library named in
  the architecture: C#/.NET on the server, and .NET WebAssembly in a Web Worker for the TypeScript
  browser client. No web-surface stack is used, and no real-time path adopts web hosting.
- **Region ownership and single writer** (II, III): Passes. The core owns no state. Every step
  function takes state in and returns state out, so the owning region (sub-spec 5) or the client's
  Worker stays the single writer. Effects inside one step apply in a fixed order: inputs, then
  movement in ascending entity ID, weapons in ascending ID, hitboxes, and projectiles in ascending ID
  ([data-model.md](data-model.md)). The core takes no locks. The analyzer (MMO1006) bans them, along
  with every blocking primitive.
- **Interaction classes and claims** (III): Passes. Movement and collision are instant interactions
  of the owning region. Projectile flight is the trace behind instant hits and deferred hand-overs.
  No claim, claim radius, portal or bridging change.
- **Regionizer invariants**: N/A. The core has no region data, and merges and splits don't touch it.
- **Tick budget and performance** (VI): Passes. Costs are stated and labelled (see Tick Budget and
  Evidence). Steps don't allocate, and buffers are owned by the world and sized from the configured
  maxima. Benchmarks (FR-030) and zero-allocation tests (FR-032) are planned for every step function.
  The core does no I/O. A degraded region at 20 Hz calls it with the 20 Hz step length.
- **Authority, netcode and trust** (IV): Passes. The server stays authoritative, and the client runs
  the same code to predict. Every input command is decoded with strict length and version checks,
  then validated before it affects state: ranges, known bits, sequencing and weapon slot (FR-019,
  [contracts/input-command-format.md](contracts/input-command-format.md)). Wire size and rate limits
  belong to sub-specs 4 and 7. Lag compensation is sub-spec 6, and the hitbox builder is its input.
- **Persistence and ownership** (V): N/A. Nothing is persisted.
- **Contracts and protocols**: Passes. Three versioned binary formats each begin with a format
  identifier and version and have golden fixtures. The canonical state serialisation is documented
  ([contracts/](contracts/)). The input command's single source of truth is the C# definition, which
  the browser runs in WebAssembly. The `.bin` and `.json` golden pairs are the reference for any
  future TypeScript decoder. Version negotiation and packet framing stay with the transport.
- **Licensing and clean room** (VII): Passes. Every new dependency is MIT or Apache-2.0. The math,
  collision and ballistics come from papers, textbooks and permissively licensed documentation, all
  listed in [research.md](research.md#design-sources). No GPL, LGPL or AGPL code was read or copied;
  Quake's slide move was deliberately not consulted. The coefficient generator is our own, run offline
  with BSD tools that are never linked.
- **Evidence and open decisions** (VI): Passes. No tuning value is set. Movement, weapon, projectile
  and hitbox values are placeholders in validated configuration
  ([contracts/config-rules.md](contracts/config-rules.md)). Open decision 3 (tick rate) stays open:
  the step length is configuration, tested at 20, 30 and 60 Hz. Open decision 5 (hit detection)
  stays open: the projectile trace and hitboxes serve both server-simulated and client-reported hits,
  and neither claims nor their validation is built. Open decisions 4 and 9 are untouched.
- **Operations**: Passes for a library and tools. The core validates its configuration with the
  dependency-free rule table (`ConfigGuard`) before any step. On the server, `SimConfigValidator`
  (FluentValidation) applies the same table and fails fast. The browser build's equivalent guard is
  `LoadConfig`. The `simcore` CLI binds and validates its options and the environment name
  (`DOTNET_ENVIRONMENT`, the known set, kept equal to the simulator's by a test), exits 2 on invalid
  configuration and logs Serilog compact JSON to stderr. Sentry, OpenTelemetry, the Agones SDK and
  health endpoints are N/A: no service runs. The core logs nothing; step reports carry reason codes
  for the caller to log.
- **Security**: Passes. Input commands, collision files and streams are untrusted. The decoders bound
  every count before allocating, check lengths in checked 64-bit arithmetic, verify checksums, and
  never return a partial result. Authentication and authorization are N/A: the core is a library.
  The local test server binds 127.0.0.1 only and guards against path traversal.
- **Testing discipline** (VIII): Passes. Tests are written first, in red-green order in `tasks.md`.
  Simulation tests run on a fixed step length and seed and advance the steps each scenario implies.
  Data variations are Theories. Tests are pure and in process. The WebAssembly runs use unique
  temporary directories and dynamic ports, so they are safe in parallel and on rerun. Persistence and
  Testcontainers are N/A. Parallel-equivalence tests are N/A, because the core has no parallel phase.
- **CI/CD and documentation**: Passes. The existing .NET workflow builds, formats and tests every new
  project, and its 95% changed-file coverage gate applies. The WebAssembly host is covered through a
  direct project reference (research R18). A new `SimCore` workflow runs the agreement suite as a
  gating check and runs the measurement job. A patch-notes entry ships with each PR. Sonar and
  Codecov still wait on the owner's accounts, as before.
- **Executable and living specs** (IX): Passes. Every acceptance scenario and Independent Test maps
  to a named automated test in `tasks.md`. The spec was updated in this phase so that US3 and FR-012
  match the analyzer design: a horizontal `Vector128` sample for scenario 1, fused operations under
  the FMA rule, and the double, float-to-integer and platform-path rules.
- **Spec independence and parallelism** (X): Passes. There is no hard dependency on another spec.
  Sub-specs 4–9 depend on this one. The vehicle-physics slice depends on this slice's collision world
  and agreement suite.
- **Incremental delivery** (XI): Passes. It is a focused Phase 1 slice in two PRs (see Delivery).
- **Autonomous delivery** (XIII): Passes. Every check is a test or a CI job. Expected hashes change
  only through `simcore streams record --reason`, which an agent runs and commits. No step needs a
  person.

## Tick Budget and Evidence

All numbers are from this session's prototypes on the sandbox (4 vCPU Xeon at 2.3 GHz, .NET SDK
10.0.115, Node 22). They are labelled as measurements on the sandbox, not on a CI runner class. The
benchmark project replaces them with runner-class measurements (FR-030, SC-007).

| Work item | Estimate or measurement | Source | Share of the region tick budget |
|-----------|-------------------------|--------|---------------------------------|
| Movement step, flat run | measurement (sandbox): 2.6 µs per character | research R8, prototype, 200,000 steps | 1,000 characters: 2.6 ms, 7.9% of 33 ms |
| Movement step, wall slide | measurement (sandbox): 3.7 µs | research R8 | 1,000: 3.7 ms, 11% |
| Movement step, wedged in a corner (worst case seen) | measurement (sandbox): 10.1 µs | research R8 | 1,000: 10.1 ms, 31% (a pathological case for all players at once) |
| Movement step, fuzz world | measurement (sandbox): 2.9 µs | research R8 | 1,000: 2.9 ms, 8.8% |
| Weapon step | estimate: < 0.2 µs per weapon (integer state, no floats except spread) | estimate | < 1% |
| Spread direction per shot | estimate: about 40 ns (two sine-cosine pairs at about 19 ns each, measured in R3, plus a square root) | estimate from research R3 | < 0.1% |
| Projectile step | estimate: 1–3 µs per projectile (segment query plus bounding-sphere-culled capsules) | estimate | 1,000 projectiles: 1–3 ms, 3–9% |
| State hash | measurement (sandbox): 129 ns per 512 B on x64 | research R13 | Test and reconciliation tool only; whether production ticks hash is sub-spec 6's decision |
| Browser: movement step per frame | measurement (sandbox): AOT about 14 µs, interpreted about 95 µs | research R8, R17 | 0.08% or 0.6% of a 16.7 ms frame |

- **Allocations**: The world owns every buffer: the canonical-state buffer, the collision query
  span, the hitbox span and the event lists, all sized from the configured maxima. Steps write into
  caller-owned spans and fixed-capacity arrays. The core has no LINQ, lambdas, boxing or static
  mutable state. Zero-allocation tests run 1,000 steps after warm-up, for each of the movement,
  weapon, projectile and full-world steps, and assert 0 bytes (US7.3, FR-032, SC-005).
- **Degradation**: The core has no Delivery work. A region that drops to 20 Hz passes the 20 Hz step
  length; the 20 Hz streams and tests cover that.
- **Metrics**: None added. Step reports expose counters (sweeps, slide-cap hits, fire blocks) for
  sub-spec 5 to export.

## Project Structure

### Documentation (this feature)

```text
specs/015-shared-simulation-core/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── input-command-format.md
│   ├── collision-data-format.md
│   ├── recorded-stream-format.md
│   ├── state-serialization.md
│   ├── config-rules.md
│   ├── analyzer-rules.md
│   ├── wasm-api.md
│   ├── simcore-cli.md
│   └── measurement-report.md
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/MmoEngine.SimCore/                    # the core; BCL + System.IO.Hashing only; analyzer attached
├── SimCoreVersion.cs                     # RulesRevision, written into every recorded stream
├── Math/                                 # SimMath; generated SimMathCoefficients.g.cs, TwoOverPi.g.cs
├── Geometry/                             # Vec3, ClosestPoints, SegmentCapsule
├── Collision/                            # CollisionWorld, Heightfield, MeshSet, UniformGrid, CapsuleSweep, SweepHit
├── Movement/                             # CharacterState, MovementStep, MovementOutcome
├── Weapons/                              # WeaponState, WeaponStep, Spread, FireBlock, WeaponPhase
├── Projectiles/                          # Projectile, ProjectileStep, Ballistics, ProjectileHit, ExpiryReason
├── Hitboxes/                             # HitboxLayout, HitboxBuilder, Hitbox
├── Inputs/                               # InputCommand, InputCodec, InputValidator, InputRejection, Buttons
├── Formats/                              # FormatError, CoreFormatException, CollisionFile, RecordedStream, StateSerializer
├── Config/                               # SimConfig, ConfigFields, ConfigRelations, ConfigGuard, CompiledConfig, ConfigBlock
├── World/                                # SimWorld, StepReport, StepFault, StepEvents
├── Batch/                                # BatchStepper, StreamRunner, StepRecord, StepFaultException, CrossBuildTables
└── Hashing/                              # StateHash
src/MmoEngine.SimCore.Analyzers/          # DeterminismAnalyzer (MMO1001–MMO1009), release-tracking files
src/MmoEngine.SimCore.Wasm/               # Microsoft.NET.Sdk.WebAssembly host: SimCoreExports ([JSExport]), Program.cs
src/MmoEngine.SimCore.Server/             # SimConfigJson, SimConfigValidator (FluentValidation), SimEnvironment,
                                          #   TestWorldGenerator, ScenarioCatalog, StreamGenerator, DivergenceReport,
                                          #   TablesComparison
src/MmoEngine.SimCore.Cli/                # `simcore`: content, golden, streams, agreement, measure, config
tests/MmoEngine.SimCore.Tests/            # core, server-helper and CLI tests
├── Architecture/                         # dependency and wiring tests (BCL + System.IO.Hashing only)
├── Math/  Collision/  Movement/  Weapons/  Projectiles/  Hitboxes/  Inputs/  Formats/  Config/
├── World/  Batch/
├── Benchmarks/                           # the dry-run export test (references the benchmark project)
├── Scenarios/                            # US1, US4, US5 acceptance scenarios at 20/30/60 Hz
├── Properties/                           # CsCheck, fixed seeds
├── Allocation/                           # zero-allocation tests (serial collection)
├── Agreement/                            # streams verify, compare, divergence report, one-ulp perturbation,
│                                         #   tables comparison
└── Cli/                                  # command tests; Fixtures/measurement-report.json
tests/MmoEngine.SimCore.Analyzers.Tests/  # rule Theories, backstop compile of src/MmoEngine.SimCore, wiring Fact
tests/MmoEngine.SimCore.Wasm.Tests/       # calls the exports directly (coverage of the host)
benchmarks/MmoEngine.SimCore.Benchmarks/  # step, hitbox and hash benchmarks; SimCoreMeasurementExporter (p99)
tools/agreement/                          # package.json + lock, playwright.config.mjs, node/, specs/, web/,
                                          #   lib/ (runner, server, tables.mjs, measure.mjs)
tools/simcore/                            # gen_coeffs.py, gen_twoopi.py, emit_cs.py, requirements.txt
simcore/                                  # config/default.json, content/test-world.mmcd, streams/*.mmrs,
                                          #   streams/REGENERATIONS.md, golden/{input,collision,stream}/, README.md
.github/workflows/simcore.yml             # SimCore: agreement (gating) and measure jobs
tests/workflows/test_simcore_workflow.py  # pins, triggers, timeout, browsers
.gitattributes                            # binary content types
```

**Structure Decision**: The core is one library with no dependencies beyond `System.IO.Hashing`, so it
trims into the WebAssembly download and has a single assembly for the analyzer to guard. Code that
only the server side needs lives in `MmoEngine.SimCore.Server`, which the continent server will reuse:

- JSON configuration binding and the FluentValidation adapter;
- the environment-name check;
- the content and stream generators;
- the divergence report.

The browser host holds only the `[JSExport]` delegations, which keeps unsafe interop code out of the
analyzed assembly. The CLI is a thin System.CommandLine shell, matching the repository's other CLIs.
The regionizer's regex banned-API scan stays as it is. Moving it onto the analyzer is a later
follow-up, because the regionizer computes in doubles.

### Decisions made during design

The design documents settled these points, which the spec leaves open. They are recorded here so
`tasks.md` and the implementation follow one answer:

- **Angle rounding.** Degrees convert to binary-angle units with `SimMath.RoundToInt` (ties to even),
  not `floor(x + 0.5)`, which rounds 0.49999997f up ([contracts/config-rules.md](contracts/config-rules.md)).
- **Collision query buffer.** The world owns it, sized from the compiled configuration and the loaded
  world. A query that would overflow it refuses the step with `QueryCapacityExceeded`; it never
  truncates.
- **Math sweeps.** The exhaustive trig sweeps are xUnit tests that run only with
  `MMO_EXHAUSTIVE_MATH=1`, so slice 1 needs no workflow change. The fixed table gates every run.
- **Golden fixtures.** In golden JSON, 64-bit integers (seeds, salts, checksums, hashes) are strings
  of 16 lowercase hex digits without a prefix, the `x16` form the reports print. Other bit patterns
  (float `bits`, a config entry's `fieldId` and `valueBits`) are uppercase `0x` hex strings
  ([contracts/input-command-format.md](contracts/input-command-format.md#shared-fixture-rules)).
  `simcore golden write` produces the `.bin` files, and a test checks it reproduces the committed ones.
- **Build names.** D9's builds plus `node-interp-nojit` (the jiterpreter off, in the agreement job)
  and `node-interp-relinked` (the measurement job). `node-interp` means the jiterpreter is on.
- **Stream outcomes.** Match, Diverged, NotComparable and Refused. Refused and NotComparable exit 2,
  a divergence exits 1, and a divergence wins when both occur ([contracts/simcore-cli.md](contracts/simcore-cli.md)).
- **Measurement gating.** The numbers never gate. The measurement job's agreement on the relinked
  interpreted and AOT builds gates when they are produced, because a divergence there is a real one.
- **The golden worked state is not a valid start position.** Its capsule overlaps a `tiny-world`
  triangle by about 23 mm. The stream fixtures that use it are format fixtures and are never run.
- **Cross-build tables.** The streams reach only part of `SimMath` (aim through `SinCosBinaryAngle`,
  `Sin` and `Cos` at a few inputs while the configuration compiles) and never compare hitboxes, which
  are not hashed. So the fixed trig table and a hitbox pose table are a second agreement artifact:
  `CrossBuildTables` in the core builds and evaluates them, the `EvaluateTables` export runs them in
  each WebAssembly build, and `agreement compare` diffs the result bits row by row (US3 scenario 8,
  SC-004, US5 scenario 5). The math rows are the same fixed table the bound tests check. A row whose
  server result is NaN matches any NaN, because NaN bits are not portable (research R4); every
  in-domain row compares bit for bit ([contracts/wasm-api.md](contracts/wasm-api.md#cross-build-tables)).
- **US1 scenario 1's distance.** The spec states the distance the controller covers from rest, the
  discrete sum Σₖ min(k·a·dt, v)·dt within 1e-4 m, with the per-rate values. float32 accumulation
  alone is about 5.4e-6 m off at 60 Hz, so 1e-4 m leaves room for the controller's other rounding. Its earlier tolerance,
  "within one step's acceleration distance", held only at 20 Hz.
- **The heat test weapon.** The rifle with `heatPerShot` 10000, so it overheats on the 10th shot of a
  30-round magazine; every other value is the rifle's. The `heat-overheat` stream's exact steps are in
  [contracts/simcore-cli.md](contracts/simcore-cli.md#named-scenario-streams).
- **Generic-math conversions and enum casts.** MMO1009 also reports the integer types'
  `CreateTruncating`, `CreateSaturating` and `CreateChecked` with a floating-point source, and a
  float-to-enum cast, matched by the enum's underlying type ([contracts/analyzer-rules.md](contracts/analyzer-rules.md#rules)).
- **Attribute applications are not analyzed as code.** Operations under an attribute application are
  metadata, never run by a step, so the operation rules skip them. Otherwise the SDK-generated
  `System.Reflection` assembly attributes would fail the core's build under MMO1008.
- **Slide rules.** An airborne character that falls onto a steep face clips against the true contact
  normal and slides down it, an airborne walkable hit clips against its normal, and the crease and
  three-plane stop apply only when the clipped velocity would move back into an earlier plane
  ([data-model.md](data-model.md#movement-step), research R8).
- **US7 scenario 1 in slice 1.** A dry-run test runs the server benchmarks in process and checks the
  exporter's `server` object with the report validator's rules, so the slice that claims US7.1 tests
  it. The full benchmark run stays in slice 2's `measure` job.

## Delivery

The work ships as two vertical slices. Each is a PR that leaves `main` green.

1. **Core, analyzer, formats and server-side agreement** on `015-shared-simulation-core`. This covers
   the spec artifacts, US1, US3, US4, US5 and US6, except the cross-build halves of US3 scenario 8
   and US5 scenario 5 (below), plus:
   - the server benchmarks, their dry-run export test, and the zero-allocation tests (US7.1, US7.3);
   - the `simcore` CLI, including the server side of the cross-build tables (`CrossBuildTables`,
     `agreement prepare` writing them and `agreement compare` diffing them);
   - the committed content and streams;
   - `streams verify`, which reruns every committed stream in a fresh process.

   The existing .NET workflow builds and runs all of it; only the full benchmark run waits for
   slice 2's `measure` job. No guarded path is touched.
2. **WebAssembly build, agreement suite and measurement job**, on a new branch off `main` after
   slice 1 merges. This covers US2 and US7.2, and the cross-build halves of US3 scenario 8, SC-004
   and US5 scenario 5, which need a WebAssembly build:
   - the Wasm host (including the `EvaluateTables` export) and its tests;
   - `tools/agreement/`, including the tables check in both runners;
   - `.github/workflows/simcore.yml`, its workflow test, and the `auto-merge.yml` listener.

   It touches `.github/`, so it gets the independent automated review before the session merges it.

`tasks.md` covers both slices. Slice 1's PR description lists slice 2 as the follow-up.

## Complexity Tracking

No violations.
