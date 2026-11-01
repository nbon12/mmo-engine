# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: [e.g., C# / .NET (pinned in global.json), TypeScript 5.x, Rust or Go for the edge transport, or NEEDS CLARIFICATION]

**Primary Dependencies**: [e.g., Babylon.js, BepuPhysics2, Npgsql/EF Core, FastEndpoints, Angular — each with its license, or NEEDS CLARIFICATION]

**Storage**: [if applicable, e.g., game database (PostgreSQL, written only by the progression tier), ClickHouse, a web surface's own PostgreSQL database, R2, or N/A]

**Testing**: [e.g., xUnit v3 + .NET Testcontainers, simulation and replay harness, BenchmarkDotNet, network-impaired bot runs, Playwright, Jasmine/Karma, Cypress, or NEEDS CLARIFICATION]

**Target Platform**: [e.g., dedicated 64–96+ core continent host, edge node, Kubernetes Deployment, Cloud Run, browser (WebGPU/WebGL2), or NEEDS CLARIFICATION]

**Project Type**: [e.g., continent server, shared simulation core, edge tier, gateway, public stats API, progression tier, browser game client, level editor, web surface, regionizer simulator, harness or tooling, or NEEDS CLARIFICATION]

**Components Touched**: [browser client / edge tier / gateway / public stats API / continent server / shared core / progression tier / ClickHouse / PostgreSQL / orchestration / level data / web surface]

**Roadmap Phase**: [Pre-Phase 1 / Roadmap Phase 1–5, and the gates this plan depends on]

**Performance Goals**: [domain-specific, e.g., region tick p99 within 33 ms at 30 Hz for N players, bytes per client per second, frame time on the dense-fight scene, or NEEDS CLARIFICATION]

**Constraints**: [domain-specific, e.g., zero per-tick allocations, no locks in gameplay code, per-client cost flat with fight size, or NEEDS CLARIFICATION]

**Scale/Scope**: [domain-specific, e.g., 5,000 players per continent, the target fight size, N web-surface users, or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Confirm the plan satisfies the active MMO Engine Constitution. Mark each gate as passing, not
applicable (with a one-line reason), or, for SHOULD-level items only, a departure justified under
Complexity Tracking. A MUST-level violation blocks the plan until the constitution is amended.

- **Scope, roadmap and flows** (Purpose and Scope, XII, Architecture Constraints): No work toward a
  Non-goal. The plan names its roadmap phase and depends only on gates that have passed. Every
  roadmap proposed change or open question this feature depends on has a recorded decision
  (constitution, Governance, Roadmap decisions). Every reference flow it touches conforms, or the
  plan names the amendment it needs.
- **Component and stack fit** (I, II, XIII, Web Surfaces): The plan names every component it touches
  and uses each component's stack. A web-app-like feature is a web surface on the web stack (Angular on
  Cloudflare Pages, FastEndpoints, PostgreSQL on Neon, Cloud Run, Cloudflare, Docker Hub, Sentry,
  Swashbuckle in development only, R2), with a stateless API and a UI that works at phone, tablet and
  desktop widths with keyboard-accessible forms, navigation and dialogs. It uses the shared account
  identity rather than its own. The game client and game backend adopt no web-surface hosting
  choices such as scale-to-zero or tolerated cold starts.
- **Region ownership and single writer** (III, IV): Every new piece of state has a named owner
  (region, area actor, global region, progression-tier owner, or a web surface's database). Gameplay
  code takes no locks, starts no work outside the tick, reads no clock but the injected one and uses
  no randomness but the project's seeded PRNG. Analyzers on gameplay and shared-core assemblies
  reject locks and blocking waits, async work and timers, every clock read and every randomness
  source except the project PRNG, unordered-collection enumeration, and mutable static state; the
  regionizer gets the clock, randomness and unordered-enumeration analyzers. Cross-region effects go
  through the entity, location, global or area queues, and cross-region reads come only from
  snapshots. Where outcomes could depend on the order of work from different senders, a
  deterministic apply order is defined.
- **Interaction classes, claims and the regionizer** (I, IV, Architecture Constraints): Each new
  interaction is classed instant, deferred or read-only. Any change to claims, claim radii, section
  size, the merge gap or portals states its effect on bridging and on the largest region's share of
  players. The regionizer invariants still hold, new region data defines its merge and split
  callbacks, and timers kept in game time survive merges, splits and tick-rate changes. A region
  waits on another only where Principle I allows: while merging into it, in the regionizer's
  serialized tick-end section (measured against the bookkeeping budget), or for a free tick thread.
- **Time, identity and teleports** (Architecture Constraints): Gameplay code reads time only from the
  injected continent time base. Every spawn, login, redeploy and Instant Action jump uses the two-step
  teleport, and moves between continents use the continent-transfer flow. Simulation entity IDs stay
  stable for the entity's lifetime, and player sessions stay independent of the worker simulating them.
- **Delivery and per-client cost** (II): Every per-client cost the feature adds is capped (attention
  sets, capped candidate sets, priority scoring within the byte budget and the per-tick scoring cap,
  rate ceilings, crowd summaries), Delivery work stays off the simulation threads, and the edge
  metrics cover it.
- **Tick budget and performance** (VII): Tick-path cost is stated and labeled estimate or measurement
  (see Tick Budget and Evidence). The tick path makes no per-tick allocations, keeps
  structure-of-arrays data, and does no blocking I/O. Benchmarks and zero-allocation tests are planned,
  the degradation order is respected, and new metrics are listed.
- **Authority, netcode and trust** (V): The server stays authoritative. Clients predict their own
  movement and weapons only with the shared simulation core; anything else they show between updates
  is presentation-only extrapolation that server state replaces. Hit claims, allowed only for
  single-target, non-homing projectiles whose flight the shared core fully determines, are replayed
  by the target's owning region under the claim rules (rewind cap, render-time check, line of fire
  against static geometry and blocking entities, sent-entity check against the individually sent
  view, staleness widening capped at the staleness limit, seed rule). Client input is validated for
  size, rate, ranges, sequencing and permission. Each client is sent nothing that game rules hide
  from its player, and no field it does not display for that relationship. Those filters are tested
  like authorization rules.
- **Persistence and ownership** (VI): Every live record has exactly one owner, and only the
  progression tier writes the game database; direct readers (web-surface pages and the public stats
  API) use a read-only role on published views, for display only. Each data class has a write policy
  (immediate ledger, on change, batched, never persisted). Currency, earned currency included, is
  written immediately as ledger rows and never batched with stats. Ledger rows carry idempotency
  keys (a repeated key returns the original result; a repeated key with a different request is
  rejected, and a write carrying an older continent epoch is rejected even when its key exists),
  progression batches apply at most once, and balance checks run inside the writing
  transaction. Mutable rows use the `xmin` concurrency check. Orleans transactions are not used.
  Migrations are strict, tested against PostgreSQL, expand-migrate-contract, and applied once per
  rollout over a direct connection.
- **Shared core, transport, hosting and failure** (I, II, Architecture Constraints): Shared-core
  changes keep the server and WebAssembly builds hash-identical on the agreement suite, and
  agreement tests are planned. Transport changes detect datagram support at runtime and keep the
  WebSocket fallback tested in CI. Continent-server changes catch tick exceptions at the tick
  boundary and quarantine the offender, keep published snapshots immutable while read, and fence
  checkpoints, batches and ledger writes by continent epoch. In Staging, Production and load tests
  each continent has a dedicated host that runs no other application workload except its own
  Delivery and edge transport processes, until they move to edge nodes. From Roadmap Phase 3
  (sub-spec 15) it runs there as an Agones GameServer, and in Staging and Production as a
  Guaranteed QoS pod with whole-CPU requests under the static CPU manager policy. No continent pod
  runs under a fractional CPU limit, in any environment. Gates 9 and 12 run before the fleet
  exists, on the bare target host class, and the Roadmap Phase 3 gate re-runs gate 12's continent
  scenario under Agones and compares tick p99 with those bare numbers. A continent server reports
  healthy only while the scheduler, the global region and every region make progress within the
  configured stall deadline, set well above the clean-degradation ceiling, so overload alone never
  marks it unhealthy. A client's first plan sets a frame-time target for the dense-fight scene on
  stated hardware.
- **Contracts and protocols** (Engineering Standards): Wire-protocol messages come from a single
  source of truth, with version negotiation and golden fixtures on every side. HTTP APIs document their
  envelope, error shape with stable error codes, global exception handling, pagination (default and
  maximum `limit`), authentication, authorization and cacheability. Every build stays compatible with
  the oldest continent-server build still allocated.
- **Licensing and clean room** (VIII): Every new shipped dependency, direct or transitive, is MIT,
  Apache-2.0, BSD-2-Clause, BSD-3-Clause or 0BSD, or an approved exception, and the CI license scan
  covers it; build- and test-only libraries carry a permissive license. A dependency outside a
  package manager (vendored source, a git submodule or a native library) is listed in the
  checked-in third-party manifest that the scan reads. No copyleft code is linked, vendored,
  copied, translated or modified, except platform components (VIII) linked or run unmodified from
  upstream; copyleft tools run only as unmodified separate processes or services. External design sources are recorded in `research.md`:
  Folia through its prose documentation only, and AzerothCore only through the architecture spec's
  description.
- **Evidence and open decisions** (VII, Architecture Constraints): Values the simulator measures
  (claim radius per class, section size, claim anchoring, the merge gap, minimum base spacing, the
  lane bridging limit, split damping, the air layer and the target fight size) come from a recorded
  simulator run before this feature depends on them. Values that need a running server come from
  load tests or, until a load test can measure them, from the constitution's starting value or a
  labeled estimate. Each records its source and lives in validated configuration. The plan does not
  silently settle one of the constitution's numbered open decisions (Architecture Constraints, Open
  decisions), and any such decision it needs is called out.
- **Operations** (Engineering Standards): Configuration is validated with FluentValidation and fails
  fast, including the environment name, and browser apps fail loudly at boot on missing or invalid
  configuration. Secrets are externalized, and signing keys are rotatable without downtime and
  rotated on a stated schedule. Serilog JSON logs, OpenTelemetry and Prometheus metrics, and
  ClickHouse events are planned; tick code logs, reports errors and records metrics only through
  per-region buffers. Sentry covers web surfaces and the browser client, with performance spans and
  trace context for web surfaces. Continent servers report health through the Agones SDK as progress
  within the stall deadline (Shared core, transport, hosting and failure), and other services expose
  health and readiness endpoints. Production errors leak no system details.
- **Security** (Engineering Standards): Authentication is required and goes through the one account
  identity (our own code on ASP.NET Core Identity, with redirect sign-in, never a popup).
  Authorization is server-side and least-privilege, with cross-scope access denied by default. The
  plan follows the token rules (access tokens expire within 15 minutes and never appear in URLs),
  the join-token rules (signed by the gateway or, until it ships, by a development signer disabled
  in Staging and Production; sent in the connection's first message, never in a URL; bound to one
  edge and accepted once), and the staff-account, audit and service-to-service rules. It covers DDoS
  protection (Cloudflare for web surfaces, the gateway and the public stats API; provider network
  protection for edges), connection and message limits, rate limits, the Content Security Policy,
  safe rendering of player-generated content, and the personal-data (pseudonymous player IDs in
  ClickHouse), retention and backup rules.
- **Caching, edge and files** (Web Surfaces): Responses are cached only when explicitly safe, and
  authenticated or user-specific responses are not edge-cached unless keyed and justified here. Static
  assets use hashed filenames. File payloads go to R2 (an S3-compatible test server locally and in CI),
  and PostgreSQL holds only metadata, ownership and references.
- **Levels** (Levels and Game Content): Level changes use the documented data format, respect lock
  states (the lock check fails an agent change to a locked object), ship with a preview and
  validation report, and pass level validation.
- **Testing discipline** (IX, Testing Standards): Tests are written first. New logic has unit or
  simulation tests, and persistence and external integrations have integration tests. Simulation is
  deterministic and replayable, with state hashes, and parallel-equivalence tests cover any change
  to parallel phases. Invariant monitors run. The isolation approach is named (a rolled-back
  transaction or an approved equivalent). Deterministic tests are parallel- and rerun-safe; load,
  stress, soak and network-impaired runs are statistical and judged by pass rules with minimum
  sample counts. Data variations use xUnit Theories or table-driven tests. Affected pass rules are
  listed, and a threshold or baseline changes only through a reviewed commit that names the source
  run, in a pull request that changes nothing else. No test is weakened, skipped or special-cased,
  no commented-out or compile-excluded test merges, and a test is quarantined only under the
  quarantine rules (evidence that it passed and failed on the same commit, in a pull request of its
  own). Web-surface and game-client test tools are used where applicable.
<!-- guard -->
- **Autonomy and decision rights** (XIV): No escalation-list item proceeds without the director, and
  any one-way-door path the plan touches is covered by CODEOWNERS (Principle XIV lists the paths),
  and on a pull request the director's account opened, held by the guard check until the director's
  label. A
  change the guard check holds waits for the director: in the constitution, `CLAUDE.md`,
  `.specify/prompts/**` or the template overrides, any change to Principle XIV, the escalation
  list or the Precedence, Roadmap decisions, Amendments, Versioning and Compliance rules (outside
  the constitution and its scaffold, any change between the guard marker lines around the
  passages that restate them, or to those lines), any other deletion or rewrite in Governance
  except the version line, or a deletion or rewrite of a sentence containing MUST, MUST NOT or
  NON-NEGOTIABLE.
  Gate-defining files (coverage configuration, license allowlist, pass-rule thresholds and
  baselines, analyzer configuration, the test quarantine list) change only in a pull request that
  changes nothing else. Nothing sensitive enters the public repository (secrets, tokens, player
  data, IP addresses, unredacted Staging or Production output), and a suspected vulnerability is
  written only in a draft repository security advisory. Decisions are recorded where their readers
  look (`design/decisions.md`, `research.md`, a roadmap phase spec's Decision column, the pull
  request, patch notes). Open design questions are answered from the creative direction and
  recorded, not asked.
<!-- /guard -->
- **CI/CD and documentation**: Required checks, Sonar, Codecov (95% for changed files under the
  checked-in coverage configuration), the license scan, environment isolation and a
  `docs/patch-notes.md` entry are accounted for, plus a Repowise refresh once Repowise is configured.
  Any new required workflow runs on every pull request with no path filters and fails closed.
- **Executable and living specs** (X): Acceptance criteria are backed by runnable, currently-passing
  tests, and load-backed criteria by an on-demand run on representative hardware against the pull
  request's head (or an earlier commit of it when later commits change none of the code under test).
  Until the director approves that hardware, a load-backed criterion is marked `pending-hardware`
  and blocks its roadmap gate, not the merge. `spec.md` stays truthful, with no drift, including
  older, already-merged specs. This feature's `spec.md` and `tasks.md` are slated to be updated
  before the PR; older specs only need their `spec.md` kept current, and `tasks.md`, `plan.md` and
  `research.md` are not refreshed. Any contradiction with a former spec is reconciled so the corpus
  stays consistent.
- **Spec independence and parallelism** (XI): This spec is individually completable without another
  spec landing first. Any hard dependency on a sibling spec (a schema, protocol message, contract or
  shared component it must land after) is documented here and in the sibling spec's `plan.md`, and
  minimized. Where this feature was split from a larger effort, the split lets sibling specs proceed in
  parallel.
- **Incremental delivery** (XII): The plan fits its roadmap phase (Roadmap Phase 1 code already follows
  the region model) and delivers a focused vertical slice, or a justified cross-cutting change.

## Tick Budget and Evidence

> Fill when the feature adds or changes tick-path work, Delivery work or a tuning value. Otherwise
> write "N/A: no tick-path, Delivery or tuning changes."

| Work item or tuning value | Estimate or measurement | Source (simulator run, load test, benchmark, starting value, or "estimate") | Share of the budget |
|---------------------------|-------------------------|-----------------------------------------------------------------------------|---------------------|
| [e.g., movement for N players] | [estimate: X ms] | [estimate] | [X% of the 33 ms region tick] |
| [e.g., view building per client] | [estimate: X µs] | [estimate] | [X% of edge CPU per client] |

- **Allocations**: [how the tick path stays allocation-free, and which zero-allocation tests prove it]
- **Per-client cost**: [bytes per client per second and edge CPU per client, against the budget]
- **Degradation**: [how the feature behaves when its region's viewers get smaller budgets or the region drops to 20 Hz]
- **Pass rules and metrics**: [pass rules affected, and metrics added with their alerts]

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. The tree shows this repository's conventions, the same as
  the tasks template's Path Conventions; every feature follows them rather than
  inventing a layout. Delete the directories the feature does not touch, replace
  [Project], [Core] and [surface] with real names, and expand the rest with real
  paths. The delivered plan must not include these placeholders.
-->

```text
src/
├── [Project]/              # .NET projects: continent server, gateway, progression tier
└── [Core]/                 # shared simulation core, built for the server and for WebAssembly

tests/
├── [Project].Tests/        # Simulation/, Integration/, Contract/ and Performance/ tests
└── [Project].Benchmarks/   # BenchmarkDotNet benchmarks

edge/                       # edge transport (Rust or Go)

client/
├── src/                    # browser game client (TypeScript + Babylon.js)
└── tests/

web/
└── [surface]/              # web surface: Angular frontend and FastEndpoints backend
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY for justified departures from SHOULD-level items. A MUST-level violation cannot be justified here.**

| Departure | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., a SHOULD-level item not followed] | [current need] | [why the default was insufficient] |
