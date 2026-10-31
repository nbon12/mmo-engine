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

**Language/Version**: [e.g., C# / .NET (version pinned in global.json), TypeScript 5.x or NEEDS CLARIFICATION]

**Primary Dependencies**: [e.g., Orleans, Npgsql/EF Core, FastEndpoints, Angular — each with its license or NEEDS CLARIFICATION]

**Storage**: [if applicable, e.g., game database (PostgreSQL via the persistence tier), a web surface's own PostgreSQL database, R2/MinIO, or N/A]

**Testing**: [e.g., xUnit + .NET Testcontainers, simulation harness, BenchmarkDotNet, Jasmine/Karma, Playwright, Cypress or NEEDS CLARIFICATION]

**Target Platform**: [e.g., Linux container on a dedicated 16+ core node, Cloud Run, browser or NEEDS CLARIFICATION]

**Project Type**: [e.g., continent server, persistence tier, gateway, browser game client, web surface, regionizer simulator, tooling or NEEDS CLARIFICATION]

**Components Touched**: [browser client / gateway / continent server / persistence tier / PostgreSQL / Agones and Kubernetes / web surface]

**Roadmap Phase**: [simulator / Phase 1 (one region per server) / Phase 2 (regionizer parallelism) / web surface / other]

**Performance Goals**: [domain-specific, e.g., region tick within 33 ms at 30 Hz for N players, bytes per client per tick, p95 API latency or NEEDS CLARIFICATION]

**Constraints**: [domain-specific, e.g., zero per-tick allocations, no locks in gameplay code, no blocking I/O on tick threads or NEEDS CLARIFICATION]

**Scale/Scope**: [domain-specific, e.g., 2,000 players per continent, a 1,000-player fight, N web-surface users or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Confirm the plan satisfies the active MMO Engine Constitution. Mark each gate as passing, not
applicable (with a one-line reason), or a violation justified under Complexity Tracking.

- **Component and stack fit** (I, XII, Web Surfaces): The plan names every component it touches
  and uses the constitution's stack for each. A web-app-like feature is a web surface on the web
  stack (Angular on Cloudflare Pages, FastEndpoints, PostgreSQL on Neon, the shared ASP.NET Core
  Identity + JWT account identity, Cloud Run, Cloudflare, Docker Hub, Sentry, Swashbuckle in
  development only, R2/MinIO); the real-time path adopts no web hosting choices.
- **Region ownership and single writer** (II, III): Every new piece of state has a named owner
  (region, area actor, global region, grain or day-one service, or web-surface database). Gameplay
  code takes no locks. Cross-region effects go through the entity, location, global or area
  queues, and cross-region reads come only from snapshots. Where outcomes could depend on the
  order of work from different senders, a deterministic apply order is defined.
- **Interaction classes and claims** (III, Architecture Constraints): Each new interaction is
  classed instant, deferred or read-only. Any change to claims, claim radii or portals states its
  effect on bridging and on the largest region's share of players.
- **Regionizer invariants** (Architecture Constraints): Invariants 1–4 still hold, new region data
  defines its merge and split callbacks, and timers survive merges and splits.
- **Tick budget and performance** (VI): Tick-path cost is stated and labeled estimate or
  measurement (see Tick Budget and Evidence). The tick path makes no per-tick allocations, keeps
  structure-of-arrays data, and does no blocking I/O. Benchmarks and zero-allocation tests are
  planned, the degradation order is respected, and new metrics are listed.
- **Authority, netcode and trust** (IV): The server stays authoritative. Client input is validated
  for size, rate, ranges, sequencing and permission. Interest management bounds what clients learn.
  Lag compensation uses pose history at true timestamps.
- **Persistence and ownership** (V): Every live record has exactly one owner, and only the
  persistence tier writes the game database. Each data class has a write policy (immediate ledger,
  on change, batched, never persisted) and a concurrency token. Orleans transactions are not used.
  Migrations are strict, tested against PostgreSQL and applied idempotently once per rollout.
- **Contracts and protocols** (Engineering Standards): Wire-protocol messages come from a single
  source of truth, with version negotiation and golden fixtures. HTTP APIs document their envelope,
  error shape, pagination (default and maximum `limit`), authentication, authorization and
  cacheability. Types crossing deploy boundaries tolerate version skew.
- **Licensing and clean room** (VII): Every new dependency is MIT or Apache-2.0 or an approved
  exception, and no copyleft code is linked, copied or deployed. External design sources are
  recorded in `research.md`; Folia and AzerothCore are used through their documentation only.
- **Evidence and open decisions** (VI, Architecture Constraints): Tuning values come from the
  simulator or load tests, cite their run IDs, and live in validated configuration. Gameplay
  content parameters live in validated configuration too. Timing is labeled a measurement only
  when it comes from dedicated hardware over repeated runs. The plan does not silently settle an
  open decision, and any decision it needs is called out.
- **Experiments and bots** (VI, Experiments and automated search, Bots and scenarios): Each
  experiment states its viability gates and score, runs at least three seeds, and re-checks its
  winner on scenarios and seeds the search did not use. Algorithm candidates implement the shared
  interface and pass the full suite before they compete. Bots join as ordinary clients through the
  production protocol, with no bot-only server code, and use the shared scenario format and bot
  library with seeded, human-like aim and reaction. Gameplay changes headed for Production have a
  human playtest planned. Tuning and content pull requests cite run IDs and wait for human
  approval.
- **Operations** (Engineering Standards): Configuration is validated with FluentValidation and
  fails fast, including the environment name. Secrets are externalized. Serilog JSON logs, Sentry
  error capture, OpenTelemetry metrics and health reporting (Agones SDK or readiness endpoints) are
  planned. Production errors leak no system details.
- **Security** (Engineering Standards): Authentication is required, authorization is server-side
  and least-privilege, per-connection rate and size limits apply, sensitive events are logged,
  admin and moderation actions are auditable, and player-generated content is treated as untrusted.
- **Testing discipline** (VIII, Testing Standards): Tests are written first. Simulation tests are
  deterministic (time base, seeds, tick stepping). Parallel-equivalence tests cover any change to
  parallel phases. Persistence tests use PostgreSQL through Testcontainers, with transaction
  isolation or an approved equivalent named here. Tests are parallel- and rerun-safe, and data
  variations use xUnit Theories. Web-surface and game-client test tools are used where applicable.
- **CI/CD and documentation**: Sonar, Codecov (95% for relevant changed files), environment
  isolation and a `docs/patch-notes.md` entry are accounted for, plus a Repowise refresh once
  Repowise is configured.
- **Executable and living specs** (IX): Acceptance criteria are backed by runnable,
  currently-passing tests. `spec.md` stays truthful, with no drift, including older, already-merged
  specs. This feature's `spec.md` and `tasks.md` are slated to be updated before the PR; older specs
  only need their `spec.md` kept current, and `tasks.md`, `plan.md` and `research.md` are not
  refreshed. Any contradiction with a former spec is reconciled so the corpus stays consistent.
- **Spec independence and parallelism** (X): This spec is individually completable without
  another spec landing first. Any hard dependency on a sibling spec (a schema, protocol message,
  contract or shared component it must land after) is documented here and minimized. Where this
  feature was split from a larger effort, the split lets sibling specs proceed in parallel.
- **Incremental delivery** (XI): The plan fits the current roadmap phase (simulator first; Phase 1
  runs one region per server with region-ready code) and delivers a focused vertical slice, or a
  justified cross-cutting change.

## Tick Budget and Evidence

> Fill when the feature adds or changes tick-path work, Delivery work or a tuning value. Otherwise
> write "N/A: no tick-path, Delivery or tuning changes."

| Work item or tuning value | Estimate or measurement | Source (run IDs, benchmark, or "estimate") | Share of the region tick budget |
|---------------------------|-------------------------|--------------------------------------------|---------------------------------|
| [e.g., movement for N players] | [estimate: X ms] | [estimate] | [X% of 33 ms] |

- **Allocations**: [how the tick path stays allocation-free, and which zero-allocation tests prove it]
- **Degradation**: [how the feature behaves when its region tightens Delivery caps or drops to 20 Hz]
- **Metrics**: [engine and gameplay metrics added or affected, and their alerts]

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
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
# [REMOVE IF UNUSED] Option 1: Single project (DEFAULT)
src/
├── models/
├── services/
├── cli/
└── lib/

tests/
├── contract/
├── integration/
└── unit/

# [REMOVE IF UNUSED] Option 2: Web application (when "frontend" + "backend" detected)
backend/
├── src/
│   ├── models/
│   ├── services/
│   └── api/
└── tests/

frontend/
├── src/
│   ├── components/
│   ├── pages/
│   └── services/
└── tests/

# [REMOVE IF UNUSED] Option 3: Mobile + API (when "iOS/Android" detected)
api/
└── [same as backend above]

ios/ or android/
└── [platform-specific structure: feature modules, UI flows, platform tests]
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
