---

description: "Task list template for feature implementation"
---

# Tasks: [FEATURE NAME]

**Input**: Design documents from `/specs/[###-feature-name]/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are REQUIRED. Per the constitution (Principles IX and X), every acceptance scenario
and functional requirement maps to an automated test, tests are written first and must fail before
implementation, and the completion gate requires every applicable suite to pass.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **.NET projects**: `src/[Project]/`, `tests/[Project].Tests/`, `tests/[Project].Benchmarks/`
- **Shared simulation core**: `src/[Core]/` (built for the server and for WebAssembly)
- **Edge transport** (Rust or Go): `edge/`
- **Browser game client**: `client/src/`, `client/tests/`
- **Web surfaces**: `web/[surface]/` (Angular frontend and FastEndpoints backend)
- Paths shown below are illustrative - use the real structure recorded in plan.md

<!--
  ============================================================================
  IMPORTANT: The tasks below are SAMPLE TASKS for illustration purposes only.

  The /speckit-tasks command MUST replace these with actual tasks based on:
  - User stories from spec.md (with their priorities P1, P2, P3...)
  - Feature requirements from plan.md
  - Entities from data-model.md
  - Endpoints and protocol messages from contracts/

  Tasks MUST be organized by user story so each story can be:
  - Implemented independently
  - Tested independently
  - Delivered as an MVP increment

  Setup and Foundational tasks apply only where the repository does not already
  provide them - check before adding.

  DO NOT keep these sample tasks in the generated tasks.md file.
  ============================================================================
-->

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

<!-- guard -->
**Gate-defining files**: a task that creates or changes the coverage configuration, license
allowlist, pass-rule thresholds and baselines, analyzer configuration or test quarantine list (such
as T004 or T019) lands in a pull request that changes nothing else (Principle XIV).
<!-- /guard -->

- [ ] T001 Create project structure per implementation plan
- [ ] T002 Initialize the .NET solution (SDK pinned in global.json) and/or the TypeScript, Rust or Go projects named in plan.md
- [ ] T003 [P] Configure linting and formatting tools
- [ ] T004 [P] Configure CI analyzers for gameplay and shared-core assemblies that reject the `lock`
      statement (custom analyzer), `System.Threading.Lock`, `Monitor`, mutexes, semaphores and
      blocking waits; `async`/`await`, `Task.Run`, `ThreadPool`, `Parallel` and timers; every clock
      read (`DateTime.Now`, `UtcNow` and `Today`, `DateTimeOffset.Now` and `UtcNow`,
      `TimeProvider.System`, `Stopwatch`, `Environment.TickCount` and `TickCount64`); every
      randomness source except the project's own seeded PRNG (`System.Random` in any form, seeded
      or not, `Random.Shared` included, `RandomNumberGenerator`, `Guid.NewGuid()`); enumeration of
      `Dictionary`, `HashSet` and other unordered collections; and mutable static state
      (non-readonly static fields, and static collections or objects that gameplay code mutates;
      static readonly immutable data is allowed). Give the regionizer only the clock, randomness
      and unordered-enumeration analyzers
- [ ] T005 [P] Configure xUnit v3 and .NET Testcontainers for test projects, with test parallelization enabled
- [ ] T006 [P] Web surfaces only: configure Jasmine/Karma, Angular Testing Library, Playwright, Cypress and Storybook

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

Examples of foundational tasks (adjust based on your project):

- [ ] T007 Setup strict PostgreSQL migrations: tested against PostgreSQL, expand-migrate-contract, applied once per rollout over a direct connection with a concurrency guard, Orleans system tables included
- [ ] T008 [P] Configure FluentValidation-validated options: fail-fast startup and environment-name validation (`Development`, `Dev`, `Test`, `Staging`, `Production`); add the browser boot-time configuration guard
- [ ] T009 [P] Configure Serilog structured JSON logging, OpenTelemetry metrics scraped by
      Prometheus, ClickHouse event tables that identify players only by pseudonymous IDs, and
      test-metric tables keyed by run ID
- [ ] T010 [P] Provide per-region buffers through which tick code records logs, errors and metrics, drained off the tick threads with deduplication and rate limits
- [ ] T011 [P] Configure Sentry for web surfaces and the browser client, with environment and release tags, sensitive-data filtering, and performance spans and trace context for web surfaces
- [ ] T012 [P] Configure health reporting: the Agones SDK for continent servers, sending Health()
      pings only while the scheduler, the global region and every region make progress within a
      configured stall deadline, set well above the clean-degradation ceiling (no tick over 100 ms
      at 20 Hz), so an overloaded fight degrades instead of failing the server; readiness endpoints
      elsewhere
- [ ] T013 Configure local parity: Docker Compose with real PostgreSQL and ClickHouse, kind or k3d with Agones, and the Agones SDK server in local mode
- [ ] T014 [P] Configure secret management and signing keys that are rotatable without downtime,
      rotated on a stated schedule and published with key IDs; keep secrets out of images and
      committed configuration
- [ ] T015 [P] Provide the deterministic simulation harness: injected clock, randomness only from
      the project's seeded PRNG split into named streams, fixed effect order, iteration in ID order,
      test-driven tick stepping, invariant monitors, state hashes, golden replays and a pinned SIMD
      vector width
- [ ] T016 [P] Provide the wire-protocol single source of truth and the shared golden-fixture harness for the C#, TypeScript and edge sides
- [ ] T017 [P] Implement authentication through the account identity (Engineering Standards,
      Security): our own code on ASP.NET Core Identity, redirect sign-in (OpenID Connect as the
      Phase 3 identity spike chooses), JWTs validated for algorithm, issuer, audience and lifetime,
      access tokens that expire within 15 minutes, rotating hashed refresh tokens with reuse
      detection, and signed one-time join tokens bound to one edge and sent in the connection's
      first message, never in a URL (until the gateway ships, from a development signer disabled in
      Staging and Production); sign-in, token refresh and join-token issuance reject suspended or
      banned accounts
- [ ] T018 [P] HTTP APIs: set up FastEndpoints, the global exception handler, the error shape with
      stable error codes, and Swashbuckle at `/swagger` in development only (web surfaces and the
      gateway; the public stats API publishes its versioned OpenAPI document in production)
<!-- guard -->
- [ ] T019 [P] Configure GitHub Actions required workflows that run on every pull request with no
      path filters and fail closed (a check that cannot run, an untrusted author or an unparseable
      verdict fails and never skips): lint, tests, Sonar scan, Codecov upload, the 95% changed-file
      coverage gate, the license scan, the review agent's verdict (its job reads the review prompt,
      `CLAUDE.md` and the constitution from the base branch), the guard check (T020), the level
      lock check, and a `hold` check that fails while the pull request, or another one from the
      same head branch, has the `hold` label or the director's latest review other than a comment
      requesting changes (a reply in a review thread does not release it); while another pull
      request from that head branch stays closed after the director closed it, until the director
      approves this one; and when anyone but the director made the label's last removal,
      dismissed that review or reopened the pull request after the director closed it. The guard,
      level lock and `hold` checks run code and
      settings kept under `.github/**` from the base branch, match the `hold` and
      `director-approved` labels by the label IDs kept there (failing while either is missing or
      renamed), and also run on `labeled` and `unlabeled` events and on submitted and dismissed
      reviews, which the default `pull_request` event types miss. Add a "checks" ruleset (pull request plus these required checks, no bypass
      actors). Agent jobs act only as the Claude GitHub App or a dedicated bot account, start only
      for events by the director or the project's own agents, receive only content those
      identities wrote (the workflow fetches the issue or pull request thread and filters it by
      author), and never run on fork pull requests; no workflow uses `pull_request_target` with a
      checkout of the pull request's head
- [ ] T020 [P] Configure CODEOWNERS so one-way-door paths need the director's review (pricing and
      store catalog data, legal text, player-data migrations and scripts, CI and deployment
      configuration, secrets configuration, `.github/**` including CODEOWNERS and workflows,
      `.claude/**`, `docs/operations/ai-organization.md`, `design/creative-direction.md`,
      `design/style-guide.md` and `design/feedback/**`), enforced by an "owners" ruleset
      (code-owner review, with stale approvals dismissed when new commits are pushed) whose only
      bypass actor is the repository-admin role in pull-request-only mode. A pull request the
      director's account opened merges through that bypass, so the guard check also holds its
      changes to those paths until the director's label. Add the required guard
      check on `.specify/memory/constitution.md`, `CLAUDE.md`, `.specify/prompts/**` and
      `.specify/templates/overrides/**`: until the director approves, it fails any change,
      additions included, to Principle XIV, the escalation list or the Precedence, Roadmap
      decisions, Amendments, Versioning and Compliance rules (found by heading in the constitution
      and its scaffold, and elsewhere as any change between the guard marker lines around the
      passages that restate them, or to those lines), any other deletion or rewrite in Governance
      except the version line, and any deletion or rewrite of a sentence containing MUST, MUST NOT
      or NON-NEGOTIABLE, and lets other changes pass. It accepts as the director's approval an
      approving review by the director of the head commit or, on a pull request the director's
      account opened, the `director-approved` label that account added after the latest push (a run
      that a push starts ignores the label, and a push's time is GitHub's record of it, such as the
      creation of the workflow run it started, never a commit date, which the pusher sets). A
      workflow removes that label on every push, and a PreToolUse hook in `.claude/settings.json`
      rejects any command or GitHub tool call that adds it, approves a pull request, merges through
      a ruleset bypass, removes the `hold` label, renames or deletes either label, dismisses a
      review or reopens a pull request the director closed
<!-- /guard -->
- [ ] T021 [P] Configure environment separation for Dev, Staging and Production (databases, Agones
      fleets, Deployments, Cloud Run services, secrets) as each one is hosted (until Dev is hosted,
      Local and CI stand in for it), and keep Dev and Staging unreachable by the public without
      the director's approval
- [ ] T022 [P] Configure DDoS protection, connection limits and rate limits: Cloudflare for web
      surfaces, the gateway and the public stats API; the edge tier with provider network
      protection for game traffic
- [ ] T023 [P] Web surfaces only: configure Neon connection limits, pooling and short-lived DbContext lifetimes

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - [Title] (Priority: P1) 🎯 MVP

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T024 [P] [US1] xUnit Theory simulation test for [behavior] with a fixed clock and seed and the invariant monitors on, in tests/[Project].Tests/Simulation/[Name]Tests.cs
- [ ] T025 [P] [US1] xUnit Theory + Testcontainers integration test for [user journey], covering authorization and data variations, isolated by a rolled-back transaction or unique per-test keys, parallel- and rerun-safe, in tests/[Project].Tests/Integration/[Name]Tests.cs
- [ ] T026 [P] [US1] Contract test for [protocol message or endpoint] against the shared golden fixtures, in tests/[Project].Tests/Contract/[Name]Tests.cs and client/tests/contract/[name].test.ts
- [ ] T027 [P] [US1] Zero-allocation test for [tick-path code] in
      tests/[Project].Tests/Performance/[Name]AllocationTests.cs (Release, runs in CI), and a
      BenchmarkDotNet benchmark in tests/[Project].Benchmarks/[Name]Benchmarks.cs with its results
      attached to the PR
- [ ] T028 [P] [US1] Parallel-equivalence test (N threads vs. one thread, pinned snapshot versions) for [parallel-phase change] in tests/[Project].Tests/Simulation/[Name]ParallelEquivalenceTests.cs
- [ ] T029 [P] [US1] Golden replay or network-impaired bot run, with its pass rule as a ClickHouse
      query that states a minimum sample count, for [netcode or smoothness behavior]
- [ ] T030 [P] [US1] Web surfaces only: Angular Testing Library component test, Cypress E2E test, and Storybook story with a visual regression case for [component or journey]

### Implementation for User Story 1

- [ ] T031 [P] [US1] Create [Entity1] in src/[Project]/[Path]/[Entity1].cs
- [ ] T032 [P] [US1] Create [Entity2] in src/[Project]/[Path]/[Entity2].cs
- [ ] T033 [US1] Implement [system, owner or service] in src/[Project]/[Path]/[Name].cs (depends on T031, T032)
- [ ] T034 [US1] Implement [protocol handler, FastEndpoint or queue message] for [feature] in src/[Project]/[Path]/[Name].cs
- [ ] T035 [US1] Add client-input validation (size, rate, value ranges, sequencing and permission), error handling and consistent response shapes
- [ ] T036 [US1] Add structured logs, security and audit events, and metrics for user story 1 operations

**Checkpoint**: At this point, User Story 1 should be fully functional and testable independently

---

## Phase 4: User Story 2 - [Title] (Priority: P2)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 2 ⚠️

- [ ] T037 [P] [US2] xUnit Theory simulation or contract test for [behavior] in tests/[Project].Tests/[Area]/[Name]Tests.cs
- [ ] T038 [P] [US2] xUnit Theory + Testcontainers integration test for [user journey] with parallel- and rerun-safe setup in tests/[Project].Tests/Integration/[Name]Tests.cs

### Implementation for User Story 2

- [ ] T039 [P] [US2] Create [Entity] in src/[Project]/[Path]/[Entity].cs
- [ ] T040 [US2] Implement [system, owner or service] in src/[Project]/[Path]/[Name].cs
- [ ] T041 [US2] Implement [protocol handler, FastEndpoint or queue message] for [feature] in src/[Project]/[Path]/[Name].cs
- [ ] T042 [US2] Integrate with User Story 1 components (if needed)

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently

---

## Phase 5: User Story 3 - [Title] (Priority: P3)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 3 ⚠️

- [ ] T043 [P] [US3] xUnit Theory simulation or contract test for [behavior] in tests/[Project].Tests/[Area]/[Name]Tests.cs
- [ ] T044 [P] [US3] xUnit Theory + Testcontainers integration test for [user journey] with parallel- and rerun-safe setup in tests/[Project].Tests/Integration/[Name]Tests.cs

### Implementation for User Story 3

- [ ] T045 [P] [US3] Create [Entity] in src/[Project]/[Path]/[Entity].cs
- [ ] T046 [US3] Implement [system, owner or service] in src/[Project]/[Path]/[Name].cs
- [ ] T047 [US3] Implement [protocol handler, FastEndpoint or queue message] for [feature] in src/[Project]/[Path]/[Name].cs

**Checkpoint**: All user stories should now be independently functional

---

[Add more user story phases as needed, following the same pattern]

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] TXXX [P] Documentation updates in docs/
- [ ] TXXX Code cleanup and refactoring
- [ ] TXXX Performance optimization across all stories
- [ ] TXXX Review that .NET tests use xUnit Theories and TypeScript tests use table-driven cases for repeated data variations
- [ ] TXXX Review tests for parallel safety, rerun safety, unique data, scoped resources, best-effort cleanup and idempotent setup
- [ ] TXXX Review simulation tests for determinism (injected clock, seeds, fixed effect order, tick stepping), and that failures report their seed
- [ ] TXXX Confirm no test was weakened, skipped, disabled, deleted, commented out, excluded from
      compilation or given hard-coded expected values to reach green, and none was quarantined
      outside the quarantine rules (evidence it passed and failed on the same commit, in a pull
      request of its own)
- [ ] TXXX Verify zero-allocation tests pass and benchmark results are attached to the PR for tick-path changes
- [ ] TXXX Review region ownership: no locks or out-of-tick work in gameplay code, cross-region effects only through queues, cross-region reads only from snapshots
- [ ] TXXX Review per-client cost: everything the change adds per client stays capped as fights grow
- [ ] TXXX Security hardening: client-input validation (size, rate, value ranges, sequencing, permission), connection and message limits, safe rendering of player-generated content, audit records
- [ ] TXXX License review: the CI license scan passes, every dependency outside a package manager
      is in the third-party manifest, and design sources are recorded in research.md
- [ ] TXXX Observability review: errors captured where required, tick code logs only through buffers, no sensitive data, required metrics and pass rules reported
- [ ] TXXX Contract review: protocol version and golden fixtures; HTTP error codes, pagination defaults and maximum `limit`, UTC dates, IDs and cacheability; compatibility with the oldest allocated continent-server build
- [ ] TXXX Migration review: expand-migrate-contract order, rollback or mitigation for destructive changes, environment seed and reference data
- [ ] TXXX Web surfaces only: accessibility pass (keyboard access, labels, validation messages) and frontend suites (Jasmine/Karma, Angular Testing Library, Playwright, Cypress, Storybook) pass
- [ ] TXXX HTTP APIs (web surfaces and the gateway): `/swagger` works in development and is disabled in production
- [ ] TXXX Verify the Sonar PR scan passes
- [ ] TXXX Verify Codecov reports at least 95% coverage for changed or added files under the coverage configuration
<!-- guard -->
- [ ] TXXX Record design calls in `design/decisions.md` and roadmap decisions in the phase spec's
      Decision column (reasoning in research.md); confirm no escalation-list item proceeded without
      the director, and that nothing sensitive (secrets, tokens, player data, IP addresses,
      unredacted Staging or Production output) went into the pull request or the repository
- [ ] TXXX Confirm the pull request changes no gate-defining file unless it changes nothing else
<!-- /guard -->
- [ ] TXXX Add a `docs/patch-notes.md` entry if this PR merges a feature or a behavior-changing fix
- [ ] TXXX Confirm pull request scope is a focused vertical slice or a justified cross-cutting
      change
- [ ] TXXX **Before submitting the PR**: bring this feature's `spec.md` AND `tasks.md` up to
      date with the work actually performed, and update any older `spec.md` that drifted from
      the code so it reflects reality. Only `spec.md` must be kept truthful for prior features;
      the `tasks.md`, `plan.md` and `research.md` of older, already-merged specs (and this
      feature's `plan.md` and `research.md`) are not required to be refreshed for this gate
- [ ] TXXX Verify the spec stays executable: every mandatory acceptance scenario and functional
      requirement is backed by an automated test that currently passes, load-backed criteria cite an
      on-demand run against the PR's head or are marked `pending-hardware` until the director
      approves the hardware, and no spec claim is left unverified
- [ ] TXXX Reconcile cross-spec contradictions: if this feature's tests or acceptance criteria
      directly contradict a former spec, update the superseded spec(s) so the full spec corpus
      is internally consistent, and record which spec prevails and why
- [ ] TXXX Run quickstart.md validation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3+)**: All depend on Foundational phase completion
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3)
- **Polish (Final Phase)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after Foundational (Phase 2) - May integrate with US1 but should be independently testable
- **User Story 3 (P3)**: Can start after Foundational (Phase 2) - May integrate with US1/US2 but should be independently testable

### Cross-Spec Dependencies

- Per the constitution's Independent, Parallel Specs principle (XI), this spec MUST NOT assume another
  spec or sub-spec has already been implemented unless that dependency is explicitly documented in
  both specs' `plan.md`. Where this spec was split from a larger effort, prefer a split that lets
  sibling specs proceed in parallel over one that sequences them.
- Work that depends on a roadmap gate's outputs MUST NOT start before that gate passes (Principle XII).
- Work that depends on a roadmap proposed change or open question starts only once its decision is
  recorded (constitution, Governance, Roadmap decisions).

### Within Each User Story

- Tests MUST be written and FAIL before implementation
- Models before services
- Services before endpoints and protocol handlers
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel
- All Foundational tasks marked [P] can run in parallel (within Phase 2)
- Once Foundational phase completes, all user stories can start in parallel (if team capacity allows)
- All tests for a user story marked [P] can run in parallel
- Models within a story marked [P] can run in parallel
- Different user stories can be worked on in parallel by different team members

---

## Parallel Example: User Story 1

```bash
# Launch all tests for User Story 1 together:
Task: "Simulation test for [behavior] in tests/[Project].Tests/Simulation/[Name]Tests.cs"
Task: "Integration test for [user journey] in tests/[Project].Tests/Integration/[Name]Tests.cs"

# Launch all models for User Story 1 together:
Task: "Create [Entity1] in src/[Project]/[Path]/[Entity1].cs"
Task: "Create [Entity2] in src/[Project]/[Path]/[Entity2].cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Test User Story 1 independently
5. Deploy/demo if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Deploy/Demo (MVP!)
3. Add User Story 2 → Test independently → Deploy/Demo
4. Add User Story 3 → Test independently → Deploy/Demo
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1
   - Developer B: User Story 2
   - Developer C: User Story 3
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
