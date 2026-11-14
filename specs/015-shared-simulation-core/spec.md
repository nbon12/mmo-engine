# Feature Specification: Shared Simulation Core

**Feature Branch**: `015-shared-simulation-core`

**Created**: 2026-11-14

**Status**: Draft

**Input**: User description: "Phase 1, sub-spec 3 (nbon12/mmo-engine#12, section 'Sub-spec 3: Shared
simulation core'): One C# library that steps character movement, character collision (capsule sweeps
against heightfield terrain and static meshes), weapon timing (fire rate, seeded spread, recoil, ammo
and heat counters) and projectile flight (ballistic small arms and heavy projectiles) identically on
the server and bots (.NET 10, Linux x64) and in the browser (.NET WebAssembly build in a Web Worker).
It owns the formats for input commands, collision data and the agreement suite's recorded streams. A
Roslyn analyzer enforces the determinism rules on the core assembly. The agreement suite runs recorded
input streams through the server build and the WebAssembly build, and per-step state hashes must match
bit for bit, in CI on every change to the core. Phase 1 measures step cost on the server and in the
browser, WebAssembly download size and startup time. Vehicle physics engine selection (Bepu, Jolt,
Rapier) is a separate later slice of sub-spec 3."

## Clarifications

### Session 2026-11-14

Answered by the agent with the most defensible option. The run was unattended (Principle XIII): the
maintainer asked for Phase 1 to start once Pre-Phase 1 closed (#13 closed 2026-11-14, owner steps in
#61), and did not drive specify or clarify.

- Q: Which of the Phase 1 issue's proposed changes does this spec follow? → A: The three that affect
  this sub-spec and are not on the constitution's open-decision list: projectile flight joins the
  shared core; the core avoids `Vector<T>` and platform math and ships its own trigonometry; and the
  core is built to run client netcode in a Web Worker (this spec provides the batched step entry point;
  prediction and reconciliation are sub-spec 6). The Rapier proposal belongs to the vehicle-physics
  slice. None of them conflicts with the constitution or the architecture spec.
- Q: Does this spec include vehicle physics? → A: No. Vehicle physics engine selection is its own
  later slice of sub-spec 3, with its own spec, so this one stays completable (Principle X). This spec
  leaves an extension point for vehicles but builds none.
- Q: Which browsers run the agreement suite? → A: Every CI run executes the WebAssembly build in Node
  and inside a dedicated Web Worker in headless Chromium. Firefox and WebKit run the same Worker suite
  in the CI job where Playwright installs them. WebAssembly arithmetic on non-NaN floats is fully
  specified, so engines can't legitimately disagree; running more than one engine catches a runtime or
  marshalling bug, not an arithmetic one.
- Q: Is the tick rate fixed at 30 Hz? → A: No. The step length is configuration, validated, and
  defaults to the constitution's 30 Hz starting target. Tests cover 20, 30 and 60 Hz. Open decision 3
  stays open.

## User Scenarios & Testing *(mandatory)*

The users are the engineers and agents who build the continent server (sub-spec 5), combat netcode
(sub-spec 6), the bots and the browser client (sub-spec 8). They need one implementation of movement,
collision, weapons and projectiles that gives the same answer, to the bit, wherever it runs. The players
are the indirect users: when the builds disagree, prediction drifts and players see rubber-banding.

### User Story 1 - A character moves the same way every time (Priority: P1)

An engineer steps a character through the shared core with a sequence of input commands, on the test
map's heightfield terrain and static meshes. The character walks, runs, crouches, jumps, falls, climbs
small steps, slides along walls and stops on slopes it can't climb. Stepping the same starting state
with the same inputs and the same collision data always gives the same state after every step, in
another process, on another day, at any configured step length.

**Why this priority**: Movement is the per-player cost the Phase 1 sweep measures first, and every
other sub-spec in Phase 1 calls it. Without a deterministic movement step there is nothing to agree on.

**Independent Test**: Step every committed recorded stream through the server build in two separate
processes and compare each step's state hash with the stream's committed expected hashes. Every hash
must match, and named movement scenarios (wall slide, step-up, slope limit, landing, crouch under a
ledge) must end in the positions their scenario states.

**Acceptance Scenarios**:

1. **Given** a character standing on flat terrain and a stream of forward inputs, **When** it is
   stepped for one simulated second, **Then** it has moved the configured run speed times one second
   along its aim heading, within one step's acceleration distance, and is still grounded.
2. **Given** a character walking into a wall at 45 degrees, **When** it is stepped, **Then** it never
   enters the wall, and it keeps moving along the wall at the component of its velocity parallel to
   the wall.
3. **Given** a stair whose rise is below the configured step height, **When** the character walks
   into it, **Then** it climbs onto the stair; **Given** a rise above the step height, **Then** it is
   blocked.
4. **Given** a slope steeper than the configured slope limit, **When** the character walks up it,
   **Then** it does not climb it; **Given** a slope below the limit, **Then** it climbs it and stays
   grounded.
5. **Given** a grounded character, **When** the jump button is pressed, **Then** it leaves the ground
   with the configured jump speed, follows gravity, and lands grounded on the terrain below without
   passing through it.
6. **Given** a character standing under a ledge lower than its standing height, **When** it is
   crouched and the crouch button is released, **Then** it stays crouched until there is room to
   stand.
7. **Given** any committed recorded stream, **When** it is stepped in two separate processes, **Then**
   the per-step state hashes are identical and equal the stream's committed expected hashes.
8. **Given** a step length of 20, 30 or 60 Hz, **When** a recorded stream made for that step length is
   stepped twice, **Then** both runs give identical per-step hashes.
9. **Given** an input command whose values are out of range (an aim outside the valid range, unknown
   button bits, a non-increasing sequence number), **When** it is offered to the step, **Then** it is
   rejected with a reason code and the character's state is unchanged by it.

---

### User Story 2 - The browser build agrees with the server bit for bit (Priority: P1)

An engineer changes the shared core and pushes. CI builds the core for the server and as a WebAssembly
build, runs every recorded stream through both, and compares the state hash after every step. The
WebAssembly build runs in Node and inside a Web Worker in a headless browser, through the same batched
entry point the browser client will call once per frame. If one step disagrees, CI fails and names the
stream, the first divergent step and the fields that differ.

**Why this priority**: Bit-exact agreement is the reason the core exists. A single-bit difference
between builds is a prediction error on every player's screen.

**Independent Test**: Run the agreement suite over every committed stream and a set of generated
streams from fixed seeds. Every step's hash must match across the server build, the WebAssembly build
in Node and the WebAssembly build in a browser Worker. Then run it against a core with a deliberate
one-ulp change in one build and check that the suite fails at the first affected step.

**Acceptance Scenarios**:

1. **Given** every committed recorded stream, **When** the agreement suite runs, **Then** the server
   build and the WebAssembly build in Node produce identical state hashes at every step.
2. **Given** the same streams, **When** the WebAssembly build runs inside a dedicated Web Worker in
   headless Chromium, **Then** it produces the same per-step hashes as the server build.
3. **Given** generated streams from at least 50 fixed seeds covering every input button and the full
   aim range, **When** the agreement suite runs, **Then** every step's hash matches across builds.
4. **Given** a build whose result differs at one step, **When** the agreement suite compares it,
   **Then** the suite fails, and its report names the stream, the first divergent step, and each
   state field that differs with both builds' values in exact bit form.
5. **Given** a stream recorded with a different core format version, **When** the suite loads it,
   **Then** it refuses the stream with a message naming both versions instead of reporting a
   divergence.
6. **Given** a frame that queues several input commands, **When** the browser calls the batched step
   entry point once, **Then** it steps each queued command in order and returns one state and hash per
   step, the same as stepping them one at a time.
7. **Given** a pull request that changes the shared core or its recorded streams, **When** CI runs,
   **Then** the agreement suite runs and its result gates the pull request.

---

### User Story 3 - The determinism rules are enforced by the build, not by review (Priority: P1)

An engineer, or an agent, writes code in the shared core that would make the builds disagree: a
`Vector<T>` operation, a call to platform math such as `MathF.Sin`, an explicit fused multiply-add, an
iteration over a hash-ordered collection, a read of the wall clock, or an unseeded random number. The
build fails on that line with a rule ID and a message that names the allowed alternative. The core's
own trigonometric functions give the same bits in every build and stay within a documented error of
the true value.

**Why this priority**: The agreement suite catches a divergence only on the paths the streams cover.
The analyzer catches the cause on every line, before any test runs.

**Independent Test**: Compile a set of small code samples against the analyzer, one per rule and one
allowed form per rule. Each violating sample must produce exactly its rule's diagnostic, and each
allowed sample none. Then build the core itself with the analyzer as an error.

**Acceptance Scenarios**:

1. **Given** core code that uses a `System.Numerics` vector, quaternion or matrix type (`Vector<T>`,
   whose width differs between builds, or `Vector2`, `Vector3`, `Vector4`, `Quaternion`,
   `Matrix4x4`, whose operations a compiler may reorder), or a horizontal or fused `Vector128`
   operation, **When** it is built, **Then** the build fails with the vector rule's diagnostic.
2. **Given** core code that calls `Math` or `MathF` functions outside the allowed list, **When** it is
   built, **Then** the build fails with the platform-math rule's diagnostic naming the core's
   replacement.
3. **Given** core code that calls `MathF.FusedMultiplyAdd`, `Math.FusedMultiplyAdd` or an `Fma`
   intrinsic, **When** it is built, **Then** the build fails with the fused-multiply-add rule's
   diagnostic.
4. **Given** core code that enumerates a `Dictionary`, `HashSet` or other hash-ordered collection,
   **When** it is built, **Then** the build fails with the unordered-iteration rule's diagnostic.
5. **Given** core code that reads `DateTime.Now`, `DateTime.UtcNow`, `Stopwatch`,
   `Environment.TickCount` or `System.Random`, **When** it is built, **Then** the build fails with the
   ambient-time-and-randomness rule's diagnostic.
6. **Given** core code that takes a lock, waits on a task or uses another blocking primitive, **When**
   it is built, **Then** the build fails with the blocking-synchronization rule's diagnostic
   (constitution Principle II).
7. **Given** each of the core's trigonometric and inverse-trigonometric functions, **When** they are
   evaluated over a fixed table of inputs spanning their domains, **Then** every result is within the
   documented error bound of the true value, and the server and WebAssembly builds return identical
   bits for every input.

---

### User Story 4 - Weapons keep the same time and spread everywhere (Priority: P2)

A player holds the trigger. The shared core decides, step by step, when each shot fires from the
weapon's fire rate, where it goes from the seeded spread rule, how much the view kicks from recoil,
and when the weapon runs dry, reloads, overheats and cools. The client predicts the same shots the
server later replays, because both compute them from the same inputs and the same seed.

**Why this priority**: Hit claims (sub-spec 6) are replayed from the shooter's input and the seeded
spread. Weapon timing must agree before hit detection can, but movement and agreement come first.

**Independent Test**: Step a character holding the trigger for every weapon in the test content and
check each shot's step, direction, recoil and counters against the values the configuration implies.
Run the same streams through the agreement suite.

**Acceptance Scenarios**:

1. **Given** a weapon of 600 rounds per minute at 30 Hz and the trigger held for 3 seconds with
   enough ammunition, **When** it is stepped, **Then** exactly 30 shots fire, and the fire times
   never drift from the configured interval by more than one step, even though the interval is not a
   whole number of steps.
2. **Given** the same match salt, player ID and shot counter, **When** spread is computed in any
   build, **Then** the shot direction is identical to the bit; **Given** a different shot counter,
   **Then** the direction changes, and over many shots the directions fall within the configured
   spread cone with the configured distribution.
3. **Given** a magazine with 2 rounds and the trigger held, **When** it is stepped, **Then** 2 shots
   fire, the weapon reports empty, no further shot fires, and a reload restores the magazine from
   reserve after the configured reload time.
4. **Given** a heat weapon fired continuously, **When** its heat reaches the overheat threshold,
   **Then** it stops firing and resumes only after heat has fallen to the configured recovery level.
5. **Given** a burst of shots, **When** it is stepped, **Then** each shot adds the configured recoil
   kick to the aim offset, and the offset recovers at the configured rate once firing stops.
6. **Given** a fire input when the weapon can't fire (switching, reloading, empty, overheated),
   **When** it is stepped, **Then** no shot fires and the step reports the reason.

---

### User Story 5 - Projectiles fly the same path everywhere (Priority: P2)

A shot leaves the muzzle. The shared core flies it along its ballistic path, step by step, against the
terrain, the static meshes and the hitboxes of characters given to it, and reports what it hits, where
and at which step. The client's local hit detection and the server's replay of the same shot trace the
identical path, so a hit the client saw is a hit the server can confirm.

**Why this priority**: The Phase 1 issue moves projectile flight into the core because client hit
detection and server replay must trace identical paths. It depends on weapon timing for its inputs.

**Independent Test**: Fire projectiles of each class at targets placed at known distances on flat
terrain, behind walls and over ridges, and check the hit, its location and its step against the
closed-form ballistic path. Run the same shots through the agreement suite.

**Acceptance Scenarios**:

1. **Given** a projectile fired horizontally with no target in range, **When** it is stepped, **Then**
   its position at every step equals the closed-form ballistic position for that step, computed by the
   core's rules, and it hits the terrain at the step the path first meets it.
2. **Given** a target character standing in the path, **When** the projectile reaches it, **Then** the
   projectile reports a hit on that character, with the hitbox it struck (head, torso or legs), the
   impact point and the step.
3. **Given** a static mesh between the muzzle and a target, **When** the projectile is stepped,
   **Then** it hits the mesh and not the target.
4. **Given** a projectile that travels further than its configured maximum range or lifetime, **When**
   it is stepped, **Then** it expires without a hit at the step it passes that limit.
5. **Given** a character's position, orientation and stance, **When** its hitboxes are built, **Then**
   every build produces the same hitboxes to the bit, and the hitboxes match the configured layout for
   that stance.
6. **Given** a projectile so fast that it crosses a whole hitbox within one step, **When** it is
   stepped, **Then** it still reports the hit, because each step sweeps the full segment travelled.

---

### User Story 6 - The core's formats are versioned and fixed by golden fixtures (Priority: P3)

The input command, the collision data and the recorded stream each have one written format with a
version. A file or message in an unknown version, or one that is malformed, is refused with a clear
error, never half-read. Golden fixtures pin each format's bytes, so a change to a format is a visible,
deliberate change.

**Why this priority**: Recorded streams carry the agreement suite, and the input command later crosses
the network. The formats are needed from story 1, but versioning and refusal paths can follow the
first working movement.

**Independent Test**: Encode and decode every committed golden fixture and compare the bytes. Then
feed truncated, oversized and wrong-version inputs to each decoder and check the refusal.

**Acceptance Scenarios**:

1. **Given** each committed golden fixture, **When** it is decoded and re-encoded, **Then** the bytes
   are identical to the fixture.
2. **Given** an input command, a collision data file or a recorded stream with an unknown format
   version, **When** it is decoded, **Then** it is refused with an error naming the version found and
   the versions supported.
3. **Given** a truncated, oversized or internally inconsistent file or message, **When** it is
   decoded, **Then** it is refused with an error naming the offset or field, and no partial result is
   returned.
4. **Given** collision data whose checksum doesn't match its contents, **When** it is loaded, **Then**
   it is refused.
5. **Given** a recorded stream that names collision data by checksum, **When** the matching collision
   data isn't supplied, **Then** the stream is refused before any step runs.

---

### User Story 7 - Each build's cost is measured and recorded (Priority: P3)

An engineer runs the measurement job. It records the server build's cost per movement step, weapon step
and projectile step, the WebAssembly build's download size, startup time and step cost, for the
interpreted build and for the ahead-of-time compiled build when the toolchain provides one. Every number
is recorded as a measurement with its runner class. Steady-state steps allocate no managed memory.

**Why this priority**: Gate 9 decides the WebAssembly build mode from these numbers, and the Phase 1
sweep needs per-player movement cost. Neither is needed to make the core correct.

**Independent Test**: Run the measurement job on a CI runner and check that its report holds every
listed measurement with units, the runner class and the build mode, and that the zero-allocation tests
pass for every step function.

**Acceptance Scenarios**:

1. **Given** the server build, **When** the step benchmarks run, **Then** they report the mean and
   p99 cost of one movement step, one weapon step and one projectile step, labeled as measurements
   with the runner class.
2. **Given** the WebAssembly build, **When** the measurement job runs, **Then** its report records the
   download size (raw and compressed), the time to the first completed step and the cost per step, for
   each build mode the toolchain produced, and names any mode it could not produce and why.
3. **Given** warmed-up movement, weapon and projectile steps, **When** a further 1,000 steps run,
   **Then** they allocate zero bytes of managed memory.

### Edge Cases

- A character exactly on a triangle edge or vertex, or wedged in a concave corner, never tunnels and
  never oscillates between two positions; the slide loop stops within its iteration limit.
- A step that would move a character further than its capsule radius is subdivided, so no wall thinner
  than the movement per step is passed through.
- A character pushed out of the heightfield's bounds is clamped to the playable area and reported, not
  left in undefined terrain.
- A collision mesh with degenerate (zero-area) triangles loads, and those triangles are ignored.
- Inputs that produce NaN or infinity anywhere in the state are impossible by validation; if one
  appears anyway, the step refuses to continue and reports the field, rather than hashing an
  undefined value.
- Negative zero and positive zero hash differently, so state that can become negative zero is
  canonicalised before hashing.
- A weapon whose fire interval is shorter than one step fires at most the configured maximum shots per
  step, and the rest of its timing accumulates without drift.
- A projectile that starts inside a hitbox (point-blank) reports that hit at its first step.
- Region merges, splits, portals and tick overruns: N/A. The core is a pure step function with no
  region data; the continent server (sub-spec 5) owns where its state lives. A degraded region at
  20 Hz calls the core with the 20 Hz step length from configuration.

## Requirements *(mandatory)*

### Functional Requirements

**Movement and collision**

- **FR-001**: The core MUST provide a movement step: a pure function from a character's state, one
  input command, the movement configuration and the collision world to the character's next state.
  It MUST read no other input, no clock and no global state.
- **FR-002**: The movement step MUST support walking, running, crouching, jumping, falling under
  gravity, climbing steps up to the configured step height, sliding along walls and refusing slopes
  steeper than the configured slope limit.
- **FR-003**: Character collision MUST sweep the character's capsule against heightfield terrain and
  static triangle meshes, and MUST never leave the capsule overlapping either by more than the
  configured contact tolerance after a step.
- **FR-004**: The collision world MUST be built only from collision data files in the core's format,
  and the same file MUST give the same collision results in every build.
- **FR-005**: Every loop in the movement step MUST have a fixed iteration limit, and the step's result
  MUST NOT depend on how long the step took to run.

**Weapons and projectiles**

- **FR-006**: The core MUST provide a weapon step that decides from the weapon's state, the input
  command and the weapon configuration whether shots fire at this step, and updates fire timing,
  recoil, ammunition, reload and heat.
- **FR-007**: Fire timing MUST accumulate the configured interval exactly, in integer units of time,
  so that the number of shots over any period never drifts from the configured rate by more than one.
- **FR-008**: Shot spread MUST be a function only of the match salt, the player ID, the shot counter
  and the weapon's spread configuration, and MUST give the same direction to the bit in every build.
- **FR-009**: The core MUST provide a projectile step that advances small-arms and heavy projectiles
  along a ballistic path computed in closed form from the launch state and the step number, and
  sweeps the segment travelled in the step against terrain, static meshes and the hitboxes it is given.
- **FR-010**: A projectile hit MUST report the entity hit (or the terrain or mesh), the hitbox struck,
  the impact point and the step. Among several hits within one step, the earliest along the segment
  wins, with ties broken by entity ID.
- **FR-011**: The core MUST build a character's hitboxes from its position, orientation and stance,
  using a configured layout, so that the same pose gives the same hitboxes in every build.

**Determinism**

- **FR-012**: The core MUST use only scalar `float` arithmetic or explicit lane-wise 128-bit vector
  operations. It MUST NOT use `System.Numerics` vector, quaternion or matrix types, horizontal
  vector operations, fused multiply-add, platform math functions outside an allowed list, iteration
  over hash-ordered collections, the wall clock, or unseeded randomness.
- **FR-013**: The core MUST ship its own sine, cosine, tangent, arcsine, arccosine, arctangent and
  two-argument arctangent, each with a documented maximum error. Square root MAY use the platform's
  IEEE 754 correctly rounded operation, which is identical in every build by definition.
- **FR-014**: A Roslyn analyzer MUST enforce FR-012 and the constitution's no-blocking rule on the core
  assembly, and the build MUST fail on any of its diagnostics.
- **FR-015**: The step length MUST come from validated configuration, and the core MUST NOT accept a
  measured frame time. Supported step lengths are those of 20 to 60 Hz.
- **FR-016**: The core MUST produce a state hash for each step: a stable 64-bit hash of a canonical
  little-endian serialisation of the character, weapon and projectile state, with negative zero
  canonicalised to positive zero.
- **FR-017**: A step that would produce NaN or infinity in any state field MUST refuse to complete and
  report the field.

**Inputs and validation**

- **FR-018**: An input command MUST carry its sequence number, the client step it was made for, the
  buttons held and the aim for that step, with the aim as fixed-point angles.
- **FR-019**: The core MUST validate each input command before it affects state: the aim within range,
  only known button bits set, and the sequence number greater than the last one applied. An invalid
  command MUST be rejected with a reason code and leave the state unchanged.

**Formats**

- **FR-020**: The input command, the collision data file and the recorded stream MUST each have a
  documented binary format that begins with a format identifier and version, and MUST be decoded
  only through a decoder that refuses unknown versions, truncation, oversize and inconsistency
  without returning a partial result.
- **FR-021**: Collision data MUST carry a checksum of its contents, and a recorded stream MUST name the
  collision data it was recorded against by that checksum.
- **FR-022**: A recorded stream MUST hold its format version, the step length, the configurations
  used, the initial state, the seed and match salt, every input command in order, and the expected
  state hash after every step.
- **FR-023**: Each format MUST have committed golden fixtures that round-trip to identical bytes.

**Builds and agreement**

- **FR-024**: The core MUST build for the server (.NET 10 on Linux x64, in process) and as a .NET
  WebAssembly build that runs in Node and in a dedicated Web Worker.
- **FR-025**: The WebAssembly build MUST expose a batched step entry point: one call steps every
  queued input command in order and returns each step's state and hash.
- **FR-026**: The agreement suite MUST run every committed recorded stream and generated streams from
  at least 50 fixed seeds through the server build and the WebAssembly build, and MUST fail on the
  first step whose hash differs, reporting the stream, the step and each differing field in exact bit
  form.
- **FR-027**: The agreement suite MUST run in CI on every pull request that changes the core, its
  analyzer, its recorded streams or its build, and its result MUST gate the pull request.
- **FR-028**: The committed expected hashes MUST change only through a committed regeneration that
  states why, never automatically during a test run.

**Configuration**

- **FR-029**: Movement, weapon, projectile and hitbox parameters MUST be configuration, validated
  before any step runs. An invalid configuration MUST be refused with an error naming each invalid
  field, on the server through the constitution's validated options and in the browser build through
  an equivalent guard.

**Measurement**

- **FR-030**: The server build's movement, weapon and projectile steps MUST have benchmarks reporting
  mean and p99 cost per step, labeled as measurements with the runner class.
- **FR-031**: A measurement job MUST record the WebAssembly build's download size (raw and
  compressed), time to the first completed step and cost per step, for the interpreted build and for
  the ahead-of-time build where the toolchain produces one, and MUST name any mode it could not build.
- **FR-032**: After warm-up, the movement, weapon and projectile steps MUST allocate no managed memory,
  verified by zero-allocation tests in CI.

### Key Entities

- **Character state**: one character's position, velocity, aim, stance, grounded flag and contact
  state, plus the last input sequence applied. The movement step's input and output.
- **Input command**: one step's input from a player: sequence number, client step, buttons and aim.
- **Movement configuration**: step length, speeds, accelerations, gravity, jump speed, step height,
  slope limit, capsule dimensions per stance and the contact tolerance.
- **Collision world**: the heightfield terrain and static triangle meshes loaded from one collision
  data file, with the spatial index used to query them, identified by the file's checksum.
- **Weapon configuration and state**: fire interval, spread, recoil, magazine, reserve, reload time and
  heat parameters; and per weapon, the fire-time accumulator, shot counter, recoil offset, ammunition,
  reload progress and heat.
- **Projectile**: launch step, origin, velocity, class (small arms or heavy), gravity factor, maximum
  range and lifetime, and its owner. Its position at any step follows from these in closed form.
- **Hitbox layout and hitboxes**: the configured capsules per stance (head, torso, legs) and the
  hitboxes built from a pose.
- **Recorded stream**: a header (format version, step length, configurations, collision data checksum,
  seed and match salt, initial state), the input commands in order, and the expected hash after each
  step.
- **Step report**: the result of one step: the new state, its hash, the shots fired and projectile
  hits, and any rejected input with its reason code.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: A shared library used by the continent server, the bots and the browser
  client. It adds no running service, no web surface and no network endpoint.
- **State ownership**: The core owns no state. Each step function takes state in and returns state
  out. In the continent server (sub-spec 5), the region that owns an entity is its single writer and
  calls the core inside its tick. On the client, the Worker owns the predicted copy.
- **Interaction class**: Movement and collision are instant interactions resolved by the owning region.
  Projectile flight provides the trace that both close-range instant hits and deferred projectile
  hand-overs use; this spec adds no claim, portal or bridging behavior, and a projectile claims no
  sections (constitution, Projectiles).
- **Authority and disclosure**: The server stays authoritative (Principle IV): the core is the same
  code on both sides, so the client predicts its own movement and weapons with the server's rules.
  Every input command is validated for value ranges, known bits and sequencing before it affects state
  (FR-019). Size and rate limits on the wire belong to the transport and edge (sub-specs 4 and 7).
  The core discloses nothing; what each client sees is Delivery's job.
- **Persistence and write policy**: N/A. The core persists nothing; recorded streams and collision data
  are committed test fixtures and content files.
- **Contracts**: Three versioned binary formats (input command, collision data, recorded stream) with
  golden fixtures. The input command is a future wire message: its single source of truth is the
  core's C# definition, which the browser client also runs (in WebAssembly), and the golden fixtures
  are the shared reference that a TypeScript decoder, if the fallback is ever needed, is verified
  against. Packet framing and protocol-version negotiation stay with the transport and Delivery.
- **Performance and evidence**: Every step function is tick-path code: zero-allocation tests and
  benchmarks are required (FR-030, FR-032). The step costs, download size and startup time recorded by
  this feature are measurements; v2's 11–33 ms per tick at 1,000 players stays an estimate until the
  gate-9 sweep replaces it. This feature sets no tuning value: movement, weapon and hitbox values are
  placeholders until game design supplies them.
- **Security and abuse controls**: Input commands are untrusted and validated (FR-019). Decoders refuse
  malformed, oversized and unknown-version data without partial results (FR-020). No authentication or
  authorization: the core is a library.
- **Observability**: N/A for the library: it does no I/O and logs nothing on the tick path. Step
  reports carry rejection reason codes for the caller to log. The measurement job writes its report
  as files.
- **Licensing and design sources**: No new code dependency beyond MIT or Apache-2.0 packages
  (System.IO.Hashing is already used; the Roslyn analyzer SDK and the .NET WebAssembly SDK are MIT).
  Design sources (Rapier's determinism guide, the .NET vectorization guidelines, .NET on Web Workers,
  the IEEE 754 and WebAssembly floating-point specifications, and the published algorithms the core's
  math functions follow) are recorded in `research.md`.
- **Accessibility**: N/A. No web surface.
- **Quality gates**: 95% line coverage for changed `src/` files. Theories cover each step length,
  each stance, each button, each analyzer rule, each format's refusal reasons, each weapon and each
  projectile class. Tests are pure and in-process except the WebAssembly runs, which use unique
  temporary directories and dynamic ports, so they are safe in parallel and against leftovers.
  Zero-allocation tests cover every step function.
- **Executable and living spec**: Every acceptance scenario maps to an automated test named in
  `tasks.md`. This spec and `tasks.md` are updated before the PR.
- **Spec independence and parallelism**: No hard dependency. The core needs nothing from sub-specs 4
  to 9; they depend on it. The vehicle-physics slice depends on this spec's collision world and
  agreement suite and is specified separately.
- **Open decisions**: None is settled. Open decision 3 (tick rate) stays open: the step length is
  configuration and tests cover 20 to 60 Hz. Open decision 5 (hit detection) stays open: projectile
  flight and hitboxes serve both server-simulated shots and client-reported hits, and this spec
  implements neither claims nor their validation. Open decisions 4 and 9 are untouched.
- **Autonomous delivery (XIII)**: Every check is an automated test or a CI job. Nothing is assigned to
  a person. Every ambiguity is recorded below as an assumption.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every committed recorded stream, at least 10 named movement, weapon and projectile
  scenarios plus generated streams from 50 seeds, gives identical per-step hashes in the server build,
  the WebAssembly build in Node and the WebAssembly build in a browser Worker: zero divergent steps.
- **SC-002**: A deliberate one-ulp change in one build is reported at its first divergent step, never
  only at the end of a stream.
- **SC-003**: Every analyzer rule fails the build on its violating sample and stays silent on its
  allowed sample, and the core builds with zero analyzer diagnostics.
- **SC-004**: Each of the core's trigonometric functions stays within its documented error bound over
  its whole test table, and agrees to the bit across builds.
- **SC-005**: Steady-state movement, weapon and projectile steps allocate zero bytes after warm-up.
- **SC-006**: The agreement suite, including the browser Worker run, completes in under 10 minutes on
  a CI runner.
- **SC-007**: The measurement report records the server step costs, and the WebAssembly download size,
  startup time and step cost for each build mode produced, each with its runner class.

## Assumptions

- **Scope of this slice.** This spec covers character movement, character collision, weapon timing,
  projectile flight, hitboxes, the three formats, the analyzer, the agreement suite and the
  measurements. Vehicle physics (choosing Bepu, Jolt or Rapier by agreement and cost) is the next
  slice of sub-spec 3, with its own spec. Client prediction and reconciliation, hit claims and their
  validation are sub-spec 6; the client's Worker plumbing and rendering are sub-spec 8.
- **Server target.** The agreement reference is .NET 10 on Linux x64. RyuJIT on x64 compiles scalar
  `float` arithmetic to SSE single-precision instructions and doesn't contract multiply-add, so x64
  results don't vary by CPU model. Other server architectures (Arm64 developer machines) can run the
  suite, but aren't the reference.
- **WebAssembly build.** The browser build is the .NET WebAssembly runtime. A probe on 2026-11-14
  (`float` multiply-add over a million iterations) gave identical bits in the interpreted WebAssembly
  build under Node and on x64, which is consistent with WebAssembly's fully specified `f32`
  arithmetic. The ahead-of-time build needs the `wasm-tools` workload; where it can't be installed,
  the measurement report says so (FR-031) and the interpreted build is still measured.
- **Square root.** IEEE 754 requires square root to be correctly rounded, and WebAssembly's `f32.sqrt`
  and x64's `sqrtss` both are, so the core may use the platform square root. Every other
  transcendental function is the core's own.
- **Input aim.** Aim is carried as fixed-point angles (16 bits per axis), so every build sees the same
  bits for the same aim; the movement and weapon steps convert them with the core's own trigonometry.
- **Spread rule.** v2's seed rule (match salt, player ID, shot counter) is implemented as a stateless
  64-bit hash of those three values, mixed with SplitMix64's finaliser, giving two uniform numbers for
  the spread cone. It needs no shared random stream, so shots can be replayed in any order.
- **Ballistics.** Projectiles follow gravity only (no drag) in Phase 1; drag can be added as
  configuration later without changing the format's version rules. The position at step *k* is
  computed in closed form from the launch state, so client and server can evaluate any step directly.
- **Test content.** Collision data for the agreement suite is generated by a deterministic tool from a
  fixed seed and committed: a heightfield with slopes on both sides of the slope limit, a ridge and a
  cliff, and static meshes with a wall, stairs of two rises, a low ledge, a concave corner and a
  thin wall. It stands in for the Phase 1 test map until level design supplies one.
- **Placeholder values.** Movement, weapon, recoil, heat and hitbox values are placeholders in the
  range of a modern infantry shooter (run speed 7 m/s, the simulator's infantry ceiling; capsule
  0.4 m radius and 1.8 m standing height; one automatic small arm at 600 rounds per minute; one heavy
  launcher). Game design replaces them; they are configuration, so replacing them changes no code.
- **Browsers in CI.** Headless Chromium runs the Worker suite on every CI run. Firefox and WebKit run it
  in the CI job when Playwright can install them there. Node runs the WebAssembly build on every run.
- **Expected hashes.** The expected hashes in recorded streams are generated by the server build with a
  committed regeneration command, and the agreement suite checks the WebAssembly build against both
  the server's live result and the committed hashes.
