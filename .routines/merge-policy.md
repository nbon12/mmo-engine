# Merge policy: when a session merges its own PR

Source: nbon12/hoa_management_system `docs/handoffs/2026-11-02-zero-touch-operations.md` §3–4 and
its §8 decision D1. nbon12 answered it on 2026-11-02 ([decision record](https://github.com/nbon12/hoa_management_system/issues/295#issuecomment-6590561424)):
guarded-path PRs get an automated review and merge without asking for permission. Never ask nbon12 to
review, approve or merge a PR. The one exception is a constitution amendment, which includes settling
an item on the constitution's "Open decisions" list (rule 6 and the constitution exception below).

A session's own PR is merged by `.github/workflows/auto-merge.yml` once the session marks it ready (see
[Auto-merge](#auto-merge)), unless it touches a guarded path: then the session **squash-merges it**
itself, when every rule below holds on the PR's current head commit. Either way, every rule below must
hold first. "Its own PR" means a PR this session, or the session or routine that handed it the work,
opened. Never merge someone else's PR under this policy.

## The rules

1. **The build and tests pass.** This repo has no CI. Run the build and tests the repo has, using
   commands you derive from the repo rather than guess, and list what you ran and the results in the PR.
   If the repo gains CI, every check run must also be complete, with none failed or cancelled.
2. **Reviews are settled.** No review thread is unresolved, and no review has requested changes.
3. **The PR is ready.** It isn't a draft, and its description lists what was verified.
4. **Patch notes are in.** The PR includes a `docs/patch-notes.md` entry whenever `CLAUDE.md` requires
   one, which is for features and behavior-changing fixes.
5. **Guarded paths get an independent review.** If the PR touches a guarded path (below), it has passed
   the independent automated review described below.
6. **No open decision is settled.** The PR doesn't settle an item on the constitution's "Open decisions"
   list. Settling one is a constitution amendment. Design the change so the decision stays open, call
   it out in the PR, and merge. Only a PR that can't avoid settling it is left for nbon12, as an
   amendment.

## Auto-merge

`.github/workflows/auto-merge.yml` squash-merges a PR that nbon12 opened (every session's PR is) once
every conversation on it is resolved and every check has passed. There is no CI yet, so there are no
checks, and it merges as soon as the PR is ready. It checks rule 5 and the parts of rules 2 and 3 that
GitHub can see: no unresolved conversation, no review requesting changes, not a draft. It can't check
the rest, so:

- **Open your PR as a draft** (`gh pr create --draft`), and mark it ready (`gh pr ready`) only once
  rules 1–4 and 6 hold. Marking it ready is the decision to merge, and here it merges within seconds.
- **To stop a merge,** add the `hold` label; removing it lets auto-merge carry on.
- **A PR touching a guarded path** isn't auto-merged. Follow the independent review below, then merge it
  yourself.

## Guarded paths

These are the code that enforces the guardrails, or that governs the project: all of `.github/`,
`.routines/`, `.claude/`, the constitution, any `CLAUDE.md`, the architecture spec and `infra/`. The
exact list is `GUARDED_PATHS` in `.github/workflows/auto-merge.yml`, which keeps these PRs out of
auto-merge. Change the list there, and only there.

## Independent automated review (guarded paths)

1. **Start an independent reviewer.** Use a fresh subagent (the `Agent` tool, `general-purpose`): it
   runs in any turn with no click from nbon12. Use a separate session (`create_session`, titled "Review
   <owner>/<repo>#<n>") only when the subagent can't run, because the desktop app lets one session
   start another only on nbon12's own message: anywhere else it becomes a card nbon12 has to click.
   Either way, give it only the PR link and tell it to read the diff from the PR. Don't give it your
   own summary or conclusions. Its prompt: run the `code-review` skill at `high` against that PR with
   `--comment`, so its findings are posted on the PR, and then end its turn.
2. **Fix every confirmed blocking finding.** Push the fixes, and answer each finding on the PR: fixed, or
   why it isn't a problem. If you make more than a small, targeted fix, have a new reviewer check it
   again.
3. **Merge, then notify.** When the other rules hold again, merge. Then send nbon12 one push notification
   (`PushNotification`), such as "merged mmo-engine#N touching guarded paths: <paths>", so they can review
   it after the fact.

**Constitution exception:** the constitution's Governance section requires amendments to be "reviewed and
approved like an architectural decision record". A PR that changes `.specify/memory/constitution.md`
stays **owner-merged** until nbon12 changes that rule. Leave it open, ready, and say so on the PR and its
issue.

## Never, under any policy

- Skip, disable or quarantine a test to get green.
- Merge with a failing build or test.
- Force-push `main`.
- Approve a review on your own PR.

## Still the owner's

- Creating or rotating root credentials, accounts and billing.
- GitHub settings that need admin: rulesets and secrets.
- Constitution amendments, including settling an open decision.
- Anything on nbon12's own computer.

Everything else should end with a notification, not a question.
