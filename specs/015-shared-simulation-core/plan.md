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
- **`MovementWorlds.Stair`'s top runs 20 m deep, not 2 m (T063, T070).** T063 asked for a 2 m deep box
  step, but T070 holds Forward for 2 simulated seconds (about 13.65 m at the placeholder run speed and
  acceleration) and asserts that the climbing rows end Grounded on the stair top; a 2 m deep step
  would leave the character back on the floor behind it. The step keeps its rise and its front face
  2 m ahead of the start, and its top runs 20 m along +Z, so the run ends on it with margin and the
  step-up itself is unchanged. T063 now says so.
- **T066 allows one ulp on returned points and, for near-parallel segment pairs, min(|d1|, |d2|)·sinθ
  on the distance (Phase 3 red check).** The task asked for points within 1e-6 m of their primitives,
  but a returned float point up to 34 m from its local origin carries up to 3.8e-6 m of coordinate
  rounding, so the test allows 1e-6 m plus one ulp of each coordinate; the distance itself stays within
  1e-6 m, which T095 meets by computing it from the separation vector rather than from the rounded
  points. For segment pairs just above the 1e-10 parallel threshold, a·e − b² is below the rounding of
  a·e, so §5.1.9 in float returns some pair on the overlap, where the distance varies by up to
  min(|d1|, |d2|)·sinθ (a prototype measured 4.7e-5 m against the 6e-5 m bound); only those cases get
  that allowance. To keep 1e-6 m reachable elsewhere, cases lie within 2 m of their first input,
  triangles are well shaped (no edge under 0.1 m, |AB × AC|² ≥ 0.01·maxEdge⁴), and near-parallel
  pairs are built near the world origin. `LocalOrigin` returns the nearest lattice point, which keeps
  local coordinates within 34 m. research.md R7's accuracy note now says so.
- **T068 bounds a hit by contact distance, not by 1e-6 in t (Phase 3 red check).** Near a triangle
  edge, Ericson's float region test can move the closest point about 0.5 mm along the face while the
  distance changes by about 1e-7 m, which tilts a Newton step; a prototype stopped up to 1.3e-5 m inside
  r + skin and up to 2e-5 past the reference time in t, so "T ≤ t_ref + 1e-6" would fail 0.1 to 1.3% of
  hits for a correct sweep, and a slack in t means nothing for a grazing sweep. A hit's `T` must now be
  no later than the first time the axis comes within r + skin − 1e-4 m (a hundredth of the skin), its
  distance at `T` must still be at least r, and a miss must never come that close. The test also
  requires every hit to name the triangle and draws at least 1,000 hits and 1,000 misses; its start
  jitter is ±0.35 m, because the first draft's ±0.6 m drew only 766 hits against an exact
  time-of-impact stand-in.
- **T067 checks a refused step through the world's committed views (Phase 3 red check).** "Leaves the
  state bytes and the hash as before" has nothing to compare with: a world with candidate capacity 1
  can never complete a step, and a refused step reports no state. The test compares
  `SimWorld.CommittedCharacters` with what `Reset` loaded (a character holding Forward, so a commit would
  change it) and requires no committed projectiles, besides `CurrentStep`, `Hash` 0 and the previous
  `Step`. "The quad's cell only" is read as heightfield cell (1, 1), which the wall quad crosses, since
  tiny-world has a single 4 m grid cell.
- **The movement properties assert that they were exercised (T075, Phase 3 red check).** Against the
  Phase 2 world, which does not move characters yet, `NeverOverlapsByMoreThanTheContactTolerance` and
  `NeverGetsStuckOnAFuzzWorld` passed: a start may already lie within r + 2·contactSkin of a triangle,
  and a character that never moves is never stuck. A contact now counts only in a step that moved the
  character, the stuck property also requires some step to move a character, and the step-up and
  loop-bound properties require a grounded horizontal move and a sweep, so all four stay red until
  T098 and T099 land.
- **The scenario tests read a touch as r + contactSkin + contactTolerance, and check their Given
  (T078 to T082, Phase 3 red check).** T079 defined first contact as the axis within r + contactSkin
  of the wall plane, but a sweep stops between r + contactSkin and r + 1.1 · contactSkin from what it
  hits, and float rounding can leave a resting axis an ulp above r + contactSkin, so that bound could
  find no contact in a correct slide. A touch now uses the rest band's upper edge, as rest height does.
  Each test also checks the run is the scenario its acceptance scenario describes (no fault, one
  character, the buttons held, the named geometry on the walk line) and that the run reached its
  feature, so a stated refusal cannot pass vacuously: `stairs-blocked` must touch the stair face, and
  `slope-limit` must reach the 50° slope (terrain 2 · capsuleRadius ahead more than stepHeight above the
  plateau). "Every step on the walkable slope" is read as every step up to the first one at rest height
  on the plateau, which needs no slope geometry beyond `PlateauHeight`.
- **`TestWorldFeatures` names the stair front edges (T080, T101).** T101 said the features name "stair
  top heights and front edges" without names. T080 reads `StairsClimbFrontEdge` and
  `StairsBlockedFrontEdge`: where each walk line crosses the stair's front face, at the stair's top
  height, with the face perpendicular to the heading, so one point gives the top height, the face
  plane and how far past it the feet are. The wall slide reuses `WallMeshId`'s wall, whose plane the
  test takes from the mesh. T101 and T104 now say so.
- **The crouch-under-ledge test reads the headroom at the position a step starts from (T083, Phase 3
  red check).** T083 asked that the first step whose standing segment clears the ledge be Standing,
  without saying which position. Sub-step 2 tests the headroom before the step moves
  (data-model.md#movement-step), so a correct step whose end position first clears the ledge is still
  Crouched and stands one step later; read at the end position, the clause would fail a correct core.
  The test takes each step's standing segment at the previous step's committed position, and the
  ledge as every triangle of the mesh that holds the level underside at `LedgeUnderside` midway
  between the named edges, so it needs no new feature name. T083 now says so.
- **`TestWorldFeatures` names the thin wall, and the corner and thin-wall tests check they reached
  their feature (T084, T085, T101, T105).** T101 said the features name the "thin-wall plane" without
  a name. T085 reads `uint ThinWallMeshId`, like `WallMeshId`, and checks it names a single
  zero-thickness quad that the walk line crosses ahead of the start; T101 and T105 now say so. The
  corner test finds the two walls as `TestWorldGeneratorTests` does, from `Corner`, so it needs no new
  name. As in T078 to T082, each test checks its Given and that the run reached its feature, so no
  clause passes vacuously: `corner-wedge` must end with the axis touching both walls and must sweep at
  some step, and `thin-wall-fast` must touch the thin wall's plane. A touch is again within
  r + contactSkin + contactTolerance.
- **A scenario definition's length is `DurationSeconds`, and `jump-land` is 2 s in all (T086, T103,
  T105, Phase 3 red check).** T086 asserts that a movement definition built at 20, 30 or 60 Hz has
  its duration in seconds × hz steps, and T103 gave definitions "durations in seconds" without naming
  the member. The test reads `double DurationSeconds`, the whole run's simulated seconds; T103 now
  names it. T105 defined `jump-land` as "Jump held for one step, then 2 s with no buttons", which is
  1 + 2·hz steps: no fixed number of seconds gives that at every rate, so it contradicted T086.
  `jump-land` is now 2 s in all, Jump in the first step and no buttons after it: 40, 60 and 120
  steps, still more than twice the about 0.92 s of airtime T082 needs to see the landing. T105 now
  says so. `StreamRunner`'s refusal of a `perturbStep` it cannot apply names the parameter
  (`ParamName` `perturbStep`), which T088 asserts; T088 and T100 now say so.
- **`LoadAllDecodesEveryFileInOrdinalOrder` loads two different worlds, and the `streams` refusals
  name what they refuse (T091, T092, T107, T108).** T092's second `ContentFilesTests` Fact loaded two
  copies of `test-world.mmcd`, which decode to equal worlds, so it could not see the order its name
  promises. `a.mmcd` is now the encoded `tiny-world` and `b.mmcd` the committed test world, written in
  that reverse order, and the test asserts the tiny world comes first. The refusal tests also pin what
  a usable refusal says, which the contract left open: both or neither selector names `--scenario` and
  `--all`, an unknown scenario is named beside the known ones, an empty `--content` names
  `test-world.mmcd`, and a missing `--dir` is named on stderr. The stream whose collision file is
  absent is `flat-run-30hz` with another `collisionChecksum`, so it fails only that check. The record
  tests also check that nothing else in the temp copy changes and that a re-added stream is
  byte-identical to the committed one. T091, T092 and contracts/simcore-cli.md now say so.
- **`ClosestPoints` refines Ericson's results where float loses the last digits (T095, T066).** Run as
  written, §5.1.9 missed T066's 1e-6 m on its own seed, and §5.1.5 on other seeds. CsCheck draws edge
  values, so two segments can share an end exactly, and §5.1.9 then rounds s an ulp short of 1 and
  reports 1.1e-6 m for segments that meet; a pair that nearly meets loses the digits that place s and t
  in b·f − c·e (2.1e-6 m off). So §5.1.9's pair is followed, when the directions are not parallel, by
  one step of iterative refinement (the same solve applied to the residual separation, whose dot
  products are small) and then by each end measured against the other segment, in the order P1, Q1, P2,
  Q2; each replaces the pair only when strictly closer, so it can only bring the result nearer the true
  minimum. In §5.1.5's face region, the barycentric point's in-plane rounding (about 1e-6 m in the 34 m
  local frame) was the whole distance of a point lying on the face, so the face region's separation is
  projected onto the face normal, which removes that rounding to first order. Segment–triangle tries P,
  then Q, against the triangle before the segment against the edges AB, BC and CA, so a capsule resting
  on a face reports the exact face contact on a tie. With these, T066 passes on its seed and on 15
  others (25,000 cases per query each). T095 now says so.
- **A sweep's contact is the nearest hit at its time of impact, not the triangle whose stop landed first
  (T097, T098).** Research R7 kept the least t, ties to the lower ID, and took the contact from that
  triangle. A flat wall made of two triangles then reports a tilted normal near their shared edge: the
  triangle whose closest point lies on the shared edge is about 8e-5 m farther, but its Newton stop can
  land up to 0.1·skin early inside its tolerance band, so its edge direction (1.2° off the wall's normal
  in the case found) became the contact normal, and a 45° wall slide at 20 Hz kept 4.962 m/s along the
  wall instead of 7·cos 45° = 4.9497 (US1.2 allows 1e-4 m/s). The sweep's T is still the least stop
  time. Its contact is a boundary plane that stops at exactly that time, so boundary planes still win
  ties; otherwise the nearest, measured at that time, of the planes and triangles that hit, ties to the
  lower ID. Pass 1 marks every candidate that does not hit with −1 in the caller's scratch span, so pass
  2 re-measures only the hits. The step-up's last sweep, which data-model.md left unsized, goes down by
  the rise plus 2·contactSkin, so it finds ground at the starting height. data-model.md#movement-step,
  research R7 and T097 now say so.
- **The content loader names the refused file through an internal overload (T107).** The commands
  print `<command>: content <file name>: <message>` for a refused content file, but
  `ContentFiles.LoadAll(string)` throws the decoder's `CoreFormatException`, which carries no file
  name, and T092 requires that exact exception type. `LoadAll` therefore has an internal overload that
  calls back with each file's name before it opens it (the public one passes no callback), and
  `ContentFiles.TryLoadAll` uses it to print the refusal and to pair each world with its file name,
  which `streams record` needs to find the collision file a scenario names. Content still loads only
  through `LoadAll`; contracts/simcore-cli.md#content-files and tasks.md T107 and T108 now say so. The `--all` removal goes
  through the stream writer too (`StreamFiles.RemoveCommitted`), so every change under the streams
  folder stays in one class: contracts/recorded-stream-format.md#regeneration names the writer's three
  entry points, and T093's architecture test and T107 cover all three (Phase 3 review).
- **Ground sweeps prefer a touching support contact (T097, T098, Phase 3 review).** The ground probe
  took the single nearest contact at the sweep's time. Where a steep face touches the capsule a little
  nearer than the walkable ground under the feet, that contact was the face, which is not support, so
  a character standing still at the foot of `slope-limit`'s 50° face was airborne from then on (95 of
  180 steps at 30 Hz, at exact rest height on the plateau) and could not jump, and walking into the
  slope lane's steep side at 60 Hz flickered between grounded and airborne. The probe and the step-up's
  two down-sweeps now use `CapsuleSweep.SweepPreferringSupport`: the same stop time and boundary-plane
  rule, but the contact is the nearest support contact whose distance at that time is within the stop
  band (r + skin + 0.1·skin), ties to the lower ID, else the nearest contact. Only touching contacts
  count, because the snap uses the one stop time: a support triangle the capsule would reach later in
  the sweep must not ground it. The starting-ground sweep of the step-up is included, since it too
  looks for the ground the rise is measured from. Positions are unchanged; only Grounded and
  GroundNormal change. The support rule now lives once, in `CapsuleSweep.IsSupport`. Of the 11
  committed movement streams only `slope-limit` changed (first changed step 86); it was recorded again
  with the reason, and `streams verify` matches the other 10 unchanged. data-model.md#movement-step,
  research R7 and R8, T097 and T098 now say so, and T069, T071 and T081 assert it.
- **The grounded probe is at least the contact band (T098, Phase 3 review).** With `snapDistance` 0,
  which the rule table and R06 allow, a grounded character's probe had zero length and never hit, so a
  character resting on flat ground alternated between grounded and airborne every step, switching Jump
  and the ground and air acceleration on and off. The grounded probe is now max(snapDistance,
  2·contactSkin), the band the airborne probe already uses, so snapDistance keeps its meaning as the
  extra reach. Raising snapDistance's minimum with a new relation was not chosen: it would change the
  configuration contract and its error codes for what is a step rule. No committed stream changes
  (the placeholder is 0.35 m).
- **An empty or whitespace-only path option is a usage error at parse time (T106 to T108, Phase 3
  review).** `streams verify --content ""`, `streams record --out ""` or `--config ""` and
  `config check ""` reached a file API that throws `ArgumentException`, which System.CommandLine turned
  into an unhandled-exception exit 1, the code the contract keeps for a divergence or a validation
  failure; and `content generate --out "   "` wrote a file named by three spaces into the working
  directory. Every path option and argument of the CLI now carries `PathOptions.RequireNonEmpty`, so
  such a value fails the parse, is logged as `Invalid arguments: <option>: the path must not be empty
  or whitespace`, and exits 2 before any command runs. A broad `catch (ArgumentException)` around the
  loaders was not chosen: it would also turn real programming errors into exit 2. The library
  guards (`ContentFiles.LoadAll`, `StreamFiles`) keep throwing for direct callers.
  contracts/simcore-cli.md#settings-output-and-exit-codes now says so; `agreement prepare` (Phase 4)
  takes the same validator.
- **The edge and vertex starts check the slide loop's bound and that the character moves (T073,
  Phase 3 review).** `CornerTests.StartsOnATriangleEdgeOrVertexNeverTunnelOrOscillate` covered the edge
  case's "never tunnels" and "never oscillates" clauses but not "the slide loop stops within its
  iteration limit", and it would have passed for a character stuck on its start. Each step now checks
  `SubMoves` ≤ maxSubMoves and `Sweeps` ≤ SubMoves · (2 · maxSlideIterations + 3) + 1, and every
  direction (not just some direction) must sweep and end away from its start, which holds today at
  every row and rate. Test-only: the code was unchanged.
- **The step-up runs 3 fixed sweeps, and the sweep bound is 2·maxSlideIterations + 3 per sub-move
  (T073, T075, T084, Phase 3 review).** data-model.md#movement-step's loop-bound line gave the step-up
  4 sweeps, while its own step-up description, `MovementStep` and `MovementResult.Sweeps` run 3: the
  2·contactSkin ground-contact down-sweep, the up-sweep and the landing down-sweep, plus the walls-only
  slide. The line now says 3, and the tests' per-sub-move bound was tightened from
  2·maxSlideIterations + 4 to + 3, so the bound they check is the documented one and an extra sweep
  would fail them instead of hiding in a spare. It holds at every row, seed and rate. The code was
  unchanged.
- **`RecordedStream.Decode` checks `stepCount` and the step numbers from Phase 3 (T092, T236, Phase 3
  review).** Two parts of the decoding contract were left to T236: check 5's `stepCount` in [1, 2²⁰],
  and check 12's `steps[i].step` = S₀ + i + 1. Without them a zero-step stream decoded, so
  `streams record` crashed (exit 1, an index exception) computing the old final hash after it had
  already replaced the stream, leaving no `REGENERATIONS.md` entry; and a checksum-valid stream with a
  misnumbered step reached `StreamRunner`, so `streams verify` threw instead of refusing it, printed no
  total line and skipped the streams after it. Both checks now run in contract order (the `stepCount`
  limit after check 4 and before the checksum; each step number before its entry's commands), the CLI's
  existing `CoreFormatException` path prints `REFUSED stream <name>: …` and goes on, and the agreement
  runners will share the refusal. Patching the CLI alone was not chosen: the decoder would still accept
  streams the contract refuses. T236 keeps both checks and adds the rest of checks 5 and 12 around them.
- **`streams record` builds the regeneration entry and its output before it writes (T107, Phase 3
  review).** It used to replace the stream files and only then format the log entry, so anything that
  threw while formatting left rewritten streams with no entry, against FR-028's "every change to an
  expected hash is a visible commit with its reason". The entry and every output line are now built
  first; only the writes can fail after a stream is replaced. An old file that no longer decodes is
  replaced as changed with its final state shown as `unreadable`, which contracts/simcore-cli.md now
  states.
- **T116 settles the cross-build table's row order and its wrong-magic text (T116, T129).**
  wasm-api.md's row list said "ordered by function ID, then as listed" but left some things open. It
  did not give the order inside a value and its neighbours, the sign of "the smallest and largest
  subnormal", where the non-finite rows go, or how Atan2's ±0/±∞ combinations are ordered. It also
  gave no detail text for a wrong magic. `InputsArePinned` checks every function's rows in a fixed
  order, and `RefusesMalformedTables` uses the core's existing `FrameText.WrongMagic` wording. The
  contract now states both, along with the per-function counts (M = 99,480), so T129 builds to the
  test. Leaving the order to the implementation was not chosen, because the task asks the test to
  check that the rows come "as listed", and that check needs one defined order.
- **The generator's pinned pitch-limit commands are always valid (T121, T122, T132).** T132 pins
  character 0's pitch to −16384 at step 0 and 16384 at step 1, but its `draw % 64 == 0` path could
  still make either command invalid. T121 and T122 count pitch coverage over accepted commands only,
  since a rejected command never reaches the world, and `fuzz-all-buttons` has one fixed seed, so a
  pinned command made invalid there would fail T122 at every rate. T132 now says both commands stay
  valid: their invalid draw is still taken, so the counter order is unchanged. T122's task text now
  says "accepted commands reach" the pitch limits, which is what the test asserts. Counting rejected
  commands toward pitch coverage was not chosen, because it would pass with no pitch limit ever
  reaching the world.
- **`WorkDirectories.Prepare` returns a disposable `PreparedWork`, not a bare path (T123).** T123
  said it returns the path. A path alone would leave its unique temp directory behind after every
  compare test, holding the streams, records and cross-build tables that one prepare writes, which
  breaks the test support's rule that each test's temp directory is deleted when the test ends
  (`TempDirectories`). `PreparedWork.Path` is the work directory and `Dispose` deletes it, so each
  test holds it in a `using`. T123's task text now says so. Returning the path and leaving cleanup
  to the operating system was not chosen, because reruns would keep piling up work directories.
- **`BatchStepper.Reset` reuses one world per pair of loads (T127, T115).** T127 said every `Reset`
  constructs a new `SimWorld`. Built that way, T115's 20 Hz and 30 Hz rows failed although
  `StepQueued` allocates nothing: their frequent Resets each allocated a world's buffers (the state
  buffer alone is 91,672 bytes at the placeholders, on the large object heap), which set off gen-2
  collections, and a few steps after each one the stepping thread's counter showed 6 to 7 KB, in
  every counted window (for example [6984, 13968, 13968] bytes for batches of 16 at 30 Hz). The
  first `Reset` after the load calls now constructs the world and later ones reuse it, since
  `SimWorld.Reset` loads the whole state; every load call drops it. Every window then counts 0 bytes,
  and the browser client, which resets on each resynchronisation, does not allocate a world each
  time. T127 and wasm-api.md#batchstepper now say so. Forcing collections or widening the count in
  the test was not chosen, because the allocations came from the stepper's own Resets.
- **The cross-build table's draws are fixed in the contract (T129).** wasm-api.md said the drawn
  patterns are uniform over the stated range from `SplitMix64.At(0x5EED0000 + function ID, i)`, and
  T129 said out-of-range draws are rejected in draw order with a bound per row, but neither said how
  a draw becomes a bit pattern. `BuildInputs` takes the upper 32 bits, keeps the sign (cleared for
  Sqrt), masks the magnitude to the smallest all-ones mask covering the range, and rejects a
  magnitude outside it. Each function has one counter, and after 64 rejections the magnitude is
  mapped in by remainder; no row reaches that bound, and the worst row takes 17 draws. An Atan2 pair
  is one draw's two halves. The contract now states this, and the inputs' XxHash64,
  `0x22B4C43A2ACFD77E`, is pinned in `InputsArePinned`. Mapping each draw by remainder alone was not
  chosen, because it is not uniform over the range.
- **The divergence report covers three cases the contract left open (T130).** A build record whose
  step number differs from the server's (the state may still match) gets a
  `  record step  server <n>  <build> <m>` line after the hash line, so a report that otherwise shows
  `0 fields differ` still says what diverged. Records a build writes past the server's last diverge at the first of them,
  with `  hash  server absent  <build> <x16>`, as the mirror of a missing build record. A state with
  bytes left after its last event list is listed with `unreadable from offset <n>` at the first of
  them, since the layout cannot read it to its end. state-serialization.md#divergence-report and
  T130 now say so, and `DivergenceReportTests` has a test for each. Ignoring extra records or
  trailing bytes was not chosen, because a build that writes them does not agree with the server.
- **`TablesComparison` reports a results length that differs under an equal header (T131).** A build
  that writes the 16-byte header but stops before the last row has equal headers and fewer bytes; the
  contract named only the header refusal, and reading past the end would throw. It is Diverged with
  `tables: build <b>, results are <n> bytes; the server's are <m>`, which wasm-api.md#cross-build-tables
  and T131 now give, and `TablesComparisonTests` has a `results-length` row.
- **`StreamGenerator`'s draws are fixed in the contract, and `fuzz-all-buttons` is a generated
  definition (T132, T133).** The contract gave the draw order (header, initial state, per step, per
  character) but not which draw feeds which field. Counter 0 is the match salt and counter 1 the
  character count (the overload given a count leaves it unused, so both overloads keep the same
  order); the initial state takes none; each command takes five (validity, buttons, yaw, pitch,
  slot), and the four invalid kinds cycle per stream. simcore-cli.md#generated-streams now states
  this. The spawn row holds at most 8 characters, 4 m apart on the flat ground in front of the firing
  lane. `ScenarioDefinition.Generated` gives the catalog a definition whose stream comes from a
  generator, with no characters of its own, so `fuzz-all-buttons` builds the generator's stream at
  any rate. The character-schedule definition was not stretched to fit, because the fuzz stream's
  commands are drawn, not held. A generated definition's `DurationSeconds` is its fixed step count at
  its own rate (`fuzz-all-buttons`: 300 steps at 30 Hz, 10 s), so T103's rule that a build at rate hz
  has `DurationSeconds` × hz steps holds for schedule definitions only; T103, T122 and T133 now say so
  (Phase 4 review), and `FuzzAllButtonsDefinitionTests` pins the rate, the empty character list and
  the 10 s. Building `DurationSeconds` × hz steps instead was not chosen, because simcore-cli.md gives
  the fuzz stream 300 steps at any rate.
- **`agreement prepare` runs every stream before it writes, and refuses three more inputs (T135).**
  The contract listed the refusals of the arguments and of a committed stream, but the perturbation
  and the refusal fixtures need `flat-run-30hz`, the generated streams need a world to run on, and a
  generated stream could still fault. Prepare now runs the committed and the generated streams first
  and writes only once all have run, so every refusal leaves `--work` as it was; a missing
  `flat-run-30hz.mmrs`, a missing `test-world.mmcd` in `--content` and a generated stream the server
  refuses or faults on each exit 2 with an `agreement prepare: ` line. Step 1 shares `streams verify`'s
  check (`StreamsCommand.VerifyStream`), and the collision files are copied byte for byte through a
  `ContentFiles.TryLoadAll` overload that also gives each file's bytes. simcore-cli.md#agreement-prepare
  and T135 now say so. Writing as each stream ran was not chosen, because a later failure would leave
  a half-prepared directory behind.
- **`agreement compare` settles five cases the contract left open (T136).** `agreement.json`'s
  `outcome` is `CannotRun` when a required build did not run, since the run neither matched nor only
  diverged, and the report and summary are still written then (not for a NotComparable or unreadable
  work directory). A build's error file takes the place of its records or table results, without its
  trailing line breaks, and a build with neither tables file is `Missing` with
  `<build>/tables.bin was not written`. Missing tables also get `tables.txt` (the contract named only
  diverged ones) and print `tables: build <b>, results missing: <message>` as their first report, so
  the bare message never stands alone on stdout. The manifest's own `formatVersion` is checked with
  the rules revision and the formats, before the rest is read. simcore-cli.md#agreement-compare and
  T136 now say so. Failing the run for a stale error file beside records was not chosen, because a
  build that threw has no records to trust.
- **T137 holds this phase's files to 95%; the gate's exit 0 stays with Phase 10 (T137).** The
  checkpoint asked for `tools/coverage_gate.py` to exit 0, but the gate lists every `src/` file changed
  since `main`, and 13 files of Phases 1 to 3 that this phase does not touch are still under 95% (and
  Phase 5's analyzer is in no report yet). Phase 1's checkpoint already gives the slice-wide gate to
  Phase 10, whose T260 tops coverage up until it exits 0. T137 now passes when every file it lists is at
  or above 95%, and the list gains `Commands/ContentFiles.cs` and `Batch/StepFaultException.cs`, which
  this phase changed; every one of them is between 95.8% and 100%, through the Theory rows and Facts T137
  names. Topping up the earlier phases' files here was not chosen, because none of them changed in this
  phase and Phase 10 covers them in one pass.
- **`agreement compare` checks the server's files before any build, and names them by path (Phase 4
  review).** A server records file that was not one whole record per expected hash, or table inputs
  and `server/tables.bin` that were not one table, were refused only deep in the comparison, by the
  `ArgumentException` of `DivergenceReport.CompareBuild` or `TablesComparison.Compare`. That refusal
  needed a build folder to exist, and its message named no file (and carried .NET's
  `(Parameter '…')` suffix), against the contract's "the missing or refused file by its
  work-directory path". `AgreementComparison` now counts each server records file against its
  stream's steps, and compares the server's table results against themselves, before reading any
  build folder; it throws `InvalidDataException` with `server/<stream>.records: …` or
  `server/tables.bin (…) and tables/inputs.bin (…) do not hold the same whole table`, and the command
  no longer turns an `ArgumentException` into exit 2, so a real bug is not reported as a bad work
  directory. `DivergenceReportTests.RefusesServerRecordsThatAreNotOnePerExpectedHash` now covers
  `CompareBuild`'s own refusal, which the CLI no longer reaches. simcore-cli.md#agreement-compare,
  T124, T136 and T137 now say so. A new public check on
  `TablesComparison` was not added, because its documented refusal already is that check and the CLI
  is its only caller.
- **`agreement prepare` enforces the manifest's stream-name rule when it writes (Phase 4 review).**
  Only `agreement compare` checked that names match `^[a-z0-9-]+$`, are unique and are not `tables`,
  so a committed `tables.mmrs` or `Flat_Run.mmrs` prepared a manifest compare then refused, and a
  committed `seed-001.mmrs` was overwritten by the generated stream of seed 1. Prepare now refuses,
  before any stream runs, a committed name that `AgreementManifest.IsStreamName` rejects or that
  `--seeds` also generates, one stderr line per file and exit 2. A committed `seed-<nnn>` outside the
  `--seeds` range stays legal, since nothing else claims that name. simcore-cli.md#agreement-prepare,
  T123 and T135 now say so. Renaming or skipping the clashing stream was not chosen, because a
  silently changed stream set is worse than a refusal.
- **The NotComparable tests carry a real divergence (Phase 4 review).** US2.5 says the refusal comes
  "instead of reporting a divergence", but the work-directory test compared builds equal to the
  server and the stream test's copies still matched, so neither could tell a refusal from a
  comparison that found nothing. `WorkDirectoryFromAnotherRulesRevisionIsNotComparable` now runs on
  the perturbed work directory and asserts the exact two stdout lines and no `report/`, and each copy
  in `NotComparableTests` also expects a wrong hash at step 12. T120 and T124 now say so.
- **A report or summary `agreement compare` cannot write exits 2, even after a divergence (Phase 4
  review).** The contract listed exit 2 only for inputs refused before the comparison, but writing
  `report/agreement.json`, a divergence report or the `--summary` file can still fail after it. That
  run prints its build lines and first reports, then
  `agreement: cannot run: the report cannot be written (<message>)` in place of the outcome line,
  and exits 2 even when a build diverged. Exit 2 wins over exit 1 because the run could not finish:
  CI must see "could not run" rather than a divergence whose report it does not have.
  `agreement.json` is written before the summary is appended, so a summary failure leaves it in
  place. simcore-cli.md#agreement-compare, T124 and T136 now say so, and
  `AgreementCommandTests.UnwritableSummaryAfterADivergenceExitsTwo` pins the precedence. Reporting
  the divergence with exit 1 and a warning was not chosen, because the step summary CI shows would
  then silently lack the section that explains the failure.
- **`TablesComparison` refuses a reference that is not one table, and names unknown IDs (T131,
  Phase 4 review).** wasm-api.md gave only the header, length and first-mismatch lines. Reading rows
  past the end of inputs or server results that do not hold the same whole table would throw
  `IndexOutOfRangeException` or compare garbage, so `Compare` checks first and throws
  `ArgumentException` (paramName `serverResults`) naming both lengths; `agreement compare` reaches it
  only through its own check of the server's files, which it reports as cannot run. A function ID or
  stance byte outside the table's set, which `Evaluate` refuses and only hand-built inputs hold, is
  named `function <n>` or `stance <n>` in the mismatch line rather than crashing the comparison.
  wasm-api.md#cross-build-tables, T117 and T131 now say so, and `TablesComparisonTests` has the
  `pose-unknown-stance` and `math-unknown-function` rows and
  `RefusesInputsAndServerResultsThatAreNotOneTable`. Treating the bad reference as a Diverged build
  was not chosen, because the fault is the server's files, not the build's.
- **`CrossBuildTables.Evaluate` refuses counts whose results would not fit in an array (T129, Phase 4
  review).** The frame check bounded M and P only through the input length 16 + 12·M + 16·P, but a
  pose result row is 100 bytes against a 16-byte input row. A whole input of about 343 MB with
  P = 21,474,837 made the `int` results length overflow, so `Evaluate` threw `OverflowException`, and
  at P = 42,949,673 the sum wrapped to 20 bytes and it threw `ArgumentOutOfRangeException`, where the
  contract promises a `CoreFormatException`. `CheckFrame` now computes 16 + 8·M + 100·P in `long` and,
  before the length check and so from the 16-byte header alone, refuses a value above
  `CrossBuildTables.MaxResultsLength` (`Array.MaxLength`, 2,147,483,591, written as a constant so the
  limit is the same in every build) as `Oversized` at 8, field `counts`,
  `the counts need <n> result bytes, above 2147483591`. wasm-api.md#cross-build-tables, T116 and T129
  now say so, and `RefusesMalformedTables` has the `oversized-counts`, `oversized-counts-wrapping` and
  `counts-at-results-limit` rows. A smaller stated cap on M and P was not chosen: the table is fixed
  at 99,480 and 6,192 rows today, and a cap would be one more number to keep in step with it, while
  the array limit is the bound that actually fails.
- **`agreement compare` reads the manifest's `formatVersion` first (Phase 4 review).** `ReadVersions`
  required `rulesRevision` and `formats` in version 1's shape before it compared `formatVersion`, so a
  manifest of another format version laid out differently was reported as
  `agreement: cannot run: manifest.json: $.formats is missing ...` rather than as NotComparable,
  although the manifest's own version exists to say its shape changed. `formatVersion` is now read
  first: in a version 1 manifest the other versions are still required, and in any other version each
  is compared only when present and a whole number in range. A missing or non-integer
  `formatVersion` is still unreadable. simcore-cli.md#agreement-compare, T124 and T136 now say so, and
  `WorkDirectoryFromAnotherRulesRevisionIsNotComparable` has the `manifest-format`,
  `manifest-format-reshaped` and `manifest-format-and-rules` rows. Reporting only the format version
  line for another manifest version was not chosen, because a readable rules revision that also
  differs is still worth naming.
- **Every branch of the wiring checks has a mutated row (T151).** T151 asks that the checks cannot
  pass vacuously, but its listed rows left some branches with no defect to find: `Directory.Build.props`,
  `RunAnalyzersDuringBuild` and `WarningsNotAsErrors` in the core check, the malformed `sdk.version`
  in the pin check, and every case of `OnlyTheCoreAttachesTheAnalyzer`, which the task listed with
  no rows although the repo's rule is that data variations are Theories. So
  `CoreProjectAttachesTheAnalyzerPrivately` also has `run-analyzers-during-build-false` and
  `props-warnings-not-as-errors`, `RoslynPinMatchesTheSdkBand` has a `("10.0", ...)` row, and
  `OnlyTheCoreAttachesTheAnalyzer(caseName)` is a Theory with `real`, `analyzer-tests-attach` and
  `analyzer-item`. The attachment check also reports an `<Analyzer Include="…MmoEngine.SimCore.Analyzers.dll" />`
  item, the route `BuildTests` uses, because that attaches the analyzer just as an
  `OutputItemType="Analyzer"` reference does. T151 and analyzer-rules.md#tests now say so.
- **`BuildTests` writes `<Project />`, not empty files, as the throwaway build's `Directory.Build.props`
  and `Directory.Build.targets` (T153).** T153 asked for empty files, but MSBuild refuses an empty
  project file because it has no root element, so the build would fail for the wrong reason. A file
  holding only `<Project />` still stops the upward search and imports nothing. T153 now says so.
- **`TableTests` also pins the ISA classes and the two fused `Vector128` members (T154).** T154 says
  its rows are transcribed one per contract entry, but its list left out entries the contract names:
  the MMO1001 rule's X86, Arm and Wasm ISA classes, and the Vector128 table's "every `*Estimate`" and
  "`FusedMultiplyAdd` is MMO1003". So the Vector128 exclusions have `Sse.IsSupported`,
  `AdvSimd.IsSupported` and `PackedSimd.IsSupported` (MMO1001), and `Vector128.FusedMultiplyAdd` and
  `Vector128.MultiplyAddEstimate`, which report MMO1003 by precedence. T154 now lists them, and T159
  and T160 say that those two rows pass with T160, not T159.
- **`TableTests`' `Half` and `NFloat` locals use `= default` (T154).** T154 asked for a local of each
  numeric type with a constant initializer, but C# has no constants of `System.Half` or `NFloat`, so
  that cannot be written for those two. `default` is the nearest form: it adds no call or conversion
  that another rule could match, and the local and its initializer are one statement, so each row still
  reports exactly one MMO1007, at the declaration. T154 now says so.
- **The exhaustive sweeps write one `SIMMATH` line per function and bound (T157).** T157 asked for
  one line per function, but its format carries a single `(bound <bound>)`, and Sin, Cos, SinCos and
  Tan each have two bounds (|x| ≤ 12867 and above). One line would have to drop a domain's maximum or
  mix two bounds. So those four functions write a second line, labelled `<function>Large`, for the
  large-argument domain, and every other function writes one line. The measure job greps the
  `SIMMATH ` prefix, so it copies both. T157 and quickstart.md now say so.
- **The `Math.FusedMultiplyAdd(double)` row of `FusedCallsReportOnlyTheFmaRule` passes with T163, not
  T160.** T160 said it makes T141 pass, but that row also asserts one MMO1007 on each `double`
  declaration (the parameter and the return type), and MMO1007 is T163's. With T160 the call reports
  exactly MMO1003 and the declarations report nothing yet, so the row cannot be green until T163.
  T160 and T163 now say so.
- **MMO1002 matches static methods, not the operators or properties of the math types (T160).** The
  contract said "static members of the generic-math interfaces", while its scope was invocations,
  method groups and function pointers. Counting operators would make `(Half)1`, whose conversion is
  the static method `Half.op_Explicit(int)`, report MMO1002 ahead of MMO1007, against T145's row,
  which expects one MMO1007; and a static property such as `T.Pi` is a constant. So MMO1002 matches
  static methods reached by an invocation, a method group or a function pointer, plus float `%` and
  `%=`. analyzer-rules.md#rules and T160 now say so.
- **A conversion to a hash-ordered type does not report MMO1004, and derived classes count (T161).**
  "Any conversion of the collection to another type" would report a `Dictionary` passed as an
  `IReadOnlyDictionary`, which is a lookup, not an enumeration, while the contract already leaves
  `Keys` and `Values` whose result is hash-ordered to the later enumeration. So a conversion reports
  only when its target type is not hash-ordered, and the converted value stays tracked by its type. A
  class derived from a listed class enumerates in the same order, so it counts; an interface counts
  only as the expression's own type, because `SortedDictionary` and `SortedSet` implement
  `IDictionary<,>` and `ISet<>`. analyzer-rules.md#hash-ordered-collections-mmo1004 and T161 now say so.
- **A parameter's default value counts as its declaration, and deduplication stops at the nearest
  statement (T159).** The contract named field and property initializers only; without parameter
  initializers, `void M(Vector3 v = default)` would report the parameter and its `default` value
  separately, one rule twice for one declaration. "The same statement" is the nearest enclosing
  operation whose syntax is a statement, so each statement in a `foreach` body reports on its own,
  even when the loop reports the same rule. analyzer-rules.md#precedence-and-deduplication and T159
  now say so.
- **Where the contract was silent, T162 and T163 chose the stricter reading.** A class derived from
  one of MMO1005's or MMO1006's whole types counts as that type, as MMO1004 already does. MMO1006's
  whole types report on declarations (T162 says so), MMO1005's only on use, because declaring a
  `Random` does nothing until a member runs. MMO1007 reports any declaration or non-constant
  registered expression whose type contains a banned type (`List<double>`), not only one that is it,
  matching the declaration list's type arguments and array elements. A method's identifier reports
  one rule, the first in precedence among its return type, `[Conditional]`/`[UnsafeAccessor]` and
  `Synchronized`, so one identifier never carries two diagnostics. MMO1008 counts `System.Type`'s
  operators and System.Reflection's sub-namespaces as members, and every `Debug` and `Trace` member,
  not only calls. MMO1009's SimMath exemption covers the helpers' own bodies, not a lambda or local
  function inside them, which can run without the range checks. A `GetHashCode` call is "not
  overridden" when Roslyn still binds it to `object`'s or `ValueType`'s, since it binds to the
  receiver type's override when there is one. T162, T163 and analyzer-rules.md now say so (tasks.md
  caught up in the Phase 5 review's third pass).
- **A positional record parameter reports once (T167).** While adding coverage rows, `record R(double X)`
  gave two identical MMO1007 errors at `X`: one for the primary constructor's parameter and one for
  the property the compiler declares from it at the same identifier. The contract said nothing about
  records, and the core declares many positional record structs, so a banned type in one would show
  every error twice. A property whose only declaring syntax is a parameter no longer reports its type,
  because the parameter already does. A property the record declares itself still reports on its own
  identifier. `RuleTests` pins both cases (`record struct S(Vector2 V)` gives one MMO1001,
  `record R(double X)` one MMO1007, and an explicit `X` property two), and
  analyzer-rules.md#precedence-and-deduplication now says so. The backstop is unchanged, since the
  change can only remove diagnostics.
- **T167 holds this phase's files to 95%; the gate's exit 0 stays with Phase 10 (T167).** T167 and
  the Phase 5 checkpoint asked for `tools/coverage_gate.py` to exit 0. The gate lists every `src/` file
  changed since `main`, though, and 12 files of Phases 1 to 3 that this phase does not touch are still
  under 95%. Phase 1's checkpoint and T137's decision already give the slice-wide gate to Phase 10's
  T260. So T167 passes when every file this phase changed is at or above 95% in the gate's table:
  `Descriptors.cs` and `Tables.cs` at 100%, `DeterminismAnalyzer.cs` at 98.49% (99.26% after the
  Phase 5 review fixes), `SimMath.cs` at 97.76%, and `SimMathBounds.cs` with no coverable lines. In
  this sandbox, `dotnet test MmoEngine.sln` also fails all 258 `MmoEngine.Harness.Tests` when no
  Docker daemon is running, because their ClickHouse fixture needs Docker. That project is Spec 014's,
  and this branch does not change it. Starting `dockerd` first makes all 258 pass locally (the Phase 5
  checkpoint run did), and CI's `.NET` workflow runs them on `ubuntu-latest`, which has Docker.
- **MMO1009 checks the element conversions Roslyn gives no operation of their own (T163, Phase 5
  review).** The rule looked only at `IConversionOperation` nodes, but a tuple conversion, a `foreach`
  iteration variable, a deconstruction, a spread and the left operand of `??` convert their elements
  with no node, so `foreach (int i in floats)`, `((int, int))t`, `(float p, float q) = ulongPair`,
  `float[] f = [.. ulongs]` and `ulongOrNull ?? f` compiled with no diagnostic. These are the raw
  conversions the rule exists to stop, so each is now checked element by element (nested tuples too,
  and the standard conversion on either side of a user-defined operator). Where to report was open:
  a `foreach` reports at its declared iteration type, or at the deconstruction variables, because that
  is where the conversion is written; a deconstruction assignment and `??` at the converted operand, as
  an implicit conversion already does; a spread at its collection expression, named `..`, as every
  spread is; a tuple conversion at its syntax, once, with its first converting element's reason. A
  `foreach` stays one operation, so over a hash-ordered collection it reports MMO1004 first. A spread
  is checked when its target is an array, a span or an `IEnumerable<T>`, which covers every collection
  type the core uses; contracts/analyzer-rules.md lists the rest under "Not covered".
- **An order-insensitive sink is a named set operation or a listed type's copy constructor (T161,
  Phase 5 review).** Every argument to every method or constructor of a hash-ordered type counted as a
  sink, so `new Dictionary<int, IEnumerable<int>> { { 0, set } }` and `d.Add(0, set)` stored a set
  whose later enumeration nothing reported. A test on the parameter's type (not a type parameter) would
  still pass `Hashtable.Add(object, object)`, so the sinks are the 14 set operations of
  `Tables.OrderInsensitiveSinkMethods` declared on a listed type, and a listed type's own constructor.
  A derived class's constructor no longer counts, because it may store or enumerate its argument; this
  is stricter than before, and the core has no such class. A sink whose comparer merges elements the
  source keeps apart is listed under "Not covered".
- **Local functions, lambdas and delegates are declarations, and accessors report their own
  `Synchronized` flag (T162, T163, Phase 5 review).** The symbol actions saw only fields, properties,
  methods and events, and skipped accessors before reading the flag, so a `[Conditional]`,
  `[UnsafeAccessor]` or synchronized local function, a synchronized accessor or lambda, and a
  delegate's `double` signature reported nothing. A second operation action now checks local functions
  as methods (at the identifier) and lambdas (at the lambda, since it has no identifier: its
  `Synchronized` flag, and only the parameter and return types its syntax writes, so an inferred `x =>
  x` does not repeat the delegate type's report). A delegate's return type reports at its identifier.
  An accessor's flag reports at its keyword, named as the accessor (`C.P.get`); a field-like event's
  accessors are implicit and share the event's identifier, so the event reports the flag that
  `[method: MethodImpl(…)]` sets, as part of its one rule. With the review rows,
  `DeterminismAnalyzer.cs` is at 98.72% line coverage, the same seven defensive lines uncovered.
- **MMO1002 does not list `ToString` (T154, T160, Phase 5 review).** The contract named `ToString` among
  the MMO1002 exclusions, but MMO1002 matches static methods only (the T160 decision above) and every
  `float.ToString` is an instance method; the core also formats floats in invariant round-trip form
  for error text (`Vec3.ToString`, `ConfigFields.FormatBound`), which is the same in every build. So the
  entry was dropped, the contract now says instance members are outside MMO1002, and `TableTests` pins
  every entry the contract still names.
- **The bound constants are pinned to research R3 (T156, Phase 5 review).** US3.8(a) says "within the
  documented error bound", and `EveryInDomainRowIsWithinItsBound` reads its bounds from
  `SimMathBounds`, so widening a constant would loosen the check with every test green; T166's "never
  widen a bound past the R3 table" was enforced only by review. `BoundsAreTheResearchR3Values` now pins
  each constant to its R3 value.
- **MMO1009's SimMath exemption is keyed by documentation ID (T147, T163, Phase 5 review, second
  pass).** It matched any method named `FloorToInt`, `RoundToInt` or `TruncateToInt` in `SimMath`, so a
  local function with one of those names inside another method, or another overload, could make raw
  casts. The contract already says the exemption covers the helpers' own bodies, and that one overload
  is keyed by its documentation comment ID. `Tables.SimMathIntegerHelpers` now holds the three IDs, and
  the check also requires `MethodKind.Ordinary`; a local function or lambda has no ID anyway.
- **Generic code cannot route around MMO1009 or float `%` (T160, T163, Phase 5 review, second pass).**
  `int.CreateSaturating(v)` with a type-parameter `TOther` went unreported, though the caller can bind
  it to `float`, and `int.CreateSaturating(float.NaN)` was measured to differ between builds; and `a % b` on a
  type parameter calls `IModulusOperators<,,>.op_Modulus`, not the built-in operator MMO1002 checked.
  Both report in the generic body, because the binding can be any number of calls away, unless the
  type parameter is constrained (directly or through another type parameter) to `IBinaryInteger<T>`,
  which no floating-point type implements. Keeping the `%` half was a judgment call: one reviewer
  suggested dropping it because float `%` is exact. But research R4 bans float `%` anyway, as unneeded,
  and the generic route is the same operation. The core has no generic numeric code, so the rule costs
  nothing, and integer generic `%` stays allowed.
- **Native code is MMO1008 (T163, Phase 5 review, second pass).** A `[DllImport]` or `InternalCall`
  `extern` method reported nothing, though it reaches native math (glibc on the server, musl in the
  browser build) that FR-012 and FR-014 keep out, just as `MathF.Sin` does. Every `extern` declaration
  (methods, local functions, accessors at their keyword, field-like events at their identifier) and a
  `[LibraryImport]` declaration, whose partial definition is not `extern`, now report MMO1008, and so
  does every member of `NativeLibrary` and `Marshal`. `DllImportAttribute` is not a table entry: it
  requires `extern`, so an entry for it would be redundant, and no test could catch it being dropped.
  This also closes what was left of the review's accessor finding. The previous pass already reported
  `Synchronized` accessors, local functions and lambdas, and a `[Conditional]` local function, and
  `[Conditional]` on an accessor does not compile (CS1667). An `[UnsafeAccessor]` on an `extern`
  accessor was still silent; now an accessor reports MMO1008 and then MMO1006 at its keyword.
- **A hash-ordered value that reaches a non-hash-ordered type without a conversion operation still
  reports (T161, Phase 5 review, second pass).** Three routes did not report: generic inference
  (`Sum(set)` with `Sum<T>(T e) where T : IEnumerable<int>`), a declaration pattern
  (`set is IEnumerable<int> e`), and a type parameter constrained to a listed type. A type parameter now
  counts as hash-ordered through its constraints, and declaration patterns and recursive patterns
  with a designation are registered. A hash-ordered type argument reports when its type parameter is
  constrained to a class or interface and to no hash-ordered type. That applies to a generic method's
  type parameters and to those of a member's containing type (`new Box<HashSet<int>>(set)`). Which type
  arguments count was a judgment call. Reporting every hash-ordered type argument whose type parameter
  is not hash-ordered, as one reviewer suggested, would also flag `First()` on a `List<HashSet<int>>`
  and every `Dictionary<int, HashSet<int>>`, which store and return the set with its own type intact.
  So an unconstrained type parameter does not report. The remaining gap is an unconstrained `T` that
  generic code type-tests back into an enumerable. The core has no such code, and the contract lists
  it under "Not covered". A type test with no designation does not enumerate, so it stays allowed.
- **The immutable builders, `StringDictionary` and `FrozenSet<T>.Items` are hash-ordered (T161, Phase 5
  review, second pass).** `ImmutableDictionary<,>.Builder` and `ImmutableHashSet<>.Builder` enumerate
  in their immutable type's hash order but derive from `object`, so derivation never caught them.
  `StringDictionary` wraps a `Hashtable` keyed by strings, whose hash is seeded per process. They are
  listed by name. `FrozenSet<T>.Items` is an `ImmutableArray<T>` in hash order, a view like
  `FrozenDictionary.Keys`, so `Items` joins `Keys` and `Values` as an enumerating property.
- **A partial member reports once (T163, Phase 5 review, second pass).** While adding the
  `[LibraryImport]` row, the declaration reported twice. Roslyn runs the symbol action for both parts
  of a partial method, property or event, so any partial member with a banned type or attribute
  reported at both identifiers (a partial `double M(double x)` gave four MMO1007). The symbol action now
  skips the implementation part (`PartialDefinitionPart` set). The defining declaration has the same
  signature and the attributes of both parts, so nothing is lost. `RuleTests` pins a
  partial method, property and event, and the contract's precedence section says so. The core has no
  partial members, so the backstop is unchanged.
- **PLINQ is MMO1006 (T162, Phase 5 review, third pass).** `a.AsParallel().Sum()` reported nothing,
  though `Parallel` was already banned. A PLINQ terminal operator blocks the caller on worker tasks just
  as `Parallel` does, and its output order and float reduction order vary between runs, so the sum's
  rounding varies too. `System.Linq.ParallelEnumerable` and the non-generic `System.Linq.ParallelQuery`
  join MMO1006's whole types; `ParallelQuery<T>` and `OrderedParallelQuery<T>` derive from the latter,
  so the existing derived-class rule covers them without entries of their own (which no test could
  tell apart from the base entry). The core uses no LINQ, so the backstop is unchanged.
- **Banned numeric types count through type arguments, and `Unsafe` is MMO1008 (T163, Phase 5 review,
  third pass).** `Unsafe.SizeOf<object>()` and `Unsafe.SizeOf<nint>()` return 4 on wasm32 and 8 on x64
  and reported nothing, and a generic call such as `Count<nint>(40)` that returns `int` escaped MMO1007,
  which checked only the expression's own type, against FR-012 and US3 scenario 7 and although the
  contract's MMO1007 row lists type arguments. MMO1007 now also scans the referenced member's type
  arguments, a generic method's own and its containing types', as MMO1001 already did for vectors. The
  containing type itself is left out, so a member of `double` reports through its operands and result
  rather than once more for its host. Banning all of `Unsafe`, not only `SizeOf`, was a judgment call:
  one reviewer offered `Unsafe.SizeOf` as the minimum, but `ByteOffset` and `AddByteOffset` also work in
  native-sized offsets and `As` reinterprets layout, the core compiles with unsafe code off and uses none
  of the type, and a whole-type entry closes the routes a member list would miss. `Marshal` was already
  a whole type (second pass), so `Marshal.SizeOf<nint>()` already reported MMO1008, which precedence
  keeps ahead of MMO1007.
- **A local declared by a pattern or a deconstruction is a declaration (T163, Phase 5 review, fourth
  pass).** Only declarators counted as local declarations, so `o is double d ? (float)d : 0f`,
  `var (a, b) = (1.0, 2.0)` and `o is not nint n` reported nothing, although the contract says a local
  reference is covered by its declaration and a `double` read narrowed to `float` reports nowhere else.
  A third operation action now checks a pattern's designation and a declaration expression's
  variables (`IsDeclaration` local references, which also cover `out var` and a deconstructing
  `foreach`) as a declarator's local. Two judgment calls: these locals are deduplicated per statement
  like a declarator's, so `double x = o is double d ? d : 0;` still reports once, at `x`; and they are
  reported by their own action rather than inside the pattern operation's single finding, because a
  declaration pattern already carries MMO1004, and folding both into one finding would let a vector or
  lock local outrank it against the precedence order. Pattern locals of vector and lock types now
  report too (MMO1001, MMO1006). The core has no such locals, so the backstop is unchanged.
- **`ValueTuple` hashes and a record class's synthesized hash are MMO1005 (T162, Phase 5 review,
  fourth pass).** MMO1005 allowed any `GetHashCode` override, but `ValueTuple<T1, T2>` through the
  eight-element tuple combine their elements through `HashCode`: `(1, 2).GetHashCode()` printed three
  different values in three runs here. A record class's synthesized `GetHashCode` mixes in
  `EqualityContract.GetHashCode()`, a `Type`'s identity hash, which stayed the same across runs but
  changed when the program hashed one other object first. Both are the seeds the rule names, reached
  through an override, so ``ValueTuple`2`` to ``ValueTuple`8``'s `GetHashCode` joined
  `Tables.AmbientMembers` and an implicitly declared `GetHashCode` on a record class reports. Which
  overrides to cover was a judgment call. One reviewer offered a doc-only fix that names the gap; but
  these two routes are cheap to match exactly, and a tuple hash is an easy thing to write in a step. A
  record struct's synthesized hash, `Tuple<…>`'s and a one-element `ValueTuple<T>`'s gave the same
  value in every run for `int` elements, so they stay allowed; with a `string` element they inherit its
  seed, which the contract lists under "Not covered" with the other indirect routes, rather than
  banning every record struct hash.
- **A call to another assembly's `[Conditional]` method is MMO1008, and so are `Contract`, `Debugger`
  and `AppContext` (T163, Phase 5 review, fifth pass).** MMO1008 matched a fixed list of types, so
  `Contract.Assert(b)`, which builds without `CONTRACTS_FULL` drop just as they drop `Debug.Assert`
  without `DEBUG`, `TraceSource.TraceInformation` (`TRACE`), `Debugger.IsAttached` and
  `AppContext.TryGetSwitch` reported nothing. A call now reports when the first method up its override
  chain that carries `[Conditional]` is declared in another assembly; the chain matters because C#
  makes an override of a conditional method conditional though the override cannot carry the
  attribute. Three judgment calls. A reviewer asked for every `[Conditional]` call "from any assembly",
  but a `[Conditional]` method the compilation declares already reports at its declaration, so its
  calls do not report again: the same rule the contract applies to locals, whose references are covered
  by their declaration, and it keeps a `[Conditional]` local function and its call at one diagnostic.
  `Contract` is banned as a whole type, not only through the attribute, because its members that are
  not `[Conditional]` (`Requires<TException>`, `Result<T>`) depend on a build-time rewriter. And
  `AppContext` is banned whole: its switches and data are set by the host, and the core reads no host
  configuration. The core uses none of them, so the backstop is unchanged.
- **Culture-sensitive formatting is listed under "Not covered", not given a rule (T160, T163, Phase 5
  review, fifth pass).** A reviewer found that the contract called `ToString` MMO1002; that wording was
  already gone (the "MMO1002 does not list `ToString`" bullet above), but the gap behind it was
  undocumented: a provider-less numeric `ToString()`, a `$"…"` to `string`, and `string.Compare`,
  `StartsWith`, `EndsWith`, `IndexOf`, `ToUpper` and `ToLower` without a `StringComparison` or
  `CultureInfo` follow the current culture. Adding a rule for them was a judgment call against. Every
  project sets `InvariantGlobalization=true` in `Directory.Build.props`, so the server and WebAssembly
  hosts run the same invariant culture; the core already formats numbers invariantly; and the hashed
  state holds only integers and float bits, so a culture can change an error message but not a step's
  result. The contract now names the gap and these backstops, and `TableTests` pins
  `x.ToString("R", CultureInfo.InvariantCulture)` as allowed, so the analyzer and the claim that
  instance members are outside MMO1002 cannot drift apart.
- **A statement nested without braces reports on its own (T159, Phase 5 review, fifth pass).** The
  deduplication walk started at the operation's parent, so in `foreach (var a in d) foreach (var b in h)`
  the inner loop's parent was the outer loop, which matched MMO1004 too, and the inner loop was dropped;
  `lock (a) lock (b) { }` likewise gave one MMO1006. With braces, the block between them stopped the walk
  and both reported, so the count depended on braces, against the contract's "each statement reports on
  its own". An operation that is a statement of its own is now its own nearest statement, and nothing
  covers it. A `foreach`'s loop variable shares its loop's syntax, so it is not treated as a statement
  of its own and is still covered by its loop: no rule can match both today, but keeping the walk for it
  states what "the same statement" means. The core has no such nesting, so the backstop is unchanged.
- **T169's spread distribution test uses the 0.1% χ² level and measures the angle of the normalised
  direction (Phase 6 red check).** With the named `SpreadSalt` 0x0015_5EED_0000_0004 and counters
  0..19,999, the data-model.md hash puts φ's 20,000 values in 16 bins with χ² = 27.78 (computed exactly
  from the integer hash), above the task's 5%-level bound of 25.0, although the hash is uniform: one
  fixed salt misses a 5% level one time in twenty. The versine bins give 27.75 against 30.1, too close
  to keep. Changing the salt until it passes would be choosing the data to fit the bound, so the salt
  stays and both bounds move to the 0.1% level: 43.82 for 19 degrees of freedom and 37.70 for 15. A
  faithful T182 `Spread` passes all of `SpreadTests` with them. The cone check also takes the acos of
  dir·f divided by |dir|, the angle between the two vectors: one sample sits at u1 = 0.9999987, where
  the float direction's length error alone (up to 1.3e-7, bounded by the |dir| assertion) moved the
  raw-dot angle within 1.6e-6 rad of the bound in a float simulation, so one ulp could fail a correct
  core. research.md R10's prototype figures are unchanged. spec.md's US4.2 and its "Spread rule"
  assumption now state the 0.1% level too (Phase 6 review), so the scenario and its test name the same
  level.
- **Two T170/T171 rows pin StepsSinceShot so that W4 does not act before the step they test (Phase 6
  red check).** A new weapon's StepsSinceShot is 65535, at or above every delay, so W4 runs first.
  `ShotReachingThresholdOverheats` would cool 2,700,000 to 2,650,000 before the shot, which then ends
  at 2,950,000 and does not overheat; the row starts at StepsSinceShot 5 (6 after W0, below the
  cooldown delay of 8). `AimWithRecoilWrapsYawAndClampsPitch` would recover the row's recoil by 61
  units before the shot (18 and −30 to 0, 100 to 39); the rifle starts at StepsSinceShot 0 (1 after
  W0, below the recovery delay of 3), and the test asserts the delay exceeds 1. T170 and T171 now say
  so.
- **A weapon outside Overheated loads only below the overheat threshold (T034, Phase 6 review).**
  `Reset` refused `heat` at or above `OverheatThresholdScaled + HeatPerShotScaled` whatever the phase,
  but the fire block reads heat only through the phase. A Ready weapon loaded at the threshold therefore
  fired, committed heat at that limit, and `Reset` then refused the world's own committed state. No valid
  run reaches such a weapon: only a shot raises heat, a shot reaching the threshold makes the weapon
  Overheated, and Overheated exits at `HeatRecoveryScaled`, below the threshold (R12). The limit is now
  per phase, as `phaseSteps`' is: below the threshold plus one shot's heat while Overheated, below the
  threshold otherwise, and both are `OutOfRange` like the `phaseSteps` ranges. The Ready row at
  3,299,999 with heat per shot became an Overheated row, a Ready row at 2,999,999 was added, Ready and
  Switching rows at the threshold are refused, and `AWeaponThatOverheatsCommitsAStateResetAccepts`
  shows that the step's highest committed heat (Overheated at the limit minus one) still loads.
  data-model.md#weaponstate and contracts/state-serialization.md give the rule.
- **US4.2 and the US4 Independent Test are split like US3.8, US5.5 and US5-IT (Phase 6 review).**
  "When spread is computed in any build" and "Run the same streams through the agreement suite" need
  the WebAssembly build, which slice 2 adds. Slice 1 tests the server halves (`SpreadTests` and T181);
  the cross-build half US4.2b and the agreement half US4-ITb, with FR-008's WebAssembly half, are owned
  by Phase 11's per-stream agreement tests over the weapon streams and are listed with the other
  slice-2 items in tasks.md T264.
- **`reload-and-empty` keeps WeaponSlot 1 selected after it releases Fire (T185, Phase 6 review).**
  T185 said "nothing is held until 4.5 s" after the slot-1 stretch without naming the slot, and the
  definition already kept slot 1, since a command's WeaponSlot is a selection and not a held button,
  and going back to slot 0 would start a second switch the scenario does not test. Phase 7 records the
  stream, so the choice is now written down: T185 and the contracts/simcore-cli.md row state it, and
  `ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks` checks that the stream has 4.5 · hz steps
  and that every step after 4 s holds no button with WeaponSlot 1 and reports no fire block.
- **`ProjectileWorlds.InFlight` takes the compiled configuration, and `PredictFirstHit` takes the
  first swept segment (T186, T187, Phase 7 red check).** T186's `InFlight(id, ownerId, classIndex,
  origin, launchVelocity, launchStep, step)` stores the closed form p(step − launchStep + 1), which
  needs the class's `HalfG` and the step rate, so the helper takes the `CompiledConfig` as well. A
  projectile loaded that way is first swept at k = step + 1 − launchStep ≥ 1, because the contract
  fixes its position as p(k + 1) with launchStep ≤ step (contracts/state-serialization.md), while
  T187's search started at k = 0: at 20 Hz a 600 m/s shot covers 30 m in segment 0, so a target
  25 m away would be predicted in a segment the world never sweeps. `PredictFirstHit` therefore takes
  an optional `firstK` (default 0, a projectile spawned in the step it is swept), and the tests of
  `InFlight` projectiles pass the first segment the world sweeps. A target inside segment 0 is
  reached only by a projectile spawned in that step. T186 and T187 now say so.
- **T189's random draws skip a 1 mm knife-edge band around each capsule's surface (Phase 7 red
  check).** In a float32 prototype of `ClippedTestMatchesADoubleReference`, one segment whose line
  entered its capsule only 3.4e-5 m deep had a hit-point error of 1.154e-4 m, over the task's
  1.1e-4 m bound (research.md R11's measured maximum). The cause is the float rounding of the inputs
  every implementation shares: b − a and a + ((b − a)·t0) are about 2e-6 m off across the line, and a
  grazing entry moves that about 50 times as far along it, so near the surface a pass or a failure,
  and a hit/miss agreement, depends on which samples are drawn rather than on the method. A segment
  whose least double distance to its capsule's axis lies within 1 mm of the radius is drawn again,
  so the test still checks exactly 100,000 segments with the 1.1e-4 m bound and zero disagreements;
  with the band the prototype's worst error was 2.4e-5 m. So that the band cannot hollow the test
  out, it requires at least 10,000 hits, 10,000 misses and 100 starts inside, and the known row
  `misses-by-1mm` still checks a segment 1 mm outside a capsule's side. T189 now says so.
- **T193's and T194's shooter stands at (0, 0, −25), 50 m from the target (Phase 7 red check).**
  Both tasks put the shooter at the origin, 25 m from the target, with the wall of
  `ProjectileWorlds.Wall()` fixed at z = 12.5 m, and fly an `InFlight` projectile. Such a projectile
  is first swept at segment k = 1 (T186), and segment 0 of a 600 m/s shot covers the first 30 m at
  20 Hz and 20 m at 30 Hz, so the world would never sweep the target at 20 Hz or the wall at 20 and
  30 Hz, and those rows could not pass. Moving the shooter back 25 m puts both beyond segment 0 at
  every rate while keeping the target at (0, 0, 25) and the wall where it is; the hit, part, point
  and step assertions are unchanged. At 50 m the uncompensated drop is 3.4 cm, so both aim with
  `ProjectileWorlds.AimWithDrop`, whose double path passes through the aimed centre, and each arrange
  asserts that `PredictFirstHit` names the row's part (and, for T194, that the target is hit without
  the wall). T196's world half spawns its projectile through the weapon step, the only way to sweep
  segment 0 in the world: its shooter stands at the target's feet with `hitboxes.eyeHeightStanding`
  1.21, so the spawned Origin is exactly the direct call's (0, 1.21, 0). T196 fixes the
  `ProjectileTargets` constructor as `(characterIds, hitboxes, bounds)` spans, which T199 and T210
  follow. The 25 m targets and the 12.5 m wall of T203's and T205's in-process rows meet the same
  segment-0 constraint. T193, T194, T196 and T210 now say so.
- **T197's exact ties are at t = 0, and its two targets share the first swept segment (Phase 7 red
  check).** The test cannot arrange an exact float tie inside a segment. A capsule's t goes through the
  bounding-sphere clip and t0 + ((t1 − t0)·tc), and a triangle's t goes through a different division,
  so two candidates that meet the segment at the same real point get t values whose last bits only the
  implementation fixes. Every correct build reports two kinds of tie exactly, both at t = 0. A segment
  that starts inside a capsule gives exactly 0 (data-model.md#projectile-step step 3). A segment that
  starts on a triangle's plane, where every coordinate along the plane's normal is equal, has a
  numerator of exactly ±0. So `overlapping-parts-lower-index` starts segment 1 inside both the crouched
  head and the crouched torso. `world-before-character` starts it on a mesh floor at y = 0, 5 cm in
  front of the standing target's feet and inside its legs. The floor sits over the flat heightfield sunk
  to −1 m, for two reasons: a mesh coplanar with the terrain would lose the tie to the lower terrain
  IDs, and a wall through the target would push the target out of its movement capsule (movement
  sub-step 7), taking its hitboxes with it. The launch puts p(1).y at exactly +0 in the contract's float
  expression: a level velocity and an origin height of −((HalfG·t)·t). The arrange checks the tie with
  the reference's segment tests on the swept float segment p(1) → p(2), because the double closed
  form's p(1) is about 1e-10 m off the plane. `nearer-target-first` puts targets 9 and 5 at z = 20 and
  24 m on the line, with the shooter's eye 600/hz m behind z = 15 m. Targets 20 and 24 m from the
  shooter could not share one swept segment at 60 Hz, where a segment is 10 m long, and then the step
  order, not the key, would pick target 9. T197 now says so.

## Delivery

The work ships as two vertical slices. Each is a PR that leaves `main` green.

1. **Core, analyzer, formats and server-side agreement** on `015-shared-simulation-core`. This covers
   the spec artifacts, US1, US3, US4, US5 and US6, except the cross-build halves of US3 scenario 8,
   US4 scenario 2 and US5 scenario 5 and the agreement halves of US4's and US5's Independent Tests
   (below), and the server half
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
   scenario 6, and its Independent Test) and US7.2, the cross-build halves of US3 scenario 8, SC-004,
   US4 scenario 2 (with FR-008's "in every build") and US5 scenario 5, and the agreement halves of
   US4's and US5's Independent Tests, which need a WebAssembly build:
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
