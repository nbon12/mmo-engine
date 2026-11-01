<!--
Sync Impact Report
==================
Version change: 1.0.0 -> 1.1.0 (MINOR: new subsections, materially expanded guidance, and a new
  open decision)
Ratified: 2026-10-28 | Last amended: 2026-10-31

Purpose: support an automated experiment loop, in which bots playtest engine settings and gameplay
  content, a search keeps the candidates that pass, and people approve what is adopted.

Modified principles (titles unchanged):
  VI. Measure, Then Decide: gameplay content parameters are validated configuration; every run has
    a run ID and a run record, and tuning values cite run IDs; timing is a measurement only from
    dedicated hardware over repeated runs; seed-determined outcomes are labeled simulator
    measurements; automated search follows Experiments and automated search.
  VII. Permissive Licenses and Clean-Room Design: offline tooling is not a code dependency
    (maintainer decision, 2026-10-31). Copyleft code still may not be copied or vendored into the
    repository, and the clean-room rule still applies.
  XI. Incremental Delivery, Simulator First: the simulator is the first part of the experiment
    harness and runs the production regionizer implementation.

Added sections:
  Architecture Constraints > Experiments and automated search
  Testing Standards > Bots and scenarios

Expanded sections:
  Architecture Constraints > Performance discipline and degradation: gameplay metrics; one set of
    metric definitions across simulators, load tests and Production.
  Architecture Constraints > Validation program: gameplay experiments, human playtests,
    calibration.
  Architecture Constraints > Open decisions: decision 11 (area effects across a region boundary);
    the amendment range becomes 3 to 11.
  Architecture Constraints > Glossary: gameplay content parameters, run, run record, run ledger,
    scenario, viability gate, offline tooling.
  Engineering Standards > Code: Python allowed for offline tooling only.
  Engineering Standards > Configuration and secrets: content parameters validated like tuning
    values.
  Testing Standards > Test types, Performance tests: runs reproduce from their run records.
  Development Workflow: tuning and content pull requests cite run IDs, need a human maintainer's
    approval, and are never merged by scheduled tasks or agents.

Removed sections: none.

Choices made without an explicit maintainer decision (review these):
  - Tuning and content pull requests need human approval and are never auto-merged.
  - Python is allowed for offline tooling only.
  - Offline tooling keeps two rules: no copying or vendoring copyleft code into the repository, and
    the clean-room rule.
  - At least three seeds per experiment candidate.
  - A gameplay change that reaches Production needs a human playtest; bot metrics alone do not
    approve it.
  - Open decision 11 is new.

Templates and guidance (outside /speckit-constitution's scope; updated in the same pull request):
  .specify/templates/overrides/plan-template.md   updated: run IDs in the evidence table; new
                                                  Experiments and bots gate
  .specify/templates/overrides/spec-template.md   updated: content parameters, gates, seeds and
                                                  held-out re-check
  .specify/templates/overrides/tasks-template.md  no change needed
  CLAUDE.md                                       updated: agent rules for experiments
  .claude/skills/architecture-police/SKILL.md     updated: registered experiment candidates are
                                                  deliberate alternatives; config values are off
                                                  limits
  docs/architecture/server-architecture.md        unchanged (imported reference copy)

Follow-up TODOs (carried over from 1.0.0):
  - TODO(REPOWISE): Repowise is not configured for this repository yet. The Development Workflow
    rule binds once indexing is set up.
  - TODO(DIAGRAMS): the spec's architecture diagram (node/333188a6-0ab8) and roadmap
    (node/3f4a1d4f-000b) were not in the export. The component list follows the spec's text and
    Principle XI encodes only the phases the text states. Confirm both against the diagrams.
-->

# MMO Engine Constitution

This constitution governs the MMO Engine project: a PlanetSide 2-style MMOFPS whose continents are
simulated by regionized C#/.NET servers, with progression and social state in an Orleans tier over
PostgreSQL, and continent servers run as an Agones fleet. It draws on two sources:

- the **Server Architecture Spec** (2026-09-30), copied to `docs/architecture/server-architecture.md`.
  This constitution carries the spec's binding rules; the spec keeps the rationale, estimates,
  rejected alternatives and sources.
- the engineering practices of the **NekoHOA (HOA Management Company) Constitution v3.1.0**,
  adapted for a real-time game. NekoHOA's technology stack applies here only to web surfaces.

**MUST** and **MUST NOT** are non-negotiable. **SHOULD** and **SHOULD NOT** may be departed from
only with a written justification in the feature's `plan.md`. **MAY** grants permission.

## Purpose and Scope

### Product

PS2-scale play: continents of up to 2,000 concurrent players, several base fights at once, and
combat that mixes infantry, ground vehicles and aircraft, with weapons that fire projectiles with
travel time. The first client is a TypeScript browser app.

### Goals

Every feature MUST align with the architecture's goals:

1. Use every core of a continent's host, with no locks in gameplay code.
2. Keep fights free of fixed server seams; region boundaries move with the players.
3. Isolate load, so an overloaded fight slows only itself.
4. Keep progression, outfits, friends and chat global across continents.
5. Depend only on permissively licensed code; GPL and AGPL projects are reference reading only.

Every feature MUST also align with the engineering goals carried over from NekoHOA: security and
privacy for every player and operator, consistent and predictable APIs and protocols, maintainable
and testable code, and accessible web surfaces.

### Non-goals

Work toward these MUST NOT be scheduled without an amendment:

- Simulating one continent across several machines (deferred; see Architecture Constraints,
  Multi-server continents).
- Splitting one fight across machines. Parallel phases raise a single fight's ceiling on one host,
  and load tests measure it.
- Console clients, which need a native client.

## Core Principles

### I. One Continent, One Process, Regions That Follow the Fight

- Each continent MUST run as exactly one server process on its own host.
- The process MUST cut its map into fixed square sections and group them into regions that tick in
  parallel. Region boundaries MUST come from activity, through the regionizer's merges and splits,
  never from fixed server seams.
- An overrunning region MUST delay only itself. No region's tick may wait on another region's tick.
- Only continent servers run the real-time simulation.

**Rationale**: Fights must stay seamless and load must stay isolated (Goals 2 and 3). One process
per continent keeps every cross-region interaction in memory.

### II. Single Writer, No Locks in Gameplay Code

- Each region owns a private data object: its entities, spatial index, projectiles, base capture
  state, timers and pose history. Only work scheduled by that region's tick MAY touch it.
- Gameplay code MUST NOT use locks or other blocking synchronization: `lock`/`Monitor`, mutexes,
  semaphores, reader-writer locks, or blocking waits on tasks and events. Gameplay code is anything
  that runs inside a region's tick, a parallel phase, the global region or an area actor. Gameplay
  assemblies MUST enforce this rule mechanically, for example with a banned-API analyzer, not by
  review alone.
- Engine infrastructure (regionizer bookkeeping, the tick scheduler, queues, snapshot publication)
  MAY synchronize internally. It MUST NOT expose synchronization to gameplay code, and it MUST NOT
  make a ticking region wait on another region.
- Every entity MUST have exactly one authority at any instant. Inside a parallel phase, every
  entity MUST have exactly one writer, and workers MUST read only the previous tick's state.

**Rationale**: Ownership instead of mutual exclusion lets the server use every core (Goal 1) and
makes behavior reproducible in tests.

### III. Messages Across Regions, Snapshots for Reading

- A region MUST change state it does not own only by posting work to the owner's queue: the entity
  queue, the location queue, the global queue or an area's queue. The owner applies queued work at
  the start of its next tick.
- A region MUST read another region's entities only from that region's latest published, immutable
  snapshot, never from live region data.
- Every interaction MUST be classed as instant, deferred or read-only (see Architecture
  Constraints). Only instant interactions MAY set region boundaries through claims.
- Receivers MUST NOT assume any ordering across senders. Where the order in which work from
  different senders is applied changes the outcome, the receiver MUST apply that work in a
  deterministic order, for example by true timestamp, then sender, then sequence number.

**Rationale**: Messages and snapshots keep regions independent and keep reads from blocking
writers. They also preserve the invariants that keep multi-server continents possible later.

### IV. The Server Is Authoritative; Clients Are Untrusted

- The server MUST be authoritative for all combat and all game state. Clients MAY predict only
  their own movement and weapon effects, and MUST reconcile when server state arrives.
- Every client message MUST be treated as untrusted input and validated (size, rate, value
  ranges, sequencing and permission) before it affects state. Checks in any client, game client or
  web frontend, are UX only and MUST NOT be relied on for enforcement.
- A client MUST receive only the state its view allows. Interest-management tiers and caps, and
  closed portals, bound what each player learns.

**Rationale**: A browser client is easy to tamper with. Server authority and minimal disclosure
are the baseline that any anti-cheat approach builds on.

### V. One Owner for Live Data; PostgreSQL Is the Source of Truth

- PostgreSQL MUST be the system of record for persistent game data.
- Live player and social data MUST have exactly one owner, and only that owner writes it. The
  owner is an Orleans grain, or the per-player or per-outfit owner inside the day-one service
  (Principle XI). Admin and moderation tools MUST change live data through its owner, never with
  direct SQL.
- Only the persistence tier writes to the game database. Every other component, web surfaces
  included, reaches game data through the persistence tier. Read-only consumers such as
  leaderboards and analytics MAY read PostgreSQL directly and accept up to one batch interval of
  lag.
- Currency and purchases MUST be written immediately as append-only ledger rows. Currency MUST
  never be held only in memory.
- Rows MUST carry a concurrency token (`xmin`) so that a stale save fails instead of overwriting.

**Rationale**: One owner per record removes write races without distributed locks. Writing the
ledger immediately means a crash can cost stats, never money.

### VI. Measure, Then Decide

- These tuning values MUST come from the regionizer simulator or from load tests: section size,
  claim radius per entity class, minimum base spacing, target fight size, tick rates,
  visibility-tier ranges, rates and caps, per-client byte budgets, the progression batch interval,
  split thresholds, minimum region lifetime and the parallel-phase threshold. Each value MUST live
  in validated configuration, not in code constants, and MUST cite the run IDs that produced it.
- Gameplay content parameters MUST also live in validated configuration, not in code constants.
  They are the numbers of weapon, vehicle and ability archetypes (for example damage, fire rate,
  projectile speed, splash radius, range, health and speed) and the contents of loadouts. Code
  implements each kind of behavior; configuration sets its numbers. A content value MAY start as a
  design judgment. Once an experiment or playtest changes it, it MUST cite that run's ID.
- Every simulator run, load, stress or soak run, experiment run and human playtest MUST have a
  unique run ID and a run record in the run ledger. The record holds the build (commit SHA), the
  scenario, the configuration and its hash, the seeds, the hardware, the harness version and the
  resulting metrics. A human playtest records its participant count and findings in place of
  seeds.
- Every performance number in a spec, plan or pull request MUST be labeled an estimate or a
  measurement. Once a measurement exists, it supersedes the estimate.
- A timing figure (tick time, GC pause, throughput) is a measurement only when it comes from
  dedicated, representative hardware over repeated runs, with the spread reported. Timing from the
  offline simulator, a developer machine or a shared CI runner is an estimate. An outcome that the
  seed fully determines, such as region count, the largest region's share of players or players
  within a radius, is a simulator measurement and MUST be labeled so until a load test confirms it.
- Automated search over tuning values, content parameters or algorithms MUST follow Experiments
  and automated search (Architecture Constraints).
- Every change to tick-path code MUST ship with benchmarks and zero-allocation tests (see Testing
  Standards).
- When a region overruns, correctness MUST come before tick rate, and the region degrades only in
  the order defined under Architecture Constraints.

**Rationale**: The spec's numbers are estimates and several of its open questions are empirical.
The simulator and load tests exist to replace guesses with data before the design depends on them.
Recording every run lets anyone reproduce a number and see what it rests on, and keeping gameplay
numbers in configuration lets experiments vary them without code changes.

### VII. Permissive Licenses and Clean-Room Design

- Code dependencies, meaning anything linked into, bundled with or deployed as part of our
  software, MUST be licensed MIT or Apache-2.0, unless listed as an approved exception.
- Approved exceptions: **Npgsql** and **Npgsql.EntityFrameworkCore.PostgreSQL** (PostgreSQL
  License, a permissive OSI license), required because PostgreSQL is mandated. Another
  permissively licensed package (for example BSD, ISC or PostgreSQL License) MAY be used only after
  a PATCH amendment adds that named package to this list.
- GPL, AGPL and other copyleft code MUST NOT be linked, copied, vendored, translated or
  distributed. Copyleft tools MAY run as unmodified, separate processes in local development and
  CI only (for example a MinIO test container), and MUST NOT run in hosted environments.
- Offline tooling is not a code dependency. Offline tooling runs only on developer machines, CI
  runners and dedicated experiment or load-test hardware, never in Dev, Staging or Production, to
  build, test, or run and analyze experiments (for example optimizers, plotting and data-analysis
  libraries), and nothing linked into, bundled with or deployed as part of our software references
  it. The license, linking and copyleft rules above do not apply to it, with one exception:
  copyleft code MUST NOT be copied, vendored or translated into this repository. The clean-room
  rule below applies to it as it does to all our code.
- Folia (GPL-3.0) and AzerothCore (GPL-2.0) are design references only. The regionizer MUST be
  implemented from Folia's published documentation, never from its source code. Each feature MUST
  record its external design sources in its `research.md`.
- Hosted services such as Sentry, Cloudflare, Neon and GitHub are not code dependencies. The stack
  sections govern them, not this principle.

**Rationale**: License obligations must never attach to our code, and recorded sources keep the
clean-room claim provable.

### VIII. Test First; Deterministic, Parallel-Safe and Rerun-Safe Tests (NON-NEGOTIABLE)

- Tests MUST be written before implementation, MUST be traceable to the spec's acceptance criteria,
  and MUST drive the work red to green.
- Every test MUST assume that other tests are running at the same time, and that earlier runs,
  including crashed or cancelled ones, left data, containers, files, messages, grains or other
  artifacts behind. No test may require a clean environment in order to pass.
- Test parallelization MUST stay enabled. A test MAY opt out of parallel execution only with a
  reason documented in the test.
- Simulation tests MUST be deterministic: time comes from an injected continent time base,
  randomness comes from seeded generators, and the test steps the ticks itself.
- Persistence tests MUST run against real PostgreSQL through Testcontainers, never against
  in-memory substitutes.

**Rationale**: A parallel, regionized server is trustworthy only if its tests are reproducible,
and the test suite must keep scaling as more contributors and agents work in parallel.

### IX. Executable, Living Specifications

Specifications are not documentation that lags behind the code. They are the system's executable
contract and MUST stay true at all times.

- **Always executable**: Every feature spec (`spec.md`) MUST remain executable. Each mandatory
  acceptance scenario and functional requirement MUST be backed by at least one automated test
  (unit, simulation, integration, contract, end-to-end, load, game-client or web-surface frontend)
  that can run on demand and that currently passes against the merged code. A spec whose acceptance
  criteria cannot be executed, or whose tests fail, is a defect that MUST be fixed before merge.
- **No unverified claims**: A spec MUST NOT describe behavior that no executable test verifies.
  Acceptance scenarios MUST be traceable to tests, and tests MUST be traceable back to acceptance
  criteria.
- **Living and truthful (`spec.md` only)**: `spec.md` MUST reflect the system as built. When the
  implementation diverges from any `spec.md`, including older, already-merged specs, the
  divergence MUST be resolved before the change merges, either by updating the spec or by
  correcting the code. Spec drift is a defect. Only `spec.md` carries this obligation; `tasks.md`,
  `plan.md` and `research.md` are point-in-time artifacts and need not be kept current for merged
  features.
- **Pre-PR freshness**: Before a pull request is submitted, the active feature's `spec.md` and
  `tasks.md` MUST match the work actually performed, and any older `spec.md` that has drifted from
  the code MUST be updated. The active feature's `plan.md` and `research.md` are not part of this
  gate.
- **Cross-spec consistency**: When a new or amended spec's tests or acceptance criteria directly
  contradict a former spec, the contradiction MUST be reconciled before merge. Reconciling means
  updating the superseded specs so the corpus is consistent and recording which spec prevails and
  why.
- **Corpus invariant**: The full body of specs MUST be free of mutually contradictory executable
  assertions at all times.

**Rationale**: Many contributors and agents work from the specs in parallel, so a stale spec misleads
every one of them. Tying each claim to a passing test keeps the corpus trustworthy.

### X. Independent, Parallel Specs

- Each spec or sub-spec MUST be individually completable: implementable, testable and mergeable on
  its own, without requiring another spec to land first. The only exception is an explicit,
  documented hard dependency, such as a schema, protocol message or contract that this spec's
  tests need from another spec.
- When a larger effort is split across specs, the split SHOULD let the resulting specs be worked on
  in parallel by different contributors or agents without blocking one another.
- Hard dependencies MUST be the exception, MUST be documented in both specs' `plan.md`, and MUST be
  minimized during planning. Restructuring the split to remove a dependency is preferred over
  accepting it.
- A spec MUST NOT assume a sibling spec has been implemented unless that dependency is documented
  as above.
- This complements Principle IX: independence governs how specs are split and sequenced, and
  consistency governs what happens when their executable assertions overlap.

**Rationale**: Independent specs let contributors and agents work in parallel without waiting on
one another, and they keep each merge small enough to verify.

### XI. Incremental Delivery, Simulator First

- The project MUST be built incrementally, in independently testable vertical slices. A plan MUST
  NOT deliver unrelated surface area all at once.
- The first deliverable is the offline regionizer simulator. Its results set the claim radii, base
  spacing and target fight size before features depend on them. It is also the first part of the
  experiment harness: its scenario format, run records and run ledger are the ones that later load
  tests and experiments use (Testing Standards, Bots and scenarios).
- The simulator MUST run the production regionizer implementation, not a separate model of it, so
  that its results carry over to continent servers.
- Phase 1 runs one region per continent server, which proves netcode and interest management
  before the regionizer adds parallelism. Gameplay code written in Phase 1 MUST already follow the
  region model (Principles II and III), so that Phase 2 can add regions without rewriting gameplay.
- Parallel phases join Phase 2 only if the Phase 1 sweep shows that one thread cannot hold the
  target fight size.
- Until Orleans is adopted (an open decision), a single ASP.NET Core service over the same schema
  MAY serve as the persistence tier, provided it keeps exactly one owner per player and per outfit
  so that the later move to grains is mechanical.

**Rationale**: Each phase proves one risky piece before the next depends on it, and small slices
stay verifiable.

### XII. Web-App-Like Features Use the Web Stack

- Every feature that is a web application MUST be built as a web surface on the Web Surfaces stack.
  Examples: forums, the account portal, a web store (including store pages the game client opens),
  leaderboard and stats sites, outfit management pages, and support and admin or moderation
  consoles.
- The real-time path (the game client, continent servers, the gateway's real-time channels and the
  persistence tier) MUST NOT adopt web-surface hosting choices such as scale-to-zero or tolerated
  cold starts.

**Rationale**: NekoHOA's stack is proven for web applications. The real-time path has different
latency and statefulness requirements.

## Architecture Constraints

These constraints encode the architecture spec. A plan that needs to depart from one MUST amend
this constitution first.

### Platform

| Area | Constraint |
| --- | --- |
| Server | C# on .NET; one process per continent; Linux containers |
| Client | TypeScript in the browser first; a native client for consoles is deferred |
| Continent size | Up to 2,000 concurrent players |
| Host | One continent per dedicated host with at least 16 physical cores; no other workloads on the node |
| Tick rate | 30 Hz per region as the starting target (33 ms budget); an overrunning region MAY drop to 20 Hz |
| Weapons | Projectiles with travel time; instant resolution only at close range |
| Database | PostgreSQL |

### Components and connections

- The components named in the spec's text are the browser client, the gateway, the continent
  servers, the persistence and social tier (Orleans silos), PostgreSQL, and Agones on Kubernetes.
  The spec counts six components; its overview diagram was not imported (see the Sync Impact
  Report).
- Each player holds two connections: game traffic goes to their continent server, and everything
  else goes through the gateway.
- Only the continent servers run the real-time simulation, and only the persistence tier writes to
  the game database.

### Sections and regions

- The map is a grid of fixed square sections whose size is a power of two. Regions own sections,
  not entities, so the regionizer works only when a section gains or loses activity.
- The regionizer MUST maintain these invariants at all times:
  1. Every live section belongs to exactly one region.
  2. Every section within an entity's claim radius, without crossing a closed portal, belongs to
     that entity's region.
  3. A ticking region never grows. New activity beside it forms a transient region that merges in
     when the tick ends.
  4. A region is always transient, ready, ticking or dead.
- **Merge**: regions whose claims touch MUST merge. If one of them is ticking, the merge waits
  until its tick ends.
- **Split**: at tick end, after pending merges, a region that holds separate islands splits into
  independent regions.
- **Damping**: a split is considered only once a region has enough sections and enough dead ones,
  and only after a minimum region lifetime, to limit merge and split churn.
- **Callbacks**: region data merges by concatenating entities and timers, and splits by
  partitioning them by position. Every new kind of region data MUST define its merge and split
  behavior.
- The regionizer MUST provide an invariant checker, and simulator, simulation, door and soak tests
  MUST run it after every tick.

### Designed boundaries and area state

- Level designers place portals (doors, elevators, tunnel mouths, airlocks) between groups of
  sections. Claims and visibility MUST NOT pass through a closed portal. Opening a portal connects
  the two sides, which MAY trigger a merge when the current tick ends.
- Each boundary MUST declare what may cross it while it is closed: entity transfers, door and
  objective events, and deferred projectiles.
- State tied to a designed area, such as its doors, objectives, hazards and timers, MUST have a
  single owner: an area actor scoped like the global region. Regions read a copy taken at the start
  of each tick and change that state only through the area's queue.
- Each map's portal graph MUST be validated in the simulator before the map ships.

### Scheduling and time

- A fixed pool of tick threads runs the regions. Each region schedules its own next tick, and the
  pool starts the region that is due earliest.
- Tick counters are per region. On a merge, the absorbed region's timers get an offset so that
  relative deadlines survive; on a split, each child inherits the parent's counter. Respawn,
  capture and cooldown timers MUST stay correct through both.
- The continent has one time base. Gameplay code MUST read time only from it, never from the wall
  clock.
- Networking, serialization and GC get reserved cores first. Total allocated threads MUST stay
  under 80% of the host's cores, and the remainder become tick threads.

### Parallel phases

A region that is too busy for one thread MAY spread its tick across the pool in five phases:

1. **Movement**: each worker integrates its own slice of players and vehicles.
2. **Shots**: each worker sweeps its own slice of projectiles, or validates its slice of reported
   hits.
3. **Effects**: damage, heals and kill credit go into per-target queues.
4. **Apply**: the worker that owns each target applies that target's queue.
5. **Gameplay**: captures, timers and objectives run once, on one thread.

- Each phase gives every entity exactly one writer, and workers read the previous tick's state.
- Phases switch on only above a region-size threshold set by load tests.
- A tick's result MUST NOT depend on how its work was scheduled across threads (see Testing
  Standards, parallel equivalence).

### Global region

- Each continent has one global region, which ticks on its own schedule and never merges or splits.
  It owns state tied to no place: lattice links, base ownership, continent lock and alerts,
  population caps and faction balance.
- Regions read a copy of it taken at the start of each tick and change it only through the global
  queue.
- The global region checkpoints base ownership to the World grain.

### Cross-region queues

- **Entity queue**: runs a task on whichever region owns an entity, following the entity through
  merges and moves.
- **Location queue**: runs a task on the next tick of the region that owns a location, creating
  that region if needed.
- **Global queue**: changes global-region state. **Area queues** change area-actor state.
- Messages from one sender arrive in order. There is no ordering across senders (Principle III).

### Deploys and teleports

- Every spawn, login, redeploy and Instant Action jump MUST use the two-step teleport: the current
  region removes the entity and sets its new position, then the location queue adds it on the
  destination region's next tick.
- Instant Action picks its destination from the population stats that regions publish each tick.

### Interaction classes and claims

| Class | Examples | Resolved by | Added delay |
| --- | --- | --- | --- |
| Instant | Melee, close-range hits, heals, repairs, revives, collisions, capture points | The owning region, inside its tick | None |
| Deferred | Projectiles that leave the firing region, artillery | The region that owns the impact point, via the location queue | Up to 1 tick, hidden in flight time |
| Read-only | Far visibility, targeting, spotting, minimap | Any region, from the last published snapshots | 1 tick stale |

- Each entity claims the sections within its own instant-interaction radius. Infantry, ground
  vehicles and aircraft get separate radii, set from simulator results. An aircraft whose guns fire
  projectiles claims only a small bubble, so it cannot bridge the fights it flies between.
- Base spacing and terrain MUST keep neighboring fights farther apart than the largest claim radius.
- The largest region's share of players MUST be tracked as a production metric, with an alert.

### Snapshots and cross-region messages

- At the end of every tick, each region MUST publish an immutable snapshot of its entities and the
  sections it owns. Each entry holds ID, class, faction, position, velocity and visible state, and
  each snapshot includes the region's cell index.
- Readers use the latest complete snapshot, so reads never block the writer. Snapshots are the only
  way a region sees another region's entities, a deliberate departure from Folia, which forbids
  cross-region reads.
- Anything that changes another region's entity, such as damage, travels as a message on the
  entity queue, and the owner applies it at the start of its next tick.

### Projectiles

- **Close range**: the firing region resolves hits instantly against its own entities.
- **In flight**: projectiles MUST NOT claim sections, so a shell arcing between two fights cannot
  fuse their regions.
- **Over empty space**: the firing region keeps simulating the projectile across unowned sections,
  where only terrain can be hit.
- **Entering another region**: the firing region hands the projectile over on the location queue,
  and the new owner tests it against pose history at the projectile's true timestamp. A hop costs
  at most one tick (33 ms at 30 Hz).

### Interest management and network LOD

- Delivery workers MUST build each player's view from published snapshots, off the simulation
  threads. They are never authoritative, and view work MUST NOT lengthen a region's tick.
- Each region indexes its entities in a grid of cells smaller than a section. Each cell keeps a
  separate list of far-visible objects (aircraft, vehicles, base structures), so long-range queries
  never scan infantry, and a list of the views watching it. A player's view set is recomputed when
  the player crosses a cell boundary, not every tick.
- Visibility tiers start at the values below. Ranges, rates and caps per entity class are tuned in
  testing (Principle VI).

| Tier | Contents | Source | Starting update rate | Starting cap in dense fights |
| --- | --- | --- | --- | ---: |
| Near | Everything inside the class's near range | Snapshot cell index | Every tick | 100 entities |
| Mid | Infantry and vehicles past near range | Snapshot cell index | 10 Hz | 300 entities |
| Far | Far-visible objects only | Snapshots of all regions | 2 Hz | 600 entities |

- Each client gets a per-tick byte budget, filled near tier first, then mid, then far. When the
  budget runs out, far updates are dropped first. Updates are delta-encoded against the client's
  last acknowledged state.
- In dense fights, each tier keeps only its most relevant entities, ranked by distance, threat,
  squad and what the player is aiming at.

### Combat netcode

- **Prediction**: the client applies its own inputs at once and reconciles when server state
  arrives.
- **Lag compensation**: each region keeps a pose history per entity, long enough to cover the
  highest supported latency, and tests close-range hits against targets rewound to the shooter's
  view time. Pose history is region data, so it moves with its entities through merges and splits.
- **Projectiles**: simulated on the server with travel time; a projectile keeps its true timestamp
  when handed to another region.
- **Hit confirmation**: the region that applies damage sends the result back to the shooter's
  region on the entity queue.

### Persistence and social tier

| Grain | Key | Owns | Stored |
| --- | --- | --- | --- |
| Player | Character ID | Profile, loadouts, progression counters, presence | Own schema; stats batched |
| Outfit | Outfit ID | Roster, ranks, outfit stats | Own schema |
| Squad | Squad ID | Members, leader, waypoints | Memory only |
| Chat channel | Channel ID | Members, message fan-out | Memory only |
| Continent | Continent instance ID | Population, faction split, server endpoint, heartbeat | Memory only |
| World | World ID | Continent directory, open and locked continents, alerts | Own schema |

- Queryable data (characters, outfits, rosters, stats and the currency ledger) MUST live in our own
  PostgreSQL schema, read and written through EF Core or Dapper.
- Opaque grain state MAY use the Orleans ADO.NET provider with JSON serialization, and MUST NOT be
  used for anything we query.
- Orleans system tables (cluster membership, reminders) live in the same PostgreSQL instance and
  are applied through the migration pipeline.

| Data | Write policy |
| --- | --- |
| Currency and purchases | Written immediately, as append-only ledger rows |
| Loadouts and settings | Written on change |
| XP and stats | Batched per player on a fixed interval, at logout and on continent transfer |
| Squads, chat, presence | Never persisted |

- A crash MUST lose at most one batch interval of stats.
- Progression: regions record XP and stat changes in region-local buffers during each tick. A
  per-continent batcher aggregates them per player and sends each Player grain one update per
  batch interval, at logout and on transfer. The Player grain applies the update in memory and
  persists it under the write policy.
- Trades and other cross-player transfers run as one PostgreSQL transaction driven by a
  short-lived trade grain. The trade grain reserves the items in both player grains, commits the
  SQL transaction, then tells both grains to apply it. This reservation is a persistence-tier
  protocol, not a lock in gameplay code.
- Orleans transactions MUST NOT be used: their documented transactional store is Azure Table, and
  falling back to regular grain storage is meant for development only.

### Orchestration

- Each continent server MUST run as an Agones GameServer in a Fleet on Kubernetes, alone on a
  dedicated node with at least 16 physical cores.
- **Opening a continent**: the World grain requests an allocation from the fleet. The allocated
  server loads the map and registers its endpoint with its Continent grain.
- **Locking a continent**: the global region detects the lock condition and tells the World grain,
  which opens the next continent through an Agones allocation. The locking server stops accepting
  deploys, moves players out to open continents through the normal teleport path, flushes
  progression batches, then shuts down.
- **Failure**: a replacement server restores the map from the base-ownership checkpoint held by the
  World grain. Lost stats are bounded by the batch interval.
- Orleans silos and the gateway run as ordinary Kubernetes Deployments, outside Agones.

### Reference flows

Specs that touch these flows MUST conform to them or amend this constitution.

- **Login and first deploy**: the client signs in through the gateway, which activates the Player
  grain. The gateway asks the World grain for open continents. The player picks a spawn, and the
  gateway returns that continent's endpoint and a one-time join token. The client connects to the
  continent server, which checks the token and fetches the loadout from the Player grain. The
  server adds the player at the spawn point through the location queue, and the owning region
  starts sending updates.
- **Instant Action**: the client asks its current region, which picks a destination fight from the
  population stats in the latest snapshots. The current region removes the player, and the
  location queue adds them to the destination region on its next tick.
- **Progression events**: see Persistence and social tier.
- **Chat and presence**: the continent server sends its Continent grain a heartbeat with population
  and the player list, and the Continent grain updates each Player grain's presence. Chat travels
  from the client to the gateway to a Chat channel grain, which fans it out to members' gateway
  connections.
- **Continent lock**: see Orchestration.

### Performance discipline and degradation

- Entities MUST be stored as structs of arrays per region. Gameplay code MUST make no per-tick
  allocations; buffers and messages are pooled. Visibility and hit tests run as tight loops over
  contiguous data.
- Tick threads MUST NOT perform blocking I/O, including synchronous logging or telemetry export.
- GC pause time MUST be tracked against the tick budget.
- A region that still overruns MUST degrade in this order:
  1. Tighten that region's Delivery caps.
  2. Drop that region from 30 Hz to 20 Hz.
  3. Inside sealed designed areas only, slow game time with a time scale over the continent-wide
     time base. Time dilation MUST NOT apply across an open boundary.
- Every continent server MUST report per-region tick time, region count, the largest region's share
  of players, merge and split rate, cross-region message volume, snapshot size, bytes per client
  per tick, and GC pause time.
- Every continent server MUST also report gameplay metrics, recorded into the same pre-allocated
  per-region buffers and exported off the tick threads: kills and deaths by weapon and entity
  class, time to kill, time from spawn to death, base captures, fight duration, and players within
  a configured radius of each fight. The simulators, load tests and Production MUST share one set
  of metric definitions, so that simulator results can be checked against load tests and live
  play.

### Multi-server continents (deferred, kept open)

Running one continent across several servers is deferred until profiling shows that one host cannot
carry a continent. These invariants MUST hold now, so that the option stays open:

- one authority per entity at any instant;
- stable entity IDs;
- player sessions independent of the worker simulating them;
- writes across regions only through messages;
- one continent-wide time base.

The Simulation Domains sub-spec is the starting point for that later work. Its designed boundaries
and area state are adopted above; its dynamic domains are dropped, because region splitting already
covers them.

### Validation program

- **Simulator**: lay out bases along lanes on a continent-sized map; place simulated players in
  several fights, plus aircraft and stragglers moving between them; run the regionizer every tick
  across a range of claim radii; plot region count and the largest region's share of players
  against claim radius; and read the claim-radius budget and minimum base spacing off the point
  where region count collapses.
- **Load tests**: bots on one continent server measure per-region tick time against the number of
  players in the region. That curve sets the target fight size.
- **Stress scenario**: 1,000 bots on one base. It passes if the region holds 30 Hz or degrades
  cleanly to 20 Hz.
- **Door tests**: portals open and close mid-fight, and merges and splits stay correct.
- **Soak test**: repeated merges and splits, checking timer correctness and churn.
- **Gameplay experiments**: bots play content variants (weapons, vehicles, abilities, loadouts,
  base layouts) in shared scenarios, and their gameplay metrics flag problems such as a dominant
  loadout, stalemates, spawn camping or long travel between fights.
- **Human playtests**: people play the candidates that pass the gameplay experiments, and their
  findings decide whether a design is adopted.
- **Calibration**: where the simulator and a load test, or a load test and Production, report the
  same metric for the same scenario, the gap MUST be recorded with both run IDs and reported with
  any decision that rests on the simulator.

### Experiments and automated search

An experiment varies tuning values, gameplay content parameters or algorithms across scenarios, by
sweep, optimizer or agent, and keeps the candidates that do best.

- An experiment MUST state its goal as viability gates plus a score. A candidate is viable only if
  every run meets each of these gates that applies to it: the regionizer invariant checker passes
  after every tick; every region holds its tick budget or degrades only in the defined order;
  Delivery stays within the per-client byte budget; and bots stay above the scenario's activity
  floors (for example shots fired, captures and distance moved), so that no candidate can score by
  stopping play. Only viable candidates are scored.
- Every candidate MUST run on at least three seeds, and its score is reported with the spread.
- Before a winner is adopted, it MUST be re-run on scenarios and seeds that the search did not use.
  It MUST stay viable there, and its score on them is reported beside its search score.
- A value chosen for its timing MUST be confirmed by a load test before it is adopted.
- An algorithm candidate, such as an alternative split heuristic or interest-management strategy,
  MUST implement the shared interface for its role and MUST pass the full test suite, including
  the invariant checker, parallel-equivalence and zero-allocation tests, before it competes.
  Candidates registered with the experiment harness are deliberate alternatives until an
  experiment's result is adopted; the losing candidates are then removed.
- Bot gameplay metrics are proxies for fun. They MAY reject a design, but they MUST NOT alone
  approve one: a gameplay change (to content parameters or gameplay rules) that reaches Production
  MUST also be supported by a human playtest.
- Experiments and agents MAY propose changes; they MUST NOT adopt them. Adoption follows the
  Development Workflow. An automated run MUST NOT settle an Open decision: decisions 1 and 2 settle
  when an approved pull request records the measurement, and decisions 3 to 11 by amendment.

### Open decisions

These decisions are deliberately unsettled. A spec MUST NOT settle one silently. Decisions 1 and 2
are settled by measurements recorded under Principle VI; later re-tuning needs no amendment.
Decisions 3 to 11 are settled by a MINOR amendment that records the choice and its evidence.

1. Section size and claim radius per entity class, from the simulator.
2. Target maximum fight size, from load tests.
3. Tick rate: stay at 30 Hz, or go higher.
4. Browser transport: WebTransport datagrams, or WebSocket.
5. Hit detection: server-simulated shots, or client-reported hits with server validation. If
   client-reported hits are chosen, the server MUST check range, fire rate and line of fire, and
   statistical outliers MUST be reviewed.
6. Which bases get designed boundaries, and which portals players can open.
7. Orleans from day one, or ASP.NET Core over PostgreSQL first.
8. Nakama, or custom grains, for friends, groups and chat.
9. Anti-cheat approach for a browser client.
10. Hosting for the game database. The web-surface stack's Neon choice does not apply to it.
11. Area effects that cross a region boundary, such as splash damage: count the effect radius toward
    claims, so that the effect stays inside one region, or have the region that owns the impact
    point post the effect to neighboring regions' entity queues, which apply it on their next tick.

### Glossary

| Term | Meaning |
| --- | --- |
| Section | Fixed square map tile; the unit regions own |
| Region | A set of sections with private data, ticked with one writer per entity |
| Claim radius | Distance around an entity whose sections must share its region |
| Global region | Continent-wide state tied to no place; never merges or splits |
| Area actor | Single owner of a designed area's state, scoped like the global region |
| Portal | A designed opening between groups of sections; claims and visibility don't cross it while closed |
| Snapshot | Immutable end-of-tick copy of a region's entities and sections |
| Entity queue | Delivers a task to whichever region owns an entity |
| Location queue | Delivers a task to the region owning a location, on its next tick |
| Far-visible object | An entity kept on a separate list for long-range visibility |
| Parallel phase | One step of a busy region's tick, run across threads with one writer per entity |
| Delivery | Workers that build each player's view from snapshots, off the simulation threads; never authoritative |
| Gameplay code | Code that runs inside a region's tick, a parallel phase, the global region or an area actor |
| Tick path | Gameplay code plus the engine code it calls during a tick |
| Grain | An Orleans virtual actor, addressed by ID |
| Persistence tier | The Orleans silos, or the day-one service, that own live player and social data and alone write the game database |
| Fleet | An Agones set of identical game servers |
| Real-time path | The game client, continent servers, the gateway's real-time channels and the persistence tier |
| Web surface | A web-app-like feature on the Web Surfaces stack |
| Gameplay content parameters | The numbers of weapon, vehicle and ability archetypes, and the contents of loadouts |
| Run | One simulator, load-test, experiment or playtest execution, identified by a run ID |
| Run record | A run's build, scenario, configuration and hash, seeds, hardware, harness version and metrics |
| Run ledger | Where run records are kept; tuning values and content changes cite its run IDs |
| Scenario | Map, bases, populations, loadouts, bot behaviors, duration and metrics, in the shared format |
| Viability gate | A pass/fail check that every run of an experiment candidate must meet before it is scored |
| Offline tooling | Tooling that runs only on developer machines, CI and experiment hardware, and that no shipped code references |

## Web Surfaces

### Scope

A web surface is any player-, operator- or public-facing feature delivered as web pages or as
request/response HTTP APIs outside the real-time simulation. Examples: the account portal
(sign-up, sign-in, recovery, settings), forums, the web store and any store page the game client
opens, news and patch-notes pages, leaderboard and stats sites, outfit management pages, support
and ticketing, and admin and moderation consoles. The game client, continent servers, the gateway's
real-time channels and the persistence tier are not web surfaces.

### Stack

Web surfaces MUST use the NekoHOA stack:

- **Frontend**: Angular, hosted on Cloudflare Pages.
- **Backend**: a .NET REST API with FastEndpoints for all application endpoints (GET, POST, PUT,
  PATCH, DELETE as appropriate). MVC controllers MUST NOT be introduced for application endpoints
  unless a plan documents a specific compatibility reason and an amendment approves it.
- **Database**: PostgreSQL on Neon, with a separate database per environment and scale-to-zero
  allowed. A surface uses shared tables within its own database; per-tenant database silos need an
  amendment.
- **Authentication**: the shared account identity (Engineering Standards, Security).
- **Containers**: Docker images published to Docker Hub.
- **Runtime**: Google Cloud Run, configured to scale to zero; cold start latency is acceptable.
- **Edge**: Cloudflare in front of all public web-surface traffic, backend API included, for DDoS
  protection, rate limiting, bot and traffic filtering, and edge caching where appropriate.
- **CI/CD**: GitHub Actions, deploying automatically on merges to `main` with environment
  promotion rules.
- **Observability**: Sentry in the frontend and backend, with trace context propagated end to end,
  plus Serilog structured JSON logs.
- **API documentation**: Swashbuckle generates OpenAPI and Swagger UI at `/swagger` in development
  only, from the implemented endpoints, kept aligned with the contracts. Swagger UI and the OpenAPI
  endpoint MUST be disabled entirely in production.
- **File storage**, if added: Cloudflare R2 in hosted environments, and MinIO in local Docker
  Compose and local or CI tests (permitted as a dev/test-only tool by Principle VII). PostgreSQL
  stores file metadata, ownership and references, never large binary payloads.

### Backend rules

- APIs MUST be stateless: correctness MUST NOT depend on in-process session state. A surface's
  persistent state lives in its own PostgreSQL database, and game data is reached only through the
  persistence tier.
- A global exception handler MUST map unhandled errors to the documented error shape. Development
  responses MAY include details for debugging. Production responses MUST NOT leak stack traces,
  internal paths or other system information; the details are logged server-side.
- Every endpoint that returns a collection MUST support `limit` and `offset` pagination and MUST
  document its default and maximum `limit`, until an amendment adopts a cursor standard.
- Database access MUST use a low maximum connection count, pooling and short-lived `DbContext`
  instances.
- Startup MUST apply migrations idempotently and safely (Engineering Standards, Data).
- Responses MUST be cached only when that is explicitly safe. Authenticated or user-specific
  responses MUST NOT be edge-cached unless they are carefully keyed and the plan justifies it.
  Static assets SHOULD use hashed filenames.
- Callbacks from external providers, such as payment webhooks, MUST be verified and idempotent: a
  replayed or duplicated callback MUST NOT take effect twice.

### Frontend rules

- The UI MUST function and render correctly at phone, tablet and desktop widths.
- The UI MUST follow a consistent design system and Angular conventions. User flows MUST be
  validated against the backend API contracts, and breaking contract changes require coordinated
  releases or versioning.
- The UI SHOULD target WCAG 2.1 AA. Forms, navigation, dialogs and content-creation flows MUST be
  keyboard-accessible, with meaningful labels and validation messages.
- Frontend authorization checks are UX only.
- The frontend MUST fail loudly at boot when required configuration is missing or invalid.

### Boundary with the game

- A web surface MUST NOT write game-owned data (characters, loadouts, progression, the currency
  ledger, outfits, world state) to the game database; it MUST call the persistence tier. Read-only
  pages such as leaderboards and stats MAY read the game database through a read-only role and
  accept up to one batch interval of lag.
- A web surface's own data, such as forum threads, MUST live in a database that the surface owns,
  never in the game schema.
- Purchases and currency grants MUST reach the currency ledger through the persistence tier and be
  written immediately (Principle V).
- When the game client offers a web-surface feature, such as a store button, it MUST open the web
  surface or call the web surface's API. The web surface remains the single authority for that
  feature.
- Web surfaces and the game share one account identity.

### Web-surface testing

- Backend tests use xUnit and .NET Testcontainers (PostgreSQL, plus MinIO when file storage is
  used) under the Testing Standards.
- Angular unit tests use Jasmine and Karma. Component tests use Angular Testing Library. Browser
  tests outside the end-to-end suite use Playwright. End-to-end tests use Cypress. Component
  stories and visual regression tests use Storybook.

## Engineering Standards

These standards apply to every component unless a section says otherwise.

### Code

- Server code is C# on .NET. The SDK version MUST be pinned (`global.json`) and SHOULD be the
  current LTS release. Code follows .NET naming conventions and team code style, and CI enforces
  linting and formatting.
- The browser game client and web frontends are written in TypeScript.
- Offline tooling (Principle VII) MAY be written in Python. Python MUST NOT be used in the real-time
  path, in web surfaces or in any deployed service.
- Prefer functional design where it fits: explicit inputs and outputs, pure functions,
  composition, and immutable DTOs, messages and snapshots. In tick code, the single-writer
  structure-of-arrays model and pooled buffers take precedence over immutability.
- Use CQRS where it fits the application boundary and existing patterns. Application services MAY
  coordinate side effects and transactional boundaries.

### Data, identifiers and time

- Durable identifiers (accounts, characters, outfits, worlds, continent instances, ledger entries)
  SHOULD be UUIDs unless a plan documents a better fit. Simulation entity IDs MUST be compact,
  unique within a continent instance and stable for the entity's lifetime. Network encodings MAY
  use per-client short handles.
- Dates and times MUST be stored and exposed in UTC, unless a field explicitly represents a
  user-entered local date or time. Simulation time comes from the continent time base.
- Schema changes MUST be made as migrations, and manual database edits are not allowed. Migrations
  MUST be tested against PostgreSQL, and MUST be applied idempotently, once per rollout, with a
  guard against concurrent runners (an advisory lock or a dedicated migration job). Destructive
  migrations require an explicit rollback or mitigation plan. Seed and reference data MUST be
  handled predictably per environment.
- Player-generated content (character and outfit names, chat, outfit messages, forum posts) MUST
  be treated as untrusted input. It MUST identify its owner, scope, visibility and moderation or
  audit state where applicable.

### Configuration and secrets

- All configuration MUST be validated at startup. In .NET services, every strongly typed options
  class MUST be bound and validated with FluentValidation, and the service MUST fail fast (refuse to
  start) on missing or invalid configuration rather than failing later at request or tick time.
- Validation rules MUST be environment-aware and MUST validate the environment name
  (`DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`) against the known set: `Development` (local
  machine), `Dev` (deployed dev), `Test`, `Staging` and `Production`.
- Browser apps, both the game client and web frontends, MUST apply an equivalent boot-time guard
  and fail loudly on missing or invalid configuration.
- New configuration of any kind MUST ship with its validator. Tuning values and gameplay content
  parameters (Principle VI) are configuration and are validated the same way.
- Secrets MUST NOT be committed or baked into images. Configuration comes from environment
  variables or managed secret stores, and Dev, Staging and Production secrets are isolated.

### Observability

- .NET services MUST emit structured JSON logs through Serilog. Request paths SHOULD propagate
  correlation IDs. Game-side logs SHOULD carry the continent instance, region, tick and entity or
  session IDs where relevant.
- Sentry MUST capture errors from every service and browser app, tagged with environment and
  release. Web surfaces and the gateway's request paths also report performance and propagate trace
  context. Sentry events MUST NOT capture secrets, credentials, payment details, private messages
  or other sensitive player data.
- Metrics MUST be exported through OpenTelemetry. Tick code records metrics into pre-allocated
  per-region buffers that are exported off the tick threads.
- Continent servers MUST report health through the Agones SDK. Other services SHOULD expose health
  and readiness endpoints.
- Production errors MUST be logged with enough detail to debug, without leaking that detail to
  clients.

### Security

- One account identity serves the game and every web surface. It MUST be implemented in the
  application, with ASP.NET Core Identity for credential storage and password hashing, and JWT
  bearer access tokens validated against a pinned signing algorithm. Refresh tokens MUST be
  rotating, single-use and stored only as hashes. A managed identity provider MAY be adopted later
  by amendment if it meets this section.
- A continent server MUST accept a player only with a one-time join token issued by the gateway.
- Every protected endpoint, channel and action MUST require authentication.
- Authorization MUST be enforced server-side, based on the authenticated principal and their role
  in the target scope (character, outfit, world or moderation scope). It MUST distinguish the
  product's roles, follow least privilege, and be covered by integration tests.
- Every inbound connection MUST enforce per-connection limits on message rate and message size, and
  violations MUST be rejected and logged.
- Data in transit MUST use TLS end to end. Data at rest is encrypted as each provider supports.
- APIs and network updates MUST NOT return more data than the principal and context need.
- These security-sensitive events MUST be logged: sign-in and account identity changes, role and
  permission changes, outfit membership and rank changes, currency, purchase and trade events,
  moderation actions such as mutes and bans, administrator and game-master actions, deletes, and
  publish or unpublish actions. Moderation and administrator actions MUST be auditable.
- Rate limiting MUST apply to auth-adjacent endpoints, chat, invites, purchases, reports and public
  discovery endpoints.

### Contracts and protocols

- HTTP APIs, meaning web surfaces and the gateway's request/response endpoints, MUST be
  implemented with FastEndpoints and MUST follow these standards:
  - a consistent envelope or documented response shape, and one documented error shape across
    endpoints;
  - contracts that document pagination (default and maximum `limit`), authentication,
    authorization and cacheability;
  - contract updates and migration notes for breaking changes;
  - Swagger in development only, as in Web Surfaces.
- The game wire protocols (client to continent server, and the gateway's real-time channels) MUST
  have a single source of truth for message definitions. The C# and TypeScript implementations MUST
  be generated from it, or verified in CI against shared golden fixtures. A protocol version MUST
  be negotiated at connect, an incompatible client MUST be rejected with a clear error, and a
  breaking change MUST bump the version and include migration notes.
- Types that are persisted or exchanged between independently deployed processes (grain
  interfaces and state, cross-process messages, protocol messages) MUST tolerate version skew
  during rolling deploys. A non-additive change needs a migration plan.

### CI/CD and code quality

- GitHub Actions MUST run linting and all applicable tests before merge.
- Pull requests MUST pass Sonar static analysis, run by a GitHub Actions workflow, and MUST publish
  Codecov results.
- Coverage MUST be at least 95% for all relevant files that a pull request changes or adds.
- Deployments MUST run automatically on merges to `main`, with environment promotion rules defined
  in pipeline configuration.
- Every service MUST be containerized. Images are published to Docker Hub and never contain
  secrets.

### Environments

- Dev, Staging and Production MUST be isolated, with separate databases, separate deployments
  (Agones fleets, silo and gateway Deployments, Cloud Run services), separate authentication
  configuration and secrets, and per-environment Cloudflare configuration.
- Local development MUST approximate production. Docker Compose, or a successor, brings up a real
  PostgreSQL and the other dependencies, so that developers and automated tests run against
  production semantics. A continent server MUST be runnable locally without Kubernetes, using the
  Agones local SDK mode.

## Testing Standards

Adapted from NekoHOA's Spec Kit Testing Constitution. Tests use xUnit and .NET Testcontainers, and
they are deterministic (repeatable and order-independent), isolated (no data leaks across tests),
realistic (PostgreSQL with production semantics), scalable, parallel-safe and rerun-safe.

### Test types

- **Unit**: pure logic, with no I/O.
- **Simulation**: region, regionizer and gameplay code driven tick by tick in process, with a
  deterministic scheduler, an injected time base and seeded randomness, and no network or database.
- **Repository**: persistence abstractions (CRUD, queries, constraints, transaction boundaries)
  tested against PostgreSQL for structural correctness, not business outcomes.
- **Business process**: a named operation that coordinates domain rules, state transitions and
  side effects, for example `ApplyProgressionBatch`, `ExecuteTrade`, `OpenContinent`,
  `LockContinent` or `JoinContinent`. Assertions target business outcomes, not repository internals.
  Each test builds minimal valid state with factories and calls no unrelated process. Grain-hosted
  processes run in an Orleans test cluster backed by PostgreSQL.
- **Contract**: protocol golden fixtures, exercised on both the C# and TypeScript sides, and HTTP
  API contracts.
- **End-to-end**: few in number. They cover full workflows across processes and, where justified,
  real infrastructure boundaries, with deterministic setup and teardown, and they check workflow
  correctness rather than low-level SQL.
- **Load, stress and soak**: performance, degradation and churn on representative hardware.
- **Game client**: unit tests for non-trivial logic (prediction and reconciliation, delta decoding,
  view-tier handling, the configuration guard) and Playwright browser tests for the boot and connect
  flows. The client's unit-test runner is chosen in its first plan, subject to Principle VII.
- **Web-surface frontend**: see Web Surfaces.

Simulator and experiment runs are not tests, but every seeded run MUST be reproducible from its
run record (Principle VI).

### Isolation and rerun safety

- Every database-touching test MUST run inside a PostgreSQL transaction that rolls back after the
  test, or use one of these approved equivalents, chosen and documented in the plan:
  - when the code under test opens its own connections (grains, for example), unique keys per test,
    with every assertion scoped to those keys;
  - a database cloned per test class or collection from a migrated template.
- Tests MUST assume concurrent tests and leftovers from earlier runs. In practice, a test:
  - generates unique IDs and names;
  - never asserts on global counts or on the absence of other rows;
  - takes dynamic ports and uses unique temp directories;
  - gives each test cluster unique cluster and service IDs;
  - namespaces queues, channels, Kubernetes namespaces and fleets per run;
  - makes setup idempotent;
  - treats cleanup as best effort, never as a precondition for correctness, and lets
    Testcontainers' resource reaper handle leftover containers.
- Each test owns its data, fails for one reason, and passes in any order.

### PostgreSQL fidelity

- Repository, business-process, integration and other persistence tests MUST use PostgreSQL
  through .NET Testcontainers. In-memory EF Core providers and Orleans in-memory storage MUST NOT
  replace PostgreSQL in those tests.

### Determinism and parallel equivalence

- Simulation code MUST be testable deterministically (time base, seeds, tick stepping), and a
  failing test MUST report the seed it ran with.
- **Parallel equivalence**: for the same initial state and inputs, a region ticked with parallel
  phases on N threads MUST end in the same state as the same region ticked on one thread. Regions
  ticked in any order MUST produce the same per-region results for the same queued work.

### Invariants, properties and churn

- The regionizer invariant checker runs after every tick in simulation, simulator, door and soak
  tests.
- Regionizer merges and splits, timer offsets across merges and splits, and snapshot publication
  SHOULD have property-based tests, using a library that satisfies Principle VII.

### Performance tests

- Tick-path code MUST have zero-allocation tests: after warm-up, steady-state ticks allocate no
  managed memory, as measured for example with `GC.GetAllocatedBytesForCurrentThread`. These tests
  run in CI.
- Tick-path changes MUST include BenchmarkDotNet benchmarks, and the results the change is expected
  to move are attached to the pull request.
- Load, stress and soak tests run on dedicated, representative hardware rather than shared CI
  runners, on a schedule and before releases. Every run has a run record (Principle VI).

### Factories and scenario builders

- Factories and scenario builders declare valid data: explicit field values, required foreign keys
  and defaults. They MUST NOT embed business rules or conditional side effects, and they MUST NOT
  modify objects they did not create.

### Bots and scenarios

- Bots that connect to a continent server, for load, stress, soak or experiment runs, MUST be
  ordinary clients. They join with a one-time join token from the gateway, use the production wire
  protocol, and pass the same validation and per-connection limits as players. Server builds MUST
  NOT contain bot-only commands, shortcuts or trust exemptions. Scenario setup, such as placing
  players or opening portals, MUST use authenticated, audited admin or game-master actions.
- Scenarios MUST use one shared definition format (map, bases, populations, loadouts, bot
  behaviors, duration and the metrics to collect), read by the regionizer simulator, simulation
  tests and load tests alike.
- Bot behaviors MUST come from one shared library. It MAY offer them at several levels of
  fidelity, from abstract movers in the regionizer simulator to full clients in load tests.
- Bot aim and reaction MUST be modeled with seeded, human-like error and delay. Bots with perfect
  aim or instant reactions MUST NOT be used to measure gameplay metrics such as time to kill.

### Test first

- Where these standards apply, tests MUST be written before implementation and traced to
  acceptance criteria before implementation begins. Implementation proceeds in red-to-green
  cycles. A non-compiling test MAY be commented out temporarily, with a clear path to restoring it.

### Theories

- Tests that check the same behavior across inputs, entity classes, roles, pagination boundaries,
  authorization states, tick rates, validation cases or error cases MUST use xUnit Theories with
  explicit data (`InlineData`, `MemberData` or `ClassData`).
- Copy-pasted Facts for data variations are prohibited, unless the test documents why the cases
  need materially different setup, behavior or assertions.

### Completion gate

A feature or task is not complete until all of the following pass, locally or in CI:

- unit, simulation, repository, business-process, contract and integration tests;
- zero-allocation tests for tick-path code;
- web-surface frontend suites (Jasmine and Karma, Angular Testing Library, Playwright, Cypress,
  Storybook visual regression) and game-client tests, where applicable;
- the coverage, Sonar and other CI gates.

## Development Workflow

- Spec Kit drives feature work: `/speckit-specify`, optionally `/speckit-clarify`, `/speckit-plan`,
  `/speckit-tasks`, optionally `/speckit-analyze`, then `/speckit-implement`. The project template
  overrides in `.specify/templates/overrides/` carry this constitution's gates.
- `/speckit-specify` creates a numbered feature branch (git extension) and its `specs/<feature>/`
  directory. That branch carries the feature's spec artifacts and its pull request.
- Pull requests SHOULD be focused vertical slices that deliver independently testable increments.
  Horizontal pull requests are allowed for cross-cutting infrastructure, quality, security or
  refactoring work. Broad mixed-scope pull requests MUST be split or explicitly justified.
- A pull request that changes tuning values or gameplay content parameters MUST cite the run IDs
  behind each change, or state that a content value is a design judgment. It MUST be merged only
  after a human maintainer approves it, and scheduled tasks and agents MUST NOT merge it.
  Experiment-driven value changes SHOULD land separately from code changes.
- Code review MUST verify that every acceptance scenario and Independent Test in the changed
  `spec.md` maps to a specific automated test that faithfully exercises its Given, When and Then,
  denial and failure scenarios included. `CLAUDE.md` details the review procedure.
- Once Repowise indexing is configured for this repository, pull requests MUST include regenerated
  or confirmed-unchanged Repowise outputs in Repowise-maintained regions.

## Governance

- This constitution supersedes all other practices. The order of precedence is this constitution,
  then the architecture spec (`docs/architecture/server-architecture.md`), then feature specs and
  plans. When a lower document conflicts with a higher one, fix the lower document or amend the
  higher one; a conflict MUST NOT be left standing.
- Amendments MUST be reviewed and approved like an architectural decision record that affects the
  whole project. Each amendment MUST document its rationale, its author and its version bump under
  semantic versioning:
  - **MAJOR**: breaking governance changes, or the removal or redefinition of a non-negotiable
    rule.
  - **MINOR**: a new principle or section, materially expanded guidance, or settling an open
    decision.
  - **PATCH**: clarifications, wording, typo fixes, non-semantic refinements, and adding a named
    package to the approved license exceptions.
- Amendments are made with `/speckit-constitution`. While an amendment is under review, the file
  carries a Sync Impact Report (an HTML comment at the top) listing the version change, the changed
  principles and follow-up items.
- Compliance: `/speckit-plan` evaluates the Constitution Check gates before research and again
  after design; `/speckit-analyze` treats constitution conflicts as CRITICAL; code review verifies
  compliance. A gate violation MUST be justified in the plan's Complexity Tracking table.
- Prior versions are retained in version control history.
- Runtime development guidance for agents lives in `CLAUDE.md`.

**Version**: 1.1.0 | **Ratified**: 2026-10-28 | **Last Amended**: 2026-10-31
