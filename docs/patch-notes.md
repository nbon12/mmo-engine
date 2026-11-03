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

## 2026-11-03 — @claude mention agent on GitHub

Spec: [specs/001-claude-mention-agent/spec.md](../specs/001-claude-mention-agent/spec.md) · PR: #35

### Behavior and contract changes

Tooling only. No application change.

- Mentioning `@claude` in an issue, issue comment, PR review or PR review comment runs Claude Code (`.github/workflows/claude.yml`). It replies on the same thread, and for a change it pushes a branch and offers a PR link.
- Only `OWNER`/`MEMBER`/`COLLABORATOR` mentions start a run. A mention on the sweep log issue (label `sweep-log`) never does, even from nbon12. That closes the follow-up from the #43 entry below.
- Only issue *opened* triggers on `issues`; assigning an issue doesn't.
- Runs for one issue or PR never overlap, but GitHub keeps only one waiting: in a burst of mentions, only the newest waiting one gets a reply.
- `auto-merge.yml` no longer merges a PR whose head commit is from `claude[bot]`. Merge it by hand, or push a reviewed commit on top. It also re-decides when `Workflow tests` finishes.
- New CI: `Workflow tests` (`.github/workflows/workflow-tests.yml`) runs `tests/workflows/` on PRs and pushes touching `.github/` or the tests.
- `.github/dependabot.yml` now opens weekly bump PRs for the SHA-pinned actions.
- Needs the `CLAUDE_CODE_OAUTH_TOKEN` (or `ANTHROPIC_API_KEY`) repository secret and the Claude GitHub App.

### Assumptions that no longer hold

- The Done story sweep entry below says this repo has no `claude.yml`, so `@claude` handoffs aren't available. They are now, and `.routines/done-story-sweep.md` says so.

### Shared pieces to reuse

- `tests/workflows/test_claude_workflow.py` has `Expr`, an evaluator for the GitHub Actions expression subset used in `if:` conditions. Use it to test other workflows' gates against event payloads.

### Open follow-ups

- No build toolchains yet. When application code lands, add the setup steps and allow the CI build and test commands (spec FR-010).
- A live smoke test (that `claude[bot]` actually replies) needs a write-access user's token as a secret. See the spec's known gap.
- hoa_management_system's `claude.yml` doesn't have the `sweep-log` guard, the `assigned` fix or the auto-merge exclusion yet.

## 2026-11-02 — Done story sweep: find the newest report from the last page of the log

Spec: none (maintenance) · PR: [#44](https://github.com/nbon12/mmo-engine/pull/44), with [nbon12/hoa_management_system#313](https://github.com/nbon12/hoa_management_system/pull/313)

### Behavior and contract changes

Tooling only. Section 6 now defines one rule, "Finding the newest report", which both the notify step (6.1, before posting) and the watchdog use. It reads the comment count, fetches only the last page with perPage 100, and steps back a page only if needed. A failed fetch is retried once, then treated as a failed lookup, never as a partial list. Before this, "page to the last page" couldn't be followed reliably, and the notify step didn't page at all. Once the log passed 30 comments, bot replies included, either step could read an old report: the notify step would send repeat pushes, and the watchdog would raise a false "didn't run" alert. hoa_management_system gets the same fix in [#313](https://github.com/nbon12/hoa_management_system/pull/313); the two merge together.

## 2026-11-02 — Done story sweep: no at-mentions of Claude; watchdog reads the newest report

Spec: none (maintenance) · PR: [#43](https://github.com/nbon12/mmo-engine/pull/43), with [nbon12/hoa_management_system#312](https://github.com/nbon12/hoa_management_system/pull/312)

### Behavior and contract changes

Tooling only.

- The watchdog now reads nbon12's newest comment on the `sweep-log` issue that starts with "Done story sweep report". It used to read the newest comment of any kind, so a bot reply could cause a false "didn't run" alert.
- Sweep comments must not at-mention Claude. In hoa_management_system that mention starts `claude.yml` as nbon12, which happened once on its [log issue #290](https://github.com/nbon12/hoa_management_system/issues/290). This repo has no Claude workflow yet; the rule is for when it gets one.

### Open follow-ups

- A prompt rule depends on every writer remembering it. The root-cause fix is a guard in the Claude workflow itself, for example skipping issues labeled `sweep-log`. Add it when this repo gets a `claude.yml`; hoa_management_system tracks the same follow-up.

## 2026-11-02 — Merge policy: the independent review runs in a subagent

Spec: none (process) · PR: #42

### Assumptions that no longer hold

Process and agent guidance only. No application change.

- **A guarded-path pull request is reviewed by a fresh subagent, not a separate session**
  (`.routines/merge-policy.md`, the independent review's step 1). Pass it your session's `model`. The
  policy is nbon12's standing request for that subagent, so don't ask first. In the desktop app, a
  session can start another session only on nbon12's own message. From an automated turn the start
  becomes a card nbon12 has to click, which is why this changed.
- **Fallback:** if the subagent fails twice, start a separate session: `create_session` in the cloud,
  `start_session` on the desktop. If that can only be a card, say on the pull request that it's
  waiting for a reviewer, and start the reviewer in the next turn nbon12 starts.
- The same change is nbon12/hoa_management_system#311, where an independent review checked this
  wording.

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
