---
name: architecture-police
description: Find places where this repo solves the same problem two or more ways, consolidate the most valuable one into a single shared implementation without changing behavior, and open and merge one PR. Run nightly by the "Architecture Police" scheduled task; can also be run by hand with /architecture-police.
disable-model-invocation: true
---

# Architecture Police (mmo-engine)

Find near-duplicate architectural patterns: two or more implementations of the same mechanism,
written separately. Pick the most valuable one, merge the copies into one implementation, and
change no behavior. Ship it as one PR, and merge the PR once every gate is green.

This runs unattended. **Never ask the user anything.** Decide from the code, the constitution
and the test results, and explain each decision in the PR description.

**No code yet means nothing to do.** If the repo has no application source outside `.specify/`,
`specs/`, `docs/`, `design/` and `.claude/`, stop and report "nothing to scan". Architecture
docs and specs are not targets: the constitution governs them, and they change only through
Spec Kit.

## What counts as a near-duplicate

Copies that do the same job and would change for the same reason. Typical examples:

- **Engine and server code (C#).** Each of these written separately in more than one place:
  - a pooled-buffer or message-queue helper;
  - a cross-region message envelope;
  - snapshot publication;
  - time-base or tick-scheduling arithmetic;
  - an ID allocator;
  - a spatial-query helper;
  - a config-binding or options-validation block;
  - a telemetry or metric registration;
  - a startup wiring block.
- **Persistence and social tier.** The same grain or state-access pattern, the same PostgreSQL
  access code, or the same retry or idempotency logic, repeated across features.
- **Browser client (TypeScript).** The same connection, reconnect, decode or interpolation
  helpers, or UI state handling, written in more than one place.
- **Web surfaces** (NekoHOA stack: FastEndpoints and Angular). The same endpoint boilerplate,
  pagination, error mapping, or Angular service or component logic.
- **Test support.** Duplicated simulators, fixtures, factories, bot drivers and seed helpers.
  Never delete or merge test *cases*: the "Nightly Unused Test Cleanup" scheduled task owns
  that.
- **Drift from a canonical piece.** A shared implementation already exists, but code elsewhere
  re-implements it instead of calling it.

These are **not** targets:

- **Coincidental similarity.** Code that looks alike but encodes rules that will change
  independently. If one implementation would need flags or mode parameters to serve both
  callers, the copies are not duplicates.
- **Alternatives kept open on purpose.** Two approaches to something on the constitution's
  **Open decisions** list are deliberate. Examples: WebTransport versus WebSocket,
  server-simulated versus client-reported hits, Orleans versus ASP.NET Core over PostgreSQL.
  Merging them would settle the decision silently. Leave them, and mention them in the report.
- **Duplication the constitution requires**, such as separate types for independently deployed
  processes, or per-region private data.
- **Generated, owned-elsewhere or excluded files** (see below).

## Hard limits

- **No behavior change.** Leave all of these exactly as they are:
  - game wire-protocol messages, their single source of truth, the protocol version and golden
    fixtures;
  - grain interfaces and persisted grain state, and any type exchanged between independently
    deployed processes (they must tolerate version skew);
  - HTTP contracts;
  - the DB schema and migrations;
  - config keys;
  - metric and log names that dashboards or alerts read;
  - simulation results. With a fixed seed and time base, every simulation test must produce the
    same outcome before and after.
- **Hot paths stay hot.** Gameplay code is anything that runs inside a region's tick, a parallel
  phase, the global region or an area actor (Principle II, and Performance discipline). A
  consolidation there must not add any of these:
  - allocations, boxing, LINQ or capturing lambdas;
  - interface or virtual dispatch;
  - locks or blocking waits.

  Struct-of-arrays layouts and pooled buffers win over neatness. Change gameplay code only by
  moving it into a static helper the JIT can inline. When a benchmark covers that code, run it
  before and after and include the numbers (Principle VI). Report anything else under
  "Findings to review" instead of changing it.
- **Never edit:**
  - `.specify/**`, `specs/**`, `design/**`, `.claude/**`;
  - `docs/**`, except your patch-notes entry;
  - migrations, generated code (including code generated from protocol definitions) and
    lockfiles.
- **Report only, never change:** `.github/**`, and any infrastructure, deploy or orchestration
  files: OpenTofu, Agones fleets, Kubernetes or Helm manifests, Dockerfiles and Compose files.
  Merges to `main` deploy automatically. List duplicates you find there in the PR description.
- No new packages, and nothing copied from outside the repo (Principle VII, clean-room design).
- Never delete, skip or weaken a test or assertion. Change tests only to follow symbols you
  moved or renamed, or to add coverage for the consolidated code.
- **One theme per PR.** Broad mixed-scope PRs must be split. A theme may touch many call sites.
  Aim for under about 800 changed lines, not counting tests. For a bigger theme, do one coherent
  slice and list the rest as follow-up.
- Never push to `main`. Never force-push a branch you did not create. Never disable, skip or
  bypass a check.

## Procedure

### 1. Orient

- Read `CLAUDE.md`, plus these parts of `.specify/memory/constitution.md`:
  - Core Principles, especially II, III, VI, VII, VIII and X;
  - Architecture Constraints, especially Performance discipline and Open decisions;
  - Engineering Standards;
  - Testing Standards.

  Also read the `docs/patch-notes.md` entries from the last two weeks.
- If the working tree is dirty, stop. Otherwise run `git checkout main && git pull --ff-only
  origin main`.
- Use the GitHub MCP tools (`mcp__github__*`, loaded with ToolSearch) for every GitHub action.
  `gh` is not authenticated in cloud sessions.
- Find the build and test commands. Look at `global.json` and the `*.sln` files (`dotnet build`,
  `dotnet test`), each `package.json` (its test and build scripts) and `.github/workflows/`.

### 2. Finish earlier runs first

- List open PRs whose head branch starts with `architecture-police/`.
- If you find one:
  1. Update it from `main`.
  2. Fix anything that fails, staying within its theme.
  3. Merge it if it goes green (step 7).
- If it cannot go green:
  - if it is more than 3 days old, close it with a comment saying why, and continue;
  - otherwise, leave it open, add a comment saying what's blocking it, and stop for this repo
    tonight.

  Do not stack a second refactor on one that is still pending.

### 3. Find candidates

Use several signals. No single one is enough.

- **Copy-paste detection.** Run the clone finder next to this file. It uses only the Python
  standard library, so it needs no install or download. Run it in both modes, over every source
  and test directory:

  ```bash
  F=.claude/skills/architecture-police/find_clones.py
  python3 -I $F <source and test dirs>
  python3 -I $F --fuzzy --min-lines 10 <source dirs>
  ```

  The first run finds literal copies. The second (`--fuzzy`) also finds copies with renamed
  identifiers or changed literals. Neither finds the same idea written differently. Treat each
  cluster as a lead, read the code, and drop clusters that are only test cases or
  coincidental.
- **Structural comparison.** Compare sibling components, features and surfaces for the same
  shape. Look for:
  - re-declared private helpers;
  - repeated constants;
  - the same sequence of calls;
  - types whose names differ only by a prefix.
- **Canonical drift.** For each shared or common project, folder or module, search for code that
  re-implements it.

Stay clear of in-flight work. List the open PRs and the files each one changes. Drop any
candidate that would touch a file another open PR changes.

### 4. Pick one theme

Rank candidates in this order:

1. Authority, ownership, protocol-validation or persistence logic implemented more than one
   way.
2. Re-implementations of an existing shared piece. Point the callers at that piece.
3. Copy-paste clusters with three or more copies.
4. Pairs.

Within a rank, prefer more lines removed and fewer components touched. Prefer code off the hot
path.

If the copies **differ** in behavior today, merging them would change behavior. A difference may
be a real bug, but do not fix it silently. Record it under "Findings to review" in the PR
description, and pick another theme.

Choose the canonical form this way:

- Use the existing shared or most-used implementation, and the pattern the constitution
  prescribes.
- Put new shared code in the shared project or module its kind already lives in, and don't
  create a new project for it.

If nothing clears the bar, stop: no branch and no PR. That is a good outcome.

### 5. Check the baseline

Run the full build and test suite on unmodified `main`, before editing. Include simulation and
contract tests. Leave out load, stress and soak tests: they need dedicated hardware.

If you cannot get the toolchain, note it. In that case the repo's GitHub checks must be the gate
(step 7). If `main` is red, change nothing in this repo and say so in the report.

### 6. Consolidate

1. Create the branch `architecture-police/<YYYY-MM-DD>` from the latest `main`.
2. Write the shared implementation, or choose the canonical existing one. Move every copy in the
   theme onto it and delete the copies. Leave no forwarding wrappers unless a public symbol must
   survive.
3. Add tests for any new shared code. Tests must be deterministic, parallel-safe and rerun-safe
   (Principle VIII). Added and changed files must reach the 95% coverage gate.
4. Build and run the full suite. Fix until green. If it cannot go green without a behavior
   change, abandon the theme, open no PR, and report why.
5. Commit as `refactor(architecture-police): <theme>`.

### 7. Open the PR and merge it

1. Push with `git push -u origin <branch>`.
2. Open a draft PR against `main` titled `refactor(architecture-police): <theme> (<YYYY-MM-DD>)`.
   Use these sections:
   - **Summary**.
   - **Duplicate pattern**: each copy as `file:line`, and how the copies matched.
   - **Consolidation**: the canonical location, what was deleted, and why behavior is unchanged
     for each caller. For gameplay code, also say why the hot path is unaffected.
   - **Verification**: commands run with their results, and benchmark numbers where relevant.
   - **Next candidates**: other ranked candidates with paths. The next run starts here.
   - **Findings to review**: report-only duplicates, open-decision alternatives left alone, and
     copies that differ in ways that look like bugs.
3. Add an entry at the top of `docs/patch-notes.md` in the shape that file describes. Title it
   "Architecture Police: \<theme\>". Write "Spec: none (maintenance)" and link the PR. Use the
   sections "Shared pieces to reuse" (the canonical piece and where it lives) and "Assumptions
   that no longer hold" (the removed variants, so nobody re-adds them). Say "No behavior change."
   Commit and push.
4. Once local checks pass, mark the PR ready for review. If the repo has GitHub checks, wait for
   all of them to finish. Subscribe to the PR's activity and end your turn: check events wake
   the session. Do not poll with `sleep`. If a check fails, fix it within the theme, push, and
   wait again.
5. **Merge with a squash merge, and delete the branch, only if all of the following hold:**
   - the full build and test suite passed. That means locally, or in GitHub checks when the
     toolchain was unavailable here. **If neither happened, do not merge.** The usual case is a
     repo with no CI yet and no local toolchain;
   - every GitHub check has completed and none failed or is pending (skipped or neutral is
     fine);
   - the branch is up to date with `main`. If `main` moved, update the branch and verify again;
   - the diff touches no path from the "Never edit" or "Report only" lists.
6. If any condition fails, leave the PR open as a draft with a comment explaining what's missing.
   Do not merge it.

### 8. Report

Give one short summary: the theme, the PR link, whether it merged (and why or why not), net
lines removed, and the top remaining candidates.

## Schedule

The "Architecture Police" scheduled task runs this playbook nightly at 4:41 AM
America/New_York, together with the matching playbook in `nbon12/hoa_management_system`. To
change what the task does, edit this file. The task reads it on every run.
