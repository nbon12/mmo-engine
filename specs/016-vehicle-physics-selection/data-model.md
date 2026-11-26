# Data Model: Vehicle Physics Engine Selection

The entities this feature adds to feature 015's shared core and tooling. 015's entities
([../015-shared-simulation-core/data-model.md](../015-shared-simulation-core/data-model.md)) are unchanged
unless a section below says it extends one. Byte layouts are in
[contracts/vehicle-formats.md](contracts/vehicle-formats.md); JSON shapes are in the other contracts.

## Conventions

- 015's frame: Y up, metres, seconds, radians inside the core; yaw 0 faces +Z. A vehicle's forward axis
  is its local +Z, its up axis local +Y and its right axis local +X.
- Every float in core state is IEEE 754 binary32, finite and never −0 once committed (015 FR-016,
  FR-017). Tool code (checks, divergence, decision) computes in `double` or `decimal` from those bits.
- Vehicle IDs are `u32` in [1, 65535], unique within a world, strictly ascending in every list.
- Every core loop has a fixed bound (015 FR-005). The vehicle step allocates nothing after the world is
  created (FR-025).
- "Core" means `src/MmoEngine.SimCore/` under the full analyzer; "tool code" means the server-only
  assemblies `MmoEngine.SimCore.Vehicles.Server`, `MmoEngine.SimCore.Vehicles.Selection` and the CLI.

## Ownership

| State | Owner | Writer |
| --- | --- | --- |
| Vehicle state in a world | the world (`SimWorld`) | `SimWorld.Step`, `SimWorld.Reset` |
| Engine world (bodies, statics) | the adapter instance the world was created with | the adapter, inside `Load` and `Step` only |
| Vehicle configuration | the compiled configuration (`CompiledVehicleConfig`) | built once from a validated block |
| Committed vehicle streams | `simcore/vehicles/streams/<engine>/` | `simcore streams record --engine <engine> --reason` only |
| Selection report, decision record | `simcore/vehicles/selection/` | committed by an agent from the `vehicle-selection` artifact and `simcore vehicles decide` |

In the continent server (sub-spec 5) the region that owns the vehicles owns the world and its engine; in
the client, the Worker that predicts its own vehicle. Engines step on the calling thread only, so in a
parallel Movement phase a region's whole vehicle world is one worker's slice.

## Vehicle configuration

The optional `vehicles` section of the simulation configuration JSON
(`simcore/vehicles/config/default.json` holds 015's sections plus this one). A configuration without it
is valid and means no vehicles (FR-005). In a recorded stream it travels as the **vehicle config block**,
separate from 015's config block, whose field table stays as stream version 1 pins it. The rule table
(field IDs, kinds, ranges, placeholders, relations) is [contracts/vehicle-config.md](contracts/vehicle-config.md).

| Group | Fields |
| --- | --- |
| World | `maxVehicles`, `gravity` |
| Chassis | `massKg`, `chassisHalfExtents.{x,y,z}`, `chassisOffsetY`, `chassisFriction` |
| Wheels | `wheels[0..3].attach.{x,y,z}` (front left, front right, rear left, rear right), `wheelRadius` |
| Suspension | `suspensionRestLength`, `suspensionTravel`, `suspensionStiffness`, `suspensionDamping` |
| Tires | `tireGrip`, `tireSolverIterations` |
| Drive | `driveForce`, `topSpeed`, `driveCutbackFraction`, `reverseDriveForce`, `reverseTopSpeed`, `reverseEngageSpeed`, `brakeDeceleration` |
| Steering | `maxSteerAngleDeg`, `steerRateDegPerSecond` |
| Solver | `substeps` (K), `contactTolerance` |

### CompiledVehicleConfig

Built by `VehicleConfigGuard.Check` then `CompiledVehicleConfig.Create`, once per world. The vehicle step
reads only this.

| Field | Derivation |
| --- | --- |
| `SubstepLength` | `1f / stepRateHz / substeps` (015's step length divided by K) |
| `Inertia` (diagonal, kg·m²) | the box about its centre (m/12 · (h_y² + h_z²)·4 and so on) plus the parallel-axis term m·d² on the X and Z axes for the vertical offset d |
| `InverseInertia` | the reciprocal of each diagonal entry |
| `MaxSteerAngle`, `SteerRatePerSubstep` | from degrees through `SimMath` (015's angle rules) |
| `WheelRayLength` | `suspensionRestLength + wheelRadius` |
| `Wheelbase`, `Track` | front z − rear z; left x − right x (the relations make them unique) |
| `SpawnCompression` | m·g / (4k): the static-equilibrium compression the scenario builders spawn with |

## Engine identity

| Field | Type | Rule |
| --- | --- | --- |
| `Engine` | `u8` code | 1 Bepu, 2 Jolt, 3 Rapier, 255 Fixture |
| `Release` | ASCII, ≤ 32 bytes | the pinned release, for example `2.5.0-beta.33`, `v5.7.0`, `0.36.1`, `fixture-1` |
| `Fingerprint` | `u64` | XxHash64 over the pin file and its listed build inputs ([contracts/engine-adapter.md](contracts/engine-adapter.md#engine-identity)); for the fixture, over its pin file and its fault code unless the fault is identity-preserving |

Two identities are equal when all three fields are. Every vehicle stream carries the identity it was
recorded with; a stream whose identity differs from the build's is NotComparable (FR-014).

## Vehicle state

One vehicle, inside canonical state version 2 (FR-012).

| Field | Type | Real state or output | Rule |
| --- | --- | --- | --- |
| `Id` | `u32` | identity | [1, 65535], strictly ascending |
| `Position` | 3 × `f32` | real | the centre of mass, world frame; inside the playable area, else the step faults (`VehicleOutOfBounds`) |
| `Orientation` | 4 × `f32` (x, y, z, w) | real | the engine's own bits; \|q\|² within [0.998, 1.002] on load (contracts/vehicle-formats.md) |
| `LinearVelocity` | 3 × `f32` | real | m/s, world frame |
| `AngularVelocity` | 3 × `f32` | real | rad/s, world frame |
| `Wheels[4]` | `WheelState` | | front left, front right, rear left, rear right |
| `LastSequence` | `u32` | real | the sequence of the last applied command; 0 before the first |

`WheelState`:

| Field | Type | Real state or output | Rule |
| --- | --- | --- | --- |
| `SteeringAngle` | `f32` | real | radians, within ±`MaxSteerAngle`; 0 on the rear wheels |
| `Spin` | `f32` | output | rad/s: forward contact speed / radius in contact, held while airborne; never read by a force |
| `Compression` | `f32` | real | `rest − length` in [0, `suspensionTravel`], where length = (ray hit distance − radius) clamped to [rest − travel, rest], and 0 when the ray misses; the next substep's damping reads its change as ṡ |
| `Contact` | `u8` | output | 1 when the wheel's ray hit, else 0 |

A vehicle that rolls onto its roof or side stays valid and keeps stepping (spec Edge Cases).

## Vehicle body (the adapter's crossing)

What the core hands an adapter and reads back each substep, in ascending vehicle ID
([contracts/engine-adapter.md](contracts/engine-adapter.md)):

| Field | Type |
| --- | --- |
| `Id` | `u32` |
| `Position` | 3 × `f32` (centre of mass) |
| `Orientation` | 4 × `f32` |
| `LinearVelocity` | 3 × `f32` |
| `AngularVelocity` | 3 × `f32` |

The wheel state never crosses: it is the core's.

## Vehicle commands

Each vehicle is driven by 015's input command version 1 (17 bytes), validated by 015's rules (015 FR-019)
against the vehicle's `LastSequence` and `input.weaponSlots` before it affects state (FR-008). A rejected
command is recorded with 015's `InputRejection` code in the vehicle input-rejection events, and the vehicle
steps with the neutral input.

| Buttons held | Effect |
| --- | --- |
| `Forward` | throttle: drive force forward, cut back over the last `driveCutbackFraction` of `topSpeed` |
| `Back` | brake while the forward speed is above `reverseEngageSpeed`; reverse drive (up to `reverseTopSpeed`) once it is at or below it |
| `Forward` and `Back` | brake (Back wins) |
| `Left` / `Right` | steering target −/+`MaxSteerAngle` (both or neither: 0), approached at `steerRateDegPerSecond` |
| any other bit | no effect on a vehicle |

## World step order (extends 015)

`SimWorld.Step(commands)` for a world created with a vehicle configuration and an engine. 015's stages 1
to 5 run first, unchanged and independent of vehicles (characters and vehicles do not interact in this
slice). Then:

| # | Stage | Reads | Writes |
| --- | --- | --- | --- |
| V1 | Validate vehicle commands, ascending vehicle ID | vehicle commands, each `LastSequence`, `input.weaponSlots` | per vehicle the accepted or neutral input; vehicle input-rejection events |
| V2 | For each substep k = 1..K: wheel model, ascending vehicle ID | vehicle bodies and wheels, inputs, `CompiledVehicleConfig`, the collision world (`SegmentQuery`) | steering angles, compression, contact, spin; chassis velocities after the tire and suspension impulses |
| V3 | For each substep: engine step | the bodies after V2 | the bodies after integration and contacts (`IVehicleEngine.Step`) |
| V4 | Bounds | the bodies after the last substep | `VehicleOutOfBounds` fault naming `vehicles[i].position.x` (or `.z`, `.y`) |
| V5 | Vehicle contacts, ascending (lower ID, higher ID) | the previous and the next vehicle poses, chassis dimensions, `contactTolerance` | vehicle contact events |

015's stage 6 (canonicalise, check, serialise, hash) then covers the version 2 state, and stage 7 commits.
A fault at any stage, an engine fault included (`EngineFault`, with the engine's code), commits nothing.

## Step events and faults (extends 015)

| List | Record | Order |
| --- | --- | --- |
| `events.vehicleContacts` | lower vehicle ID, higher vehicle ID | ascending pair |
| `events.vehicleInputRejections` | vehicle ID, `InputRejection` code | ascending vehicle ID |

`StepFaultCode` gains `VehicleOutOfBounds` and `EngineFault`; 015's `None`, `NonFinite` and
`QueryCapacityExceeded` are unchanged.

## Version 2 formats (summary)

| Format | Version | Changes from 015 |
| --- | --- | --- |
| Canonical state | 2 | version 1's bytes, then the vehicle part ([contracts/vehicle-formats.md](contracts/vehicle-formats.md#canonical-state-version-2)) |
| Version 1 part hash | — | XxHash64 (seed 0) of the version 1 prefix |
| Step record | 2 (version 2 streams only) | `u32 step | u32 stateLength | state | u64 hash | u64 v1PartHash` |
| Recorded stream | 2 | a 116-byte header with the engine identity and the vehicle rules revision; a vehicle config block; the initial state in version 2; commands to characters and vehicles; both expected hashes per step |
| Vehicle config block | 1 | 015's entry encoding, sections 8 and 9 |
| Agreement manifest | 2 | the engine, the vehicle rules revision, the modes and their builds, windows |
| Input command, collision data | 1 | unchanged |

`SimCoreVersion.RulesRevision` stays 1. `SimCoreVersion.VehicleRulesRevision` (`u32`, starts at 1) names
the vehicle rules: the command mapping, the wheel model, contact derivation and the bounds fault.

## Vehicle test world

`simcore/vehicles/content/vehicle-world.mmcd`, `MMCD` version 1, generated by `VehicleWorldGenerator`
(`src/MmoEngine.SimCore.Vehicles.Server/`) from seed `0x5645484943313600` and reproduced byte for byte by
`simcore vehicles content generate`. Placeholder layout (research R21):

| Zone | Extent (x, z in metres; the heightfield spans x ∈ [0, 512], z ∈ [0, 384]) | Terrain | Meshes | Used by |
| --- | --- | --- | --- | --- |
| Flat lane | x ∈ [40, 440], z ∈ [16, 40] | flat at y = 0 | the thin wall: 0.1 m thick, 3 m tall, 20 m wide, across the lane at x = 430 | flat runs, braking, `thin-wall` |
| Turning pad | x ∈ [40, 100], z ∈ [60, 120] | flat | none | `turning-circle` |
| Rolling terrain | x ∈ [120, 380], z ∈ [60, 180] | sum of sines, 0.5–1.5 m amplitude, 20–60 m wavelengths, blending to flat at the edges | none | `rolling-terrain` |
| Ramp | x ∈ [400, 500], z ∈ [60, 120] | flat | a ramp 10 m long, 6 m wide, rising 1.6 m to a sharp lip at x = 430, flat landing beyond | `ramp-jump` |
| Arena | x ∈ [40, 160], z ∈ [220, 340] | flat | none | collisions, `pile-up`, generated streams, benchmarks |
| Enclosure | the heightfield's border | | 3 m walls, 0.5 m thick, around the whole world | every stream |

Every flat zone has no mesh within its extent except the one named, so the ideals of US1 scenarios 1 to 3
are not disturbed by terrain.

## Vehicle scenarios

`VehicleScenarioCatalog` (`src/MmoEngine.SimCore.Vehicles.Server/`) defines the named streams; each is
recorded per candidate. Speeds and steps are placeholders. Every vehicle spawns at its static-equilibrium
compression (`SpawnCompression`), upright, unless the row says otherwise.

| Stream | Rate | Vehicles and commands | Backs |
| --- | --- | --- | --- |
| `flat-accel-20hz`, `flat-accel-30hz`, `flat-accel-60hz` | 20, 30, 60 Hz | one vehicle at rest in the lane; `Forward` for 10 s | US1.1, US1.9 |
| `brake-to-stop` | 30 Hz | one vehicle; `Forward` until it is within 1% of top speed (recorded), then `Back` until at rest plus 1 s | US1.2 |
| `turning-circle` | 30 Hz | one vehicle at 5 m/s on the pad; `Right` held 15 s; `Forward` on exactly the steps that start below 5 m/s (the recording governor, US1.3) | US1.3 |
| `rolling-terrain` | 30 Hz | one vehicle; `Forward` 30 s across the rolling zone along +X, then a diagonal pass | US1.4 |
| `ramp-jump` | 30 Hz | one vehicle starting 60 m before the lip; `Forward` until 15 m/s (recorded), then held; 8 s | US1.5 |
| `collide-head-on` | 30 Hz | two vehicles 40 m apart in the arena, facing, coasting at 8 m/s each | US1.6 |
| `collide-side` | 30 Hz | one at rest broadside; one coasting at 8 m/s into its side | US1.6 |
| `collide-rear` | 30 Hz | two in line, coasting at 12 and 4 m/s | US1.6 |
| `pile-up` | 30 Hz | four vehicles converging on the arena centre at 8 m/s | US1.6, FR-010 |
| `thin-wall` | 30 Hz | one vehicle at top speed 30 m before the wall; `Forward` held | US1.7 |
| `rejected-commands` | 30 Hz | one vehicle; commands with unknown button bits, pitch out of range, a slot outside `input.weaponSlots` and a repeated sequence, between valid ones | US1.11 |
| `characters-and-vehicles` | 30 Hz | two characters from 015's flat-run start set on the vehicle world, and two vehicles in the arena | US7.7 |

Generated streams `vseed-001` to `vseed-050` (research R21): 8 vehicles in the arena, 20 s of simulated
time, the rate cycling 20, 30 and 60 Hz by seed, commands from `SplitMix64.At(seed, counter)`. The
coverage conditions (FR-010): every seed has at least one vehicle contact event and every vehicle travels
at least 50 m (path length); at least one seed has a vehicle with all four wheels out of contact.

## Selection configuration

`simcore/vehicles/selection.json`, validated by `SelectionConfigValidator` (FluentValidation, server
side), kept out of every stream (FR-036). Shape and rules: [contracts/selection-config.md](contracts/selection-config.md).

| Group | Fields (placeholder) |
| --- | --- |
| `acceleration` | `minFractionOfTopSpeed` 0.95, `maxOvershootFraction` 0.02, `durationSeconds` 10, `maxTiltDeg` 10, `wheelContactAfterSeconds` 1 |
| `braking` | `restSpeed` 0.1, `maxDistanceFraction` 1.20 |
| `turning` | `targetSpeed` 5, `speedBand` 0.5, `maxSeconds` 15, `maxRadiusError` 0.25 |
| `landing` | `settleSeconds` 2 |
| `collision` | `withinSteps` 3, `maxClosingSpeedFraction` 0.10 |
| `divergence` | `windowSeconds` 0.5, `maxPositionErrorMetres` 0.05, `maxOrientationErrorDeg` 1 |
| `decision` | `tieBand` 0.10 |

## Fixture engine and fault table

`FixtureEngine` (`src/MmoEngine.SimCore.Vehicles.Fixture/`) implements `IVehicleEngine` with the model in
research R22. Its configuration is the vehicle configuration plus `fault` (a fault code or `none`), read
from `simcore/vehicles/engines/fixture.json`. The fault table, `simcore/vehicles/fixtures/fault-table.json`:

| Code | Kind | Targets | Status | Reason code: expected detail |
| --- | --- | --- | --- | --- |
| `overshoot-top-speed` | model | US1.1 | Failed | `BehaviorCheckFailed`: US1 scenario 1, overshoot 3.0% |
| `weak-brake` | model | US1.2 | Failed | `BehaviorCheckFailed`: US1 scenario 2, distance above 120% |
| `under-steer` | model | US1.3 | Failed | `BehaviorCheckFailed`: US1 scenario 3, radius error above 25% |
| `sinks-on-slopes` | model | US1.4 | Failed | `BehaviorCheckFailed`: US1 scenario 4, penetration above the tolerance |
| `sticks-to-ramp` | model | US1.5 | Failed | `BehaviorCheckFailed`: US1 scenario 5, no airborne phase |
| `vehicles-pass-through` | model | US1.6 | Failed | `BehaviorCheckFailed`: US1 scenario 6, penetration above the tolerance |
| `no-continuous-collision` | model | US1.7 | Failed | `BehaviorCheckFailed`: US1 scenario 7, past the far face |
| `stale-expected-hash` | input | US1.8 | Failed | `ServerNotReproducible`: the stream and step |
| `state-survives-reload` | model | US1.9 | Failed | `ServerNotReproducible`: the stream and step of the second run |
| `hidden-accumulator` | model | US1.10 | Failed | `ResetNotEquivalent`: the stream, restart step and first differing step |
| `rejected-command-applied` | model | US1.11 | Failed | `RejectionNotNeutral`: the stream and step |
| `non-finite-velocity` | model | FR-037 row | Failed | `StepFaulted`: `NonFinite` at `vehicles[0].linearVelocity.y` |
| `coverage-parked` | model | FR-037 row | Failed | `CoverageNotMet`: the seed and condition |
| `browser-perturb-step-15` | identity-preserving, browser builds only (the manifest's `injectedFault`) | US2.9 | Diverged | `RecordDiverged`: the stream, step 15, the build, `vehicles[0].position.x` |
| `other-identity-stream` | input | FR-037 row | Failed | `StreamNotComparable`: the stream and both identities |
| `build-failed`, `build-timed-out`, `mode-not-produced`, `build-not-run` | input | FR-037 rows | NotBuilt | the matching code, the build and the reason |
| `license-gpl-transitive` | input | US2.10 | LicenseBlocked | `LicenseNotAllowed`: the component and `GPL-3.0-only` |
| `fit-*` (one per criterion a to f) | input | US6.2 | NotTested | `FitCheckFailed`: that criterion |

A lock cannot be written under the analyzer, so the `TakesLock` row is tested on the native lock test
library and the instrumented fixture assembly instead (research R16).

## Candidate statuses and results

Per candidate and build mode, exactly one status from FR-037's table, chosen by the order NotTested,
NotBuilt, LicenseBlocked, Failed, Diverged, Qualified, with every reason listed. Per required build of the
mode, one result: Match, Diverged or NotRun.

| Build mode | Required builds |
| --- | --- |
| `interp-prebuilt` | `node-interp`, `node-interp-nojit`, `chromium-worker` |
| `interp-relinked` | `node-interp-relinked`, `node-interp-relinked-nojit`, `chromium-worker-relinked` |
| `aot` | `node-aot`, `chromium-worker-aot` |

**Candidate result** (one per candidate in the selection report): identity; fit check (Rapier); build
outcomes per target and mode; license per target; behavior checks (one verdict per US1 scenario 1 to 12,
with the measured value and bound); reproducibility; server reset; coverage; per mode the status,
reasons, per-build results and reset results; divergence results; server costs; browser costs, startup
and download per mode; lock evidence; parallel-phase evidence; the vector pin (Bepu). The JSON shape is
[contracts/selection-report.md](contracts/selection-report.md).

**Divergence result** (per candidate, stream, required build and mode): for position (m), orientation (°)
and linear velocity (m/s), the maximum with its step and vehicle, and the p99; the window size in steps;
the bound verdict (`WithinBounds` or `BeyondBounds`, naming the stream, step, vehicle and value).

**Fit check result** (Rapier): six criteria a to f, each `{verdict: pass | fail, notEvaluated, evidence}`,
and the evaluation order.

**License result** (per candidate and target): `verdict` Pass or Blocked, the blocked components with
their expressions and reasons, the boundary components with their evidence, and the component count
([contracts/license-scan.md](contracts/license-scan.md)).

## Selection report

`simcore/vehicles/selection/report.json` (committed) and the `vehicle-selection` artifact: the format
version, runner class, commit, CI run, the selection configuration's fingerprint, every candidate result,
and the fixture pair's results in a separate `fixtures` member. Validated by
`simcore vehicles select validate-report`; a candidate or mode missing without a reason fails validation.

## Decision record

`simcore/vehicles/selection/decision.json`, written by `simcore vehicles decide` from a validated report
([contracts/decision-record.md](contracts/decision-record.md)):

| Field | Holds |
| --- | --- |
| `outcomes` | per mode: `BitExact`, `NonExact` or `None`, and the winner or null |
| `conditional` | true when the winners or outcomes differ between modes |
| `reasoning` | per mode: the rule branch taken, each tied set, and the comparisons made |
| `candidates` | every candidate's status and reasons per mode |
| `decidingNumbers` | per mode: browser costs, added Brotli bytes, divergence maxima |
| `provenance` | each engine's identity, the runner class, the commit and the CI run measured |
| `fingerprint` | the input fingerprint (FR-028) |
| `evidence` | server reset equivalence of the winner; parallel-phase evidence per engine; Bepu's vector pin effect |
| `provisional` | for NonExact: the flag and its exact condition text |
| `gate9` | whether the record meets gate 9's vehicle exit criterion, and why not |
| `amendmentCandidates` | components a PATCH amendment would have to name (for example foldhash, Zlib) |

State transitions of the record: none in place. A new selection run and decision produce a new record
that replaces the old one in a pull request; the staleness check fails between an input change and that
replacement.

## Reason and outcome codes

| Code set | Values |
| --- | --- |
| Status | `Qualified`, `Diverged`, `Failed`, `NotBuilt`, `NotTested`, `LicenseBlocked` |
| Reason | `FitCheckFailed`, `BuildFailed`, `BuildTimedOut`, `ModeNotProduced`, `BuildNotRun`, `LicenseNotAllowed`, `BehaviorCheckFailed`, `ServerNotReproducible`, `ResetNotEquivalent`, `RejectionNotNeutral`, `TakesLock`, `StepFaulted`, `StreamNotComparable`, `CoverageNotMet`, `RecordDiverged`, `ResetDiverged` |
| Per-build result | `Match`, `Diverged`, `NotRun` |
| Outcome | `BitExact`, `NonExact`, `None` |
| License failure | `LicenseNotAllowed`, `NoLicense`, `UnparseableExpression` |
| Build outcome | `Built`, `BuildFailed`, `BuildTimedOut` |
| Step fault (added) | `VehicleOutOfBounds`, `EngineFault` |
| Engine result (adapter) | `Ok`, `Faulted` (with the engine's code), `LoadRefused` |
