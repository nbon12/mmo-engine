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
is recorded in full under Assumptions. The spec review on the same day added the last four.

- Q: Is the vehicle physics engine one of the constitution's open decisions? → A: No. The open
  decisions are numbered 1 to 10, and none of them is the vehicle physics engine. It is a row of gate
  9's decision table in issue #12, whose rule is "sub-spec 3's agreement-and-cost test". This spec
  defines that rule from the issue's ordering (qualify on bit-exact hashes, then browser step cost,
  then download size) in FR-026. The choices it adds, the inclusive tie band and what ends a tie, are
  recorded under Assumptions ("The deciding cost"). The result is recorded as gate 9's decision, with
  its evidence. No amendment is needed.
- Q: When is Bepu with non-exact vehicles chosen over an engine that agrees bit for bit? → A: Never.
  Exactness comes first, because gate 9's exit criterion asks for identical state hashes. Non-exact
  Bepu is the outcome only when no candidate qualifies, Bepu passed every server-side check (server
  reset equivalence included), and Bepu's measured divergence stays inside the stated bounds.
- Q: How are "vehicle corrections stay small" judged before combat netcode (sub-spec 6) exists? → A:
  By a windowed divergence measurement: the browser build restarts from the server build's state at
  the start of every half-second window, as reconciliation would, and its position and orientation
  error against the server is measured at every step. The bounds are 5 cm and 1 degree, in the
  selection configuration. A test checks that the window covers the reconciliation span of every
  committed network profile, loss included. No correction distance is measured yet, so a NonExact
  record carries a provisional flag that names that missing measurement, and it does not by itself
  meet gate 9's vehicle exit criterion.
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
  rule is applied in each of the three build modes (`interp-prebuilt`, `interp-relinked` and `aot`).
  A mode the toolchain could not produce is NotBuilt, with its reason. If the winners differ between
  modes, the record names the winner for each mode, and the choice is conditional on gate 9's
  build-mode decision. This spec does not settle the build mode.
- Q: Which browser cost decides? → A: The mean cost per step of a world with one driven vehicle, in
  the browser build in a Chromium Worker. A client predicts only its own vehicle (sub-spec 6), so
  that is what it pays every frame.
- Q: How do Jolt's and Rapier's WebAssembly builds join the .NET WebAssembly build? → A: Each is its
  own module, loaded beside the .NET runtime in the same Worker and called through `[JSImport]`, so
  both exist in all three build modes. The per-step crossing counts in the step cost, and the module
  and its glue count in the download (FR-032).
- Q: Which components does the license scan judge? → A: The ones a candidate adds over the core-only
  builds. Those builds already hold the .NET runtime, the operating-system libraries and the .NET
  WebAssembly runtime (FR-031). This spec does not settle how Principle VII treats those platform
  libraries. It reports the question as a PATCH amendment the maintainer may make.
- Q: Does Principle II's no-lock rule reach the engines? → A: Yes. The engine step runs inside a
  region's tick. So each adapter carries the analyzer, exempt only from what its engine forces
  (FR-004), and a lock test counts the locks the engine's native code takes (FR-034).
- Q: How are the behavior checks tested when a real engine may legitimately fail them? → A: On a
  fixture engine whose faults are known (FR-035). The real engines' verdicts are evidence in the
  report, and the winner's verdicts are a real test on `main`.

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
file, with the same input commands. The turning-circle stream is the one exception (scenario 3). The
checks of scenarios 1 to 12 decide whether a candidate's vehicle accelerates, brakes, turns, crosses
rolling terrain, jumps off a ramp and lands, and collides with other vehicles head-on, from the side
and from behind, the way a ground vehicle should. They also decide whether its server build gives
the same state after every step, in another process and on another day, and whether a world
restarted from any committed state steps on exactly like a world that never restarted. Each check's
verdict is recorded per candidate. Which engines pass is what the selection finds out. So the checks
themselves are tested on the fixture engine (FR-035), whose outcomes are known, and the engine that
the decision record names passes every one of them.

**Why this priority**: An agreement test means nothing if the vehicle does not really drive: a vehicle
that never moves agrees trivially. Every other story runs these scenarios.

**Independent Test**: Run the checks of scenarios 1 to 12 on the fixture engine with no fault, and
require every check to pass. Then run them once for each fault in its fault table, and require
exactly the targeted check to fail, with its reason code, scenario and measured value (scenario 13).
Run the checks on each built candidate's server build, with `simcore streams verify` in two separate
processes for scenario 8. Require the selection report to hold one verdict per check and candidate.
Then, when the committed decision record names a winner, require the winner to pass every check
(scenario 14). The numbers in scenarios 1 to 7 are the selection configuration's defaults (FR-036;
Assumptions, "Behavior tolerances and time bounds"). Each check reads its outcome from oracles that
do not use the engine under test (FR-011).

**Acceptance Scenarios**:

1. **Given** a vehicle at rest on flat terrain and the throttle held for 10 seconds, **When** it is
   stepped at 30 Hz, **Then** the check passes only if the vehicle reaches at least 95% of the
   configured top speed, never exceeds the top speed by more than 2%, stays upright (the chassis up
   axis within 10 degrees of world up), and has all four wheels in contact with the ground at every
   step after the first second.
2. **Given** a vehicle driven to its top speed on flat terrain, **When** the brake is held from step
   s onward, **Then** the check passes only if the vehicle comes to rest (speed below 0.1 m/s) and
   stays upright. Its stopping distance must be at most 120% of v² / (2b). Here v is its speed at the
   start of step s, and b is the configured braking deceleration. The stopping distance is the path
   length of the chassis position, from the start of step s to the first step at rest.
3. **Given** a vehicle on flat terrain at 5 m/s with full steering held, **When** it is stepped for
   up to 15 seconds, **Then** the check passes only if four things hold: its heading turns in one
   direction only; it completes 360 degrees of heading change within the 15 seconds; its speed stays
   within 5 ± 0.5 m/s; and the turn's radius is within 25% of the configured wheelbase divided by the
   tangent of the configured maximum steering angle. The radius is that of the least-squares circle
   through the chassis positions over that turn, in the horizontal plane. The stream holds the
   throttle with a recording governor: when the stream is recorded, `Forward` is pressed on exactly
   the steps that start below 5 m/s. So this stream's commands differ between candidates.
4. **Given** rolling heightfield terrain, **When** a vehicle is driven across it at full throttle for
   30 seconds, crossing heightfield triangle edges and cell boundaries, **Then** the check passes only
   if the chassis and wheels never penetrate the terrain by more than the configured contact
   tolerance, measured against the core's collision world, and the vehicle ends upright.
5. **Given** a ramp in the static meshes, approached at the scenario's stated speed, **When** the
   vehicle drives up it, **Then** the check passes only if the vehicle leaves the ramp's lip with all
   four wheels out of contact. It must land beyond the lip without penetrating the terrain or meshes
   by more than the contact tolerance, measured against the core's collision world. And it must be
   upright with all four wheels in contact within 2 seconds of landing.
6. **Given** two vehicles coasting into each other head-on, side-on or rear-on at the scenario's
   stated speeds, **When** they meet, **Then** the check passes only if three things hold. First, the
   vehicle contact event for the pair appears exactly at the first step at which their chassis'
   oriented-box separation, computed from the canonical state (FR-011), is at most the contact
   tolerance. Second, their penetration depth, by the same computation, is at most the contact
   tolerance at every step. Third, within 3 steps of the contact event, their closing speed is at
   most 10% of what it was the step before the event.
7. **Given** a vehicle driven at top speed into a static wall thinner than its travel in one step,
   **When** it is stepped, **Then** the check passes only if the vehicle never passes through the
   wall: its chassis never ends a step past the wall's far face, measured against the core's
   collision world.
8. **Given** any committed vehicle stream of a candidate, **When** it is stepped in two separate
   processes on that candidate's server build, **Then** the check passes only if the per-step state
   hashes are identical and equal the stream's committed expected hashes.
9. **Given** a vehicle stream made for 20, 30 or 60 Hz, **When** it is stepped twice, **Then** the
   check passes only if both runs give identical per-step hashes.
10. **Given** a world stepped on a candidate's server build from a stream's initial state to its last
    step, **When** a second world on the same build is loaded with the first world's committed state
    at step j and stepped on with the same commands, for every j that starts a divergence window
    (FR-022), **Then** the check passes only if the second world's per-step hashes from step j + 1
    onward equal the first world's (server-build reset equivalence).
11. **Given** a vehicle command with an out-of-range value (unknown button bits, a pitch out of
    range, a weapon slot outside the configured slots, or a sequence number that does not increase),
    **When** it is offered to the step, **Then** the check passes only if the command is rejected
    with 015's reason code and the vehicle is stepped with the neutral input, as a character is.
12. **Given** a candidate's vehicle step on the server build, warmed up, **When** a further 1,000
    steps run, **Then** the check passes only if they take no lock (FR-034).
13. **Given** the fixture engine with one fault from its fault table (FR-035), for example a drive
    force that overshoots the top speed by 3%, **When** the checks run through the selection's
    harness, **Then** the fixture engine is Failed in every build mode. Its reason gives the code for
    the check the fault targets, that check's scenario and the measured value. For the overshoot the
    reason is `BehaviorCheckFailed`, US1 scenario 1, 3.0%. Every other check passes, the fixture
    engine is not Qualified, and every other engine in the run is still evaluated.
14. **Given** a committed decision record that names a winner, **When** the checks of scenarios 1 to
    12 run on the winner's server build, **Then** every check passes. **Given** a record whose
    outcome is None in every mode, **Then** it names no winner and gives each candidate's failing
    checks (US3 scenario 5).

---

### User Story 2 - Each candidate's builds agree, or the first divergence is named (Priority: P1)

An engineer runs the selection in CI. For every candidate that built, the agreement suite runs the
candidate's committed vehicle streams and generated streams from 50 seeds through its server build and
its browser build. The browser build runs in Node, with the jiterpreter on and off in the interpreted
modes, and inside a dedicated Web Worker in headless Chromium. It runs in each of the three build
modes (FR-018). Every step's record, its state and hash, is compared with the server build's. A
candidate qualifies when its records all match, its browser build also restarts exactly from the
server's committed states, and it passed the checks of US1. A candidate that differs at any step is
reported with the stream, the first divergent step, the build, the build mode and the fields that
differ. Each candidate ends each build mode with exactly one status from FR-037's table. One
candidate's failure never stops the others from being evaluated.

**Why this priority**: Qualification is the first test of the rule. An engine whose builds disagree
makes every predicted vehicle drift.

**Independent Test**: Run the selection run of the `SimCore` workflow. Its report must hold one status
for each candidate and build mode, from FR-037's table: Qualified, Diverged, Failed, NotBuilt,
NotTested or LicenseBlocked, each with its reason codes. It must also hold one result for each
required build of that mode: Match, Diverged or NotRun. Then a deliberate one-ulp change, the stream
runner's perturbation of the first vehicle's `position.x` at step 15, must in a candidate's browser
build agree with the server's records before that step and differ at it. And
`simcore agreement compare`, given a candidate's records perturbed the same way, must exit 1, naming
step 15 as the first divergent step and `vehicles[0].position.x` as the field that differs. Then the
denial scenarios (9 to 11) run through the real harness with the fixture engine and fixture license
inputs, and each must end with its stated status, reason and decision.

**Acceptance Scenarios**:

1. **Given** a built candidate's committed vehicle streams, **When** the agreement suite runs,
   **Then** the per-step records of each required build (FR-018) are compared byte for byte with the
   server build's, in each of the three build modes. The required builds are `node-interp`,
   `node-interp-nojit` and `chromium-worker` in `interp-prebuilt`; `node-interp-relinked`,
   `node-interp-relinked-nojit` and `chromium-worker-relinked` in `interp-relinked`; and `node-aot` and
   `chromium-worker-aot` in `aot`.
2. **Given** generated vehicle streams from 50 fixed seeds (`--seeds 1-50`), each with 8 vehicles
   driving seeded throttle, brake and steering for 20 seconds on the enclosed course, **When** the
   agreement suite runs, **Then** every step's record is compared across the builds in the same way.
3. **Given** a candidate whose browser build differs from its server build at one step, **When** the
   suite compares them, **Then** the candidate's status in that build mode is Diverged
   (`RecordDiverged`). The report names the stream, the first divergent step, the build and each
   state field that differs, with both builds' values in exact bit form. The suite goes on to
   evaluate the other candidates.
4. **Given** a candidate's browser build loaded with its server build's committed state at a step
   and stepped on with the same commands, **When** its records are compared, **Then** they equal the
   server build's from the next step on. A candidate for which they do not is Diverged in that mode
   (`ResetDiverged`). The reason names the build, the stream, the restart step and the first
   differing step.
5. **Given** a frame that queues several commands for a driven vehicle, **When** the batched step
   entry point is called once, on the server build (`BatchStepper.StepQueued`) or through the
   browser build's `StepQueued` export in a dedicated Worker, **Then** it steps each command in order
   and returns one state and hash per step, the same as stepping them one at a time.
6. **Given** 015's 19 committed streams and `simcore/content/test-world.mmcd`, **When** the agreement
   suite runs after this feature, **Then** each file's SHA-256 equals the committed manifest taken at
   commit 65112ab (FR-015), and every required build still reports Match.
7. **Given** a vehicle stream recorded with another engine, another engine version or build setting,
   or another vehicle rules revision, or a stream in a format version this build does not support,
   **When** the suite loads it, **Then** it reports NotComparable, naming both values, and never
   Diverged. `simcore agreement compare` and `simcore streams verify` exit 2, as in 015. In the
   selection, a candidate whose own committed stream is NotComparable is Failed
   (`StreamNotComparable`).
8. **Given** a candidate whose server or browser build fails, or does not finish within its
   15-minute bound (FR-021), **When** the selection runs, **Then** that candidate is NotBuilt
   (`BuildFailed` or `BuildTimedOut`). The reason gives the failing step, or the timeout, and the last
   50 lines of its output, and every other candidate is still evaluated.
9. **Given** two fixture engines in one selection run, one whose browser build perturbs
   `vehicles[0].position.x` by one ulp at step 15 of a committed stream and one with no fault,
   **When** the selection and the decision run, **Then** the perturbed one is Diverged
   (`RecordDiverged`) in every build mode. The reason names the stream, step 15, the build and
   `vehicles[0].position.x`. The other is Qualified. Whatever their measured costs, the record picks
   the Qualified one with the outcome BitExact, and it lists the Diverged one with its status and
   reason.
10. **Given** a fixture license input in which a candidate's added components include a transitive
    dependency licensed GPL-3.0-only, **When** the selection runs, **Then** that candidate is
    LicenseBlocked (`LicenseNotAllowed`) in every build mode. The reason names the component and its
    license expression. The other candidates are still evaluated, and the run exits 0.
11. **Given** a fixture input for each row of FR-037's status table, **When** the selection runs on
    it, **Then** the candidate ends that build mode with that row's status and reason code. Where
    several rows hold at once, it takes the first status in the table's order, and the report lists
    every reason. The selection run exits 0 whenever every candidate has a status in every mode.
12. **Given** the generated vehicle streams of seeds 1 to 50, **When** they are stepped on the
    fixture engine, and on each candidate's server build, **Then** each seed's stream has at least
    one vehicle contact event and every vehicle in it travels at least 50 m. At least one of the 50
    seeds also has an airborne phase, a vehicle with all four wheels out of contact. On the fixture
    engine, a miss fails the generator's test, naming the seed and the condition. On a candidate, a
    miss makes it Failed (`CoverageNotMet`), naming the same.

---

### User Story 3 - The engine is chosen by the stated rule and recorded as gate 9's decision (Priority: P1)

The selection run writes a validated report. The decision command reads it, applies the decision rule
(FR-026) and writes the decision record: the chosen engine, or none, why, and the evidence behind it.
An agent commits the record. Gate 9's "Vehicle physics engine" row reads it. From then on, CI gates
every change to the vehicle code on the chosen engine's vehicle streams, and refuses a record that no
longer describes what is in the repository.

**Why this priority**: The choice is this slice's deliverable and one of gate 9's exit criteria.

**Independent Test**: Run the decision command on a committed set of fixture reports and compare each
record with its expected record. The set covers every branch of the rule, its band boundaries, the
mixed-mode case and a cheapest candidate that is Diverged (scenarios 1 to 6, 10 and 11). Then run it
on the committed selection report and require the committed decision record byte for byte. Then
change one input the record fingerprints and require the staleness check to fail, naming it. In
every scenario below, a value x of a candidate ties with the lowest value L of its kind among the
candidates compared when x ≤ 1.10 × L, inclusive (FR-026).

**Acceptance Scenarios**:

1. **Given** a report in which exactly one candidate is Qualified in every build mode, **When** the
   decision runs, **Then** that candidate is chosen with the outcome BitExact.
2. **Given** several Qualified candidates in a build mode, **When** the decision runs, **Then** the
   one with the lowest browser cost per step (one driven vehicle, Chromium Worker, mean) is chosen,
   when every other qualifier's cost exceeds 1.10 × its cost.
3. **Given** Qualified candidates whose browser costs tie with the lowest, **When** the decision
   runs, **Then** the tie narrows to those whose added Brotli download bytes tie with the smallest
   among them. If one remains, it wins. **Given** several that still tie, **Then** the one with the
   lowest browser cost per step wins, then the one with the fewest added Brotli bytes. The order
   Bepu, Jolt, Rapier decides only when both values are exactly equal. For example, with Jolt at
   100 µs and 500 KB and Bepu at 109 µs and 540 KB, both bands tie and Jolt wins.
4. **Given** a report with no Qualified candidate in a build mode, in which Bepu is Diverged in that
   mode and its divergence stayed within bounds (US5) on every stream and required build of that
   mode, **When** the decision runs, **Then** Bepu is chosen in that mode with the outcome NonExact.
   Diverged means that every check Bepu missed is a cross-build one (`RecordDiverged`,
   `ResetDiverged`). So it passed every server-side check, including server-build reset equivalence
   (US1 scenario 10). The record carries the provisional flag with its condition (FR-027), and it
   states that it does not by itself meet gate 9's vehicle exit criterion.
5. **Given** a report with no Qualified candidate in a build mode, in which Bepu's divergence exceeded
   a bound in that mode or Bepu has any status but Diverged, **When** the decision runs, **Then** that
   mode's outcome is None. The record gives each candidate's status and reasons in that mode. When
   every mode's outcome is None, the record states that gate 9's vehicle exit criterion is not met.
   The decision command exits 0.
6. **Given** a report in which the rule gives different winners or outcomes in different build
   modes, **When** the decision runs, **Then** the record names the outcome and winner in each mode
   and marks the choice as conditional on gate 9's WebAssembly build-mode decision. One fixture is
   mixed-mode, with no qualifier in any mode. In it, Bepu is within bounds in `aot` and beyond a bound
   in `interp-prebuilt` and `interp-relinked`. So the record is NonExact in `aot` and None in the
   other two.
7. **Given** the same report, **When** the decision runs twice, **Then** both records are identical
   byte for byte.
8. **Given** a committed decision record, **When** an input the record fingerprints differs from the
   one in the repository, **Then** the staleness check fails and names each input that changed. The
   inputs are the engine versions or build settings, the vehicle rules revision, the vehicle
   configuration, the selection configuration and the committed vehicle streams. When the record
   carries the provisional flag, the check prints it.
9. **Given** a committed record with the outcome BitExact, **When** a pull request changes the core,
   the vehicle code, the vehicle streams, an engine build, the selection configuration or the
   record, **Then** the `SimCore` workflow's vehicle agreement job runs the chosen engine's vehicle
   streams across the required builds of each mode it won. The job fails on any divergent step, and
   it gates the pull request the way the `agreement` job does. **Given** the outcome NonExact,
   **Then** the job instead fails in three cases: the server build misses a committed hash, a
   browser build's divergence exceeds a bound, or the version 1 part hash (FR-012) differs at any
   step. It prints the provisional flag in its log and summary. **Given** the outcome None in every
   mode, **Then** the job runs no vehicle gate and reports the record's outcome in the job summary.
10. **Given** the boundary fixture reports, **When** the decision runs, **Then** each record's tied
    sets and winner equal the fixture's expected record. The cost fixtures are three: costs of 100
    and 110 µs (exactly 1.10 ×, a tie); 100 and 110.01 µs (no tie); and three qualifiers at 100, 109
    and 115 µs (the tied set is the first two). The same three cases are repeated for added Brotli
    bytes.
11. **Given** a fixture report in which the candidate with the lowest browser cost is Diverged and a
    dearer one is Qualified, **When** the decision runs, **Then** the Qualified one wins with the
    outcome BitExact, and the record lists the Diverged one with its status and reason.

---

### User Story 4 - Each candidate's cost and download size are measured (Priority: P2)

The selection run measures every built candidate in the same job on the same runner class: the server
build's cost per vehicle step, the browser build's cost per step, its startup time, and the download
size it adds to the shared core's browser build. Each is measured in each of the three build modes and
recorded as a measurement with its runner class. A candidate or mode that could not be measured is
named, with the reason. Steady-state vehicle steps allocate no managed memory.

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
2. **Given** each built candidate's browser build, in each of the three build modes, **When** the
   measurement runs, **Then** the report records the mean and p99 cost per step with 1, 16 and 64
   vehicles, in Node and in a Chromium Worker, measured the way 015 measures step cost, and the time to
   the first completed vehicle step over 10 cold starts. For Jolt and Rapier, the cost per step
   includes the crossing between the .NET runtime and the engine's module (FR-032).
3. **Given** each built candidate's browser build in each mode, **When** the download size is
   measured, **Then** the report records the bytes it adds (raw, Brotli and gzip) to the shared
   core's own browser build in the same mode. For Jolt and Rapier, the added bytes include the
   engine's own WebAssembly module and its JavaScript glue, which the selection run compresses the way
   015's measurement compresses every file.
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
non-exact vehicles is a valid outcome. The window covers the reconciliation span of every committed
network profile, and a test checks that.

**Why this priority**: It decides the issue's fallback outcome. It matters only if no candidate
qualifies, but it must be measured before the decision runs.

**Independent Test**: Run the divergence measurement on a committed pair of record sets whose
differences are known, and check the reported errors. Then run it on Bepu's vehicle streams in every
required build and build mode, and check that the report holds each stream's maxima and the bound
verdict. Then check the window against every committed network profile (scenario 6).

**Acceptance Scenarios**:

1. **Given** Bepu's committed and generated vehicle streams, **When** the divergence measurement runs,
   **Then** each required browser build is loaded with the server build's state at the start of every
   window of ⌈0.5 s × step rate⌉ steps (15 at 30 Hz). It is stepped through the window with the
   stream's commands. For every vehicle and every step, the measurement records three errors against
   the server build at the same step: the chassis position error (metres), the orientation error
   (degrees of the rotation between the two orientations) and the linear velocity error (m/s).
2. **Given** the measurement's results, **When** the report is written, **Then** it gives, for each
   stream, required build and build mode, the maximum and p99 of each error, and the step and
   vehicle of each maximum.
3. **Given** two record sets whose only difference is a known offset of one vehicle (0.03 m in
   position and 0.5 degrees in orientation), **When** the divergence is measured, **Then** the reported
   errors equal those offsets within 1e-4 m and 1e-3 degrees.
4. **Given** results in which every window's maximum position error is at most 0.05 m and maximum
   orientation error at most 1 degree, on every stream and required build of a build mode, **When**
   the bounds are applied, **Then** Bepu is within bounds in that mode. **Given** one step beyond
   either bound, **Then** Bepu is beyond bounds in that mode, and the report names the stream, step,
   vehicle and value. Linear velocity error is reported and not bounded.
5. **Given** a candidate whose browser and server records are identical, **When** its divergence is
   measured, **Then** every error is exactly 0.
6. **Given** the committed network profiles (`harness/profiles/*.json`) and the selection
   configuration's window, **When** the window check runs at each tested step rate (20, 30 and
   60 Hz), **Then** for every profile the window's step count is at least that profile's
   reconciliation span: ⌈(round trip + 2 × jitter) × step rate⌉ steps, plus 2 steps of input buffer,
   plus 1 step for a lost state update when the profile's loss is above zero. With the committed
   profiles, Poor at 20 Hz needs 7 + 2 + 1 = 10 steps, which is exactly the window. A profile or rate
   that the window does not cover fails the check, naming the profile, the step rate and both spans.

---

### User Story 6 - Rapier joins only if it fits the harness cheaply (Priority: P3)

Rapier is a third candidate, tested only if it fits the same scenario harness cheaply. An automated fit
check evaluates it against six stated criteria. If they all pass, Rapier is a full candidate. If any
fails, Rapier is recorded as NotTested with the failing criterion, and the decision is made over Bepu
and Jolt.

**Why this priority**: Rapier adds a candidate, not a capability. The decision can be made without it.

**Independent Test**: Run the fit check on Rapier's pinned release and check that the report records a
verdict and evidence for every criterion, whatever the verdicts are. Then drive the selection's
orchestration with committed fit-check fixtures: one fixture per criterion in which that criterion
fails, and one in which every criterion passes. Check each result. The real Rapier's verdicts and
status are whatever the committed selection report records.

**Acceptance Scenarios**:

1. **Given** Rapier's pinned release, **When** the fit check runs, **Then** it records a pass or fail,
   with evidence, for each criterion of FR-030.
2. **Given** a committed fit-check fixture in which one criterion fails, one fixture per criterion,
   **When** the selection's orchestration runs with it, **Then** Rapier's status is NotTested
   (`FitCheckFailed`) with that criterion as the reason. The other candidates are decided as usual,
   and the run exits 0.
3. **Given** a committed fit-check fixture in which every criterion passes, **When** the selection's
   orchestration runs with it, **Then** it schedules Rapier for every scenario, agreement check and
   measurement that Bepu and Jolt get. Rapier's report entries have the same shape as theirs.

---

### User Story 7 - Vehicle state joins the canonical state under new versions (Priority: P3)

Vehicles enter the canonical state in a new state serialisation version and a new recorded stream
format version, each pinned by golden fixtures. Version 1 is untouched, so every stream and fixture
015 committed still reads, runs and hashes as before. A file in an unknown version, or one that is
malformed, is refused with a clear error and never half-read.

**Why this priority**: Every other story needs the formats, but their versioning and refusal paths
can follow the first working vehicle, as 015's did.

**Independent Test**: Encode and decode every new golden fixture and compare the bytes. Decode every
015 golden fixture. Compare 015's committed stream files and `simcore/content/test-world.mmcd` with
the committed SHA-256 manifest taken at commit 65112ab (FR-015), and run every 015 committed stream
against the expected hashes it carries. Then feed truncated, oversized, inconsistent and
unknown-version vehicle streams and states to the decoders and check each refusal.

**Acceptance Scenarios**:

1. **Given** each new valid golden fixture (state version 2, stream version 2), **When** it is decoded
   and re-encoded, **Then** the bytes are identical to the fixture.
2. **Given** a version 1 stream, **When** it is decoded and run, **Then** it runs under state
   serialisation version 1, its step hashes equal the expected hashes it carries, and its file's
   SHA-256 equals the manifest's.
3. **Given** a stream or state in a version this build does not support, **When** it is decoded,
   **Then** it is refused with an error naming the version found and the versions supported. The
   agreement suite reports such a stream as NotComparable, as 015's does (US2 scenario 7).
4. **Given** a truncated, oversized or internally inconsistent vehicle stream or state (for example a
   vehicle count that disagrees with the initial state, or vehicle IDs that do not ascend), **When** it
   is decoded, **Then** it is refused with an error naming the offset or field, and no partial result
   is returned.
5. **Given** a vehicle stream whose collision data, matched by checksum, is not supplied, **When** it
   is opened, **Then** it is refused before any step runs.
6. **Given** a vehicle field that would be negative zero after a step, **When** the state is
   committed, **Then** it is stored and hashed as positive zero. **Given** a field the engine left as
   NaN or infinity, **Then** the step refuses to complete and names the field, such as
   `vehicles[0].angularVelocity.y`.
7. **Given** a version 2 stream with characters and vehicles, **When** it is stepped, **Then** each
   step's record carries two hashes: the full state hash, and the hash of the state's version 1 part
   (FR-012). Characters and vehicles do not interact in this slice, so the version 1 part hash
   equals the hash that the same stream gives with its vehicles removed.

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
  vehicle IDs). The core derives vehicle contact events from the canonical state (FR-012), so an
  engine's own contact report never sets them, whatever its order, its speculative contacts or its
  rounded shapes.
- Jolt's and Rapier's browser modules have their own WebAssembly memory. State and commands cross
  between it and the .NET runtime's memory as bytes, never as JavaScript numbers, so no float is
  rounded and no NaN payload changes on the way (FR-032).
- A heightfield triangle edge or vertex under a wheel, and a degenerate (zero-area) mesh triangle:
  every candidate collides with exactly the triangles the core's collision world uses, with
  degenerate ones dropped.
- An engine version bump changes the engine's identity, so every stream recorded with the old version
  is NotComparable until it is recorded again with a stated reason.
- An engine that starts worker threads: every candidate steps on the calling thread only, with no
  engine job system, because gameplay code takes no locks (Principle II). The lock test (US1
  scenario 12, FR-034) checks it.
- Region merges, splits, portals and tick overruns: N/A in Phase 1, which runs one region. Phase 2's
  merge and split callbacks will need to load a vehicle exactly into another world. Every winner must
  pass server-build reset equivalence (US1 scenario 10, FR-026), so the chosen engine's vehicle loads
  exactly into another world on the server build, and the record states this (FR-027). A live change
  of step rate is not supported, as in 015.

## Requirements *(mandatory)*

### Functional Requirements

**Candidates and builds**

- **FR-001**: The selection MUST evaluate three candidates, each at an exactly pinned release: Bepu,
  in process on the same .NET runtime as the core in both builds, with the server build's vectors
  pinned to 128 bits so that its vector width matches WebAssembly's; Jolt, built from pinned sources
  with its cross-platform determinism option, as a native library called in process on the server and
  as its own WebAssembly module in the browser (FR-032); and Rapier, subject to the fit check
  (FR-030), built from pinned sources with its enhanced-determinism feature, through its C API in
  process on the server and as its own WebAssembly module in the browser (FR-032).
- **FR-002**: Every candidate's builds MUST be made with determinism-preserving settings, recorded with
  its identity: no fast-math, no fused multiply-add contraction, the engine stepped on the calling
  thread only, and sleeping off. The engine's sources, toolchains and build settings MUST be pinned by
  version and checksum, and any change to them changes the engine's identity.
- **FR-003**: Every candidate MUST be driven through one engine-neutral adapter interface by one
  scenario harness. The scenarios, formats, runners and other candidates' adapters MUST NOT contain
  engine-specific branches.
- **FR-004**: The determinism analyzer MUST stay attached to the core assembly with no rule relaxed.
  The engine adapters MUST live outside that assembly, because each breaks one rule by construction.
  The analyzer MUST also be attached to each adapter assembly and to the fixture engine (FR-035),
  with every rule at Error, except for one declared exemption per adapter:
  - Bepu's adapter: MMO1001, for the `System.Numerics` vector types that Bepu's API and callbacks
    use.
  - Jolt's and Rapier's adapters: the native-interop members of MMO1008 (`extern` and
    `[LibraryImport]` declarations, `NativeLibrary` and `Marshal`), plus, in the browser build,
    `[JSImport]` declarations and the interop code their generator emits. Every other member of
    MMO1008 stays at Error.

  MMO1006 (locks) is never exempt, and the fixture engine has no exemption. Each exemption is
  declared per assembly and names the rule and the members it admits. It is justified in `plan.md`'s
  Complexity Tracking. The analyzer's backstop test compiles each adapter's sources and requires zero
  diagnostics outside its declared exemption, suppressed ones included. No analyzer reads the
  engines' own code, so the agreement suite and the lock test (FR-034) are their determinism checks.
- **FR-032**: In the browser, Jolt and Rapier MUST each run as their own WebAssembly module. Each
  module is built with the engine's own pinned toolchain (FR-002) and loaded beside the .NET runtime
  in the same Worker. The candidate's .NET adapter calls it through `[JSImport]` once per step, or once
  per batch. State and commands MUST cross between the two memories as bytes copied through a buffer,
  never as JavaScript numbers. The crossing is part of the measured browser step cost (FR-024). The
  module and its JavaScript glue count toward the bytes the candidate adds. Each candidate's browser
  build therefore exists in all three build modes, `interp-prebuilt` included. Each candidate MUST
  have its own browser host project, so 015's `MmoEngine.SimCore.Wasm` project, its publish and its
  `agreement` job do not change (US2 scenario 6).
- **FR-033**: Every part of the vehicle step that does not call the engine MUST live in the core, under
  the full analyzer. That covers:
  - mapping commands to throttle, brake, reverse and steering (FR-008), and integrating steering at
    the configured rate;
  - validating the vehicle configuration (FR-005);
  - the canonical serialisation and hashing of vehicle state (FR-012);
  - deriving vehicle contact events and their order (FR-012);
  - the out-of-bounds fault;
  - the wheel model's suspension and tire forces, where an adapter builds that model on the engine's
    rigid bodies and queries instead of using the engine's own vehicle support.

  An adapter MUST hold only calls into its engine and the marshalling of state and commands across
  that boundary.
- **FR-034**: A candidate's steady-state vehicle step MUST take no lock or other blocking
  synchronization (Principle II). A test MUST show it the way the zero-allocation tests show
  allocation: over 1,000 steps after warm-up on the server build, it counts locks, and requires zero.
  - On the native path (Jolt and Rapier), it counts calls to the pthread mutex, read-write-lock,
    condition-variable and semaphore functions, through an interposed shim, and `futex` system
    calls.
  - On the managed path, MMO1006 covers the adapters and the core. A scan of the Bepu assembly for
    the members on MMO1006's blocking list covers the methods the step reaches.

  The count is itself tested on a native test library that takes one uncontended mutex per step,
  which the test must count. Jolt's adapter uses Jolt's no-lock body and body-lock interfaces. The
  report also lists, as evidence, the lock symbols each native library links. A candidate whose step
  takes a lock is Failed (`TakesLock`), naming the function.

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
  the vehicle. Every vehicle command MUST be validated by 015's input rules (015's FR-019) before it
  affects state.
- **FR-009**: The vehicle step MUST run at the world's configured step rate (20 to 60 Hz) with the
  configured fixed number of substeps, and MUST NOT accept a measured frame time.

**Scenarios and behavior**

- **FR-010**: Each candidate MUST have committed named vehicle streams for at least these scenarios:
  flat acceleration, braking to a stop, a turning circle, rolling terrain, a ramp jump, head-on,
  side and rear collisions, a pile-up of at least four vehicles, a thin wall at top speed, and flat
  acceleration at 20 and 60 Hz. The agreement suite MUST also generate streams from 50 fixed seeds.
  Each has 8 vehicles driving seeded throttle, brake and steering for 20 seconds of simulated time
  on the enclosed course. Each seed's stream MUST have at least one vehicle contact event, and every
  vehicle in it MUST travel at least 50 m. At least one of the 50 seeds MUST have an airborne phase.
  The generator's test checks this on the fixture engine and names the seed and the condition that
  fails. The selection checks it on each candidate's server build (`CoverageNotMet`, FR-037).
- **FR-011**: The behavior outcomes of US1 scenarios 1 to 7 MUST be checked on each candidate's server
  build, with oracles that do not use the engine under test:
  - penetration of the terrain and meshes is measured against the core's collision world;
  - for each chassis pair, separation and penetration depth are computed as oriented boxes from the
    canonical state: position, orientation and the configured chassis dimensions;
  - speeds, headings, distances, contact flags and the turning radius are read from the canonical
    state.

  A candidate that misses an outcome is Failed (`BehaviorCheckFailed`), naming the scenario and the
  measured value. Each check's verdict is recorded per candidate in the selection report.
- **FR-035**: A fixture engine MUST stand behind the same adapter interface. It is a small test-double
  vehicle model, written for the tests in engine-neutral C# under the full analyzer. It is built for
  the server and the browser like a candidate, and it is never a candidate of the real decision. Its
  configuration selects at most one fault from a committed fault table. The table has one fault per
  check of US1 scenarios 1 to 11, and one per status row of FR-037 that a vehicle model can cause.
  Each fault is designed to miss exactly its target. Examples: a drive force that overshoots the top
  speed by 3%; an accumulator outside the canonical state, which breaks reset equivalence; and, in
  the browser build only, a one-ulp perturbation of `vehicles[0].position.x` at step 15. The behavior
  checks, the agreement suite, the status table, the divergence measurement and the selection's
  orchestration MUST be tested on the fixture engine. Their tested outcomes then do not depend on
  which real engine passes.

**State, hash and formats**

- **FR-012**: Canonical state serialisation version 2 MUST hold version 1's content followed by the
  vehicles, in ascending vehicle ID, and the step's vehicle events. Each vehicle holds its ID, chassis
  position, orientation, linear and angular velocity, per-wheel state (steering angle, spin, suspension
  compression and contact) and the last input sequence applied. The vehicle events are the vehicle
  contacts that began in the step, and the vehicle input rejections. The core derives a contact from
  the canonical state, not from the engine. A contact begins in a step when the pair's chassis
  oriented-box separation (FR-011) is at most the contact tolerance at the end of the step, and was
  not at the end of the step before. Contacts are listed by ascending pair of vehicle IDs. The state
  hash MUST cover all of it, with 015's hash, negative-zero canonicalisation and non-finite refusal
  (015's FR-016 and FR-017). Each step's record MUST also carry the version 1 part hash: 015's hash
  over the state's version 1 part (characters, weapons and projectiles), serialised exactly as
  version 1 serialises it. The non-vehicle state can then be compared bit for bit on its own.
- **FR-013**: The canonical state MUST be complete: a world loaded with any committed state and
  stepped on MUST give the same per-step hashes as the world that produced that state (reset
  equivalence). On the server build, the world is reloaded with its own committed state (US1
  scenario 10). On each browser build, it is reloaded with the server build's committed state, which
  makes it a cross-build check (US2 scenario 4).
- **FR-014**: Recorded stream format version 2 MUST carry everything version 1 carries, and also:
  - the vehicle configuration;
  - the initial state in serialisation version 2;
  - commands addressed to vehicles as well as characters;
  - the engine's identity (engine, pinned release and build-settings fingerprint);
  - the vehicle rules revision;
  - for each step, the expected full state hash and the expected version 1 part hash.

  It MUST NOT carry the selection configuration (FR-036). A stream whose engine identity or vehicle
  rules revision differs from the build's MUST be NotComparable, naming both values.
- **FR-015**: Version 1 MUST keep working unchanged. The decoders MUST accept stream versions 1 and 2,
  and run a version 1 stream under state serialisation version 1. 015's 19 committed streams,
  `simcore/content/test-world.mmcd` and the core's rules revision MUST NOT change. A change to vehicle
  rules MUST bump the vehicle rules revision instead. A committed SHA-256 manifest of those 19 stream
  files and of `simcore/content/test-world.mmcd`, taken at commit 65112ab (015's merge to `main`),
  MUST be compared with the files in the repository by a test. The same test MUST check that
  `SimCoreVersion.RulesRevision` is 1.
- **FR-016**: The new versions MUST have committed golden fixtures that round-trip to identical bytes,
  and refusal fixtures for an unknown version, truncation, oversize and inconsistency. 015's refusal
  fixture that used stream version 2 as its unknown version MUST move to a version that is still
  unknown, so that it keeps testing a refusal.
- **FR-017**: Each candidate's committed vehicle streams MUST carry expected hashes recorded by that
  candidate's server build, and MUST change only through `simcore streams record --reason` (015's
  FR-028).

**Agreement and qualification**

- **FR-018**: The agreement suite MUST run each built candidate's committed and generated vehicle
  streams through its server build and through every required build of each of the three build
  modes. It MUST compare every step's record byte for byte, and report the first divergent step of
  each stream, with each differing field in exact bit form (015's FR-026). The required builds extend
  015's build names (`contracts/simcore-cli.md`, Build names):
  - `interp-prebuilt`: 015's `node-interp`, `node-interp-nojit` and `chromium-worker`;
  - `interp-relinked`: `node-interp-relinked`, `node-interp-relinked-nojit` and
    `chromium-worker-relinked`;
  - `aot`: `node-aot` and `chromium-worker-aot`, with no jiterpreter variant, because the
    jiterpreter speeds up only interpreted code.

  Each mode's `--require` set is its required builds. The selection run runs every one of them for
  every candidate. The vehicle agreement job runs them for the chosen engine in each mode the record
  names it for (FR-029). A mode the toolchain did not produce is NotBuilt (`ModeNotProduced`), with
  its reason. A report that lacks a mode for a candidate fails validation.
- **FR-019**: A candidate MUST be Qualified in a build mode only if all of these hold in that mode:
  - it built for both targets within their bounds (FR-021);
  - its license scan passed (FR-031);
  - every check of US1 scenarios 1 to 12 passed on its server build (FR-011, FR-034);
  - its generated streams met their coverage (FR-010);
  - its server build reproduced every committed hash in two processes;
  - every vehicle stream matched on every required build of that mode;
  - reset equivalence held on the server build, and from the server's states on every required
    build of that mode (FR-013).

  Otherwise its status MUST be the one FR-037 gives.
- **FR-037**: Each condition MUST map to exactly one status and reason code, as below. Each reason
  carries the details listed beside its code.

  | Condition, in a build mode | Status | Reason code: details |
  | --- | --- | --- |
  | Rapier's fit check failed a criterion (FR-030) | NotTested | `FitCheckFailed`: the criterion |
  | A server or browser build failed, or ran past its 15-minute bound | NotBuilt | `BuildFailed` or `BuildTimedOut`: the build, the failing step, the last 50 lines of its output |
  | The toolchain did not produce the mode, or a required build of it did not run | NotBuilt | `ModeNotProduced` or `BuildNotRun`: the build and the reason |
  | The license scan found a component outside FR-031 | LicenseBlocked | `LicenseNotAllowed`: the component and its license expression |
  | A check of US1 scenarios 1 to 7 missed | Failed | `BehaviorCheckFailed`: the scenario, the measured value and the bound |
  | The server build missed a committed hash, or two runs differed (US1 scenarios 8 and 9) | Failed | `ServerNotReproducible`: the stream and step |
  | A world reloaded on the server build differed (US1 scenario 10) | Failed | `ResetNotEquivalent`: the stream, the restart step and the first differing step |
  | A rejected command did not step the neutral input (US1 scenario 11) | Failed | `RejectionNotNeutral`: the stream and step |
  | The steady-state step took a lock (US1 scenario 12, FR-034) | Failed | `TakesLock`: the function |
  | A step faulted: a non-finite field, or a vehicle out of bounds | Failed | `StepFaulted`: the stream, the step and the fault |
  | A committed vehicle stream of the candidate was NotComparable, or in an unsupported version | Failed | `StreamNotComparable`: the stream and both values |
  | A generated stream missed its coverage (FR-010) | Failed | `CoverageNotMet`: the seed and the condition |
  | A browser build's record differed from the server's (US2 scenario 3) | Diverged | `RecordDiverged`: the build, the stream, the first divergent step and the fields |
  | A browser build reloaded with the server's state differed (US2 scenario 4) | Diverged | `ResetDiverged`: the build, the stream, the restart step and the first differing step |
  | None of the above | Qualified | none |

  When several conditions hold, the status is the first one in the order NotTested, NotBuilt,
  LicenseBlocked, Failed, Diverged, and the report lists every reason. The report MUST hold one
  status per candidate and build mode. For each required build of that mode, it MUST hold one result:
  Match, Diverged or NotRun. The exit codes MUST be these:
  - `simcore agreement compare` and `simcore streams verify` keep 015's: 0, then 1 for a divergence
    or a validation failure, and 2 for a NotComparable stream or a check that cannot run.
  - The selection run exits 0 whenever every candidate has a status in every mode, Diverged and
    Failed included. It exits 1 when its report fails validation, and 2 when it cannot run.
  - The decision command exits 0 for every outcome, None included. It exits 1 when its input report
    fails validation, and 2 on a usage error or an unreadable input.
- **FR-020**: The batched step entry point (`BatchStepper`, and the browser build's `StepQueued`
  export) MUST step queued commands for a driven vehicle and return each step's state and hash, the
  same as stepping them one at a time.
- **FR-021**: A candidate that fails to build, run or measure MUST be recorded with its status and
  reason, and MUST NOT stop the evaluation of the other candidates. Every candidate's server build
  and browser build MUST each complete within 15 minutes on the CI runner class. The step's timeout
  enforces the bound. A build that fails, or runs past its bound, is NotBuilt (FR-037).

**Divergence measurement**

- **FR-022**: The selection MUST measure each built candidate's windowed divergence (US5). Each
  required browser build is loaded with the server build's state at the start of every window of
  ⌈w × step rate⌉ steps, where w is the window, 0.5 s. The measurement records the position,
  orientation and linear velocity error of every vehicle at every step. It reports the maximum and
  p99 per stream, required build and build mode. The bounds apply to position (0.05 m) and
  orientation (1 degree). Linear velocity error is reported and not bounded (Assumptions). The window
  and the bounds are in the selection configuration (FR-036). A test MUST check the window against
  every committed network profile (`harness/profiles/*.json`), at each tested step rate. For each
  profile, the window's step count must be at least ⌈(round trip + 2 × jitter) × step rate⌉ steps,
  plus 2 steps of input buffer, plus 1 step for a lost state update when the profile's loss is
  above zero. A profile or rate that the window does not cover fails the test, naming the profile,
  the rate and both spans (US5 scenario 6).

**Selection configuration**

- **FR-036**: The selection's own thresholds MUST be a validated selection configuration. It is kept
  apart from the vehicle configuration that streams carry, and from the vehicle rules. It holds:
  - the behavior tolerances of US1 scenarios 1 to 7: 95% and 2% of top speed, 10 degrees from
    upright, 1 second to wheel contact, 0.1 m/s at rest, 120% of the ideal braking distance, the
    turning circle's 5 ± 0.5 m/s, 15 seconds and 25%, 2 seconds to settle after landing, and 3 steps
    and 10% for a collision;
  - the divergence window (0.5 s) and its bounds (0.05 m and 1 degree);
  - the tie band of the decision rule (10%).

  An invalid selection configuration MUST be refused with an error naming each invalid field.
  Changing it changes no stream and no vehicle rules revision. The decision record fingerprints it
  as an input of its own (FR-028). The contact tolerance is a simulation input, because the core's
  contact events read it, so it stays in the vehicle configuration (FR-005).

**Cost**

- **FR-023**: Each built candidate's server build MUST have benchmarks reporting the mean and p99 cost
  of one world step and of one vehicle step with 1, 16, 64 and 256 vehicles, labeled as measurements
  with the runner class.
- **FR-024**: The selection MUST record, for each built candidate and build mode, the browser build's
  mean and p99 cost per step with 1, 16 and 64 vehicles in Node and in a Chromium Worker, the time to
  the first completed vehicle step over 10 cold starts, and the download bytes (raw, Brotli and gzip)
  it adds to the shared core's own browser build in the same mode. For Jolt and Rapier, the step cost
  includes the crossing into the engine's module. Their added bytes include that module and its
  JavaScript glue, which the selection run compresses the way 015's measurement compresses every
  file (FR-032). A mode or candidate that could not be measured MUST be named with the reason.
- **FR-025**: After warm-up, every candidate's vehicle step MUST allocate no managed memory, verified by
  zero-allocation tests in CI. All candidates MUST be measured in one job run on one runner class: the
  selection run, in the `SimCore` workflow, on every pull request that changes the vehicle code, a
  candidate's build, the vehicle streams or the selection configuration, and on demand.

**Decision**

- **FR-026**: The decision rule MUST be applied separately in each of the three build modes, to that
  mode's statuses and numbers in the validated selection report. A value x ties with the lowest value
  L of its kind when x ≤ 1.10 × L, inclusive. The factor is 1 plus the selection configuration's tie
  band, 10% by default (FR-036). The comparison is exact, on the report's decimal values.
  1. If one or more candidates are Qualified, the cost-tied set is every qualifier whose browser cost
     per step (one driven vehicle, Chromium Worker, mean) ties with the lowest. If it holds one
     candidate, that candidate wins. Otherwise it narrows to those whose added Brotli download bytes
     tie with the smallest among them. If one remains, it wins. Otherwise the one with the lowest
     browser cost per step wins, then the one with the fewest added Brotli bytes. The order Bepu,
     Jolt, Rapier decides only when both values are exactly equal. The outcome is BitExact.
  2. Otherwise, Bepu wins with the outcome NonExact if two things hold. First, Bepu is Diverged in
     that mode: every check it missed is a cross-build one (`RecordDiverged`, `ResetDiverged`;
     FR-037), so it passed every server-side check, including server-build reset equivalence (US1
     scenario 10, FR-013). Second, its divergence (FR-022) is within bounds on every stream and
     required build of that mode. The record is provisional (FR-027).
  3. Otherwise that mode's outcome is None.

  If the winner or outcome differs between build modes, the decision MUST name the outcome and winner
  in each mode, and be conditional on gate 9's WebAssembly build-mode decision.
- **FR-027**: The decision command MUST write a decision record that holds:
  - the outcome and winner in each build mode, and whether the choice is conditional;
  - the rule applied and each step of its reasoning, with each tied set;
  - every candidate's status and reasons per build mode;
  - the deciding numbers (browser costs, download sizes, divergence maxima);
  - each engine's identity, the runner class, and the commit and CI run measured;
  - a fingerprint of the inputs (FR-028).

  The record MUST also state these, as evidence that does not decide:
  - whether the winner's vehicle loads exactly into another world on the server build, from US1
    scenario 10. Phase 2's merge and split callbacks need this.
  - how each candidate's vehicles would step in a parallel Movement phase. The whole vehicle world is
    one worker's slice. For an engine that documents results independent of thread count, and whose
    pinned release can run that way with its determinism setting on, the record also states whether
    its committed streams give the same per-step hashes at 1 and 4 threads. That run is a separate
    evidence run, never the gameplay configuration and never part of a status. Where it was not
    made, the record states why.
  - for Bepu, the server-wide effect of its 128-bit vector pin.

  A NonExact record MUST carry a provisional flag with its exact condition: "vehicle correction
  distances under the 014 network profiles (`harness/profiles/*.json`) are not measured against
  stated bounds". It MUST state that bit-exact agreement is given up for every world that contains
  vehicles, including after vehicle–character interaction lands. A provisional record does not by
  itself meet gate 9's vehicle exit criterion, and the record says so. The same report MUST always
  give the same record, byte for byte. The record is committed and is gate 9's decision for
  "Vehicle physics engine".
- **FR-028**: A staleness check MUST fail when the committed record's fingerprint no longer matches the
  repository. The fingerprint covers the engines' identities, the vehicle rules revision, the vehicle
  configuration, the selection configuration and the committed vehicle streams. The check MUST name
  each input that changed. It MUST print the record's provisional flag when the flag is set.
- **FR-029**: Once a record is committed, a vehicle agreement job in the `SimCore` workflow MUST run on
  every pull request that changes the core, the vehicle code, the vehicle streams, the engine builds,
  the selection configuration or the record. It runs in each build mode the record names a winner
  for, across that mode's required builds (FR-018).
  - With the outcome BitExact, it MUST fail on any divergent step of the winner's vehicle streams.
  - With NonExact, it MUST fail on a missed committed hash on the server build, a divergence bound
    exceeded, or any divergent step of the version 1 part hash (FR-012). It MUST print the
    provisional flag in its log and summary.
  - A conditional record gates each mode's winner in that mode.
  - With the outcome None in every mode, it MUST gate nothing and report the outcome in the job
    summary.

  It gates the pull request as the `agreement` job does. It MUST have its own 15-minute bound in each
  build mode it runs, so that 015's `agreement` job and its 10-minute bound are unchanged.

**Rapier fit**

- **FR-030**: The fit check MUST evaluate Rapier against these criteria and record each verdict with
  its evidence: (a) its published C API and WebAssembly build cover every operation of the adapter
  interface, with no patch to Rapier's sources; (b) it builds from pinned sources with its
  enhanced-determinism feature and locked dependencies; (c) its license scan passes (FR-031); (d) its
  adapter implements the shared interface unchanged. A static check that can run on `main` tests
  this: no file of the scenarios, formats, runners, shared adapter interface or other adapters
  references Rapier (its library, namespace or adapter types), and Rapier's adapter compiles against
  that interface as Bepu's and Jolt's do; (e) each of its two builds completes within the 15-minute
  bound every candidate has (FR-021), and the flat-acceleration stream runs to completion on both
  builds; (f) the Rapier-specific code written for it (shim, bindings and build scripts) is at most
  1,500 non-blank lines. Rapier is a full candidate only if all six pass. Otherwise it is NotTested
  (`FitCheckFailed`). The real Rapier's verdicts are whatever the report records. The orchestration's
  handling of each verdict is tested with committed fit-check fixtures (US6).

**Licensing**

- **FR-031**: An automated license scan MUST cover every component a candidate adds beyond the shared
  core's own builds. That is the set of files the candidate's server process loads and its browser
  build ships, with their transitive dependencies and the toolchain runtime libraries in them, minus
  the set that the core-only server and browser builds already load and ship. The core-only builds
  hold the .NET runtime, the operating-system libraries and the .NET WebAssembly runtime, the same
  for every candidate. Every component in the difference MUST be MIT or Apache-2.0, or an approved
  exception of Principle VII.
  - An SPDX `OR` expression passes when one of its options passes, and an `AND` expression passes
    only when every part passes.
  - `Apache-2.0 WITH LLVM-exception` passes as Apache-2.0 (Assumptions, "License scan boundary").
  - Emscripten builds use `emmalloc` (`-sMALLOC=emmalloc`, under Emscripten's `MIT OR NCSA`) instead
    of the default dlmalloc, which is dedicated to the public domain (CC0-1.0). This is a build
    setting in the engine's identity (FR-002).

  A candidate with any other component MUST be LicenseBlocked (`LicenseNotAllowed`), naming the
  component and its license expression. The record lists that component as a candidate for a PATCH
  amendment.

### Key Entities

- **Candidate**: one engine under evaluation (Bepu, Jolt or Rapier), with its identity: the engine,
  its pinned release, and a fingerprint of its sources, toolchains and build settings.
- **Vehicle configuration**: the vehicle model's parameters, the substeps and the contact tolerance:
  what the step reads, validated before any step and carried by every vehicle stream.
- **Selection configuration**: the behavior tolerances, the divergence window and bounds, and the tie
  band (FR-036). These are what the checks and the decision read. They are validated, kept out of
  every stream, and fingerprinted by the decision record.
- **Fixture engine**: the test-double vehicle model behind the adapter interface, with its committed
  fault table (FR-035). It is never a candidate of the real decision.
- **Vehicle state**: one vehicle's ID, chassis position, orientation, linear and angular velocity,
  per-wheel state and last input sequence, inside canonical state version 2.
- **Vehicle stream**: a recorded stream in format version 2 for one candidate: header, configuration,
  engine identity, vehicle rules revision, initial state, commands for vehicles and characters, and the
  expected full state hash and version 1 part hash after each step.
- **Agreement status**: per candidate and build mode, one status from FR-037's table (Qualified,
  Diverged, Failed, NotBuilt, NotTested or LicenseBlocked) with every reason code. Per required build
  of that mode, one result: Match, Diverged or NotRun.
- **Divergence result**: per candidate, stream, required build and build mode: the maximum and p99
  position, orientation and velocity errors, where each maximum occurred, and the bound verdict.
- **Selection report**: the validated output of one selection run: every candidate's statuses, behavior
  results, costs, download sizes, startup times, divergence results and fit-check verdicts, with the
  runner class and the commit.
- **Fit check result**: Rapier's six criteria, each with a verdict and its evidence.
- **Decision record**: the outcome (BitExact, NonExact or None), the winner per build mode, the
  reasoning, the evidence and the input fingerprint. It also holds the provisional flag of a NonExact
  outcome and the evidence that does not decide (FR-027). It is gate 9's vehicle-physics decision.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: The shared simulation core and its tooling: the core's vehicle state and
  formats, one adapter and one browser host project per candidate, the fixture engine, the
  candidates' native and WebAssembly builds, the `simcore` commands, the agreement runners and the
  `SimCore` workflow. No running service, web surface or network endpoint is added.
- **State ownership**: The core still owns no state between steps. A vehicle world, the engine's own
  world included, has one writer: in the continent server (sub-spec 5) the region that owns the
  vehicles, and on the client the Worker that owns the predicted copy. Engines step on the calling
  thread only. So in a parallel Movement phase, a region's whole vehicle world is one worker's slice.
  The record states what each engine would allow beyond that (FR-027).
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
  golden fixtures and the version 1 part hash (FR-012, FR-014). Version 1 is unchanged, and a
  committed manifest pins it (FR-015). The input command and collision data formats stay at version 1.
  The decision record, the selection report, the selection configuration and the status table
  (FR-037) have documented, validated shapes.
- **Performance and evidence**: The vehicle step is tick-path code: zero-allocation tests,
  benchmarks and the lock test are required (FR-023, FR-025, FR-034). Every cost, size and
  divergence figure is a measurement with its runner class, build mode and engine identity. Plan
  estimates stay labeled as estimates. Measured numbers decide the engine but never fail CI. Vehicle
  values are placeholders in the vehicle configuration. The behavior tolerances, the divergence
  window and bounds and the tie band are placeholders in the selection configuration (FR-036). This
  feature sets no tuning value of Principle VI.
- **Security and abuse controls**: Commands, streams, states and collision files are untrusted and
  decoded with bounded counts and no partial results (015's FR-020). The native and JavaScript
  boundaries check every length and count before anything crosses them. Engine sources and
  toolchains are pinned by version and checksum (FR-002). No authentication or authorization: these
  are a library and tools.
- **Observability**: N/A for the library: the vehicle step does no I/O and logs nothing. The `simcore`
  commands log Serilog compact JSON on stderr. The selection job writes its report and the decision
  as files, shows them in the job log and summary, and uploads them as artifacts.
- **Licensing and design sources**: The candidates are Apache-2.0 (Bepu, Rapier) and MIT (Jolt). Every
  component a candidate adds beyond the core-only builds is license-scanned (FR-031). A candidate
  that needs a package outside MIT and Apache-2.0 is LicenseBlocked. Adding that package would take a
  PATCH amendment, which no automation makes. The scan's boundary decides nothing about how Principle
  VII treats platform and toolchain runtime libraries or licenses with exceptions. That question is
  reported once, in the implementing pull request's patch-notes entry (Assumptions, "Follow-ups for
  the patch notes"), and it does not block the merge. Design sources (Bepu's documentation and
  roadmap, Jolt's determinism documentation, Rapier's determinism guide, and the vehicle-model
  references) are recorded in `research.md`.
- **Accessibility**: N/A. No web surface.
- **Quality gates**: 95% line coverage for changed `src/` files, the C++ and Rust shims included, each
  measured with its own toolchain's coverage tool. Theories cover each candidate, each scenario, each
  step rate, each required build, each build mode, each status row, each fixture-engine fault and
  each branch and band boundary of the decision rule. Tests are in process except the fresh-process
  runs, the engine builds and the WebAssembly runs. Those use unique temporary directories and
  dynamic ports, so they are safe in parallel and against leftovers. Zero-allocation tests cover
  every candidate's vehicle step, and the lock test covers every candidate's step (FR-034).
- **Executable and living spec**: Every acceptance scenario maps to an automated test named in
  `tasks.md`, and every scenario, requirement and success criterion this spec states as current
  behavior has a test that passes on `main` at merge. What a given real engine does is evidence in
  the committed selection report, not a current-behavior claim. So the checks, statuses and
  orchestration are tested on the fixture engine and on fixture reports and fit-check fixtures
  (FR-035, US6), and the winner's checks are a real test on `main` (US1 scenario 14). The decision
  record and the selection report it was made from are committed in the same pull request. The
  staleness check (FR-028) keeps them true afterwards. Where a 015 statement becomes incomplete (its
  state hash covers characters, weapons and projectiles; its stream decoder supports version 1),
  015's `spec.md` is updated in the same pull request.
- **Spec independence and parallelism**: One documented hard dependency, already met: 015's shared
  core, formats, agreement runners, `simcore` CLI and `SimCore` workflow, all merged on `main`. No
  sibling spec of sub-specs 4 to 9 needs to land first, and none waits on this one except gate 9's
  decision table. A NonExact record's provisional flag names a measurement that this feature does
  not make. `plan.md` documents that as sub-spec 6's dependency on this record (Principle X), and
  nothing here waits on sub-spec 6.
- **Open decisions**: None is settled. The vehicle physics engine is not on the constitution's list; it
  is gate 9's decision, made by FR-026. Open decision 3 (tick rate) stays open: vehicles step at the
  configured rate, tested at 20, 30 and 60 Hz. Open decision 5 (hit detection) is untouched:
  projectiles do not hit vehicles in this slice. Open decisions 4 and 9 are untouched. Gate 9's other
  open choices stay open: the WebAssembly build mode (the rule runs per mode, FR-026) and parallel
  phases. On parallel phases: engines run single-threaded, so the single-world model makes a
  region's vehicles one slice of the Movement phase. The record's per-candidate evidence (FR-027)
  shows what each engine would allow beyond that, so gate 9 does not lose the option unnoticed.
- **Autonomous delivery (XIII)**: Every check is an automated test or CI job, the decision is computed
  by a command from CI evidence and committed by an agent, and nothing is assigned to a person. Every
  ambiguity is recorded below as an assumption. A license that would need an amendment is reported as
  LicenseBlocked, and the selection goes on without that candidate. Questions only the maintainer can
  settle are reported once as patch-notes follow-ups, and they never block the merge.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every built candidate is evaluated on at least 12 named vehicle streams and 50 seeded
  streams of 8 vehicles, on every required build of each of the three build modes. It ends each mode
  with exactly one status from FR-037's table. A report that lacks a mode fails validation.
- **SC-002**: 015's 19 committed streams and `simcore/content/test-world.mmcd` match the committed
  SHA-256 manifest taken at commit 65112ab, and Match in every required build. 015's `agreement` job
  stays within its 10-minute bound.
- **SC-003**: The decision rule gives its stated outcome on every fixture report: each branch, each
  band boundary, the mixed-mode case, and a cheapest candidate that is Diverged. The committed
  decision record is reproduced byte for byte from the committed selection report.
- **SC-004**: A deliberate one-ulp change to a vehicle field in one build is reported at its first
  divergent step, never only at the end of a stream.
- **SC-005**: Steady-state vehicle steps of every candidate allocate zero bytes of managed memory after
  warm-up.
- **SC-006**: The selection report records, for every built candidate, the server cost per vehicle step
  at 4 vehicle counts, the browser cost per step at 3 vehicle counts in 2 runtimes per build mode, the
  startup time and the download bytes added, each with its runner class.
- **SC-007**: The chosen engine's vehicle agreement job completes in under 15 minutes per build mode,
  and the whole selection run in under 120 minutes, on a CI runner: each job's time bound fails a run
  that does not.
- **SC-008**: A decision record is committed, its staleness check passes on `main`, and it gives gate 9
  an outcome for the vehicle physics engine, with its evidence. The outcome is an engine that agrees
  bit for bit; Bepu with non-exact vehicles, which is provisional and does not by itself meet gate 9's
  vehicle exit criterion; or none.
- **SC-009**: On the fixture engine and the fixture inputs, every check of US1 scenarios 1 to 12 and
  every row of FR-037's status table give their stated result. With no fault, the fixture engine is
  Qualified. Each fault or fixture input produces exactly its targeted status and reason code.

## Assumptions

- **Not an open decision.** The constitution's Open decisions list (1 to 10) was checked: section and
  claim sizes, fight size, tick rate, browser transport, hit detection, designed boundaries, Orleans,
  Nakama, anti-cheat and game-database hosting. The vehicle physics engine is not on it. Issue #12
  lists it in gate 9's decision table with the rule "sub-spec 3's agreement-and-cost test", so this
  spec defines that rule (FR-026), and the result is recorded as gate 9's decision with its evidence.
  No amendment is needed or made. `docs/roadmap/` does not exist; issue #12 is the Phase 1 source.
- **Exactness first.** The issue orders the rule as "qualifies only if the hashes match", then "among
  qualifiers, browser step cost, then download size", and calls non-exact Bepu "a valid outcome". Gate
  9's exit criterion asks for identical state hashes. So a bit-exact qualifier always wins over
  non-exact Bepu, and non-exact Bepu is chosen only when no candidate qualifies. With a NonExact
  outcome, version 1 streams, which are worlds without vehicles, still require identical hashes for
  characters, weapons and projectiles. In version 2 streams, the version 1 part hash (FR-012) is
  still required bit for bit. That holds only while characters and vehicles do not interact, as in
  this slice. A NonExact record therefore states that bit-exact agreement is given up for every world
  that contains vehicles. Once vehicle–character interaction lands (sub-spec 5), non-exact vehicle
  state flows into character state, and mixed worlds stop agreeing on the version 1 part too. That
  cost to sub-specs 5 and 6 is a patch-notes follow-up. Reading the issue the other way, letting
  non-exact Bepu compete with qualifiers on cost, is the rejected alternative.
- **Corrections, before sub-spec 6.** The issue's text is "vehicle corrections stay small under the
  network profiles". The Input above shortens it. No reconciliation or correction-distance logging
  exists yet, so this spec measures "vehicle corrections stay small" as windowed divergence (US5,
  FR-022). The window is ⌈0.5 s × step rate⌉ steps. It covers the reconciliation span of the worst
  committed network profile, Poor: its 250 ms round trip, plus two standard deviations of its 50 ms
  jitter, plus the 1–2 tick starting input buffer (issue #12, sub-spec 5), plus one step for a lost
  state update. At Poor's 3% loss, the expected number of lost updates in a window is under one at
  every tested rate: at most 0.9, at 60 Hz with a state update every step. So one step covers the
  expected case. Two lost in a row (about 0.09% of updates) are left to a real correction
  measurement. Poor needs 10 steps at 20 Hz, exactly the window, 14 of the 15 at 30 Hz and 24 of the
  30 at 60 Hz. Every other committed profile has a smaller round trip, jitter and loss. A test reads every profile and checks this (US5
  scenario 6), so a new or changed profile that the window no longer covers fails it. The bounds,
  0.05 m and 1 degree, are under a tenth of one step's travel at the placeholder top speed (20 m/s is
  0.67 m per step at 30 Hz). That is small enough for the on-screen smoothing of small corrections,
  over about 100 ms, that issue #12 describes. They are placeholders in the selection configuration. This feature measures no correction distance, so a NonExact record
  carries a provisional flag whose condition names that missing measurement (FR-027). This feature
  builds the flag, prints it in the vehicle agreement job and the staleness check, and does not
  count a provisional record as meeting gate 9's vehicle exit criterion. Measuring correction
  distances, and clearing or overturning the flag, is follow-up work. `plan.md` records it as
  sub-spec 6's dependency on this record, and the patch notes list it.
- **Behavior tolerances and time bounds.** The numbers in US1 scenarios 1 to 7 are defaults that the
  agent chose. Their job is to catch a vehicle model that is wrong, not to grade a good one. They
  decide whether a candidate is Failed, so they are values in the validated selection configuration
  (FR-036), as Principle VI asks of values that decide, and not test constants. Retuning one changes
  no code and no stream, and it stales the record. All of them are estimates, not measurements. If
  every candidate misses a check, the record shows each measured value beside its bound, so a bad
  default shows up as one. Their reasons:
  - *95% of top speed within 10 s.* A drive force limited by speed approaches the top speed
    asymptotically, and drag and rolling resistance hold it a little below. 10 s is expected to be
    long enough for the placeholder drive force to get there. A vehicle that cannot drive misses it
    by far.
  - *At most 2% over the top speed.* One step's speed gain is expected to be well under 2% of the top
    speed, so the band allows the step in which the cap engages. A missing cap overshoots without
    bound.
  - *Upright within 10 degrees, and four wheels in contact after 1 s.* Squat under throttle and dive
    under braking tilt a sprung chassis by a few degrees, and a vehicle spawned just above the ground
    settles within a second. A flip, or a wheel that never touches, misses them.
  - *Stopping within 120% of v² / (2b), and rest below 0.1 m/s.* v² / (2b) is the distance at a
    constant deceleration b. The extra 20% allows for the step the brake takes to apply, load
    transfer, suspension pitch and tire slip. A brake that does not apply misses it by far. 0.1 m/s
    is about 3 mm per step at 30 Hz.
  - *Turning circle within 25% of L / tan(δ).* The kinematic bicycle model ignores tire slip, which
    widens the circle as lateral acceleration grows. The 25% is the slip allowance at 5 m/s. A
    missing or inverted steering model drives straight or turns the other way. 15 s for the full
    turn is several times the placeholder geometry's ideal turn at 5 m/s. The ±0.5 m/s band keeps the
    radius measured at the stated speed.
  - *2 s to settle after landing.* A sprung chassis rebounds a few times after a jump. A vehicle that
    never regains contact misses it.
  - *A closing speed of at most 10% of the approach within 3 steps.* A solver with substeps resolves
    a contact over one to a few steps (100 ms at 30 Hz). Vehicles that pass through or sink into each
    other keep their closing speed.
  - *Linear velocity error is reported and not bounded (US5).* A velocity error shows up within the
    window as position error, which is bounded, and each window restarts from the server's state. A
    velocity bound would count the same error twice.
  - *Time bounds.* The 15 minutes per build, the vehicle agreement job's 15 minutes per mode and the
    selection run's 120 minutes are CI step and job timeouts, not configuration. On `main`, 015's
    `agreement` job took 2.3 minutes and its `measure` job took 17.5 minutes (run 45066104551, a
    measurement on the runner class). The selection does the measure job's work for three
    candidates, plus their vehicle streams and up to two uncached engine builds. That is an estimate
    of 60 to 80 minutes, so 120 minutes leaves headroom. A vehicle agreement run with cached engines
    is an estimate of a few minutes per mode, and 15 minutes leaves room for one uncached build.
- **Rapier's fit, as a time box.** "Fits the same scenario harness cheaply" is the six criteria of
  FR-030. Rapier's work is the last candidate phase of this feature's tasks, and its exit is the fit
  check: when a criterion fails, Rapier is recorded as NotTested with that criterion and the feature
  goes on without it, rather than spending more effort. The 1,500-line cap stands in for effort
  because it can be counted: it is about the size we expect one engine's shim and bindings to need
  (an estimate, not a measurement), so a Rapier that needs much more than Jolt does is not cheap.
  Criterion (d) is a static check. "No change since before Rapier" would need a baseline commit, and
  a squash merge does not keep one on `main`. Instead, the check finds no reference to Rapier
  outside Rapier's own files. That can run on `main` at any time.
- **Versions, not rewrites.** Vehicle state joins the canonical state through state serialisation
  version 2 and stream format version 2 (FR-012, FR-014), and the decoders keep version 1 (FR-015).
  The core's `SimCoreVersion.RulesRevision` names the character, weapon and projectile rules, which do
  not change, so it stays at 1 and 015's streams stay byte-identical with their committed hashes. The
  vehicle rules get their own revision, and the engine's identity sits beside it in the stream,
  because each candidate gives different results under the same rules. The vehicle test world is a new
  collision file, because regenerating `test-world.mmcd` would change its checksum and refuse every 015
  stream. 015's stream refusal fixture that used version 2 as its unknown version moves to a version
  that is still unknown, through `simcore golden write`, and its message now lists versions 1 and 2.
  "Unchanged" needs a baseline that does not move when this feature merges. "`main` before this
  feature" moves. So the baseline is a committed SHA-256 manifest of 015's stream files and test
  world, taken at commit 65112ab, 015's merge to `main` (FR-015). Version 2 records also carry the
  version 1 part hash, because the full state hash alone could not show that the non-vehicle state
  still agrees in a world with non-exact vehicles.
- **Per build mode.** 015 left gate 9's WebAssembly build mode open (the prebuilt interpreter, the
  relinked interpreter or AOT). Every candidate's browser build exists in all three modes (FR-032).
  Bepu runs inside the .NET runtime, so its whole step depends on the mode (015's prototype measured
  about 95 µs interpreted and 14 µs AOT for one character step). Jolt's and Rapier's engine modules
  are compiled by their own toolchains whatever the mode. But their adapters and the crossing run in
  the .NET mode, so their step cost is measured per mode too. Applying the rule in each mode keeps
  the build-mode decision open: when the winners differ, the record is conditional on it.
- **Browser integration of Jolt and Rapier.** The issue gives Jolt an Emscripten build and Rapier a
  WebAssembly build in the browser, beside the .NET WebAssembly core. There are two ways to join them.
  - Linking the engine into the .NET runtime's own module (`NativeFileReference`). This needs the
    `wasm-tools` workload's relink and the Emscripten version that .NET 10's workload pins. Jolt and
    Rapier then could not exist in `interp-prebuilt`, the mode 015's gating `agreement` job tests.
    Rapier would also need the `wasm32-unknown-emscripten` target at .NET's Emscripten version,
    rather than its usual `wasm32-unknown-unknown` build.
  - A separate module called through `[JSImport]`. This works in all three modes and lets each
    engine pin its own toolchain (FR-002). The price is a crossing on every step.

  This spec takes the separate module (FR-032). It counts the crossing in the step cost and the
  module and glue in the download, and it does not use static linking for this selection. If a
  candidate loses only on the crossing, the record's numbers show that, and static linking can be
  measured as a follow-up. Each candidate has its own browser host project, so 015's
  `MmoEngine.SimCore.Wasm`, its publish and its `agreement` job do not change.
- **The deciding cost.** The browser cost that decides is the mean cost per step of a world with one
  driven vehicle, measured in a Chromium Worker with 015's step-cost method. A client predicts only its
  own vehicle, so that is its cost each frame. Costs with 16 and 64 vehicles, and the server's costs,
  are recorded for the gate-9 sweep and do not decide. The issue orders the rule as cost first, then
  download size, and gives no tie rule. This spec adds one, with the exact formula: x ties with the
  lowest L when x ≤ 1.10 × L, inclusive, for cost and for download size alike. Without a band, the
  candidate with the smaller download could lose on a cost difference within run-to-run noise. A
  candidate is eliminated on download size only when it adds more than 10% more bytes than the
  smallest of the cost-tied set. When both bands still tie, the lowest measured cost wins, then the
  smallest download. Noise can then decide between two near-equal candidates. But a fixed order at
  that point could pick a candidate that is worse on both measured values, such as Bepu at 109 µs
  and 540 KB over Jolt at 100 µs and 500 KB, which contradicts "cost first". The fixed order Bepu,
  Jolt, Rapier, v2's default order with Rapier added last, decides only on exact equality.
- **The vehicle model.** Phase 1 content has one ground vehicle (issue #12, sub-spec 5), so the model
  is one four-wheeled vehicle with keyboard-style digital controls read from 015's input command
  version 1, and no new input format. An adapter may use its engine's own vehicle support, or build the
  wheel model on the engine's rigid bodies and queries, as long as it meets the same behavior outcomes.
  A wheel model built that way is engine-neutral code, so its suspension and tire forces live in the
  core (FR-033). In every candidate, the chassis collision shape is the configured box, with no
  rounding. Wheels collide only with the static collision, and vehicle-to-vehicle collision is
  chassis box against chassis box, so the oriented-box oracle (FR-011) measures what the engines
  collide. Placeholder values: 2,000 kg, a 4.5 × 2 × 1.5 m chassis, a 20 m/s top speed (under the
  simulator's 30 m/s ground-vehicle ceiling), 8 m/s² braking and a 35 degree steering limit. Game
  design replaces them; they are configuration, so replacing them changes no code. The turning-circle
  stream's throttle is chosen when the stream is recorded (US1 scenario 3). A fixed duty cycle would
  hold 5 m/s on one engine and drift out of the band on another, which would test the drive model
  rather than the steering. Recording is deterministic, so the stream still verifies like any other.
- **Out of scope.** Vehicle damage, occupants, seats, entering and leaving, vehicle weapons and turrets,
  tracked vehicles, aircraft, and interactions between vehicles and characters or projectiles. In this
  slice characters and vehicles share a world but do not collide; collisions between them and
  vehicle damage belong to sub-spec 5, and vehicle hits to sub-spec 6.
- **Adapters under the analyzer, no locks, single-threaded engines.** The adapters, the fixture engine
  and the engine step run inside a region's tick, so under the constitution's glossary they are
  gameplay code. Principle II requires a mechanical no-lock check. The analyzer gives the managed
  code that check (FR-004), and the lock test gives it to the engines' native code (FR-034).
  - An adapter's exemption covers only what its engine's interface forces: MMO1001 for Bepu's
    `System.Numerics` API, and MMO1008's native-interop members for Jolt and Rapier. So the time,
    randomness, hash-order, `double`, FMA and lock rules still hold in the adapters. Engine-neutral
    logic sits in the core under every rule (FR-033), so no vehicle logic escapes the analyzer by
    living in an adapter.
  - Principle II's blocking list is read as written. An uncontended mutex is still a lock, so an
    engine whose step takes one is Failed, however little contention there is. Jolt's no-lock body
    interfaces avoid the locks its API offers a choice about.
  - The count sees every lock that calls a pthread function or enters the kernel. A lock that stays
    in user space while uncontended is not seen; Rust's standard mutex is one. So the lock test also
    lists, as evidence, the lock symbols each native library links.

  Each engine steps on the calling thread with no job system, because job systems synchronize, and
  because parallel phases are gate 9's open choice. In a parallel Movement phase, a region's vehicle
  world is therefore one worker's slice, and the record states what each engine would allow beyond
  that (FR-027). Sleeping is off, and engine caches are either in the state or off, so the canonical
  state is complete (FR-013). Their effect on cost is part of what is measured.
- **License scan boundary.** Every candidate's server build runs in the .NET 10 process on Linux,
  which already loads glibc, libstdc++ and libgcc_s. Every browser build ships the .NET WebAssembly
  runtime, which links zlib, Brotli and ICU (the core's own WebAssembly link line). Those libraries
  are the same for all three candidates and for the core without any engine. So the scan covers only
  what a candidate adds over the core-only builds (FR-031). Scanning them for every candidate would
  make the outcome None by construction. Scanning them for some candidates and not others would be
  asymmetric. This reading decides nothing about how Principle VII treats platform and toolchain
  runtime libraries, or licenses with exceptions. That question is a patch-notes follow-up: a PATCH
  amendment the maintainer may make, which does not block the merge. Within the scan, three readings
  apply.
  - An SPDX `OR` expression passes when one of its options is MIT or Apache-2.0, because the
    recipient may choose that option. Examples are Emscripten's `MIT OR NCSA` and most Rust crates'
    `MIT OR Apache-2.0`. An `AND` expression needs every part.
  - `Apache-2.0 WITH LLVM-exception` (libc++, libc++abi and compiler-rt) passes as Apache-2.0. The
    exception only adds permissions, and it takes away no right that Apache-2.0 grants.
  - Emscripten's default allocator, dlmalloc, is dedicated to the public domain (CC0-1.0), which is
    neither license. So Emscripten builds use `emmalloc` instead.

  Any other component outside the two licenses is LicenseBlocked. The record names it as a
  candidate for an amendment, and the selection goes on without that candidate.
- **Statuses, reasons and exit codes.** One status per candidate and build mode keeps the decision
  rule simple, and every reason stays in the report. The order NotTested, NotBuilt, LicenseBlocked,
  Failed, Diverged puts first what stops evaluation earliest.
  - A browser build that diverges after a restart from the server's state is Diverged
    (`ResetDiverged`), not Failed. It is the same cross-build difference that the windowed divergence
    measures.
  - A server build that does not reload its own state exactly is Failed (`ResetNotEquivalent`),
    because its canonical state is incomplete.
  - A candidate's own committed stream that is NotComparable makes it Failed in the selection,
    because a candidate's streams must match its own identity. The suite and the `simcore` commands
    still report NotComparable with exit 2, as 015's do.

  The selection run and the decision command exit 0 for every legitimate outcome, a Diverged
  candidate or a None decision included, because finding out which candidates fail is their job.
  They exit 1 only when a report fails validation, and 2 only when they cannot run.
- **A fixture engine for the checks.** The feature exists to find out which candidates fail. A test
  that asserted a given engine's verdict would either fail on `main` when that engine legitimately
  misses a check, or accept the miss and stop testing its scenario. So the checks, statuses,
  orchestration and decision are tested on the fixture engine and on fixture reports, whose outcomes
  are known. The real engines' verdicts are evidence in the committed report. The winner is the
  exception: the record names it, and its checks are a real test on `main` (US1 scenario 14). The
  fixture engine is a small engine-neutral model, not a physics engine, and it never takes part in a
  real decision. A lock cannot be written under the analyzer, so the lock test's own check uses a
  native test library instead of a fixture-engine fault (FR-034).
- **Generated vehicle streams.** Eight vehicles per seeded stream fill the arena enough for contacts
  in every seed. Twenty seconds of simulated time, at any of the three rates, gives each vehicle room
  to cover 50 m at a 2.5 m/s average, well under the placeholder top speed. The coverage conditions
  catch a generator, or an engine, whose vehicles sit still or never meet and so agree trivially.
  One airborne phase over 50 seeds is enough, because the named ramp-jump stream covers jumps
  directly.
- **Follow-ups for the patch notes.** The pull request that builds this feature lists these under
  Open follow-ups in its `docs/patch-notes.md` entry. None of them blocks its merge.
  1. How Principle VII treats platform and toolchain runtime libraries, and licenses with exceptions
     such as the LLVM exception. This is a PATCH amendment the maintainer may make (License scan
     boundary).
  2. For a NonExact record: measuring vehicle correction distances under the network profiles. That
     clears or overturns the provisional flag, and it is sub-spec 6's dependency on this record.
  3. For a NonExact record: the cost to sub-specs 5 and 6. Once vehicles and characters interact,
     worlds that contain both stop agreeing bit for bit on the version 1 part as well.
  4. Static linking of Jolt or Rapier into the .NET module, if the crossing decided the outcome
     (Browser integration of Jolt and Rapier).
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
