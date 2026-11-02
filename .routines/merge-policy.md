# Merge policy: when a session merges its own PR

Source: nbon12/hoa_management_system `docs/handoffs/2026-11-02-zero-touch-operations.md` §3–4 (issue
nbon12/hoa_management_system#295). Decision D1 is still open there. Until nbon12 answers it, sessions
follow the recommended answer, which is what this file says.

A session **squash-merges its own PR** when every rule below holds on the PR's current head commit. "Its
own PR" means a PR this session, or the session or routine that handed it the work, opened. Never merge
someone else's PR under this policy.

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
   list. If it would, leave it open for nbon12 and say so.

## Guarded paths

These are the code that enforces the guardrails, or that governs the project:

```
.github/workflows/**          .routines/**              .claude/**
.specify/memory/constitution.md                         CLAUDE.md
docs/architecture/server-architecture.md               infra/**
```

## Independent automated review (guarded paths)

1. **Start a separate reviewer session.** Use `create_session` with this repo as the source, titled
   "Review <owner>/<repo>#<n>". Give it only the PR link and tell it to read the diff from the PR.
   Don't give it your own summary or conclusions. Its prompt: run the `code-review` skill at `high`
   against that PR with `--comment`, so its findings are posted on the PR, and then end its turn.
2. **Fix every confirmed blocking finding.** Push the fixes, and answer each finding on the PR: fixed, or
   why it isn't a problem. If you make more than a small, targeted fix, have a new reviewer session check
   it again.
3. **Merge, then notify.** When the other rules hold again, merge. Then send nbon12 one push notification
   (`PushNotification`), such as "merged mmo-engine#N touching guarded paths: <paths>", so they can review
   it after the fact.

**Constitution exception:** the constitution's Governance section requires amendments to be "reviewed and
approved like an architectural decision record". A PR that changes `.specify/memory/constitution.md`
stays **owner-merged** until nbon12 changes that rule. Leave it open, ready, and say so on the PR and its
issue.

**Tuning and content exception:** the constitution's Development Workflow (v1.1.0) requires a human
maintainer to approve any PR that changes tuning values or gameplay content parameters, and forbids
scheduled tasks and agents from merging one. Such a PR is also **owner-merged**: leave it open and ready,
and say so on the PR and its issue.

## Never, under any policy

- Skip, disable or quarantine a test to get green.
- Merge with a failing build or test.
- Force-push `main`.
- Approve a review on your own PR.

## Still the owner's

- Creating or rotating root credentials, accounts and billing.
- GitHub settings that need admin: rulesets and secrets.
- Product and spec decisions, open decisions, and constitution amendments.
- Anything on nbon12's own computer.

Everything else should end with a notification, not a question.
