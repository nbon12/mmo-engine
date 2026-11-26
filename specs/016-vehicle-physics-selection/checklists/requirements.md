# Specification Quality Checklist: Vehicle Physics Engine Selection

**Purpose**: Validate specification completeness and quality before proceeding to planning
**Created**: 2026-11-22
**Feature**: [spec.md](../spec.md)

## Content Quality

- [x] No implementation details (languages, frameworks, APIs)
- [x] Focused on user value and business needs
- [x] Written for non-technical stakeholders
- [x] All mandatory sections completed

## Requirement Completeness

- [x] No [NEEDS CLARIFICATION] markers remain
- [x] Requirements are testable and unambiguous
- [x] Success criteria are measurable
- [x] Success criteria are technology-agnostic (no implementation details)
- [x] All acceptance scenarios are defined
- [x] Edge cases are identified
- [x] Scope is clearly bounded
- [x] Dependencies and assumptions identified

## Feature Readiness

- [x] All functional requirements have clear acceptance criteria
- [x] User scenarios cover primary flows
- [x] Feature meets measurable outcomes defined in Success Criteria
- [x] No implementation details leak into specification

## Notes

- The feature is the choice among three named physics engines, and its subject is cross-build
  determinism, so the spec names the candidates (Bepu, Jolt, Rapier), the two builds (.NET 10 on Linux
  x64 and WebAssembly in a Web Worker), the runtimes the agreement suite already uses, and 015's
  formats, entry points and CLI commands. Issue #12 fixes these as the subject of the test, not as
  implementation choices. Code-level choices (adapter layout, how each engine models the wheels, the
  shims and bindings, the cache keys, the record's file paths and the decision command's name) are
  left to `plan.md` and `research.md`. Specs 013, 014 and 015 follow the same convention.
- Success criteria name builds, runtimes and runner classes because agreement and cost on them are
  the outcome; they state no implementation of how agreement is reached.
- Every user story has Given/When/Then acceptance scenarios and an Independent Test that an automated
  test can run. The decision rule's branches and band boundaries are fixture reports. The divergence
  measurement has a known-offset fixture. The behavior checks, statuses and orchestration run on the
  fixture engine (FR-035), and Rapier's fit verdicts run on fit-check fixtures. So no test depends on
  which engine wins, and only the winner's checks run against a real engine on `main` (US1
  scenario 14).
- The review fixes name a few more mechanisms: the analyzer's rule IDs, pthread and `futex` counting,
  `[JSImport]`, `emmalloc` and SPDX expressions. These are the subjects of the determinism, no-lock
  and license requirements, which Principles II and VII state in those terms. They are not choices
  about how the selection is built.
- No `[NEEDS CLARIFICATION]` markers were written. The run was unattended (Principle XIII), so each
  open question was answered with the most defensible default and recorded under Assumptions and in
  the Clarifications session: the decision is gate 9's and not a constitution open decision;
  exactness comes first; corrections are judged by windowed divergence with stated bounds, deferring
  the network-profile check to sub-spec 6; Rapier's fit is six automated criteria; vehicle state joins
  through new format versions, leaving version 1 and 015's streams unchanged; the rule runs per
  WebAssembly build mode, so gate 9's build-mode decision stays open.
- Validation iteration 1: four items needed fixes (the tie rule with three qualifiers, per-mode
  wording in US3 and US5, NonExact eligibility excluding reset equivalence, and the native shims'
  coverage). Iteration 2: all items pass.
- Iteration 3, after the spec review. FR numbers stay stable, so the review's references still
  point at the right text. New requirements take the next free numbers, FR-032 to FR-037, plus
  SC-009. The fixes:
  - The adapters carry the analyzer, exempt only from named rules, and engine-neutral vehicle code
    lives in the core. A lock test is a qualification check (FR-004, FR-033, FR-034).
  - The license scan covers only what a candidate adds over the core-only builds, with SPDX `OR`,
    the LLVM exception and `emmalloc` settled for the scan. How Principle VII treats platform
    libraries is a follow-up (FR-031).
  - Iteration 1's NonExact eligibility excluded reset equivalence wholesale. It now exempts only the
    cross-build part, and server-build reset equivalence is required of every winner (FR-026).
  - US1 checks are tested on a fixture engine with known faults, and the winner's checks run on
    `main`. US6 runs on fit-check fixtures (FR-035).
  - A NonExact record is scoped to worlds with vehicles, and version 2 records carry a version 1
    part hash (FR-012, FR-027).
  - The provisional flag has an exact condition and does not by itself meet gate 9's exit criterion.
    Its follow-up is sub-spec 6's dependency, not a duty this spec gives sub-spec 6.
  - Every behavior tolerance and time bound is justified under Assumptions, and the tolerances, the
    window and the bounds moved to a selection configuration that no stream carries (FR-036).
  - The decision records per-candidate evidence for parallel Movement phases (FR-027).
  - One inclusive tie formula, a cost-then-size tie-break before the fixed order, boundary,
    mixed-mode and cheapest-but-Diverged fixtures (FR-026, US3).
  - End-to-end denial scenarios for Diverged, Failed and LicenseBlocked (US1 scenario 13, US2
    scenarios 9 to 11).
  - A status table with reason codes, precedence and exit codes (FR-037).
  - Oracles that do not use the engine, and contact events derived by the core (FR-011, FR-012).
  - Eight vehicles per seeded stream, with checked coverage, and the build modes and builds named
    (FR-010, FR-018).
  - A committed manifest at commit 65112ab as 015's baseline, a 15-minute bound on every build, and
    a static fit check (d) (FR-015, FR-021, FR-030).
  - Jolt and Rapier run as separate WebAssembly modules through `[JSImport]`, in all three modes
    (FR-032).
  - The window is checked against every committed network profile, loss included (FR-022).

  All items pass.
