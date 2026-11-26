---

description: "Task list for vehicle physics engine selection (feature 016)"
---

# Tasks: Vehicle Physics Engine Selection

**Input**: Design documents from `specs/016-vehicle-physics-selection/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md,
quickstart.md, contracts/ (ci-jobs, decision-record, engine-adapter, license-scan, selection-config,
selection-report, vehicle-config, vehicle-formats, vehicles-cli)

**Tests**: Tests are REQUIRED. Per the constitution (Principles VIII and IX), every acceptance
scenario maps to an automated test, tests are written first and must fail before implementation,
and the completion gate requires every applicable suite to pass. Every acceptance scenario and
Independent Test of spec.md is mapped to its task and test in [Scenario coverage](#scenario-coverage).
Denial scenarios (a refusal, a NotComparable stream, a Failed, NotBuilt, NotTested or LicenseBlocked
status, a None outcome, a stale record) have their own red tasks that assert the exact code, reason
and exit code.

**Organization**: Tasks are grouped by user story. Every story except US7 is split across the two
delivery slices (plan.md, Delivery): its slice-1 phase builds what runs on the fixture engine, Bepu's
server build and fixture inputs, and its slice-2 phase adds Jolt, the browser builds, the CI jobs and
the committed decision.

**Autonomous delivery (constitution Principle XIII)**: Every task is executed by the agent. No task,
checkpoint or checklist item assigns work to a person or waits on manual verification, review,
approval or sign-off. A checkpoint is passed when its automated checks are green. The guarded-path
review in Phase 19 is the independent automated review of `.routines/merge-policy.md`, run in a
subagent.

**Open decisions**: none is settled here. Open decision 3 (tick rate) stays open: vehicle tests run
at 20, 30 and 60 Hz through Theories, and no task fixes a rate. Open decisions 4, 5 and 9 are
untouched. Gate 9's WebAssembly build mode stays open: the decision rule runs per mode (FR-026) and a
mixed result is conditional. Parallel phases stay open: engines step single-threaded and the
threaded runs are evidence only (FR-027).

**Delivery** (plan.md): **slice 1** is Phases 1-10 on `016-vehicle-physics-selection`, one pull
request that touches no guarded path. **Slice 2** is Phases 11-19 on
`016-vehicle-physics-selection-slice-2`, cut from `main` after slice 1 merges; it touches
`.github/workflows/simcore.yml`, so it takes the independent automated review. See
[Delivery](#delivery) for the task ranges.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an unfinished task in the phase).
  Green tasks that edit `src/MmoEngine.SimCore/World/SimWorld.cs`, `src/MmoEngine.SimCore.Cli/Program.cs`,
  `src/MmoEngine.SimCore.Cli/Commands/VehiclesCommand.cs`,
  `src/MmoEngine.SimCore.Vehicles.Selection/Orchestration/SelectionRun.cs` or
  `.github/workflows/simcore.yml` are never [P].
- **[Story]**: Which user story the task belongs to (US1 to US7). Setup, Foundational and Polish tasks
  carry no story label.
- Every task names its exact repo-relative paths. Red tasks name the test (`Class.Method`), its file,
  the scenario it covers and the command that shows it failing; green tasks name the red tasks they
  turn green.
- Test conventions, for every red task below: xUnit v3; data variations are Theories; every
  simulation test runs at a fixed step rate and seed and advances exactly the steps its scenario
  implies (the contact event's step, the landing window, each window restart); rate-dependent tests
  run at 20, 30 and 60 Hz through `TestRates.All`; child processes, temporary directories and ports
  are unique per test (`TempDirectories`, dynamic ports), so every test is parallel- and rerun-safe;
  committed files under `simcore/` are opened read-only through `RepoPaths`. No test is skipped or
  disabled; a test that needs a native library or a browser lives where that toolchain runs (the
  native test project, `tools/agreement/`), never behind a skip.
- Real engines' verdicts are evidence in the selection report, never a test assertion (spec,
  "A fixture engine for the checks"). A test on Bepu or Jolt asserts that a verdict, its measured
  value and its bound are recorded, never which verdict it is. The exceptions are the requirements
  that bind our own adapter code for every candidate: zero allocation (FR-025), the vector guard
  (FR-001), the analyzer and allowlist (FR-004), and the winner's checks once a record names it
  (US1 scenario 14).

## Path Conventions

- Core (analyzed, BCL and `System.IO.Hashing` only): `src/MmoEngine.SimCore/` with the new
  `Vehicles/`, `Vehicles/Config/`, `Vehicles/Engine/` folders and additions in `Collision/`,
  `Formats/`, `World/`, `Batch/`.
- Adapters (analyzed, one declared exemption each): `src/MmoEngine.SimCore.Vehicles.Fixture/`,
  `src/MmoEngine.SimCore.Vehicles.Bepu/`, `src/MmoEngine.SimCore.Vehicles.Rapier/` (slice 1),
  `src/MmoEngine.SimCore.Vehicles.Jolt/` with `native/` (slice 2).
- Server-only tools: `src/MmoEngine.SimCore.Vehicles.Server/` (catalog, content, scenarios, checks,
  divergence), `src/MmoEngine.SimCore.Vehicles.Selection/` (config, license, fit check, locks,
  orchestration, report, decision), `src/MmoEngine.SimCore.Cli/` (`simcore`).
- Browser hosts (slice 2, not analyzed, as 015's host): `src/MmoEngine.SimCore.Vehicles.Fixture.Wasm/`,
  `src/MmoEngine.SimCore.Vehicles.Bepu.Wasm/`, `src/MmoEngine.SimCore.Vehicles.Jolt.Wasm/`.
- Tests: `tests/MmoEngine.SimCore.Tests/` (core: `Vehicles/`, `Formats/`, `World/`, `Batch/`, `Cli/`,
  `Allocation/`), `tests/MmoEngine.SimCore.Vehicles.Tests/` (`Fixture/`, `Checks/`, `Scenarios/`,
  `Bepu/`, `Allocation/`, `Locks/`, `Selection/`, `License/`, `FitCheck/`, `Decision/`, `Divergence/`,
  `Engines/`, `Cli/`), `tests/MmoEngine.SimCore.Analyzers.Tests/`; slice 2 adds
  `tests/MmoEngine.SimCore.Vehicles.Wasm.Tests/`, `tests/MmoEngine.SimCore.Vehicles.Native.Tests/`
  (outside `MmoEngine.sln`), `src/MmoEngine.SimCore.Vehicles.Jolt/native/tests/` (`ctest`),
  `tools/agreement/node/*.test.mjs`, `tools/agreement/specs/*.spec.mjs` and
  `tests/workflows/test_simcore_workflow.py`.
- Benchmarks (slice 2): `benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks/`.
- Committed content: `simcore/vehicles/` (config, selection configuration, content, engine pin files,
  streams, license tables, fixtures, selection), `simcore/baseline/015-65112ab.sha256`,
  `simcore/golden/stream/v2-*`. Only the `simcore` writing commands change them.

---

# Slice 1: formats, the vehicle model, the fixture engine, Bepu on the server and the selection machinery

Branch `016-vehicle-physics-selection`. No guarded path is touched; the existing `.NET` workflow runs
every test in this slice.

## Phase 1: Setup (Shared Infrastructure, slice 1)

**Goal**: Add the slice-1 projects, package pins and committed folders, so the solution builds and
formats clean with them.

**Independent Test**: `dotnet build MmoEngine.sln -warnaserror` and
`dotnet format MmoEngine.sln --verify-no-changes` exit 0 with the six new projects in
`dotnet sln MmoEngine.sln list`.

### Tests (write first, confirm red)

This phase owns no acceptance scenario. Its checks are the build and format commands above; the
wiring is tested in Phase 2 (`WiringTests`, `ExemptionTests`, `CoreDependencyTests`).

- [ ] T001 Confirm the phase's checks are red: `dotnet build tests/MmoEngine.SimCore.Vehicles.Tests/MmoEngine.SimCore.Vehicles.Tests.csproj` exits non-zero because the project does not exist, and `dotnet sln MmoEngine.sln list` names none of `src/MmoEngine.SimCore.Vehicles.Fixture`, `src/MmoEngine.SimCore.Vehicles.Bepu`, `src/MmoEngine.SimCore.Vehicles.Rapier`, `src/MmoEngine.SimCore.Vehicles.Server`, `src/MmoEngine.SimCore.Vehicles.Selection` or `tests/MmoEngine.SimCore.Vehicles.Tests`.

### Implementation

- [ ] T002 [P] Add to `Directory.Packages.props`, after the Spec 015 block, under the comment `<!-- Spec 016 (specs/016-vehicle-physics-selection/research.md R1, R16): Bepu (Apache-2.0), referenced only by the Bepu adapter; Mono.Cecil (MIT), referenced only by MmoEngine.SimCore.Vehicles.Selection. -->`, one element per line with `Include` before `Version`: `BepuPhysics` 2.5.0-beta.33, `BepuUtilities` 2.5.0-beta.33, `Mono.Cecil` 0.11.6. Change no existing version.
- [ ] T003 [P] Create `src/MmoEngine.SimCore.Vehicles.Fixture/MmoEngine.SimCore.Vehicles.Fixture.csproj` (net10.0, references `src/MmoEngine.SimCore`), attaching `src/MmoEngine.SimCore.Analyzers` with 015's wiring (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`), every rule at Error, and no `MmoDeterminismExemption` property (contracts/engine-adapter.md#analyzer).
- [ ] T004 [P] Create `src/MmoEngine.SimCore.Vehicles.Bepu/MmoEngine.SimCore.Vehicles.Bepu.csproj`: references the core, `BepuPhysics` and `BepuUtilities`; `RestorePackagesWithLockFile` true so `src/MmoEngine.SimCore.Vehicles.Bepu/packages.lock.json` exists (an identity input of `bepu.json`); the analyzer with 015's wiring, `<MmoDeterminismExemption>MMO1001:system-numerics</MmoDeterminismExemption>` and `<CompilerVisibleProperty Include="MmoDeterminismExemption" />`.
- [ ] T005 [P] Create `src/MmoEngine.SimCore.Vehicles.Rapier/MmoEngine.SimCore.Vehicles.Rapier.csproj`: references the core, `AllowUnsafeBlocks`, the analyzer with `<MmoDeterminismExemption>MMO1008:interop</MmoDeterminismExemption>` and the `CompilerVisibleProperty` item.
- [ ] T006 [P] Create `src/MmoEngine.SimCore.Vehicles.Server/MmoEngine.SimCore.Vehicles.Server.csproj` (server-only, not analyzed, as 015's `MmoEngine.SimCore.Server`): references `src/MmoEngine.SimCore.Server`, the fixture, Bepu and Rapier adapters.
- [ ] T007 [P] Create `src/MmoEngine.SimCore.Vehicles.Selection/MmoEngine.SimCore.Vehicles.Selection.csproj`: references `MmoEngine.SimCore.Vehicles.Server`, `FluentValidation` and `Mono.Cecil`.
- [ ] T008 [P] Create `tests/MmoEngine.SimCore.Vehicles.Tests/MmoEngine.SimCore.Vehicles.Tests.csproj` (xunit.v3, CsCheck, coverlet.collector; references Vehicles.Server, Vehicles.Selection and the CLI) with `tests/MmoEngine.SimCore.Vehicles.Tests/vehicles.runsettings` setting `DOTNET_PreferredVectorBitWidth=128` under `RunConfiguration/EnvironmentVariables`, and `<RunSettingsFilePath>` pointing at it. Link 015's `tests/MmoEngine.SimCore.Tests/Support/{RepoPaths,TempDirectories,TestRates,SimcoreCli}.cs` as `Compile` items so both test projects share one implementation.
- [ ] T009 Add the projects of T003-T008 to `MmoEngine.sln`, and reference `MmoEngine.SimCore.Vehicles.Selection` from `src/MmoEngine.SimCore.Cli/MmoEngine.SimCore.Cli.csproj` (depends on T003-T008). Then the phase's Independent Test commands exit 0.
- [ ] T010 [P] Create the committed folders with a `.gitkeep` only where no file lands in this slice: `simcore/vehicles/{config,content,engines,streams/fixture,streams/bepu,license,fixtures,selection}` and `simcore/baseline/`. `.gitattributes` already marks `*.mmcd` and `*.mmrs` binary; add `simcore/vehicles/fixtures/**/*.records binary` and `simcore/vehicles/fixtures/**/*.windows binary`, and confirm with `git check-attr binary simcore/vehicles/fixtures/divergence/x.records`.

**Checkpoint**: the Independent Test commands exit 0.

---

## Phase 2: Foundational (Blocking Prerequisites, slice 1)

**Purpose**: The analyzer exemption, the vehicle configuration, the engine interface, the core vehicle
model and step, canonical state version 2 and recorded stream version 2 (encode, decode, run), the
engine catalog and identity, the vehicle test world, the selection configuration and the fixture
engine with no fault. Every user story needs them.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~Vehicles|FullyQualifiedName~StateSerializerV2|FullyQualifiedName~RecordedStreamV2|FullyQualifiedName~SimWorldVehicle|FullyQualifiedName~StaticTriangles"`, `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests` and `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Engines|FullyQualifiedName~SelectionConfig|FullyQualifiedName~VehicleWorld|FullyQualifiedName~FixtureEngine|FullyQualifiedName~Architecture"` pass, and 015's whole suite still passes unchanged.

### Test support (before the red tests that use it)

- [ ] T011 [P] Add `tests/MmoEngine.SimCore.Tests/Support/FakeVehicleEngine.cs` (an `IVehicleEngine` that records every `Load`, `Begin` and `Step` call with its bodies and substep length, integrates gravity only, and returns a scripted `EngineResult` per call) and `tests/MmoEngine.SimCore.Tests/Support/VehicleWorlds.cs` (small in-process collision worlds: flat ground, a slope, a wall, built through 015's `CollisionWorld` builders; never the committed `.mmcd`).
- [ ] T012 [P] Add `tests/MmoEngine.SimCore.Vehicles.Tests/Support/VehicleRuns.cs` (open a committed vehicle stream read-only, run it through `StreamRunner` with an engine factory, return the records and decoded states) and `tests/MmoEngine.SimCore.Vehicles.Tests/Support/SyntheticTrajectories.cs` (builds sequences of canonical version 2 states from given positions, velocities, orientations and wheel contacts, for engine-free oracle tests).

### Tests (write first, confirm red)

- [ ] T013 [P] Write failing Theory `ExemptionTests.DeclaredExemptionAdmitsOnlyItsRuleAndMembers` in `tests/MmoEngine.SimCore.Analyzers.Tests/ExemptionTests.cs` (FR-004). Each row compiles a sample with `build_property.MmoDeterminismExemption` set through an `AnalyzerConfigOptionsProvider` and asserts the exact diagnostics: with `MMO1001:system-numerics`, `System.Numerics.Vector3`, `Quaternion` and `Vector<float>` give no MMO1001, while `lock`, `Monitor.Enter` (MMO1006), `DateTime.UtcNow` (MMO1005) and `double` arithmetic (MMO1007) still give their rule at Error; with `MMO1008:interop`, a `[LibraryImport]` declaration, `NativeLibrary.Load`, `Marshal.SizeOf`, `OperatingSystem.IsBrowser()` and a `[JSImport]` declaration give no MMO1008, while every other MMO1008 member of 015's table still reports; with no property, all of them report. Add `ExemptionTests.MMO1006IsNeverExempt` (a property value naming `MMO1006` exempts nothing). Red: `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~ExemptionTests"` fails, because the analyzer reads no such property.
- [ ] T014 [P] Replace 015's `WiringTests.OnlyTheCoreAttachesTheAnalyzer` in `tests/MmoEngine.SimCore.Analyzers.Tests/WiringTests.cs` with `WiringTests.AnalyzerIsAttachedToTheCoreTheFixtureAndEachAdapter` (the set is exactly the core, `Vehicles.Fixture`, `Vehicles.Bepu`, `Vehicles.Rapier`, and in slice 2 `Vehicles.Jolt`; the hosts, `Vehicles.Server` and `Vehicles.Selection` are not analyzed) and add `WiringTests.OnlyTheAdaptersDeclareAnExemptionWithTheirValues` (every `*.csproj` read; `MmoDeterminismExemption` appears only as `MMO1001:system-numerics` in Bepu's and `MMO1008:interop` in Rapier's and Jolt's; none in the core or the fixture). Red: the same filter fails until T003-T005 and T030 land.
- [ ] T015 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Vehicles/Config/VehicleConfigRuleTableTests.cs` (FR-005, contracts/vehicle-config.md). `EveryFieldAcceptsItsBoundsAndRefusesOneStepOutside` is a Theory over the 38 rows, "Min and max are inclusive", asserting no error at min and max and exactly `vehicles.<path>: must be between {min} and {max}` one float step (or one integer) outside; the rows are, verbatim: `maxVehicles` Int 1-256, `gravity` 0.1-50, `massKg` 1-100000, `chassisHalfExtents.x` 0.1-10, `.y` 0.1-10, `.z` 0.1-20, `chassisOffsetY` −5-5, `chassisFriction` 0-2, `wheelRadius` 0.05-3, `suspensionRestLength` 0.01-3, `suspensionTravel` 0.01-3, `suspensionStiffness` 1-10000000, `suspensionDamping` 0-1000000, `tireGrip` 0.05-5, `tireSolverIterations` Int 1-16, `driveForce` 0-10000000, `topSpeed` 0.5-150, `driveCutbackFraction` 0.001-1, `reverseDriveForce` 0-10000000, `reverseTopSpeed` 0.1-150, `reverseEngageSpeed` 0-5, `brakeDeceleration` 0.1-100, `maxSteerAngleDeg` 0.1-80, `steerRateDegPerSecond` 1-3600, `substeps` Int 1-8, `contactTolerance` 0.001-0.5, and `wheels[i].attach.{x,y,z}` −20-20. `NonFiniteValuesAreRefused` (NaN, ±infinity → `must be finite`); `EveryRelationRefusesAtItsBoundary` over VR01-VR07 with each message verbatim (for VR07: `with stepRateHz {r} and substeps {K} the suspension's ω·dt is {x}, which must be below 2`); `ErrorsComeInRuleTableThenRelationOrder`; `PlaceholderSectionPasses`; `BlockRoundTripsInAscendingFieldIdOrder` (the block is "2 + 6 · 38 = 230 bytes", field IDs `0x8001`-`0x801A` and `0x9i01`-`0x9i03`); `ConfigWithoutAVehiclesSectionIsValidAndMeansNoVehicles`. In `tests/MmoEngine.SimCore.Tests/Config/SimConfigJsonVehicleTests.cs`: `EveryOmittedLeafIsRequired` (`<path>: is required`), `WheelsMustHaveExactlyFourEntries` (`must have exactly 4 entries`), `ValidatorReports015ErrorsThenTheVehicleGuardsErrors`. Red: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~VehicleConfig|FullyQualifiedName~SimConfigJsonVehicle"` does not compile.
- [ ] T016 [P] Write failing Theory `CompiledVehicleConfigTests.DerivedValuesFollowTheDataModel` in `tests/MmoEngine.SimCore.Tests/Vehicles/Config/CompiledVehicleConfigTests.cs` at 20, 30 and 60 Hz and substeps 1, 2, 8: `SubstepLength` = `1f / stepRateHz / substeps` bit for bit; the diagonal inertia of the box plus "the parallel-axis term m·d² on the X and Z axes" for `chassisOffsetY`; `InverseInertia` the reciprocals; `WheelRayLength` = `suspensionRestLength + wheelRadius`; `Wheelbase` 2.8 and `Track` 1.7 with the placeholders; `SpawnCompression` = m·g / (4k) = 0.109 m (to f32 rounding) with the placeholders. Red: does not compile.
- [ ] T017 [P] Write failing Theory `ChassisBoxTests.SeparationAndPenetrationMatchTheClosedForm` in `tests/MmoEngine.SimCore.Tests/Vehicles/ChassisBoxTests.cs` (FR-011's oriented-box oracle): axis-aligned boxes apart by 1.0 m (separation 1.0), touching (0), overlapping by 0.2 m (penetration 0.2), one rotated 45° about Y with the closed-form gap, edge-to-edge and corner-to-face cases, and the offset `chassisOffsetY` applied; separation and penetration are computed from canonical positions, orientations and the configured half-extents only. Red: does not compile.
- [ ] T018 [P] Write failing Theory `VehicleCommandsTests.ButtonsMapAsTheDataModelTableSays` in `tests/MmoEngine.SimCore.Tests/Vehicles/VehicleCommandsTests.cs` (FR-008): `Forward` → drive force, cut back over the last `driveCutbackFraction` of `topSpeed`; `Back` above `reverseEngageSpeed` → brake, at or below it → reverse drive up to `reverseTopSpeed`; `Forward` and `Back` → brake; `Left`/`Right` → steering target −/+`MaxSteerAngle`, both or neither → 0, approached by `SteerRatePerSubstep` per substep; every other button bit (each of 015's other `Buttons`) changes nothing on a vehicle. Red: does not compile.
- [ ] T019 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Vehicles/WheelModelTests.cs` on `VehicleWorlds` flat ground and slope: `RaysUseTheCoreSegmentQueryFromEachAttachPoint`; `CompressionIsRestMinusLengthClampedToTravel` (data model: `Compression` "`rest − length` in [0, `suspensionTravel`], where length = (ray hit distance − radius) clamped to [rest − travel, rest], and 0 when the ray misses"); `ContactIsOneOnlyWhenTheRayHit` ("1 when the wheel's ray hit, else 0"); `SpinIsForwardContactSpeedOverRadiusAndHeldWhileAirborne` ("never read by a force"); `RearSteeringAnglesAreZero` ("0 on the rear wheels"); `SteeringStaysWithinTheMaximum` ("within ±`MaxSteerAngle`"); `AtSpawnCompressionTheSuspensionHoldsTheWeight` (net vertical impulse over one substep within 1% of m·g·dt); `TirePassesRunTireSolverIterationsTimes`. Red: does not compile.
- [ ] T020 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Vehicles/VehicleContactsTests.cs` (FR-012): `AContactBeginsOnlyWhenTheSeparationCrossesTheTolerance` (≤ `contactTolerance` at the end of the step and > it at the end of the step before; a pair already in contact begins nothing; the first step after a load uses the loaded poses); `ContactsAreListedByAscendingPair` (four vehicles, IDs 2, 5, 9, 11, all touching: pairs (2,5), (2,9), … in order); `MoreThanEightTimesMaxVehiclesBegunContactsFaultTheStep` ("8 × `vehicles.maxVehicles`; more begun contacts in one step fault the step with `QueryCapacityExceeded` at `events.vehicleContacts.count`"). Red: does not compile.
- [ ] T021 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Collision/StaticTrianglesTests.cs` (FR-006): `CopyStaticTrianglesGivesEveryKeptTriangleInTriangleIdOrder`, `HeightfieldTrianglesAreWoundToPointUp` ((B − A) × (C − A) has y > 0, kind `HeightfieldUp`), `MeshTrianglesAreAsStoredAndDoubleSided` (kind `MeshDoubleSided`), `DegenerateTrianglesAreDropped` (a zero-area mesh triangle is absent), and `ASpanOfTheWrongLengthIsRefused`. Red: does not compile.
- [ ] T022 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/StateSerializerV2Tests.cs` (FR-012, contracts/vehicle-formats.md): `VehicleRecordIs112BytesAtTheContractOffsets` (id 0, position 4, orientation 16, linearVelocity 32, angularVelocity 44, wheels 56 as 4 × 13 bytes, lastSequence 108); `StateLengthIsPPlusSixPlusTheRecords` (`stateLength(v2) = P + 6 + 112·n + 8·c + 5·r`); `TheVersion1PartIsByteForByteVersion1` (the prefix equals `StateSerializer` version 1's bytes for the same characters, weapons and projectiles, `characterCount` 0 included); `V1PartHashIsXxHash64OfThePrefix` and `FullHashCoversTheWholeState`; `LayoutGainsTheVehicleRowsAfter015s74` (wheel element index = vehicle index × 4 + wheel); `MaxStateLengthMatchesTheContract` (103,262 bytes at V = 64 and 138,014 at V = 256 with 015's placeholders; `MaxStepRecordLength` + 24); `ClearEventsZeroesEveryEventCountAndDropsTheRecords`. Red: does not compile.
- [ ] T023 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/RecordedStreamV2Tests.cs` (FR-014): `EncodeDecodeRoundTripsAStreamWithCharactersAndVehicles` (header 116 bytes; `fileLength = 116 + configLength + vehicleConfigLength + initialStateLength + stepsLength + 8`; each step entry `23 + 18·C + 19·V` bytes; both expected hashes per step); `Version1StreamsDecodeAsBefore` (all 19 committed 015 streams decode with `Version` 1, `VehicleRulesRevision` 0 and a null `VehicleConfig`); `UnsupportedVersionNamesTheVersionsSupported` (`unsupported version 3; this reader supports versions 1 and 2`); `TheSelectionConfigurationIsNeverInAStream` (no field of `selection.json` in the encoded bytes). In `tests/MmoEngine.SimCore.Tests/Batch/StepRecordV2Tests.cs`: `Version2RecordIs24PlusStateLengthWithV1PartHash`. Red: does not compile.
- [ ] T024 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/World/SimWorldVehicleTests.cs` with `FakeVehicleEngine` (data-model.md, World step order): `VehicleStagesRunAfter015sStagesInOrder` (V1 validate in ascending vehicle ID, V2 wheel model and V3 one `Step` per substep for K = 1, 2 and 4, V4 bounds, V5 contacts; `Step` receives the bodies in ascending ID); `EngineFaultStopsTheStepWithItsCodeAndCommitsNothing` (`StepFault { EngineFault }` with the engine's detail; the next `Step` starts from the unchanged state); `NonFiniteBodyReadBackIsRefusedNamingTheField` (the engine returns NaN in `angularVelocity.y`: `StepFault { NonFinite, vehicles[0].angularVelocity.y }`); `VehicleOutsideThePlayableAreaFaultsNamingTheVehicle` (`VehicleOutOfBounds` naming `vehicles[i].position.x`, `.z` or `.y`); `AWorldWithoutAVehicleConfigurationStepsExactlyAs015` (015's `flat-run-30hz` gives its committed hashes); `MeasuredFrameTimeIsNotAccepted` (the step API has no frame-time parameter: a reflection check over `SimWorld`'s public `Step` overloads, FR-009). Red: does not compile.
- [ ] T025 [P] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Engines/EngineIdentityFingerprintTests.cs` (FR-002, contracts/engine-adapter.md#engine-identity): `FingerprintIsXxHash64OfCanonicalPinJsonThenEachInputPathAndSha256` (a temporary pin file and inputs, the expected value computed in the test from the stated byte sequence); `ChangingThePinFileOrAnyInputChangesTheFingerprint` Theory; `WhitespaceInThePinFileDoesNotChangeIt`; `EveryCommittedPinFileHasTheRequiredMembers` (`fixture.json`, `bepu.json`, `rapier.json`: `formatVersion` 1, `engine`, `release` "ASCII, ≤ 32 bytes", `adapterRevision`, `sources`, `buildSettings`, `engineSettings`, `historyFreeMode`, `identityInputs`; Bepu's names `vectorPin: "DOTNET_PreferredVectorBitWidth=128"`). In `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/VehiclesIdentityCommandTests.cs`: `IdentityPrintsEngineReleaseAndFingerprint` (`fixture fixture-1 <16 hex>`, exit 0) and `UnknownEngineIsAUsageError` (exit 2). Red: does not compile.
- [ ] T026 [P] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionConfigValidatorTests.cs` (FR-036, contracts/selection-config.md): Theory `EachRuleAcceptsItsBoundsAndRefusesOnePastThem` over the rules verbatim: `formatVersion` 1; `acceleration.durationSeconds` 1 to 120; `acceleration.minFractionOfTopSpeed` greater than 0, at most 1; `acceleration.maxOvershootFraction` 0 to 1; `acceleration.maxTiltDeg` greater than 0, at most 90; `acceleration.wheelContactAfterSeconds` 0 to `acceleration.durationSeconds`; `braking.restSpeed` greater than 0, at most 5; `braking.maxDistanceFraction` at least 1, at most 10; `turning.targetSpeed` greater than 0, at most 50; `turning.speedBand` greater than 0, below `turning.targetSpeed`; `turning.maxSeconds` 1 to 120; `turning.maxRadiusError` greater than 0, at most 1; `landing.settleSeconds` greater than 0, at most 30; `collision.withinSteps` an integer, 1 to 60; `collision.maxClosingSpeedFraction` 0 to 1; `divergence.windowSeconds` greater than 0, at most 10; `divergence.maxPositionErrorMetres` greater than 0, at most 10; `divergence.maxOrientationErrorDeg` greater than 0, at most 180; `decision.tieBand` 0 to 1. Each refusal is one `<path>: <message>` line in the table's order. `MissingMemberIsRefused`, `UnknownMemberIsNotAKnownField` (`is not a known field`), `CommittedFilePasses` (`simcore/vehicles/selection.json` equals the contract's placeholder values), `TieBandIsReadAsAnExactDecimal` (0.1m), and `FingerprintIsStableUnderWhitespaceAndMemberOrder` (`SelectionConfigFingerprint`, 16 lowercase hex digits). Red: does not compile.
- [ ] T027 [P] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Scenarios/VehicleWorldGeneratorTests.cs` (FR-007, data-model.md, Vehicle test world): `GeneratedBytesEqualTheCommittedWorld` (seed `0x5645484943313600`, `MMCD` version 1, byte-identical to `simcore/vehicles/content/vehicle-world.mmcd`); Theory `EachZoneHasItsTerrainAndMeshes` (flat lane x ∈ [40, 440], z ∈ [16, 40] at y = 0; the thin wall "0.1 m thick, 3 m tall, 20 m wide, across the lane at x = 430"; the turning pad flat; rolling terrain amplitude 0.5-1.5 m; the ramp "10 m long, 6 m wide, rising 1.6 m to a sharp lip at x = 430"; the arena flat; the enclosure "3 m walls, 0.5 m thick"); `FlatZonesHaveNoMeshButTheNamedOne`; `ItIsSeparateFromTheTestWorld` (its checksum differs from `simcore/content/test-world.mmcd`, which is unchanged). In `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/VehiclesContentCommandTests.cs`: `ContentGeneratePrintsBytesAndChecksumAndReproducesTheFile` into a temp path (one line `<path>: <bytes> bytes, checksum <hash>`, exit 0). Red: does not compile.
- [ ] T028 [P] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Fixture/FixtureEngineTests.cs` (FR-035, research R22): `AVehicleAtSpawnCompressionOnFlatGroundStaysAtRest` (100 steps at each rate: speed < 0.1 m/s, all four contacts 1); `TwoInstancesGiveIdenticalBodies` (stateless: the same inputs on two fresh instances give the same bits for 600 steps); `InternalSubstepsAreBoundedAt64`; `BoxAgainstBoxResolvesWithAnInelasticImpulse` (two bodies closing at 8 m/s: closing speed ≤ 10% within 3 steps); `IdentityIsFixtureFixture1AndItsFingerprint`; `StepsOnTheCallingThread` (the thread ID inside `Step` equals the caller's). Red: does not compile.
- [ ] T029 [P] Write failing tests (FR-003, FR-033): extend `tests/MmoEngine.SimCore.Tests/Architecture/CoreDependencyTests.cs` with `CoreStillReferencesOnlyTheBclAndSystemIOHashing` covering the `Vehicles` namespaces; add `tests/MmoEngine.SimCore.Vehicles.Tests/Architecture/EngineNeutralityTests.cs` `NoSharedFileHasAnEngineSpecificBranch` (no file under `src/MmoEngine.SimCore/`, `src/MmoEngine.SimCore.Vehicles.Server/Checks/`, `src/MmoEngine.SimCore.Vehicles.Server/VehicleScenarioCatalog.cs`, `src/MmoEngine.SimCore.Cli/Agreement/` or `tools/agreement/lib/` mentions `Bepu`, `Jolt` or `Rapier` outside `EngineCatalog.cs` and `EngineCode`) and `AdaptersHoldOnlyEngineCallsAndMarshalling` (each adapter assembly's public and internal types implement `IVehicleEngine`, `IVehicleEngineFactory`, an engine callback interface or a marshalling helper, listed in the test). Red: the new tests do not compile.

### Implementation

- [ ] T030 Read `build_property.MmoDeterminismExemption` in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and add the admitted-member tables to `src/MmoEngine.SimCore.Analyzers/Tables.cs`: `MMO1001:system-numerics` admits `System.Numerics.Vector3`, `Quaternion`, `Vector<T>` and the BepuUtilities wide types; `MMO1008:interop` admits `extern` and `[LibraryImport]` declarations, `NativeLibrary`, `Marshal`, `OperatingSystem.IsBrowser()` and `[JSImport]` declarations with the generator's `JSFunctionBinding` and `JSMarshalerArgument` code. Any other value admits nothing; MMO1006 is never admitted. 015's suppression rules hold: `.editorconfig` severities and `[SuppressMessage]` still hide nothing from the backstop. Makes T013 and, with T003-T005, T014 pass.
- [ ] T031 [P] Add `public const uint VehicleRulesRevision = 1;` to `src/MmoEngine.SimCore/SimCoreVersion.cs`; `RulesRevision` stays 1.
- [ ] T032 [P] Implement `src/MmoEngine.SimCore/Vehicles/Config/{VehicleConfig,VehicleConfigFields,VehicleConfigRelations,VehicleConfigGuard,VehicleConfigBlock,CompiledVehicleConfig}.cs` from contracts/vehicle-config.md: one rule table (38 fields, sections 8 and 9, `fieldId = section << 12 | instance << 8 | field`), relations VR01-VR07 after the fields, `VehicleConfigGuard.Check(VehicleConfig, int stepRateHz)` returning every `ConfigError` in rule-table then relation order, the block (`u16 count`, then `{u16 fieldId, u32 valueBits}` "in strictly ascending field ID, every field exactly once"), and `CompiledVehicleConfig.Create` throwing `ConfigValidationException`. Makes the core half of T015 and T016 pass.
- [ ] T033 Bind the optional `vehicles` section in `src/MmoEngine.SimCore.Server/SimConfigJson.cs`, report 015's errors then the vehicle guard's in `src/MmoEngine.SimCore.Server/SimConfigValidator.cs`, and commit `simcore/vehicles/config/default.json` (015's `simcore/config/default.json` sections plus the contract's `vehicles` placeholders). `simcore/config/default.json` is unchanged. Makes the rest of T015 pass (depends on T032).
- [ ] T034 [P] Implement `src/MmoEngine.SimCore/Vehicles/Engine/{IVehicleEngineFactory,IVehicleEngine,EngineIdentity,EngineCode,VehicleBody,StaticTriangle,EngineResult}.cs` exactly as contracts/engine-adapter.md#interface declares them: `EngineCode : byte { Bepu = 1, Jolt = 2, Rapier = 3, Fixture = 255 }`; `EngineIdentity(EngineCode Engine, string Release, ulong Fingerprint)` with `Release` "ASCII, ≤ 32 bytes" checked at construction; `EngineResultCode { Ok = 0, Faulted = 1, LoadRefused = 2 }`; `StaticTriangleKind { HeightfieldUp = 0, MeshDoubleSided = 1 }`.
- [ ] T035 [P] Implement `src/MmoEngine.SimCore/Vehicles/{VehicleState,WheelState,Quat}.cs` with the data model's rules: `Id` u32 "[1, 65535], strictly ascending"; `Position` the centre of mass; `Orientation` "the engine's own bits", checked on load as "\|q\|² within [0.998, 1.002]" (contracts/vehicle-formats.md); `Wheels[4]` front left, front right, rear left, rear right; `LastSequence` "0 before the first"; `WheelState.Contact` u8 0 or 1. Fixed-size, no allocation per step.
- [ ] T036 [P] Add `src/MmoEngine.SimCore/Collision/CollisionWorld.StaticTriangles.cs` with `StaticTriangleCount` and `CopyStaticTriangles(Span<StaticTriangle>)`. Makes T021 pass.
- [ ] T037 [P] Implement `src/MmoEngine.SimCore/Vehicles/ChassisBox.cs` (oriented-box separation and penetration by the separating-axis test, 15 axes, fixed loop bounds, `SimMath` only). Makes T017 pass.
- [ ] T038 [P] Implement `src/MmoEngine.SimCore/Vehicles/VehicleCommands.cs`. Makes T018 pass.
- [ ] T039 Implement `src/MmoEngine.SimCore/Vehicles/WheelModel.cs` (rays through `CollisionWorld.SegmentQuery`, suspension spring and damper reading the compression change as ṡ, tire impulses solved by `tireSolverIterations` Gauss-Seidel passes, world-owned buffers; depends on T032, T035, T038). Makes T019 pass.
- [ ] T040 Implement `src/MmoEngine.SimCore/Vehicles/VehicleContacts.cs` (all pairs, bounding-sphere reject then `ChassisBox`, ascending (lower ID, higher ID), capacity 8 × `maxVehicles`; depends on T037). Makes T020 pass.
- [ ] T041 Implement `src/MmoEngine.SimCore/Vehicles/VehicleStep.cs` (stages V1-V5) and wire it into `src/MmoEngine.SimCore/World/SimWorld.cs` (a world created with a vehicle configuration and an `IVehicleEngine`; `Reset` calls `Begin` with the ascending IDs), add `VehicleOutOfBounds` and `EngineFault` to `src/MmoEngine.SimCore/World/StepFaultCode.cs`, and `vehicleContacts` and `vehicleInputRejections` to `src/MmoEngine.SimCore/World/StepEvents.cs` (depends on T039, T040, T034). 015's stages 1-5 stay unchanged. Makes T024 pass.
- [ ] T042 Extend `src/MmoEngine.SimCore/Formats/StateSerializer.cs` with the version 2 layout rows, canonicalisation over every `f32` in layout order, the version 1 part hash and `ClearEvents`, and `src/MmoEngine.SimCore/Config/CompiledConfig.cs` with the version 2 `MaxStateLength` and `MaxStepRecordLength` (depends on T035). Makes T022 pass.
- [ ] T043 Extend `src/MmoEngine.SimCore/Formats/RecordedStream.cs` to read and write versions 1 and 2 (`SupportedVersions = [1, 2]`, `HeaderLengthV2 = 116`, `RecordedStep` with `VehicleCommands` and `ExpectedV1PartHash`, `RecordedVehicleCommand(ushort VehicleIndex, InputCommand Command)`), running decode checks 2-13 of contracts/vehicle-formats.md#decoding in order; add `VehicleRulesRevisionMismatch = 11` and `EngineIdentityMismatch = 12` to `src/MmoEngine.SimCore/Formats/FormatErrorCode.cs`; write the version 2 record in `src/MmoEngine.SimCore/Batch/StepRecord.cs`; add `StreamRunner.Run(stream, worlds, engines, perturbStep)` and `StreamRunner.RunWindow(stream, worlds, engines, startState, startStep, count)` to `src/MmoEngine.SimCore/Batch/StreamRunner.cs`, opening a version 2 stream in the contract's order 1-8 (a stream opened without an engine factory is `EngineIdentityMismatch` with `no engine was supplied`), and the perturbation: `MathF.BitIncrement` on `vehicles[0].position.x` after `perturbStep` commits, then recanonicalise, reserialise and rehash both hashes (depends on T042, T041). Makes T023 pass.
- [ ] T044 Implement `src/MmoEngine.SimCore.Vehicles.Server/{EngineCatalog,PinFile,EngineIdentityFingerprint}.cs` (engine names `bepu`, `jolt`, `rapier`, `fixture`; `rapier` refuses with `rapier: not built; its fit check failed criterion (c), see the selection report`; `jolt` refuses in slice 1 with `jolt: not built in this slice`), commit `simcore/vehicles/engines/fixture.json` and `simcore/vehicles/engines/bepu.json` (BepuPhysics and BepuUtilities 2.5.0-beta.33 with their nupkg SHA-256s from the restore, `packages.lock.json` as an identity input, the engine settings of contracts/engine-adapter.md#adapters, `historyFreeMode`, `vectorPin`), and add `vehicles identity` to `src/MmoEngine.SimCore.Cli/Commands/VehiclesCommand.cs` (new; registered in `src/MmoEngine.SimCore.Cli/Program.cs`). Makes T025 pass.
- [ ] T045 Implement `src/MmoEngine.SimCore.Vehicles.Server/VehicleWorldGenerator.cs` (the zones of data-model.md, research R21), add `vehicles content generate` to `VehiclesCommand.cs`, and commit `simcore/vehicles/content/vehicle-world.mmcd` written by that command (depends on T044 for `VehiclesCommand.cs`). Makes T027 pass.
- [ ] T046 [P] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Config/{SelectionConfig,SelectionConfigValidator,SelectionConfigFingerprint}.cs` (numbers read with `Utf8JsonReader.GetDecimal`) and commit `simcore/vehicles/selection.json` with the contract's placeholders. Makes T026 pass.
- [ ] T047 Implement `src/MmoEngine.SimCore.Vehicles.Fixture/{FixtureEngine,FixtureEngineFactory,FixtureModel,FaultCode}.cs` (research R22: semi-implicit Euler with gravity, normalised quaternion update, internal substeps from top speed and contact tolerance "bounded at 64", box corners against the core world by box query and a corner segment sweep, inelastic normal impulse and Coulomb friction at μ_c, box against box by `ChassisBox`; `FaultCode.None` only in this phase) (depends on T041, T036). Makes T028 pass, and with every task above, T029.

**Checkpoint**: the phase's Independent Test commands pass, and `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify` still prints `streams verify: 19 streams, 19 match, 0 diverged, 0 not comparable, 0 refused`.

---

## Phase 3: User Story 1 - Every candidate drives the same vehicle scenarios (Priority: P1), slice 1: fixture engine and Bepu's server build 🎯 MVP

**Goal**: The 14 named scenarios, stream recording and verification per engine, the engine-free
oracles of US1 scenarios 1 to 11, the fixture engine's fault table, the managed lock check, Bepu's
adapter with its vector guard and allowlist, and the selection's server-side phases and Failed rows,
so the checks are proven on the fixture engine and recorded for Bepu.

**Independent Test**: `FixtureChecksTests.EveryCheckPassesWithNoFault` passes every check of
scenarios 1 to 12 on the fixture engine; `FaultTableTests.EachFaultFailsExactlyItsTargetCheck` fails
exactly the targeted check per fault with its reason code, scenario and measured value;
`StreamsVerifyEngineTests.EveryCommittedStreamMatchesInTwoFreshProcesses` passes for `fixture` and
`bepu`; `BepuServerChecksTests.EveryCheckRecordsOneVerdictWithItsMeasuredValueAndBound` holds one
verdict per check for Bepu.

### Tests for User Story 1 (write first, confirm red)

- [ ] T048 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Scenarios/VehicleScenarioCatalogTests.cs` (FR-010, data-model.md, Vehicle scenarios): `CatalogDefinesExactlyTheFourteenNamedStreams` (`flat-accel-20hz`, `flat-accel-30hz`, `flat-accel-60hz`, `brake-to-stop`, `turning-circle`, `rolling-terrain`, `ramp-jump`, `collide-head-on`, `collide-side`, `collide-rear`, `pile-up`, `thin-wall`, `rejected-commands`, `characters-and-vehicles`); Theory `EachStreamHasItsRateVehiclesAndCommands` (each row of the table: the rate, the spawn poses at `SpawnCompression` upright, the speeds, `pile-up` with four vehicles); `TurningCircleGovernorPressesForwardExactlyOnStepsThatStartBelowFiveMetresPerSecond` (US1 scenario 3's recording governor, on the fixture engine); `BrakeToStopAndRampJumpSwitchPhaseAtTheirRecordedSpeeds`; `RecordingIsDeterministic` (recording twice gives identical bytes). Red: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~VehicleScenarioCatalogTests"` does not compile.
- [ ] T049 [P] [US1] Write failing Theory `AccelerationCheckTests.PassesOnlyWhenEveryClauseHolds` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/AccelerationCheckTests.cs` (US1 scenario 1) on `SyntheticTrajectories` at 30 Hz for 10 s (300 steps): reaching exactly 95% of top speed passes and 94.99% fails (`BehaviorCheckFailed`, scenario US1.1, measured fraction, bound 0.95); 2.00% over top speed passes and 2.01% fails; the up axis 10.0° from world up passes and 10.01° fails; one wheel out of contact at step 31 (after the first second) fails and at step 29 passes. Each failure asserts the exact reason code, scenario, measured value and bound. Red: does not compile.
- [ ] T050 [P] [US1] Write failing Theory `BrakingCheckTests.PassesOnlyWhenItStopsWithinTheIdealDistanceBand` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/BrakingCheckTests.cs` (US1 scenario 2): v taken at the start of step s, b = `brakeDeceleration`; a path length of exactly 120% of v² / (2b) to the first step below 0.1 m/s passes, 120.01% fails; never dropping below 0.1 m/s fails; tilting past 10° fails; the distance is the path length of the chassis position from the start of step s. Red: does not compile.
- [ ] T051 [P] [US1] Write failing Theory `TurningCheckTests.PassesOnlyWhenAllFourClausesHold` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/TurningCheckTests.cs` (US1 scenario 3): a heading that reverses once fails; 360° completed at 14.9 s passes and not by 15 s fails; speed leaving 5 ± 0.5 m/s fails; a least-squares radius within 25% of `Wheelbase / tan(MaxSteerAngle)` (4.0 m with the placeholders) passes, 25.01% fails; `LeastSquaresCircleRecoversAKnownRadius` (points on a 4 m circle with known noise). Red: does not compile.
- [ ] T052 [P] [US1] Write failing Theory `TerrainPenetrationCheckTests.PenetrationIsMeasuredAgainstTheCoreWorld` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/TerrainPenetrationCheckTests.cs` (US1 scenario 4) over `VehicleWorlds` slopes and the vehicle world's rolling zone: a chassis or wheel penetration of exactly `contactTolerance` passes and 0.001 m more fails, naming the step; ending tilted past 10° fails; the oracle reads only canonical states and `CollisionWorld` (an engine factory that throws if called is passed). Red: does not compile.
- [ ] T053 [P] [US1] Write failing Theory `RampJumpCheckTests.LeavesTheLipAirborneLandsBeyondItAndSettlesWithinTwoSeconds` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/RampJumpCheckTests.cs` (US1 scenario 5, research R20): an airborne run starting within 3 steps of the lip crossing passes; no step with all four wheels out of contact fails (`no airborne phase`); landing short of the lip fails; landing penetration above the tolerance fails; settled (four contacts, upright) at ⌈2 s × rate⌉ steps after landing passes and one step later fails. Rates 20, 30 and 60 Hz. Red: does not compile.
- [ ] T054 [P] [US1] Write failing Theory `CollisionCheckTests.ContactEventPenetrationAndClosingSpeedEachDecide` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/CollisionCheckTests.cs` (US1 scenario 6) for head-on, side and rear trajectories: the state's contact event at exactly the first step whose oriented-box separation is ≤ the tolerance passes, one step early or late fails; penetration by the same computation at most the tolerance at every step passes, above it fails; closing speed within 3 steps at most 10% of the step before the event passes, 10.01% fails. The check is asserted only after the step that carries the event has committed. Red: does not compile.
- [ ] T055 [P] [US1] Write failing Theory `ThinWallCheckTests.ChassisNeverEndsAStepPastTheFarFace` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/ThinWallCheckTests.cs` (US1 scenario 7): a trajectory whose chassis corner ends one step 0.001 m past the far-face plane inside the wall's extent fails, naming the step; corners past the plane outside the extent pass; stopping before the wall passes. Red: does not compile.
- [ ] T056 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/StreamsVerifyEngineTests.cs` (US1 scenario 8): Theory `EveryCommittedStreamMatchesInTwoFreshProcesses` over `fixture` and `bepu` (two child `simcore streams verify --engine <e>` processes, each with its own working directory; Bepu's with `DOTNET_PreferredVectorBitWidth=128`): both exit 0, both print one `match` line per committed stream of `simcore/vehicles/streams/<e>/`, and the per-step hashes each wrote equal each other and the streams' committed expected hashes (full and version 1 part). `AStaleExpectedHashIsServerNotReproducible` (the `stale-expected-hash` input fault, a copy with one expected hash changed: exit 1 naming the stream and step; through `VehicleChecks`, `ServerNotReproducible` with the stream and step). Red: does not compile.
- [ ] T057 [P] [US1] Write failing Theory `RepeatRunCheckTests.AStreamSteppedTwiceGivesIdenticalHashes` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/RepeatRunCheckTests.cs` (US1 scenario 9) over `flat-accel-20hz`, `flat-accel-30hz` and `flat-accel-60hz` on the fixture: one engine instance loaded twice gives identical per-step hashes; with `state-survives-reload` the second run differs and the check fails with `ServerNotReproducible`, naming the stream and the first step of the second run that differs. Red: does not compile.
- [ ] T058 [P] [US1] Write failing Theory `ServerResetCheckTests.AWorldReloadedAtEveryWindowStartStepsOnIdentically` in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/ServerResetCheckTests.cs` (US1 scenario 10, FR-013): for every j that starts a window (S₀, S₀ + W, …, W = ⌈0.5 s × rate⌉) a second world loaded with the first world's committed state at j (events cleared by `ClearEvents`) gives hashes equal to the first world's from step j + 1 to the stream's end; with `hidden-accumulator` it fails with `ResetNotEquivalent` naming the stream, the restart step and the first differing step. Red: does not compile.
- [ ] T059 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/RejectionCheckTests.cs` (US1 scenario 11, FR-008): Theory `EachInvalidCommandIsRejectedWithItsReasonAndStepsTheNeutralInput` over unknown button bits, a pitch out of range, a weapon slot outside `input.weaponSlots` and a sequence that does not increase, on `rejected-commands`: the step's `events.vehicleInputRejections` holds the vehicle ID and 015's `InputRejection` code (1 to 4) for that command, `LastSequence` is unchanged, and the vehicle's state equals a world stepped with the neutral input at that step; `RejectedCommandApplied`: with `rejected-command-applied` the check fails with `RejectionNotNeutral` naming the stream and step. Red: does not compile.
- [ ] T060 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Locks/ManagedLockCheckTests.cs` (US1 scenario 12, FR-034, research R16): `InstrumentationCountsOneLockPerStepOnAFixtureAssembly` (a test assembly compiled in memory with Roslyn, whose step takes one `lock` per step, instrumented by `BepuLockSites`: exactly 1,000 counted entries over 1,000 steps after 300 warm-up steps, and the check is Failed `TakesLock` naming `System.Threading.Monitor.Enter` and the method); `TheFixtureEngineTakesNoLockOver1000Steps` (`simcore vehicles lockcount --engine fixture` in a child process: exit 0, `0 lock calls`); `StaticSiteListIsReportedAsEvidence` (Bepu's assemblies: the site list names `ConstraintRemover.RemoveConstraintsFromTypeBatch`'s `lock` and `IslandAwakener`'s `SpinLock.Enter`, as research R16 found); `LockcountRecordsAVerdictForBepu` (`vehicles lockcount --engine bepu` with the pin: exit 0 with 0 executed sites or exit 1 naming each executed member; the test asserts the output matches the exit code and lists the steps measured, never which). Red: does not compile.
- [ ] T061 [P] [US1] Write failing Theory `FixtureChecksTests.EveryCheckPassesWithNoFault` in `tests/MmoEngine.SimCore.Vehicles.Tests/Fixture/FixtureChecksTests.cs` (US1 Independent Test, SC-009) over US1 scenarios 1 to 12 on the fixture's committed streams through `VehicleChecks.Run`: each verdict is `Pass`, and each measured value lies inside its bound (for US1.1, fraction ≥ 0.95, overshoot ≤ 0.02, tilt ≤ 10°). Red: does not compile.
- [ ] T062 [P] [US1] Write failing Theory `FaultTableTests.EachFaultFailsExactlyItsTargetCheck` in `tests/MmoEngine.SimCore.Vehicles.Tests/Fixture/FaultTableTests.cs` (US1 scenario 13, FR-035) over every row of `simcore/vehicles/fixtures/fault-table.json` that targets US1.1-US1.11, `StepFaulted` or `CoverageNotMet`: with that fault, exactly the targeted check fails with the row's reason code and detail (`overshoot-top-speed`: `BehaviorCheckFailed`, US1 scenario 1, measured overshoot 3.0% to one decimal place; `non-finite-velocity`: `StepFaulted`, `NonFinite` at `vehicles[0].linearVelocity.y`), and every other check passes. `FaultTableTests.TheTableHasOneFaultPerCheckAndStatusRow` (one row per check of US1 scenarios 1 to 11 and per FR-037 row a vehicle model can cause; model faults change the fixture's identity unless `identityPreserving`). Red: does not compile.
- [ ] T063 [P] [US1] Write failing Theory `SelectionRunServerPhaseTests.AFixtureFaultIsFailedInEveryModeAndOthersAreStillEvaluated` in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionRunServerPhaseTests.cs` (US1 scenario 13) over the model faults: `SelectionRun` with the faulted fixture and the unfaulted fixture as two candidates, through the server-side phases and `StatusTable`: the faulted one is Failed in `interp-prebuilt`, `interp-relinked` and `aot` with the fault's reason (for the overshoot, `BehaviorCheckFailed`, `{scenario: "US1.1", measured: 0.030, bound: 0.02}`); every other check of it passes; the other candidate has its checks run and recorded; the run exits 0. Red: does not compile.
- [ ] T064 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/VectorWidthGuardTests.cs` (FR-001, research R8): `TheTestProcessRunsWithTheVectorPin` (`Vector<float>.Count` is 4 and `Vector256.IsHardwareAccelerated` false under `vehicles.runsettings`); `ABepuCommandWithoutThePinExitsTwoNamingTheVariable` (a child `simcore streams verify --engine bepu` with the variable removed: exit 2 and stderr contains `Bepu needs DOTNET_PreferredVectorBitWidth=128`). Red: does not compile.
- [ ] T065 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/BepuAllowlistTests.cs` (research R7): `TheAdapterReferencesOnlyAllowlistedBepuMembers` (member references read with `System.Reflection.Metadata` from the built adapter, compared with `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/bepu-allowlist.txt`); `BannedMembersAreNotOnTheList` (`CenterDistanceConstraint`, `CenterDistanceLimit`, `AreaConstraint`, `VolumeConstraint`, `Matrix3x3.CreateFromAxisAngle`, `Matrix.CreateFromAxisAngle`, `QuaternionEx.CreateFromAxisAngle`, `CreateFromYawPitchRoll`, `Slerp`, `GetAngleFromQuaternion`, the `SwingLimit.MaximumSwingAngle` setter). Red: does not compile.
- [ ] T066 [P] [US1] Write failing Theory `AdapterBackstopTests.EachAdapterHasNoDiagnosticOutsideItsExemption` in `tests/MmoEngine.SimCore.Analyzers.Tests/AdapterBackstopTests.cs` (FR-004) with rows `MmoEngine.SimCore.Vehicles.Fixture` (no exemption) and `MmoEngine.SimCore.Vehicles.Bepu` (`MMO1001:system-numerics`): compiles the adapter's sources with the analyzer and only that exemption, suppressed diagnostics included, and requires zero. Rapier's row lands in Phase 8 and Jolt's in Phase 12. Red: does not compile.
- [ ] T067 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/BepuServerChecksTests.cs` (US1 Independent Test): `EveryCheckRecordsOneVerdictWithItsMeasuredValueAndBound` (`VehicleChecks.Run` for `bepu` over its committed streams: one entry per US1 scenario 1 to 11 with verdict `Pass` or `Fail`, its stream, measured values and bounds from `selection.json`; the verdicts themselves are not asserted); `HistoryFreeModeRebuildsEverySubstep` (the adapter calls `Simulation.Clear()`, clears `PairCache.Mapping` and re-adds the statics and bodies in ascending ID before each `Timestep`, observed through a counting callback); `BepuStaticsAreExactlyTheCoreTriangles` (the mesh's triangle count and vertices equal `CopyStaticTriangles` on the vehicle world). Red: does not compile.
- [ ] T068 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/VehiclesChecksCommandTests.cs` (contracts/vehicles-cli.md): `ChecksPrintsOneLinePerScenarioThenATotal` (`<scenario> <stream>: pass|fail <measured> (bound <bound>)`, exit 0 on the fixture with no fault); `AFailedCheckExitsOne` (fixture fault `weak-brake`); `MissingStreamsExitTwo`; `ChecksScenarioEightRunsTwoChildProcesses` (the command's scenario-8 line reports two process runs). Red: does not compile.

### Implementation for User Story 1

- [ ] T069 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Server/VehicleScenarioCatalog.cs` (the 14 rows of data-model.md, the recording governor and the speed-triggered phases, stepping the engine while recording). Makes T048 pass.
- [ ] T070 [US1] Add `--engine`, `--content` and `--config` to `streams record` and `streams verify` in `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs` and `src/MmoEngine.SimCore.Cli/Commands/StreamFiles.cs` (writes only under `simcore/vehicles/streams/<engine>/`, appends `REGENERATIONS.md` there, `--reason` required, identity and `VehicleRulesRevision` in each header; verify checks both expected hashes per step; NotComparable exits 2; without `--engine`, exactly 015's behavior), and extend `tests/MmoEngine.SimCore.Tests/Architecture/StreamWriterTests.cs` so `StreamFiles` stays the only writer of `simcore/vehicles/streams/` (depends on T069).
- [ ] T071 [P] [US1] Implement `src/MmoEngine.SimCore.Vehicles.Server/Checks/{CheckResult,AccelerationCheck,BrakingCheck,TurningCheck,TerrainPenetrationCheck,RampJumpCheck,CollisionCheck,ThinWallCheck}.cs` (research R20: `double` from the `f32` bits, canonical state, `CollisionWorld` and the selection configuration only; time windows ⌈t × rate⌉ steps). Makes T049-T055 pass.
- [ ] T072 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Server/Checks/{ReproducibilityCheck,RepeatRunCheck,ServerResetCheck,RejectionCheck,VehicleChecks}.cs` (each check on a fresh engine instance except US1.9, which reloads one instance; US1.8 by two child `streams verify` processes) and `vehicles checks` in `VehiclesCommand.cs` (depends on T071, T070). Makes T056-T059 and T068 pass.
- [ ] T073 [US1] Add the model faults of the fault table to `src/MmoEngine.SimCore.Vehicles.Fixture/{FaultCode,FixtureModel,FixtureEngine}.cs` (`overshoot-top-speed` +3%, `weak-brake`, `under-steer`, `sinks-on-slopes` only where the terrain normal is not vertical, `sticks-to-ramp`, `vehicles-pass-through`, `no-continuous-collision`, `state-survives-reload` (state kept across `Load` on one instance), `hidden-accumulator` (outside the canonical state), `rejected-command-applied`, `non-finite-velocity`, `coverage-parked`, `browser-perturb-step-15` as identity-preserving and inert on the server), read `fault` from `simcore/vehicles/engines/fixture.json` or `--fault`, and commit `simcore/vehicles/fixtures/fault-table.json` with every row of data-model.md, Fixture engine and fault table, plus the input-fault inputs `simcore/vehicles/fixtures/streams/stale-expected-hash.mmrs` and `other-identity-stream.mmrs` written by `streams record` into a temporary directory and then mutated by the task. Makes T062 pass.
- [ ] T074 [US1] Record the fixture's 14 streams with `dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --engine fixture --all --reason "Initial fixture engine streams (016)"` into `simcore/vehicles/streams/fixture/` (with `REGENERATIONS.md`). Makes T061 and the fixture row of T056 pass (depends on T073, T072).
- [ ] T075 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Bepu/{BepuEngine,BepuEngineFactory,BepuNarrowPhaseCallbacks,BepuPoseIntegratorCallbacks,NoOpRefinementScheduler}.cs` (contracts/engine-adapter.md#adapters: `SolveDescription(velocityIterations 4, substeps 4)`, `ContinuousDetection.Continuous`, contact `SpringSettings(30, 1)`, `MaximumRecoveryVelocity` 2 m/s, `Nonconserving` angular integration, no thread dispatcher, sleep threshold −1, no-op refinement schedulers; clear-rebuild every substep; one unrounded chassis box per vehicle offset by `chassisOffsetY`, mass and inertia from `CompiledVehicleConfig`, friction μ_c, restitution 0; `BufferPool` sized at `Load` for `maxVehicles`), `src/MmoEngine.SimCore.Vehicles.Server/VectorWidthGuard.cs` (`Require128()` with the contract's exact message), the `bepu` entry of `EngineCatalog`, and `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/bepu-allowlist.txt`. Makes T064, T065 and T066 pass.
- [ ] T076 [US1] Record Bepu's 14 streams with `DOTNET_PreferredVectorBitWidth=128 dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --engine bepu --all --reason "Initial Bepu 2.5.0-beta.33 streams (016)"` into `simcore/vehicles/streams/bepu/`. Makes the Bepu row of T056 and T067 pass (depends on T075, T072).
- [ ] T077 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Locks/{BepuLockSites,LockSiteInstrumenter,LockCountResult}.cs` (Mono.Cecil: every call site to a member on MMO1006's blocking list in `BepuPhysics.dll` and `BepuUtilities.dll`, an instrumented copy in a temporary directory with a counter before each site, run in a child process) and `vehicles lockcount` in `VehiclesCommand.cs` (`--steps 1000 --warmup 300`, streams `flat-accel-30hz` and `pile-up`; managed engines through the instrumented copy or, for the fixture, the analyzer's guarantee plus the instrumented fixture assembly; a native engine without the interposer exits 2 `the counter is missing`). Makes T060 pass.
- [ ] T078 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Orchestration/{StatusTable,CandidateResult,SelectionRun}.cs` with the server-side phases (server checks in a child process per candidate with its environment, lock test, allocation evidence placeholder) and the Failed rows of FR-037 (`BehaviorCheckFailed`, `ServerNotReproducible`, `ResetNotEquivalent`, `RejectionNotNeutral`, `TakesLock`, `StepFaulted`, `StreamNotComparable`, `CoverageNotMet`), a server-side condition holding in every mode (contracts/selection-report.md#statuses); a phase crash or timeout is that phase's reason. Makes T063 pass (depends on T072, T077).

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Checks|FullyQualifiedName~Fixture|FullyQualifiedName~Bepu|FullyQualifiedName~Locks|FullyQualifiedName~SelectionRunServerPhase|FullyQualifiedName~StreamsVerifyEngine|FullyQualifiedName~VehicleScenarioCatalog|FullyQualifiedName~VehiclesChecksCommand"` and `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests` pass.

---

## Phase 4: User Story 2 - Each candidate's builds agree, or the first divergence is named (Priority: P1), slice 1: server-side agreement, statuses and the license scan

**Goal**: The batched vehicle step, manifest version 2 and the vehicle work directory, compare over
version 2 records and windows, NotComparable for engine identity and vehicle rules revision, the
generated streams and their coverage, the license classifier, boundary and NuGet collector, the full
status table, the selection report and its validator, and the selection run's build, license and
agreement phases over fixture inputs.

**Independent Test**: `AgreementCompareVehicleTests.PerturbedRecordsExitOneNamingStep15AndThePositionField`
passes; `StatusTableTests.EachRowGivesItsStatusAndReason` and
`SelectionRunInputTests.EachStatusRowFixtureEndsWithItsStatusAndExitsZero` pass for every FR-037 row;
`SelectionRunInputTests.AGplTransitiveDependencyIsLicenseBlockedInEveryMode` passes.

### Tests for User Story 2 (write first, confirm red)

- [ ] T079 [P] [US2] Write failing Theory `BatchStepperVehicleTests.QueuedCommandsForTheFirstVehicleEqualOneAtATime` in `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperVehicleTests.cs` (US2 scenario 5, FR-020) at 20, 30 and 60 Hz with `FakeVehicleEngine` and the fixture: after `LoadVehicleConfig` and `Reset` with a version 2 state, `StepQueued(n)` for n = 1, 7 and 32 queued commands returns n records (layout 2), each equal byte for byte to stepping the same commands one at a time with the command on the lowest-ID vehicle and the neutral input on every other vehicle and character; `WithoutAVehicleConfigurationItBehavesAs015` (015's `BatchStepperTests.QueuedCommandsEqualOneAtATime` rows still pass); `LoadVehicleConfigReturnsConfigInvalidWithOneLinePerError`. Red: does not compile.
- [ ] T080 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Tests/Cli/AgreementCompareVehicleTests.cs` (US2 scenario 3, US2 Independent Test, SC-004): `PerturbedRecordsExitOneNamingStep15AndThePositionField` (records from `StreamRunner.Run(flat-accel-30hz, …, perturbStep: 15)` on the fixture placed as a build's records: `simcore agreement compare` exits 1, names step 15 as the first divergent step and `vehicles[0].position.x` as the field, with both values in exact bit form, and the report's earlier steps all match); `PerturbationChangesOnlyPositionXByOneUlpAtStep15` (`StreamRunnerVehicleTests` in `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerVehicleTests.cs`: records before step 15 equal, at step 15 the field's bits rise by exactly 1 and both hashes are recomputed); `EveryDifferingFieldIsListedInBitForm` (two fields changed at one step); `WindowsThatDifferAfterARestartAreResetDiverged` (US2 scenario 4: a build's `<stream>.windows` differing from the server's at one window: `ResetDiverged` naming the build, the stream, the restart step and the first differing step); `CompareV1PartComparesOnlyTheVersion1PartAndItsHash` (`--compare v1-part`: a vehicle-only difference passes, a character difference fails). Red: does not compile.
- [ ] T081 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Tests/Cli/AgreementPrepareVehicleTests.cs` (US2 scenarios 1 and 2, contracts/vehicles-cli.md#manifest-version-2): `PrepareWritesManifestVersion2` (`agreement prepare --engine fixture --seeds 1-50` into a temp work directory: `formatVersion` 2, `formats` `{ inputCommand 1, collisionData 1, recordedStream 2, stateSerialization 2, vehicleConfig 1 }`, `vehicleRulesRevision`, `engine`, `host`, `recordLayout` 2, `windows` `{ seconds 0.5 }` with `windowSteps` per stream, `perturbation`, `streams[].vehicles`, `injectedFault` null; prints `prepared <work>: 64 streams (14 named, 50 generated), <steps> steps, rules revision 1`); `PrepareWithAFaultWritesInjectedFault` (`--fault browser-perturb-step-15`: `{ "stream": "flat-accel-30hz", "step": 15 }`); `RequiredBuildsPerModeAreTheSpecsEight` (`BuildNames` gives `node-interp,node-interp-nojit,chromium-worker`, `node-interp-relinked,node-interp-relinked-nojit,chromium-worker-relinked`, `node-aot,chromium-worker-aot`); `CompareOnA015WorkDirectoryPrintsExactly015sSevenBuildLines` (US2 scenario 6, a version 1 manifest). Red: does not compile.
- [ ] T082 [P] [US2] Write failing Theory `NotComparableVehicleTests.AnotherIdentityRevisionOrVersionIsNotComparableNeverDiverged` in `tests/MmoEngine.SimCore.Tests/Agreement/NotComparableVehicleTests.cs` (US2 scenario 7, FR-014) over a fixture stream with its engine code, release, fingerprint or `vehicleRulesRevision` changed, or written as version 3: the suite reports NotComparable naming both values (`engine fixture fixture-1 <a> in the stream; this build is fixture fixture-1 <b>`, `vehicle rules revision 2 in the stream; this core implements vehicle rules revision 1`), never Diverged; `simcore streams verify --engine fixture` and `simcore agreement compare` exit 2. The selection half, `SelectionRunInputTests.AnOwnStreamNotComparableIsFailed`, is written in T088. Red: does not compile.
- [ ] T083 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Scenarios/VehicleStreamGeneratorTests.cs` (US2 scenario 12, FR-010): `EverySeedOnTheFixtureEngineMeetsItsCoverage` (seeds 1 to 50, 8 vehicles, 20 s at the rate cycling 20, 30 and 60 Hz by seed: each seed has at least one vehicle contact event, every vehicle's path length is at least 50 m, and at least one seed has a step with a vehicle whose four wheels are out of contact); `AMissNamesTheSeedAndTheCondition` (`CoverageCheck` on the `coverage-parked` fixture: fails naming each seed and `every vehicle travels at least 50 m`); `CommandsComeFromSplitMix64AndOneInSixtyFourIsInvalid`; the selection half, `SelectionRunInputTests.CoverageMissIsCoverageNotMet`, is written in T088; `BepuCoverageVerdictIsRecorded` in `tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/BepuServerChecksTests.cs` (one coverage entry with verdict and misses, not asserted). Red: does not compile.
- [ ] T084 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/License/SpdxTests.cs` (FR-031, contracts/license-scan.md#classifier): Theory `EachExpressionGivesItsVerdict` over the contract's table verbatim (`MIT AND Apache-2.0 WITH LLVM-exception AND (MIT OR Apache-2.0)` pass; `MIT/Apache-2.0` pass; `Zlib OR Apache-2.0 OR MIT` pass; `Unlicense OR MIT` pass; `MIT OR NCSA` pass; `MIT OR Apache-2.0 AND BSD-3-Clause` pass; `(MIT OR Apache-2.0) AND Unicode-3.0` `LicenseNotAllowed`; `Zlib`, `CC0-1.0`, `SunPro`, `GPL-3.0-only` and `GPL-3.0-or-later WITH GCC-exception-3.1` `LicenseNotAllowed`; `MIT AND (` `UnparseableExpression`; empty or null `NoLicense`) and `PrecedenceIsWithThenAndThenOr`. Red: does not compile.
- [ ] T085 [P] [US2] Write failing Theory `ClassifierGoldenTests.EachInventoryGivesItsExpectedResult` in `tests/MmoEngine.SimCore.Vehicles.Tests/License/ClassifierGoldenTests.cs` (US2 scenario 10) over `simcore/vehicles/fixtures/license/<case>.inventory.json` → `<case>.expected.json` byte for byte: `all-allowed` (Pass); `gpl-transitive` (Blocked, naming the transitive component and `GPL-3.0-only`); `dlmalloc-cc0` (`CC0-1.0`); `musl-sunpro` (`SunPro`); `foldhash-zlib` (`Zlib`); `unmapped-member` (`NoLicense`); `os-toolchain-boundary` (listed under `boundary`, verdict Pass); `nuget-license-file` and `nuget-license-url` (`NoLicense`). Red: does not compile.
- [ ] T086 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/License/NuGetCollectorTests.cs` and `BoundaryTests.cs`: `ServerCollectorReadsDepsJsonPackagesAndTheirNuspecLicenses` and `BrowserCollectorReadsAssetsWithRuntimeAssembliesInTheBootManifest` over a committed tree under `simcore/vehicles/fixtures/license/nuget/` (`project.assets.json`, `deps.json`, `.nuspec` files); `InventoryIsCanonical` (sorted keys and components, LF, no absolute path: roots `<repo>`, `<nuget>`); `OverridesNameThePackageVersionExpressionAndEvidence`; `BoundaryTests.TheCoreOnlyServerMapsTheBoundaryLibraries` (a child core-only `simcore streams verify` on `flat-run-30hz` reads its `/proc/self/maps`; every library of `simcore/vehicles/license/boundary.json`'s server set is mapped); `BoundaryComponentsNeverBlock`. Red: does not compile.
- [ ] T087 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/StatusTableTests.cs` (US2 scenario 11, FR-037, SC-009): Theory `EachRowGivesItsStatusAndReason` over one fixture input per row in `simcore/vehicles/fixtures/statuses/` (`FitCheckFailed` NotTested; `BuildFailed`, `BuildTimedOut`, `ModeNotProduced`, `BuildNotRun` NotBuilt; `LicenseNotAllowed` LicenseBlocked; `BehaviorCheckFailed`, `ServerNotReproducible`, `ResetNotEquivalent`, `RejectionNotNeutral`, `TakesLock`, `StepFaulted`, `StreamNotComparable`, `CoverageNotMet` Failed; `RecordDiverged`, `ResetDiverged` Diverged; none Qualified), each with the reason's details as contracts/selection-report.md lists them; `SeveralRowsTakeTheFirstStatusInOrderAndListEveryReason` (for example `LicenseNotAllowed` and `RecordDiverged`: LicenseBlocked with both reasons); `ServerConditionsHoldInEveryModeAndBuildConditionsOnlyInTheirs`; `ARequiredBuildThatDidNotRunIsNotRunAndMakesTheModeNotBuilt`. Red: does not compile.
- [ ] T088 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionRunInputTests.cs` (US2 scenarios 8, 10 and 11): Theory `EachStatusRowFixtureEndsWithItsStatusAndExitsZero` (each row's input through `SelectionRun` with in-process phase runners; the run exits 0 whenever every candidate has a status in every mode); Theory `ABuildFailureOrTimeoutIsNotBuiltWithItsStepAndLastFiftyLines` (`build-failed`, `build-timed-out` outcome files: NotBuilt with the build, the failing step and exactly the last 50 lines; every other candidate still evaluated); `AGplTransitiveDependencyIsLicenseBlockedInEveryMode` (the `license-gpl-transitive` input: LicenseBlocked `LicenseNotAllowed` naming the component and `GPL-3.0-only` in all three modes; the others still evaluated; exit 0); `AnOwnStreamNotComparableIsFailed` (US2 scenario 7: the `other-identity-stream` input is Failed `StreamNotComparable` with the stream and both identities); `CoverageMissIsCoverageNotMet` (US2 scenario 12: `coverage-parked` is Failed `CoverageNotMet` with the seed and condition); `AReportThatFailsValidationExitsOne`; `MissingRunnerClassExitsTwo`. Red: does not compile.
- [ ] T089 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionReportValidatorTests.cs` (FR-018, SC-001, contracts/selection-report.md#what-validate-report-checks): `AValidFixtureReportPasses`; Theory `EachDefectFailsWithItsPathAndProblem` (a mode missing for a candidate; a candidate or fixture missing or out of order; a status with a required build missing or extra; a Qualified status with a reason; a status not matching its first reason's row; a reason missing a detail; Rapier without six fit-check criteria; an absolute path or clock time in a string; an unknown enumerated value); `ValidateReportExitCodes` (`simcore vehicles select validate-report`: 0 valid, 1 invalid with one `path: problem` line per problem, 2 unreadable or over 16 MiB). Red: does not compile.

### Implementation for User Story 2

- [ ] T090 [US2] Add `LoadVehicleConfig` and the vehicle `StepQueued` to `src/MmoEngine.SimCore/Batch/BatchStepper.cs` (the constructor takes an optional `IVehicleEngineFactory`; with no vehicle configuration it behaves exactly as in 015). Makes T079 pass.
- [ ] T091 [US2] Extend `src/MmoEngine.SimCore.Cli/Agreement/{BuildNames,AgreementManifest,WorkDirectory,AgreementComparison,AgreementReport,RefusalFixtures}.cs` and `src/MmoEngine.SimCore.Cli/Commands/AgreementCommand.cs`: the three new builds with their modes, manifest version 2 for vehicle work directories only, the vehicle work directory layout (`server/<stream>.windows`, `engine/identity.json`), layout-2 records, `.windows` comparison (`ResetDiverged`), `--compare full|v1-part`, `--engine`, `--fault`, `--selection-config`, the NotComparable lines of contracts/vehicles-cli.md; version 1 work directories print exactly 015's seven build lines. Extend `src/MmoEngine.SimCore.Server/DivergenceReport.cs` and `StateFieldReader.cs` for the vehicle field paths. Makes T080, T081 and T082 pass (depends on T090).
- [ ] T092 [P] [US2] Implement `src/MmoEngine.SimCore.Vehicles.Server/{VehicleStreamGenerator,Checks/CoverageCheck}.cs` (`vseed-<nnn>`, 8 vehicles in the arena, 20 s, rate cycling 20, 30, 60 Hz by seed, commands from `SplitMix64.At(seed, counter)`, one in 64 invalid) and wire the coverage check into `VehicleChecks`. Makes T083 pass.
- [ ] T093 [US2] Implement `src/MmoEngine.SimCore.Vehicles.Selection/License/{Inventory,SpdxExpression,LicenseClassifier,Boundary,NuGetCollector,LicenseResult}.cs` (contracts/license-scan.md: inventory canonical JSON; `ecosystem` one of `nuget`, `runtime-pack`, `cargo`, `rust-sysroot`, `source`, `emscripten`, `os-toolchain`; `basis` one of `nuspec-expression`, `nuspec-override`, `cargo-license`, `spdx-header`, `notice`, `owner-default`, `package-copyright`; classifier pure), commit `simcore/vehicles/license/boundary.json` and `simcore/vehicles/license/overrides.json`, the fixture inventories and expected results under `simcore/vehicles/fixtures/license/`, and add `vehicles license scan` (NuGet and runtime-pack collectors in this slice) and `vehicles license classify` to `VehiclesCommand.cs` (0 Pass, 1 Blocked, 2). Makes T084, T085 and T086 pass.
- [ ] T094 [US2] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Report/{SelectionReport,SelectionReportWriter,SelectionReportValidator}.cs` (the contract's fields; `formatVersion` 1; `modes` exactly the three modes in order; `candidates` exactly `bepu`, `jolt`, `rapier` in order; `fixtures` exactly `fixture` and `fixture-perturbed`; bounded read through `BoundedRead.ReadAtMost` at 16 MiB) and `vehicles select validate-report` in `VehiclesCommand.cs`. Makes T089 pass.
- [ ] T095 [US2] Complete `StatusTable.Decide` over every row of FR-037 and the order NotTested, NotBuilt, LicenseBlocked, Failed, Diverged, and extend `SelectionRun` with the build-outcome phase (`<engine>/outcomes.json`, `<host>/<mode>/outcome.json`), the license phase, the agreement phase behind an `IAgreementRunner` seam (an in-process runner calling `StreamRunner.Run` and `RunWindow` with the manifest's `injectedFault`, used by the tests; the Node and Playwright runner lands in Phase 13) and `vehicles select run` (`MMO_RUNNER_CLASS` required, `--work` empty or absent, one line per candidate and mode, `selection: <n> candidates, report <path>`); commit the status-row inputs under `simcore/vehicles/fixtures/statuses/` (depends on T078, T093, T094, T091). Makes T087 and T088 pass.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BatchStepperVehicle|FullyQualifiedName~AgreementCompareVehicle|FullyQualifiedName~AgreementPrepareVehicle|FullyQualifiedName~NotComparableVehicle|FullyQualifiedName~StreamRunnerVehicle"` and `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~License|FullyQualifiedName~StatusTable|FullyQualifiedName~SelectionRunInput|FullyQualifiedName~SelectionReportValidator|FullyQualifiedName~VehicleStreamGenerator"` pass.

---

## Phase 5: User Story 3 - The engine is chosen by the stated rule and recorded as gate 9's decision (Priority: P1), slice 1: the rule, the record, staleness and the gate plan on fixture reports

**Goal**: `simcore vehicles decide`, the decision record, the staleness check and the gate plan,
tested on committed fixture reports covering every branch and band boundary.

**Independent Test**: `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` decides every
fixture report and matches its expected record byte for byte; `StalenessTests.EachChangedInputIsNamed`
fails the check naming each changed input.

### Tests for User Story 3 (write first, confirm red)

- [ ] T096 [P] [US3] Write failing Theory `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/DecisionRuleTests.cs` (US3 scenarios 1-6, 10 and 11, SC-003) over every `simcore/vehicles/fixtures/decision/<case>.report.json`, comparing `simcore vehicles decide`'s output with `<case>.expected.json` byte for byte, with one row per case: `one-qualified` (US3.1: that candidate, BitExact in every mode); `cheapest-wins` (US3.2: the lowest cost wins when every other exceeds 1.10 × it); `cost-tie-brotli-decides`, `both-tie-lowest-cost-wins` and `exact-equal-fixed-order` (US3.3, the last two including Jolt 100 µs and 500 KB against Bepu 109 µs and 540 KB: both bands tie and Jolt wins; Bepu before Jolt only on exactly equal values); `nonexact-bepu` (US3.4: NonExact, the provisional flag with its exact condition text `vehicle correction distances under the 014 network profiles (harness/profiles/*.json) are not measured against stated bounds`, `givesUp`, and `gate9.vehicleExitCriterionMet` false with a statement saying a NonExact mode does not by itself meet it); `none-bepu-beyond-bound`, `none-bepu-failed`, `none-every-mode` (US3.5: None, each candidate's status and reasons per mode, and for `none-every-mode` the statement that gate 9's vehicle exit criterion is not met); `mixed-mode` (US3.6: NonExact in `aot`, None in `interp-prebuilt` and `interp-relinked`, `conditional` true, `conditionalOn` `"gate 9's WebAssembly build-mode decision"`); `cost-100-110` (tie), `cost-100-110.01` (no tie), `cost-100-109-115` (tied set the first two), and the same three for Brotli bytes (US3.10: the record's tied sets and winner); `cheapest-diverged` (US3.11: the Qualified dearer candidate wins BitExact and the Diverged one is listed with its status and reason). Add `DecisionRuleTests.FixtureCandidatesNeverTakePartInTheRealDecision`. Red: does not compile.
- [ ] T097 [US3] Write failing test `DecisionRuleTests.TheSameReportGivesIdenticalBytesTwice` in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/DecisionRuleTests.cs` (US3 scenario 7): two `decide` runs on `mixed-mode.report.json` into two temp files give identical bytes, with LF, two-space indentation, a final newline and no clock time. Red: does not compile.
- [ ] T098 [P] [US3] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/StalenessTests.cs` (US3 scenario 8, FR-028) on a temporary repository copy holding a fixture record: Theory `EachChangedInputIsNamed` over an engine pin file, an engine identity input file, `SimCoreVersion.VehicleRulesRevision` (through the recomputation seam), the vehicle config block of `simcore/vehicles/config/default.json`, `simcore/vehicles/selection.json`, a committed stream changed, a stream added and a stream removed: `simcore vehicles decision check-stale --record <copy>` prints `stale: <input>: record <old>, repository <new>` for exactly the changed inputs, then `decision record: stale`, and exits 1; `AnUnchangedRepositoryIsCurrent` (exit 0, `decision record: current`); `TheProvisionalFlagIsPrintedFirst` (a NonExact record: the first line is `provisional: <condition>`); `AMissingOrInvalidRecordExitsTwo`. Red: does not compile.
- [ ] T099 [P] [US3] Write failing Theory `GatePlanTests.EachRecordGivesItsGateAndLegs` in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/GatePlanTests.cs` (FR-029, supports US3 scenario 9) over no record (`{"gate":"NoRecord","legs":[],"provisional":false}`), None in every mode (`gate` None, no legs), BitExact in every mode (three legs with each mode's `require` set), NonExact (`provisional` true) and a conditional record (each mode's own winner in its leg): one JSON line, exit 0. Red: does not compile.
- [ ] T100 [P] [US3] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/VehiclesDecideCommandTests.cs` (FR-037 exit codes): `DecideExitsZeroForEveryOutcomeNoneIncluded`; `AnInvalidReportExitsOneAndWritesNothing` (its `path: problem` lines printed, the out file absent); `AUsageErrorOrUnreadableInputExitsTwo`; `OutputIsOneLinePerModeThenTheSummary` (`<mode>: <outcome> <winner|->`, then `decision: <conditional|unconditional>; provisional: <yes|no>`); `FixturesFlagDecidesOverTheFixturePair` (a report whose `fixtures` are Qualified and Diverged: `fixture` with BitExact). Red: does not compile.

### Implementation for User Story 3

- [ ] T101 [US3] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Decision/{DecisionRule,DecisionRecord,DecisionRecordWriter,InputFingerprint,Gate9Statement}.cs` (contracts/decision-record.md: per mode, values as `System.Decimal`, ties when x ≤ (1 + band) × L; branches qualified, nonExactBepu and none; the record's members in the contract's order; `amendmentCandidates` from every `LicenseNotAllowed` component) and `vehicles decide` in `VehiclesCommand.cs`, and commit the 19 fixture cases as `simcore/vehicles/fixtures/decision/<case>.report.json` and `<case>.expected.json` (the expected records written by the command and read back against the contract's rules in the task). Makes T096, T097 and T100 pass.
- [ ] T102 [US3] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Decision/{StalenessCheck,GatePlan}.cs` and `vehicles decision check-stale` and `vehicles decision gate-plan` in `VehiclesCommand.cs` (depends on T101). Makes T098 and T099 pass.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Decision|FullyQualifiedName~VehiclesDecideCommand"` passes.

---

## Phase 6: User Story 4 - Each candidate's cost and download size are measured (Priority: P2), slice 1: zero allocation and the measurement fields

**Goal**: Zero managed allocation per vehicle step for the fixture and Bepu, the allocation
evidence phase, and the report's measurement fields with their validation.

**Independent Test**: `VehicleStepAllocationTests.EachCandidatesVehicleStepAllocatesNothingAfterWarmUp`
passes for `fixture` and `bepu`; `SelectionReportValidatorTests.AMissingMeasurementWithoutAReasonFails`
passes every row.

### Tests for User Story 4 (write first, confirm red)

- [ ] T103 [P] [US4] Write failing Theory `VehicleStepAllocationTests.EachCandidatesVehicleStepAllocatesNothingAfterWarmUp` in `tests/MmoEngine.SimCore.Vehicles.Tests/Allocation/VehicleStepAllocationTests.cs` (US4 scenario 4, FR-025, SC-005) over `fixture` and `bepu` at 20, 30 and 60 Hz on `pile-up` and an 8-vehicle generated stream: after warm-up, ten windows of 1,000 steps, each 0 bytes by `GC.GetAllocatedBytesForCurrentThread`, with 015's one-rerun rule (`ZeroAllocationMeasurement`), in a `ZeroAllocationCollection` with parallelization off (its reason in a comment). Add core cases to `tests/MmoEngine.SimCore.Tests/Allocation/VehicleStageAllocationTests.cs`: `WheelModelContactsAndSerializationAllocateNothing` with `FakeVehicleEngine` at 64 and 256 vehicles. Red: does not compile.
- [ ] T104 [P] [US4] Write failing Theory `SelectionReportValidatorTests.AMissingMeasurementWithoutAReasonFails` in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionReportValidatorTests.cs` (US4 scenario 5, FR-024) over a fixture report with: the server benchmarks missing a vehicle count of 1, 16, 64 and 256; a mode's browser entry missing with `produced` true; a step cost missing a runtime (`node`, `chromium-worker`) or a vehicle count (1, 16, 64); startup missing `coldStarts` 10; the download missing raw, Brotli or gzip; a measurement missing its runner class, build mode or engine identity: each fails with its `path: problem` line; and `AnUnmeasuredModeWithItsReasonPasses` (`produced: false` with a reason). Red: the filter fails.

### Implementation for User Story 4

- [ ] T105 [US4] Make every vehicle buffer world-owned and sized at world creation from `vehicles.maxVehicles` and the static triangles in `src/MmoEngine.SimCore/Vehicles/*.cs`, `src/MmoEngine.SimCore.Vehicles.Fixture/*.cs` and `src/MmoEngine.SimCore.Vehicles.Bepu/BepuEngine.cs` (`BufferPool` sized at `Load`; `Load` and `Begin` may allocate, `Step` never does), and add the allocation evidence phase (`allocation.bytesPer1000Steps` per candidate) to `SelectionRun`. Makes T103 pass. If Bepu's adapter still allocates (research R17's 5 B per step), find and remove its source in the adapter; the test stays.
- [ ] T106 [US4] Add the measurement members (`server` with `label: "measurement"` and Bepu's `vectorPin`, `browser[]` per mode with `produced`, `reason`, `download`, `startup`, `stepCost` with `meanMicros` and `p99Micros` to three decimals) and their validation rules to `src/MmoEngine.SimCore.Vehicles.Selection/Report/{SelectionReport,SelectionReportValidator}.cs`. Makes T104 pass.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Allocation|FullyQualifiedName~SelectionReportValidator"` and `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~VehicleStageAllocation"` pass.

---

## Phase 7: User Story 5 - Bepu's cross-build divergence is measured (Priority: P2), slice 1: the measurement on record pairs and the window check

**Goal**: `DivergenceMeasurement`, its bound verdict, `vehicles divergence`, the window check against
every committed network profile, and `vehicles selection-config check`.

**Independent Test**: `DivergenceMeasurementTests.AKnownOffsetIsReportedWithinItsTolerance` and
`WindowCheckTests.TheWindowCoversEveryCommittedProfile` pass.

### Tests for User Story 5 (write first, confirm red)

- [ ] T107 [P] [US5] Write failing test `DivergenceMeasurementTests.AKnownOffsetIsReportedWithinItsTolerance` in `tests/MmoEngine.SimCore.Vehicles.Tests/Divergence/DivergenceMeasurementTests.cs` (US5 scenario 3) on the committed pair `simcore/vehicles/fixtures/divergence/known-offset.{server,browser}.records`, whose only difference is one vehicle offset by 0.03 m in position and 0.5° in orientation: the reported position error equals 0.03 within 1e-4 m and the orientation error equals 0.5 within 1e-3 degrees, at that vehicle and every step of the offset. Add `DivergenceFixtureTests.CommittedPairsReproduce` (the pairs regenerate byte for byte from `DivergenceFixtures.Write`, a test-support writer in `tests/MmoEngine.SimCore.Vehicles.Tests/Support/DivergenceFixtures.cs`). Red: does not compile.
- [ ] T108 [US5] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Divergence/DivergenceMeasurementTests.cs` (US5 scenarios 2, 4 and 5): `MaximaAndP99NameTheirStepAndVehicle` (per stream, build and mode: the maximum and nearest-rank p99 of position, orientation and linear velocity error, with the step and vehicle of each maximum, on a pair with offsets placed at known steps); Theory `BoundsGiveWithinOrBeyond` (every window's maximum position error exactly 0.05 m and orientation exactly 1° → `WithinBounds`; one step at 0.0500001 m or 1.00001° → `BeyondBounds` naming the stream, step, vehicle and value; a large linear velocity error alone → `WithinBounds`, because it is reported and not bounded); `IdenticalRecordsGiveExactlyZero` (every error exactly 0.0); `QAndMinusQAreTheSameOrientation` (a sign-flipped quaternion gives orientation error 0); `WindowStepsAreTheCeilingOfHalfASecondTimesTheRate` (10, 15 and 30 at 20, 30 and 60 Hz). Red: does not compile.
- [ ] T109 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/VehiclesDivergenceCommandTests.cs` (contracts/vehicles-cli.md): `DivergenceMeasuresEveryBuildsWindowsAgainstTheServer` (a work directory prepared for the fixture, with `.windows` files written in process by `StreamRunner.RunWindow`, one build copied from the server and one from the `known-offset` pair: writes `report/divergence.json` with one result per build and stream, exit 0 when within bounds and 1 when beyond); `MissingWindowsExitTwo`. Red: does not compile.
- [ ] T110 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/WindowCheckTests.cs` (US5 scenario 6, FR-022): `TheWindowCoversEveryCommittedProfile` (every `harness/profiles/*.json` at 20, 30 and 60 Hz: span = `ceil((roundTripMs + 2 × jitterMs) / 1000 × rate) + 2 + (lossPercent > 0 ? 1 : 0)` ≤ `ceil(windowSeconds × rate)`; Poor at 20 Hz is 7 + 2 + 1 = 10, exactly the window; 14 of 15 at 30 Hz and 24 of 30 at 60 Hz); Theory `AProfileOneStepOverTheWindowFailsNamingProfileRateAndSpans` at each rate on fixture profiles in a temp directory (`profile <name> at <rate> Hz: reconciliation span <n> steps, window <w> steps`); `SelectionConfigCheckExitCodes` (`simcore vehicles selection-config check`: 0 valid and covered, 1 invalid or a profile not covered, 2 unreadable). Red: does not compile.

### Implementation for User Story 5

- [ ] T111 [US5] Implement `src/MmoEngine.SimCore.Vehicles.Server/DivergenceMeasurement.cs` (research R23: position Euclidean, orientation 2·acos(min(1, \|q₁·q₂\|)) in degrees, linear velocity, in `double`; maxima, p99, verdict) and `vehicles divergence` in `VehiclesCommand.cs` (reads `.windows` files, `u32 startStep | u32 recordCount | records (layout 2)` per window), and commit the record pairs under `simcore/vehicles/fixtures/divergence/` written by `DivergenceFixtures.Write`. Makes T107, T108 and T109 pass.
- [ ] T112 [US5] Implement `src/MmoEngine.SimCore.Vehicles.Selection/Config/WindowCheck.cs` and `vehicles selection-config check` in `VehiclesCommand.cs`. Makes T110 pass.
- [ ] T113 [US5] Add the windowed-run and divergence phase to `SelectionRun` (per required build and mode, through `IAgreementRunner`) and the `divergence[]` member to the report (depends on T111, T095).

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Divergence|FullyQualifiedName~WindowCheck"` passes.

---

## Phase 8: User Story 6 - Rapier joins only if it fits the harness cheaply (Priority: P3), slice 1: the Rapier adapter and manifest, the fit check and its fixtures

**Goal**: Rapier's C# adapter over the shared byte ABI (behind a fake in tests), its manifest-only
crate, the fit check's criteria (b), (d) and (f) on the committed files, the evaluation order, and the
orchestration driven by fit-check fixtures.

**Independent Test**: `FitCheckOrchestrationTests.EachFailingCriterionIsNotTestedWithThatCriterion`
and `FitCheckOrchestrationTests.AllPassingSchedulesRapierForEveryPhaseBepuAndJoltGet` pass.

### Tests for User Story 6 (write first, confirm red)

- [ ] T114 [P] [US6] Write failing Theory `FitCheckOrchestrationTests.EachFailingCriterionIsNotTestedWithThatCriterion` in `tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/FitCheckOrchestrationTests.cs` (US6 scenario 2) over `simcore/vehicles/fixtures/fitcheck/fail-{a,b,c,d,e,f}.json`: through `SelectionRun`, Rapier is NotTested (`FitCheckFailed`) in all three modes with that criterion as the status's criterion; the other candidates are decided as usual; the run exits 0. Red: does not compile.
- [ ] T115 [US6] Write failing test `FitCheckOrchestrationTests.AllPassingSchedulesRapierForEveryPhaseBepuAndJoltGet` in the same file (US6 scenario 3) with `simcore/vehicles/fixtures/fitcheck/all-pass.json`: the orchestrator's plan lists for Rapier exactly the phases, scenarios, agreement checks and measurements it lists for Bepu and Jolt, and Rapier's report entry, written with phase results from in-process fakes, has the same members as theirs. Red: does not compile.
- [ ] T116 [P] [US6] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/FitCheckCriteriaTests.cs` (FR-030): `CriteriaRunInTheOrderCBDFAE`; `ACriterionAfterAFailedCIsNotEvaluatedWithItsReason` (`notEvaluated` true); Theory `CriterionBChecksTheManifest` on `src/MmoEngine.SimCore.Vehicles.Rapier/native/` and mutated temp copies (an exact `=` version, `enhanced-determinism` on, `simd8` off, no `[patch]`, a `Cargo.lock` checksum per crate, `--locked` on every build command: each mutation fails); Theory `CriterionFCountsNonBlankLines` (1,500 passes, 1,501 fails, over `src/MmoEngine.SimCore.Vehicles.Rapier/**` C#, Rust, scripts and glue). In `tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/RapierIsolationTests.cs` (criterion (d)): `NoSharedFileReferencesRapier` (no file of the scenarios, formats, runners, `IVehicleEngine` or the other adapters references Rapier's library, namespace or adapter types) and `TheRapierAdapterCompilesAgainstIVehicleEngine`. Red: does not compile.
- [ ] T117 [P] [US6] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/RapierEngineTests.cs` (research R28) driving `RapierEngine` through a fake `IVehicleEngineCalls`: `LoadPayloadMatchesTheAbi` (`"VEHL"`, `u16` version 1, `u16` reserved 0, the vehicle block, the engine settings entries, `u32 triangleCount`, 9 `f32` and a `u8` kind per triangle); `BodiesCrossAsFiftySixByteRecordsWithRawBits` (no float converted: NaN payloads and −0 survive the round trip); Theory `NegativeReturnsMapToEngineResults` (−1/−2/−3 from `veh_load` → `LoadRefused`; −4 from `veh_step` → `Faulted` with the `i32 engineCode`; −2 count or IDs differing from `Begin`); `TheBindingFactoryChoosesTheBrowserBindingOnlyInTheBrowser`; `IdentityMismatchIsLoadRefused`. Add the Rapier row (`MMO1008:interop`) to `AdapterBackstopTests.EachAdapterHasNoDiagnosticOutsideItsExemption`. Red: does not compile.

### Implementation for User Story 6

- [ ] T118 [US6] Implement `src/MmoEngine.SimCore.Vehicles.Rapier/{RapierEngine,RapierEngineFactory,IVehicleEngineCalls,RapierCalls,RapierNative,RapierJs}.cs` (the marshalling of contracts/engine-adapter.md#native-abi with `BinaryPrimitives`; `RapierNative` bodiless `[LibraryImport("libmmo_vehicles_rapier")]` partial methods; `RapierJs` bodiless `[JSImport(..., "mmo-vehicle-engine")]` methods with `[SupportedOSPlatform("browser")]`; `RapierCalls.Create()` choosing by `OperatingSystem.IsBrowser()`), the manifest-only crate `src/MmoEngine.SimCore.Vehicles.Rapier/native/{Cargo.toml,Cargo.lock,rust-toolchain.toml,src/lib.rs}` (`rapier3d` `=0.36.1` with `enhanced-determinism`, Rust 1.97.0, empty `lib.rs`; the lock file from `cargo generate-lockfile` with crates.io access), and `simcore/vehicles/engines/rapier.json`. Makes T117 pass.
- [ ] T119 [US6] Implement `src/MmoEngine.SimCore.Vehicles.Selection/FitCheck/{FitCheck,FitCheckResult,ManifestCriterion,IsolationCriterion,LineCountCriterion}.cs` (criteria (a), (c) and (e) read their evidence from an input the slice-2 collectors and builds write), commit `simcore/vehicles/fixtures/fitcheck/{fail-a,fail-b,fail-c,fail-d,fail-e,fail-f,all-pass}.json`, and add the fit-check phase to `SelectionRun` (Rapier only, first; with any failure Rapier is NotTested in every mode with every failed criterion listed and the first in the order as the status's criterion; with all passing, Rapier is scheduled like Bepu and Jolt) (depends on T118, T095). Makes T114, T115 and T116 pass.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~FitCheck"` and `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~AdapterBackstopTests"` pass.

---

## Phase 9: User Story 7 - Vehicle state joins the canonical state under new versions (Priority: P3)

**Goal**: Pin the version 2 formats with golden and refusal fixtures, move 015's unsupported-version
case to version 3, commit the version 1 baseline manifest, and prove canonicalisation, refusals and
the version 1 part hash. This story is complete in slice 1.

**Independent Test**: `VehicleGoldenFixtureTests.EachValidV2CaseRoundTripsToIdenticalBytes`,
`VehicleGoldenRefusalTests.EachRefusalCaseIsRefusedWithItsCodeOffsetAndField`,
`Version1BaselineTests.TheVersion1FilesMatchTheBaseline` and
`Version1BaselineTests.EveryVersion1StreamStillMatches` pass.

### Tests for User Story 7 (write first, confirm red)

- [ ] T120 [P] [US7] Write failing Theory `VehicleGoldenFixtureTests.EachValidV2CaseRoundTripsToIdenticalBytes` in `tests/MmoEngine.SimCore.Tests/Formats/VehicleGoldenFixtureTests.cs` (US7 scenario 1, FR-016) over `simcore/golden/stream/v2-one-vehicle`, `v2-characters-and-vehicles`, `v2-rejected-vehicle-command` and `v2-seeded`: decode then re-encode gives bytes identical to the `.bin`, and the decoded values equal the case's JSON (for `v2-one-vehicle`: engine Fixture, release `fixture-1`, fingerprint `0123456789abcdef`, `vehicleRulesRevision` 1, 30 Hz, 0 characters, vehicle ID 1 at (64.0, 0.85, 28.0), spawn compression 0.109, expected hashes `0000000000000001` and `0000000000000002`, v1 part hashes `0000000000000003` and `0000000000000004`; for `v2-rejected-vehicle-command` the buttons `0xFE00` decode, and stepping rejects them). Red: the fixture files do not exist.
- [ ] T121 [P] [US7] Write failing Theory `VehicleGoldenRefusalTests.EachRefusalCaseIsRefusedWithItsCodeOffsetAndField` in `tests/MmoEngine.SimCore.Tests/Formats/VehicleGoldenRefusalTests.cs` (US7 scenarios 3, 4 and 5) with one row per refusal case of contracts/vehicle-formats.md#golden-fixtures: `v2-unsupported-version` (`UnsupportedVersion`, NotComparable, `version`), `v2-header-length` (`Inconsistent`, `headerLength`), `v2-truncated` (`Truncated`, `fileLength`), `v2-trailing-bytes` (`Oversized`, `fileLength`), `v2-no-entities` (`OutOfRange`, `characterCount`), `v2-too-many-vehicles` (`Oversized`, `vehicleCount`), `v2-unknown-engine` (`OutOfRange`, `engine`), `v2-release-padding` (`Inconsistent`, `release`), `v2-vehicle-config-unknown-field`, `v2-vehicle-config-missing-field`, `v2-state-vehicle-count` (`Inconsistent`, `initialState.vehicleCount`), `v2-vehicle-ids-not-ascending` (`Inconsistent`, `initialState.vehicles[1].id`), `v2-vehicle-id-zero` (`OutOfRange`, `initialState.vehicles[0].id`), `v2-vehicle-non-finite` (`NonFinite`, `initialState.vehicles[0].linearVelocity.y`), `v2-vehicle-negative-zero` (`OutOfRange`, `initialState.vehicles[0].position.y`), `v2-state-has-vehicle-events`, `v2-vehicle-command-index`, `v2-too-many-vehicle-commands`, and the open cases `v2-vehicle-rules-revision` (`VehicleRulesRevisionMismatch`, NotComparable), `v2-engine-identity` (`EngineIdentityMismatch`, NotComparable), `v2-collision-missing` (`CollisionDataMissing` at offset 84, refused before any step: the fixture engine's `Step` is never called) and `v2-vehicle-config-invalid` (`ConfigValidationException`: `vehicles.massKg: must be finite`). Each row asserts the code, the offset listed in the case's JSON and the field; every refusal returns no partial result (the decoder's output is null and nothing was allocated past the bounded header). Red: the fixture files do not exist.
- [ ] T122 [P] [US7] Change 015's tests to expect the moved refusal (FR-016): `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs` row `unsupported-version` expects version 3 and the detail `unsupported version 3; this reader supports versions 1 and 2`; `tests/MmoEngine.SimCore.Tests/Agreement/NotComparableTests.cs` builds its modified `flat-run-30hz` at version 3; `tests/MmoEngine.SimCore.Tests/Cli/AgreementCommandTests.cs`'s `unsupported-version` refusal expects the same text. Red: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~GoldenRefusalTests|FullyQualifiedName~NotComparableTests|FullyQualifiedName~AgreementCommandTests"` fails on the committed version 2 case.
- [ ] T123 [P] [US7] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/Version1BaselineTests.cs` (US7 scenario 2, US2 scenario 6, FR-015, SC-002): `TheVersion1FilesMatchTheBaseline` (each line `<sha256>  <path>` of `simcore/baseline/015-65112ab.sha256`, in path order, equals the SHA-256 of the repository's file; exactly 20 lines: the 19 streams and `simcore/content/test-world.mmcd`; and `SimCoreVersion.RulesRevision == 1`); `EveryVersion1StreamStillMatches` (each of the 19 streams decodes as version 1, runs under state serialisation version 1 with no engine, and every step's hash equals the expected hash it carries). Red: the manifest does not exist.
- [ ] T124 [P] [US7] Write failing tests in `tests/MmoEngine.SimCore.Tests/World/VehicleCanonicalisationTests.cs` (US7 scenario 6): Theory `NegativeZeroIsCommittedAndHashedAsPositiveZero` over every vehicle `f32` field (`FakeVehicleEngine` returns −0 there after a step: the committed state holds +0's bits and the hash equals the hash with +0); Theory `ANonFiniteFieldStopsTheStepNamingIt` over NaN, +∞ and −∞ in every body field (for example `vehicles[0].angularVelocity.y`): `StepFault { NonFinite, <path> }`, nothing committed. Red: the filter fails on the paths not yet canonicalised.
- [ ] T125 [P] [US7] Write failing Theory `VehicleStateLoadTests.EachLoadCheckRefusesWithItsCodeAndField` in `tests/MmoEngine.SimCore.Tests/Formats/VehicleStateLoadTests.cs` (US7 scenario 4) over the loading table of contracts/vehicle-formats.md: `vehicleCount` 257 (`Oversized`); a record or count past the end (`Truncated`) and bytes after the last list (`Oversized`, `length`); IDs 0 or 65536 (`OutOfRange`) and not ascending (`Inconsistent`, with the message shape `state vehicles[1].id at offset <o>: id 3 is not above 7`); a non-finite or −0 float; a position beyond ±8192 m, \|q\|² of 0.997 or 1.003, `contact` 2, `compression` −0.01 or above `suspensionTravel`, a rear steering angle not 0, a steering angle above the maximum (`OutOfRange`); a non-zero vehicle event count (`Inconsistent`); a position outside the playable area; `vehicleCount` above `vehicles.maxVehicles`. Every refusal leaves the world's previous state unchanged. Red: the filter fails on the rows not yet checked.
- [ ] T126 [P] [US7] Write failing Theory `V1PartHashTests.TheVersion1PartHashEqualsTheStreamWithItsVehiclesRemoved` in `tests/MmoEngine.SimCore.Tests/Formats/V1PartHashTests.cs` (US7 scenario 7) over the fixture's `characters-and-vehicles` stream: every step's record carries two hashes, and the `v1PartHash` of each step equals the full hash of the same characters' commands run as a version 1 stream with no vehicles. Red: the fixture stream `characters-and-vehicles` is recorded in Phase 3; the test does not compile until `V1PartHashTests` exists.

### Implementation for User Story 7

- [ ] T127 [US7] Add the four valid and 22 refusal version 2 cases to `src/MmoEngine.SimCore.Server/Golden/` and `src/MmoEngine.SimCore.Cli/Commands/GoldenCommand.cs` (015's shared fixture rules, mutations `write`, `truncate`, `append`, `fixChecksum`), change 015's `unsupported-version` case to write version 3 (`write 4 "0300"`) and `src/MmoEngine.SimCore.Cli/Agreement/RefusalFixtures.cs` to match, and write `simcore/golden/stream/v2-*.{json,bin}` with `dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden`. Close any decode or load check that a row of T121, T124 or T125 finds missing in `RecordedStream.cs`, `StateSerializer.cs` or `SimWorld.cs`. Makes T120, T121, T122, T124 and T125 pass.
- [ ] T128 [P] [US7] Write `simcore/baseline/015-65112ab.sha256` once from `git show 65112ab:<path> | sha256sum` for each of the 19 `simcore/streams/*.mmrs` and `simcore/content/test-world.mmcd`, in path order, two spaces between hash and path; no command writes it afterwards. Makes T123 pass.
- [ ] T129 [US7] Make `V1PartHashTests` pass against the fixture's committed `characters-and-vehicles` stream (the version 1 part serialised exactly as version 1, research R6). Makes T126 pass.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~Formats|FullyQualifiedName~VehicleCanonicalisation|FullyQualifiedName~NotComparableTests|FullyQualifiedName~AgreementCommandTests"` passes, and after `golden write`, `git diff --exit-code simcore/golden` is clean.

---

## Phase 10: Polish and PR, slice 1

**Goal**: Close slice 1: every local gate green, the committed content reproduces, the spec artifacts
match the work, the spec-test review has run, the patch-notes entry is in, and the draft PR from
`016-vehicle-physics-selection` is marked ready once `.routines/merge-policy.md` rules 1-4 and 6
hold. Rule 5 does not apply (no guarded path), so `auto-merge.yml` squash-merges it.

**Independent Test**: with `COV="$(mktemp -d)"`, each exits 0: `dotnet build MmoEngine.sln -warnaserror`,
`dotnet format MmoEngine.sln --verify-no-changes`,
`dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory "$COV"` and
`python3 tools/coverage_gate.py --base origin/main --coverage-dir "$COV"`; after
`vehicles content generate`, `golden write`, `streams verify`, `streams verify --engine fixture` and
`streams verify --engine bepu`, `git diff --exit-code simcore/` is clean; the guarded-path grep of
T136 exits 1.

### Tests (write first, confirm red)

- [ ] T130 Write failing test `QuickstartTests.EverySliceOneQuickstartCommandParses` in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/QuickstartTests.cs`: it reads `specs/016-vehicle-physics-selection/quickstart.md` read-only and collects every `simcore` command line in the sections `Check the configurations`, `Reproduce the vehicle test world`, `Rewrite the golden fixtures`, `Verify the committed streams`, `Run the behavior checks`, `Regenerate an engine's streams` and the `vehicles license classify` and decision-fixture lines; asserts the exact line count; each parses against `Program.CreateRootCommand()` with no errors and resolves to its named command; every path in those sections exists; the documented refusal (`streams record --engine fixture --all` without `--reason`) exits 2 and writes nothing into a temp `--out`. Red: the vehicles commands are not all registered until every phase above is done; run `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~QuickstartTests"`.

### Implementation

- [ ] T131 Make T130 pass: correct whichever side differs from contracts/vehicles-cli.md, the command definition in `src/MmoEngine.SimCore.Cli/Commands/` or the line in `specs/016-vehicle-physics-selection/quickstart.md`.
- [ ] T132 [P] Add a `vehicles/` and `baseline/` section to `simcore/README.md`: each folder, what it holds and the only command that writes it (`streams record --engine <e> --reason` for `streams/<e>/`, `vehicles content generate`, `golden write`, the agent's commit of `selection/` from the `vehicle-selection` artifact; `baseline/` written once).
- [ ] T133 Run the Independent Test commands (`dotnet build`, `dotnet format`, `dotnet test` with coverage, `tools/coverage_gate.py`, the `simcore` reproductions) and record each command, exit code and summary line (with the coverage gate's table) in a PR body file in the session's scratchpad directory.
- [ ] T134 Top up coverage from T133's gate table: every changed `src/MmoEngine.SimCore*/**/*.cs` below 95.00% or not in a report gets Theories over its uncovered branches in the test folder that owns it, with exact asserted values; never `[ExcludeFromCodeCoverage]`, a skip or a run-settings exclusion. Rerun until `tools/coverage_gate.py` exits 0.
- [ ] T135 Audit the slice-1 tests with commands, fixing every hit: `grep -rnE "Skip *=|Assert\.Skip|SkipUnless|SkipWhen" tests/MmoEngine.SimCore.Vehicles.Tests tests/MmoEngine.SimCore.Tests tests/MmoEngine.SimCore.Analyzers.Tests --include=*.cs` lists only 015's `ExhaustiveSweepTests.cs`; `grep -rn "Path.GetTempPath" tests/MmoEngine.SimCore.Vehicles.Tests` lists nothing outside the linked `TempDirectories.cs`; every CsCheck `Sample` passes a `const` seed; the only `DisableParallelization = true` collections are the zero-allocation ones, each with its reason; running `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests` twice in a row passes both times and leaves `git status --porcelain simcore/ specs/` empty.
- [ ] T136 Check scope and licenses: `git diff --name-only --no-renames origin/main...HEAD | grep -E '^\.github/|^\.routines/|^\.claude/|^\.specify/memory/constitution\.md$|(^|/)CLAUDE\.md$|^docs/architecture/server-architecture\.md$|^infra/'` prints nothing and exits 1; `git diff origin/main...HEAD -- Directory.Packages.props` adds exactly the three `PackageVersion` lines of T002, MIT or Apache-2.0, matching research.md R1 and R16; no `package.json` changes; record the changed-file count.
- [ ] T137 Bring the spec artifacts in line with the work: in `specs/016-vehicle-physics-selection/spec.md`, record every behavior, message or name settled differently from the text; set `**Status**` to `Slice 1 implemented; slice 2 (Jolt, browser builds, CI jobs, decision) pending`; apply plan.md's "Deviations from the spec's text" (the Bepu 2.5.0-beta.33 prerelease sentence in "Engine releases", the linker startup objects and header-instantiated runtime-library code sentence in "License scan boundary", the Rapier static-linking sentence in "Browser integration of Jolt and Rapier"); add `## Planned for slice 2: not yet built, no test on main` after Success Criteria, opening with the statement that these items are planned, not current, behavior, and move into it (keeping their IDs): US1 scenario 14 and the Jolt half of the US1 Independent Test; US2 scenarios 1, 2 and 9, the browser halves of US2 scenarios 3, 4, 5, 6 ("every required build still reports Match") and 8, and the browser half of the US2 Independent Test; US3 scenario 9 and the committed-report half of the US3 Independent Test; US4 scenarios 1, 2 and 3 and the CI half of the US4 Independent Test; US5 scenario 1 and the real-build half of the US5 Independent Test; US6 scenario 1; FR-018's browser runs, FR-021's 15-minute bound, FR-023, FR-024, FR-025's CI job, FR-029, FR-032, FR-034's native path; SC-006, SC-007 and SC-008. Update `specs/015-shared-simulation-core/spec.md` where it names only version 1 (FR-016, FR-020, the unknown-version examples), noting that 016 prevails for vehicle state and version 2. Check off every Phase 1-9 task in this file and correct any renamed test in [Scenario coverage](#scenario-coverage). Index check: every slice-1 `Class.Method` in that table appears in `dotnet test <project> --list-tests` (one `grep -F` per name, each exits 0).
- [ ] T138 Put one line at the top of each slice-2 section of `specs/016-vehicle-physics-selection/quickstart.md` (`Build Jolt`, `Run the lock test`, `Run the agreement suite for one candidate and mode`, `Run the selection locally`, `Decide from the committed report`'s committed-record half, the `fitcheck` half of `Check Rapier's fit and a license input`, `Benchmarks`, `Workflow tests`): "Planned for slice 2, not on main yet."
- [ ] T139 Commit on `016-vehicle-physics-selection` (messages end with the session's `Co-Authored-By:` and `Claude-Session:` lines), push, and open the draft PR with `gh pr create --draft --base main --head 016-vehicle-physics-selection --title "016 vehicle physics selection, slice 1: formats, vehicle model, fixture engine, Bepu server and selection machinery" --body-file <scratchpad file>`. The body holds T133's commands and results, the judgment calls, "No open decision is settled" (decisions 3, 4, 5 and 9 untouched; gate 9's build mode and parallel phases open), the slice-2 follow-up as the contents of spec.md's planned section, and the attribution lines last.
- [ ] T140 Add the `docs/patch-notes.md` entry at the top, below `<!-- Add the newest entry below this line. -->`: `## <date> — Vehicle physics selection, slice 1: vehicle formats, model, fixture engine and selection machinery`, `Spec: [specs/016-vehicle-physics-selection/spec.md](../specs/016-vehicle-physics-selection/spec.md) · PR: #<n>`; **Behavior and contract changes** (canonical state, stream and step record version 2; `VehicleRulesRevision`; `FormatErrorCode` 11 and 12; 015's `unsupported-version` case now version 3; the `--engine` options and `vehicles` commands; the analyzer's per-assembly exemption; `simcore/vehicles/` and the baseline manifest); **Shared pieces to reuse** (`IVehicleEngine`, `EngineCatalog`, `VehicleChecks`, `StatusTable`, `DivergenceMeasurement`, `LicenseClassifier`, `DecisionRule`); **Open follow-ups** (slice 2; plan.md follow-ups 1 to 7, including the Principle VII platform-runtime reading, the foldhash amendment candidate and `DOTNET_EnableAVX512F=0` being a no-op on .NET 10). Commit and push.
- [ ] T141 Run the spec-test review in a fresh subagent given only the PR link: it applies CLAUDE.md's "Code review: every natural-language test MUST map to a real, faithful automated test" rules to `specs/016-vehicle-physics-selection/spec.md` through this file's [Scenario coverage](#scenario-coverage), excluding the items in spec.md's planned section. Fix each finding in the test it names so it asserts the scenario's exact Then (never weaken an assertion or skip a test), rerun that project and the coverage gate, push, and answer each finding on the PR.
- [ ] T142 Audit for human steps (Principle XIII): `grep -n -i -E "manual|by hand|sign-off|sign off|approv|QA|stop and validate|reviewer must|ask the (owner|maintainer)"` over this file, `spec.md`, `quickstart.md` and the PR body file; every hit describes system behavior or an automated check, else reword it. Confirm the final description with `gh pr view <n> --json body`.
- [ ] T143 Mark the PR ready and see it merged under `.routines/merge-policy.md` (depends on every task above): wait for every check on the head commit with `gh pr checks <n> --watch`; confirm no unresolved thread or change request; `gh pr ready <n>`; read the `auto-merge` run's summary. If it squash-merged, done. If it reports the over-300-files case, re-check rules 1-4 and 6 and squash-merge with `gh pr merge <n> --squash`, saying why on the PR. Any other reason: fix the cause and repeat. Never add `hold`, skip a test or merge with a failing check.

**Checkpoint**: the Independent Test commands exit 0, `QuickstartTests.EverySliceOneQuickstartCommandParses` passes, the review's findings outside the planned section are fixed, and the PR is merged into `main`.

---

# Slice 2: Jolt, the browser builds, the CI jobs and the decision

Branch `016-vehicle-physics-selection-slice-2`, cut from `main` after slice 1 merges (the slice-1
branch is closed once merged). It touches `.github/workflows/simcore.yml`, a guarded path.

## Phase 11: Setup (slice 2)

**Purpose**: The slice-2 branch, projects and toolchain pins.

**Independent Test**: `dotnet build MmoEngine.sln -warnaserror` exits 0 with the Jolt adapter, the
three hosts, the Wasm test project and the benchmarks in the solution, and
`dotnet restore tools/vehicles/emsdk/emsdk.proj` restores the three pinned packs.

- [ ] T144 Create `016-vehicle-physics-selection-slice-2` from an up-to-date `main` (`git checkout main && git pull origin main && git checkout -b 016-vehicle-physics-selection-slice-2`).
- [ ] T145 [P] Create `src/MmoEngine.SimCore.Vehicles.Jolt/MmoEngine.SimCore.Vehicles.Jolt.csproj` (analyzer, `<MmoDeterminismExemption>MMO1008:interop</MmoDeterminismExemption>`, `AllowUnsafeBlocks`), the three hosts `src/MmoEngine.SimCore.Vehicles.{Fixture,Bepu,Jolt}.Wasm/` as `Microsoft.NET.Sdk.WebAssembly` projects with 015's settings (`Exe`, `AllowUnsafeBlocks`, `InvariantGlobalization`, `Program.Main` returning 0; Jolt's copies `wwwroot/engine/` from `$MMO_VEHICLE_NATIVE_DIR/jolt/browser/` through an MSBuild item, a missing file failing the publish), `tests/MmoEngine.SimCore.Vehicles.Wasm.Tests/` and `benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks/`, and add them to `MmoEngine.sln`; create `tests/MmoEngine.SimCore.Vehicles.Native.Tests/MmoEngine.SimCore.Vehicles.Native.Tests.csproj` outside the solution.
- [ ] T146 [P] Create `tools/vehicles/emsdk/emsdk.proj` (`PackageDownload` of `Microsoft.NET.Runtime.Emscripten.3.1.56.{Sdk,Cache,Node}.linux-x64` 10.0.15) and commit `simcore/vehicles/engines/jolt.json` per contracts/engine-adapter.md#engine-identity, with the three `sha512` values from the restore's `.nupkg.sha512` files, Jolt commit `cf16769bb0ede9c45d57d06068ff0689e9702f5c` and tree `47f0b9a2d9a830c64c3a729b372e0c951a4c1daa`, `nativeIdentity` `jolt v5.7.0 cpd=1 blue=1 fpcontract=off obj16 emmalloc`.

**Checkpoint**: the Independent Test commands exit 0.

---

## Phase 12: User Story 1 - Every candidate drives the same vehicle scenarios (Priority: P1), slice 2: Jolt's adapter, shim, lock counter and native tests

**Goal**: Jolt's adapter over the byte ABI, its C++ shim over pinned Jolt v5.7.0, the server and
browser-module builds, the lock counter, the native and shim tests, and Jolt's streams.

**Independent Test**: `src/MmoEngine.SimCore.Vehicles.Jolt/native/build.sh --out "$NATIVE"` writes
`outcomes.json` with `server`, `browser-module` and `evidence-threaded`; `ctest` in
`src/MmoEngine.SimCore.Vehicles.Jolt/native/tests/` passes with LCOV written;
`MMO_VEHICLE_NATIVE_DIR="$NATIVE" dotnet test tests/MmoEngine.SimCore.Vehicles.Native.Tests` passes.

### Tests for User Story 1, slice 2 (write first, confirm red)

- [ ] T147 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Jolt/JoltEngineTests.cs` driving `JoltEngine` through a fake `IVehicleEngineCalls` (research R28): `LoadPayloadMatchesTheAbi`, `BodiesCrossAsFiftySixByteRecordsWithRawBits`, Theory `NegativeReturnsMapToEngineResults`, `AbiVersionOrIdentityMismatchIsLoadRefused` (against the pin file's `nativeIdentity`), `TheBindingFactoryChoosesTheBrowserBindingOnlyInTheBrowser`; add the Jolt row to `AdapterBackstopTests.EachAdapterHasNoDiagnosticOutsideItsExemption` and to `WiringTests`. Red: does not compile.
- [ ] T148 [P] [US1] Write failing `ctest` cases in `src/MmoEngine.SimCore.Vehicles.Jolt/native/tests/shim_tests.cpp`: `abi_version_is_1`; `identity_string_matches_the_build`; `load_refuses_malformed_and_oversized_payloads` (−1, −2) and an engine refusal (−3); `begin_before_load_is_minus_1`; `step_with_ids_differing_from_begin_is_minus_2`; `step_with_a_short_out_buffer_is_minus_3`; `exact_triangle_shape_collides_with_exactly_the_given_triangles` (degenerate dropped, heightfield winding up); `body_ids_are_a_function_of_the_vehicle_id`; `history_free_substeps_give_the_same_bodies_after_a_rewrite` (writing the read-back state into the bodies and stepping equals stepping on); `arena_has_zero_system_allocator_fallbacks_over_1000_steps` (research R17); `platform_header_takes_no_lock` (the no-op lock header's mutex type is empty). Red: `ctest` fails to configure because the shim sources do not exist.
- [ ] T149 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Native.Tests/LockCounterTests.cs` (US1 scenario 12, FR-034): `TheCounterCountsOneMutexPerStepOnTheTestLibrary` (`liblocktest.so` under `LD_PRELOAD=liblockcount.so`: exactly 1,000 counted locks over 1,000 steps after warm-up, the check Failed `TakesLock` naming `pthread_mutex_lock`); `OnlyCallsFromTheEngineLibraryOnTheSteppingThreadCount` (a lock from another library and another thread is not counted); `FutexSyscallsAreCounted`; `JoltLockcountRecordsAVerdictAndItsLinkedLockSymbols` (`simcore vehicles lockcount --engine jolt` under the interposer: the output's count matches the exit code, and the linked lock symbols are listed from `nm -D --undefined-only`); `WithoutTheInterposerLockcountExitsTwo`. In `tests/MmoEngine.SimCore.Vehicles.Native.Tests/JoltNativeTests.cs`: `RealJoltStepsTheFixtureScenariosThroughPInvoke` (the fixture's `flat-accel-30hz` commands through Jolt: records written, no fault), `EmscriptenTraceUsesEmmallocAndNoDlmalloc` (the trace has `libemmalloc.a(emmalloc.o)` and no `libdlmalloc.a` member), `RestingOffsetCalibration` (a box at rest on flat ground settles within `contactTolerance`), and `JoltServerChecksRecordOneVerdictPerCheck` (US1 Independent Test: one verdict per scenario 1 to 11 with measured value and bound, not asserted). Red: the project does not build.

### Implementation for User Story 1, slice 2

- [ ] T150 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Jolt/{JoltEngine,JoltEngineFactory,JoltCalls,JoltNative,JoltJs}.cs` (contracts/engine-adapter.md: `[LibraryImport("libmmo_vehicles_jolt")]`, `SetLastError = false`, `NativeLibrary.SetDllImportResolver` from `MMO_VEHICLE_NATIVE_DIR`; `[JSImport("step", "mmo-vehicle-engine")]` with `JSType.MemoryView` spans; `JoltCalls.Create()` by `OperatingSystem.IsBrowser()`), sharing the marshalling shape of `RapierEngine`, and the `jolt` entry of `EngineCatalog`. Makes T147 pass.
- [ ] T151 [US1] Implement `src/MmoEngine.SimCore.Vehicles.Jolt/native/{CMakeLists.txt,build.sh,exports.map,src/shim.cpp,src/ExactTriangleGridShape.cpp,src/Arena.cpp,include/Jolt/Core/PlatformBlue.h,glue/jolt-glue.mjs,tests/CMakeLists.txt}`: fetch Jolt by commit and check the tree hash, compile only `Jolt/`, `-DCROSS_PLATFORM_DETERMINISTIC=ON -DCMAKE_BUILD_TYPE=Distribution -DOBJECT_LAYER_BITS=16`, `JPH_PLATFORM_BLUE` with the no-op lock header, `-ffp-contract=off -fno-exceptions -fno-rtti`, the engine settings of the pin file (`mConstraintWarmStart` false, `mUseBodyPairContactCache` false, `mDeterministicSimulation` true, sleeping off, `LinearCast`, chassis convex radius 0, `CreateBodyWithID(BodyID(id))`, writes through `GetBodyInterfaceNoLock()`), the server `.so` with the version script (`nm -D --undefined-only` shows no pthread lock function and no `pthread_create`), the browser module with Emscripten 3.1.56 `-O3 -msimd128 -sMALLOC=emmalloc -sALLOW_MEMORY_GROWTH=0 -Wl,--trace`, the evidence-only threaded build (BLUE off, `JobSystemThreadPool`), each build under `timeout --kill-after=30s 15m` writing `outcomes.json` (`Built`, `BuildFailed`, `BuildTimedOut` with the failing step and the last 50 lines) and the script exiting 0; clang source-based coverage for `ctest`. The glue's `instantiate(baseUrl, expectedSha256)` checks the SHA-256 before instantiating, uses fixed 8 MiB and 1 MiB areas and copies through `copyTo` and `set`. Makes T148 pass.
- [ ] T152 [US1] Implement `tools/vehicles/lockcount/{lockcount.c,locktestlib.c,build.sh}` (research R16: interposed pthread mutex, rwlock, condition-variable and semaphore functions, `syscall()` counting `SYS_futex`, and the glibc allocator entry points as evidence; counted only when `dladdr(__builtin_return_address(0))` lies in the engine library, on the stepping thread, between the two exported marks) and the native path of `vehicles lockcount`. Makes T149 pass (with T151).
- [ ] T153 [US1] Record Jolt's 14 streams with `MMO_VEHICLE_NATIVE_DIR="$NATIVE" dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --engine jolt --all --reason "Initial Jolt v5.7.0 streams (016)"` into `simcore/vehicles/streams/jolt/`.

**Checkpoint**: the phase's Independent Test commands pass and `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~Jolt"` passes.

---

## Phase 13: User Story 2 - Each candidate's builds agree, or the first divergence is named (Priority: P1), slice 2: browser hosts, runners, collectors and the fixture self-check

**Goal**: The three browser hosts, the runner changes in `tools/agreement/`, the windowed browser
runs, the fixture pair through the real harness, and the cargo, source, Emscripten and OS-toolchain
license collectors on real builds.

**Independent Test**: in `tools/agreement`, `node --test node/runtime.test.mjs node/server.test.mjs`
passes; the quickstart's agreement leg for Jolt in `interp-prebuilt` (publish, prepare, both Node
runs, the Chromium Worker run, `agreement compare --require node-interp,node-interp-nojit,chromium-worker`)
exits 0 with three `match` lines; the same leg for `fixture-perturbed` exits 1 naming step 15 and
`vehicles[0].position.x` for every required build.

### Tests for User Story 2, slice 2 (write first, confirm red)

- [ ] T154 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Wasm.Tests/VehicleExportsTests.cs` (each host's exports called as plain methods, with the fixture and Bepu factories): Theory `EachExportDelegatesToBatchStepperOrStreamRunner` (`LoadCollision`, `LoadConfig`, `LoadVehicleConfig`, `Reset`, `CommandBuffer`, `ReportBuffer`, `StepQueued`, `RunStream`, `RunWindow` equal the direct calls byte for byte); `EngineIdentityIsEngineReleaseLengthReleaseAndFingerprint`; `StepQueuedDrivesTheFirstVehicle` (US2 scenario 5). Red: does not compile.
- [ ] T155 [P] [US2] Write failing `node:test` cases in `tools/agreement/node/runtime.test.mjs` and `tools/agreement/node/server.test.mjs`: `'bootRuntime keeps 015 defaults without options'`; `'bootRuntime with engineGlue registers mmo-vehicle-engine before returning'`; `'readRecords reads layout 2 records'`; `'the glue refuses a module whose SHA-256 differs'`; `'GET /engine/* serves the host engine files'`; `'WORKER_BUILDS adds chromium-worker-relinked and chromium-worker-aot'`; `'PUT accepts <stream>.windows and <stream>.windows.error.txt'`. Red: `node --test tools/agreement/node/runtime.test.mjs tools/agreement/node/server.test.mjs` fails.
- [ ] T156 [P] [US2] Write failing `node:test` cases in `tools/agreement/node/agreement.test.mjs` for a manifest version 2 (US2 scenarios 1, 2, 3, 4 and 5): `test('<stream>')` per committed and generated stream, comparing the build's layout-2 records byte for byte with `server/<stream>.records`; `'batch inputs through StepQueued equal RunStream'`; `'perturbation diverges at exactly its step'` (step 15, `vehicles[0].position.x`, earlier records equal); `'windowed runs restart at every window start from the server state'` (each `<build>/<stream>.windows` equals the server's windows); `'injected fault perturbs only the named stream'` (`fixture-perturbed`: `flat-accel-30hz` differs at step 15 and its windows still match); `'a build reporting another engine identity writes <stream>.error.txt'`; `NODE_BUILDS` includes `node-interp-relinked-nojit`, with the jiterpreter state asserted from the runtime's own report. Red: the tests fail with no vehicle host.
- [ ] T157 [P] [US2] Write failing Playwright cases in `tools/agreement/specs/worker.spec.mjs` (US2 scenarios 1, 4 and 5 in a dedicated Worker): with a manifest version 2, `'every vehicle stream, batch and window matches the server in a dedicated Worker'`, run as `chromium-worker`, `chromium-worker-relinked` or `chromium-worker-aot` from `AGREEMENT_MODE` (`interp-prebuilt` when unset), comparing every record and window file the Worker wrote with the server's; `'the Worker never sets self.onmessage before importing dotnet.js'`. Red: the spec fails with no vehicle host.
- [ ] T158 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/License/CollectorTests.cs` (FR-031): `CargoCollectorDropsProcMacroSubgraphs` (a committed path-only Cargo workspace under `simcore/vehicles/fixtures/license/cargo/` with a `GPL-3.0-only` path dependency, run offline with an empty `CARGO_HOME`: the GPL crate is in the inventory and classifies `LicenseNotAllowed`); `SourceCollectorRequiresAnMitSpdxHeaderOnEveryJoltFile` (committed depfile excerpts); `EmscriptenCollectorMapsTraceMembersThroughTheTable` (a committed `--trace` excerpt and a small table: an unmapped member and a table whose version differs from the pinned Emscripten fail closed with `NoLicense`); `OsToolchainCollectorMapsNeededEntriesAndLinkInputsToBoundaryPackages`. In `tests/workflows/test_gen_emscripten_table.py`: `test_table_is_generated_from_system_libs` on a committed excerpt of `tools/system_libs.py`. Red: does not compile, and the Python test fails with no generator.

- [ ] T159 [P] [US2] Write failing tests in `tests/workflows/test_publish_host.py` (US2 scenario 8, FR-021): `test_a_failing_publish_is_a_recorded_outcome` (a stub `dotnet` on `PATH` that exits 1: the script exits 0 and `outcome.json` holds `BuildFailed`, the step `dotnet publish` and exactly the last 50 lines of its output); `test_a_publish_past_its_bound_is_build_timed_out` (a stub that sleeps, with the bound lowered through `MMO_PUBLISH_TIMEOUT` for the test); `test_prebuilt_publish_disables_the_workload`. Red: the script does not exist.
- [ ] T160 [P] [US2] Write failing Theory `CheckFixturesCommandTests.TheFixturePairPassesAndAnyOtherEndingFails` in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/CheckFixturesCommandTests.cs` (US2 scenario 9) over committed fixture reports in `simcore/vehicles/fixtures/check-fixtures/`: `pair-as-expected` (exit 0); `perturbed-qualified`, `perturbed-diverged-at-step-14`, `fixture-diverged` and `decision-not-fixture` (exit 1, naming the fixture and what it ended as). Red: does not compile.

### Implementation for User Story 2, slice 2

- [ ] T161 [US2] Implement `src/MmoEngine.SimCore.Vehicles.{Fixture,Bepu,Jolt}.Wasm/{Program,VehicleExports}.cs` (one-line delegations to `new BatchStepper(32, factory)` and `StreamRunner`; `RunStream(byte[] stream, byte[] collision, int perturbStep)`, `RunWindow(byte[] stream, byte[] collision, byte[] startState, int startStep, int count)`, `EngineIdentity()`). 015's `src/MmoEngine.SimCore.Wasm` is unchanged. Makes T154 pass.
- [ ] T162 [US2] Make the additive runner changes of contracts/engine-adapter.md#runners in `tools/agreement/lib/runtime.mjs`, `tools/agreement/lib/server.mjs`, `tools/agreement/node/agreement.test.mjs`, `tools/agreement/specs/worker.spec.mjs` and `tools/agreement/web/worker.mjs` (no new npm package), and replace `SelectionRun`'s in-process `IAgreementRunner` in production with `NodeAndPlaywrightRunner` (`node --test tools/agreement/node/agreement.test.mjs` and `npx playwright test --project chromium` with `AGREEMENT_APP`, `AGREEMENT_WORK`, `AGREEMENT_BUILD`, `AGREEMENT_MODE` per build; the in-process runner stays for the tests) in `src/MmoEngine.SimCore.Vehicles.Selection/Orchestration/NodeAndPlaywrightRunner.cs`. Makes T155, T156 and T157 pass (depends on T161).
- [ ] T163 [US2] Implement `src/MmoEngine.SimCore.Vehicles.Selection/License/{CargoCollector,RustSysrootCollector,SourceCollector,EmscriptenCollector,OsToolchainCollector}.cs` and `tools/vehicles/license/gen_emscripten_table.py`, and commit `simcore/vehicles/license/emscripten-3.1.56.json` generated from the pinned packs; add the collectors to `vehicles license scan`. Makes T158 pass.
- [ ] T164 [US2] Implement `tools/vehicles/ci/publish-host.sh <Core|Fixture|Bepu|Jolt> <mode> <dir>` (each publish under `timeout 15m`, writing `<host>/<mode>/outcome.json` with `Built`, `BuildFailed` or `BuildTimedOut`, the failing step and the last 50 lines; `interp-prebuilt` with `-p:UsingBrowserRuntimeWorkload=false`; AOT with its own `-p:BaseIntermediateOutputPath`), executable. Makes T159 pass.
- [ ] T165 [US2] Implement `vehicles select check-fixtures` in `VehiclesCommand.cs` (`fixture` Qualified and `fixture-perturbed` Diverged `RecordDiverged`, step 15, `vehicles[0].position.x`, in every mode, and `decide --fixtures` giving `fixture` with BitExact; exit 1 naming the fixture that ended otherwise). Makes T160 pass.

**Checkpoint**: the phase's Independent Test commands pass, and `dotnet test tests/MmoEngine.SimCore.Vehicles.Wasm.Tests` and `python3 -m unittest tests/workflows/test_gen_emscripten_table.py tests/workflows/test_publish_host.py` and `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~CheckFixturesCommandTests"` pass.

---

## Phase 14: User Story 4 - Each candidate's cost and download size are measured (Priority: P2), slice 2: benchmarks and browser measurements

**Goal**: Server benchmarks at 1, 16, 64 and 256 vehicles per engine, browser step costs, cold starts
and download sizes per mode, and Jolt's zero-allocation test.

**Independent Test**: `dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks -- --engine fixture --filter '*'`
with `MMO_RUNNER_CLASS=local-dev` exits 0 and writes its export labelled `measurement`;
`node --test tools/agreement/node/measure-vehicles.test.mjs` passes.

### Tests for User Story 4, slice 2 (write first, confirm red)

- [ ] T166 [P] [US4] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Benchmarks/VehicleBenchmarkDryRunTests.cs` (US4 scenario 1, FR-023): `EveryEngineAndVehicleCountIsACase` (cases for 1, 16, 64 and 256 vehicles; the arena fits 256); `TheExportHasMeanAndP99OfWorldStepAndVehicleStepLabelledMeasurement` (a dry run on the fixture: `label` `measurement`, the runner class, `vehicleStep` = world step / n); `BepuRunRecordsTheVectorPin`; `MissingRunnerClassExitsTwo`. Red: does not compile.
- [ ] T167 [P] [US4] Write failing `node:test` cases in `tools/agreement/node/measure-vehicles.test.mjs` (US4 scenarios 2 and 3, FR-024): `'step cost is 3008 timed steps after 512 warm-up in 16-command batches'`; `'worlds of 1, 16 and 64 vehicles drive vehicle 0 and park the others'`; `'each cold start is checked against the server first record of flat-accel-30hz'` (10 starts); `'download added is the host set plus engine files minus the core download in the same mode, raw, brotli and gzip'`; `'Jolt step cost includes the crossing and its download includes the module and glue'`; `'an unproduced mode is produced false with its reason'`. Red: the module does not exist.
- [ ] T168 [P] [US4] Add the `jolt` row to `VehicleStepAllocationTests.EachCandidatesVehicleStepAllocatesNothingAfterWarmUp` in `tests/MmoEngine.SimCore.Vehicles.Native.Tests/JoltAllocationTests.cs` (US4 scenario 4, SC-005: ten windows of 1,000 steps, each 0 managed bytes, through P/Invoke). Red: the test fails to build.

### Implementation for User Story 4, slice 2

- [ ] T169 [US4] Implement `benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks/{Program,VehicleBenchmarks,VehicleBenchmarkConfig,VehicleMeasurementExporter}.cs` (BenchmarkDotNet 0.15.8, 015's exporter shape, one process per engine with `--engine`, world steps in the arena with seeded commands). Makes T166 pass.
- [ ] T170 [US4] Implement `tools/agreement/lib/measure-vehicles.mjs` with 015's method (`lib/passes.mjs`) and add the measurement phase to `SelectionRun` (server benchmarks per candidate, `measure-vehicles.mjs` per mode). Makes T167 pass.
- [ ] T171 [US4] Make Jolt's adapter step allocation-free (world-owned crossing buffers sized at `Load`). Makes T168 pass.

**Checkpoint**: the phase's Independent Test commands pass.

---

## Phase 15: User Story 5 - Bepu's cross-build divergence is measured (Priority: P2), slice 2: windowed runs on real builds

**Goal**: The divergence measurement runs on every candidate's (and Bepu's) committed and generated
streams across every required build and mode in the selection.

**Independent Test**: the quickstart's `vehicles divergence --work "$WORK" --require node-interp,node-interp-nojit,chromium-worker`
on Jolt's `interp-prebuilt` leg exits 0 with every error 0, and on the `fixture-perturbed` leg reports
the step-15 position error.

### Tests for User Story 5, slice 2 (write first, confirm red)

- [ ] T172 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/SelectionRunDivergenceTests.cs` (US5 scenario 1): `EachRequiredBuildIsLoadedAtEveryWindowStartAndStepsTheWindow` (with the recorded runner calls: for each stream and required build of each mode, restarts at S₀, S₀ + W, … with W = ⌈0.5 s × rate⌉ (15 at 30 Hz), each loaded with the server's state at that step with events cleared, stepping W steps with the stream's commands, fewer at the end); `ThreeErrorsAreRecordedPerVehicleAndStep` (position in metres, orientation in degrees, linear velocity in m/s). In `tools/agreement/node/agreement.test.mjs`: `'Bepu windows are written for every committed and generated stream'` on a Bepu host. Red: does not compile.

### Implementation for User Story 5, slice 2

- [ ] T173 [US5] Drive the divergence phase of `SelectionRun` from the real runners' `.windows` files for every candidate, mode and required build, writing `divergence[]` for Bepu's NonExact branch. Makes T172 pass.

**Checkpoint**: the phase's Independent Test commands pass.

---

## Phase 16: User Story 6 - Rapier joins only if it fits the harness cheaply (Priority: P3), slice 2: the real fit check

**Goal**: `vehicles fitcheck --engine rapier` evaluates every criterion on the pinned release with the
cargo collector.

**Independent Test**: `dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles fitcheck --engine rapier`
prints a verdict with evidence for c, b, d, f, a and e.

### Tests for User Story 6, slice 2 (write first, confirm red)

- [ ] T174 [P] [US6] Write failing tests in `tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/FitCheckCommandTests.cs` (US6 scenario 1): `FitcheckRecordsAVerdictAndEvidenceForEveryCriterion` (`vehicles fitcheck --engine rapier --out <tmp>` over the committed manifest with a pre-fetched `CARGO_HOME` fixture: six criteria in the order c, b, d, f, a, e, each with `verdict` pass or fail, `notEvaluated` and non-empty evidence; exit 0 when all pass and 1 when any fails, consistent with the verdicts; the verdicts themselves are not asserted); `FitcheckWithoutCargoExitsTwo`. Red: does not compile.

### Implementation for User Story 6, slice 2

- [ ] T175 [US6] Implement `vehicles fitcheck` in `VehiclesCommand.cs`, criterion (c) through `CargoCollector` and `LicenseClassifier` for both targets, (a) and (e) recorded `notEvaluated` with their reason when (c) fails (research R12), and use it in `SelectionRun`'s fit-check phase in place of the fixture input. Makes T174 pass.

**Checkpoint**: the phase's Independent Test commands pass.

---

## Phase 17: User Story 3 - The engine is chosen by the stated rule and recorded as gate 9's decision (Priority: P1), slice 2: the SimCore jobs, the selection run and the committed record

**Goal**: The six `SimCore` jobs, their workflow tests, the selection run in CI, and the committed
report and decision record, with the vehicle agreement gate and the winner's checks on `main`.

**Independent Test**: `python3 -m unittest discover -s tests/workflows -v` passes; on the PR, the
`vehicle-selection` job passes and uploads `vehicle-selection`;
`DecisionRuleTests.TheCommittedRecordComesFromTheCommittedReport` passes; `vehicle-agreement-plan`,
`vehicle-agreement` and `vehicle-winner-checks` pass on the commit that adds the record.

### Tests for User Story 3, slice 2 (write first, confirm red)

- [ ] T176 [P] [US3] Write failing tests in `tests/workflows/test_simcore_workflow.py` class `VehicleTriggers` (contracts/ci-jobs.md#triggers, FR-025, FR-029): `test_adds_workflow_dispatch_and_tools_vehicles` (015's `pull_request` and `push` lists kept, `tools/vehicles/**` added to both); `test_changed_paths_outputs_for_each_fixture_diff` (runs `tools/vehicles/ci/changed-paths.sh` on fixture diffs in a temp git repository: a vehicle-code change gives `selection=true gate=true`; a core-only change `selection=false gate=true`; a record-only change under `simcore/vehicles/selection/` `selection=false gate=true`; an unrelated change both false; `workflow_dispatch` both true; a `push` to `main` `gate=true`); `test_changed_paths_table_matches_the_contract`. Red: `python3 -m unittest tests/workflows/test_simcore_workflow.py -k VehicleTriggers` fails.
- [ ] T177 [US3] Write failing tests in class `VehicleJobs` of `tests/workflows/test_simcore_workflow.py` (SC-007, FR-021, FR-029): `test_each_job_has_its_needs_if_and_timeout` (`vehicle-changes` 2, `vehicle-engines` 45, `vehicle-agreement-plan` 2, `vehicle-agreement` 15 per leg, `vehicle-winner-checks` 15, `vehicle-selection` 120); `test_every_step_has_its_own_bound`; `test_matrix_is_fail_fast_false_from_gate_plan`; `test_prebuilt_leg_installs_no_workload`; `test_selection_publishes_prebuilt_before_the_workload_and_aot_last`; `test_every_action_is_sha_pinned`; `test_bepu_vector_pin_is_set_on_bepu_child_processes_only`; `test_015_agreement_and_measure_jobs_equal_their_committed_definitions` (against `tests/workflows/fixtures/simcore-015-jobs.json`, US2 scenario 6, SC-002); `test_selection_fails_only_on_validation_fixtures_or_a_step_that_cannot_run` (no threshold compared). Red: fails.
- [ ] T178 [US3] Write failing tests in class `VehicleAgreementGate` of `tests/workflows/test_simcore_workflow.py` (US3 scenario 9): `test_bitexact_leg_compares_with_the_modes_require_set_and_fails_on_divergence`; `test_nonexact_leg_runs_streams_verify_divergence_and_v1_part_compare_and_prints_the_flag` (log and `$GITHUB_STEP_SUMMARY`); `test_none_or_no_record_writes_the_outcome_to_the_summary_and_skips_the_gate_jobs`; `test_plan_runs_check_stale_first` (a stale record fails the job); `test_winner_checks_run_checks_and_lockcount_for_the_winner` (US1 scenario 14, under `LD_PRELOAD` for a native winner). Red: fails.
- [ ] T179 [P] [US3] Write failing tests in `tests/workflows/test_coverage_gate.py` (research R28, the `vehicle-engines` job's coverage step): `test_lcov_lines_count_for_native_files` (an LCOV file with `SF:`/`DA:` records for a changed `src/MmoEngine.SimCore.Vehicles.Jolt/native/src/shim.cpp` is read with `--lcov` and gated at 95%) and `test_without_lcov_behavior_is_unchanged`. Red: `python3 -m unittest tests/workflows/test_coverage_gate.py` fails on the unknown `--lcov` option.
- [ ] T180 [P] [US3] Write failing test `DecisionRuleTests.TheCommittedRecordComesFromTheCommittedReport` in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/DecisionRuleTests.cs` (US3 Independent Test, SC-003, SC-008): `vehicles decide` on `simcore/vehicles/selection/report.json` gives `simcore/vehicles/selection/decision.json` byte for byte, the report passes `validate-report`, and `vehicles decision check-stale` on the repository exits 0 (`decision record: current`). Red: the files do not exist.

### Implementation for User Story 3, slice 2

- [ ] T181 [US3] Implement `tools/vehicles/ci/changed-paths.sh` (prints `selection=true|false` and `gate=true|false` from `git diff --name-only <base>...HEAD` and the contract's two lists, with the event rules), executable. Makes the script half of T176 pass.
- [ ] T182 [US3] Add `workflow_dispatch`, `tools/vehicles/**` and the six jobs of contracts/ci-jobs.md to `.github/workflows/simcore.yml`, leaving `agreement` and `measure` byte-identical, and commit `tests/workflows/fixtures/simcore-015-jobs.json` from the current definitions of those two jobs: `vehicle-changes`; `vehicle-engines` (emsdk restore and `.nupkg.sha512` check, identity and cache key `vehicle-engine-jolt-<fingerprint>-<sha256 of the shim's sources>`, `build.sh`, lockcount build, `ctest` with coverage and `python3 tools/coverage_gate.py --base <base> --coverage-dir coverage --lcov coverage/jolt.lcov`, `dotnet format` and `dotnet test` of the native tests, the `vehicle-engines` artifact); `vehicle-agreement-plan`; `vehicle-agreement`; `vehicle-winner-checks`; `vehicle-selection` (publishes through `publish-host.sh` in the stated order, `npm ci`, `npx playwright install --with-deps chromium`, `vehicles select run` under `timeout 100m`, `validate-report`, `check-fixtures`, `decide`, the job summary, the artifact uploaded also after a failed step). Every action SHA-pinned with a version comment; `contents: read`. Makes T176, T177 and T178 pass (depends on T181, T164, T165).
- [ ] T183 [P] [US3] Add LCOV input to `tools/coverage_gate.py` (`--lcov <file>`, merged with the Cobertura reports for the changed files it covers; behavior without it unchanged). Makes T179 pass.
- [ ] T184 [US3] Push the branch, let the PR's `vehicle-selection` job run, download its `vehicle-selection` artifact with `gh run download`, copy `report.json` to `simcore/vehicles/selection/report.json`, run `dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles decide --report simcore/vehicles/selection/report.json --out simcore/vehicles/selection/decision.json`, and commit both in the same PR (the commit touches only the record's folder, so it starts `vehicle-agreement` and its staleness check but not another selection run). Makes T180 pass, and with Phase 18, T185. If the record is NonExact, the patch-notes entry names sub-spec 6's dependency on the provisional flag (plan.md, Spec independence).

**Checkpoint**: the phase's Independent Test commands pass on the PR's head commit.

---

## Phase 18: Committed-report proofs for User Stories 1, 2, 4 and 5 (slice 2)

**Goal**: Prove, on the report and record that Phase 17 commits, the scenarios whose evidence is the
real selection run: the winner's checks (US1 scenario 14), the fixture pair through the real harness
(US2 scenario 9 and the US2 Independent Test), every measurement (US4 Independent Test) and Bepu's
divergence results (US5 Independent Test).

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Vehicles.Tests --filter "FullyQualifiedName~CommittedReportFixtureTests|FullyQualifiedName~WinnerChecksTests"` passes.

### Tests (write first, confirm red)

- [ ] T185 [P] [US1] Write failing test `WinnerChecksTests.TheRecordsWinnerPassesEveryCheckOrTheRecordNamesNone` in `tests/MmoEngine.SimCore.Vehicles.Tests/Decision/WinnerChecksTests.cs` (US1 scenario 14) reading the committed record: for each mode with a winner, that engine's `vehicles checks` and `vehicles lockcount` results in the committed report are `Pass` for every scenario 1 to 12; with outcome None in every mode, `outcomes[].winner` is null in every mode and `candidates[]` gives each candidate's failing checks (US3 scenario 5). For a native winner the live run is the `vehicle-winner-checks` job (T178); for a managed winner the test also runs `vehicles checks --engine <winner>` in a child process and requires exit 0. Red: the record does not exist.
- [ ] T186 [P] [US2] Write failing test `CommittedReportFixtureTests.TheFixturePairEndedQualifiedAndDivergedInEveryMode` in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/CommittedReportFixtureTests.cs` (US2 scenario 9, US2 Independent Test): in `simcore/vehicles/selection/report.json`, `fixtures[fixture]` is Qualified in all three modes and `fixtures[fixture-perturbed]` is Diverged with `RecordDiverged` `{build, stream: "flat-accel-30hz", step: 15, fields: ["vehicles[0].position.x"]}` for every required build of every mode; `decide --fixtures` on the report gives `fixture` with BitExact in every mode and lists `fixture-perturbed` with its status and reason. Add `CommittedReportFixtureTests.EveryCandidateHasOneStatusPerModeAndOneResultPerRequiredBuild` (US2 Independent Test, SC-001). Red: the report does not exist.
- [ ] T187 [US4] Write failing test `CommittedReportFixtureTests.EveryBuiltCandidateHasEveryMeasurement` in `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/CommittedReportFixtureTests.cs` (US4 Independent Test, SC-006): every built candidate has server costs at 4 vehicle counts, browser costs at 3 vehicle counts in 2 runtimes per mode, startup over 10 cold starts and the download bytes added, each with the runner class `gha-ubuntu-latest-x64`, the build mode and the engine identity, or `produced: false` with a reason. Red: the report does not exist until T184.
- [ ] T188 [US5] Write failing test `CommittedReportFixtureTests.BepuHasDivergenceResultsForEveryStreamBuildAndMode` in the same file (US5 Independent Test): for every mode in which Bepu built, one divergence result per committed and generated stream and required build, each with maxima, p99s, their step and vehicle, `windowSteps` and a verdict. Red: as above.

### Implementation

- [ ] T189 [US1] Make T185 pass from the record committed by T184: a winner that fails a check in `simcore/vehicles/selection/report.json` means the rule picked a non-Qualified candidate, a defect fixed in `src/MmoEngine.SimCore.Vehicles.Selection/Decision/DecisionRule.cs` or `Orchestration/StatusTable.cs` before the selection is rerun.
- [ ] T190 [US2] Make T186 pass from the committed report: a fixture that ended otherwise is a harness defect, fixed in `src/MmoEngine.SimCore.Vehicles.Selection/Orchestration/` or `tools/agreement/` (never in a fixture's expectation), then the `vehicle-selection` job is rerun and its report and record are committed by T184's steps.
- [ ] T191 [US4] Make T187 pass from the committed report: a missing measurement without a reason is a defect in `SelectionRun`'s measurement phase or `tools/agreement/lib/measure-vehicles.mjs`, fixed before the selection is rerun.
- [ ] T192 [US5] Make T188 pass from the committed report: a missing divergence result is a defect in `SelectionRun`'s divergence phase, fixed before the selection is rerun.

**Checkpoint**: the phase's Independent Test command passes.

---

## Phase 19: Polish and PR, slice 2

**Goal**: Close slice 2: local gates, coverage (C# and the shim's LCOV), the spec moved back from the
planned section, the patch-notes entry, the spec-test review, the independent automated review for
the guarded path, and the session's own squash-merge with one push notification.

**Independent Test**: each exits 0 locally: `dotnet build MmoEngine.sln -warnaserror`,
`dotnet format MmoEngine.sln --verify-no-changes`, `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory "$COV"`,
`python3 tools/coverage_gate.py --base origin/main --coverage-dir "$COV" --lcov "$COV/jolt.lcov"`,
`python3 -m unittest discover -s tests/workflows -v`, `node --test tools/agreement/node/*.test.mjs`,
and the quickstart's agreement leg; on the PR, every `SimCore`, `.NET` and `Workflow tests` check
passes on the head commit.

### Tests (write first, confirm red)

- [ ] T193 Write failing test `QuickstartTests.EverySliceTwoQuickstartCommandAndPathIsReal` in `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/QuickstartTests.cs`: every `simcore` command in the slice-2 sections parses against the command tree, every path and project exists, every build name and mode is known (`BuildNames`), and every script (`build.sh`, `publish-host.sh`, `changed-paths.sh`, lockcount `build.sh`) is executable. Red: fails while the quickstart's slice-2 sections carry the "Planned for slice 2" line or a path is missing.

### Implementation

- [ ] T194 Make T193 pass, removing the "Planned for slice 2" lines from `specs/016-vehicle-physics-selection/quickstart.md`.
- [ ] T195 Run the Independent Test commands and record each with its exit code and summary in a PR body file in the scratchpad directory; top up coverage until the gate exits 0 for every changed `src/` file, `src/MmoEngine.SimCore.Vehicles.Jolt/native/**` through LCOV included (Theories over uncovered branches; no exclusion, no skip).
- [ ] T196 Move every item of spec.md's `Planned for slice 2: not yet built, no test on main` section back to its place and delete the section; set `**Status**` to implemented; record what the implementation settled differently; correct any renamed test in [Scenario coverage](#scenario-coverage); check off every task of Phases 11-18; index check every slice-2 `Class.Method`, `node:test` name, Playwright title and Python test with `grep -F` against the listings (`dotnet test --list-tests`, `node --test --test-reporter=spec`, `npx playwright test --list`, `python3 -m unittest -v`). Check `specs/013-regionizer-simulator/spec.md`, `specs/014-telemetry-test-harness/spec.md` and `specs/015-shared-simulation-core/spec.md` for statements this feature contradicts, and record which spec prevails and why in this spec's Assumptions.
- [ ] T197 Commit and push `016-vehicle-physics-selection-slice-2` and open the draft PR with `gh pr create --draft --base main --head 016-vehicle-physics-selection-slice-2 --title "016 vehicle physics selection, slice 2: Jolt, browser builds, SimCore jobs and the decision"`, body from the scratchpad file: the commands and results, the decision record's outcome per mode and whether it is conditional or provisional, the judgment calls, "No open decision is settled", and the attribution lines last.
- [ ] T198 Add the `docs/patch-notes.md` entry: `## <date> — Vehicle physics selection, slice 2: Jolt, browser builds, SimCore vehicle jobs and the gate 9 decision`, the spec and PR links; **Behavior and contract changes** (the six `SimCore` jobs and what they gate; Jolt's adapter, shim and pins; the three hosts; the runner changes; the committed report and record with its outcome per mode); **Assumptions that no longer hold** (vehicle changes now gate on the chosen engine's streams); **Shared pieces to reuse** (`IVehicleEngine` and the byte ABI, the hosts' exports, `measure-vehicles.mjs`, `gate-plan`); **Open follow-ups** (spec Assumptions' four and plan.md's seven, de-duplicated: the Principle VII platform-runtime reading as a PATCH amendment the maintainer may make; correction distances under the network profiles for a NonExact record, sub-spec 6's dependency; the cost to sub-specs 5 and 6 of a NonExact record; static linking if the crossing decided; a foldhash (Zlib) amendment making Rapier a candidate; `DOTNET_EnableAVX512=0` as the effective knob for 014's and 015's runner class; the upstream Bepu and Mono AOT reports). Commit and push.
- [ ] T199 Run the spec-test review in a fresh subagent given only the PR link, applying `CLAUDE.md`'s natural-language-test rules to every acceptance scenario and Independent Test of `specs/016-vehicle-physics-selection/spec.md` through [Scenario coverage](#scenario-coverage); fix each finding in the test it names, rerun, push and answer each finding on the PR.
- [ ] T200 Audit for human steps (Principle XIII) with T142's grep over this file, `spec.md`, `quickstart.md` and the PR body; reword any hit that is not system behavior or an automated check.
- [ ] T201 Run the independent automated review of `.routines/merge-policy.md` (guarded paths): a fresh `general-purpose` subagent on this session's model reviews the PR, `.github/workflows/simcore.yml` included; fix every finding it reports, push, and rerun it until it reports none.
- [ ] T202 Merge (depends on every task above): wait for every check on the head commit with `gh pr checks <n> --watch`; confirm merge-policy rules 1-6 hold on the head commit (rule 5 by T201); `gh pr ready <n>`; squash-merge with `gh pr merge <n> --squash` (the PR touches a guarded path, so `auto-merge.yml` does not merge it); send one push notification to nbon12 saying it merged, with the PR link and the decision's outcome. Never add `hold`, skip a test or merge with a failing check.

**Checkpoint**: the Independent Test commands exit 0, the reviews' findings are fixed, and the PR is merged into `main`.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on Setup; blocks every user story.
- **Slice-1 story phases (3-9)**: each depends on Phase 2. Within slice 1, US1 (Phase 3) creates
  `StatusTable` and `SelectionRun`, which US2 (Phase 4) completes and US4 (Phase 6), US5 (Phase 7)
  and US6 (Phase 8) extend; US7's `V1PartHashTests` reads the fixture stream US1 records.
- **Polish, slice 1 (Phase 10)**: depends on Phases 3-9; ends with the slice-1 merge.
- **Slice 2 (Phases 11-19)**: depends on slice 1 merged into `main` (a new branch off `main`).
  Phase 11 (Setup) precedes everything in the slice. Phase 12 (Jolt) precedes Phase 13 (hosts,
  runners, collectors), which precedes Phases 14 (measurements), 15 (windowed runs) and 16 (the real
  fit check, which needs the cargo collector). Phase 17 (the jobs and the committed record) needs
  Phases 12-16, because the selection run uses every phase they add. Phase 18 needs Phase 17's
  committed report. Phase 19 depends on all of them.

### User Story Dependencies

- **US1 (P1)**: after Phase 2. No dependency on another story. Scenario 14 (Phase 18) needs US3's
  committed record.
- **US2 (P1)**: after Phase 2; its status table and orchestration extend US1's (`StatusTable`,
  `SelectionRun`), and its fixture-pair proof (Phase 18) needs US3's selection job.
- **US3 (P1)**: after Phase 2; the rule runs on committed fixture reports and needs no other story
  in slice 1. Its slice-2 half needs every other story's slice-2 phase (the selection run uses them all).
- **US4 (P2)**: after Phase 2; its allocation phase extends `SelectionRun` (US1).
- **US5 (P2)**: after Phase 2; its phase in `SelectionRun` needs US2's `IAgreementRunner`.
- **US6 (P3)**: after Phase 2; its orchestration needs US2's `SelectionRun`.
- **US7 (P3)**: after Phase 2; one test reads US1's fixture stream.

### Cross-Spec Dependencies

- One hard dependency, already met: feature 015's core, formats, analyzer, runners, `simcore` CLI and
  `SimCore` workflow, merged on `main` (plan.md, Spec independence). No sibling of sub-specs 4 to 9
  must land first. A NonExact record makes sub-spec 6 depend on this record's provisional flag; that
  is documented in plan.md and goes into sub-spec 6's plan when it is written.

### Within Each User Story

- Red tests before their green tasks; each green task names the red tasks it turns green.
- Core types before the step; the step before formats that serialise it; formats before streams;
  streams before checks; checks before the orchestration; the orchestration before the report and
  the decision.

### Parallel Opportunities

- Setup tasks marked [P] (T002-T008, T010) run together.
- Every Phase 2 red task is [P]; the green tasks T031, T032, T034,
  T035, T036, T037, T038, T044 and T046 run together.
- After Phase 2, the slice-1 stories' red tasks all run together; their green tasks serialise only on
  `VehiclesCommand.cs`, `SelectionRun.cs` and `SimWorld.cs`.
- In slice 2, the shim (T151), the lock counter (T152), the collectors (T163)
  and the benchmarks (T169) are independent.

---

## Parallel Example: User Story 1

```bash
# Launch the oracle tests together:
Task: "AccelerationCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Checks/AccelerationCheckTests.cs"
Task: "BrakingCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Checks/BrakingCheckTests.cs"
Task: "TurningCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Checks/TurningCheckTests.cs"
Task: "RampJumpCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Checks/RampJumpCheckTests.cs"
Task: "CollisionCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Checks/CollisionCheckTests.cs"
# Then the Bepu tests beside them:
Task: "VectorWidthGuardTests and BepuAllowlistTests in tests/MmoEngine.SimCore.Vehicles.Tests/Bepu/"
```

## Parallel Example: User Story 2

```bash
Task: "BatchStepperVehicleTests in tests/MmoEngine.SimCore.Tests/Batch/BatchStepperVehicleTests.cs"
Task: "SpdxTests and ClassifierGoldenTests in tests/MmoEngine.SimCore.Vehicles.Tests/License/"
Task: "StatusTableTests in tests/MmoEngine.SimCore.Vehicles.Tests/Selection/StatusTableTests.cs"
Task: "VehicleStreamGeneratorTests in tests/MmoEngine.SimCore.Vehicles.Tests/Scenarios/"
# Green, in parallel: the license library and the report model
Task: "License/*.cs in src/MmoEngine.SimCore.Vehicles.Selection/License/"
Task: "Report/*.cs in src/MmoEngine.SimCore.Vehicles.Selection/Report/"
```

## Parallel Example: User Story 3

```bash
Task: "DecisionRuleTests in tests/MmoEngine.SimCore.Vehicles.Tests/Decision/DecisionRuleTests.cs"
Task: "StalenessTests in tests/MmoEngine.SimCore.Vehicles.Tests/Decision/StalenessTests.cs"
Task: "GatePlanTests in tests/MmoEngine.SimCore.Vehicles.Tests/Decision/GatePlanTests.cs"
```

## Parallel Example: User Story 4

```bash
Task: "VehicleStepAllocationTests in tests/MmoEngine.SimCore.Vehicles.Tests/Allocation/"
Task: "SelectionReportValidatorTests measurement rows in tests/MmoEngine.SimCore.Vehicles.Tests/Selection/"
# Slice 2:
Task: "measure-vehicles.test.mjs in tools/agreement/node/"
Task: "VehicleBenchmarkDryRunTests in tests/MmoEngine.SimCore.Vehicles.Tests/Benchmarks/"
```

## Parallel Example: User Story 5

```bash
Task: "DivergenceMeasurementTests in tests/MmoEngine.SimCore.Vehicles.Tests/Divergence/"
Task: "WindowCheckTests in tests/MmoEngine.SimCore.Vehicles.Tests/Selection/WindowCheckTests.cs"
```

## Parallel Example: User Story 6

```bash
Task: "FitCheckOrchestrationTests in tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/"
Task: "FitCheckCriteriaTests and RapierIsolationTests in tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/"
Task: "RapierEngineTests in tests/MmoEngine.SimCore.Vehicles.Tests/FitCheck/RapierEngineTests.cs"
```

## Parallel Example: User Story 7

```bash
Task: "VehicleGoldenFixtureTests and VehicleGoldenRefusalTests in tests/MmoEngine.SimCore.Tests/Formats/"
Task: "Version1BaselineTests in tests/MmoEngine.SimCore.Tests/Formats/Version1BaselineTests.cs"
Task: "VehicleCanonicalisationTests in tests/MmoEngine.SimCore.Tests/World/"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 (Setup) and Phase 2 (Foundational).
2. Phase 3 (US1, slice 1): the checks proven on the fixture engine, recorded for Bepu.
3. **Validate**: run Phase 3's checkpoint command; continue when it is green.

### Incremental Delivery

1. Slice 1: Setup, Foundational, then US1, US2, US3, US4, US5, US6 and US7 in that order (each phase's
   checkpoint green before the next is marked done), then Phase 10's PR, merged on green.
2. Slice 2, on a new branch off `main`: the projects (Phase 11), Jolt (Phase 12), the browser builds and runners
   (Phase 13), the measurements (Phase 14), the windowed runs (Phase 15), the real fit check (Phase 16),
   the CI jobs and the committed decision (Phase 17), the committed-report proofs (Phase 18), then
   Phase 19's PR with the independent automated review and the session's merge.
3. Each slice leaves `main` green; spec.md states as current behavior only what `main` tests.

### Parallel Agent Strategy

After Phase 2, independent agents can take US1, US3, US7 and the license half of US2 at once; US4, US5
and US6 follow US2's `SelectionRun`. In slice 2, the shim and lock counter, the hosts and runners, and
the benchmarks proceed side by side until Phase 17 joins them.

---

## Delivery

| Slice | Phases | Task IDs | Branch | Guarded paths | PR and merge |
| --- | --- | --- | --- | --- | --- |
| 1 | 1-10 | T001-T143 | `016-vehicle-physics-selection` | None | One draft PR; marked ready once merge-policy rules 1-4 and 6 hold; `auto-merge.yml` squash-merges it |
| 2 | 11-19 | T144-T202 | `016-vehicle-physics-selection-slice-2`, cut from `main` after slice 1 merges | `.github/workflows/simcore.yml` | One draft PR; independent automated review in a subagent; the session squash-merges and sends one push notification |

---

## Scenario coverage

Every acceptance scenario and Independent Test of spec.md, with the task that writes its test and the
test that covers it. Rows whose task is in slice 2 cite slice-2 tests; slice 1's spec.md lists those
items in its planned section (T137).

| Scenario | Task | Test |
| --- | --- | --- |
| US1.1 flat acceleration to 95%, ≤ 2% over, upright, four wheels after 1 s | T049, T061 | `tests/MmoEngine.SimCore.Vehicles.Tests/Checks/AccelerationCheckTests.cs` `AccelerationCheckTests.PassesOnlyWhenEveryClauseHolds`; `tests/MmoEngine.SimCore.Vehicles.Tests/Fixture/FixtureChecksTests.cs` `FixtureChecksTests.EveryCheckPassesWithNoFault` (US1.1 row) |
| US1.2 braking within 120% of v²/(2b), at rest, upright | T050, T061 | `Checks/BrakingCheckTests.cs` `BrakingCheckTests.PassesOnlyWhenItStopsWithinTheIdealDistanceBand`; `FixtureChecksTests.EveryCheckPassesWithNoFault` (US1.2 row) |
| US1.3 turning circle: one direction, 360° in 15 s, 5 ± 0.5 m/s, radius within 25% | T051, T048, T061 | `Checks/TurningCheckTests.cs` `TurningCheckTests.PassesOnlyWhenAllFourClausesHold`; `Scenarios/VehicleScenarioCatalogTests.cs` `VehicleScenarioCatalogTests.TurningCircleGovernorPressesForwardExactlyOnStepsThatStartBelowFiveMetresPerSecond`; `FixtureChecksTests.EveryCheckPassesWithNoFault` (US1.3 row) |
| US1.4 rolling terrain, no penetration beyond the tolerance, ends upright | T052, T061 | `Checks/TerrainPenetrationCheckTests.cs` `TerrainPenetrationCheckTests.PenetrationIsMeasuredAgainstTheCoreWorld`; `FixtureChecksTests` (US1.4 row) |
| US1.5 ramp: airborne off the lip, lands beyond, settles within 2 s | T053, T061 | `Checks/RampJumpCheckTests.cs` `RampJumpCheckTests.LeavesTheLipAirborneLandsBeyondItAndSettlesWithinTwoSeconds`; `FixtureChecksTests` (US1.5 row) |
| US1.6 collisions: event at the first step within tolerance, penetration bound, closing speed ≤ 10% within 3 steps | T054, T020, T061 | `Checks/CollisionCheckTests.cs` `CollisionCheckTests.ContactEventPenetrationAndClosingSpeedEachDecide`; `tests/MmoEngine.SimCore.Tests/Vehicles/VehicleContactsTests.cs` `VehicleContactsTests.AContactBeginsOnlyWhenTheSeparationCrossesTheTolerance`; `FixtureChecksTests` (US1.6 row) |
| US1.7 thin wall never passed through | T055, T061 | `Checks/ThinWallCheckTests.cs` `ThinWallCheckTests.ChassisNeverEndsAStepPastTheFarFace`; `FixtureChecksTests` (US1.7 row) |
| US1.8 two processes give identical hashes equal to the committed ones | T056 | `Cli/StreamsVerifyEngineTests.cs` `StreamsVerifyEngineTests.EveryCommittedStreamMatchesInTwoFreshProcesses` (fixture, bepu); denial `StreamsVerifyEngineTests.AStaleExpectedHashIsServerNotReproducible` |
| US1.9 20, 30 and 60 Hz streams stepped twice identical | T057 | `Checks/RepeatRunCheckTests.cs` `RepeatRunCheckTests.AStreamSteppedTwiceGivesIdenticalHashes` (with the `state-survives-reload` denial row) |
| US1.10 server reset equivalence at every window start | T058 | `Checks/ServerResetCheckTests.cs` `ServerResetCheckTests.AWorldReloadedAtEveryWindowStartStepsOnIdentically` (with the `hidden-accumulator` denial row) |
| US1.11 out-of-range command rejected with 015's code, neutral input stepped | T059 | `Checks/RejectionCheckTests.cs` `RejectionCheckTests.EachInvalidCommandIsRejectedWithItsReasonAndStepsTheNeutralInput`; denial `RejectionCheckTests.RejectedCommandApplied` |
| US1.12 no lock over 1,000 warmed steps | T060, T149 | `Locks/ManagedLockCheckTests.cs` `ManagedLockCheckTests.TheFixtureEngineTakesNoLockOver1000Steps`, `ManagedLockCheckTests.InstrumentationCountsOneLockPerStepOnAFixtureAssembly` (the `TakesLock` denial); slice 2: `tests/MmoEngine.SimCore.Vehicles.Native.Tests/LockCounterTests.cs` `LockCounterTests.TheCounterCountsOneMutexPerStepOnTheTestLibrary` |
| US1.13 one fixture fault: Failed in every mode, exact reason, others pass and are evaluated | T062, T063 | `Fixture/FaultTableTests.cs` `FaultTableTests.EachFaultFailsExactlyItsTargetCheck`; `Selection/SelectionRunServerPhaseTests.cs` `SelectionRunServerPhaseTests.AFixtureFaultIsFailedInEveryModeAndOthersAreStillEvaluated` |
| US1.14 the record's winner passes every check; a None record names no winner and each candidate's failing checks | T185, T178 | `Decision/WinnerChecksTests.cs` `WinnerChecksTests.TheRecordsWinnerPassesEveryCheckOrTheRecordNamesNone`; `tests/workflows/test_simcore_workflow.py` `VehicleAgreementGate.test_winner_checks_run_checks_and_lockcount_for_the_winner` (the `vehicle-winner-checks` job) |
| US1-IT fixture no fault passes all; each fault fails exactly its target; candidates' verdicts recorded; streams verify in two processes; winner passes | T061, T062, T067, T056, T147, T149, T185 | `FixtureChecksTests.EveryCheckPassesWithNoFault`; `FaultTableTests.EachFaultFailsExactlyItsTargetCheck`; `Bepu/BepuServerChecksTests.cs` `BepuServerChecksTests.EveryCheckRecordsOneVerdictWithItsMeasuredValueAndBound`; `StreamsVerifyEngineTests.EveryCommittedStreamMatchesInTwoFreshProcesses`; slice 2: `JoltNativeTests.JoltServerChecksRecordOneVerdictPerCheck`, `WinnerChecksTests.TheRecordsWinnerPassesEveryCheckOrTheRecordNamesNone` |
| US2.1 per-step records of each required build compared byte for byte in three modes | T156, T157, T081 | `tools/agreement/node/agreement.test.mjs` `test('<stream>')`; `tools/agreement/specs/worker.spec.mjs` `'every vehicle stream, batch and window matches the server in a dedicated Worker'`; `Cli/AgreementPrepareVehicleTests.cs` `AgreementPrepareVehicleTests.RequiredBuildsPerModeAreTheSpecsEight` |
| US2.2 50 generated seeds compared the same way | T081, T156, T157 | `AgreementPrepareVehicleTests.PrepareWritesManifestVersion2` (64 streams, 50 generated); `agreement.test.mjs` `test('vseed-001')`…`test('vseed-050')`; `worker.spec.mjs` (as US2.1) |
| US2.3 a one-step difference is Diverged (`RecordDiverged`) naming stream, step, build and fields in bit form | T080, T087, T156 | `tests/MmoEngine.SimCore.Tests/Cli/AgreementCompareVehicleTests.cs` `AgreementCompareVehicleTests.PerturbedRecordsExitOneNamingStep15AndThePositionField`, `EveryDifferingFieldIsListedInBitForm`; `Selection/StatusTableTests.cs` `StatusTableTests.EachRowGivesItsStatusAndReason` (`RecordDiverged`); `agreement.test.mjs` `'perturbation diverges at exactly its step'` |
| US2.4 browser restart from the server state equals the server; else `ResetDiverged` | T080, T156, T157 | `AgreementCompareVehicleTests.WindowsThatDifferAfterARestartAreResetDiverged`; `agreement.test.mjs` `'windowed runs restart at every window start from the server state'`; `worker.spec.mjs` (as US2.1) |
| US2.5 batched entry point equals one at a time, server and Worker | T079, T154, T156, T157 | `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperVehicleTests.cs` `BatchStepperVehicleTests.QueuedCommandsForTheFirstVehicleEqualOneAtATime`; `VehicleExportsTests.StepQueuedDrivesTheFirstVehicle`; `agreement.test.mjs` `'batch inputs through StepQueued equal RunStream'`; `worker.spec.mjs` (as US2.1) |
| US2.6 015's files equal the 65112ab manifest and still Match | T123, T081, T177 | `tests/MmoEngine.SimCore.Tests/Formats/Version1BaselineTests.cs` `Version1BaselineTests.TheVersion1FilesMatchTheBaseline`, `EveryVersion1StreamStillMatches`; `AgreementPrepareVehicleTests.CompareOnA015WorkDirectoryPrintsExactly015sSevenBuildLines`; `test_simcore_workflow.py` `VehicleJobs.test_015_agreement_and_measure_jobs_equal_their_committed_definitions` |
| US2.7 another engine, version, setting, rules revision or format is NotComparable, exit 2; own stream → `StreamNotComparable` | T082, T088 | `tests/MmoEngine.SimCore.Tests/Agreement/NotComparableVehicleTests.cs` `NotComparableVehicleTests.AnotherIdentityRevisionOrVersionIsNotComparableNeverDiverged`; `Selection/SelectionRunInputTests.cs` `SelectionRunInputTests.AnOwnStreamNotComparableIsFailed` |
| US2.8 a failed or timed-out build is NotBuilt with step and last 50 lines; others evaluated | T088, T159 | `SelectionRunInputTests.ABuildFailureOrTimeoutIsNotBuiltWithItsStepAndLastFiftyLines`; `tests/workflows/test_publish_host.py` `test_a_failing_publish_is_a_recorded_outcome` |
| US2.9 fixture pair: perturbed Diverged at step 15, other Qualified, BitExact pick | T160, T186, T156 | `Cli/CheckFixturesCommandTests.cs` `CheckFixturesCommandTests.TheFixturePairPassesAndAnyOtherEndingFails`; `Selection/CommittedReportFixtureTests.cs` `CommittedReportFixtureTests.TheFixturePairEndedQualifiedAndDivergedInEveryMode`; `agreement.test.mjs` `'injected fault perturbs only the named stream'` |
| US2.10 GPL-3.0-only transitive dependency: LicenseBlocked in every mode, exit 0 | T085, T088 | `License/ClassifierGoldenTests.cs` `ClassifierGoldenTests.EachInventoryGivesItsExpectedResult` (`gpl-transitive`); `SelectionRunInputTests.AGplTransitiveDependencyIsLicenseBlockedInEveryMode` |
| US2.11 each FR-037 row's fixture gives its status and reason; first status wins, every reason listed; exit 0 | T087, T088 | `StatusTableTests.EachRowGivesItsStatusAndReason`, `StatusTableTests.SeveralRowsTakeTheFirstStatusInOrderAndListEveryReason`; `SelectionRunInputTests.EachStatusRowFixtureEndsWithItsStatusAndExitsZero` |
| US2.12 every seed has a contact, every vehicle ≥ 50 m, one airborne; misses named; `CoverageNotMet` | T083, T088 | `Scenarios/VehicleStreamGeneratorTests.cs` `VehicleStreamGeneratorTests.EverySeedOnTheFixtureEngineMeetsItsCoverage`, `AMissNamesTheSeedAndTheCondition`; `SelectionRunInputTests.CoverageMissIsCoverageNotMet`; `BepuServerChecksTests.BepuCoverageVerdictIsRecorded` |
| US2-IT one status per candidate and mode, one result per required build; one-ulp change agrees before and differs at step 15; compare exits 1; denials 9-11 | T089, T080, T156, T186, T088 | `Selection/SelectionReportValidatorTests.cs` `SelectionReportValidatorTests.EachDefectFailsWithItsPathAndProblem`; `AgreementCompareVehicleTests.PerturbedRecordsExitOneNamingStep15AndThePositionField`; `agreement.test.mjs` `'perturbation diverges at exactly its step'`; `CommittedReportFixtureTests.EveryCandidateHasOneStatusPerModeAndOneResultPerRequiredBuild`; the US2.9-US2.11 tests above |
| US3.1 one Qualified in every mode → BitExact | T096 | `Decision/DecisionRuleTests.cs` `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`one-qualified`) |
| US3.2 lowest browser cost wins when others exceed 1.10 × | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`cheapest-wins`) |
| US3.3 cost tie narrows by Brotli; then lowest cost, fewest bytes, fixed order only on equality | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`cost-tie-brotli-decides`, `both-tie-lowest-cost-wins`, `exact-equal-fixed-order`) |
| US3.4 no qualifier, Bepu Diverged within bounds → NonExact, provisional, not meeting gate 9 alone | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`nonexact-bepu`) |
| US3.5 Bepu beyond a bound or not Diverged → None; statuses listed; every mode None states criterion not met; exit 0 | T096, T100 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`none-bepu-beyond-bound`, `none-bepu-failed`, `none-every-mode`); `Cli/VehiclesDecideCommandTests.cs` `VehiclesDecideCommandTests.DecideExitsZeroForEveryOutcomeNoneIncluded` |
| US3.6 different winners or outcomes per mode → conditional; mixed-mode fixture | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`mixed-mode`) |
| US3.7 same report twice → identical bytes | T097 | `DecisionRuleTests.TheSameReportGivesIdenticalBytesTwice` |
| US3.8 a changed fingerprint input fails the staleness check naming it; provisional printed | T098 | `Decision/StalenessTests.cs` `StalenessTests.EachChangedInputIsNamed`, `TheProvisionalFlagIsPrintedFirst`, `AnUnchangedRepositoryIsCurrent` |
| US3.9 vehicle agreement job gates BitExact, NonExact and None as stated | T099, T178 | `Decision/GatePlanTests.cs` `GatePlanTests.EachRecordGivesItsGateAndLegs`; `test_simcore_workflow.py` `VehicleAgreementGate.test_bitexact_leg_compares_with_the_modes_require_set_and_fails_on_divergence`, `test_nonexact_leg_runs_streams_verify_divergence_and_v1_part_compare_and_prints_the_flag`, `test_none_or_no_record_writes_the_outcome_to_the_summary_and_skips_the_gate_jobs` |
| US3.10 band boundaries: 100/110 tie, 100/110.01 no tie, 100/109/115 first two; same for Brotli | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`cost-100-110`, `cost-100-110.01`, `cost-100-109-115`, `brotli-100-110`, `brotli-100-110.01`, `brotli-100-109-115`) |
| US3.11 cheapest Diverged, dearer Qualified wins BitExact; Diverged listed | T096 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` (`cheapest-diverged`) |
| US3-IT fixture reports match; committed record reproduced; a changed input stales it | T096, T180, T098 | `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord`; `DecisionRuleTests.TheCommittedRecordComesFromTheCommittedReport`; `StalenessTests.EachChangedInputIsNamed` |
| US4.1 server benchmarks: mean and p99 of world and vehicle step at 1, 16, 64, 256; Bepu pinned | T166 | `Benchmarks/VehicleBenchmarkDryRunTests.cs` `VehicleBenchmarkDryRunTests.TheExportHasMeanAndP99OfWorldStepAndVehicleStepLabelledMeasurement`, `EveryEngineAndVehicleCountIsACase`, `BepuRunRecordsTheVectorPin` |
| US4.2 browser cost at 1, 16, 64 in Node and Worker per mode; 10 cold starts; crossing included | T167 | `tools/agreement/node/measure-vehicles.test.mjs` `'step cost is 3008 timed steps after 512 warm-up in 16-command batches'`, `'worlds of 1, 16 and 64 vehicles drive vehicle 0 and park the others'`, `'each cold start is checked against the server first record of flat-accel-30hz'`, `'Jolt step cost includes the crossing and its download includes the module and glue'` |
| US4.3 added bytes raw, Brotli, gzip; module and glue included | T167 | `measure-vehicles.test.mjs` `'download added is the host set plus engine files minus the core download in the same mode, raw, brotli and gzip'` |
| US4.4 zero managed bytes over 1,000 warmed steps | T103, T168 | `Allocation/VehicleStepAllocationTests.cs` `VehicleStepAllocationTests.EachCandidatesVehicleStepAllocatesNothingAfterWarmUp` (fixture, bepu); `tests/MmoEngine.SimCore.Vehicles.Native.Tests/JoltAllocationTests.cs` (jolt row) |
| US4.5 unmeasured candidate or mode named with a reason; missing without one fails validation | T104, T167 | `SelectionReportValidatorTests.AMissingMeasurementWithoutAReasonFails`, `AnUnmeasuredModeWithItsReasonPasses`; `measure-vehicles.test.mjs` `'an unproduced mode is produced false with its reason'` |
| US4-IT every measurement with units, runner class, mode and identity; zero allocation | T187, T103 | `CommittedReportFixtureTests.EveryBuiltCandidateHasEveryMeasurement`; `VehicleStepAllocationTests.EachCandidatesVehicleStepAllocatesNothingAfterWarmUp` |
| US5.1 each required build loaded at every window start, three errors per vehicle and step | T172, T156 | `Selection/SelectionRunDivergenceTests.cs` `SelectionRunDivergenceTests.EachRequiredBuildIsLoadedAtEveryWindowStartAndStepsTheWindow`, `ThreeErrorsAreRecordedPerVehicleAndStep`; `agreement.test.mjs` `'Bepu windows are written for every committed and generated stream'` |
| US5.2 max and p99 per stream, build and mode, with step and vehicle | T108 | `Divergence/DivergenceMeasurementTests.cs` `DivergenceMeasurementTests.MaximaAndP99NameTheirStepAndVehicle` |
| US5.3 known offset 0.03 m and 0.5° reported within 1e-4 m and 1e-3° | T107 | `DivergenceMeasurementTests.AKnownOffsetIsReportedWithinItsTolerance` |
| US5.4 within 0.05 m and 1° → within bounds; one step beyond → beyond, named; velocity not bounded | T108 | `DivergenceMeasurementTests.BoundsGiveWithinOrBeyond` |
| US5.5 identical records → every error exactly 0 | T108 | `DivergenceMeasurementTests.IdenticalRecordsGiveExactlyZero` |
| US5.6 window covers every profile at 20, 30, 60 Hz; an uncovered one fails naming profile, rate and spans | T110 | `Selection/WindowCheckTests.cs` `WindowCheckTests.TheWindowCoversEveryCommittedProfile`, `AProfileOneStepOverTheWindowFailsNamingProfileRateAndSpans` |
| US5-IT known pair; Bepu's streams in every build and mode; window against profiles | T107, T188, T110 | `DivergenceMeasurementTests.AKnownOffsetIsReportedWithinItsTolerance`; `CommittedReportFixtureTests.BepuHasDivergenceResultsForEveryStreamBuildAndMode`; `WindowCheckTests.TheWindowCoversEveryCommittedProfile` |
| US6.1 fit check records pass or fail with evidence per criterion | T174 | `FitCheck/FitCheckCommandTests.cs` `FitCheckCommandTests.FitcheckRecordsAVerdictAndEvidenceForEveryCriterion` |
| US6.2 one failing criterion → NotTested (`FitCheckFailed`) with it; others decided; exit 0 | T114 | `FitCheck/FitCheckOrchestrationTests.cs` `FitCheckOrchestrationTests.EachFailingCriterionIsNotTestedWithThatCriterion` |
| US6.3 all passing → Rapier scheduled for everything Bepu and Jolt get, same report shape | T115 | `FitCheckOrchestrationTests.AllPassingSchedulesRapierForEveryPhaseBepuAndJoltGet` |
| US6-IT verdict and evidence per criterion; each fixture's result | T174, T114, T115, T116 | the US6.1-US6.3 tests; `FitCheckCriteriaTests.CriteriaRunInTheOrderCBDFAE`; `RapierIsolationTests.NoSharedFileReferencesRapier` |
| US7.1 valid v2 golden fixtures round-trip | T120 | `tests/MmoEngine.SimCore.Tests/Formats/VehicleGoldenFixtureTests.cs` `VehicleGoldenFixtureTests.EachValidV2CaseRoundTripsToIdenticalBytes` |
| US7.2 a v1 stream runs under v1, its hashes and SHA-256 match | T123 | `Version1BaselineTests.EveryVersion1StreamStillMatches`, `Version1BaselineTests.TheVersion1FilesMatchTheBaseline` |
| US7.3 unsupported version refused naming found and supported; NotComparable in the suite | T121, T122, T023 | `Formats/VehicleGoldenRefusalTests.cs` `VehicleGoldenRefusalTests.EachRefusalCaseIsRefusedWithItsCodeOffsetAndField` (`v2-unsupported-version`); `GoldenRefusalTests` (`unsupported-version`, version 3); `RecordedStreamV2Tests.UnsupportedVersionNamesTheVersionsSupported` |
| US7.4 truncated, oversized or inconsistent stream or state refused naming offset or field, no partial result | T121, T125 | `VehicleGoldenRefusalTests.EachRefusalCaseIsRefusedWithItsCodeOffsetAndField`; `Formats/VehicleStateLoadTests.cs` `VehicleStateLoadTests.EachLoadCheckRefusesWithItsCodeAndField` |
| US7.5 collision data not supplied → refused before any step | T121 | `VehicleGoldenRefusalTests.EachRefusalCaseIsRefusedWithItsCodeOffsetAndField` (`v2-collision-missing`, engine `Step` never called) |
| US7.6 −0 stored and hashed as +0; NaN or infinity refuses the step naming the field | T124 | `World/VehicleCanonicalisationTests.cs` `VehicleCanonicalisationTests.NegativeZeroIsCommittedAndHashedAsPositiveZero`, `ANonFiniteFieldStopsTheStepNamingIt` |
| US7.7 two hashes per step; v1 part hash equals the stream without vehicles | T126, T022 | `Formats/V1PartHashTests.cs` `V1PartHashTests.TheVersion1PartHashEqualsTheStreamWithItsVehiclesRemoved`; `StateSerializerV2Tests.V1PartHashIsXxHash64OfThePrefix` |
| US7-IT golden round trips; 015 fixtures decode; manifest; 015 streams; hostile inputs refused | T120, T122, T123, T121, T125 | the US7.1-US7.5 tests above; 015's `GoldenFixtureTests` still passing |

Supporting requirement tests outside the scenarios: FR-004 `ExemptionTests.DeclaredExemptionAdmitsOnlyItsRuleAndMembers`,
`WiringTests.OnlyTheAdaptersDeclareAnExemptionWithTheirValues`, `AdapterBackstopTests.EachAdapterHasNoDiagnosticOutsideItsExemption`;
FR-003 and FR-033 `EngineNeutralityTests.NoSharedFileHasAnEngineSpecificBranch`; FR-005 `VehicleConfigRuleTableTests`;
FR-006 `StaticTrianglesTests`; FR-007 `VehicleWorldGeneratorTests.GeneratedBytesEqualTheCommittedWorld`;
FR-009 `SimWorldVehicleTests.MeasuredFrameTimeIsNotAccepted`; FR-036 `SelectionConfigValidatorTests`;
FR-001 and research R8 `VectorWidthGuardTests`; research R7 `BepuAllowlistTests`; FR-002 `EngineIdentityFingerprintTests`.

---

## Notes

- [P] tasks touch different files and depend on no unfinished task in their phase.
- The [Story] label maps each task to its user story for traceability; Setup, Foundational and Polish
  tasks carry none.
- Each red test is seen failing before its green task; each checkpoint is passed when its automated
  checks are green.
- Commit after each task or logical group; never commit to a merged branch.
- Avoid: vague tasks, two [P] tasks on one file, a test asserting a real engine's verdict.
