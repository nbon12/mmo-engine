---

description: "Task list template for feature implementation"
---

# Tasks: [FEATURE NAME]

**Input**: Design documents from `/specs/[###-feature-name]/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are REQUIRED. Per the constitution (Principles VIII and IX), every acceptance
scenario maps to an automated test, tests are written first and must fail before implementation,
and the completion gate requires every applicable suite to pass.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **.NET projects**: `src/[Project]/`, `tests/[Project].Tests/`, `tests/[Project].Benchmarks/`
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

- [ ] T001 Create project structure per implementation plan
- [ ] T002 Initialize the .NET solution (SDK pinned in global.json) and/or TypeScript projects named in plan.md
- [ ] T003 [P] Configure linting and formatting tools
- [ ] T004 [P] Configure a banned-API analyzer for gameplay assemblies (locks and blocking synchronization, wall-clock time, blocking I/O)
- [ ] T005 [P] Configure xUnit and .NET Testcontainers for test projects, with test parallelization enabled
- [ ] T006 [P] Web surfaces only: configure Jasmine/Karma, Angular Testing Library, Playwright, Cypress and Storybook

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

Examples of foundational tasks (adjust based on your project):

- [ ] T007 Setup strict PostgreSQL migrations: tested against PostgreSQL, applied idempotently once per rollout with a concurrency guard, Orleans system tables included
- [ ] T008 [P] Configure FluentValidation-validated options: fail-fast startup and environment-name validation (`Development`, `Dev`, `Test`, `Staging`, `Production`)
- [ ] T009 [P] Configure Serilog structured JSON logging with correlation IDs and continent, region and tick context
- [ ] T010 [P] Configure Sentry error capture with environment and release tags and sensitive-data filtering
- [ ] T011 [P] Configure OpenTelemetry metrics; tick code records into pre-allocated per-region buffers that are exported off the tick threads
- [ ] T012 [P] Configure health reporting: the Agones SDK for continent servers, readiness endpoints for other services
- [ ] T013 Configure Docker Compose for local parity (a real PostgreSQL service; MinIO if file storage is used)
- [ ] T014 [P] Configure secret management, keeping secrets out of images and committed configuration
- [ ] T015 [P] Provide the deterministic simulation harness: injected continent time base, seeded randomness, test-driven tick stepping, regionizer invariant checker
- [ ] T016 [P] Provide the wire-protocol single source of truth and the shared golden-fixture harness for the C# and TypeScript sides
- [ ] T017 [P] Implement authentication: ASP.NET Core Identity + JWT with a pinned signing algorithm, rotating single-use hashed refresh tokens, one-time join tokens for continent servers
- [ ] T018 [P] HTTP APIs: set up FastEndpoints, the global exception handler, consistent error responses, and Swashbuckle at `/swagger` in development only
- [ ] T019 [P] Configure GitHub Actions: lint, tests, Sonar scan, Codecov upload and the 95% changed-file coverage gate
- [ ] T020 [P] Configure environment separation for Dev, Staging and Production (databases, Agones fleets, Deployments, Cloud Run services, secrets)
- [ ] T021 [P] Web surfaces only: configure Cloudflare edge, rate-limit and cache defaults
- [ ] T022 [P] Web surfaces only: configure Neon connection limits, pooling and short-lived DbContext lifetimes

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - [Title] (Priority: P1) 🎯 MVP

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 1 ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T023 [P] [US1] xUnit Theory simulation test for [behavior] with a fixed time base and seed and the invariant checker on, in tests/[Project].Tests/Simulation/[Name]Tests.cs
- [ ] T024 [P] [US1] xUnit Theory + Testcontainers integration test for [user journey], covering authorization and data variations, isolated by a rolled-back transaction or unique per-test keys, parallel- and rerun-safe, in tests/[Project].Tests/Integration/[Name]Tests.cs
- [ ] T025 [P] [US1] Contract test for [protocol message or endpoint] against the shared golden fixtures, in tests/[Project].Tests/Contract/[Name]Tests.cs and client/tests/contract/[name].test.ts
- [ ] T026 [P] [US1] Zero-allocation test and BenchmarkDotNet benchmark for [tick-path code] in tests/[Project].Benchmarks/[Name]Benchmarks.cs
- [ ] T027 [P] [US1] Parallel-equivalence test (N threads vs. one thread) for [parallel-phase change] in tests/[Project].Tests/Simulation/[Name]ParallelEquivalenceTests.cs
- [ ] T028 [P] [US1] Web surfaces only: Angular Testing Library component test for [component] in web/[surface]/src/[path]/[component].spec.ts
- [ ] T029 [P] [US1] Web surfaces only: Cypress E2E test for [user journey] in web/[surface]/cypress/e2e/[name].cy.ts
- [ ] T030 [P] [US1] Web surfaces only: Storybook story and visual regression case for [component]

### Implementation for User Story 1

- [ ] T031 [P] [US1] Create [Entity1] in src/[Project]/[Path]/[Entity1].cs
- [ ] T032 [P] [US1] Create [Entity2] in src/[Project]/[Path]/[Entity2].cs
- [ ] T033 [US1] Implement [system, grain or service] in src/[Project]/[Path]/[Name].cs (depends on T031, T032)
- [ ] T034 [US1] Implement [protocol handler, FastEndpoint or queue message] for [feature] in src/[Project]/[Path]/[Name].cs
- [ ] T035 [US1] Add client-input validation (size, rate, ranges, permission), error handling and consistent response shapes
- [ ] T036 [US1] Add Serilog structured logs, security and audit events, and metrics for user story 1 operations

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
- [ ] T040 [US2] Implement [system, grain or service] in src/[Project]/[Path]/[Name].cs
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
- [ ] T046 [US3] Implement [system, grain or service] in src/[Project]/[Path]/[Name].cs
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
- [ ] TXXX Review that tests use xUnit Theories for repeated data variations
- [ ] TXXX Review tests for parallel safety, rerun safety, unique data, scoped resources, best-effort cleanup and idempotent setup
- [ ] TXXX Review simulation tests for determinism (time base, seeds, tick stepping), and that failures report their seed
- [ ] TXXX Verify zero-allocation tests pass and benchmark results are attached to the PR for tick-path changes
- [ ] TXXX Review region ownership: no locks in gameplay code, cross-region effects only through queues, cross-region reads only from snapshots
- [ ] TXXX Security hardening: client-input validation, per-connection rate and size limits, untrusted player-generated content, audit events
- [ ] TXXX License review: every new dependency is MIT or Apache-2.0 or an approved exception, and design sources are recorded in research.md
- [ ] TXXX Sentry, logging and metrics review: errors captured with environment and release tags, no sensitive data, required metrics reported
- [ ] TXXX Contract review: protocol version and golden fixtures; HTTP pagination defaults and maximum `limit`, UTC dates, IDs and cacheability
- [ ] TXXX Migration review: rollback or mitigation for destructive changes, environment seed and reference data
- [ ] TXXX Web surfaces only: accessibility pass (keyboard access, labels, validation messages) and frontend suites (Jasmine/Karma, Angular Testing Library, Playwright, Cypress, Storybook) pass
- [ ] TXXX Web surfaces only: `/swagger` works in development and is disabled in production
- [ ] TXXX Verify the Sonar PR scan passes
- [ ] TXXX Verify Codecov reports at least 95% coverage for relevant changed or added files
- [ ] TXXX Add a `docs/patch-notes.md` entry if this PR merges a feature or a behavior-changing fix
- [ ] TXXX Confirm pull request scope is a focused vertical slice or a justified cross-cutting change
- [ ] TXXX **Before submitting the PR**: bring this feature's `spec.md` AND `tasks.md` up to
      date with the work actually performed, and update any older `spec.md` that drifted from
      the code so it reflects reality. Only `spec.md` must be kept truthful for prior features;
      the `tasks.md`, `plan.md` and `research.md` of older, already-merged specs (and this
      feature's `plan.md` and `research.md`) are not required to be refreshed for this gate
- [ ] TXXX Verify the spec stays executable: every mandatory acceptance scenario and functional
      requirement is backed by an automated test that currently passes, and no spec claim is left
      unverified
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

- Per the constitution's Independent, Parallel Specs principle (X), this spec MUST NOT assume
  another spec or sub-spec has already been implemented unless that dependency is explicitly
  documented in this spec's `plan.md`. Where this spec was split from a larger effort, prefer a
  split that lets sibling specs proceed in parallel over one that sequences them.

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
