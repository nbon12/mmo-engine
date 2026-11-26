# Research: Vehicle Physics Engine Selection

Phase 0 output for `016-vehicle-physics-selection`. Each item records the decision, why, what else was
considered, and the risks. External design sources are listed at the end (Principle VII). This feature
builds on feature 015's shared core; its research ([../015-shared-simulation-core/research.md](../015-shared-simulation-core/research.md))
is not repeated here.

**Evidence labels.** Six throwaway research threads were run before this plan: one per candidate
(Bepu, Jolt, Rapier), .NET WebAssembly native interop, the vehicle model, and the license scan. None
edited the repository; their prototypes are listed under [Prototype locations](#prototype-locations).

- **[measured]**: measured by a prototype in the same sandbox: a 4-vCPU Xeon at 2.1 GHz with AVX-512,
  .NET SDK 10.0.115 (runtime 10.0.15), the `wasm-tools` workload with Emscripten 3.1.56 (clang 19.1.0,
  binaryen 117), Rust 1.97.0, clang 18.1.3, GCC 13.3.0, Node 22 and Playwright Chromium. Sandbox timings
  are not runner-class numbers; the selection run (R24) records those.
- **[source]**: read in an engine's or toolchain's source at the pinned release.
- **[documentation]**: an engine's docs, package metadata or Microsoft's docs.
- **[estimate]**: reasoned, not measured.

**Open decisions.** None is settled here. Open decision 3 (tick rate) stays open: vehicles step at the
configured rate, tested at 20, 30 and 60 Hz. Open decision 5 (hit detection) is untouched: projectiles
do not hit vehicles. Open decisions 4 and 9 are untouched. Gate 9's open choices stay open: the
WebAssembly build mode (the rule runs per mode, R25) and parallel phases (engines step single-threaded,
R27). The vehicle physics engine is not an open decision (spec Clarifications); FR-026 decides it.

**Expected outcome [estimate].** From the evidence below, Bepu is expected to agree bit for bit in the
two interpreted modes and to be NotBuilt in `aot`; Jolt is expected to agree in all three modes; Rapier
is expected to be NotTested (`FitCheckFailed`, criterion c). That is a forecast for planning, not a
result: the committed selection report decides.

## R1. Candidates and pinned releases

**Decision**:

| Candidate | Pin | License | Source of the pin |
| --- | --- | --- | --- |
| BepuPhysics2 | NuGet `BepuPhysics` and `BepuUtilities` **2.5.0-beta.33** (git tag `2.5.0-beta.33`, commit `29ed466870534c413aba9eeccd813cf15d135d0b`, net8.0 assemblies) | Apache-2.0 | nupkg SHA-256 `9c1db047cd7b3597cd808d7b94a1ef6626e0041e305bb3d8fe41aa658c3f1d03` (BepuPhysics) and `d1cf1bb261720fef05d197b4de9f17e06bac6d2a6c7574a6de88bc21122c26ea` (BepuUtilities); the NuGet lock file's `contentHash` |
| Jolt Physics | **v5.7.0**, commit `cf16769bb0ede9c45d57d06068ff0689e9702f5c`, tree `47f0b9a2d9a830c64c3a729b372e0c951a4c1daa` | MIT | git commit SHA, checked against the tree hash after fetch |
| Rapier | crates.io `rapier3d` **=0.36.1** with `parry3d` 0.31.1 (tag `v0.36.1`, commit `3406750a38286a0d3c7d0f4be2f9d53153e81d90`) | Apache-2.0 | `.crate` checksum `033e2edc815d61ab97bd74ed5d1cbea5ebe575218774a7cfedbd3551f0033317`; a committed `Cargo.lock` with every crate's checksum |

**Bepu's pin departs from the spec's "latest stable release" assumption.** The latest stable Bepu is
2.4.0 (2022-02-18). Every release since is `2.5.0-beta.N`, the latest beta.33 on 2026-10-11
[measured, NuGet registration API].

- 2.4.0 has no `BroadPhase.ActiveRefinementSchedule` or `StaticRefinementSchedule`. Its tree
  refinement keys off a private `frameIndex`, which is hidden state (R5). Neutralising it would need
  reflection, which MMO1008 forbids in the adapter, or a new `Simulation` per step, which allocates
  (FR-025) [source].
- 2.4.0 has the same Mono AOT crash (R9) and the same remover `lock` (R16) [source].
- The author's packaging notes say results differ between versions and that the main branch is kept
  stable [documentation, `PackagingAndVersioning.md`].

So the plan pins 2.5.0-beta.33 and records the departure in `plan.md`. The implementing pull request
updates the spec's "Engine releases" assumption to say Bepu is pinned to its latest release, a
prerelease, because 2.4.0 lacks the refinement-schedule hook.

**Rationale**:

- Jolt v5.7.0 is the latest tag (`git ls-remote`); `master` is past it but is not a release [measured].
  557 of the 560 files under `Jolt/` carry `SPDX-License-Identifier: MIT`; the others are a diagram, a
  natvis file and `Jolt.cmake` [measured].
- Rapier 0.36.1 is crates.io's `max_stable_version`, published 2026-10-10, `rust-version` 1.86
  [measured].
- BepuUtilities has no dependencies, and BepuPhysics depends only on BepuUtilities [measured], so
  Bepu adds exactly two Apache-2.0 assemblies.

**Alternatives considered**:

- **Bepu 2.4.0**: rejected above.
- **A Bepu source build at the tag**: gives the NuGet build's hashes [measured]; needed only if a
  patch were ever taken, which this plan does not take.
- **Jolt v5.6.0** (what the JoltC binding pins): older, without v5.7.0's vehicle and triangle fixes.
- **The GitHub release tarball for Jolt**: GitHub does not promise stable archive bytes, and the
  session proxy refused the download [measured]. The commit SHA plus the tree hash is the pin.

**Risks**:

- Every pin bump changes the engine's identity (R19), so every stream recorded with the old one is
  NotComparable until recorded again with a reason.
- Jolt v5.7.0 was 8 days old when researched.
- Bepu is a prerelease pin.

## R2. The vehicle model: one raycast-wheel model, owned by the core

**Decision**: Use **one engine-neutral raycast-wheel vehicle model, owned by the core** (the second
branch of FR-033). No engine's own vehicle layer is used.

- **Engine side.** Each vehicle is one rigid body: the chassis box, with no rounding, its centre of
  mass at the body origin, and the box offset from it vertically by a configured amount. The engine
  integrates gravity, resolves chassis-against-static and chassis-against-chassis contacts with
  continuous collision detection, and sets and gets body state. Wheels are not engine bodies.
- **Core side, per vehicle and per substep** (`src/MmoEngine.SimCore/Vehicles/`):
  1. Map the command to throttle, brake, reverse and steering (FR-008), and move the steering angle
     toward its target at the configured rate.
  2. Cast each wheel's segment from its attachment point along the chassis down axis, rest length
     plus radius long, with the core's `CollisionWorld.SegmentQuery` (double-sided, inclusive edges,
     lower triangle ID on ties, no allocation). The hit's triangle gives the contact normal.
  3. Suspension force F = max(0, k·(rest − s) − c·ṡ), where s is the suspension length and ṡ comes
     from the previous step's stored compression.
  4. Tire impulses along the rolling and lateral directions of each wheel in contact: drive force × dt
     (cut back linearly over the last configured fraction of top speed), or a brake impulse that
     cancels the contact's forward velocity, capped at m·b·dt/4, and a lateral impulse that cancels the
     contact's lateral velocity through the contact's effective mass. The pair is clipped to the
     friction circle μ·F·dt. The four wheels are solved with a fixed number of Gauss–Seidel passes
     (`vehicles.tireSolverIterations`), each pass updating the chassis velocity it reads.
  5. Apply the impulses to the chassis velocity the core owns: Δv = ΣJ / m and
     Δω = I_w⁻¹ · Σ(r × J), with the configured mass and the core-computed inertia rotated into the
     world frame.
  6. Hand the bodies to the adapter, which writes them into its engine, steps it by the substep's
     length and reads them back (R13).
- **Wheel spin** is the forward contact speed divided by the radius while the wheel is in contact, and
  is held while it is airborne. It never feeds back into a force.

**Rationale**:

- **The engines' own vehicle layers are not the same vehicle** [measured, V1]. The same placeholder
  parameters mapped onto each, US1's flat checks at 20, 30 and 60 Hz:
  - Rapier's `DynamicRayCastVehicleController` (a Bullet raycast vehicle): stiffness per unit mass,
    forward friction allowance doubled, side impulse 0.2 × stiffness per step, side impulses raised
    toward the centre of mass. Braking reached 115.6–116.1% of v²/2b, and the turning radius
    +23.6% to +25.5% of L/tan δ, past US1 scenario 3's band at 60 Hz and dependent on the step rate.
  - Jolt's `VehicleConstraint` with `WheeledVehicleController` (engine torque curve, gears, clutch,
    differentials, slip curves): an untuned mapping reached 94.3% of top speed in 10 s, braked in
    158.5–176.1% of v²/2b and never finished the turning circle.
  - Bepu has no vehicle layer [source]. Its demo car makes each wheel a cylinder body held by four
    constraints: 5 bodies and 16 constraints per car, whose wheel bodies carry full 6-DOF state that
    FR-012's per-wheel fields cannot hold (RB2).
- **Hidden state.** Jolt's vehicle constraint carries engine RPM, transmission timers, the previous
  Δt and per-wheel lambdas, about 341 B per vehicle [measured, J7]. Restoring FR-012's fields alone
  diverged at the next step in every setting. With the core model, every engine holds only rigid
  bodies, so FR-012's fields are the whole vehicle state (R5).
- **Fairness.** Only the rigid-body solver, contacts and integrator differ between candidates. The
  wheel model, its rays, its contact flags and its numbers are the same code under the full analyzer,
  so the deciding browser cost compares engines, not three vehicle models.
- **The prototype passes US1's flat checks** [measured, V1], on one Bepu body with flat ground standing
  in for `SegmentQuery`: 100.0% of top speed with 0% overshoot, 95% at 4.88–4.90 s, tilt ≤ 1.55°;
  braking 106.6–108.0% of v²/2b with μ = 1.2 and the centre of mass 0.85 m up; turning radius
  −5.3% to +20.4%.

**Alternatives considered**:

- **Each engine's native vehicle layer**: rejected for non-equivalence and Jolt's hidden state. The
  parameter mapping is kept below for the record.

  | Shared parameter | Jolt `WheeledVehicleController` | Rapier raycast controller | Bepu demo car |
  | --- | --- | --- | --- |
  | k, c | `SpringSettings(StiffnessAndDamping, k, c)` | k / m, c / m | servo spring from the corner mass |
  | rest, travel | min and max length | rest ± `max_suspension_travel` | servo offset and limits |
  | μ | friction curves × √(ground friction) | `friction_slip`, forward ×2, plus `side_friction_stiffness` | contact friction on the wheel body |
  | drive, top speed | torque, RPM range, gear, clutch | `engine_force` with a core governor | `AngularAxisMotor` target and maximum torque |
  | b | `mMaxBrakeTorque = m·b·r/4` | `brake = m·b·dt/4` | motor target 0 |
  | state outside FR-012 | engine, transmission, contact cache, lambdas | `rotation`, `delta_rotation` | 4 wheel bodies' poses, velocities and impulses |

- **Engine ray or shape casts for the wheels**: rejected. Bepu's triangle ray tests are one-sided,
  Jolt's ignore back faces by default [measured], Jolt documents its narrow-phase result order as
  unspecified, and the core query is identical everywhere.
- **A core capsule sweep per wheel**: kept as a follow-up if ray wheels fail the ramp or terrain
  checks [estimate].

**Risks**:

- Margins are thin with the placeholders: at μ = 1 and a 1 m centre of mass, braking is 118–120%
  [measured]. The placeholders use μ = 1.2 and a 0.85 m centre of mass (R3).
- The turning radius moves by about ±20% with step rate and substeps when the four wheels are solved
  once each [measured]. The Gauss–Seidel passes should narrow it [estimate].
- Explicit suspension forces are stable for ω·dt < 2; with the placeholders it is 0.47 at 20 Hz
  [estimate]. Landings and bottoming out are tested by US1 scenario 5.
- Ray wheels ignore curbs and other vehicles, which the spec accepts.

## R3. Shared parameters and engine settings

**Decision**:

- **Vehicle configuration** (shared, carried by every vehicle stream; the rule table is in
  [contracts/vehicle-config.md](contracts/vehicle-config.md)): maximum vehicles; gravity; mass; chassis
  half-extents and the box's vertical offset from the centre of mass; four wheel attachment points;
  wheel radius; suspension rest length, travel, stiffness (N/m) and damping (N·s/m); tire grip μ; drive
  force, top speed and cut-back fraction; reverse drive force, reverse top speed and the reverse
  engage speed; braking deceleration b; maximum steering angle and steering rate; core substeps K; tire
  solver passes; the contact tolerance; and chassis contact friction μ_c.
- **Inertia** is computed by the core from the box and its offset (parallel-axis theorem) and passed to
  every engine explicitly (Bepu `BodyInertia`, Jolt `EOverrideMassProperties::MassAndInertiaProvided`,
  Rapier mass properties). No engine computes it from its shape. The offset is vertical only, so the
  inertia is diagonal.
- **One friction value.** Every collider, static and chassis, gets μ_c, so each engine's combine rule
  (Jolt's geometric mean, Rapier's average, Bepu's callback) gives μ_c. Restitution is 0.
- **"Solver substeps" (FR-005) means the core's K**: the vehicle step runs K substeps of the step's
  length / K, each with one engine call. Engine-internal substeps and iterations are engine settings.
- **Engine settings belong to each engine's identity (R19)**, not to the shared configuration: solver
  iterations and internal substeps, slop and speculative margins, the CCD mode (Bepu `Continuous`,
  Jolt `EMotionQuality::LinearCast`, Rapier `ccd_enabled`), sleeping off, single thread, and the
  history-free stepping mode (R5).
- **Placeholders** (validated configuration, never tuning values): 2,000 kg; chassis 4.5 × 2 × 1.5 m
  (length × width × height), forward +Z; centre of mass 0.85 m above the ground at rest; box centre
  0.25 m above the centre of mass; wheelbase 2.8 m, track 1.7 m; wheel radius and rest length 0.35 m,
  travel 0.25 m; k = 45,000 N/m, c = 3,800 N·s/m; μ = 1.2; drive 8,000 N; top speed 20 m/s; cut-back
  over the last 10%; reverse 4,000 N and 5 m/s; reverse engage speed 0.1 m/s; b = 8 m/s²; 35° steering
  at 90°/s; K = 1; 4 tire passes; contact tolerance 0.03 m; μ_c = 0.5.

**Rationale**:

- **CCD is required by US1 scenario 7** [measured, V2]. A 0.1 m wall at 20 m/s and 30 Hz: without CCD
  every engine ends step 1 about 0.27 m past the far face; with it, Rapier stops 0.105 m short of the
  far face, Jolt 0.080 m short and Bepu 0.093 m short.
- **Resting offsets differ** [measured]. Against a zero-thickness wall Rapier stops 5 mm short, Bepu
  6.4 mm past and Jolt 20 mm past (its default slop). The 0.03 m tolerance covers all three once Jolt's
  `mPenetrationSlop` is 0.01 m. 015's character tolerance (0.002 m) is far too small for vehicles.
- **Physical units, mapped once in each adapter.** Bullet-style per-mass stiffness is a trap (R2).
- **K = 1** keeps one engine crossing per step in the browser (FR-032); the prototype passed at K = 1
  and K = 2 [measured].

**Alternatives considered**: inertia from each engine's shape (bits may differ); per-mass stiffness
(not physical); mapping K onto each engine's own substeps (the meaning differs per engine [source]).

**Risks**: K > 1 costs K crossings per step for Jolt and Rapier (about 0.3–0.5 µs each, R14). A
calibration test asserts each engine's resting offset against a flat floor and a wall is inside the
contact tolerance.

## R4. Static collision per engine

**Decision**: Every engine's statics are built from the core `CollisionWorld`'s kept triangles (the
same vertex expression, the fixed v00→v11 diagonal, degenerate triangles already dropped), never from
the raw file. Heightfield triangles face up; `.mmcd` mesh triangles stay double-sided.

- **Bepu**: one `Mesh` for everything. Bepu triangles are one-sided with the front opposite the
  right-handed normal (B−A)×(C−A) [measured, documentation], so heightfield triangles are emitted as
  (A, C, B) and mesh triangles in both windings. Bepu stores the vertices bit for bit (6,144 of 6,144)
  [measured].
- **Jolt**: an **exact-triangle user shape** in our shim, `ExactTriangleGridShape`
  (`EShapeSubType::User1`), holding the core's float triangles on a uniform grid. It registers
  collide and cast functions for (Box, User1) and the reverse through
  `CollisionDispatch::sRegisterCollideShape` and `sRegisterCastShape`, and visits candidate triangles
  with Jolt's own `CollideConvexVsTriangles` and `CastConvexVsTriangles`, as Jolt's
  `HeightFieldShape` does [source]. Every edge is active. Mesh triangles are emitted in both windings,
  heightfield triangles once with up as the front. No Jolt source changes.
- **Rapier**: `TriMesh` with empty flags. It is bit-exact (6,144 and 8,192 of each) and double-sided
  [measured]. Never `FIX_INTERNAL_EDGES` (it made a double-sided triangle one-sided [measured]), and
  never `HeightField` (its vertex expression differs from the core's: 512 of 3,072 cells bit-exact at
  a 0.75 m cell [measured]).

**Rationale**:

- FR-006 requires exactly the core's triangles. Jolt's `MeshShape` quantizes vertices to 21 bits per
  component over the mesh bounds [source]: 84 of 18,432 vertices were bit-exact, with a maximum error
  of 1.62e-5 m on a 48 × 36 m world and 8.63e-5 m on 256 × 256 m [measured]. `HeightFieldShape`
  quantizes heights and needs a square grid [measured]. Only a user shape keeps FR-006.
- The core's wheel rays never touch engine statics (R2), so the engine statics matter only for the
  chassis contacts and CCD.
- Every engine treats every triangle edge as active, so no engine has internal-edge smoothing the
  others lack.

**Alternatives considered**:

- **Jolt `MeshShape` with a recorded FR-006 deviation**: deterministic in both builds, and the error is
  0.3% of the contact tolerance, but it breaks a MUST of the spec. Kept only as the fallback should the
  user shape fail, which would need a spec change in the implementing pull request.
- **One Jolt `TriangleShape` per triangle in a `StaticCompoundShape`**: the compound recentres its
  sub-shapes, which rounds the vertices, and it is slow [estimate].
- **Generating world vertices on Jolt's quantization grid**: the grid depends on the bounds, so it is
  fragile.

**Risks**:

- The user shape is about 500–800 lines of C++ [estimate], with stubs for the `Shape` members a
  static-only shape needs; it is covered by the shim's own tests (R28).
- Doubled windings on coincident triangles might interact with Bepu's mesh contact reduction and
  Jolt's contact reduction. Only a thin wall was measured, from both sides in Bepu and one side in Jolt.
- The `.mmcd` boundary planes are character-only. Vehicles rely on the enclosing walls and the core's
  out-of-bounds fault (R20).

## R5. Engine history and reset equivalence: history-free stepping

**Decision**: Each adapter steps its engine in a **history-free mode**: the engine holds no state that
affects the next step beyond what the canonical state holds. The mode is part of each engine's identity.

| Engine | Mode | Measured |
| --- | --- | --- |
| Bepu | **Clear and rebuild every step.** `Simulation.Clear()`, then `Simulation.NarrowPhase.PairCache.Mapping.Clear()`, then re-add the statics (the cached `Mesh` shape, so no tree rebuild) and the bodies in ascending vehicle ID, then `Timestep`. No-op broad-phase refinement schedulers (R7). | Restart from step 150 exact (`firstDiff = none`) on x64 at every width and in every wasm build; clear-rebuild hashes identical to a new `Simulation` per step [measured, RB6]. Per-step remove and re-add of dynamic bodies was also exact in a 24-box arena [measured, V5]. |
| Jolt | **Persistent world, state written every step.** `mConstraintWarmStart = false` and `mUseBodyPairContactCache = false`. Bodies are created once with `CreateBodyWithID(BodyID(vehicleId))`, so Jolt's ID-sorted order is ascending vehicle ID, and every substep writes each body's canonical pose and velocity before stepping. The chassis shape's centre of mass is exactly the body origin (R11). | Warm start and pair cache off: exact at every restart 5–100 (small scene) and 10–200 (24-box arena); either one alone, or neither, diverges [measured, V5]. |
| Rapier | **A fresh `PhysicsWorld` every step**, statics from reused `SharedShape`s, bodies inserted in ascending vehicle ID. | Exact at every restart 5–200; `warmstart_coefficient = 0` with `contact_recycling = false` diverged from restarts 20–40, and per-step remove and insert diverged at step 53 or 57 [measured, V5, R7]. |

**Rationale**:

- **Persistent engines carry hidden state** [measured]: Bepu's pair cache warm-start impulses,
  constraint batch layout and broad-phase tree; Jolt's warm-start lambdas and body-pair cache; Rapier's
  contact, island and solver-order history. A world reloaded from pose and velocity diverged at the
  next step in every engine's default settings.
- **Making the caches part of the state is not possible through the engines' public APIs** for Bepu
  (pair cache, constraint handles and batch layout) [source]. For Jolt and Rapier it would mean engine
  snapshot bytes in the canonical state (Jolt's `SaveState`, about 475 B per vehicle; Rapier's serde
  snapshot, 657 KB) whose layout the divergence report cannot decode field by field [measured].
- **Switching history off keeps FR-012's engine-neutral record** and makes US1 scenario 10 and US2
  scenario 4 checks of the canonical state, not of an engine blob.

**Alternatives considered**:

- **Engine snapshot bytes in the state** (Jolt `SaveState`, Rapier serde, J7, R7): exact, but
  engine-specific layouts. Rejected.
- **Bepu `Solver.ScaleAccumulatedImpulses(0)`**: exact on 3 boxes, diverged in the 24-box arena
  [measured].
- **A new Bepu `Simulation` per step**: allocates managed objects (FR-025).
- **Rebuilding Jolt's bodies every step**: kept as Jolt's fallback if reset equivalence fails with the
  committed streams; it costs broad-phase inserts every step.

**Risks**:

- Contacts start cold every step (warm starting still runs across a step's internal substeps). That may
  hurt resting, stacking and pile-up quality (US1 scenario 6) [estimate].
- The rebuild costs: Bepu clear-rebuild about 2–2.5× a persistent step [measured, RB5]; Rapier's fresh
  world 1.7–2.3× [measured, R7]. It is part of what the selection measures.
- Bepu's clear-rebuild depends on the `Mapping.Clear()` workaround for a `Simulation.Clear()` bug: on
  its own, `Clear()` leaves the active pair mapping pointing at removed constraints and the next step
  throws in `NarrowPhase.UpdateConstraint` [measured, source]. The bug is a candidate upstream report
  (patch-notes follow-up).
- Jolt's history independence was shown on boxes, single-threaded. The reset tests (US1 scenario 10,
  US2 scenario 4) re-prove it on the committed vehicle streams; failing them makes Jolt Failed or
  Diverged, which is a legitimate result.

## R6. Canonical state version 2 and the version 1 part

**Decision**:

- **Version 2 = version 1's bytes, then the vehicle part.** The version 1 part (header, characters,
  projectiles, the six event lists) is byte-identical to what serialisation version 1 writes for the
  same characters, weapons and projectiles, so the **version 1 part hash** is XxHash64 (seed 0) of the
  state's prefix up to the vehicle part. The vehicle part is a vehicle count, the vehicle records in
  ascending ID, and two vehicle event lists (contacts and input rejections). The layout is in
  [contracts/vehicle-formats.md](contracts/vehicle-formats.md).
- **A vehicle record is 112 bytes**: ID; centre-of-mass position; orientation (x, y, z, w) as the
  engine's own bits; linear and angular velocity (world frame); four wheels of steering angle, spin,
  compression and contact; and the last input sequence.
- **Raw IEEE 754 bits**, with 015's −0 → +0 canonicalisation at commit and the non-finite refusal
  (FR-012). No quantization.
- **What is real state.** Position, orientation, velocities, steering angle, compression (the next
  substep's damping reads it as ṡ) and the last sequence are inputs to the next step. Spin and contact
  are outputs, hashed but never read by the step. Reverse is stateless ("Back reverses once speed ≤ the
  engage speed").
- **Contact events are derived by the core** from the canonical state: a pair's oriented-box
  separation at the end of the step ≤ the contact tolerance, and > it at the end of the previous step,
  listed by ascending (lower ID, higher ID). The previous step's separation is computed from the
  previous state's poses, so loading a state needs nothing else.
- **The step record of a version 2 stream** carries the version 1 part hash after the full hash:
  `u32 step | u32 stateLength | state | u64 hash | u64 v1PartHash`. Version 1 records are unchanged.

**Rationale**:

- A prefix layout makes the version 1 part hash a hash over bytes the step already wrote, at no extra
  serialisation, and US7 scenario 7 ("equals the hash the same stream gives with its vehicles removed")
  holds by construction while characters and vehicles do not interact.
- A one-ulp difference near a rounding boundary still flips a quantized value, and snapping would
  change the dynamics [estimate]; the spec's edge case asks for the engine's own bits.
- The orientation is stored as the engine gives it, so a build that flips its sign diverges (spec Edge
  Cases), while the divergence measurement treats q and −q as equal (R23).

**Alternatives considered**: engine snapshot bytes per vehicle (R5); a separate vehicle hash stream
instead of a v2 record (two files per stream, two comparisons); putting the v1 part hash into the
state (circular).

**Risks**: the state grows by 112 B per vehicle plus events; 256 vehicles add 28.7 KB, inside the
configured maximum state length.

## R7. Bepu: stepping settings and a member allowlist

**Decision**:

- `Simulation.Timestep(dt)` with **no `IThreadDispatcher`**; bodies with
  `BodyActivityDescription(sleepThreshold: -1)`; `SolveDescription` velocity iterations and substeps as
  engine settings; `ContinuousDetection.Continuous` on every chassis; both broad-phase refinement
  schedules replaced by a no-op scheduler.
- **A committed member allowlist** of every Bepu type and member the adapter may reference.
  `BepuAllowlistTests` reads the adapter assembly's member references with `System.Reflection.Metadata`
  and fails on any Bepu member not on the list. It names and bans:
  - `CenterDistanceConstraint`, `CenterDistanceLimit`, `AreaConstraint` and `VolumeConstraint`, which
    call `MathHelper.FastReciprocal` and `FastReciprocalSquareRoot`, SSE `rcpps`/`rsqrtps` on x64 with
    4 lanes, whose bits differ by CPU vendor [source];
  - every BepuUtilities helper that calls `Math.Sin`, `Cos`, `Tan` or `Acos` in double:
    `Matrix3x3.CreateFromAxisAngle`, `Matrix.CreateFromAxisAngle`, `QuaternionEx.CreateFromAxisAngle`,
    `CreateFromYawPitchRoll`, `Slerp`, `GetAngleFromQuaternion` and the `SwingLimit.MaximumSwingAngle`
    setter [source].

  Angles reach Bepu only as vectors and quaternions the core computed with its own trigonometry.

**Rationale**:

- Bepu documents determinism on one machine with a single thread, or with threads and
  `Simulation.Deterministic` [documentation, `QuestionsAndAnswers.md`].
- The sleep threshold compares with v·v + ω·ω ≥ 0, so a negative threshold never sleeps [source].
- The default refinement scheduler keeps a private frame index and calls `float.Log2`, a platform
  function [source]; the no-op scheduler removes both. Hashes were identical with and without it in
  the test scenes, but it matters for reset [measured].
- The hot path's math is deterministic: Bepu's own polynomial `MathHelper.Sin`/`Cos` for pose
  integration, no `FusedMultiplyAdd` or `MultiplyAddEstimate`, integer-only horizontal reductions
  [source].

**Alternatives considered**: `Deterministic = true` on one thread (harmless, does nothing without a
dispatcher); no allowlist (MMO1001's exemption cannot see into Bepu's internals).

**Risks**: a later Bepu version can add new `IsSupported` branches; a version bump is a new identity
and reruns the whole suite.

## R8. Bepu: pinning the server to 128-bit vectors

**Decision**: Every process that steps Bepu on the server runs with **`DOTNET_PreferredVectorBitWidth=128`**.
`VectorWidthGuard` in `src/MmoEngine.SimCore.Vehicles.Server/` (outside the analyzer) checks, before
any Bepu world is created, that `Vector<float>.Count == 4` and `!Vector256.IsHardwareAccelerated`, and
refuses otherwise with an error naming the variable. The selection run sets the variable on the child
processes that step Bepu only (R26); the vehicles test project sets it through its run settings.

**Rationale** [measured, .NET 10.0.15 on AVX-512]:

| Setting | `Vector<float>.Count` | Vector256 accelerated | Vector512 accelerated |
| --- | --- | --- | --- |
| default | 8 | yes | yes |
| `PreferredVectorBitWidth=128` | **4** | **no** | **no** |
| `MaxVectorTBitWidth=128` | 4 | yes | yes |
| `EnableAVX2=0` | 4 | no | no |
| `EnableAVX512F=0` | 8 | yes | yes (the knob is ignored) |
| `EnableAVX512=0` | 8 | yes | no |
| `EnableHWIntrinsic=0` | 4 | no | no |

- Bepu stores bodies, constraints and pairs as bundles of `Vector<float>.Count` lanes, and has
  `Avx.IsSupported && Vector<float>.Count == 8` paths and `Vector256`/`Vector512.IsHardwareAccelerated`
  branches [source]. `PreferredVectorBitWidth=128` is the one setting that drops all of them while
  keeping AVX2 and FMA for the rest of the process.
- Box and car hashes were identical at the default 8 lanes, at 4 lanes and with AVX or AVX2 off; only
  `EnableHWIntrinsic=0` changed one scene [measured]. The pin is the safe choice for larger scenes.
- **Server-wide effect, for the record (FR-027).** The setting is process-wide: every `Vector<T>` user
  and every BCL path gated on `Vector256`/`Vector512.IsHardwareAccelerated` (span search, UTF-8,
  `System.Text.Json`) drops to 128-bit, while `Avx2.IsSupported` and `Fma.IsSupported` stay true. The
  pin cost was noise-level on a two-car scene (101–139 µs pinned against 125 µs) [measured].

**Side finding.** On .NET 10.0.15, `DOTNET_EnableAVX512F=0` is a no-op [measured]: `Avx512F.IsSupported`
and `Vector512.IsHardwareAccelerated` stay true, and the effective knob is `DOTNET_EnableAVX512=0`.
`.github/workflows/simcore.yml` and 014's sweeps set `DOTNET_EnableAVX512F: "0"`, so that pin has
probably done nothing since the move to .NET 10. A related runtime discussion is dotnet/runtime PR
99784. This feature does not change those workflows (FR-029 keeps 015's jobs unchanged); it is a
patch-notes follow-up.

**Alternatives considered**: `MaxVectorTBitWidth=128` (leaves Bepu's `Vector256` paths on);
`EnableAVX2=0` (turns off FMA and AVX2 everywhere); no pin (unproven in general).

**Risks**: the variable must be set by whatever launches the process; it was not tested as a
`runtimeconfig.json` property, so the guard covers a missed setting.

## R9. Bepu in WebAssembly

**Decision**:

- `interp-prebuilt` and `interp-relinked` are built normally. They agreed bit for bit with x64 in
  every mode tested: Node with the jiterpreter on and off, and a Chromium module Worker [measured].
- **`aot` is expected NotBuilt (`BuildFailed`).** `mono-aot-cross` aborts on
  `TriangleCylinderTester.TryAddInteriorPoint` (`mini-llvm.c:10284, condition 'LLVMTypeOf (lhs) ==
  LLVMTypeOf (rhs)' not met`) in under 10 s [measured]. The adapter does not patch Bepu and does not
  use the internal `_AOT_InternalForceInterpretAssemblies` item. The failure is a candidate upstream
  report to dotnet/runtime (patch-notes follow-up).

**Rationale** [measured]:

- The wasm runtime has `Vector<float>.Count = 4`, `PackedSimd`, and `wasmEnableSIMD: true`; SIMD edge
  cases (min/max with ±0 and NaN, square root, division, multiply-add without contraction) agree with
  x64.
- Line 55 of `TriangleCylinderTester` adds the `Vector<int>` results of `Vector.GreaterThan` on floats,
  which Mono's LLVM lowering mistypes; rewriting it moved the abort to the next method, so patching is
  open-ended. The crash is at compile time, so avoiding the cylinder–triangle pair at run time does not
  help. 2.4.0 has the same line [source].
- AOT with `WasmEnableSIMD=false` ran clang past 15 minutes (FR-021's bound).
- Mixed AOT (Bepu forced to the interpreter) builds in 69 s but **diverges from step 2** on the car
  scene in Node and in Chromium, and is no faster than the interpreter (1.9–2.0 ms per step).
- Download added by Bepu in prebuilt: `BepuPhysics.wasm` 611,605 B raw / 176,357 B Brotli /
  214,193 B gzip and `BepuUtilities.wasm` 72,981 / 21,956 / 26,291, about 198 KB Brotli; no trim
  warnings.

**Alternatives considered**: patching Bepu for AOT; mixed AOT (diverges); AOT without SIMD (too slow,
and it changes the vector path).

**Risks**: if gate 9 picks `aot`, Bepu cannot win that mode (FR-026). The diverging mixed build
suggests Mono's LLVM AOT may also miscompile Bepu-style SIMD code silently.

## R10. Jolt: build, determinism settings and the single-threaded platform

**Decision**:

- Build every target with `CROSS_PLATFORM_DETERMINISTIC=ON`, `CMAKE_BUILD_TYPE=Distribution`,
  `-ffp-contract=off`, `-fno-exceptions -fno-rtti`, `JPH_OBJECT_LAYER_BITS=16`, every `TARGET_*` and
  `JPH_USE_VK|DX12|MTL|CPU_COMPUTE` option `OFF`, `ENABLE_OBJECT_STREAM`,
  `DEBUG_RENDERER_IN_DEBUG_AND_RELEASE` and `PROFILER_IN_DEBUG_AND_RELEASE` `OFF`, and identical `JPH_*`
  defines for Jolt and our shim. Compile only `Jolt/` through `Build/CMakeLists.txt`; never vendor the
  repository's `Assets/` (LGPL-3.0 racetrack data and other third-party assets).
- **Single-threaded platform without patching Jolt**: build with `JPH_PLATFORM_BLUE` and supply our own
  29-line `Jolt/Core/PlatformBlue.h` (in the shim's include directory) whose mutex, rwlock and semaphore
  macros compile to nothing, as `Core.h`'s comment invites. The wasm build also defines
  `JPH_PLATFORM_WASM`, because BLUE otherwise suppresses wasm CPU detection. Step with
  `JobSystemSingleThreaded` and a fixed `TempAllocatorImpl`.
- **Server**: a clang-built `.so` with `-fPIC`, AVX2 (Jolt's x64 default), `-fvisibility=hidden` and a
  version script that exports only `veh_*`; **libstdc++ linked dynamically** (R18).
- **Browser**: the same sources with Emscripten 3.1.56 from the .NET 10.0.15 workload packs, fetched by
  exact NuGet version and SHA-512 (R19): `-O3 -msimd128 -sMALLOC=emmalloc -fno-exceptions -fno-rtti
  -sMODULARIZE -sEXPORT_ES6 -sENVIRONMENT=web,worker,node -sFILESYSTEM=0 -sALLOW_MEMORY_GROWTH=0` and a
  fixed initial memory.

**Rationale**:

- `JPH_CROSS_PLATFORM_DETERMINISTIC` suppresses `JPH_USE_FMADD`, turns the `rsqrt` approximations into
  `a / Sqrt(b)` and sets a feature bit that `RegisterTypes` checks. Jolt's trigonometry is always its own
  polynomials, never libm. `-ffp-contract=off` comes from `Build/CMakeLists.txt`, not `Jolt.cmake`
  [source]. With CPD off the control build diverged on every scenario [measured], so the test is
  sensitive.
- **Cross-build agreement** [measured, J3]: clang 18 and g++ 13 x64 with AVX2, and Emscripten 3.1.56
  wasm32 scalar and SIMD128, gave identical chained hashes over 600–1,800 steps with 4 and 8 vehicles,
  and the .NET server (`[LibraryImport]`), prebuilt wasm (`[JSImport]` into a separate module),
  relinked and AOT (static link) paths all gave identical chains over 3,000 steps.
- **Stock Jolt takes locks every step** [measured, J5]: 31 calls per step with 4 cars (5
  `pthread_mutex_lock`, 16 `pthread_rwlock_rdlock`, 10 `pthread_rwlock_wrlock`), all uncontended.
  `PhysicsSystem::Update` locks unconditionally (`PhysicsSystem.cpp` lines 242–250) and the no-lock body
  interfaces do not avoid them [source]. Under Principle II an uncontended mutex is still a lock, so a
  stock build is Failed (`TakesLock`). With the BLUE header: 0 lock calls over 1,000 steps, no pthread
  mutex or rwlock symbols and no `pthread_create` imported, and identical hashes [measured].
- The `.so` NEEDs `libstdc++.so.6`, `libm`, `libgcc_s.so.1` and `libc`, all already loaded by the .NET
  process; its only libm import is the exact `fmodf@GLIBC_2.38` (Ubuntu 24.04 runners have glibc 2.39)
  [measured].
- The library builds in 15.7 s natively and about 19 s per wasm variant [measured].

**Alternatives considered**: patching Jolt; accepting Failed (`TakesLock`); interposing
`pthread_mutex_lock` as a no-op inside the `.so` (would hide locks from the test); upstream emsdk 6.0.8,
which Jolt's determinism CI uses (not tested here, and another download host); `-static-libstdc++`
(adds GPL-3.0 WITH GCC-exception code to the candidate).

**Risks**: BLUE is meant for a console platform and a later release may change its macro set, so the
lock test also requires no lock symbol and no `pthread_create` in the shipped `.so` (R16). Jolt's own
determinism CI does not cover vehicles [source], which is why the agreement suite runs them.

## R11. Jolt: body setup, state access and allocation

**Decision**:

- The chassis shape is `OffsetCenterOfMassShape(RotatedTranslatedShape(offset, BoxShape(halfExtents,
  convexRadius 0)), −offset)`, so the centre of mass is exactly the body origin and `GetPosition()`
  equals `GetCenterOfMassPosition()` without arithmetic. Mass and inertia are provided
  (`MassAndInertiaProvided`), `mMotionQuality = LinearCast`, `mAllowSleeping = false`.
- State is set and read only through `GetBodyInterfaceNoLock()` and `GetBodyLockInterfaceNoLock()`
  (FR-034).
- Jolt's allocation hooks (`JPH::Allocate`, `Reallocate`, `Free`, `AlignedAllocate`, `AlignedFree`)
  point at a per-world arena sized at load, which counts any fallback to the system allocator.

**Rationale**:

- Restoring a body through `GetPosition()` → `SetPositionRotationAndVelocity` changed the bits of 2 of 4
  centre-of-mass positions, because Jolt stores the centre of mass and derives the position [measured,
  J7]. With the centre of mass at the origin, nothing is derived.
- `BoxShape`'s default convex radius of 0.05 m rounds the box; the spec requires the configured box with
  no rounding.
- Stock Jolt mallocs 1 + 4 per vehicle per step, from `WheeledVehicleController`'s local arrays
  [measured, J6]. The core wheel model removes that source; the hooks make the step's allocation a
  property of the shim that its own tests check (R17).

**Alternatives considered**: storing the engine's derived position (bits change on restore); the stock
allocator (glibc's arena lock is a lock under Principle II that the lock counter cannot see).

**Risks**: `RestoreState`-style ordering is not used, but `CreateBodyWithID` needs `maxBodies` above the
largest vehicle ID; vehicle IDs are limited to [1, 65535] (contracts/vehicle-formats.md).

## R12. Rapier: release, determinism, the license failure and the fit check's scope

**Decision**:

- The pin and settings, for when Rapier can be built: `rapier3d = "=0.36.1"`, `default-features = false`,
  features `dim3`, `f32`, `std`, `enhanced-determinism`; no `simd8` (a `compile_error!` with
  determinism), no `parallel` except in the FR-027 evidence build; `panic = "abort"`, `lto = "fat"`,
  `codegen-units = 1`, `opt-level = 3`; the libm symbols pinned in the shim (below); a fresh
  `PhysicsWorld` per step (R5); `TriMesh` statics (R4); an `extern "C"` shim over the byte ABI (R13);
  a raw-export `wasm32-unknown-unknown` browser module with no wasm-bindgen (R14).
- **Criterion (c) fails at 0.36.1.** `parry3d 0.31.1` has a direct, non-optional dependency on
  `foldhash 0.2.0`, licensed **Zlib** [measured, its `Cargo.toml` and `cargo tree -e normal`]. Zlib is
  neither MIT nor Apache-2.0, and no SPDX `OR` option rescues it. parry3d uses
  `foldhash::fast::FixedState` as its `HashMap`/`HashSet` hasher (`src/utils/hashmap.rs:63`), and its
  `alloc` feature also turns on `hashbrown/default-hasher`, which pulls foldhash again; `rapier3d/std`
  requires `alloc` [source].
- **The fit check is a time box (spec Assumptions) that also respects Principle VII.** It evaluates the
  criteria in the order (c), (b), (d), (f), (a), (e): the static ones first, the build last. A criterion
  that cannot be evaluated without linking a component that (c) rejected is recorded as **fail with
  `notEvaluated: true`** and a reason, never as a pass. At 0.36.1 the expected verdicts are:

  | Criterion | Expected | Evidence |
  | --- | --- | --- |
  | (c) license scan | fail | `foldhash 0.2.0`, `Zlib`, through `parry3d 0.31.1`; `cargo metadata --locked --offline` on the committed manifest |
  | (b) pinned sources, determinism feature, locked dependencies | pass | the manifest's exact version and features, `Cargo.lock` checksums, `--locked` |
  | (d) shared interface unchanged | pass | the static reference check, and the Rapier C# adapter compiling against `IVehicleEngine` |
  | (f) ≤ 1,500 Rapier-specific lines | pass | the non-blank line count of Rapier's files; the evidence says the shim is not written |
  | (a) published interfaces cover the adapter | fail, not evaluated | needs the shim, which is not written while (c) fails |
  | (e) both builds within 15 minutes, flat acceleration runs | fail, not evaluated | building would link foldhash (Zlib), outside Principle VII |

  So Rapier is **NotTested (`FitCheckFailed`: c)** in every mode, with `LicenseNotAllowed: foldhash
  0.2.0, Zlib` as a further reason, and the decision record lists foldhash as a PATCH-amendment
  candidate (FR-031). If the maintainer approves foldhash, writing the shim (designed below and in R13)
  and rerunning the selection is the follow-up.
- **What is committed for Rapier now**: the C# adapter (bindings over the shared byte ABI, covered with a
  fake binding, R28), and a manifest-only crate under `src/MmoEngine.SimCore.Vehicles.Rapier/native/`
  (`Cargo.toml`, `Cargo.lock`, `rust-toolchain.toml` and an empty `src/lib.rs`), which the license scan
  and criterion (b) read. No Rust code is compiled or linked.

**Rationale**:

- Principle VII: "Code dependencies, meaning anything linked into, bundled with or deployed as part of
  our software, MUST be licensed MIT or Apache-2.0", and another permissive package "MAY be used only
  after a PATCH amendment". Building the shim, its tests, its coverage run or its browser module would
  link foldhash into our software. Reading Cargo metadata links nothing.
- The spec's time box says Rapier's exit is the fit check, "rather than spending more effort". Not
  writing a shim that cannot be built or covered (R28) is that exit.
- **Evidence for the follow-up** [measured, from the research probe outside the repository]: per-step
  vehicle hashes matched bit for bit over 900 steps × 8 vehicles in two scenes across x86_64 SSE2,
  x86_64-v3, wasm32-unknown-unknown scalar and simd128, wasm32-unknown-emscripten, .NET P/Invoke, and
  the `parallel` feature at 1 and 4 threads; 0 lock calls and 0 system calls over 1,000 steps.
- **The libm leak, for the follow-up** [measured]: parry and Rapier call inherent `f32` methods, which
  win over simba's libm-pinned trait even with `enhanced-determinism`, so the native `.so` imports
  glibc's `acosf`, `cosf`, `sinf`, `sincosf`, `expf`, `log2f` and `powf` (trimesh pseudo-normals and the
  BVH optimiser). Over all 2³² inputs glibc and `libm` 0.2.16 differ on 11,575,817 `log2f` inputs and
  1,695,544,999 `acosf` inputs. A shim that defines those symbols over `libm = "=0.2.16"`, links with
  `-Wl,-Bsymbolic` and is checked by `nm -D --undefined-only` removes the leak.

**Alternatives considered**:

- **Building and testing Rapier in CI anyway**: links foldhash; rejected under Principle VII.
- **`[patch.crates-io]` with an MIT foldhash stub**: a patched dependency set, which breaks (b) and works
  around Principle VII. Rejected.
- **The official C bindings** (`bindings/c`, `rapier3d-ffi`): unpublished (`publish = false`), need Rust
  1.99, fine-grained (681 functions, dozens of calls per vehicle per step, against FR-032), and cannot
  restore a vehicle controller [measured].
- **npm `@dimforge/rapier3d-deterministic` 0.21.0**: crosses values as JavaScript numbers (FR-032) and
  lags the crate.
- **Stopping at (c) with no other verdicts**: conflicts with "a pass or fail, with evidence, for each
  criterion" (US6 scenario 1).

**Risks**: Rapier releases often (0.35.0 to 0.36.1 shipped August to October 2026); a later release
might drop foldhash, and a bump is a new identity anyway.

## R13. Native interop: one byte ABI, two bindings

**Decision**:

- **One C ABI per native engine**, the same shape for Jolt now and Rapier later
  ([contracts/engine-adapter.md](contracts/engine-adapter.md#native-abi)): `veh_abi_version`,
  `veh_identity`, `veh_load(in, inLen, out, outCap)` and `veh_step(in, inLen, out, outCap)`, each
  returning an `int32` length or a negative code. Bodies cross as 56-byte little-endian records (ID and
  13 floats as raw bits); the load call carries the static triangles and the engine settings.
- **One C# adapter per engine**, a plain `net10.0` class library under the analyzer
  (`src/MmoEngine.SimCore.Vehicles.Jolt/`, `.Rapier/`) holding: an internal engine-call interface; a
  `[LibraryImport]` binding for the server; a `[SupportedOSPlatform("browser")]` `[JSImport]` binding;
  and the choice between them by `OperatingSystem.IsBrowser()`, made once in the binding factory.
- **Server**: `[LibraryImport]` with blittable pointers from `fixed` spans, and
  `NativeLibrary.SetDllImportResolver` resolving the engine library from the directory the engine
  registry is given.
- One crossing per substep (K = 1: once per step), plus one at load.

**Rationale** [measured]:

- A `Microsoft.NET.Sdk` class library holding both generated-interop forms builds with
  `TreatWarningsAsErrors` (CA1416 satisfied by the guard), and a WebAssembly host referencing it binds
  and calls its `[JSImport]` (N6).
- The .NET server path reproduced the native hashes exactly for Jolt (3,000 steps) and in Rapier's
  probe (900 steps).
- Our own coarse shim beats the bindings on offer: **joltc** (12,875 lines of C++, no `StateRecorder`,
  no single-threaded job system) and **JoltC** (pinned to v5.6.0, no vehicle functions); a vendored
  binding that size would fall under the 95% coverage gate (R28) [measured].

**Alternatives considered**: separate server and browser adapter assemblies (duplicated marshalling);
`#if` per target (needs multi-targeting, and MMO1008 bans `#if`); fine-grained bindings (many crossings
per step).

**Risks**: the analyzer's per-adapter exemption must name the interop generator's emitted members
(`JSFunctionBinding`, `JSMarshalerArgument`) (plan Complexity Tracking).

## R14. Browser integration: a separate engine module behind `[JSImport]`

**Decision** (confirms FR-032):

- **Jolt's browser build is its own Emscripten module** (`jolt-engine.mjs` and `jolt-engine.wasm`, plus
  our ~30-line glue `jolt-glue.mjs`), shipped by its host project under `wwwroot/engine/`. Rapier's
  follow-up module is a raw-export `wasm32-unknown-unknown` cdylib with zero imports and its own glue.
- **Loading**: the runner boots .NET, imports `engine/<engine>-glue.mjs`, which instantiates the module
  and returns its functions, and calls `runtime.setModuleImports('mmo-vehicle-engine', imports)` before
  any export can reach the adapter. Engine files are checked against their SHA-256 in the engine
  identity before instantiation, because the runtime does not integrity-check them.
- **Crossing**: one `[JSImport]` per engine call, with two `[JSMarshalAs<JSType.MemoryView>]
  Span<byte>` parameters (input and output). The glue copies the input with `inView.copyTo(inArea)` and
  the output with `outView.set(outArea.subarray(0, n), 0)` into fixed areas of the engine's memory,
  which never grows. No float crosses as a JavaScript number.
- **Worker rule**: never assign `self.onmessage` before importing `dotnet.js`; use
  `addEventListener('message', …)`, as 015's `web/worker.mjs` already does.

**Rationale** [measured]:

- **Static linking (`NativeFileReference`) exists only in `interp-relinked` and `aot`.** Any
  `NativeFileReference` sets `WasmBuildNative=true` [source]; a prebuilt publish
  (`-p:UsingBrowserRuntimeWorkload=false`) silently drops the reference, ships the runtime pack's
  `dotnet.native.wasm` byte for byte, and fails at the first call with `DllNotFoundException`. So a
  statically linked engine would be NotBuilt in the mode 015's gating job tests.
- **Rapier cannot be linked statically with .NET 10's emsdk** at all on stable Rust: binaryen 117's
  `wasm-opt` rejects `--enable-bulk-memory-opt`, a target feature of Rust 1.97's prebuilt `core`, and the
  exception models differ [measured]. The spec's assumption understated this; its conclusion stands.
- **The crossing is cheap.** `[JSImport]` with two MemoryViews: 1.5–2.0 µs per call through a real
  Jolt module in Node prebuilt; a toy byte step costs 0.3–0.5 µs more per crossing than static P/Invoke
  in the same mode, about 0.003% of a 16.7 ms frame. With the jiterpreter off it is about 0.8 µs per
  step. Jolt with one car: 16–20 µs per step through .NET against 10–12 µs driven from JavaScript only.
- **No managed allocation**: 10,000 Span-step crossings allocated 0 bytes in every mode, in Node and in
  Chromium, and `GC.GetAllocatedBytesForCurrentThread` does track Mono WebAssembly allocations (a control
  of 1,000 × `new byte[16]` read 32,000 bytes). 015 R17 had left this unverified.
- **Numbers do not keep bits**: a signaling NaN `0x7f800001` passed as a `float` comes back as
  `0x7fc00001`; as bytes it is unchanged.
- **Loading works in every mode**: `setModuleImports` after `dotnet.create()` and before the first call
  binds in prebuilt, relinked and AOT; an unregistered module aborts the runtime
  (`Assert failed: ES6 module not-registered was not imported yet`), so the runner registers first.
- **A `self.onmessage` set before `dotnet.js` makes `dotnet.create()` hang**, because `dotnet.js`
  treats the Worker as a pthread deputy (`globalThis.onmessage||(globalThis.dotnetSidecar=!0)`) [source].
- Engine files in `wwwroot/engine/` are published with `.br` and `.gz` siblings by the same SDK step as
  `_framework/`, so the download measurement reads them the same way; they are not in the boot
  manifest, so the list is explicit.

**Alternatives considered**: static linking (N1–N3); a hybrid (static in relinked and AOT, a module in
prebuilt: two integration paths per engine); wasm-bindgen or `rapier.js` (JavaScript numbers, another
pinned CLI); `JSHost.ImportAsync` from managed code (relative URLs resolve under `/_framework/`);
raw pointers (unsafe code on both sides for 0.04–0.1 µs).

**Risks**: `ReadOnlySpan<byte>` is refused by the generator (SYSLIB1072), so the adapter keeps a
writable input buffer; a MemoryView is valid only during the call, so the glue copies inside it; the
`dotnetSidecar` heuristic is internal to the SDK (pinned by `global.json`).

## R15. Build modes, hosts and runner changes

**Decision**:

- **One host per engine**: `src/MmoEngine.SimCore.Vehicles.Fixture.Wasm/`, `.Bepu.Wasm/` and
  `.Jolt.Wasm/` (Rapier's host is part of its follow-up). Each uses 015's host shape: the WebAssembly
  SDK, `Exe`, `AllowUnsafeBlocks`, one-line `[JSExport]` delegations
  ([contracts/engine-adapter.md](contracts/engine-adapter.md#browser-hosts)). The names keep
  `simcore.yml`'s `src/MmoEngine.SimCore*/**` filter matching.
- **Additive runner changes** in `tools/agreement/`:
  - `BuildNames.Known` and `agreement.test.mjs`'s `NODE_BUILDS` add `node-interp-relinked-nojit`;
    `server.mjs`'s `WORKER_BUILDS` and the known list add `chromium-worker-relinked` and
    `chromium-worker-aot`.
  - `worker.spec.mjs` derives the Worker build name from `AGREEMENT_MODE` (unset is `interp-prebuilt`,
    so 015's runs are unchanged).
  - `runtime.mjs`'s `bootRuntime` gains optional `assembly`, `exportsPath` and `engineGlue` parameters
    whose defaults are 015's values.
  - `server.mjs` gains a `GET /engine/*` route served from `<app>/engine/`; 015's host has no such
    folder and answers 404 there.
  - `agreement compare` prints, for a 015 work directory (manifest version 1), exactly 015's seven
    builds, and for a vehicle work directory (manifest version 2) the builds of the modes it lists.
- **Publish order** in a job: every host's prebuilt publish before the workload is installed, then the
  relinked publishes, then the AOT publishes, each host with its own intermediate directory
  (`-p:BaseIntermediateOutputPath`), so an AOT publish's stripped assemblies never reach an
  interpreted publish (015 T309).

**Rationale**: FR-018 and FR-032 require per-candidate hosts, and 015's `MmoEngine.SimCore.Wasm`, its
publish and its `agreement` job must not change (US2 scenario 6).

**Alternatives considered**: one host with every engine (a download measurement that cannot separate
engines; Bepu's assemblies in Jolt's download); new Playwright projects per mode (three times the
projects for one variable).

**Risks**: none beyond R14's.

## R16. The lock test (FR-034)

**Decision**:

- **Native path (Jolt now, Rapier later).** `tools/vehicles/lockcount/liblockcount.so`, loaded with
  `LD_PRELOAD` into a child `simcore vehicles lockcount --engine <e>` process, interposes the pthread
  mutex, read-write-lock, condition-variable and semaphore functions, libc's `syscall()` (counting
  `SYS_futex`), and the glibc allocator entry points. It counts only calls whose return address lies in
  the engine library (`dladdr(__builtin_return_address(0))`), on the stepping thread, between start and
  stop marks the command sets through two exported functions, around 1,000 steps after warm-up. Any
  counted lock call is `TakesLock`, naming the function. Allocator calls are reported as evidence
  (R17). The report also lists the lock symbols the shipped library links (`nm -D --undefined-only`),
  and the lock test requires that list to hold no pthread lock function and no `pthread_create`.
- **The counter is itself tested**: `tools/vehicles/lockcount/locktestlib.c` takes one uncontended
  `pthread_mutex_lock`/`unlock` pair per step; the test requires exactly 1,000 counted locks over
  1,000 steps.
- **Managed path (Bepu).** MMO1006 covers the adapters, the fixture engine and the core. For Bepu's own
  assemblies, "the methods the step reaches" are **the methods the step actually executes**, computed
  dynamically: `BepuLockSites` scans `BepuPhysics.dll` and `BepuUtilities.dll` with Mono.Cecil for every
  call site to a member on MMO1006's blocking list (a `lock`'s `Monitor.Enter`, `SpinLock.Enter`,
  `Thread.SpinWait`, `SpinWait.SpinOnce`, waits on tasks and events, and the rest of the list), writes an
  instrumented copy with a counter before each site, and runs 1,000 steps after warm-up on the copy in a
  child process. Any executed site is `TakesLock`, naming the member and the Bepu method. The static
  site list is reported as evidence. The instrumentation is itself tested on a fixture assembly that
  takes one `lock` per step.

**Rationale**:

- Inside a .NET process a global count also sees runtime threads (GC, JIT), and the stepping thread can
  block at a GC poll on P/Invoke return [measured, J5], so the native count filters by return address
  and thread.
- Stock Jolt: 31 lock calls per step; BLUE Jolt: 0 [measured]. Rapier's probe: 0 lock calls and 0
  system calls [measured].
- **A static reachability scan fails Bepu by construction** [source, measured, RB7]:
  `lock (batchRemovalLocker)` in `ConstraintRemover.RemoveConstraintsFromTypeBatch` is reachable from
  `Timestep` through `NarrowPhase.Flush` on the single-threaded branch, and `SpinLock.Enter` in
  `IslandAwakener`. Measured with a counter beside that `lock`: 3 entries per 300 box steps and 73–82
  per 300 car steps in a persistent world, and **0 in clear-rebuild mode**, which has no carried-over
  pairs and so no removals. Measuring what runs is the only reading under which the history-free mode's
  real behavior decides.
- Rust's standard mutex and other user-space locks are invisible while uncontended; the linked-symbol
  evidence covers them, as the spec says.

**Alternatives considered**: a static reachability scan of Bepu (Failed by construction); coverlet over
the whole run (counts warm-up too); patching the lock out of Bepu (changes the identity); counting
futex calls with `strace` or perf events (needs extra privileges or packages on the runner; the
`syscall()` interposer catches Rust's futex path, and glibc's own futex use is behind the counted
pthread calls).

**Risks**: Mono.Cecil 0.11.6 (MIT) is a new tool dependency of the selection library; the instrumented
copy is evidence only and never steps a stream that is compared.

## R17. Allocation (FR-025) and native allocation

**Decision**:

- Every candidate's vehicle step allocates no managed memory after warm-up, checked by zero-allocation
  tests in the style of 015 (ten windows of 1,000 steps, each 0 bytes, with one rerun when exactly one
  window is not). Bepu's `BufferPool` is sized at world creation for the configured maximum vehicles and
  contacts; the core and the adapters use world-owned buffers.
- **Native allocation is a shim property, tested by the shim's own tests**: Jolt's allocation hooks point
  at a per-world arena (R11), whose tests require zero system-allocator fallbacks over 1,000 steps after
  warm-up. The lock test's interposer also reports the glibc allocator calls made from the engine
  library during its window, as evidence in the report.

**Rationale**: Bepu clear-rebuild allocated 0 managed bytes per step on a box scene and 5 B per step on
the car scene, on x64 and in Mono wasm [measured, RB6]; the 5 B source was not found, and the
one-body-per-vehicle design removes the wheel constraints it most likely came from [estimate]. The
constitution's "no per-tick allocations" covers gameplay code, and the engine step runs in a region's
tick, so native code is designed allocation-free too; FR-025 states only the managed check, so the
native count is evidence, not a status.

**Alternatives considered**: a new `NativeAllocation` status (not in FR-037's table); ignoring native
allocation (glibc's arena lock is invisible to the lock counter).

**Risks**: Bepu's remaining 5 B/step (if it persists with one body per vehicle) must be found before its
zero-allocation test passes; a Bepu that allocates fails that test, which the selection reports.

## R18. The license scan (FR-031)

**Decision**: The scan is our own code in `src/MmoEngine.SimCore.Vehicles.Selection/License/`, in three
stages ([contracts/license-scan.md](contracts/license-scan.md)):

1. **Collectors** emit a component inventory, `{ecosystem, name, version, expression, basis, origin}`:
   - `nuget`: from the publish's `deps.json` (server) and `project.assets.json` runtime assets
     intersected with the boot manifest (browser), each license from the package's `.nuspec`
     `<license type="expression">`; a `type="file"` license or a bare `licenseUrl` is `NoLicense` unless
     a committed override names the package, version and expression;
   - `cargo`: `cargo metadata --format-version 1 --locked --offline --filter-platform <triple>` after
     `cargo fetch --locked`, walking normal edges only and dropping proc-macro crates and everything
     reachable only through them; plus the Rust sysroot crates the link used, when a Rust artifact is
     built;
   - `source`: every Jolt file in the build's compiler depfiles must carry `SPDX-License-Identifier:
     MIT`;
   - `emscripten`: every `lib*.a(member.o)` of `-Wl,--trace`, mapped to its source by a committed table
     generated from Emscripten's `tools/system_libs.py` for the pinned version, classified by the
     source's own notice, else by its owner's default (musl `MIT`; LLVM libc++, libc++abi, libunwind and
     compiler-rt `Apache-2.0 WITH LLVM-exception`; Emscripten's own files and the JS glue `MIT OR NCSA`);
   - `os-toolchain`: each shared library a shim NEEDs and each static input the system linker adds
     (`crti.o`, `crtn.o`, `crtbeginS.o`, `crtendS.o`, `libgcc.a`, `libc_nonshared.a`, and libstdc++
     headers in the depfiles), mapped to its runner-image source package.
2. **A boundary step** marks the components the core-only builds already have. It is read at the
   **component** level: the core-only server process maps `libc.so.6`, `libm.so.6`, `libgcc_s.so.1`,
   `libstdc++.so.6` and `ld-linux`, so glibc, the GCC runtime and libstdc++ (headers included) are
   boundary packages, listed in the report with their expressions and evidence, never silently dropped
   and never blocking. A test runs the core-only server build and asserts the mapped set it relies on.
3. **A pure classifier** applies FR-031's rules: SPDX precedence WITH > AND > OR with parentheses; the
   legacy `A/B` as `A OR B`; `MIT` and `Apache-2.0` pass; `Apache-2.0 WITH LLVM-exception` passes as
   Apache-2.0; every other `WITH`, `+`, `LicenseRef-*`, an empty, missing or unparseable expression
   fails closed (`LicenseNotAllowed`, `NoLicense` or `UnparseableExpression`, each naming the component
   and its raw expression).

The output is canonical JSON (sorted, LF, no timestamps, paths relative to named roots) and is the
report's `license` section per candidate and target. External tools (cargo-deny, nuget-license) are not
used as the gate.

**Rationale** [measured]:

- No single tool covers NuGet, Cargo, C++ sources and Emscripten archives, and the tools measure a
  different set than FR-031 asks for (cargo-deny walks the whole graph including proc-macros;
  nuget-license reads the restore graph, which includes build-only SDK packages and omits the runtime
  pack).
- cargo-deny 0.22.0 agreed with the prototype classifier on the Rapier graph: exactly `foldhash` (Zlib)
  and `unicode-ident` (`(MIT OR Apache-2.0) AND Unicode-3.0`, reached only through proc-macros) fail.
- **Results**: Bepu adds `BepuPhysics` and `BepuUtilities`, both `Apache-2.0`. Jolt's 430 compiled
  files are all MIT; its emmalloc module links 105 `libJolt.a` members, 74 musl `libc.a` members (none
  with a Sun notice: Jolt supplies its own trigonometry, so only `fmodf`, `frexp`, `scalbn(l)`,
  `copysignl`, `fabsl` and `__fpclassifyl` came in), libc++, libc++abi and compiler-rt members, and
  `libemmalloc.a(emmalloc.o)`. Rapier's shipped set at 0.36.1 is 49 crates on x86_64 and 48 on wasm32,
  of which exactly `foldhash 0.2.0` fails.
- **The spec's boundary text misses static toolchain code** [measured]: the system linker puts GCC's
  `crtbeginS.o`/`crtendS.o` (`GPL-3.0-or-later WITH GCC-exception-3.1`) and glibc's `crti.o`/`crtn.o`
  into every new `.so`, and the Jolt `.so` defines 59 header-instantiated `std::` templates from
  libstdc++. A file-level reading would make every native candidate LicenseBlocked by the platform's own
  linker, the "None by construction" the spec's assumption rejects. The component-level reading is
  recorded here; the implementing pull request adds one sentence to the spec's "License scan boundary"
  assumption naming startup objects and header-instantiated library code, and patch-notes follow-up 1
  covers the maintainer's question.
- **dlmalloc is already shipped by the core-only browser build**: the .NET 10.0.15 `dotnet.native.wasm`
  exports `dlmalloc`/`dlfree` (its `emcc-link.rsp` sets no `MALLOC`), so it sits inside the boundary;
  the engine module is a separate file and uses emmalloc as FR-031 requires.
- **Jolt's repository holds other licenses outside `Jolt/`** (`Assets/Racetracks/LICENSE.txt` is
  LGPL-3.0; `HelloWorld` is CC0-1.0; third-party test headers), so only `Jolt/` is compiled and nothing
  is vendored.
- **wasm-bindgen 0.2.130 would fail the scan** through `unicode-ident` in a normal dependency of
  `wasm-bindgen-shared`, so a Rust browser module must use raw exports.

**Alternatives considered**: cargo-deny plus nuget-license plus a hand list for C++ (three rule engines
and formats); ScanCode or a REUSE-style text scan (scans what is in a tree, not what is linked); a
strict file-level boundary (rejected above); classifying Emscripten by archive only (would pass a
linked Sun-notice math file, SPDX `SunPro`).

**Risks**: an SPDX precedence bug (pinned by the classifier's tests: `MIT OR Apache-2.0 AND
BSD-3-Clause` passes as `MIT OR (…)`); the Emscripten table must be regenerated when the pin changes,
and a version mismatch fails the scan; package names follow the GCC version, so the boundary list is
keyed by source package (`gcc-N`, `glibc`) and checked on the runner; Jolt's
`GaussianElimination.h` and `EigenValueSymmetric.h` say "Taken and adapted from Numerical Recipes" and
are declared MIT by their author, which a declaration-based scan cannot judge (recorded here as a
provenance note).

## R19. Engine identity and supply-chain pins (FR-002)

**Decision**:

- **Each engine has a committed pin file**, `simcore/vehicles/engines/<engine>.json`, holding its
  release, source pins, toolchain pins, build settings, engine settings (including the history-free mode,
  R5) and an `adapterRevision`, plus the list of build-input files whose SHA-256 enters the identity
  (build script, CMake file, BLUE header, lock files, `rust-toolchain.toml`). The **identity
  fingerprint** is XxHash64 (seed 0) over the canonical bytes of the pin file followed by those
  SHA-256s, printed as 16 lowercase hex digits ([contracts/engine-adapter.md](contracts/engine-adapter.md#engine-identity)).
- `adapterRevision` is bumped, like `SimCoreVersion.RulesRevision`, by any adapter or shim change that
  can change results; changing an adapter without bumping it is caught by `streams verify` against the
  committed expected hashes.
- **Toolchains**: .NET SDK per `global.json`; Jolt's native build with the runner's clang, whose
  version (`clang --version`) is recorded in the build outcome and checked against the pin file; Jolt's
  wasm build with the Emscripten 3.1.56 packs `Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64`,
  `.Cache.linux-x64` and `.Node.linux-x64` at exactly **10.0.15**, downloaded through a
  `PackageDownload` restore of `tools/vehicles/emsdk/emsdk.proj` and checked against the SHA-512s the pin
  file holds; Rust 1.97.0 in `rust-toolchain.toml` (for the Rapier follow-up); Jolt sources fetched by
  commit and checked against the tree hash.
- **NuGet**: `RestorePackagesWithLockFile` for the Bepu adapter and host, `--locked-mode` restores in
  the vehicle jobs; the lock file's `contentHash` is an identity input.
- **Caching**: CI caches each native build under `vehicle-engine-<engine>-<fingerprint>-<shim sources
  hash>`, so changed shim code rebuilds without changing the identity.

**Rationale**:

- The workload's own emsdk is the toolchain the research measured bit-exact, but installing the
  workload ties the Emscripten build to whatever SDK patch `setup-dotnet` installs (`global.json` rolls
  forward). Downloading the packs by exact version pins them independently of the SDK, from nuget.org,
  which CI already reaches, with NuGet's SHA-512.
- A fingerprint over a committed file keeps the staleness check (FR-028) a pure function of the
  repository.

**Alternatives considered**: upstream emsdk pinned by release (another download host, untested from
CI); hashing the adapter's source files into the identity (every refactor would make every stream
NotComparable); vendoring Jolt's `Jolt/` (5.7 MB in the repository).

**Risks**: the runner image's clang can change; a mismatch with the pinned version fails the native
build (`BuildFailed`, naming both versions) rather than building a different identity silently.

## R20. Scenario outcomes and oracles (FR-011)

**Decision**:

- Every outcome reads only the canonical state, the core `CollisionWorld` and the configurations, never
  an engine query or contact report, and is computed in `double` in tool code
  (`src/MmoEngine.SimCore.Vehicles.Server/Checks/`) from the `f32` state bits.
- Bands are relative to ideals from the configuration (top speed, v²/2b, L/tan δ), never to another
  candidate. Time windows are step counts, ⌈t × rate⌉.
- **Definitions**: speed is the horizontal centre-of-mass speed |v_xz|; wheel contact is the state's
  contact flag (the core ray); wheel penetration is max(0, (rest − travel) − (ray distance − radius));
  chassis penetration is the oriented box's penetration depth against the core world's triangles
  (separating-axis test); "upright" is the chassis up axis within the configured angle of world up;
  the heading is the forward axis's angle in the horizontal plane; the turning radius is the
  least-squares circle through the chassis positions over the turn.
- **Airborne off the ramp**: a maximal run of steps with no wheel in contact, starting within 3 steps of
  the chassis centre crossing the lip plane; landing is the first contact step after it beyond the lip;
  "settled" is all four wheels in contact and upright within ⌈2 s × rate⌉ steps of landing.
- **Wall**: no chassis corner ends a step past the far-face plane inside the wall's extent. The course's
  thin wall is 0.1 m thick: thinner than one step's travel at top speed (1.0, 0.67 and 0.33 m at 20, 30
  and 60 Hz), thicker than every engine's resting penetration.
- **Spawning** at static equilibrium, compression m·g / (4k) (0.109 m with the placeholders), so the
  "four wheels in contact after 1 s" clause does not test spawn bounce.
- **US1 scenario 6's first clause** (the contact event appears exactly when the separation first drops
  within the tolerance) tests the core's derivation, not the engine; the penetration and closing-speed
  clauses test the engine. The plan records this so the report does not read the first clause as engine
  evidence.

**Rationale**: the core model makes "wheel in contact" and "airborne" one definition everywhere; engine
contact callbacks have engine-specific order and speculative contacts (Jolt documents its order as
nondeterministic).

**Alternatives considered**: engine contact callbacks; bands relative to another engine (circular).

**Risks**: the thresholds are placeholders in the selection configuration (FR-036); a bad placeholder
can fail every candidate, and the report shows each measured value beside its bound.

## R21. The vehicle test world and the named scenarios (resolved here)

The research notes did not lay out the course or the streams. Defaults:

**Decision**:

- **`simcore/vehicles/content/vehicle-world.mmcd`**, generated by `VehicleWorldGenerator` from a fixed
  seed, `MMCD` version 1 (015's format, unchanged): a 512 × 384 m heightfield with 2 m cells, enclosed
  by 3 m mesh walls, holding five zones ([data-model.md](data-model.md#vehicle-test-world)):
  - a flat lane 400 m long and 24 m wide (acceleration, braking and the flat runs at 20, 30 and 60 Hz);
  - a flat 60 × 60 m pad (the turning circle; the ideal radius is 4 m);
  - rolling terrain, sum-of-sines heights of 0.5–1.5 m amplitude over 20–60 m wavelengths (US1 scenario
    4), crossed along both cell axes and the diagonal;
  - a ramp zone: a mesh ramp 10 m long rising 1.6 m (9.1°) with a sharp lip, and flat landing ground 40 m
    beyond it (US1 scenario 5);
  - a thin wall: a mesh slab 0.1 m thick, 3 m tall and 20 m wide across the flat lane's far end
    (US1 scenario 7);
  - an open flat arena 120 × 120 m (collisions, the pile-up and the generated streams).
  The heightfield's degenerate triangles are dropped by the core as in 015.
- **Named vehicle streams per candidate**, `simcore/vehicles/streams/<engine>/`, recorded only by
  `simcore streams record --engine <engine> --reason` (FR-017); the definitions are in
  `VehicleScenarioCatalog` ([data-model.md](data-model.md#vehicle-scenarios)): `flat-accel-20hz`,
  `flat-accel-30hz`, `flat-accel-60hz`, `brake-to-stop`, `turning-circle`, `rolling-terrain`,
  `ramp-jump`, `collide-head-on`, `collide-side`, `collide-rear`, `pile-up` (four vehicles),
  `thin-wall`, `rejected-commands` and `characters-and-vehicles`: 14 streams, against FR-010's 12.
- **Generated streams**: `vseed-<nnn>` for seeds 1–50, each 8 vehicles for 20 s of simulated time in the
  arena at a rate cycling 20, 30 and 60 Hz by seed, with seeded throttle, brake and steering from
  SplitMix64; never committed. One in 64 commands is invalid on purpose, as in 015.
- **The fixture engine has its own committed streams**, `simcore/vehicles/streams/fixture/`, so the
  checks are tested on `main` without a real engine.

**Rationale**: every zone is flat where a check measures against an ideal, so terrain never contaminates
the acceleration, braking and turning bands; 400 m covers acceleration to top speed (about 80 m
[estimate]) and braking (25 m ideal) with margin; the arena fits 256 parked vehicles for the server
benchmarks (256 × 9 m² against 14,400 m²).

**Alternatives considered**: reusing `test-world.mmcd` (would change its checksum and refuse every 015
stream); a smaller world (no room for the lane at top speed).

**Risks**: zone geometry may need adjusting once the first engine runs; regenerating the world changes
its checksum and re-records every vehicle stream, with a reason.

## R22. The fixture engine and its fault table (resolved here)

**Decision**:

- **`src/MmoEngine.SimCore.Vehicles.Fixture/`**, engine-neutral C# under the full analyzer with no
  exemption, behind `IVehicleEngine`: semi-implicit Euler integration with gravity; quaternion update
  with normalisation; a fixed internal substep count derived from the configured top speed and contact
  tolerance (bounded, every loop with a fixed bound); chassis-against-static by the box's corners against
  the core world (box query, then a segment sweep of each corner for continuous collision) with an
  inelastic normal impulse and Coulomb friction at μ_c; chassis-against-chassis by the core's
  oriented-box separating-axis test with an inelastic normal impulse. It never takes part in a real
  decision.
- **The committed fault table** `simcore/vehicles/fixtures/fault-table.json` lists one fault per check
  of US1 scenarios 1 to 11 and one per FR-037 status row a vehicle model can cause, each with its target
  check, status, reason code and expected measured value. Model faults are selected by the fixture's
  configuration (`fault`), and enter the fixture's identity unless marked `identityPreserving`;
  **input faults** are committed inputs (a stream with a stale expected hash, a stream from another
  identity, a license inventory, a fit-check result, a build log) for the status rows no deterministic
  model under the analyzer can cause. The faults ([data-model.md](data-model.md#fixture-engine-and-fault-table)):
  `overshoot-top-speed` (+3%, US1.1), `weak-brake` (US1.2), `under-steer` (US1.3), `sinks-on-slopes`
  (US1.4, only where the terrain normal is not vertical), `sticks-to-ramp` (US1.5), `vehicles-pass-through`
  (US1.6), `no-continuous-collision` (US1.7), `stale-expected-hash` (input, US1.8), `state-survives-reload`
  (US1.9: state kept across `Load` on one engine instance), `hidden-accumulator` (US1.10, outside the
  canonical state), `rejected-command-applied` (US1.11), `browser-perturb-step-15` (identity-preserving,
  applied only to the fixture's browser builds through the agreement manifest, US2 scenario 9), `coverage-parked` (vehicles never move
  in generated streams, `CoverageNotMet`), and `non-finite-velocity` (`StepFaulted`).
- **Each check runs in isolation**: a fresh engine instance and world per run, except the US1.9 check,
  which reloads the same instance, so a fault aimed at one check cannot reach another.
- **In CI**, the selection run includes the fixture pair `fixture` (no fault) and `fixture-perturbed`
  through the real harness (builds, runners, compare), and checks that they end Qualified and Diverged
  (`RecordDiverged` at step 15, `vehicles[0].position.x`) and that the fixture-only decision picks
  `fixture` with BitExact (US2 scenario 9).

**Rationale**: FR-035 needs known outcomes for the checks, the statuses, the divergence measurement and
the orchestration, independent of which real engine passes. A sc8 ("the server build missed a committed
hash") or sc9 failure cannot come from a deterministic model in a fresh world, so sc8 uses a committed
input, and sc9 uses state that survives a reload, which is a real bug class.

**Alternatives considered**: per-check processes (slow, and still no way to make sc8 fail from a model);
faults that touch several checks (break "exactly the targeted check").

**Risks**: the fixture is a small physics model and must itself pass every check with no fault; its
tests are written first against the fault table.

## R23. Windowed divergence and browser reset (resolved here)

**Decision**:

- **One windowed run serves US2 scenario 4 and US5.** For each stream, the browser runner restarts at
  every window start j (S₀, S₀ + W, S₀ + 2W, …, W = ⌈0.5 s × rate⌉), loading the server build's record
  state at j with its events cleared (the initial state at S₀), steps W steps (fewer at the end), and
  writes the records. Compared byte for byte with the server's records, a difference is `ResetDiverged`;
  the same records feed the error measurement. The windows tile the stream.
- **Server reset (US1 scenario 10)** restarts at every window start and steps to the stream's end, as its
  text says.
- **Errors**, computed in `double` by `DivergenceMeasurement` in tool code, per vehicle and step: chassis
  position error (Euclidean, m); orientation error, 2·acos(min(1, |q₁·q₂|)) in degrees, so q and −q are
  equal; linear velocity error (m/s). The report gives per stream, build and mode the maximum and p99
  (nearest rank) of each, with the step and vehicle of each maximum, and the bound verdict (position ≤
  0.05 m and orientation ≤ 1°; velocity not bounded).
- **Window check (US5 scenario 6)**: for every `harness/profiles/*.json` and each rate 20, 30 and 60 Hz,
  ⌈(roundTripMs + 2 × jitterMs) / 1000 × rate⌉ + 2 + (lossPercent > 0 ? 1 : 0) ≤ W. With the committed
  Poor profile (250 ms, 50 ms, 3%): 7 + 2 + 1 = 10 = W at 20 Hz.

**Rationale**: loading a record's state needs its events cleared, because a loaded state has no events
(015's rule); events are outputs, so clearing them does not change the next step. Window-only browser
restarts cost n steps per stream instead of n²/W, and every step is covered by exactly one restart;
hidden state that builds over more than W steps is caught by the server reset check and the continuous
runs.

**Alternatives considered**: browser restarts run to the end (n²/W steps: 20 restarts of a 600-step
stream is about 6,300 steps per build); a separate reset run (doubles the browser work).

**Risks**: none beyond the measurement's own.

## R24. Measurements (FR-023, FR-024)

**Decision**:

- **Server**: `benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks/` (BenchmarkDotNet 0.15.8, 015's
  exporter shape), one process per candidate (Bepu's pinned, R8), world steps with 1, 16, 64 and 256
  vehicles all driving seeded commands in the arena; the vehicle step cost is the world step's mean and
  p99 divided by the vehicle count.
- **Browser**, per candidate, mode and runtime (Node, Chromium Worker): `tools/agreement/lib/measure-vehicles.mjs`
  with 015's method (`lib/passes.mjs`): 512 warm-up steps, then 3,008 timed steps in 16-command batches,
  mean and nearest-rank p99; worlds of 1, 16 and 64 vehicles where vehicle 0 is driven by the batch and
  the others are parked on flat ground (sleeping is off, so they still step); the **deciding number** is
  the Chromium Worker mean with one vehicle, in microseconds with three decimals.
- **Startup**: 10 cold starts per mode and runtime, from `import()` of `dotnet.js` to the first completed
  vehicle step, including the engine module's fetch, integrity check and instantiation.
- **Download added**: the candidate host's download set (015's boot-manifest rule) plus its
  `wwwroot/engine/` files, minus 015's `MmoEngine.SimCore.Wasm` download set in the same mode; raw,
  Brotli and gzip from the publish's own siblings. The fixture's added bytes are recorded too, as the
  cost of the vehicle code without an engine.

**Rationale**: a client predicts only its own vehicle (spec), so one driven vehicle in a Worker is what
it pays per frame. [measured, sandbox, not deciding]: Jolt with one car, 16–20 µs per step through .NET
prebuilt in Node and 19.2 µs mean in Node wasm SIMD alone; its module and glue 240,892 B Brotli
(SIMD) or 298,816 B (scalar). Bepu in Node prebuilt: 330 µs per step on a one-box scene (persistent);
about 198 KB Brotli added. These include no core wheel model; the selection measures the whole step.

**Alternatives considered**: driving every vehicle in the browser worlds (the batch API drives one); the
16- or 64-vehicle cost as the deciding number (a client does not simulate other players' vehicles).

**Risks**: Worker timings are noisy run to run (one 761 ns crossing against a 492 ns rerun [measured]);
the 10% tie band absorbs noise only partly, and the record shows the numbers.

## R25. The decision rule, the record and the staleness check (resolved here)

**Decision**:

- **Exact decimal arithmetic.** Costs are JSON numbers with at most three decimals (µs) and byte counts
  are integers; the decision reads them as `System.Decimal` and ties x with L when x ≤ (1 + band) × L,
  inclusive, with band 0.10 from the selection configuration. The fixtures (100 / 110, 100 / 110.01,
  100 / 109 / 115) are exact in decimal.
- **Per mode**: FR-026's three branches, in order, over that mode's statuses; fixture candidates are never
  part of the real decision. Ties break on the lower cost, then fewer Brotli bytes, then the order Bepu,
  Jolt, Rapier only on exact equality of both values.
- **The record** is canonical JSON (fixed member order, LF, invariant culture), carrying no clock time: the
  commit and CI run it records are the report's. The same report gives the same bytes
  ([contracts/decision-record.md](contracts/decision-record.md)).
- **Fingerprint inputs** (FR-028): each engine's identity fingerprint, `SimCoreVersion.VehicleRulesRevision`,
  the vehicle configuration (XxHash64 of its canonical config block), the selection configuration
  (XxHash64 of its canonical JSON), and each committed vehicle stream (its XxHash64 trailer, by path).
  The staleness check recomputes them and names each one that changed; it prints the provisional flag
  when set.

**Rationale**: floating-point comparison of decimal fixtures is not exact (110.0 vs 1.1 × 100.0 in
binary64 is 110.00000000000001); `decimal` is exact for these values.

**Alternatives considered**: integer nanoseconds (the spec's fixtures are µs with two decimals; three
decimals keep nanosecond precision).

**Risks**: none.

## R26. CI shape (resolved from N8)

**Decision** ([contracts/ci-jobs.md](contracts/ci-jobs.md)): 015's `agreement` and `measure` jobs stay
unchanged. `simcore.yml` gains `workflow_dispatch` and six jobs:

- **`vehicle-changes`** (2 min): computes, from `git diff` against the pull request's base, whether the
  change touches the selection paths (vehicle code, engine builds, vehicle streams, selection
  configuration) and the gate paths (those plus the core and the record).
- **`vehicle-engines`** (45 min): builds and tests Jolt's native library and browser module (and the
  evidence-only threaded build), each build bounded by `timeout 15m`, cached by identity, with a build
  outcome per build (`Built`, `BuildFailed` or `BuildTimedOut`, the failing step and the last 50 lines);
  runs the shim's own tests with coverage and the native-dependent .NET tests; uploads the
  `vehicle-engines` artifact.
- **`vehicle-agreement-plan`** (2 min): the staleness check (FR-028) and the matrix from the committed
  record (`simcore vehicles decision gate-plan`); with no record, or None in every mode, it writes the
  outcome to the job summary and the gate jobs are skipped.
- **`vehicle-agreement`** (a matrix, `fail-fast: false`, **15 minutes per leg**): one leg per build mode
  the committed record names a winner for, running that mode's required builds of the winner's streams,
  then compare (BitExact) or the NonExact gate. The `interp-prebuilt` leg installs no workload.
- **`vehicle-winner-checks`** (15 min): the winner's US1 checks and lock test on its server build, a real
  test on `main` (US1 scenario 14).
- **`vehicle-selection`** (120 min): every candidate and the fixture pair in one job on one runner class
  (FR-025), on pull requests that touch the selection paths and on `workflow_dispatch`: prebuilt
  publishes before the workload, then relinked, then AOT; `simcore vehicles select run`; validation;
  the fixture self-check; the job summary; the `vehicle-selection` artifact.

**Rationale**: the matrix gives each mode its own 15-minute bound (FR-029) and parallel legs; the
prebuilt leg tests exactly the prebuilt runtime; the job split keeps 015's jobs and bounds unchanged
(US2 scenario 6, SC-002). Path filters are workflow-level in GitHub Actions, so the job-level selection
is computed by `vehicle-changes`.

**Alternatives considered**: installing the workload in `agreement` (forbidden by FR-029 and FR-032);
one sequential vehicle-agreement job (needs 015's deadline pattern for per-mode bounds); folding
vehicles into `measure` (changes that job).

**Risks**: each matrix leg is its own check run (auto-merge waits on the `SimCore` workflow, which is
fine [estimate]); the 120-minute selection delays vehicle pull requests.

## R27. Parallel-phase evidence (FR-027)

**Decision**: An evidence-only run per engine whose pinned release can step with threads and its
determinism setting on, never a gameplay configuration and never part of a status: Bepu with
`Simulation.Deterministic = true` and a `ThreadDispatcher` of 1 and 4 threads; Jolt with an evidence-only
native build (`JobSystemThreadPool`, BLUE off) at 1 and 4 threads. The record states, for each engine,
whether it documents thread-count independence (with the citation), whether the run was made, and
whether the committed streams gave the same per-step hashes. Rapier's run is not made (NotTested), and
the record says so.

**Rationale**: [measured] Bepu's car scene gave the same 300-step chain with no dispatcher and with 1, 2
and 4 deterministic threads (`0019b902214e1980`), but Bepu documents determinism with threads, not
thread-count independence, and its threaded broad-phase refine engages only at ≥ 256 leaves [source].
Jolt's determinism CI validates one hash across runners with different core counts [read], which
implies independence; a `JobSystemThreadPool` run brings the pthread locks back, so it must be a
separate build. Rapier documents bitwise-identical results for any thread-pool size with
`enhanced-determinism` and `parallel` [documentation].

**Alternatives considered**: no evidence run (the record would lose the option for gate 9 unnoticed).

**Risks**: small scenes may not exercise the threaded paths; the record says which scenes ran.

## R28. Coverage of the new code, and where native tests run (resolved here)

**Decision**:

- **C# adapters for native engines are covered in the .NET workflow without native libraries**: their
  marshalling (body records, load payloads, result codes and fault mapping) sits behind an internal
  engine-call interface that tests drive with a fake; the `[LibraryImport]` and `[JSImport]`
  declarations are bodiless partial methods with no coverable lines; the binding factory's choice is
  one tested line.
- **Native-dependent .NET tests** (real Jolt through P/Invoke, the lock counter's self-test, Jolt's
  calibration of resting offsets) live in `tests/MmoEngine.SimCore.Vehicles.Native.Tests/`, outside
  `MmoEngine.sln`, and run in the `vehicle-engines` job with `MMO_VEHICLE_NATIVE_DIR`; that job also runs
  `dotnet format` on the project.
- **The Jolt shim's C++** is tested by `ctest` with clang's source-based coverage
  (`-fprofile-instr-generate -fcoverage-mapping`, `llvm-profdata`, `llvm-cov export -format=lcov`), and
  the changed-file gate reads that LCOV beside the Cobertura reports (an additive change to
  `tools/coverage_gate.py`, run in the `vehicle-engines` job for `src/MmoEngine.SimCore.Vehicles.Jolt/native/**`).
- The Rust shim gets `cargo llvm-cov` when it is written (R12's follow-up).

**Rationale**: the constitution's 95% changed-file rule covers the shims (spec Quality gates), and the
.NET workflow has no C++ or Rust toolchain; keeping native-dependent tests out of the solution keeps
`dotnet test MmoEngine.sln` green with no skipped tests.

**Alternatives considered**: skipping native tests when the library is missing (skipped tests are
review findings); adding C++ and Rust toolchains to `dotnet.yml` (a guarded workflow change for every
.NET pull request).

**Risks**: two coverage reports for one pull request; the gate merges them.

## Design sources

Design references only. No code was copied, and no GPL, LGPL or AGPL code was read; Jolt's
`Assets/Racetracks` data (LGPL-3.0) was not opened.

**Engines**

- BepuPhysics2 at tag `2.5.0-beta.33`: `Documentation/QuestionsAndAnswers.md` (determinism),
  `Documentation/roadmap.md` (item 14, cross-platform determinism), `Documentation/PackagingAndVersioning.md`,
  and `Demos/Demos/Cars/*.cs` (read for the vehicle-model comparison only).
- Jolt Physics v5.7.0: `Docs/Architecture.md` (Deterministic Simulation, Rolling Back a Simulation),
  `Docs/ReleaseNotes.md`, `.github/workflows/determinism_check.yml`, `Jolt/Core/Core.h`, `Mutex.h`,
  `Memory.h`, `Jolt/Physics/PhysicsSystem.cpp`, `Jolt/Physics/Collision/Shape/HeightFieldShape.cpp`
  (the user-shape pattern), `Jolt/Physics/Vehicle/*`.
- Rapier v0.36.1: `website/docs/user_guides/templates/determinism.mdx` and `serialization.mdx`,
  `CHANGELOG.md`, `bindings/c/README.md` and `include/rapier.h`, `src/control/ray_cast_vehicle_controller.rs`,
  `src/pipeline/physics_world.rs`; parry3d 0.31.1's `Cargo.toml`, `src/utils/hashmap.rs`,
  `src/math/vector_ext.rs` and `src/partitioning/bvh/bvh_optimize.rs`; foldhash 0.2.0's `LICENSE`.
- joltc (amerkoleci), JoltC (SecondHalfGames) and JoltPhysicsSharp, read for the binding comparison.

**Vehicle dynamics and collision**

- The kinematic bicycle model and the friction circle (textbook vehicle dynamics).
- Bullet's `btRaycastVehicle`, through Rapier's documented port, for the comparison only.
- Ericson, *Real-Time Collision Detection* (2005): oriented-box separating-axis tests.

**.NET, WebAssembly and the browser**

- Microsoft's docs sources (dotnet/AspNetCore.Docs): `blazor/webassembly-native-dependencies.md` and
  `client-side/dotnet-interop/index.md`; the `JSType.MemoryView` API page.
- The .NET 10 WebAssembly SDK targets (`WasmApp.Common.targets`) and the shipped `dotnet.js`.
- dotnet/runtime PR 99784 (AVX-512 knob removal discussion).
- Rust platform support for `wasm32-unknown-emscripten` and the `emscripten-wasm-eh` unstable-book page.

**Licensing**

- The SPDX specification, Annex D (expression syntax), and npm `spdx-license-ids` 3.0.24 and
  `spdx-exceptions` 2.5.0 for identifier checks.
- Emscripten 3.1.56's `tools/system_libs.py` and `LICENSE`; musl's `COPYRIGHT`; the GCC Runtime Library
  Exception.

**New dependencies:**

- `BepuPhysics` and `BepuUtilities` 2.5.0-beta.33 (Apache-2.0), in `Directory.Packages.props`, referenced
  only by the Bepu adapter.
- Jolt Physics v5.7.0 (MIT), built in CI from pinned sources; not vendored.
- `Mono.Cecil` 0.11.6 (MIT), referenced only by `MmoEngine.SimCore.Vehicles.Selection` (R16).
- The Emscripten 3.1.56 packs (`Microsoft.NET.Runtime.Emscripten.3.1.56.*` 10.0.15, MIT, from nuget.org),
  a toolchain, never shipped; the libraries they link into Jolt's module are scanned (R18).
- `rapier3d` 0.36.1 (Apache-2.0) is declared in a manifest only and never built while foldhash (Zlib)
  blocks it (R12).

## Prototype locations

The six research threads' prototypes were throwaway work in the session's scratch directory, outside
the repository, and were not committed: the Bepu scenes, hash runners, Node and Chromium runners and AOT
logs; the Jolt driver, shim sketch, lock and malloc counters and the BLUE header; the Rapier probe, its
pinned-libm, no-serde and parallel variants, and the exhaustive glibc-versus-libm comparison; the .NET
interop benchmarks and engine-glue runners; the vehicle-model experiments for all three engines; and the
license collectors, SPDX evaluator, fixture packages and link traces. Everything the implementation
needs from them is recorded here, in the data model and in the contracts.
