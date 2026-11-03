---
description: "Tasks for 013 Regionizer Simulator"
---

# Tasks: Regionizer Simulator

**Input**: Design documents from `/specs/013-regionizer-simulator/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md),
[data-model.md](data-model.md), [contracts/](contracts/)

**Tests**: Required (Principles VIII and IX). Each story writes its tests first and sees them fail,
then implements. Each acceptance scenario's test is named in its task.

**Autonomous delivery (Principle XIII)**: The agent executes every task. A checkpoint passes when its
automated checks are green.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: The user story the task belongs to

---

## Phase 1: Setup (Shared Infrastructure)

- [ ] T001 Pin the SDK and shared build settings: `global.json` (9.0.300, `rollForward: latestPatch`),
  `Directory.Build.props` (net9.0, nullable, `TreatWarningsAsErrors`, `AnalysisLevel` latest-recommended,
  `InvariantGlobalization`), `Directory.Packages.props` with the versions in research D13, and
  `.editorconfig` (.NET conventions)
- [ ] T002 Create `MmoEngine.sln` and the project skeletons from plan.md: `src/MmoEngine.Regionizer`,
  `src/MmoEngine.Maps`, `src/MmoEngine.Regionizer.Simulator`, `src/MmoEngine.Regionizer.Simulator.Cli`
  (assembly `regionizer-sim`), `src/MmoEngine.PortalValidator.Cli` (assembly `portal-validator`),
  `tests/MmoEngine.Regionizer.Tests`, `tests/MmoEngine.Maps.Tests`,
  `tests/MmoEngine.Regionizer.Simulator.Tests` and `benchmarks/MmoEngine.Regionizer.Benchmarks`. Add
  `InternalsVisibleTo` from the core to its test projects. Test parallelization stays on.
- [ ] T003 [P] Add `.github/workflows/dotnet.yml` (SHA-pinned actions, like the existing workflows):
  restore, `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test` with
  coverlet, and the changed-file coverage gate `tools/coverage_gate.py`. The gate fails when a `.cs`
  file under `src/` that the PR adds or changes is under 95% line coverage. Add its tests in
  `tests/workflows/test_coverage_gate.py`.
- [ ] T004 [P] Banned-API check for the simulated path, as a test: `tests/MmoEngine.Regionizer.Tests/Architecture/BannedApiTests.cs`
  scans `src/MmoEngine.Regionizer/**`, `src/MmoEngine.Maps/**` and every folder of
  `src/MmoEngine.Regionizer.Simulator/` except `Output/` and `Analysis/` for
  `Math.Sin|Cos|Tan|Atan|Exp|Log|Pow`, `MathF.`, `lock (`, `Monitor.`, `DateTime.Now|UtcNow` and
  `System.Random` (FR-016, FR-019). A real banned-API analyzer is N/A here: there are no gameplay
  assemblies yet.

---

## Phase 2: Foundational (Blocking Prerequisites)

- [ ] T005 Core value types and topology in `src/MmoEngine.Regionizer/Sections/`: `SectionKey`,
  `LayerSpec`, `PortalSpec`, `SectionTopology` (`OpenWorld`, `Build`, dense indexing, area tags,
  portal endpoint lookup, `TrySectionAt` using floor(x / s)) and the enums in
  `src/MmoEngine.Regionizer/Model.cs`, per contracts/regionizer-api.md
- [ ] T006 [P] Topology tests in `tests/MmoEngine.Regionizer.Tests/Sections/SectionTopologyTests.cs`:
  key↔index round trip over layers, floor rounding on edges and corners, positions outside a layer
  rejected, and a section size that isn't a power of two rejected (FR-001)
- [ ] T007 [P] PRNG in `src/MmoEngine.Regionizer.Simulator/Random/`: SplitMix64, xoshiro256**, FNV-1a-64
  stream derivation, `UnitDisc()` by rejection sampling, `NextDouble()` and `NextInt(min, max)`, with
  known-answer and stream-independence tests in `tests/MmoEngine.Regionizer.Simulator.Tests/Random/PrngTests.cs` (FR-016)
- [ ] T008 [P] Core test builders in `tests/MmoEngine.Regionizer.Tests/Support/RegionizerBuilder.cs`
  (topology, options and placed entities by explicit values) and `RecordingObserver.cs`. They hold no
  business rules.

**Checkpoint**: Solution builds, topology and PRNG tests are green.

---

## Phase 3: User Story 1 - Core invariants through merges and splits (P1) 🎯 MVP

**Goal**: The regionizer core library (FR-001–FR-015).

**Independent Test**: Simulation tests step ticks on a fixed time base, with `InvariantChecker.Check`
after every step.

### Tests for User Story 1 ⚠️ (write first, see them fail)

- [ ] T009 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Sections/ClaimGeometryTests.cs`:
  - a Theory over anchoring × entity class covering the D1 geometry;
  - a radius of 0 claims only the entity's own section;
  - claims are clipped at the map edge;
  - **US1.8** `SectionAnchoredClaimIsStableWithinSection_PositionAnchoredFollowsPosition` (FR-003, FR-004)
- [ ] T010 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Sections/ClaimReachabilityTests.cs`:
  - claims stop at area boundaries and closed portals;
  - claims pass through open portals across layers;
  - a CsCheck property that the fast path equals the walk (FR-003, D2)
- [ ] T011 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Regions/MergeTests.cs`:
  - **US1.1** `ClaimsSharingASectionMergeAtTickEnd_CauseIsMoverClass`, where the mover is a ground
    vehicle and the cause entity ID is asserted;
  - **US1.9** Theory `MergeGapDecidesWhetherOneSectionApartMerges`;
  - a three-region chain merge ends as one region whatever the order;
  - the lower ID absorbs;
  - a claim on a dead section forces a merge (FR-005)
- [ ] T012 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Regions/MergeLaterTests.cs`:
  - **US1.2** `TickingRegionNeverGrows_TransientAbsorbedAtTickEnd`;
  - a region that owes two ticking partners becomes transient and is absorbed by the later tick end;
  - a pending merge on a region that drops to 20 Hz still completes at its tick end;
  - a ticking region owing merges to two non-ticking regions absorbs both at its tick end in
    ascending ID order (FR-006, FR-007)
- [ ] T013 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Regions/TickEndAndSplitTests.cs`:
  - **US1.3** `TwoIslandsMeetingDampingSplit_EntitiesAndTimersFollowSections`;
  - **US1.4** `RegionYoungerThanTminDoesNotSplit`;
  - a Theory over N_min and p_dead thresholds;
  - dead sections are kept until the split check;
  - an island holding only dead sections creates no child;
  - the largest island keeps the parent ID and its age, while new children start at age zero;
  - the Theory includes a region exactly *T*min old (FR-007–FR-010)
- [ ] T014 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Regions/RegionStateTests.cs`:
  - **US1.6** a Theory over every state pair, `IllegalTransitionIsRejectedAndStateUnchanged`;
  - legal moves succeed (FR-011)
- [ ] T015 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Timers/TimerTests.cs`:
  - **US1.7** a Theory over timer modes, `TimerFiresExactlyOnceAtDelayAcrossMerge`;
  - firing across a split;
  - absolute mode fires at the first owner tick at or after the deadline when the owner ticks at
    20 Hz (FR-012)
- [ ] T016 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Portals/PortalToggleTests.cs`:
  - **US1.5** `ClosedPortalSeparatesRegions_OpeningMergesAtNextTickEnd`;
  - closing a portal allows a split once damping holds;
  - `PortalToggledDuringTickTakesEffectAtTickEnd` (FR-013)
- [ ] T017 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Entities/EntityLifecycleTests.cs`:
  - a two-step teleport never leaves an entity in two regions;
  - an unoccupied capture point claims only its own section;
  - a heavy projectile claims nothing;
  - removing every entity kills the region at its next tick end, even when the region is smaller
    than *N*min and younger than *T*min;
  - an entity in transit across a step boundary has no owner and the checker stays clean;
  - `LayerChangeOnlyThroughOpenPortalOrDirectEntryLayer`: a move to another layer is rejected unless
    it crosses an open portal from the entity's section, or either layer allows direct entry
    (FR-001, FR-002, FR-003, edge cases)
- [ ] T018 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Invariants/InvariantCheckerTests.cs`: one Fact
  per rule (1–6), each corrupting state through the internal test hooks and asserting the exact rule
  reported; a clean state reports none (FR-014)
- [ ] T019 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Properties/RegionizerPropertyTests.cs`: CsCheck
  random sequences of add, move, remove, teleport, portal, tick-start and tick-end operations in both
  anchoring modes and both merge gaps. The invariant checker reports nothing after every boundary, and
  timer offsets keep deadlines.
- [ ] T020 [P] [US1] `tests/MmoEngine.Regionizer.Tests/Architecture/CoreDependencyTests.cs`: the core
  assembly references only BCL assemblies and no package or project (FR-015, SC-007)

### Implementation for User Story 1

- [ ] T021 [US1] Entity table (structure of arrays) and pooled per-entity claim buffers in
  `src/MmoEngine.Regionizer/Entities/EntityTable.cs`
- [ ] T022 [US1] Claim computation (D1 geometry, D2 walk and fast path) in
  `src/MmoEngine.Regionizer/Sections/ClaimCalculator.cs`
- [ ] T023 [US1] Region store, the state machine (D5), pending-merge partners, the merge procedure
  (D3) and transient regions in `src/MmoEngine.Regionizer/Regions/`
- [ ] T024 [US1] Tick end, split damping, union-find islands and split callbacks (D4) in
  `src/MmoEngine.Regionizer/Regions/TickEnd.cs`
- [ ] T025 [US1] Timers in both modes (D6) in `src/MmoEngine.Regionizer/Timers/TimerStore.cs`
- [ ] T026 [US1] The `Regionizer` façade, observer calls, `IRegionDataExtension`, two-step teleport,
  portals, `RegionizerStats` and serialized-cost timestamps in `src/MmoEngine.Regionizer/Regionizer.cs`
- [ ] T027 [US1] `InvariantChecker` with an independent claim walk, plus the recorded rule 3 and rule 4
  breaches, in `src/MmoEngine.Regionizer/Invariants/`
- [ ] T079 [US1] Defer portal toggles made while any region is ticking until the last ticking region's
  tick ends (FR-013). Enforce the layer-change rule in `MoveEntity`, using `LayerSpec.AllowsDirectEntry`
  for the air layer (FR-001). Kill regions with no entities at tick end, whatever the damping (FR-008).

**Checkpoint**: All US1 tests are green.

---

## Phase 4: User Story 2 - Reproducible seeded runs with a complete record (P1)

**Goal**: The run pipeline: configuration, lockstep scheduling, Baseline, metrics, state hash and the
output folder.

**Independent Test**: Baseline run twice in separate processes gives identical hashes, and the output
folder matches contracts/run-output.md.

### Tests for User Story 2 ⚠️

- [ ] T028 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Config/ConfigValidationTests.cs`:
  - **US2.4** a Theory, `InvalidConfigIsRefusedNamingTheField`, over invalid fields:
    - section size not a power of two, merge gap 2, negative radius, p_dead 101;
    - population over the limit, unknown scenario, bad environment name;
    - a base step rate that isn't a multiple of the tick rates.
  - Each case fails with the field's JSON path.
  - A map file the portal validator rejects (`simulator/maps/test/seeded-leaks.json`) gives exit
    code 2 naming `map.file`.
  - Valid defaults load for every scenario (FR-018, FR-026).
- [ ] T029 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Determinism/DeterminismTests.cs`:
  - **US2.1** `SameSeedInSeparateProcessesGivesIdenticalHashes` (two CLI processes);
  - **US2.2** `DifferentSeedsGiveDifferentHashes`;
  - entity and region iteration is in ID order (FR-017, FR-019)
- [ ] T030 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Output/RunOutputTests.cs`:
  - **US2.3** `RunOutputHasManifestTicksEventsAndSummary`: manifest fields, one ticks row per tick,
    the event columns and summary percentiles;
  - the `_incomplete` marker is present during a run and absent after;
  - a rerun clears a run folder;
  - a non-empty folder that is not a run folder is refused with exit code 2 and nothing deleted
    (FR-024)
- [ ] T031 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Metrics/MetricsTests.cs`:
  - the fusion definition (each side holds a tag the other lacks);
  - the region count counts players only;
  - cross-region pairs use the deferred and read-only ranges, each pair counted once under the
    smallest range it falls within (a 200 m pair counts as deferred);
  - fused fight pairs;
  - an oversized fusion is flagged;
  - merge rate by causing class, split rate, split latency, section churn, region lifetime, and
    largest region size and share, each against exact values from a scripted run;
  - percentiles use nearest rank (FR-024)
- [ ] T032 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Movement/MovementModelTests.cs`: a
  Theory over classes:
  - speed never exceeds the configured ceiling;
  - infantry stay within their fight radius plus one step;
  - travellers stay on their lane;
  - aircraft altitudes are 50 or 300 m (FR-021)
- [ ] T033 [P] [US2] `tests/MmoEngine.Regionizer.Simulator.Tests/Cli/SimulatorCliTests.cs`: `run`
  exit codes 0, 2 and 3 (an injected violation). Arguments are validated.

### Implementation for User Story 2

- [ ] T034 [US2] Configuration records, deep merge over scenario defaults, canonical JSON, the config
  hash and FluentValidation validators in `src/MmoEngine.Regionizer.Simulator/Config/`, with defaults in
  `simulator/scenarios/*.json` (D12)
- [ ] T035 [US2] Map layout generator (lanes, bases), fights, population scaling and the Baseline
  scenario in `src/MmoEngine.Regionizer.Simulator/Scenarios/`
- [ ] T036 [US2] Movement models (data-model.md), using only IEEE basic ops, in
  `src/MmoEngine.Regionizer.Simulator/Movement/`
- [ ] T037 [US2] Lockstep scheduler, step loop, location queue and step-boundary events (D7) in
  `src/MmoEngine.Regionizer.Simulator/Scheduling/`
- [ ] T038 [US2] Metrics collector (observer), state hash (D8) and percentile summaries in
  `src/MmoEngine.Regionizer.Simulator/Metrics/`
- [ ] T039 [US2] Run writer (manifest, ticks.csv, events.csv, summary.json, the incomplete marker),
  git SHA and runtime capture in `src/MmoEngine.Regionizer.Simulator/Output/`
- [ ] T040 [US2] `regionizer-sim run` with Serilog compact JSON on stderr in
  `src/MmoEngine.Regionizer.Simulator.Cli/Program.cs`
- [ ] T080 [US2] Configurable tick rates (`scheduling.baseStepHz`, `tickHz`, `degradedTickHz`, validated
  and recorded in the manifest). The state hash includes each entity's section. Timer-overhead
  calibration for bookkeeping cost. Run-folder safety (refuse non-run folders).

**Checkpoint**: US2 tests are green, and Baseline runs end to end.

---

## Phase 5: User Story 3 - Interleaved scheduling and monitors (P2)

**Goal**: Interleaved mode, the monitors after every step, synthetic timers, and the Migration,
Stacked areas, Air traffic and Soak scenarios.

**Independent Test**: Migration interleaved is clean and reproducible. Injected corruption fails the
run at its tick. A shortened Soak is clean, and every timer fires exactly once.

### Tests for User Story 3 ⚠️

- [ ] T041 [P] [US3] `tests/MmoEngine.Regionizer.Simulator.Tests/Scheduling/InterleavedSchedulingTests.cs`:
  **US3.1** `MigrationInterleavedHitsTwentyHzAndMergeLaterWithZeroViolations`. It checks at least one
  20 Hz tick, at least one merge between regions with different tick counters, zero violations and
  identical rerun hashes (FR-018).
- [ ] T042 [P] [US3] `tests/MmoEngine.Regionizer.Simulator.Tests/Monitors/MonitorTests.cs`:
  - **US3.2** `SectionInTwoRegionsFailsRunAtThatTickWithRuleAndSeed`;
  - the timer monitor reports a deliberately late timer through its test hook (FR-020)
- [ ] T043 [P] [US3] `tests/MmoEngine.Regionizer.Simulator.Tests/Scenarios/SoakTests.cs`: **US3.3**
  `ShortSoakInterleavedHasZeroViolationsAndExactTimers` (FR-012, FR-020)

### Implementation for User Story 3

- [ ] T044 [US3] Interleaved scheduler (seeded interleaving, 20 Hz drops) in
  `src/MmoEngine.Regionizer.Simulator/Scheduling/InterleavedScheduler.cs`
- [ ] T045 [US3] Invariant monitor wiring and the timer shadow monitor in
  `src/MmoEngine.Regionizer.Simulator/Monitors/`, with synthetic timers from the timers stream
- [ ] T046 [US3] Migration, Stacked areas (with `simulator/maps/stacked-base.json` loaded through
  MmoEngine.Maps and validated), Air traffic (air layer with hysteresis) and Soak scenarios in
  `src/MmoEngine.Regionizer.Simulator/Scenarios/`

**Checkpoint**: US3 tests are green.

---

## Phase 6: User Story 4 - Every scenario and sweep from configuration (P2)

**Goal**: The remaining scenarios, the sweep driver, and trace recording and replay.

**Independent Test**: Every scenario runs clean briefly. A sweep gives one result per cell and seed,
identical at any parallelism. A replayed trace reproduces the hashes.

### Tests for User Story 4 ⚠️

- [ ] T047 [P] [US4] `tests/MmoEngine.Regionizer.Simulator.Tests/Scenarios/ScenarioTests.cs`:
  - **US4.1** a Theory over all nine scenarios, `EachScenarioRunsCleanAndWritesOutput`;
  - **US4.5** `AirLayerPutsHighAircraftOnTheirOwnLayer`, which also asserts aircraft-caused merges
    above 0 with and without the layer;
  - **US4.6** a Theory over all nine scenarios, `EachScenarioRerunGivesIdenticalHashes` (FR-023, SC-001)
- [ ] T048 [P] [US4] `tests/MmoEngine.Regionizer.Simulator.Tests/Sweep/SweepDriverTests.cs`:
  - **US4.2** `SweepProducesOneReproducibleResultPerCellAndSeed`;
  - **US4.3** `SweepResultsDoNotDependOnParallelism`;
  - seed *i* is the same across cells (FR-025)
- [ ] T049 [P] [US4] `tests/MmoEngine.Regionizer.Simulator.Tests/Trace/TraceReplayTests.cs`: **US4.4**
  `ReplayedTraceReproducesRecordedHashes` (FR-022)

### Implementation for User Story 4

- [ ] T050 [US4] Spacing, Lane traffic (clumped or uniform), Vehicle columns and One base scenarios in
  `src/MmoEngine.Regionizer.Simulator/Scenarios/`
- [ ] T051 [US4] Sweep spec, cell expansion, common seeds, the parallel runner and `cells.csv` in
  `src/MmoEngine.Regionizer.Simulator/Sweep/`, plus `regionizer-sim sweep`
- [ ] T052 [US4] Trace writer and reader, and the replay movement source, in
  `src/MmoEngine.Regionizer.Simulator/Trace/`, plus `regionizer-sim replay`

**Checkpoint**: US4 tests are green.

---

## Phase 7: User Story 5 - The portal validator finds every leak (P2)

**Goal**: The map format, its loader and the portal validator CLI.

**Independent Test**: Seeded-leak maps report exactly their seeded leaks. The clean map reports
nothing.

### Tests for User Story 5 ⚠️

- [ ] T053 [P] [US5] `tests/MmoEngine.Maps.Tests/MapLoaderTests.cs`:
  - rectangles and the default area;
  - overlapping rectangles are an error;
  - an unknown format version is an error;
  - the conversion to `SectionTopology` (FR-029)
- [ ] T054 [P] [US5] `tests/MmoEngine.Maps.Tests/PortalValidatorTests.cs`:
  - **US5.1** `SeededLeakMapReportsExactlyTheSeededLeaks`;
  - **US5.2** `CleanMapReportsNothing`;
  - **US5.3** `PortalToNonNavigableSectionIsDead`;
  - **US5.4** `PortalWithoutClosedPolicyIsReported`;
  - **US5.5** `SealedAreaReachableWithPortalsClosedIsReported` (FR-030)
- [ ] T055 [P] [US5] `tests/MmoEngine.Maps.Tests/PortalValidatorCliTests.cs`: exit codes 0, 1 and 2,
  and the `--json` output

### Implementation for User Story 5

- [ ] T056 [US5] Map model and JSON loader in `src/MmoEngine.Maps/` (contracts/map-format.md)
- [ ] T057 [US5] `PortalValidator` (Leak, DeadPortal, MissingPolicy, SealingFailure) in
  `src/MmoEngine.Maps/PortalValidator.cs`
- [ ] T058 [US5] `portal-validator` CLI in `src/MmoEngine.PortalValidator.Cli/Program.cs`
- [ ] T059 [US5] Test maps `simulator/maps/test/clean.json`, `seeded-leaks.json` (with
  `seeded-leaks.expected.json`), `dead-portal.json`, `missing-policy.json` and `unsealed.json`

**Checkpoint**: US5 tests are green.

---

## Phase 8: User Story 6 - Analysis and the decision memo (P3)

**Goal**: The three analytic checks against the simulator, and the memo generator.

**Independent Test**: The worked examples match. The merge-distance and lane-bridging checks pass
against the core. The memo has every row with its evidence.

### Tests for User Story 6 ⚠️

- [ ] T060 [P] [US6] `tests/MmoEngine.Regionizer.Simulator.Tests/Analysis/AnalyticChecksTests.cs`:
  - **US6.1** `ClaimRadiusFloorForClosingAircraftIsRangePlus8m`;
  - **US6.3** a Theory, `LaneBridgingEstimateMatchesWorkedExamples` (30, 60 and 120 travellers give
    0.04%, 4% and 74%) (FR-031)
- [ ] T061 [P] [US6] `tests/MmoEngine.Regionizer.Simulator.Tests/Analysis/SimulatorAgreementTests.cs`:
  - **US6.2** a Theory over anchoring and gap, `MergeDistanceBoundsHoldAgainstTheCore`;
  - lane-bridging simulation within 10 percentage points at each step, with at least 200 trials;
  - **US6.1** (second part) `ClaimRadiusFloorHoldsInTheCore_BelowFloorFails`, the floor check of
    research D16 (FR-031, SC-006)
- [ ] T062 [P] [US6] `tests/MmoEngine.Regionizer.Simulator.Tests/Analysis/MemoGeneratorTests.cs`:
  **US6.4** `MemoHasARowPerValueWithEvidence`, from a small generated result set (FR-032)

### Implementation for User Story 6

- [ ] T063 [US6] Analytic formulas and the simulator agreement checks in
  `src/MmoEngine.Regionizer.Simulator/Analysis/`, plus `regionizer-sim check`
- [ ] T064 [US6] Memo rules (D15), the curve readers and the Markdown memo writer in
  `src/MmoEngine.Regionizer.Simulator/Analysis/Memo/`, plus `regionizer-sim memo`

**Checkpoint**: US6 tests are green.

---

## Phase 9: User Story 7 - Bookkeeping cost against its budget (P3)

**Goal**: The bookkeeping cost measurement, zero allocation and benchmarks.

**Independent Test**: One base and Baseline summaries carry the bookkeeping rows. Steady-state ticks
allocate nothing.

### Tests for User Story 7 ⚠️

- [ ] T065 [P] [US7] `tests/MmoEngine.Regionizer.Simulator.Tests/Performance/BookkeepingCostTests.cs`:
  **US7.1** a Theory over One base and Baseline at 5,000 entities,
  `SummaryRecordsBookkeepingPercentilesSerializedAndBudget` (FR-027)
- [ ] T066 [P] [US7] `tests/MmoEngine.Regionizer.Tests/Allocation/ZeroAllocationTests.cs`: **US7.2**
  `SteadyStateBookkeepingAllocatesNothing` (FR-028). It opts out of parallel execution, with the
  reason (allocation counters) documented in the test.

### Implementation for User Story 7

- [ ] T067 [US7] Remove the allocations the test finds on the steady-state path in
  `src/MmoEngine.Regionizer/`, and wire the per-step total and serialized cost into the metrics
  (`src/MmoEngine.Regionizer.Simulator/Metrics/`)
- [ ] T068 [P] [US7] BenchmarkDotNet benchmarks (move with no section crossing, move with a crossing,
  tick start and end, a 5,000-entity step at s = 8, 16, 32, 64) in
  `benchmarks/MmoEngine.Regionizer.Benchmarks/BookkeepingBenchmarks.cs`

**Checkpoint**: US7 tests are green.

---

## Phase 10: Polish & Cross-Cutting Concerns

- [ ] T069 Sweep specs for every row of D15 in `simulator/sweeps/*.json`, plus
  `simulator/sweeps/memo-rules.json`:
  - the population per row as D15 sets it;
  - the measured options (merge gap 0 vs 1, tick-counter vs absolute timers) from Migration in
    interleaved mode
- [ ] T070 Run the sweeps (Release, all cores). Commit their summary tables to `docs/simulator/results/`
  and the generated `docs/simulator/decision-memo.md`. Raw run folders stay local. (SC-005)
- [ ] T071 Run the 24-hour simulated Soak in interleaved mode, and record its manifest and summary in
  `docs/simulator/results/soak-24h/` (SC-002)
- [ ] T072 [P] Run the benchmarks, and attach the results to the PR and to
  `docs/simulator/results/benchmarks.md` (SC-004)
- [ ] T073 [P] Add an automated quickstart check, `tests/MmoEngine.Regionizer.Simulator.Tests/Cli/QuickstartTests.cs`,
  that runs quickstart.md's commands at small sizes
- [ ] T074 Review the tests:
  - Theories cover the data variations;
  - unique temp folders per test;
  - simulation failures print their seed;
  - no skipped tests
- [ ] T075 License review against research D13, and the Folia sources in research.md
- [ ] T076 Add a `docs/patch-notes.md` entry: behavior and contract changes; the shared pieces to reuse
  (the core library, output formats, map format); open follow-ups (.NET 10, Sonar and Codecov
  accounts, sub-spec 2, open decision 1 recommendations)
- [ ] T077 **Before the PR**: bring spec.md and tasks.md up to date with the work done. Check every
  acceptance scenario and FR maps to a passing test. Run the 95% changed-file coverage gate locally.
- [ ] T078 Audit for human steps (Principle XIII): none in the spec, plan, tasks or PR description

---

## Dependencies & Execution Order

- Setup (T001–T004) → Foundational (T005–T008) → US1 (T009–T027).
- US2 depends on US1 (it drives the core). US3, US4, US6 and US7 depend on US2's run pipeline.
- US5 depends only on Foundational (T005) and can run in parallel with US1–US4.
- US3's Soak uses the scenarios it builds itself (Migration, Stacked areas, Air traffic). US4's
  scenario Theory includes them.
- US6's simulator agreement tests use the core directly and can start after US1. Its memo needs
  only the output contracts.
- Polish T069–T071 need US2–US4 and US6. T072 needs US7.

## Parallel Opportunities

- After US1, these can run as separate workstreams, each confined to its own files:
  - US5 (Maps and validator);
  - US6's analytic checks and memo (Analysis/);
  - US7's benchmarks.
- All test tasks marked [P] within a story can run together.

## Implementation Strategy

1. Setup and Foundational, then US1. The core is the MVP: everything else measures it.
2. US2, the run pipeline, then US3 and US4 for the scenario set, with US5 in parallel.
3. US6 and US7, then the polish phase: sweeps, the memo, the soak and benchmarks.
