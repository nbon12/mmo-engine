# MMO Engine Development Guidelines

PlanetSide 2-style MMOFPS: regionized C#/.NET continent servers, an Orleans persistence and social
tier over PostgreSQL, continent servers run as an Agones fleet, and a TypeScript browser client.
Web-app-like features (forums, account portal, web store, leaderboards, admin consoles) are web
surfaces on the NekoHOA stack.

## Read first

- **Constitution** (governs everything): `.specify/memory/constitution.md`
- **Architecture spec** (rationale, estimates, alternatives, sources): `docs/architecture/server-architecture.md`
- **Patch notes** (what changed on `main`): `docs/patch-notes.md`
- **Art and asset designs** (for anything players see): `design/`, starting with
  `design/style-guide.md`. One page per asset; `design/README.md` explains the layout.

The constitution wins any conflict with the architecture spec, and both win over feature specs and
plans. Do not settle an item on the constitution's Open decisions list inside a feature; call it
out instead.

## Spec Kit setup

- Spec Kit **1.2.5** with the Claude integration. Its skills live in `.claude/skills/speckit-*` and
  are invoked with hyphens: `/speckit-specify`, `/speckit-clarify`, `/speckit-plan`,
  `/speckit-tasks`, `/speckit-analyze`, `/speckit-checklist`, `/speckit-implement`,
  `/speckit-converge`, `/speckit-taskstoissues`, `/speckit-constitution`.
- Project templates are **overrides** in `.specify/templates/overrides/` (plan, spec, tasks). They
  replace the core templates in `.specify/templates/`. Edit the overrides, never the core files,
  which Spec Kit owns and may rewrite on upgrade.
- The bundled **git extension** creates the numbered feature branch before `/speckit-specify` runs
  (the `before_specify` hook). Its auto-commit hooks are off (`.specify/extensions/git/git-config.yml`).
- The globally installed `specify` CLI (0.3.0) predates Spec Kit 1.x and cannot init or upgrade this
  project. Run Spec Kit CLI commands with
  `uvx --from git+https://github.com/github/spec-kit.git@v1.2.5 specify <command>`, or upgrade the
  tool with `uv tool install specify-cli --force --from git+https://github.com/github/spec-kit.git@v1.2.5`.

## Patch notes: read them, and add to them when you merge

`docs/patch-notes.md` is the running log of what changed on `main`, newest first. Each entry covers
behavior and contract changes, assumptions that no longer hold, shared pieces to reuse, and open
follow-ups. Other agents rely on it to catch up.

- **Before starting work**, skim the entries newer than the last merge you know about, especially
  any that touch the area you're changing.
- **When a PR merges a feature or a behavior-changing fix**, add an entry at the top of
  `docs/patch-notes.md` **in that same PR**. Follow the shape described at the top of the file:
  date, title, spec link, PR link, then the sections that apply. Keep it to what someone working
  nearby needs to know. Dependency bumps and doc-only changes don't need an entry.

## Spec Kit workflow: PR watching

Spec Kit commands other than `/speckit-implement` (that is, `/speckit-specify`, `/speckit-clarify`,
`/speckit-plan`, `/speckit-tasks`, `/speckit-analyze`, `/speckit-checklist`, `/speckit-converge`,
`/speckit-constitution` and `/speckit-taskstoissues`) only produce Markdown and spec artifacts under
`specs/` or `.specify/`. These changes do not exercise application code, so:

- Do **not** subscribe to PR activity, watch CI, or schedule self check-ins for PRs that contain
  only Spec Kit or Markdown document changes.
- Do **not** treat CI runs triggered by such doc-only PRs as something to babysit or autofix.
- Still commit and push the artifacts and open or update the draft PR as usual.

PR watching and CI babysitting are appropriate for `/speckit-implement` and other changes that touch
application or infrastructure code, unless the user explicitly asks otherwise.

## Spec Kit workflow: use the Spec Kit feature branch

`/speckit-specify` creates a numbered feature branch (for example `003-region-merge-split`) and the
matching `specs/<feature>/` directory. **That Spec Kit feature branch is the branch to use** for all
of the feature's Spec Kit work and its PR.

- Do **not** move the work onto, or push it to, any `claude/*` branch that the harness may create by
  default (this happens especially in cloud sessions). Prefer the Spec Kit feature branch even when
  a `claude/*` development branch is designated for the session.
- If the session starts on a `claude/*` branch, switch to (or create) the Spec Kit feature branch
  that `/speckit-specify` reports, and commit and push the Spec Kit artifacts and PR there.
- The Spec Kit scripts also key off the branch and feature directory. Running them from the feature
  branch keeps `check-prerequisites.sh` and friends working without `SPECIFY_FEATURE` overrides.

## Git branch lifecycle

After a branch is merged into `main`, that branch is **immutable and closed**. Do not:

- commit to it,
- push to it, or
- check it out to continue work.

If more work is needed after a merge:

1. Switch to `main`.
2. Pull the latest changes: `git pull origin main`.
3. Create a new branch off `main`: `git checkout -b <new-branch-name>`.
4. Do all further work on the new branch.

Merged branches exist only as historical artifacts. Treat them as read-only.

## Don't ask about work order — just pick and start

Under the constitution's Independent, Parallel Specs principle (Principle X), specs and sub-specs
are designed to be individually completable and, where split from a larger effort,
parallelizable. Because of this:

- Do **not** ask the user "which should come first," "what should I work on first," or any other
  question about ordering across independent specs or sub-specs.
- These questions are low-value and only delay the workflow. If specs are truly independent, order
  does not change the outcome.
- Instead, **pick one and start.** Use any reasonable tie-breaker (a priority label if present,
  numeric spec order, or simple judgment) and proceed without waiting for confirmation.
- This does not apply to genuine hard dependencies that are explicitly documented (Principle X).
  Those must still be sequenced correctly.

## Code review: every natural-language test MUST map to a real, faithful automated test

A **natural-language test (NLT)** in this repo is any Given/When/Then **Acceptance Scenario** and any
**Independent Test** line written in a `specs/<feature>/spec.md`. The constitution's executable-spec
principle (Principle IX) commits that every acceptance scenario maps to an automated test that runs
on demand and passes before merge. Code review MUST enforce that commitment. These rules bind the
reviewer whenever a change implements, modifies, or claims to complete a Spec Kit feature.

- **Existence: every NLT MUST have a test.** For each acceptance scenario and each Independent Test
  in the changed feature's `spec.md`, the reviewer MUST locate the specific automated test that
  covers it and cite it by file and name. That test may be an xUnit unit, simulation, integration,
  contract or business-process test, a game-client test, or a web-surface test (Jasmine/Karma,
  Angular Testing Library, Playwright or Cypress). An acceptance scenario with **no** corresponding
  automated test is a review finding: name the scenario and report the gap. Do not accept "covered
  by the suite" without pointing at the exact test.
- **Faithfulness: the test MUST test what the NLT describes.** For each mapped test, the reviewer
  MUST verify that the test actually exercises the scenario's Given/When/Then: the arrange sets up
  the **Given**, the act performs the **When**, and the assertions verify the **Then**. A test that
  exists in name but does not assert the scenario's stated outcome MUST be flagged as *not covering
  its NLT*. Specifically flag:
  - assertions that only prove the code "does not throw";
  - truthiness assertions where the scenario states a specific value, entity state, record set,
    status code or error code;
  - tests that assert a different condition than the scenario describes;
  - tautological or mock-only tests that verify the mocks rather than the behavior;
  - skipped or disabled tests (`[Fact(Skip = …)]`, `[Theory(Skip = …)]`, an unconditional
    `Assert.Skip`, `xit`, `it.skip`, `describe.skip`, or a `.only` that excludes siblings).
- **Simulation scenarios must step time faithfully.** A simulation test MUST run on a fixed time
  base and seed, and MUST advance the ticks the scenario implies. For example, a cross-region effect
  is asserted only after the owning region's next tick, and a merge or split only after the tick
  that triggers it ends. A test that asserts before the queued work could have applied does not
  cover its NLT.
- **Negative and denial scenarios count.** A scenario stating that a request or action is denied,
  refused or rejected (for example `403 FORBIDDEN`, an invalid join token, an over-rate client
  message, or a closed portal blocking a claim) MUST have a test asserting that exact denial, not
  merely that the happy path works. A missing negative-path test for a stated denial scenario is a
  finding.
- **Report format.** Report each violation against the specific scenario, quoting the scenario text
  from `spec.md` and naming either the missing test or the test whose assertions do not match the
  scenario. A finding without both the cited scenario and the cited test or gap is not a finding.
