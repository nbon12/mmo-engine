# Feature Specification: Regionizer Simulator

**Feature Branch**: `013-regionizer-simulator`

**Created**: 2026-11-03

**Status**: Draft

**Input**: User description: "Pre-Phase 1, sub-spec 1: Regionizer simulator (nbon12/mmo-engine#13). An
offline, deterministic regionizer simulator that answers how large claims may be, and how far apart
fights must sit, before separate fights fuse into one region. It drives the real regionizer core
library, which sub-spec 10 later runs inside the continent server unchanged, with synthetic movement,
and records what the regions do."

## Clarifications

### Session 2026-11-03

Answered by the agent with the recommended option (unattended run, Principle XIII).

- Q: When does a merge count as a fusion event? → A: Each player in a fight carries that fight's tag
  from the scenario; travellers, aircraft and capture points carry none. A merge is a fusion when each
  side holds a fight tag the other side does not.
- Q: What does "fires exactly its delay after it was set" mean when a region drops to 20 Hz? → A: In
  tick-counter mode the delay counts the owning region's ticks, carried across merges (offset) and
  splits (copied counter). In absolute mode the timer fires at the first tick of its owning region at
  or after the deadline on the continent time base.
- Q: What ranges does the cross-region pairs metric count? → A: Two configurable placeholder ranges,
  300 m for deferred and 1,000 m for read-only interactions. A pair is counted once, under the
  smallest range it falls within: deferred up to 300 m, read-only from 300 m to 1,000 m. (Corrected by
  analysis: counting under the larger range would always leave the deferred count at zero.)
- Q: What tolerances do the analytic checks use? → A: The claim-radius floor matches exactly; the
  merge distance is checked as exact bounds with zero violations allowed; the lane-bridging estimate
  matches within 10 percentage points at each traffic step, with uniformly random travellers and at
  least 200 trials per step.
- Q: Must the per-tick hashes match across machines? → A: They must match on the same machine and
  build. The design avoids platform-dependent math so hashes can also match across machines, but
  proving that with golden runs belongs to sub-spec 2.

## User Scenarios & Testing *(mandatory)*

The users of this feature are the engineers and agents who tune the regionizer and later run it
inside the continent server, and the level designers who check maps for portal leaks. The simulator
models where entities are and what they claim, not what they do.

### User Story 1 - The regionizer core keeps its invariants through merges and splits (Priority: P1)

An engineer drives the regionizer core library tick by tick: entities move, claims change, regions
merge when claims touch and split when they hold separate islands. Every rule the constitution sets
for sections and regions holds at every tick boundary, and the library carries no simulator-specific
code, so the continent server can reuse it unchanged.

**Why this priority**: Every other story measures this library. If it is wrong, every number the
simulator produces is wrong.

**Independent Test**: Simulation tests construct small section layouts with a few entities, step
ticks on a fixed time base, and assert region membership, state and timers after each tick end, with
the invariant checker running after every tick.

**Acceptance Scenarios**:

1. **Given** an infantry entity and a ground vehicle in separate regions whose claims are three
   sections apart, **When** the ground vehicle moves until their claims share a section and the tick
   ends, **Then** both entities belong to one region, and the merge is recorded with the ground
   vehicle's class and entity ID as its cause.
2. **Given** a region that is ticking, **When** a new entity's claim lands on sections beside it
   during that tick, **Then** the ticking region's section set does not change during the tick, the
   new sections form a transient region recorded on the ticking region, and the transient region is
   absorbed when the ticking region's tick ends.
3. **Given** a region holding two islands of entities that meets the split damping thresholds (at
   least *N*min sections, at least *p*dead of them dead, at least *T*min old), **When** its tick ends,
   **Then** it splits into two child regions, each entity belongs to the child that owns its section,
   and each child carries the parent's timers for its entities.
4. **Given** a region holding two islands that is younger than *T*min, **When** its tick ends, **Then**
   it does not split.
5. **Given** a closed portal between a surface section and an underground section with an entity
   beside it on each side, **When** ticks run, **Then** neither entity's claim includes a section on
   the far side and the two stay in separate regions; **When** the portal opens, **Then** the claims
   reach through it and the two regions merge at the next tick end.
6. **Given** a region in the ticking state, **When** something tries to move it straight to dead,
   **Then** the transition is rejected and the region stays ticking.
7. **Given** a timer with a delay of 90 ticks set on a region that merges into a region with a
   different tick counter 30 ticks later, **When** ticks run, **Then** the timer fires exactly once:
   - in tick-counter mode, on the owner's 90th tick after it was set;
   - in absolute mode (delay 90 × baseStepHz / tickHz base steps), at the owner's first tick at or
     after the deadline.
8. **Given** section-anchored claims, **When** an entity moves within one section, **Then** its claimed
   sections do not change; **Given** position-anchored claims, **When** the same entity moves far
   enough that its radius covers a new section, **Then** its claimed sections change on that tick.
9. **Given** two claims in adjacent sections that share no section, **When** the tick ends with a
   merge gap of 1, **Then** their regions merge; **When** the merge gap is 0, **Then** they stay
   separate.

---

### User Story 2 - A seeded scenario run is reproducible and leaves a complete record (Priority: P1)

An engineer runs the Baseline scenario in lockstep mode with a seed and configuration. The run
writes a manifest, per-tick metrics and a run summary to a local output folder. Running it again
with the same inputs reproduces it tick for tick.

**Why this priority**: The decision memo is only trustworthy if anyone can re-run any number in it.

**Independent Test**: Run Baseline twice in separate processes with the same seed and compare the
per-tick state hash sequences, then inspect the output folder's manifest, metrics and summary.

**Acceptance Scenarios**:

1. **Given** the Baseline scenario, a seed and a fixed configuration, **When** it runs twice in
   separate processes, **Then** the per-tick state hashes are identical for every tick.
2. **Given** two different seeds, **When** Baseline runs with each, **Then** their per-tick state hash
   sequences differ.
3. **Given** a completed run, **When** its output folder is read, **Then** it holds a manifest (source
   revision, configuration hash, scenario, seed, parameters, runtime and hardware-intrinsic
   settings), one metrics row per tick, and a summary with p50, p95, p99 and max for each metric.
4. **Given** a configuration whose section size is not a power of two, **When** the simulator starts,
   **Then** it refuses to run, exits with a non-zero code and names the invalid field.

---

### User Story 3 - Interleaved scheduling exercises the hard cases, and monitors catch any violation (Priority: P2)

An engineer runs scenarios in interleaved mode, where region ticks start and end in a seeded random
order and regions randomly drop to 20 Hz, so merges against ticking regions and diverging tick
counters really happen. Invariant monitors run after every tick and stop the run at the first
violation.

**Why this priority**: Lockstep ticks every region together at one rate, so tick counters never
diverge and no region runs at 20 Hz. Interleaved mode produces merges between regions with different
tick counters, which exercise timer offsets, and the monitors prove the core under that churn.

**Independent Test**: Run Migration in interleaved mode and confirm a clean, reproducible run; inject
a corrupted state through the test hook and confirm the run fails at that tick with the rule and
seed; run a shortened Soak and confirm every timer fired exactly once.

**Acceptance Scenarios**:

1. **Given** the Migration scenario in interleaved mode with a seed, **When** it runs, **Then**:
   - at least one region ticks at 20 Hz;
   - at least one merge joins two regions with different tick counters;
   - the run ends with zero invariant violations;
   - a rerun with the same seed gives identical per-tick hashes.
2. **Given** a run in which a section is made to belong to two regions at tick *t*, **When** the
   monitors run at tick *t*, **Then** the run fails at tick *t* and reports rule 1, the tick number
   and the seed.
3. **Given** the Soak scenario (migration, stacked areas and air traffic) with a shortened duration
   in interleaved mode, **When** it runs, **Then** it ends with zero violations, and every synthetic
   timer fired exactly once, when FR-012 says for the active timer mode.

---

### User Story 4 - Every scenario and sweep runs from configuration (Priority: P2)

An engineer runs any of the nine scenarios, or a sweep that steps one parameter around a default
point with ten seeds per cell, spread across cores. Each run in a sweep is independent and
reproducible from its own manifest.

**Why this priority**: The values the simulator sets are read off scenario curves; without the full
scenario set and sweeps there is nothing to read.

**Independent Test**: Run each scenario for a short duration; run a small sweep twice with different
degrees of parallelism and compare results; replay a recorded trace and compare its hashes.

**Acceptance Scenarios**:

1. **Given** each of Baseline, Spacing, Lane traffic, Air traffic, Vehicle columns, Stacked areas,
   Migration, One base and Soak, **When** it runs with its defaults for a short duration, **Then** it
   ends with zero invariant violations and writes its manifest, metrics and summary.
2. **Given** a sweep of infantry claim radius over 8 to 40 m in 4 m steps with 10 seeds per cell,
   **When** the sweep driver runs it, **Then** it produces exactly one result per cell and seed, and
   each result's manifest reproduces that result's hash sequence.
3. **Given** the same sweep, **When** it runs once on one worker and once on several, **Then** every
   cell and seed produces the same hash sequence and the same summary, apart from wall-clock
   measurements such as bookkeeping cost.
4. **Given** a position trace recorded from a run, **When** the simulator replays it, **Then** the
   replayed run's per-tick state hashes match the recorded run's.
5. **Given** the Air traffic scenario with and without the air layer, **When** each runs, **Then**
   the run with the air layer has aircraft above the altitude threshold in their own section layer,
   and both runs record more than zero merges caused by aircraft.
6. **Given** each scenario, **When** it runs twice with the same seed for a short duration, **Then**
   the two runs' per-tick state hashes are identical (SC-001).

---

### User Story 5 - The portal validator finds every leak in a map (Priority: P2)

A level designer runs the portal validator, a standalone tool, on a map. It reports every navigable
opening across an area or layer boundary that is not a declared portal, every dead portal, every
portal without a closed-state policy, and any sealed area that a claim could still reach with all
portals closed.

**Why this priority**: Claims stop at closed portals only if the map declares every opening.
The constitution requires every map's portal graph to be validated in the simulator before it ships.

**Independent Test**: Run the validator on test maps with known seeded leaks and on a clean map, and
compare the reported findings with the seeded list.

**Acceptance Scenarios**:

1. **Given** a test map with seeded leaks, including an undeclared walkway between areas and a window
   opening that is not declared as a portal, **When** the validator runs, **Then** it reports exactly
   the seeded leaks with their locations and exits with a non-zero code.
2. **Given** a clean test map, **When** the validator runs, **Then** it reports no findings and exits
   with code 0.
3. **Given** a portal whose far side is not a navigable section, **When** the validator runs, **Then**
   it reports that portal as dead.
4. **Given** a portal that declares no closed-state policy, **When** the validator runs, **Then** it
   reports the missing policy.
5. **Given** a sealed area that a claim could still reach through navigable adjacency with all
   portals closed, **When** the validator runs, **Then** it reports a sealing failure naming the area.

---

### User Story 6 - Analysis checks the simulator against arithmetic and drafts the decision memo (Priority: P3)

An engineer runs the analysis over a set of results. It checks the simulator against three analytic
results (the claim-radius floor, the merge distance and the lane-bridging estimate), then writes a
decision memo that recommends a value for each row of the issue's "Values it sets" table, with the
curve, margin, seeds and runs behind it.

**Why this priority**: The memo is the feature's final output, but it depends on every earlier story.

**Independent Test**: Compute the analytic checks for the issue's worked examples; run the merge
distance check against a two-entity stepping run; generate a memo from a small result set and check
that every value has a row with its evidence.

**Acceptance Scenarios**:

1. **Given** an aircraft interaction range and two aircraft closing at 240 m/s at 30 Hz, **When** the
   claim-radius floor is computed, **Then** it is the interaction range plus 8 m. **When** an aircraft
   with a claim radius at the floor closes on an entity that claims only its own section, **Then** at
   every tick boundary where the two are within one tick of interaction range they share a region.
   **When** the radius is below the floor by more than the section slack, **Then** at least one such
   boundary finds them in different regions.
2. **Given** two entities stepped apart one section at a time in each anchoring mode, **When** the
   merge-distance check runs, **Then** every pair within the sum of their radii shares a region, and
   no pair farther apart than the merge distance d_max shares a region because of their claims.
3. **Given** a 600 m lane and a merge distance of 30 m, **When** the lane-bridging estimate is computed
   for 30, 60 and 120 travellers, **Then** it gives about 0.04%, 4% and 74%.
4. **Given** a set of sweep results, **When** the memo generator runs, **Then** the memo has a row for
   each of claim radius per class, section size, claim anchoring, minimum base spacing, lane bridging
   limit, split damping, air layer and target fight size, each with a recommended value labeled as a
   measurement, the curve it was read from, its margin, its seeds and its run IDs.

---

### User Story 7 - Bookkeeping cost is measured against its budget (Priority: P3)

An engineer reads, for Baseline and One base at 5,000 entities, how long the regionizer's
bookkeeping takes per tick, with the serialized part measured separately, against the proposed
budget of 1 ms p99 per tick on one thread.

**Why this priority**: Section size is chosen as the smallest size whose bookkeeping fits the budget,
so the cost has to be measured before section size can be recommended.

**Independent Test**: Run One base and Baseline and read the summary's bookkeeping rows; run the
steady-state allocation test.

**Acceptance Scenarios**:

1. **Given** the One base and Baseline scenarios at 5,000 entities, **When** each runs, **Then** its
   summary records bookkeeping cost per tick at p50, p95, p99 and max, records the serialized part
   separately, and states whether p99 is within the configured budget.
2. **Given** entities moving after warm-up with no region created, merged or split, **When** ticks
   run, **Then** the regionizer's bookkeeping allocates no managed memory.

---

### Edge Cases

- An entity exactly on a section edge or corner belongs to exactly one section, by a fixed rounding
  rule, and its claim is computed from that section.
- An entity at the map edge claims only sections inside the map.
- A claim never wraps around the map or skips a section; a claim radius of zero claims only the
  entity's own section.
- A portal opens or closes while any region is ticking: the change takes effect for claims when the
  last ticking region's tick ends, never mid-tick.
- A ticking region owes merges to two regions that are not ticking: both are absorbed at its tick
  end, in ascending region ID order.
- Three regions chain-merge in one tick end: the result is one region, whatever the iteration order.
- A region drops to 20 Hz while a merge into it is pending: the merge still happens at its tick end,
  and timers keep their deadlines.
- Every entity leaves a region: the region's sections go dead, and at its next tick end it drops them
  and dies, whatever the split damping thresholds.
- A teleport moves an entity into another region's sections: it is removed from its old region and
  added on the destination region's next tick (two-step teleport), and never belongs to two regions.
- A split would leave an island holding only dead sections: no child region is created for it.
- A capture point that becomes unoccupied stops claiming its zone at once. Its sections go dead and
  are dropped at the next split check.
- A scenario asks for more entities than the configured population limit: the simulator refuses to
  start and names the field.
- A run is interrupted: its output folder is marked incomplete, and a rerun into the same folder
  starts clean rather than appending.
- An output folder that is non-empty but holds neither `manifest.json` nor `_incomplete` is not a
  run folder: the simulator refuses it with exit code 2 and deletes nothing.

## Requirements *(mandatory)*

### Functional Requirements

**Regionizer core library**

- **FR-001**: The library MUST divide the map into square sections whose side is a power of two in
  metres, keyed by layer and grid position. Layer 0 is the open world, and each sealed designed area
  is its own layer. A move MUST change an entity's layer only when it crosses an open portal from its
  current section, or when the move is to or from a layer marked as allowing direct entry (the
  optional air layer). Any other layer change is rejected. A two-step teleport may land on any layer.
- **FR-002**: Each section MUST keep a count of the claims on it. A section its region owns while it
  has zero claims is dead.
- **FR-003**: Each entity MUST claim the sections within its class's claim radius, reached from its own
  section without crossing a closed portal. An occupied capture point MUST claim its zone. Heavy
  projectiles MUST claim nothing. A heavy projectile MUST:
  - start in a section its firing region owns;
  - keep that region as owner wherever it flies;
  - change owner only through the location queue (two-step teleport) to a region that owns its
    destination (Principles II and III).
- **FR-004**: The library MUST support section-anchored claims, computed from the entity's section and
  changing only when it crosses a section edge, and position-anchored claims, computed from its exact
  position. The mode MUST come from configuration.
- **FR-005**: A claim landing on a section owned by another region MUST force those regions to merge.
  With a merge gap of 1, a claim landing on a section adjacent (eight-neighbour, same area, or across
  an open portal) to a section owned by another region MUST also force a merge. With a gap of 0 it
  MUST NOT.
- **FR-006**: A ticking region MUST NOT grow. Sections it newly needs MUST go into a transient region
  recorded on it, which it absorbs when its tick ends.
- **FR-007**: At each region's tick end the library MUST, in this order, process its pending merges;
  mark it transient if it still owes a merge elsewhere; otherwise, if the split damping thresholds
  hold, drop its dead sections and try a split. Dead sections stay owned by their region until then.
- **FR-008**: A region MUST split only if it owns at least *N*min sections, at least *p*dead of them
  are dead, and it is at least *T*min old. All three thresholds MUST come from configuration. The child
  that keeps the parent's ID keeps its age, and other children start at age zero. A region with no
  entities left (heavy projectiles count as entities) drops its sections and dies at its tick end,
  whatever these thresholds.
- **FR-009**: A split MUST create one child region per island: a group of entities linked by shared
  claimed sections, judged with the same merge gap as merges. No child region may be created for an
  island that holds only dead sections.
- **FR-010**: A merge MUST concatenate the regions' entity lists and timers. A split MUST hand each
  entity, and its timers, to the child that owns its section.
- **FR-011**: Every region MUST be transient, ready, ticking or dead, and only these moves are legal:
  transient to ready; ready to ticking or transient; ticking to ready or transient; any non-ticking
  state to dead. An illegal move MUST be rejected and leave the state unchanged.
- **FR-012**: The library MUST support two timer modes, chosen in configuration: per-region tick
  counters, where a merge offsets the absorbed region's timers and a split copies the parent's
  counter (the default, as the constitution requires), and absolute deadlines on the continent time
  base. In both modes a timer MUST fire exactly once: in tick-counter mode after exactly its delay in
  ticks of its owning region, counted across merges and splits; in absolute mode at the first tick of
  its owning region at or after its deadline.
- **FR-013**: Opening a portal MUST let claims reach through it, which may cause a merge. Closing one
  MUST stop claims crossing it, which may allow a split. A toggle made while no region is ticking
  takes effect at once. A toggle made while any region is ticking MUST be deferred until the last
  ticking region's tick ends, so no claim changes mid-tick because of it.
- **FR-014**: The library MUST expose an invariant checker for tick boundaries: points where no region
  is ticking, which the simulator reaches at the end of every base step. It verifies that:
  - every live section belongs to exactly one region;
  - every section in an entity's claim belongs to its region (at a boundary no merge is still
    pending, so the issue's "or a region pending a merge with it" clause reduces to this, and the
    checker also reports any pending merge);
  - no ticking region's section set changed during its tick (recorded at each tick end);
  - every region is in a legal state and moved only by legal transitions;
  - every entity not in transit has one owner and its section belongs to that owner, while an entity
    in transit (two-step teleport) has no owner. A heavy projectile is exempt only from "its section
    belongs to that owner".
  - no claim crosses an area boundary except through an open portal.
- **FR-015**: The library MUST NOT depend on the simulator, so the continent server can reuse it
  unchanged.

**Simulator**

- **FR-016**: The simulator MUST use its own seeded random number generator, split into named streams
  (movement, scheduling, timers), so that a runtime upgrade cannot change a sequence and a new draw in
  one stream cannot shift another.
- **FR-017**: The simulator MUST iterate entities and regions in ID order, never over an unordered
  collection.
- **FR-018**: The simulator MUST offer lockstep scheduling, where every region ticks together, and
  interleaved scheduling, where region ticks start and end in a seeded random order and regions
  randomly drop to the degraded tick rate. The base step rate, the normal tick rate and the degraded
  tick rate are configuration, defaulting to 60, 30 and 20 Hz. The base step rate must be a multiple
  of both tick rates.
- **FR-019**: The simulator MUST compute a 64-bit state hash every tick over a canonical serialization
  of section owners, region membership, each entity's section and timers. The same scenario, configuration and seed MUST
  give the same hash sequence on the same machine and build. The simulated path MUST avoid
  platform-dependent math, so that hashes can also match across machines.
- **FR-020**: The simulator MUST run the invariant checker at the end of every base step and fail the
  run at the first violation, reporting the rule, tick, seed and details. In every build it MUST also
  check that every synthetic timer fires exactly once, when FR-012 says for the active timer mode. A
  timer fault fails the run like a violation.
- **FR-021**: The simulator MUST move entities with synthetic models: infantry clustered around a
  fight's objectives in a random walk, some redeploying by two-step teleport; ground vehicles along
  lanes, loitering at fight edges; aircraft point to point with strafing passes; travellers along
  lanes; capture points static. Each class's speed ceiling MUST come from configuration, defaulting
  to 7, 30 and 120 m/s for infantry, ground vehicles and aircraft.
- **FR-022**: The simulator MUST be able to replay a recorded position trace in place of synthetic
  movement.
- **FR-023**: The simulator MUST provide the Baseline, Spacing, Lane traffic, Air traffic (with and
  without an air layer), Vehicle columns, Stacked areas, Migration, One base and Soak scenarios, with
  the population, map size and lane layout as parameters. The default map is 8 × 8 km with three
  lanes.
- **FR-024**: Every run MUST write to its output folder a manifest, a metrics row per tick (or every
  configured number of ticks, default 1), the events (merges, splits, fusions, violations) and a
  summary with p50, p95, p99 and max per metric over the recorded rows. It never deletes a folder that
  isn't a run folder.
  The metrics are region count, largest region size and share, fusion events with the causing class,
  merge rate by causing class, split rate, split latency, section churn, region lifetime, cross-region
  pairs, fused fight pairs, bookkeeping cost and oversized fusions:
  - A merge counts as a merge (and toward its causing class) only when both sides hold at least one
    entity. A merge that absorbs an entity-less region, such as a transient region collecting a
    ticking region's growth, is an *absorption*. Absorptions are reported separately and excluded
    from merge rates, region lifetimes and section churn.
  - A merge is a fusion when each side holds a fight tag the other does not; only players in a fight
    carry a fight tag.
  - Fused fight pairs counts, per tick, the pairs of distinct fight tags that share a region.
  - Cross-region pairs count entity pairs in different regions within the configured deferred range
    (default 300 m) or read-only range (default 1,000 m). Each pair is counted once, under the
    smallest range it falls within.
- **FR-025**: The sweep driver MUST run one-parameter-at-a-time sweeps around a default point and grids
  over several parameters, with a configurable number of seeds per cell, spread across the machine's
  cores. Each run's result MUST be the same whatever the degree of parallelism.
- **FR-026**: Every configuration value, tuning values included, MUST be validated before a run starts.
  An invalid value MUST stop the simulator with a non-zero exit code and an error naming the field.
  Tuning values MUST come from configuration, never from code constants.
- **FR-027**: The simulator MUST measure the regionizer's bookkeeping cost per tick, and the serialized
  part of it separately, and the summary MUST compare p99 with the configured budget.
- **FR-028**: Once warmed up, the regionizer's bookkeeping MUST allocate no managed memory on ticks
  where no region is created, merged or split.

**Portal validator**

- **FR-029**: The portal validator MUST run as a standalone tool on a map file that gives each layer a
  section grid with area tags, the portals as edges between sections, the navigable adjacency between
  sections, and tagged interaction openings such as windows.
- **FR-030**: The validator MUST report every navigable adjacency or interaction opening across an area
  or layer boundary that is not a declared portal, every portal that does not join two navigable
  sections, every portal without a closed-state policy (entity transfers, door and objective events,
  deferred projectiles), and every sealed area a claim could reach with all portals closed. It MUST
  exit with a non-zero code when it reports any finding.

**Analysis**

- **FR-031**: The analysis MUST compute the claim-radius floor, the merge distance for both anchoring
  modes and the lane-bridging estimate, compare them with simulator results, and flag any
  disagreement outside tolerance:
  - **Floor**: with a radius at the floor, there are zero boundaries where an entity within one tick
    of interaction range is in another region. With a radius below it by more than the section slack,
    there is at least one.
  - **Merge distance**: the bounds allow zero violations.
  - **Lane bridging**: the estimate at d = 30 m matches within 10 percentage points at 30, 60 and 120
    travellers. The simulator is set up so that merges happen between 30 and 31.4 m:
    - a 600 m lane with one 15 m-radius entity at each end;
    - travellers placed uniformly at random, each with a 15 m radius;
    - position anchoring, merge gap 0 and 1 m sections;
    - at least 200 trials per step.
- **FR-032**: The memo generator MUST write a decision memo from a set of results, with a row for each
  value the simulator sets: its recommended value labeled as a measurement, the curve it was read from,
  the margin, the seeds and the run IDs. Values that settle an item on the constitution's Open
  decisions list MUST be written as recommendations recorded under Principle VI.

### Key Entities *(include if feature involves data)*

- **Section**: a square map tile keyed by layer and grid position, with an area tag, a claim count and
  an owning region.
- **Region**: a set of sections, the entities in them and their timers, with a state (transient, ready,
  ticking, dead), an age, a tick counter and the merges it owes.
- **Entity**: something with a stable ID, a class (infantry, ground vehicle, aircraft, traveller,
  capture point, heavy projectile), a position, a layer, the sections it claims and, for players in a
  fight, that fight's tag.
- **Portal**: a declared edge between two sections, open or closed, with a policy for what crosses it
  while closed.
- **Timer**: a synthetic gameplay timer with a delay and an owner, expressed as a tick-counter deadline
  or an absolute deadline.
- **Scenario**: a named setup of map, fights, lanes, population and movement that a run executes.
- **Run**: one execution, with its manifest, metrics, events, summary and state hash sequence.
- **Sweep**: a set of runs over parameter cells and seeds.
- **Map file**: the layered section grid, area tags, portals, navigable adjacency and openings that the
  portal validator reads.
- **Decision memo**: the recommended values with their evidence.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: An offline tool and the regionizer core library that the continent
  server will run (Principle I). No browser client, gateway, persistence tier, orchestration or web
  surface.
- **State ownership**: Each region owns its sections, entities and timers; region state changes only
  through the regionizer's merges, splits and tick-end processing (Principle II). The simulator's
  synthetic movement stands in for gameplay and writes positions only for entities it owns.
- **Interaction class**: Claims model instant interactions only (Principle III). Deferred and
  read-only interactions do not claim; the cross-region pairs metric counts entity pairs within
  deferred or read-only range that sit in different regions, as a proxy for message volume.
- **Authority and disclosure**: N/A: offline tool with no clients.
- **Persistence and write policy**: N/A: nothing is persisted to a database. Results are local files;
  loading them into the analytics store belongs to sub-spec 2.
- **Contracts**: The output folder layout and file formats (manifest, metrics, events, summary), the
  map file format and the trace format are documented contracts that sub-spec 2's loaders read.
  Changing them is a breaking change with a version field.
- **Performance and evidence**: Bookkeeping cost is measured per tick against a proposed budget of
  1 ms p99 per tick at 5,000 entities on one thread (an estimate until measured). The simulator sets
  section size, claim radius per class, minimum base spacing, split thresholds, minimum region
  lifetime and the target fight size reading (Principle VI). Every number in the memo is labeled
  estimate or measurement.
- **Security and abuse controls**: N/A: offline tool with no network surface or untrusted input beyond
  local configuration and map files, which are validated.
- **Observability**: Structured logs from the simulator and validator. No Sentry, metrics export or
  health reporting, since it is not a service; the per-tick metrics files are its measurement output.
- **Licensing and design sources**: New dependencies must be MIT or Apache-2.0 (Principle VII).
  Folia's region logic is a design reference from its published documentation only, recorded in
  `research.md`.
- **Accessibility** *(web surfaces)*: N/A: no web surface.
- **Quality gates**: 95% coverage on changed files; xUnit Theories over entity classes, anchoring
  modes, merge gaps, timer modes, scheduling modes and scenarios; property-based tests for merges,
  splits and timer offsets; simulation tests on a fixed time base and seed that report the seed on
  failure; zero-allocation tests for steady-state bookkeeping; temp output folders unique per test.
- **Executable and living spec**: Every acceptance scenario and functional requirement maps to an
  automated test that runs on demand and passes before merge. `spec.md` and `tasks.md` are updated to
  match the work before the pull request. No earlier spec covers the regionizer, so nothing needs
  reconciling.
- **Spec independence and parallelism**: Completable on its own. Sub-spec 2 (telemetry and test
  harness) reads this feature's output formats but does not block it, and this feature does not
  depend on sub-spec 2.
- **Open decisions**: Decision 1 (section size and claim radius per class) is measurement-settled
  under Principle VI: this feature records measurements and recommendations with their runs and keeps
  the values in configuration. Decision 2 (target fight size) comes from load tests; the simulator
  only reports the Baseline reading the Phase 1 sweep starts from. No other open decision is touched.
- **Autonomous delivery (XIII)**: No requirement, task or scenario assigns work to a person or relies
  on manual verification, review or sign-off. Every ambiguity is resolved under Assumptions.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For every scenario, two runs on the same machine with the same seed and configuration
  give identical per-tick state hashes on every tick.
- **SC-002**: A 24-hour simulated Soak run in interleaved mode ends with zero invariant violations, and
  every synthetic timer fires exactly once, when FR-012 says.
- **SC-003**: The portal validator reports 100% of the leaks seeded into the test maps, and nothing on
  the clean test map.
- **SC-004**: Bookkeeping cost at 5,000 entities is recorded at p50, p95, p99 and max for Baseline and
  One base, with the serialized part separate, and compared with the 1 ms p99 budget.
- **SC-005**: The decision memo recommends a value, with its curve, margin, seeds and runs, for each
  of the eight values the simulator sets.
- **SC-006**: The three analytic checks agree with simulator results within the tolerances FR-031
  sets.
- **SC-007**: The regionizer core library has no dependency on the simulator, so sub-spec 10 can use it
  without changes.

## Assumptions

- **Population**: The constitution caps a continent at 2,000 concurrent players; the issue's Baseline
  mixes 5,000 entities. Population is a scenario parameter. The Baseline default follows the issue,
  as the stress bound the bookkeeping budget is set at, and a 2,000-player variant scaled in
  proportion gives the reading at the constitution's cap.
- **Timers**: The constitution requires per-region tick counters with merge offsets, so that is the
  default timer mode. Absolute deadlines on the continent time base, which the issue recommends, are
  built for comparison only. Adopting them would need a constitution amendment, so the memo can only
  recommend it.
- **Invariant 2**: It is checked in the issue's tick-boundary form (a claimed section belongs to the
  entity's region or to a region pending a merge with it). That is how the constitution's invariants 2
  and 3 hold together under merge-later.
- **Proposed changes to v2**: Layers for sealed areas, the merge-gap rule and portal-only claims are
  the issue's proposed changes, which need a yes or no before sub-spec 10. The simulator implements
  them to measure them and does not settle them.
- **Placeholders**: Speed ceilings and interaction ranges are placeholders from the issue until game
  design supplies real values. Results rescale through the claim-radius floor.
- **Air layer**: Aircraft above a configured altitude use their own section layer only when the
  scenario enables it, with hysteresis on the altitude threshold. This departs from the portal-only
  rule for layer changes, so it stays a measured option.
- **Soak duration**: The 24-hour simulated soak is a recorded run, cited in the memo with its manifest.
  The automated test suite runs the same Soak scenario for a shortened duration.
- **Sweep scale**: Sweeps run locally across the machine's cores. Spreading them across machines and
  Kubernetes Jobs belongs to sub-spec 2.
- **Output formats**: Results are local files (manifest, per-tick metrics, events and summary) in a
  documented, versioned format. Loading them into the analytics store belongs to sub-spec 2.
- **Bookkeeping cost**: It is wall-clock time of regionizer work per tick on the machine the run used,
  which the manifest records. Numbers from other machines are not compared directly.
- **Edge rounding**: An entity's section is found by flooring its position divided by the section size.
- **Map edges**: Claims are clipped to the map; nothing wraps.
- **Deferred portal toggles in the server**: The library applies a toggle made mid-tick when no
  region is ticking. That moment comes at every step end in the simulator, but a continent server
  whose ticks always overlap may never reach it. Sub-spec 10 must apply queued toggles at a point it
  guarantees, for example once no region owning an entity near the portal is ticking. This is
  recorded as a sub-spec 10 follow-up.
