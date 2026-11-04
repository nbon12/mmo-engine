---
description: "Task list for the telemetry and test harness (Pre-Phase 1 sub-spec 2)"
---

# Tasks: Telemetry and Test Harness

**Input**: Design documents from `specs/014-telemetry-test-harness/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: Tests are required, and they come first (Principles VIII and IX). Every acceptance scenario
maps to the test named in its task.

**Autonomous delivery (Principle XIII)**: The agent does every task. A checkpoint is passed when its
automated checks are green.

**Delivery** (plan.md):

| Slice | Stories | Status |
| --- | --- | --- |
| 1 | Setup, US1, US2, US3 | Done (first PR) |
| 2 | US4, US5 | Done (second PR) |
| 3 | US6, including the CI workflow and the golden-run job | Not started |
| 4 | US7 | Not started |

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Setup

- [x] T001 Add the projects to `MmoEngine.sln`: `src/MmoEngine.Harness`, `src/MmoEngine.Harness.Cli` (`harness`) and
  `tests/MmoEngine.Harness.Tests`. Add the packages to `Directory.Packages.props`: `ClickHouse.Driver` 1.5.0,
  `YamlDotNet` 18.1.0 and `Testcontainers.ClickHouse` 4.16.0, all MIT (research R1, R2).
- [x] T002 [P] Add an assembly-wide ClickHouse fixture (Testcontainers, `clickhouse/clickhouse-server:25.8`) and
  run-folder helpers that give every test unique run IDs and temp folders, in `tests/MmoEngine.Harness.Tests/Support/`.
- [x] T003 [P] Record the fixtures with `regionizer-sim`: a 60-tick Baseline run and a two-cell sweep, in
  `tests/MmoEngine.Harness.Tests/Fixtures/`.
- [x] T004 Add validated settings (FR-034) in `src/MmoEngine.Harness/HarnessSettings.cs`: the environment name, and the
  connection string from `--clickhouse` or `MMO_CLICKHOUSE`. Add the exit codes in `HarnessExitCodes.cs`.

## Phase 2: User Story 1 - Every run becomes rows in the store (P1)

**Independent Test**: `LoaderTests` loads the recorded run and the sweep into a real ClickHouse, compares counts and
values with the files, and loads them again.

- [x] T010 [P] [US1] Tests in `tests/MmoEngine.Harness.Tests/Store/LoaderTests.cs`:
  - Scenario 1: `CompletedRunLoadsOneRunRowAndEveryTickAndEventRow`.
  - Scenario 2: `LoadingTheSameRunAgainAddsNoRows`, and the edge case `PartialLoadIsRedoneWithoutDuplicates`.
  - Scenario 3: `IncompleteRunFolderIsRefusedAndLoadsNothing`.
  - Scenario 4: `UnknownFormatVersionIsRefusedNamingTheVersion`.
  - Scenario 5: `SweepLoadsEveryCompletedRunWithItsSweepAndCell`, plus
    `SweepWithoutRunFoldersLoadsItsCellsAndCountsMissingFolders`.
  - Scenario 6: `ApplyingTheSchemaTwiceSucceedsAndEveryTableExists`.
  - FR-002: `RawTablesExpireAfterFourteenDaysAndRetainedTablesDoNot`.
- [x] T011 [P] [US1] Reader tests in `tests/MmoEngine.Harness.Tests/Store/RunFolderReaderTests.cs`: CSV quoting,
  invalid values naming the file, line and column, missing files, and the committed first-pass sweeps parsing.
- [x] T012 [US1] DDL for every table in `src/MmoEngine.Harness/Store/Schema/001..013_*.sql`, and `SchemaScripts`
  (FR-001, FR-002, contracts/store-ddl.md).
- [x] T013 [US1] `ClickHouseStore`: batched inserts of 5,000 rows, the runs commit marker, lightweight deletes and
  retention copies (research R3, R4), in `src/MmoEngine.Harness/Store/`.
- [x] T014 [US1] `RunFolderReader`, `RunLoader` and `SweepIndex` in `src/MmoEngine.Harness/Loading/` (FR-004 to FR-008).
- [x] T015 [US1] `harness schema apply` and `harness load`, in `src/MmoEngine.Harness.Cli/Commands.cs`. CLI tests are
  in `tests/MmoEngine.Harness.Tests/Cli/HarnessCliTests.cs`.

**Checkpoint**: The loader tests and CLI tests are green.

## Phase 3: User Story 2 - A SQL rule with a threshold decides pass or fail (P1)

**Independent Test**: `RuleRunnerTests.ZeroViolationsRuleFailsARunWithOneViolation` and
`FailingRunIsRetainedAndKeepsManifestSeedAndInputs`.

- [x] T020 [P] [US2] Store-backed tests in `tests/MmoEngine.Harness.Tests/Rules/RuleRunnerTests.cs`:
  - Scenario 1: `ZeroViolationsRuleFailsARunWithOneViolation`.
  - Scenario 2: `ZeroViolationsRulePassesACleanRun`.
  - Scenario 3: `RuleBelowItsSampleFloorIsInconclusive`.
  - Scenarios 4 and 5: `BaselineRuleReportsWithoutBaselineAndFailsBeyondTheRegression`.
  - Scenario 6: `FailingRunIsRetainedAndKeepsManifestSeedAndInputs`.
  - Scenario 8: `RuleForAnotherRunKindIsSkipped`.
  - Edge cases: `BrokenQueryIsAnErrorNotAPass` and `RunThatIsNotLoadedIsRefused`.
- [x] T021 [P] [US2] Pure tests in `tests/MmoEngine.Harness.Tests/Rules/RuleEvaluatorTests.cs`:
  - Scenarios 1 and 2: `FixedZeroViolationsRuleFailsOnOneAndPassesOnNone`.
  - Scenario 3: `BelowTheSampleFloorIsInconclusive`.
  - Scenario 4: `BaselineRuleWithoutBaselineReportsOnly`.
  - Scenario 5, at 110 and 111: `BaselineRuleFailsBeyondTheAllowedRegression`.
  - FR-015: `ExitCodeAndVerdictAreTheWorstOutcome`.
- [x] T022 [P] [US2] Scenario 7: `RuleFileParserTests.InvalidRuleFileIsRefusedNamingFileAndField`, and through the
  CLI, `HarnessCliTests.InvalidRuleFileExitsTwoNamingFileAndField`. Baseline-file validation (FR-014):
  `BaselineWithoutSourceRunIsRefused`.
- [x] T023 [US2] `RuleDefinition`, `RuleFileParser`, `RuleEvaluator`, `RuleRunner` and `BaselineFile` in
  `src/MmoEngine.Harness/Rules/` (FR-009 to FR-016, contracts/rule-file.md).
- [x] T024 [US2] The committed rules `harness/rules/sim.violations.yaml`, `sim.timer_faults.yaml`,
  `bot.correction_p99.yaml` (the issue's example) and `bot.host_health.yaml`, and `harness/baselines/README.md`.
  Tested by `CommittedRulesParse` and `CommittedTimerFaultRuleCountsTimerFaultEvents`.
- [x] T025 [US2] `harness rules run` and `harness rules baselines sync`. Tested by
  `LoadThenRulesRunFailsCiOnAViolationAndKeepsArtifacts`, `RulesRunPassesACleanRun` and
  `BaselinesSyncCopiesCommittedFilesIntoTheTable`.

**Checkpoint**: A failing rule exits 5 from the CLI, and the run's artifacts are written.

## Phase 4: User Story 3 - Golden runs prove determinism in CI (P2)

**Independent Test**: `GoldenRunTests` records a real simulator run, replays it in process, and then replays it again
with a changed parameter.

- [x] T030 [P] [US3] Tests in `tests/MmoEngine.Harness.Tests/Golden/GoldenRunTests.cs`:
  - Scenario 1: `ReplayOnTheSameRunnerClassMatchesEveryTick`.
  - Scenario 2: `ReplayWithAChangedParameterIsReportedAtItsFirstDivergentTick`, `DivergenceFromTickTIsReportedAtTickT`
    and `ShorterReplayDivergesAtItsFirstMissingTick`.
  - Scenario 3: `GoldenFromAnotherRunnerClassIsNotComparable`.
  - Scenario 4: `ManifestRecordsTheRunnerClassAndEveryIntrinsicVariable`.
  - Edge cases: `GoldenInAnotherFormatVersionIsNotComparable` and `ReplayInAnotherRunFormatVersionIsNotComparable`.
- [x] T031 [P] [US3] Runner-class tests in `RunFolderReaderTests.cs` (`RunnerClassTests`). The simulator manifest's
  CPU model is tested by `RunOutputTests.CpuModelReadsCpuInfoOrFallsBackToUnknown`.
- [x] T032 [US3] `RunnerClass` (research R5), and `runtime.cpuModel` and `runtime.runnerClass` in the simulator's
  manifest. These are additive, so the run-output format stays at version 1, and the contract is updated.
- [x] T033 [US3] `GoldenRuns.Record` and `GoldenRuns.Compare` (FR-018 to FR-020, contracts/golden-run.md), and
  `harness golden record` and `harness golden check`. Tested by `GoldenRecordThenCheckReportsMatchAndDivergence`.
- [ ] T034 [US3] Commit golden runs in `harness/golden/` recorded on the CI runner class, and a CI job that replays
  them (slice 3). This needs the workflow, because a golden run recorded on any other machine is "not comparable"
  in CI by design.

## Phase 5: User Story 4 - Network profiles are defined, applied and calibrated (P2), slice 2

**Independent Test**: `CalibrationTests` gives synthetic sample sets their documented verdicts, and
`ProbePairTests.LoopbackProbeRunRecordsEverySentPacketAndLosesNone` runs the probe pair over loopback.

- [x] T040 [P] [US4] Scenario 1: `NetworkProfileTests.CatalogueHoldsEachProfileAndItsReorderingVariant`, with
  `TolerancesAreTheIssuesProposals`, the profile-file refusals and `CalibratedSettingsRoundTripAndTakePrecedence`
  (FR-026).
- [x] T041 [P] [US4] `tests/MmoEngine.Harness.Tests/Network/CalibrationTests.cs`:
  - Scenario 2: `SamplesWithinToleranceOfTheProfilePass`, for each profile.
  - Scenario 3: `JitterBeyondToleranceFailsNamingJitter`, plus `RoundTripBeyondToleranceFailsNamingRoundTrip`.
  - Scenario 4: `LossBeyondToleranceInOneDirectionFailsNamingThatDirection`.
  - Scenario 5: `ReorderingFailsUnlessTheProfileAllowsIt`.
  - Scenario 6: `InterarrivalJitterIsReportedForEachDirection` and `InterarrivalJitterFollowsTheRfcRecurrence`.
- [x] T042 [P] [US4] `tests/MmoEngine.Harness.Tests/Network/ImpairmentTests.cs`:
  - Scenario 7: `NetemSplitsPerDirectionSeedsLossAndAddsAFifoChild`, `NetemReorderingVariantAddsReorderAndNoFifoChild`
    and `TcpIngressRedirectsThroughIfb`.
  - Scenario 8: `NetworkChaosSetsDelayJitterLossDirectionAndTargetPods`.
- [x] T043 [P] [US4] Scenario 9: `ProbePairTests.LoopbackProbeRunRecordsEverySentPacketAndLosesNone`, plus
  `ProbeRowsRoundTripThroughNetSamples`, `LostPacketsMapToTheirDirection`, `ServerDropsForeignAndOversizedDatagrams` and
  `NoServerMeansEveryPacketIsLostAndTheReportIsIncomplete`.
- [x] T044 [US4] The implementation:
  - `harness/profiles/*.json`;
  - `src/MmoEngine.Harness/Network/`: `NetworkProfile`, `ProfileCatalog`, `Calibration`, `NetemScript`,
    `NetworkChaosManifest`, `ProbeServer`, `ProbeClient` and `NetSamples`;
  - `contracts/network-profile.md`;
  - the commands `harness profile list|netem|chaos`, `harness probe serve|run` and `harness calibrate`, tested in
    `Cli/NetworkCliTests.cs`.
- [ ] T045 [US4] Calibrate the four profiles and their variants with netem on the CI runner class, 5 minutes each,
  and commit the calibrated settings. This is slice 3: it needs root or Chaos Mesh, which only the CI job has.

## Phase 6: User Story 5 - Bots and a stub target prove the measurement path (P3), slice 2

**Independent Test**: `BotHostTests` runs the stub target and a bot host over loopback.

- [x] T050 [P] [US5] `tests/MmoEngine.Harness.Tests/Bots/BotHostTests.cs`. These run in a collection with parallelism
  off, for frame timing.
  - Scenario 1: `FourBotsForTwoSecondsWriteEachFrameOnceAndInOrder`.
  - Scenario 2: `BotsRecordSnapshotsAndTheStubEchoesEveryInput`, plus `UdpEdgeCaseTests.BotsAgainstAClosedPortRecordUnderrunsAndNoSnapshots`.
  - Scenario 3: `HostThatMissesDeadlinesIsInvalidAndHostHealthIsInconclusive`, against ClickHouse with the committed
    `bot.host_health` rule. Also `HealthyHostPassesHostHealth`.
  - Scenario 4: `WriterFlushesAtFiveThousandRowsAndFinallyAtCompletion` and `WriterFlushesATrickleOnceASecond`.
- [x] T051 [US5] The implementation:
  - `src/MmoEngine.Harness/Bots/`: `StubTarget`, `BotHost` (research R8), `BatchWriter`, `StoreFrameSink` and
    `MemoryFrameSink`;
  - `ClickHouseStore.MarkBotHostInvalidAsync`;
  - the commands `harness stub-target` and `harness bots run`, tested by `NetworkCliTests.BotsRunWritesFramesAndARunRow`.

## Phase 7: User Story 6 - The harness comes up with one command (P3), slice 3

- [ ] T060 [US6] `infra/harness/up.sh`, `kind-config.yaml`, and the kustomize base and observability overlay
  (research R9).
- [ ] T061 [US6] `.github/workflows/harness.yml`:
  - the stack job runs scenarios 1 to 5 by running `up.sh` twice, checking the tables, loading a run and running
    the rules;
  - the golden-run job runs T034.

  It touches guarded paths, so it gets the independent review.

## Phase 8: User Story 7 - The replay viewer (P4), slice 4

- [ ] T070 [P] [US7] Tests for scenarios 1 and 2 that compare the SVG's elements with the regionizer state at the tick.
- [ ] T071 [US7] `regionizer-sim view` (research R10).

## Phase 9: Polish

- [x] T090 A patch-notes entry for each slice.
- [x] T091 The quickstart commands run against a local ClickHouse container: an 18,000-tick run loaded in 1.2 s
  (SC-001, a measurement on this session's x64 container), and all 13 first-pass sweeps loaded.
