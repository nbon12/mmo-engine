# Patch notes

The running log of what changed on `main`, newest first. Read the entries newer than the last merge
you know about before starting work, especially any that touch the area you're changing.

When a PR merges a feature or a behavior-changing fix, add an entry at the top of this file in that
same PR. Dependency bumps and doc-only changes don't need an entry. Use this shape, keeping only the
sections that apply:

```markdown
## YYYY-MM-DD — <title>

Spec: [specs/NNN-feature/spec.md](../specs/NNN-feature/spec.md) · PR: #NNN

### Behavior and contract changes
### Assumptions that no longer hold
### Shared pieces to reuse
### Open follow-ups
```

<!-- Add the newest entry below this line. -->

## 2026-11-02 — Constitution 1.1.0: automated experiment loop

Spec: none (constitution amendment) · PR: #29

### Behavior and contract changes

Governance only. No application change.

- Gameplay content parameters (weapon, vehicle and ability numbers, and loadouts) must live in
  validated configuration, not code constants.
- Every simulator, load, experiment and playtest run needs a run ID and a run record. Tuning values
  cite their run IDs.
- Pull requests that change tuning or content values need a human maintainer's approval. Scheduled
  tasks and agents must never merge them.
- Bots that connect to a continent server are ordinary clients. No bot-only server code.
- `.routines/merge-policy.md` gains a tuning and content exception: those PRs are owner-merged, like
  constitution amendments.

### Assumptions that no longer hold

- Timing from the simulator, a developer machine or CI is no longer a "measurement". Only dedicated
  hardware over repeated runs is.
- Offline tooling (optimizers, plotting, analysis) is no longer bound by Principle VII's license
  allowlist. It still may not copy or vendor copyleft code into the repo.
- Architecture Police must leave registered experiment candidates alone, and must not change
  config values.

### Open follow-ups

- The first experiment-harness plan (with the regionizer simulator) sets up the run ledger, the
  scenario format and the bot behavior library.
- Open decision 11 (area effects across a region boundary) is new.

## 2026-11-02 — Done story sweep: dispatcher session + watchdog

Spec: none (maintenance) · PR: #33

### Behavior and contract changes

Tooling only. No application change.

- `.routines/done-story-sweep.md` now has two modes, picked by whether the session has `mcp__claude-code-remote__list_sessions`:
  - **Dispatch:** the scheduled task "Done story sweep dispatch: mmo-engine" runs daily at 5:29 AM America/New_York. It messages the long-lived session "Done story sweep dispatcher: mmo-engine (scheduled daily; do not archive)", which runs this repo's routine. hoa_management_system has its own pair.
  - **Watchdog:** the existing "Daily done checker" task (6:30 AM, a fresh session) only checks that the dispatcher posted today's report, and pushes a notification if it didn't.
- Each dispatch run posts a `Done story sweep report <date>` comment on this repo's open `sweep-log` issue, created on the first run. That issue is never a sweep candidate.
- A push notification goes out only when something new needs nbon12, or a step failed.

### Assumptions that no longer hold

- A scheduled task that starts a fresh session gets **no** session-management tools, whether it was made in the UI or from a session (tested 2026-11-02). That is why the 2026-11-02 run did nothing. Only sessions started from another session, and tasks that message such a session, have them.

### Open follow-ups

- This repo has no `.github/workflows/claude.yml`, so `@claude` handoffs aren't available here. (hoa_management_system's works as of 2026-11-02.) The sweep doesn't depend on it.
- "Run now" on a dispatch task starts a fresh session, not the dispatcher, and that session only runs the watchdog check. To start a dispatch run by hand, use a one-shot task bound to the dispatcher session, or `send_message`.
- To repair the dispatcher after its session is archived, follow the "Dispatcher setup / repair" steps in hoa_management_system's `docs/patch-notes.md` (2026-11-02 entry).

## 2026-10-31 — Architecture Police: nightly duplicate-pattern consolidation

Spec: none (maintenance) · PR: #28

### Behavior and contract changes

Tooling only. No application change.

- The **Architecture Police** scheduled task runs nightly at 4:41 AM America/New_York. It follows `.claude/skills/architecture-police/SKILL.md`.
- Once application code exists, each run consolidates **one** duplicated pattern with no behavior change. It merges a `refactor(architecture-police): …` PR only when the full suite has passed.
- Until then, runs report "nothing to scan".

### Shared pieces to reuse

- `.claude/skills/architecture-police/find_clones.py` is a standard-library clone finder:
  - `python3 -I find_clones.py [--fuzzy] [--min-lines N] PATH…`;
  - `--fuzzy` also matches copies with renamed identifiers.

### Open follow-ups

- The repo has no CI yet. Until it does, a consolidation merges only if the full suite ran locally in the nightly session. Otherwise the PR stays open as a draft.
