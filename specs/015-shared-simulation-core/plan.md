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
  jiterpreter on and off) and in a Chromium Worker, plus Firefox and WebKit Workers when Playwright
  can install them (otherwise reported NotRun, never as a pass; research R16). The CLI compares every
  step's hash and reports the first divergent step field by field. CI gates on the required builds
  (Node and Chromium).
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
- The WebAssembly download size is recorded, not gated. Measured on the sandbox with toy kernels
  (research R17): 888 KiB brotli for the relinked interpreter, 1,377 KiB for the prebuilt
  interpreter and 1,588–1,663 KiB for AOT. The core's own share (tens of KiB) is an estimate.

**Scale/Scope**:

- World capacity is configurable up to 1,024 characters and 16,384 projectiles. Tests and streams use
  up to 64 characters and 1,024 projectiles.
- 19 committed named streams plus 50 generated seeds.
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
- **Regionizer invariants**: N/A. The core defines no region data. `SimWorld` is the composition for
  the client Worker, the agreement suite, tests and bots. A server region keeps its entities in its own
  storage (sub-spec 5), and that sub-spec defines the merge and split callbacks, continent-unique IDs
  (including the next projectile ID handed to the weapon step) and the `LaunchStep` offset on a merge.
- **Tick budget and performance** (VI): Passes. Costs are stated and labelled (see Tick Budget and
  Evidence). Steps don't allocate, and buffers are owned by the world and sized from the configured
  maxima. Benchmarks (FR-030) and zero-allocation tests (FR-032) are planned for every step function.
  The core does no I/O. The per-entity steps take one entity's state in and write it out, and impose
  no storage layout, so a region's structure-of-arrays storage can call them; `StepCaseTests` (T245)
  checks drivers that call them directly against `SimWorld` runs. `SimWorld`'s struct arrays are not
  region storage. The core runs at 20 Hz when configured for it (the 20 Hz streams and
  tests). A live 30 to 20 Hz drop needs a state conversion between rates, which this feature does not
  provide; it is a follow-up for sub-spec 5 (spec Assumptions).
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
  That includes the zero-allocation tests: each entry point's allocation test is a red task in the
  phase that builds the entry point, before its green task, and Phase 9's `StepCase`-driven test is
  the regression across all of them.
  Simulation tests run on a fixed step length and seed and advance the steps each scenario implies.
  Data variations are Theories. Tests are pure and in process. The WebAssembly runs use unique
  temporary directories and dynamic ports, so they are safe in parallel and on rerun. Persistence and
  Testcontainers are N/A. Parallel-equivalence tests are N/A, because the core has no parallel phase.
- **CI/CD and documentation**: Passes. The existing .NET workflow builds, formats and tests every new
  project, and its 95% changed-file coverage gate applies. The WebAssembly host is covered through a
  direct project reference (research R18). A new `SimCore` workflow runs the agreement suite as a
  gating check and runs the measurement job. A patch-notes entry ships with each PR. Sonar and
  Codecov still wait on the owner's accounts, as before.
- **Executable and living specs** (IX): Passes at each merge. Every acceptance scenario and
  Independent Test maps to a named automated test in `tasks.md`. Slice 1's spec.md states only slice-1
  behavior as built and lists the WebAssembly items under "Planned for slice 2: not yet built, no test
  on main", with no claim of current behavior (T264). Slice 2 moves them back with their tests
  (T316). The spec was updated in this phase so that US3 and FR-012 match the analyzer design: a
  horizontal `Vector128` sample for scenario 1, fused operations under the FMA rule, and the double,
  float-to-integer, `ulong`-to-`float` and platform-path rules.
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
  mutable state. Zero-allocation tests run three batches of 1,000 steps after warm-up, for each of
  the movement, weapon, projectile and full-world steps, and assert that the smallest batch is 0 bytes
  (US7.3, FR-032, SC-005).
- **Degradation**: The core has no Delivery work. The core runs at 20 Hz when configured for it (the
  20 Hz streams and tests). A live 30 to 20 Hz drop needs a state conversion between rates, which this
  feature does not provide; it is a follow-up for sub-spec 5 (spec Edge Cases and Assumptions).
- **Metrics**: None added. The per-entity steps return their outcomes and counters (sub-moves, sweeps,
  slide-cap hits, fire blocks) for sub-spec 5 to aggregate and export. `StepReport` sums them for
  `SimWorld`'s callers.

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
└── Hashing/                              # StateHash, SplitMix64
src/MmoEngine.SimCore.Analyzers/          # DeterminismAnalyzer (MMO1001–MMO1009), release-tracking files
src/MmoEngine.SimCore.Wasm/               # Microsoft.NET.Sdk.WebAssembly host: SimCoreExports ([JSExport]), Program.cs
src/MmoEngine.SimCore.Server/             # SimConfigJson, SimConfigValidator (FluentValidation), SimEnvironment,
                                          #   TestWorldGenerator, ScenarioCatalog, StreamGenerator, DivergenceReport,
                                          #   TablesComparison, StreamComparison, StateFieldReader, Golden/
src/MmoEngine.SimCore.Cli/                # `simcore`: content, golden, streams, agreement, measure, config;
                                          #   Commands/, Agreement/, Measure/
tests/MmoEngine.SimCore.Tests/            # core, server-helper and CLI tests
├── Architecture/                         # dependency and wiring tests (BCL + System.IO.Hashing only)
├── Math/  Collision/  Movement/  Weapons/  Projectiles/  Hitboxes/  Inputs/  Formats/  Config/
├── World/  Batch/  Hashing/
├── Support/                              # shared fixtures: TestConfigs, TestWorlds, TestStates, references
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
tools/agreement/                          # package.json + lock, playwright.config.mjs, node/, specs/,
                                          #   lib/ (runtime.mjs, server.mjs, tables.mjs, passes.mjs, measure.mjs,
                                          #   measure-node.mjs), web/ (page.html, worker.mjs, measure.html,
                                          #   measure-worker.mjs)
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
- **Build names.** Research R16 and [contracts/simcore-cli.md](contracts/simcore-cli.md#build-names)
  define the builds. Besides the `server` reference, the agreement job runs `node-interp` (jiterpreter
  on), `node-interp-nojit` (jiterpreter off) and `chromium-worker`, `firefox-worker` and
  `webkit-worker`. The measurement job runs `node-interp-relinked` and `node-aot`.
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
- **Slice-2 items in spec.md at slice 1's merge.** Slice 1's polish task (T264) moves every item only
  slice 2 builds into a spec.md section headed "Planned for slice 2: not yet built, no test on main",
  keeping each item's ID, and slice 2 (T316) moves them back once their tests exist. This keeps spec.md
  truthful on `main` (Principle IX). Splitting them into a separate sub-spec was rejected: `tasks.md`
  covers both slices, the spec's Input and scope name them, and sub-specs 4–9 depend on this spec.
- **A live step-rate change is out of scope.** A world's step rate is fixed for its life, and its
  heat, fire wait, phase and delay counters and launch steps are stored in that rate's units. The core
  ships no conversion of a live state between rates; the 30 to 20 Hz degradation that needs it is a
  follow-up for sub-spec 5. Building the conversion here would add scope that no scenario of this
  spec asks for, and it does not touch open decision 3.
- **US7 scenario 1 in slice 1.** A dry-run test runs the server benchmarks in process and checks the
  exporter's `server` object with the report validator's rules, so the slice that claims US7.1 tests
  it. The full benchmark run stays in slice 2's `measure` job.
- **The over-the-ridge shots of US5's Independent Test.** The heavy class clears the ridge and lands
  beyond it. The small-arms class has no clear-and-land row: a 600 m/s shot from eye height that
  clears a 3 m crest cannot come down within its 600 m range, so its ridge-clearing row asserts the
  Range expiry at the closed-form step instead, and both classes keep a row that hits the ridge face
  (T205). Putting a small-arms shooter on the ridge's slope was rejected: it no longer fires "from a
  shooter" on the ground as the other placements do. The projectile test worlds span ±512 m, so a
  level small-arms shot lands on terrain (US5 scenario 1, T200).
- **US5's "run the same shots through the agreement suite" gets its own committed stream.**
  `shots-at-known-distances` fires both classes on the test world at a target 25 m away on flat
  terrain, at a target behind the wall, into the ridge face and over the ridge; it makes 19 named
  streams (69 with the generated ones). T205's own placements stay in process on the projectile test
  worlds, which no committed stream uses. Adding the shots to `launcher-arc` was rejected because
  that stream backs US5 scenario 1's single-shot assertion and is the measurement's projectile step;
  adding them to `mesh-occludes-shot` was rejected because it would stretch that stream past what
  its name and US5 scenario 3 say, and still leave the heavy class's target shot uncovered. Over the
  ridge, the test asserts against the double reference shot by shot (hit, or Range expiry) rather
  than a fixed outcome, because the test world beyond the ridge is not shaped for these shots.
- **Embedded input errors are reported from Phase 2.** `RecordedStream.Decode` already decodes each
  step's commands with `InputCodec.TryDecode` in Phase 2, so it re-reports a refused command there
  (format `stream`, file offset, `steps[i].commands[c].input.<field>`); Phase 8 adds the step-number,
  command-count and index checks around it. Deferring the re-report to Phase 8 would leave Phase 2 to
  Phase 7 with a decode whose refusal is undefined, and US2 scenario 5's "an embedded command version
  is refused, not not comparable" test could not pass in Phase 4.
- **The analyzer tables are pinned by behaviour.** `TableTests` has one sample per entry the analyzer
  contract names, allowed or banned, and asserts its exact rule. Comparing the internal `Tables` sets
  with transcribed copies was rejected: several rules are patterns in code (the intrinsics namespace,
  `float.Is*`, `*Estimate`, `FusedMultiply*`), so a set comparison would pin the implementation's
  layout rather than the rules, and it would need `InternalsVisibleTo` on the analyzer.
- **Phase 6 follows Phase 5.** The weapon phase's checkpoint runs Phase 5's backstop over the new
  weapon code, and every phase shares one test assembly where red means a compile failure, so Phase 6
  starts once Phase 5's checkpoint is green; Phase 8 can still run beside Phase 5.
- **The test world's layout constraints are the cross-phase ones only.** `TestWorldGenerator` keeps
  a clear firing lane in front of the flat-run start: no mesh and no ramp, plateau, ridge or cliff at
  z ≥ start.z − 1, the ground within 0.5 m of the start's height, and at least 100 m of heightfield
  in +Z, past the launcher's longest pitch-0 ground range (about 83 m). `rifle-burst` (Phase 6) and
  `launcher-arc` (Phase 7) fire along it, and the Phase 3 generator test checks it on the decoded
  data alone, so a later phase cannot find its lane blocked. The start's x is at least 1 m, so the
  flat runs' `position.x` stays positive and the one-ulp perturbation (Phases 4 and 11) adds exactly
  1 to its raw bits, in C# and in JavaScript alike. The ledge is 4 m deep so the
  `crouch-under-ledge` schedule (Crouch released at 1.5 s, 4.5 s in all) releases under it and stands
  up before the run ends. The wall pair (two starts 25 m apart across the wall) and the ridge shot
  start and crest, which Phase 7's `mesh-occludes-shot` and `shots-at-known-distances` use, are named
  in `TestWorldGenerator.Features` and checked by the same Phase 3 test. Wall and corner sizes are
  left to the Phase 3 tests that already force them.
- **Fuzz worlds start clear, not small.** The movement properties constrain only the start state:
  each character starts at rest with its standing capsule at least capsuleRadius + contactSkin from
  every triangle and boundary plane, Grounded only on a walkable down-probe hit, and samples that
  cannot place a character are filtered out. A character that starts clear cannot become `Stuck`, so
  `NeverGetsStuckOnAFuzzWorld` then depends only on the controller. Pinning mesh counts and sizes was
  rejected because it narrows the worlds the properties cover; CsCheck's default 100 iterations per
  rate keeps the run time known.
- **Only the CLI checks a collision file's size before reading it.** `streams record`, `streams
  verify` and `agreement prepare` load `--content` through one helper, `ContentFiles.LoadAll`, which
  refuses a file over 256 MiB from its length (`Oversized`, offset 8, `fileLength`) before opening
  it (simcore-cli.md#content-files). The test helpers (`RepoPaths`) get no such guard: tests read
  only committed or generated files of known size, so the guard would protect nothing there, and
  `CollisionFile.Decode`'s own limit checks still run on every buffer.
- **`streams record`'s fault path is tested through a CLI-side runner parameter.** A valid
  configuration cannot make a step fault, so `Program.Run` has an internal overload taking
  `runStream` (production: `StreamRunner.Run`), and `streams record` runs every stream through it.
  The test passes a runner that throws a `StepFaultException` built from
  `SimWorld.StepWithInjectedBits`. A fault parameter on the public `StreamRunner`, a mutable static
  or delegate field, and `InternalsVisibleTo` from the core to the CLI were rejected: each would put
  a test hook into production code paths or widen the core's internals beyond the test project.
- **Analyzer messages use Roslyn's error-message display formats.** `{0}` for a symbol is
  `SymbolDisplayFormat.CSharpShortErrorMessageFormat` (`MathF.Sin(float)`, the form the compiler's
  own diagnostics use), and a type in `{1}` is the default `ToDisplayString()`, namespace-qualified
  (`System.Collections.Generic.Dictionary<int, int>`), so a hash-ordered type is unambiguous; the
  other operations use a fixed keyword or their source text (analyzer-rules.md#messages). The
  quickstart's MMO1004 line is asserted verbatim by `BuildTests.ABuildWithAViolationFails`.

### Decisions made during implementation

- **T018's companion test is a Theory, not a Fact.** `EveryEnumHasExactlyTheContractMembersAndUnderlyingType`
  takes one row per enum (member count, underlying type, `[Flags]`), because the repo's rule is that
  data variations are Theories, never loops inside a Fact; it also asserts that the non-flags enums
  are not `[Flags]`.
- **T015 checks static auto-property backing fields.** Skipping every compiler-generated field, as the
  task first said, would let a settable static auto-property (`<Name>k__BackingField`) pass, which is
  exactly the static mutable state the test exists to refuse. The test skips compiler-generated types
  and fields except those backing fields.
- **The Payne–Hanek path keeps a tail, and takes π/2 from the Cody–Waite parts (T045).** After the
  96-bit window of 2/π, the fraction is multiplied by π/2 in Q62 fixed point, which is the exact sum
  of the four pinned Cody–Waite parts rather than a second, unpinned π/2 constant, and rounded once to
  float; the next 24 bits go into the kernels as the tail, as on the Cody–Waite path, because R3 asks
  for no more than one rounding of r and a dropped tail costs accuracy for nothing. R3's prototype
  measured 1.550 / 2.072 ulp on this path; with the tail the exhaustive sweep gives 0.804 / 1.317,
  inside the same bounds. research.md R3's measured maxima now come from this
  implementation, and T164 cites them.
- **"Degree 5" for asin and the atan degree are read in x² (T042, T045).** asin(x) ≈ x + x·R(x²) with
  R(z) = z·(A0 + … + z·A4), degree 5 in z (a degree-5 polynomial in x would be ~3e-4 off at ½), and
  atan(x) ≈ x − x·Q(x²) with Q of degree 6 in z, which R3 left open; its fit error is 0.001 ulp. The
  generator also emits 2/π and the hi/lo pairs of π/2, π/4, π, atan(½), atan(3/2) and 3π/4, so every
  irrational constant SimMath uses is generated and pinned by `SimMathTests.CoefficientsHaveTheirPinnedBits`.
- **The guard lists every shape error, then stops (T049).** "Any shape error ends the check" could
  also be read as stopping at the first one. `ConfigGuard.Check` lists them all, in section order, so
  one run shows everything that is missing, and then runs no field or relation check. A null
  `weapons`, `projectileClasses` or capsule list counts as a list with no entries and gets the count
  message, because the shape table gives those lists no `is required` row.
  contracts/config-rules.md#check-order-and-the-error-list and T049 now say so, and T020's
  `ShapeErrorsEndTheCheck` rows `world-and-input-null`, `weapons-null` and `standing-null` pin both.
- **`ConfigRuleTableTests.OutOfRange` boxes each value as its own type (T020, T049).** Its switch arms
  were tuples of `int`, `float` and `uint`, so the switch's natural type was `(float, string)` and the
  Int and Enum values reached their properties as boxed floats, which reflection refuses. Each arm now
  casts its value to `object`; the cases and the expected messages are unchanged.
- **`Vec3` has bitwise value equality (T046).** T051's state and event structs have value equality and
  hold `Vec3`s. Comparing component bits (−0 differs from +0, a NaN equals itself) is the canonical
  state's own rule, and it avoids `ValueType.Equals`' reflection; the hash mixes the bits with no
  per-process seed.
- **Phase 2's decoders refuse a block that overruns, instead of throwing a slice exception (T058,
  T059).** Checks 5, 6 and 12 are Phase 8's (T235, T236), so in Phase 2 a checksum-valid file whose
  counts or block lengths disagree with its length reaches the parse. Its slices are bounds-checked
  and refuse with `CoreFormatException`, which is what the decoders' API promises, rather than letting
  an `ArgumentOutOfRangeException` escape: a collision slice past the content is `Inconsistent` at 8
  `fileLength` with the `CountsNeed` detail, a stream block is `Inconsistent` at 56 `fileLength`
  (`the blocks need <n> bytes; fileLength is <m>`), and a step entry past the steps block is
  `Inconsistent` at the field being read, the rule T236 states. They run after the checksum, so the
  Phase 8 fixtures that reach checks 5 and 6 without `fixChecksum` stay red until T235 and T236 put
  those checks first, which leaves these refusals as unreachable backstops.
- **`Reset` checks every rule of the weapon record, with a range for `phaseSteps` per phase (T034,
  T060, Phase 2 review).** The guard checked only the holstered phase, `fireWait`, `magazine` and
  the recoil bounds, although the contract said "every rule". It now also refuses `heat` at or above
  `OverheatThresholdScaled + HeatPerShotScaled` (data-model's bound) and a `phaseSteps` outside the
  range its phase allows: 0 in Ready and Firing, [1, `EquipSteps`] in Switching, [1, `ReloadSteps`] in
  Reloading, [0, `EquipSteps`] for the active weapon while Overheated, and 0 for a holstered Overheated
  weapon. These ranges are tighter than data-model's "otherwise 0" wording, and are exactly what the
  weapon phases can produce: a loaded Switching or Reloading weapon at 0 would never complete, because
  W2 counts down only from above 0. Both refusals are `OutOfRange`; the holstered phase stays
  `Inconsistent`. data-model.md#weaponstate and contracts/state-serialization.md now give the ranges.
- **`MaxQueryCandidates` counts ⌊(w + 1/64)/c⌋ + 2 cells per axis, not ⌈w/c⌉ + 1 (T031, T057,
  Phase 2 review).** A float box reaches further than its nominal width: on 4 m grid cells from
  origin 0, the box [3.9999998, 3.9999998 + 4f] ends at exactly 8 and spans grid cells 0 to 2, and a
  0.3 m capsule with a 0.05 m skin has w = 1f, whose box [1.9999999, 3] spans three 1 m cells. So the
  old count fell one cell short per axis whenever w/c is a whole number, and a valid configuration
  could fault with `QueryCapacityExceeded`. An interval of real length L overlaps at most ⌊L/c⌋ + 2
  cells, and the 1/64 m margin covers the rounding: coordinates are at most 8192 m, where a float
  step is about 0.001 m, against cells of at least 0.25 m. ⌈w/c⌉ + 2 would also be sound, but it
  raises the placeholder buffer from 274 to 608 for cells a 1.22 m box cannot reach; the margin keeps
  research R6's 274 and every row whose ratio is not whole, and changes only the whole-number ratios
  (4 m on 4 m grid cells: 306 to 648). `CollisionWorldBuildTests.MaxQueryCandidatesCoversEveryFloatBoxOfThatWidth`
  checks real float boxes near cell boundaries, including near the 8192 m limit.
- **`config check` exits 2 for a config file it refuses or cannot read (T037, T062, Phase 2 review).**
  The CLI's exit-code table gives 2 for "an unreadable or refused input", but the command passed
  `SimConfigJson.Load`'s size and read errors through as binding errors and exited 1, as its own
  section ("2 when the file is missing") allowed. The general table wins, so `config check` matches
  every other command's refusals: it checks the length against `SimConfigJson.MaxFileLength` and reads
  the file itself, writes `<path>: …` to stderr and exits 2, and binds the text with
  `SimConfigJson.Parse`. `SimConfigJson.Load` is unchanged, because the server's fail-fast startup
  wants one error list whatever the cause. The `oversized-file` row pins the refusal. An unreadable
  file has no row: file permissions do not stop a test run as root, so there is no portable fixture.
- **`SimMath.Clamp` is a compare-select, not `System.Math.Clamp` (T016, T045, Phase 2 review).**
  `System.Math.Clamp` throws `ArgumentException`, and allocates its message, when min > max. Step code
  must throw and allocate nothing, and later phases clamp against bounds computed in `float`, which
  could cross by an ulp. `value < min ? min : (value > max ? max : value)` gives the same bits whenever
  min ≤ max (including a NaN value or bound and the signed zeros) and never throws.
  `MinMaxAreCompareSelects` pins NaN, −0 and both crossed-bound branches. The analyzer's "float
  `Clamp` inside SimMath only" allowance stays, so research R4 and contracts/analyzer-rules.md are
  unchanged there. T183's pitch clamp is in `int`, so it now names the integer `System.Math.Clamp`,
  which the analyzer allows, rather than `SimMath.Clamp`, which has no `int` overload.
- **Zero-allocation tests require the smallest of three 1,000-call batches to be 0 bytes (T013, T036,
  Phase 2 review).** During review, `WorldAllocationTests.StepAllocatesNothing(20)` counted 4,952 bytes
  over 1,000 steps in one of five full-suite runs, and every rerun counted 0; more than 70 full-suite
  runs here, sequential, after a rebuild and four at a time, did not reproduce it, and a probe with
  other threads allocating and collecting showed no stray count. With the cause unknown and outside the code under test, a single
  counted window can fail a correct step. Each test now warms up twice as before and then counts three
  separate batches through `ZeroAllocation.MeasureBatches`: a per-call allocation, or one that recurs
  within 1,000 calls, shows in every batch, while a one-off stray count lands in one. What this gives
  up is a single allocation made once, after the warm-up, which the batches would read as stray; the
  world's buffers are sized at construction (data-model.md#world-simworld), so no step has such a
  path. There is no byte tolerance, and the failure message lists every batch. More warm-up or turning
  tiered compilation off was not chosen: the core runs unoptimised in the Debug test build, which the
  JIT does not tier, and a review run reported a stray count with tiering off as well. T076, T115,
  T172, T199 and T246 use the same rule.

## Delivery

The work ships as two vertical slices. Each is a PR that leaves `main` green.

1. **Core, analyzer, formats and server-side agreement** on `015-shared-simulation-core`. This covers
   the spec artifacts, US1, US3, US4, US5 and US6, except the cross-build halves of US3 scenario 8
   and US5 scenario 5 and the agreement half of US5's Independent Test (below), and the server half
   of US2 (scenarios 4 and 5, and scenario 6 through `BatchStepper` called directly), plus:
   - the server benchmarks, their dry-run export test, and the zero-allocation tests (US7.1, US7.3);
   - the `simcore` CLI, including the server side of the cross-build tables (`CrossBuildTables`,
     `agreement prepare` writing them and `agreement compare` diffing them);
   - the committed content and streams;
   - `streams verify`, which reruns every committed stream in a fresh process.

   The existing .NET workflow builds and runs all of it; only the full benchmark run waits for
   slice 2's `measure` job. No guarded path is touched.
2. **WebAssembly build, agreement suite and measurement job**, on a new branch off `main` after
   slice 1 merges. This covers the rest of US2 (scenarios 1 to 3 and 7, the browser half of
   scenario 6, and its Independent Test) and US7.2, the cross-build halves of US3 scenario 8, SC-004
   and US5 scenario 5, and the agreement half of US5's Independent Test, which need a WebAssembly
   build:
   - the Wasm host (including the `EvaluateTables` export) and its tests;
   - `tools/agreement/`, including the tables check in both runners;
   - `.github/workflows/simcore.yml`, its workflow test, and the `auto-merge.yml` listener.

   It touches `.github/`, so it gets the independent automated review before the session merges it.

`tasks.md` covers both slices. Slice 1's PR description lists slice 2 as the follow-up. At slice 1's
merge, spec.md lists the slice-2-only items under "Planned for slice 2: not yet built, no test on
main" (T264), so it states as built only what slice 1 tests; slice 2 moves them back (T316).

## Complexity Tracking

No violations. The slice-1 interim state of spec.md, with the slice-2 items in a marked planned
section, is not one: spec.md claims no behavior that `main` does not test (Constitution Check, IX).
