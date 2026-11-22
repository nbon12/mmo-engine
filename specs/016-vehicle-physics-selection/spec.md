# Feature Specification: Vehicle Physics Engine Selection

**Feature Branch**: `016-vehicle-physics-selection`

**Created**: 2026-11-22

**Status**: Draft

**Input**: User description: "Phase 1, sub-spec 3 (issue #12), vehicle physics slice: choose the
vehicle physics engine with an agreement-and-cost test, as the issue's "Vehicle physics" section
describes. v2 defaults to BepuPhysics2 with Jolt as the fallback; Phase 1 adds Rapier as a third
candidate. Candidates: BepuPhysics2 (Apache 2.0, C#, inside the shared core; cross-platform
determinism is only a roadmap item, so the server build is pinned to 128-bit vectors to give it its
best chance), Jolt (MIT, C++, built ourselves with CROSS_PLATFORM_DETERMINISTIC; P/Invoke on the
server, an Emscripten build in the browser) and Rapier (Apache 2.0, Rust, enhanced-determinism
feature; C API through P/Invoke on the server, a WebAssembly build in the browser; tested only if it
fits the same scenario harness cheaply). The same vehicle scenarios - terrain driving, ramps and
vehicle-to-vehicle collisions - run on the server build (.NET 10 LTS, Linux x64, in process) and the
browser build (WebAssembly in a Web Worker, called once per frame with batched inputs). An engine
qualifies only if per-step state hashes match bit for bit between the two builds on the agreement
suite. Among qualifiers, browser step cost decides first, then download size. If Bepu misses
bit-exact agreement but its vehicle corrections stay small, keeping Bepu with non-exact vehicles is a
valid outcome. The slice measures the cost per vehicle step on the server and in the browser, and the
WebAssembly download size each engine adds. It reuses feature 015's shared core
(specs/015-shared-simulation-core: the input-command, collision-data and recorded-stream formats, the
canonical state and its hash, the determinism rules and analyzer, the agreement suite and its runners
in tools/agreement/, the simcore CLI, and the SimCore CI workflow with its agreement and measure
jobs). Gate 9 exit criterion it serves: "The vehicle physics engine is chosen, and the server and
WebAssembly builds of the shared core produce identical state hashes on the agreement suite." Gate
9's decision table: "Vehicle physics engine - Sub-spec 3's agreement-and-cost test.""

## Clarifications

### Session 2026-11-22

Answered by the agent with the most defensible option. The run was unattended (Principle XIII): a
workflow started `/speckit-specify`, and the maintainer did not drive specify or clarify. Each answer
is recorded in full under Assumptions.

- Q: Is the vehicle physics engine one of the constitution's open decisions? → A: No. The open
  decisions are numbered 1 to 10, and none of them is the vehicle physics engine. It is a row of gate
  9's decision table in issue #12, whose rule is "sub-spec 3's agreement-and-cost test". This spec
  states that rule exactly (FR-026) and records the result as gate 9's decision, with its evidence.
  No amendment is needed.
- Q: When is Bepu with non-exact vehicles chosen over an engine that agrees bit for bit? → A: Never.
  Exactness comes first, because gate 9's exit criterion asks for identical state hashes. Non-exact
  Bepu is the outcome only when no candidate qualifies and Bepu's measured divergence stays inside the
  stated bounds.
- Q: How are "vehicle corrections stay small" judged before combat netcode (sub-spec 6) exists? → A:
  By a windowed divergence measurement: the browser build restarts from the server build's state at
  the start of every half-second window, as reconciliation would, and its position and orientation
  error against the server is measured at every step. The bounds are 5 cm and 1 degree. The check
  under the network profiles is deferred to sub-spec 6, so a non-exact outcome is provisional.
- Q: What does "fits the same scenario harness cheaply" mean for Rapier? → A: An automated fit check
  of six criteria (FR-030): published interfaces only, pinned unpatched sources, permissive licenses,
  the shared adapter interface unchanged, a bounded build time, and a bounded amount of
  Rapier-specific code. If any criterion fails, Rapier is recorded as not tested, with the reason, and
  the decision runs over Bepu and Jolt.
- Q: How does vehicle state join the canonical state and its hash without breaking 015's committed
  streams? → A: Through new versions: canonical state serialisation version 2 and recorded stream
  format version 2, plus a vehicle rules revision of its own. Version 1 stays as it is, so 015's 19
  committed streams, their expected hashes, `simcore/content/test-world.mmcd` and the core's rules
  revision do not change.
- Q: Which build mode's costs decide, while gate 9's WebAssembly build mode is still open? → A: The
  rule is applied in every build mode the toolchain produced. If the winners differ between modes, the
  record names the winner for each mode, and the choice is conditional on gate 9's build-mode decision.
  This spec does not settle the build mode.
- Q: Which browser cost decides? → A: The mean cost per step of a world with one driven vehicle, in
  the browser build in a Chromium Worker. A client predicts only its own vehicle (sub-spec 6), so
  that is what it pays every frame.

## User Scenarios & Testing *(mandatory)*

The users are the engineers and agents who build the continent server (sub-spec 5), combat netcode
(sub-spec 6) and the browser client (sub-spec 8), and gate 9 (sub-spec 9), which records the Phase 1
decisions. They need one vehicle physics engine whose server and browser builds give the same answer,
chosen by a rule anyone can rerun, with the evidence kept beside the choice. Players are the indirect
users: if a predicted vehicle disagrees with the server, it rubber-bands, and a vehicle fight magnifies
small differences into large ones.

The candidates are BepuPhysics2 ("Bepu"), Jolt and Rapier. A candidate's **server build** runs in
process in .NET 10 on Linux x64. Its **browser build** runs as WebAssembly with the shared core, in
Node and in a dedicated Web Worker, called through the batched step entry point.

### User Story 1 - Every candidate drives the same vehicle scenarios (Priority: P1)

An engineer runs the vehicle scenarios through a candidate's server build. Every candidate drives the
same vehicle: a four-wheeled ground vehicle with a rigid chassis and sprung wheels, set up from one
validated configuration. It drives on the same terrain and meshes, loaded from one collision data
file, with the same input commands. The vehicle accelerates, brakes, turns, crosses rolling terrain,
jumps off a ramp and lands, and collides with other vehicles head-on, from the side and from behind,
the way a ground vehicle should. A candidate's server build gives the same state after every step, in
another process and on another day. Restarting a world from any committed state and stepping on gives
the same states as a world that never restarted.

**Why this priority**: An agreement test means nothing if the vehicle does not really drive: a vehicle
that never moves agrees trivially. Every other story runs these scenarios.

**Independent Test**: For each built candidate, step every committed vehicle stream through the
server build in two separate processes (`simcore streams verify`) and compare each step's hash with
the stream's committed expected hashes. Every hash must match. Then run the behavior checks of
scenarios 1 to 7 on the server build. Each candidate must meet every stated outcome.

**Acceptance Scenarios**:

1. **Given** a vehicle at rest on flat terrain and the throttle held for 10 seconds, **When** it is
   stepped at 30 Hz, **Then** it reaches at least 95% of the configured top speed, never exceeds the
   top speed by more than 2%, stays upright (the chassis up axis within 10 degrees of world up), and
   has all four wheels in contact with the ground at every step after the first second.
2. **Given** a vehicle at the configured top speed v on flat terrain, **When** the brake is held,
   **Then** it comes to rest (speed below 0.1 m/s) within 120% of v² / (2b), where b is the configured
   braking deceleration, and stays upright.
3. **Given** a vehicle at 5 m/s on flat terrain with full steering held, **When** it is stepped,
   **Then** its heading turns in one direction only, and it completes a full turn on a circle whose
   radius is within 25% of the configured wheelbase divided by the tangent of the configured maximum
   steering angle.
4. **Given** rolling heightfield terrain, **When** a vehicle is driven across it at full throttle for
   30 seconds, crossing heightfield triangle edges and cell boundaries, **Then** its chassis and
   wheels never penetrate the terrain by more than the configured contact tolerance, and it ends
   upright.
5. **Given** a ramp in the static meshes, approached at the scenario's stated speed, **When** the
   vehicle drives up it, **Then** it leaves the ramp's lip with all four wheels out of contact, lands
   beyond the lip without penetrating the terrain or meshes by more than the contact tolerance, and
   is upright with all four wheels in contact within 2 seconds of landing.
6. **Given** two vehicles coasting into each other head-on, side-on or rear-on at the scenario's
   stated speeds, **When** they meet, **Then** a vehicle contact event names the pair at the first
   step they touch, they never interpenetrate by more than the contact tolerance, and within 3 steps
   of that contact their closing speed is at most 10% of what it was the step before.
7. **Given** a vehicle driven at top speed into a static wall thinner than its travel in one step,
   **When** it is stepped, **Then** it never passes through the wall.
8. **Given** any committed vehicle stream of a candidate, **When** it is stepped in two separate
   processes on that candidate's server build, **Then** the per-step state hashes are identical and
   equal the stream's committed expected hashes.
9. **Given** a vehicle stream made for 20, 30 or 60 Hz, **When** it is stepped twice, **Then** both
   runs give identical per-step hashes.
10. **Given** a world stepped from a stream's initial state to its last step, **When** a second world
    is loaded with the first world's committed state at step j, for every j that starts a window of
    ⌈0.5 s × step rate⌉ steps (US5), and stepped on with the same commands, **Then** its per-step
    hashes from step j + 1 onward equal the first world's (reset equivalence).
11. **Given** a vehicle command with an out-of-range value (unknown button bits, a pitch out of
    range, a weapon slot outside the configured slots, or a sequence number that does not increase),
    **When** it is offered to the step, **Then** it is rejected with 015's reason code and the
    vehicle is stepped with the neutral input, as a character is.

---

### User Story 2 - Each candidate's builds agree, or the first divergence is named (Priority: P1)

An engineer runs the selection in CI. For every candidate that built, the agreement suite runs the
candidate's committed vehicle streams and generated streams from 50 seeds through its server build and
its browser build. The browser build runs in Node with the jiterpreter on and off, and inside a
dedicated Web Worker in headless Chromium, in every build mode the toolchain produced. Every step's
record, its state and hash, is compared with the server build's. A candidate whose records all match,
and whose browser build also restarts exactly from a committed state, qualifies. A candidate that
differs at any step is reported with the stream, the first divergent step, the runtime, the build mode
and the fields that differ. One candidate's failure never stops the others from being evaluated.

**Why this priority**: Qualification is the first test of the rule. An engine whose builds disagree
makes every predicted vehicle drift.

**Independent Test**: Run the selection run of the `SimCore` workflow. Its report must list, for each
candidate, build mode and runtime, one agreement status: Qualified, Diverged (with the stream, step
and fields), Failed, NotBuilt, NotTested or LicenseBlocked. Then a deliberate one-ulp change, the
stream runner's perturbation of the first vehicle's `position.x` at step 15, must in a candidate's
browser build agree with the server's records before that step and differ at it. And
`simcore agreement compare`, given a candidate's records perturbed the same way, must exit 1, naming
step 15 as the first divergent step and `vehicles[0].position.x` as the field that differs.

**Acceptance Scenarios**:

1. **Given** a built candidate's committed vehicle streams, **When** the agreement suite runs, **Then**
   each runtime's (`node-interp`, `node-interp-nojit` and the Chromium Worker) per-step records are
   compared byte for byte with the server build's, in every build mode produced.
2. **Given** generated vehicle streams from 50 fixed seeds (`--seeds 1-50`), with several vehicles
   driving seeded throttle, brake and steering on an enclosed course so that they collide, **When**
   the agreement suite runs, **Then** every step's record is compared across the builds in the same
   way.
3. **Given** a candidate whose browser build differs from its server build at one step, **When** the
   suite compares them, **Then** the candidate's status in that build mode is Diverged, the report
   names the stream, the first divergent step, the runtime and each state field that differs with
   both builds' values in exact bit form, and the suite goes on to evaluate the other candidates.
4. **Given** a candidate's browser build loaded with its server build's committed state at a step
   and stepped on with the same commands, **When** its records are compared, **Then** they equal the
   server build's from the next step on; a candidate for which they do not is not Qualified.
5. **Given** a frame that queues several commands for a driven vehicle, **When** the batched step
   entry point is called once, on the server build (`BatchStepper.StepQueued`) or through the
   browser build's `StepQueued` export in a dedicated Worker, **Then** it steps each command in order
   and returns one state and hash per step, the same as stepping them one at a time.
6. **Given** 015's 19 committed streams, **When** the agreement suite runs after this feature,
   **Then** the stream files are byte-identical to the ones on `main` before this feature, and every
   required build still reports Match.
7. **Given** a vehicle stream recorded with another engine, another engine version or build setting,
   or another vehicle rules revision, **When** the suite loads it, **Then** it reports NotComparable,
   naming both values, and never Diverged.
8. **Given** a candidate whose server or browser build cannot be produced within its bound, **When**
   the selection runs, **Then** that candidate is NotBuilt, with the failing step and its output's
   last lines, and every other candidate is still evaluated.

---

### User Story 3 - The engine is chosen by the stated rule and recorded as gate 9's decision (Priority: P1)

The selection run writes a validated report. The decision command reads it, applies the decision rule
(FR-026) and writes the decision record: the chosen engine, or none, why, and the evidence behind it.
An agent commits the record. Gate 9's "Vehicle physics engine" row reads it. From then on, CI gates
every change to the vehicle code on the chosen engine's vehicle streams, and refuses a record that no
longer describes what is in the repository.

**Why this priority**: The choice is this slice's deliverable and one of gate 9's exit criteria.

**Independent Test**: Run the decision command on a committed set of fixture reports that covers every
branch of the rule (scenarios 1 to 6) and compare each record with its expected record. Then run it on
the committed selection report and require the committed decision record byte for byte. Then change
one input the record fingerprints and require the staleness check to fail, naming it.

**Acceptance Scenarios**:

1. **Given** a report in which exactly one candidate is Qualified in every build mode, **When** the
   decision runs, **Then** that candidate is chosen with the outcome BitExact.
2. **Given** several Qualified candidates, **When** the decision runs, **Then** the one with the lowest
   browser cost per step (one driven vehicle, Chromium Worker, mean) is chosen, provided its cost is
   more than 10% below every other qualifier's.
3. **Given** two Qualified candidates whose browser costs are within 10% of each other, **When** the
   decision runs, **Then** the one that adds fewer Brotli-compressed download bytes wins, provided it
   adds more than 10% fewer. **Given** download sizes also within 10% of each other, **Then** the one
   earlier in the order Bepu, Jolt, Rapier wins.
4. **Given** a report with no Qualified candidate, in which Bepu passed every check except cross-build
   agreement and reset equivalence, and its divergence stayed within bounds (US5) on every stream,
   runtime and build mode,
   **When** the decision runs, **Then** Bepu is chosen with the outcome NonExact, marked provisional
   until sub-spec 6's correction measurements confirm it.
5. **Given** a report with no Qualified candidate, in which Bepu's divergence exceeded a bound or Bepu
   failed another check, **When** the decision runs, **Then** the outcome is None, the record gives
   each candidate's status and reason, and it states that gate 9's vehicle exit criterion is not met.
6. **Given** a report in which the rule picks different winners in different build modes, **When** the
   decision runs, **Then** the record names the winner in each mode and marks the choice as
   conditional on gate 9's WebAssembly build-mode decision.
7. **Given** the same report, **When** the decision runs twice, **Then** both records are identical
   byte for byte.
8. **Given** a committed decision record, **When** the engine versions or build settings, the vehicle
   rules revision, the vehicle configuration or the committed vehicle streams differ from the ones the
   record fingerprints, **Then** the staleness check fails and names each input that changed.
9. **Given** a committed record with the outcome BitExact, **When** a pull request changes the core,
   the vehicle code, the vehicle streams or an engine build, **Then** the `SimCore` workflow's vehicle
   agreement job runs the chosen engine's vehicle streams across the required builds, fails on any
   divergent step, and gates the pull request the way the `agreement` job does. **Given** the outcome
   NonExact, **Then** the job instead fails when the server build misses a committed hash or the
   browser build's divergence exceeds a bound. **Given** the outcome None, **Then** the job runs no
   vehicle gate and reports the record's outcome in the job summary.

---

### User Story 4 - Each candidate's cost and download size are measured (Priority: P2)

The selection run measures every built candidate in the same job on the same runner class: the server
build's cost per vehicle step, the browser build's cost per step, its startup time, and the download
size it adds to the shared core's browser build. Each is measured in every build mode the toolchain
produced and recorded as a measurement with its runner class. A candidate or mode that could not be
measured is named, with the reason. Steady-state vehicle steps allocate no managed memory.

**Why this priority**: The costs break ties among qualifiers, and the gate-9 sweep needs the server's
per-vehicle cost. Neither is needed to decide whether a candidate qualifies.

**Independent Test**: Run the selection run on a CI runner and check that its report holds every
listed measurement for every built candidate, with units, the runner class, the build mode and the
engine's identity (`simcore measure validate-report`). Check that the zero-allocation tests pass for
every candidate's vehicle step.

**Acceptance Scenarios**:

1. **Given** each built candidate's server build, **When** the vehicle benchmarks run, **Then** they
   report the mean and p99 cost of one world step and of one vehicle step with 1, 16, 64 and 256
   vehicles, labeled as measurements with the runner class. Bepu's are measured with the server pinned
   to 128-bit vectors, and the report says so.
2. **Given** each built candidate's browser build, in each build mode produced, **When** the
   measurement runs, **Then** the report records the mean and p99 cost per step with 1, 16 and 64
   vehicles, in Node and in a Chromium Worker, measured the way 015 measures step cost, and the time to
   the first completed vehicle step over 10 cold starts.
3. **Given** each built candidate's browser build in each mode, **When** the download size is
   measured, **Then** the report records the bytes it adds (raw, Brotli and gzip) to the shared
   core's own browser build in the same mode.
4. **Given** warmed-up vehicle steps for each candidate, **When** a further 1,000 steps run, **Then**
   they allocate zero bytes of managed memory.
5. **Given** a candidate or build mode that could not be built or measured, **When** the report is
   written, **Then** it names the candidate and mode and gives the reason, and validation fails on a
   candidate or mode that is missing without one.

---

### User Story 5 - Bepu's cross-build divergence is measured (Priority: P2)

Bepu promises determinism only on the same machine and build. Whether or not it agrees bit for bit,
the selection measures how far its browser build drifts from its server build over the span that
client reconciliation would replay. The browser build restarts from the server's state at the start
of every window, steps the window with the same commands, and its position, orientation and velocity
errors against the server are recorded at every step. The bounds decide whether keeping Bepu with
non-exact vehicles is a valid outcome.

**Why this priority**: It decides the issue's fallback outcome. It matters only if no candidate
qualifies, but it must be measured before the decision runs.

**Independent Test**: Run the divergence measurement on a committed pair of record sets whose
differences are known, and check the reported errors. Then run it on Bepu's vehicle streams in every
runtime and build mode, and check that the report holds each stream's maxima and the bound verdict.

**Acceptance Scenarios**:

1. **Given** Bepu's committed and generated vehicle streams, **When** the divergence measurement runs,
   **Then** the browser build is loaded with the server build's state at the start of every window of
   ⌈0.5 s × step rate⌉ steps (15 at 30 Hz) and stepped through the window with the stream's commands,
   and for every vehicle and every step it records the chassis position error (metres), orientation
   error (degrees of the rotation between the two orientations) and linear velocity error (m/s)
   against the server build at the same step.
2. **Given** the measurement's results, **When** the report is written, **Then** it gives, for each
   stream, runtime and build mode, the maximum and p99 of each error, and the step and vehicle of
   each maximum.
3. **Given** two record sets whose only difference is a known offset of one vehicle (0.03 m in
   position and 0.5 degrees in orientation), **When** the divergence is measured, **Then** the reported
   errors equal those offsets within 1e-4 m and 1e-3 degrees.
4. **Given** results in which every window's maximum position error is at most 0.05 m and maximum
   orientation error at most 1 degree, on every stream and runtime of a build mode, **When** the
   bounds are applied, **Then** Bepu is within bounds in that mode; **Given** one step beyond either
   bound, **Then** Bepu is beyond bounds in that mode, and the report names the stream, step, vehicle
   and value.
5. **Given** a candidate whose browser and server records are identical, **When** its divergence is
   measured, **Then** every error is exactly 0.

---

### User Story 6 - Rapier joins only if it fits the harness cheaply (Priority: P3)

Rapier is a third candidate, tested only if it fits the same scenario harness cheaply. An automated fit
check evaluates it against six stated criteria. If they all pass, Rapier is a full candidate. If any
fails, Rapier is recorded as NotTested with the failing criterion, and the decision is made over Bepu
and Jolt.

**Why this priority**: Rapier adds a candidate, not a capability. The decision can be made without it.

**Independent Test**: Run the fit check on Rapier's pinned release and check that the report records a
verdict and evidence for every criterion. Then run it on committed fixtures in which one criterion
fails, and check that the result is NotTested, naming that criterion.

**Acceptance Scenarios**:

1. **Given** Rapier's pinned release, **When** the fit check runs, **Then** it records a pass or fail,
   with evidence, for each criterion of FR-030.
2. **Given** a fit check in which any criterion fails, **When** the selection runs, **Then** Rapier's
   status is NotTested with that criterion as the reason, the other candidates are decided as usual,
   and the run does not fail.
3. **Given** a fit check in which every criterion passes, **When** the selection runs, **Then** Rapier
   runs every scenario, agreement check and measurement that Bepu and Jolt run.

---

### User Story 7 - Vehicle state joins the canonical state under new versions (Priority: P3)

Vehicles enter the canonical state in a new state serialisation version and a new recorded stream
format version, each pinned by golden fixtures. Version 1 is untouched, so every stream and fixture
015 committed still reads, runs and hashes as before. A file in an unknown version, or one that is
malformed, is refused with a clear error and never half-read.

**Why this priority**: Every other story needs the formats, but their versioning and refusal paths
can follow the first working vehicle, as 015's did.

**Independent Test**: Encode and decode every new golden fixture and compare the bytes. Decode every
015 golden fixture and run every 015 committed stream and compare the results with `main` before this
feature. Then feed truncated, oversized, inconsistent and unknown-version vehicle streams and states to
the decoders and check each refusal.

**Acceptance Scenarios**:

1. **Given** each new valid golden fixture (state version 2, stream version 2), **When** it is decoded
   and re-encoded, **Then** the bytes are identical to the fixture.
2. **Given** a version 1 stream, **When** it is decoded and run, **Then** it runs under state
   serialisation version 1, and its step records and hashes are the same as before this feature.
3. **Given** a stream or state in a version this build does not support, **When** it is decoded,
   **Then** it is refused with an error naming the version found and the versions supported.
4. **Given** a truncated, oversized or internally inconsistent vehicle stream or state (for example a
   vehicle count that disagrees with the initial state, or vehicle IDs that do not ascend), **When** it
   is decoded, **Then** it is refused with an error naming the offset or field, and no partial result
   is returned.
5. **Given** a vehicle stream whose collision data, matched by checksum, is not supplied, **When** it
   is opened, **Then** it is refused before any step runs.
6. **Given** a vehicle field that would be negative zero after a step, **When** the state is
   committed, **Then** it is stored and hashed as positive zero; **Given** a field the engine left as
   NaN or infinity, **Then** the step refuses to complete and names the field, such as
   `vehicles[0].angularVelocity.y`.

### Edge Cases

- A vehicle that rolls onto its roof or side stays a valid state and keeps stepping; its records are
  compared like any other.
- The vehicle course is enclosed by walls. A vehicle whose chassis leaves the playable area anyway
  faults the step with an out-of-bounds fault naming the vehicle, rather than stepping in undefined
  terrain.
- Vehicles at rest in contact with each other or the ground keep being simulated. Sleeping is off in
  every candidate, so whether a body sleeps is never hidden state.
- Engine-internal state that affects the next step (contact caches, warm-starting impulses) is either
  part of the canonical state or switched off. Reset equivalence (US1 scenario 10) checks it either
  way.
- An orientation q and its negation −q are the same rotation. The canonical state stores the engine's
  own bits, so a build that flips the sign diverges, and the divergence measurement treats the two as
  equal.
- Several contacts that begin in the same step are listed in a fixed order (ascending pair of
  vehicle IDs), whatever order the engine reports them in.
- A heightfield triangle edge or vertex under a wheel, and a degenerate (zero-area) mesh triangle:
  every candidate collides with exactly the triangles the core's collision world uses, with
  degenerate ones dropped.
- An engine version bump changes the engine's identity, so every stream recorded with the old version
  is NotComparable until it is recorded again with a stated reason.
- An engine that starts worker threads: every candidate steps on the calling thread only, with no
  engine job system, because gameplay code takes no locks (Principle II).
- Region merges, splits, portals and tick overruns: N/A in Phase 1, which runs one region. The
  decision record notes, from the reset-equivalence results, whether a candidate's vehicle can be
  loaded exactly into another world, which Phase 2's merge and split callbacks will need. A live
  change of step rate is not supported, as in 015.

## Requirements *(mandatory)*

### Functional Requirements

**Candidates and builds**

- **FR-001**: The selection MUST evaluate three candidates, each at an exactly pinned release: Bepu,
  in process on the same .NET runtime as the core in both builds, with the server build's vectors
  pinned to 128 bits so that its vector width matches WebAssembly's; Jolt, built from pinned sources
  with its cross-platform determinism option, as a native library called in process on the server and
  as a WebAssembly build in the browser; and Rapier, subject to the fit check (FR-030), built from
  pinned sources with its enhanced-determinism feature, through its C API in process on the server and
  as a WebAssembly build in the browser.
- **FR-002**: Every candidate's builds MUST be made with determinism-preserving settings, recorded with
  its identity: no fast-math, no fused multiply-add contraction, the engine stepped on the calling
  thread only, and sleeping off. The engine's sources, toolchains and build settings MUST be pinned by
  version and checksum, and any change to them changes the engine's identity.
- **FR-003**: Every candidate MUST be driven through one engine-neutral adapter interface by one
  scenario harness. The scenarios, formats, runners and other candidates' adapters MUST NOT contain
  engine-specific branches.
- **FR-004**: The determinism analyzer MUST stay attached to the core assembly with no rule relaxed.
  The engine adapters MUST live outside that assembly, because each breaks a rule by construction
  (native calls for Jolt and Rapier, `System.Numerics` vectors for Bepu). The agreement suite is their
  determinism check.

**Vehicle model, content and inputs**

- **FR-005**: Every candidate MUST simulate the same vehicle model, a four-wheeled ground vehicle with a
  rigid chassis and sprung wheels, from one validated configuration: mass, chassis dimensions, wheel
  positions and radius, suspension stiffness, damping and travel, tire grip, drive force, top speed,
  braking deceleration, maximum steering angle and steering rate, solver substeps and the contact
  tolerance. An invalid vehicle configuration MUST be refused with an error naming each invalid
  field, on the server and in the browser build, as 015's FR-029 requires for the rest of the
  configuration. A configuration with no vehicle section stays valid and means no vehicles.
- **FR-006**: Every candidate's static collision MUST be built from the same collision data file in
  015's format (version 1), with exactly the triangles the core's collision world uses: the same
  heightfield triangle split and vertices, and degenerate triangles dropped.
- **FR-007**: The vehicle scenarios MUST use a committed vehicle test world, generated by a
  deterministic tool from a fixed seed, separate from `simcore/content/test-world.mmcd`: an enclosed
  course with flat ground, rolling terrain, a ramp, a thin wall and an open arena for collisions.
- **FR-008**: Each vehicle MUST be driven by its own input commands in 015's input command format,
  version 1. The vehicle step reads `Forward` as throttle, `Back` as brake and then reverse once
  stopped, and `Left` and `Right` as steering at the configured rate; other buttons have no effect on
  the vehicle. Every vehicle command MUST be validated by 015's rules (FR-019) before it affects state.
- **FR-009**: The vehicle step MUST run at the world's configured step rate (20 to 60 Hz) with the
  configured fixed number of substeps, and MUST NOT accept a measured frame time.

**Scenarios and behavior**

- **FR-010**: Each candidate MUST have committed named vehicle streams for at least these scenarios:
  flat acceleration, braking to a stop, a turning circle, rolling terrain, a ramp jump, head-on,
  side and rear collisions, a pile-up of at least four vehicles, a thin wall at top speed, and flat
  acceleration at 20 and 60 Hz. The agreement suite MUST also generate streams from 50 fixed seeds.
- **FR-011**: The behavior outcomes of US1 scenarios 1 to 7 MUST be checked on each candidate's server
  build, with penetration measured against the core's collision world, so that the check does not
  depend on the engine under test. A candidate that misses an outcome is Failed with that reason.

**State, hash and formats**

- **FR-012**: Canonical state serialisation version 2 MUST hold version 1's content followed by the
  vehicles, in ascending vehicle ID, and the step's vehicle events. Each vehicle holds its ID, chassis
  position, orientation, linear and angular velocity, per-wheel state (steering angle, spin, suspension
  compression and contact) and the last input sequence applied. The vehicle events are the vehicle
  contacts that began in the step, listed by ascending pair of vehicle IDs, and the vehicle input
  rejections. The state hash MUST cover all of it, with 015's hash, negative-zero canonicalisation and
  non-finite refusal (015's FR-016 and FR-017).
- **FR-013**: The canonical state MUST be complete: a world loaded with any committed state and
  stepped on MUST give the same per-step hashes as the world that produced that state (reset
  equivalence), on the server build and on the browser build.
- **FR-014**: Recorded stream format version 2 MUST carry the vehicle configuration, the initial state
  in serialisation version 2, commands addressed to vehicles as well as characters, the engine's
  identity (engine, pinned release and build-settings fingerprint) and the vehicle rules revision, as
  well as everything version 1 carries. A stream whose engine identity or vehicle rules revision
  differs from the build's MUST be NotComparable, naming both values.
- **FR-015**: Version 1 MUST keep working unchanged. The decoders MUST accept stream versions 1 and 2,
  and run a version 1 stream under state serialisation version 1. 015's 19 committed streams,
  `simcore/content/test-world.mmcd` and the core's rules revision MUST NOT change. A change to vehicle
  rules MUST bump the vehicle rules revision instead.
- **FR-016**: The new versions MUST have committed golden fixtures that round-trip to identical bytes,
  and refusal fixtures for an unknown version, truncation, oversize and inconsistency. 015's refusal
  fixture that used stream version 2 as its unknown version MUST move to a version that is still
  unknown, so that it keeps testing a refusal.
- **FR-017**: Each candidate's committed vehicle streams MUST carry expected hashes recorded by that
  candidate's server build, and MUST change only through `simcore streams record --reason` (015's
  FR-028).

**Agreement and qualification**

- **FR-018**: The agreement suite MUST run each built candidate's committed and generated vehicle
  streams through its server build and its browser build in Node with the jiterpreter on and off and
  in a dedicated Web Worker in headless Chromium, in each build mode produced, and MUST compare every
  step's record byte for byte, reporting the first divergent step of each stream with each differing
  field in exact bit form (015's FR-026).
- **FR-019**: A candidate MUST be Qualified in a build mode only if, in that mode: it built for both
  targets; its licenses passed (FR-031); its behavior outcomes passed (FR-011); its server build
  reproduced every committed hash in two processes; every vehicle stream matched on every required
  runtime; and reset equivalence held on both builds. Otherwise its status MUST be one of Diverged,
  Failed (with the reason), NotBuilt, NotTested or LicenseBlocked.
- **FR-020**: The batched step entry point (`BatchStepper`, and the browser build's `StepQueued`
  export) MUST step queued commands for a driven vehicle and return each step's state and hash, the
  same as stepping them one at a time.
- **FR-021**: A candidate that fails to build, run or measure MUST be recorded with its status and
  reason, and MUST NOT stop the evaluation of the other candidates.

**Divergence measurement**

- **FR-022**: The selection MUST measure each built candidate's windowed divergence (US5): the browser
  build loaded with the server build's state at the start of every window of ⌈0.5 s × step rate⌉
  steps, and the position, orientation and linear velocity error of every vehicle at every step,
  reported as the maximum and p99 per stream, runtime and build mode. The window length (0.5 s) and
  the bounds, a 0.05 m position error and a 1 degree orientation error, MUST be validated
  configuration.

**Cost**

- **FR-023**: Each built candidate's server build MUST have benchmarks reporting the mean and p99 cost
  of one world step and of one vehicle step with 1, 16, 64 and 256 vehicles, labeled as measurements
  with the runner class.
- **FR-024**: The selection MUST record, for each built candidate and build mode, the browser build's
  mean and p99 cost per step with 1, 16 and 64 vehicles in Node and in a Chromium Worker, the time to
  the first completed vehicle step over 10 cold starts, and the download bytes (raw, Brotli and gzip)
  it adds to the shared core's own browser build in the same mode. A mode or candidate that could not
  be measured MUST be named with the reason.
- **FR-025**: After warm-up, every candidate's vehicle step MUST allocate no managed memory, verified by
  zero-allocation tests in CI. All candidates MUST be measured in one job run on one runner class: the
  selection run, in the `SimCore` workflow, on every pull request that changes the vehicle code, a
  candidate's build or the vehicle streams, and on demand.

**Decision**

- **FR-026**: The decision rule MUST be applied separately in each build mode produced, to that mode's
  statuses and numbers in the validated selection report:
  1. If one or more candidates are Qualified, the tied set is every qualifier whose browser cost per
     step (one driven vehicle, Chromium Worker, mean) is within 10% of the lowest. If it holds one
     candidate, that candidate wins. Otherwise it narrows to the qualifiers in it whose added Brotli
     download bytes are within 10% of the smallest among them, and the earliest of those in the order
     Bepu, Jolt, Rapier wins. The outcome is BitExact.
  2. Otherwise, if Bepu passed every check of FR-019 except cross-build agreement and reset
     equivalence, and its divergence (FR-022) is within bounds on every stream and runtime, Bepu wins
     with the outcome NonExact, provisional until sub-spec 6 confirms the correction distances under
     the network profiles.
  3. Otherwise the outcome is None.

  If the winner or outcome differs between build modes, the decision MUST name the winner in each mode
  and be conditional on gate 9's WebAssembly build-mode decision.
- **FR-027**: The decision command MUST write a decision record that holds the outcome and winner (per
  mode where they differ), the rule applied and each step of its reasoning, every candidate's status
  and reason per build mode, the deciding numbers (browser costs, download sizes, divergence maxima),
  each engine's identity, the runner class, the commit and CI run measured, and a fingerprint of the
  inputs (FR-028). The same report MUST always give the same record, byte for byte. The record is
  committed and is gate 9's decision for "Vehicle physics engine".
- **FR-028**: A staleness check MUST fail when the committed record's fingerprint no longer matches the
  repository: the engines' identities, the vehicle rules revision, the vehicle configuration or the
  committed vehicle streams. It MUST name each input that changed.
- **FR-029**: Once a record is committed, a vehicle agreement job in the `SimCore` workflow MUST run on
  every pull request that changes the core, the vehicle code, the vehicle streams, the engine builds or
  the record. It MUST fail on any divergent step of the chosen engine's vehicle streams (BitExact), or
  on a missed committed hash or a bound exceeded (NonExact), and gate the pull request as the
  `agreement` job does. A conditional record gates each build mode's winner in that mode. With the
  outcome None it MUST gate nothing and report the outcome. It MUST have its own time bound, so that
  015's `agreement` job and its 10-minute bound are unchanged.

**Rapier fit**

- **FR-030**: The fit check MUST evaluate Rapier against these criteria and record each verdict with
  its evidence: (a) its published C API and WebAssembly build cover every operation of the adapter
  interface, with no patch to Rapier's sources; (b) it builds from pinned sources with its
  enhanced-determinism feature and locked dependencies; (c) its license scan passes (FR-031); (d) its
  adapter implements the shared interface with no change to the scenarios, formats, runners or other
  adapters; (e) each of its two builds completes in under 15 minutes on the CI runner class, and the
  flat-acceleration stream runs to completion on both builds; (f) the Rapier-specific code written for
  it (shim, bindings and build scripts) is at most 1,500 non-blank lines. Rapier is a full candidate
  only if all six pass, and NotTested otherwise.

**Licensing**

- **FR-031**: Every component that a candidate's builds link or ship, its transitive dependencies and
  the toolchain runtime libraries included, MUST be MIT or Apache-2.0 (Apache-2.0 with the LLVM
  exception counts), or an approved exception of Principle VII, checked by an automated license scan.
  A candidate with any other component MUST be LicenseBlocked, naming the component.

### Key Entities

- **Candidate**: one engine under evaluation (Bepu, Jolt or Rapier), with its identity: the engine,
  its pinned release, and a fingerprint of its sources, toolchains and build settings.
- **Vehicle configuration**: the vehicle model's parameters, the divergence bounds and window, and the
  substeps, validated before any step.
- **Vehicle state**: one vehicle's ID, chassis position, orientation, linear and angular velocity,
  per-wheel state and last input sequence, inside canonical state version 2.
- **Vehicle stream**: a recorded stream in format version 2 for one candidate: header, configuration,
  engine identity, vehicle rules revision, initial state, commands for vehicles and characters, and the
  expected hash after each step.
- **Agreement status**: per candidate, build mode and runtime: Qualified, Diverged (stream, step,
  fields), Failed (reason), NotBuilt, NotTested or LicenseBlocked.
- **Divergence result**: per candidate, stream, runtime and build mode: the maximum and p99 position,
  orientation and velocity errors, where each maximum occurred, and the bound verdict.
- **Selection report**: the validated output of one selection run: every candidate's statuses, behavior
  results, costs, download sizes, startup times, divergence results and fit-check verdicts, with the
  runner class and the commit.
- **Fit check result**: Rapier's six criteria, each with a verdict and its evidence.
- **Decision record**: the outcome (BitExact, NonExact or None), the winner per build mode, the
  reasoning, the evidence and the input fingerprint; gate 9's vehicle-physics decision.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: The shared simulation core and its tooling: the core's vehicle state and
  formats, one adapter per candidate, the candidates' native and WebAssembly builds, the `simcore`
  commands, the agreement runners and the `SimCore` workflow. No running service, web surface or
  network endpoint is added.
- **State ownership**: The core still owns no state between steps. A vehicle world, the engine's own
  world included, has one writer: in the continent server (sub-spec 5) the region that owns the
  vehicles, and on the client the Worker that owns the predicted copy. Engines step on the calling
  thread only.
- **Interaction class**: Vehicle-to-vehicle and vehicle-to-terrain collisions are instant
  interactions resolved by the owning region (constitution, Interaction classes). No claim, claim
  radius, portal or bridging change: the ground vehicle's claim radius stays the simulator's.
- **Authority and disclosure**: The server stays authoritative (Principle IV); the client runs the
  same engine to predict its own vehicle. Every vehicle command is validated by 015's rules before it
  affects state (FR-008). Wire size and rate limits stay with sub-specs 4 and 7. The core discloses
  nothing.
- **Persistence and write policy**: N/A. Nothing is persisted; streams, collision data, reports and
  the decision record are committed files.
- **Contracts**: Canonical state serialisation version 2 and recorded stream format version 2, with
  golden fixtures; version 1 unchanged (FR-015). The input command and collision data formats stay at
  version 1. The decision record and the selection report have documented, validated shapes.
- **Performance and evidence**: The vehicle step is tick-path code: zero-allocation tests and
  benchmarks are required (FR-023, FR-025). Every cost, size and divergence figure is a measurement
  with its runner class, build mode and engine identity; plan estimates stay labeled as estimates.
  Measured numbers decide the engine but never fail CI. Vehicle values, the divergence bounds and the
  window are placeholders in validated configuration; this feature sets no tuning value of Principle
  VI.
- **Security and abuse controls**: Commands, streams, states and collision files are untrusted and
  decoded with bounded counts and no partial results (015's FR-020). The native boundary checks every
  length and count before it crosses. Engine sources and toolchains are pinned by version and
  checksum (FR-002). No authentication or authorization: these are a library and tools.
- **Observability**: N/A for the library: the vehicle step does no I/O and logs nothing. The `simcore`
  commands log Serilog compact JSON on stderr. The selection job writes its report and the decision
  as files, shows them in the job log and summary, and uploads them as artifacts.
- **Licensing and design sources**: The candidates are Apache-2.0 (Bepu, Rapier) and MIT (Jolt). Every
  linked or shipped component is license-scanned (FR-031). A candidate that needs a package outside
  MIT and Apache-2.0 is LicenseBlocked, and adding the package would take a PATCH amendment, which no
  automation makes. Design sources (Bepu's documentation and roadmap, Jolt's determinism
  documentation, Rapier's determinism guide, and the vehicle-model references) are recorded in
  `research.md`.
- **Accessibility**: N/A. No web surface.
- **Quality gates**: 95% line coverage for changed `src/` files, the C++ and Rust shims included, each
  measured with its own toolchain's coverage tool. Theories cover each candidate, each
  scenario, each step rate, each runtime, each build mode, each status and each branch of the decision
  rule. Tests are in process except the fresh-process runs, the engine builds and the WebAssembly
  runs, which use unique temporary directories and dynamic ports, so they are safe in parallel and
  against leftovers. Zero-allocation tests cover every candidate's vehicle step.
- **Executable and living spec**: Every acceptance scenario maps to an automated test named in
  `tasks.md`, and every scenario, requirement and success criterion this spec states as current
  behavior has a test that passes on `main` at merge. The decision record and the selection report it
  was made from are committed in the same pull request, and the staleness check (FR-028) keeps them
  true afterwards. Where a 015 statement becomes incomplete (its state hash covers characters, weapons
  and projectiles; its stream decoder supports version 1), 015's `spec.md` is updated in the same pull
  request.
- **Spec independence and parallelism**: One documented hard dependency, already met: 015's shared
  core, formats, agreement runners, `simcore` CLI and `SimCore` workflow, all merged on `main`. No
  sibling spec of sub-specs 4 to 9 needs to land first, and none waits on this one except gate 9's
  decision table.
- **Open decisions**: None is settled. The vehicle physics engine is not on the constitution's list; it
  is gate 9's decision, made by FR-026. Open decision 3 (tick rate) stays open: vehicles step at the
  configured rate, tested at 20, 30 and 60 Hz. Open decision 5 (hit detection) is untouched:
  projectiles do not hit vehicles in this slice. Open decisions 4 and 9 are untouched. Gate 9's other
  open choices stay open: the WebAssembly build mode (the rule runs per mode, FR-026) and parallel
  phases (engines run single-threaded).
- **Autonomous delivery (XIII)**: Every check is an automated test or CI job, the decision is computed
  by a command from CI evidence and committed by an agent, and nothing is assigned to a person. Every
  ambiguity is recorded below as an assumption. A license that would need an amendment is reported as
  LicenseBlocked, and the selection goes on without that candidate.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every built candidate is evaluated on at least 12 named vehicle streams and 50 seeded
  streams, on three required runtimes and in every build mode produced, and ends each mode with
  exactly one status.
- **SC-002**: 015's 19 committed streams stay byte-identical and Match in every required build, and
  015's `agreement` job stays within its 10-minute bound.
- **SC-003**: The decision rule gives its stated outcome on every branch's fixture report, and the
  committed decision record is reproduced byte for byte from the committed selection report.
- **SC-004**: A deliberate one-ulp change to a vehicle field in one build is reported at its first
  divergent step, never only at the end of a stream.
- **SC-005**: Steady-state vehicle steps of every candidate allocate zero bytes of managed memory after
  warm-up.
- **SC-006**: The selection report records, for every built candidate, the server cost per vehicle step
  at 4 vehicle counts, the browser cost per step at 3 vehicle counts in 2 runtimes per build mode, the
  startup time and the download bytes added, each with its runner class.
- **SC-007**: The chosen engine's vehicle agreement job completes in under 15 minutes, and the whole
  selection run in under 120 minutes, on a CI runner: each job's time bound fails a run that does not.
- **SC-008**: A decision record is committed, its staleness check passes on `main`, and it gives gate 9
  an outcome for the vehicle physics engine: an engine that agrees bit for bit, Bepu with non-exact
  vehicles, or none, each with its evidence.

## Assumptions

- **Not an open decision.** The constitution's Open decisions list (1 to 10) was checked: section and
  claim sizes, fight size, tick rate, browser transport, hit detection, designed boundaries, Orleans,
  Nakama, anti-cheat and game-database hosting. The vehicle physics engine is not on it. Issue #12
  lists it in gate 9's decision table with the rule "sub-spec 3's agreement-and-cost test", so this
  spec defines that rule (FR-026), and the result is recorded as gate 9's decision with its evidence.
  No amendment is needed or made. `docs/roadmap/` does not exist; issue #12 is the Phase 1 source.
- **Exactness first.** The issue orders the rule as "qualifies only if the hashes match", then "among
  qualifiers, browser step cost, then download size", and calls non-exact Bepu "a valid outcome". Gate
  9's exit criterion asks for identical state hashes, so a bit-exact qualifier always wins over
  non-exact Bepu, and non-exact Bepu is chosen only when no candidate qualifies. With a NonExact
  outcome the agreement suite still requires identical hashes for characters, weapons and projectiles,
  and the record states that the gate's criterion then holds for them and not for vehicles. Reading
  the issue the other way, letting non-exact Bepu compete with qualifiers on cost, is the rejected
  alternative.
- **Corrections, before sub-spec 6.** No reconciliation or correction-distance logging exists yet, so
  "vehicle corrections stay small" is measured as windowed divergence (US5, FR-022). The window,
  ⌈0.5 s × step rate⌉ steps, covers the reconciliation span of the worst committed network profile:
  Poor's 250 ms round trip, plus two standard deviations of its 50 ms jitter, plus the 1–2 tick starting
  input buffer, about 417 ms at 30 Hz. The bounds, 0.05 m and 1 degree, are under a tenth of one
  step's travel at the placeholder top speed (20 m/s is 0.67 m per step at 30 Hz), small enough for
  sub-spec 6's smoothing to hide. They are placeholders in configuration. The check under the network
  profiles themselves, with real reconciliation, is deferred to sub-spec 6, so a NonExact decision is
  marked provisional and sub-spec 6's correction-distance baselines confirm or overturn it.
- **Rapier's fit, as a time box.** "Fits the same scenario harness cheaply" is the six criteria of
  FR-030. Rapier's work is the last candidate phase of this feature's tasks, and its exit is the fit
  check: when a criterion fails, Rapier is recorded as NotTested with that criterion and the feature
  goes on without it, rather than spending more effort. The 1,500-line cap stands in for effort
  because it can be counted: it is about the size we expect one engine's shim and bindings to need
  (an estimate, not a measurement), so a Rapier that needs much more than Jolt does is not cheap.
- **Versions, not rewrites.** Vehicle state joins the canonical state through state serialisation
  version 2 and stream format version 2 (FR-012, FR-014), and the decoders keep version 1 (FR-015).
  The core's `SimCoreVersion.RulesRevision` names the character, weapon and projectile rules, which do
  not change, so it stays at 1 and 015's streams stay byte-identical with their committed hashes. The
  vehicle rules get their own revision, and the engine's identity sits beside it in the stream,
  because each candidate gives different results under the same rules. The vehicle test world is a new
  collision file, because regenerating `test-world.mmcd` would change its checksum and refuse every 015
  stream. 015's stream refusal fixture that used version 2 as its unknown version moves to a version
  that is still unknown, through `simcore golden write`, and its message now lists versions 1 and 2.
- **Per build mode.** 015 left gate 9's WebAssembly build mode open (the prebuilt interpreter, the
  relinked interpreter or AOT). Bepu runs inside the .NET runtime, so its browser cost depends heavily
  on the mode (015's prototype measured about 95 µs interpreted and 14 µs AOT for one character step),
  while Jolt's and Rapier's WebAssembly modules do not. Applying the rule in each mode keeps the
  build-mode decision open: when the winners differ, the record is conditional on it.
- **The deciding cost.** The browser cost that decides is the mean cost per step of a world with one
  driven vehicle, measured in a Chromium Worker with 015's step-cost method. A client predicts only its
  own vehicle, so that is its cost each frame; costs with 16 and 64 vehicles, and the server's costs,
  are recorded for the gate-9 sweep and do not decide. "Within 10%" counts as a tie, for both cost and
  download size, so run-to-run noise on a shared runner cannot decide the choice. The fixed order
  Bepu, Jolt, Rapier, which ends a double tie, is v2's default order with Rapier added last.
- **The vehicle model.** Phase 1 content has one ground vehicle (issue #12, sub-spec 5), so the model
  is one four-wheeled vehicle with keyboard-style digital controls read from 015's input command
  version 1, and no new input format. An adapter may use its engine's own vehicle support or build the
  wheel model on the engine's rigid bodies and queries, as long as it meets the same behavior outcomes.
  Placeholder values: 2,000 kg, a 4.5 × 2 × 1.5 m chassis, a 20 m/s top speed (under the simulator's
  30 m/s ground-vehicle ceiling), 8 m/s² braking and a 35 degree steering limit. Game design replaces
  them; they are configuration, so replacing them changes no code.
- **Out of scope.** Vehicle damage, occupants, seats, entering and leaving, vehicle weapons and turrets,
  tracked vehicles, aircraft, and interactions between vehicles and characters or projectiles. In this
  slice characters and vehicles share a world but do not collide; collisions between them and
  vehicle damage belong to sub-spec 5, and vehicle hits to sub-spec 6.
- **Single-threaded engines, no sleeping.** Each engine steps on the calling thread, because engine job
  systems take locks and gameplay code may not (Principle II), and because parallel phases are gate
  9's open decision. Sleeping is off and engine caches are either in the state or off, so the
  canonical state is complete (FR-013). Their effect on cost is part of what is measured.
- **Bepu's vector pin.** Bepu uses `Vector<T>`, whose width is 4 lanes in WebAssembly and up to 8 on
  x64. Its server build runs with the runtime's preferred vector width at 128 bits, as the issue asks.
  The setting applies to the whole server process, so if Bepu is chosen, every continent server runs
  with it, and server-only code outside the core loses wider vectors; the record states this
  consequence. 014's sweeps already pin `DOTNET_EnableAVX512F=0`.
- **Engine releases.** Each candidate is pinned to its latest stable release when the plan is written,
  recorded in `research.md`. Jolt and Rapier are built by CI from pinned sources with pinned
  toolchains, and cached by their identity, so the vehicle agreement job does not rebuild them.
- **Runner class.** All measurements run on the CI runner class 015 uses (`gha-ubuntu-latest-x64`, with
  014's pinned intrinsic settings), in one job, so the candidates are compared on the same hardware.
  The selection reruns on demand; a new record replaces the old one in a pull request.
- **Losing candidates stay until gate 9 closes.** Every candidate's adapter, builds and streams stay in
  the repository and in the selection run, so the decision can be rerun on a new runner class or engine
  release. Removing the candidates gate 9 does not keep is a follow-up after gate 9.
- **Older specs.** `specs/013-regionizer-simulator/spec.md` gives ground vehicles a claim radius and
  speeds; this spec changes neither. `specs/014-telemetry-test-harness/spec.md` defines the network
  profiles the window rests on; this spec only reads them. In `specs/015-shared-simulation-core/spec.md`
  the statements that vehicle physics is a separate slice and that the core leaves an extension point
  for vehicles stay true. Its US2 scenario 5 and US6 scenario 2 (an unknown version is refused, naming
  both versions) still hold, with version 2 now known. Its FR-016 and FR-020 describe version 1 and are
  extended, not contradicted, by FR-012 and FR-015; the pull request that builds this feature updates
  015's `spec.md` where its text names only version 1. This spec prevails for vehicle state and for
  version 2.
