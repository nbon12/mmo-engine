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

## 2026-11-02 — Auto-merge for nbon12's pull requests

Spec: none (tooling) · PR: #40

### Behavior and contract changes

Tooling only. No application change.

- `.github/workflows/auto-merge.yml` squash-merges a ready pull request into `main` that nbon12
  opens once every conversation on it is resolved and every check has passed. That includes every
  Claude session's pull request, since sessions open them as nbon12. Anyone else's pull request is
  untouched.
- `main` has no CI yet, so there are no checks: a qualifying pull request merges within seconds of
  being marked ready (or opened ready).
- **Left alone (manual):** drafts, the `hold` label, a review requesting changes, a failed check, a
  guarded path (`GUARDED_PATHS` in the workflow, now the one list the merge policy points to), 300+
  changed files, a base other than `main`. The run summary says which.
- The `auto-merge` label marks the pull requests it is handling. It only turns off auto-merge on
  those; `hold` turns off anyone's.
- **Fails closed:** if a run errors, a last step turns off the pull request's auto-merge.
- It merges with the `MERGE_TOKEN` secret, not `GITHUB_TOKEN`, so its merges will start push-to-main
  workflows once there are some.
- **Settings changed alongside this pull request:** "Allow auto-merge" is on, and `main` now has branch
  protection: conversations must be resolved before merging, and force pushes and deletion are
  blocked. Labels `auto-merge` and `hold` exist now.

### Assumptions that no longer hold

- **Open a pull request as a draft, and mark it ready when it's ready to merge.**
  `.routines/merge-policy.md` and `CLAUDE.md` say so now: marking it ready is the decision to merge,
  because the workflow can't check the local build and tests listed in the description, the patch
  notes or open decisions.
- A session no longer merges its own non-guarded pull request; auto-merge does. Guarded-path pull
  requests still follow the merge policy's independent review, and the session merges them.

### Open follow-ups

- When CI lands: make its jobs required checks on `main`, set `REQUIRE_CHECKS` to `"true"` in the
  workflow, and add a `workflow_run` trigger listing the CI workflows (as hoa_management_system's copy
  does).
- If agents move to the Claude GitHub App or a bot account, add that login to `AUTHORS` in the
  workflow.

## 2026-11-02 — Constitution 1.1.0: no human gates, human input is feedback after merge

Spec: none (constitution amendment) · PR: #38

### Behavior and contract changes

Process and agent guidance only. No application change.

- New **Principle XIII, Autonomous Delivery; Human Input Arrives as Feedback**. The maintainer is not a step in the pipeline. New and changed specs, plans, tasks, checklists, quickstarts and PR descriptions must not contain:
  - tasks or checklist items assigned to a person;
  - manual verification or QA, or playtests as acceptance;
  - "STOP and validate", approval or sign-off checkpoints;
  - "things only a human can do" sections.

  Every check is an automated test or CI check. Existing specs are not rewritten only to remove an earlier human step.
- **Ambiguity is decided, not asked.** Pick the most defensible default, record it under the spec's Assumptions, and continue. No `[NEEDS CLARIFICATION]` markers and no mid-workflow questions. The exception is an open decision from the constitution: call it out, design so it stays open, and keep going on the rest. `CLAUDE.md` says this overrides the upstream Spec Kit text that asks the user: the clarification questions in `/speckit-specify` and the "incomplete checklists, proceed anyway?" prompt in `/speckit-implement`.
- **Unless otherwise specified:** when the maintainer themselves runs `/speckit-specify` or `/speckit-clarify` (or says they want to be involved), the agent engages and asks in those two phases. Plan, tasks, analyze, implement, the PR and the merge stay hands-off either way. Commands an agent, scheduled task or routine starts on its own are never maintainer-driven. The exception relaxes only "decide, don't ask": no human steps in artifacts and merge on green still apply, and an open decision still takes an amendment.
- **A green PR always merges.** When every required check passes, the agent merges it or enables auto-merge, and nobody is asked to review at that stage. CI/CD or ops controls are automated guards (dry run, rate limit, kill switch, audit trail), not required reviewers. Product features where people act (a moderation console, a support queue) are unaffected.
- **Exceptions are narrow:** third-party accounts and root credentials, billing, admin-only settings, amending the constitution (which includes settling open decisions 3 to 10), and an irreversible action on production data or real money that no guard bounds. Even then the work ships inert and safe by default, the maintainer is told once with exact steps, and nothing blocks a merge.
- **Template overrides changed** (`.specify/templates/overrides/`): the spec's Constitution Requirements and Assumptions text, the plan's Constitution Check gate for XIII, and the tasks template: "STOP and VALIDATE" is now "VALIDATE … continue when green", "Deploy/demo" is "Deploy", quickstart validation is an automated check, and the Polish phase has a human-step audit task.
- Development Workflow gains one bullet: code review and `/speckit-analyze` treat an added human step as a constitution violation.

### Open follow-ups

- Read together with `.routines/merge-policy.md` (#37, handoff task S3), which landed first and agrees with Principle XIII: no human review, guarded paths get an independent *automated* review, and no open decision is settled. `CLAUDE.md` now links it.
- Open decision 5 says statistical outliers "MUST be reviewed". If that option is chosen, express the review as automated flagging; a person reviewing outliers would be a human step under XIII.
- Amendments to the constitution stay owner-merged under Governance, this one included. The owner can relax that by amending Governance.

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
