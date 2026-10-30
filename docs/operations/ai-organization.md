# AI organization

How the studio runs with the director in the loop only for creative direction, playtests and
`needs-decision` items (constitution, Principle XIV). GitHub holds the queue, the guardrails and the
record; Claude Code does the work on the director's Claude subscription; no third-party orchestrator
is needed. This file is guidance subordinate to the constitution: where they differ, the constitution
governs and this file is corrected. Researched 2026-10-29; the Claude Code features named here change
often, so check the sources before relying on a limit.

## Roles

| Role | Who | What it does | Runs on |
| --- | --- | --- | --- |
| Director | The repo owner | Creative direction, feedback, playtests, `needs-decision` items, one-way-door approvals, holds | — |
| Producer | A scheduled GitHub Actions workflow (`on: schedule`), daily, running the Claude Code Action as the Claude GitHub App | Reads the roadmap, creative direction, feedback, patch notes and open issues; files and orders the next issues; triages `blocked` items; raises costed `needs-decision` items for paid resources; writes the weekly digest | GitHub Actions, as the Claude GitHub App |
| Worker | The Claude Code GitHub Action, on the `status/ready` label | One issue per run: Spec Kit stages, tests first, implementation, a pull request | GitHub Actions, as the Claude GitHub App |
| Long-feature worker | Chained Action runs on the feature branch, each under the 6-hour job limit, resuming from the branch and a studio-marker checkpoint comment; or a terminal session whose git author and gh credentials are the App's or a dedicated bot account's, never the director's (a cloud session only if it pushes as the App) | Features too big for one Action run | GitHub Actions, or the terminal |
| Reviewer | The Claude Code GitHub Action, on `pull_request` | Runs `.specify/prompts/code-review.md`, read from the base branch, in a fresh context; fixes reversible findings, at most two fix rounds per pull request; blocks on escalation items | GitHub Actions, as the Claude GitHub App |
| Judge | CI and rulesets | Tests, coverage, Sonar, the license scan, pass rules, the guard check, the level lock check and the `hold` check, as required checks no agent can bypass | GitHub |
| Night shift | Scheduled workflows | Mutation testing, bot playtests, and load and soak runs once hardware exists; a regression files an issue | GitHub Actions and rented hosts |

## Work queue

- **GitHub issues are the queue.** Labels: `status/ready`, `status/in-progress`, `blocked`,
  `needs-decision`, `feedback`, `hold` and `director-approved` (the director's, on pull requests),
  `icebox`, plus `kind/*`
  (feature, fix, chore, spike, audit) and `phase/*` (the roadmap phase). Dependencies use GitHub's
  native "blocked by" relationships and sub-issues, never body text.
- **Issues are self-contained prompts**, enforced by an issue template: the problem, acceptance
  criteria with the tests expected, likely files, constraints, and "done when".
- **Trust boundary.** The repository is public. Workflows start agents only for events by allowlisted
  identities (the director and the project's own agents), checked from the event's author before any
  agent starts, and pass them only content those identities wrote. The workflow fetches the issue or
  pull request body and comments itself, filters them by author, and gives the agent no tool that
  reads unfiltered threads. Agent jobs never run on fork pull requests, and no workflow uses
  `pull_request_target` with a checkout of the pull request's head. Interaction limits (collaborators
  only) are renewed before they expire, or queued issues are locked. Third-party actions are pinned by
  full commit SHA. Agent jobs get least-privilege tokens with no `statuses`, `checks` or `workflows`
  write access. Everything else is data.
- **Kept from the Spec Queue Runner**: hidden comment markers (`<!-- studio:v1 kind=<k> id=<id> -->`) so
  bots find their own comments and never post twice, and the decision and digest comment formats
  (decision: ambiguity, choice, alternatives, rationale, commit; digest: what changed, review results,
  decisions, what was deliberately not done, whether it merged).

## Guardrails (deterministic, on GitHub)

- **Rulesets on `main`**: a "checks" ruleset requires a pull request and the required checks (build
  and tests, coverage, Sonar, the license scan, the review agent's verdict, the guard check, the level
  lock check and the `hold` check), each pinned to the GitHub Actions source, with no bypass actors.
  An "owners" ruleset requires code-owner review and dismisses stale approvals when new commits are
  pushed; its only bypass actor is the repository-admin role in pull-request-only mode, so the
  director can merge their own one-way-door edits. Agents never merge through it, in any session.
- **CODEOWNERS** makes the director the owner of one-way-door paths: pricing and store catalog data,
  legal text, player-data migrations and scripts, CI and deployment configuration, secrets
  configuration, `.github/**` (CODEOWNERS and workflows included), `.claude/**`,
  `docs/operations/ai-organization.md`, `design/creative-direction.md`, `design/style-guide.md` and
  `design/feedback/**`. Those pull requests wait for the director; everything else merges on green.
  A pull request the director's account opened cannot get that review and merges through the
  "owners" bypass, so the guard check also holds its changes to those paths until the director adds
  `director-approved` after its latest push.
- **Guard check** (required) on `.specify/memory/constitution.md`, `CLAUDE.md`, `.specify/prompts/**`
  and `.specify/templates/overrides/**`. Until the director approves, it fails any change, additions
  included, to Principle XIV, the escalation list or the Precedence, Roadmap decisions, Amendments,
  Versioning and Compliance rules; any other deletion or rewrite in Governance except the version
  line; and any deletion or rewrite of a sentence containing MUST, MUST NOT or NON-NEGOTIABLE. In the
  constitution and its scaffold it finds those rules by heading. In the other files, the passages
  that restate them sit between `<!-- guard -->` and `<!-- /guard -->` lines, and a change inside
  them or to those lines counts as a change to the rule. Other changes pass, among them new rules
  elsewhere, new Source reconciliations and new Amendment log rows. The director approves with an
  approving review of the head commit or, on a pull request the director's account opened (GitHub
  does not let an author approve their own pull request, and the "checks" ruleset has no bypass),
  with the `director-approved` label. The check accepts the label only when the director's account
  added it after the latest push: a run that a push starts ignores the label, and a workflow removes
  it. An agent's PATCH or MINOR amendment that the check does not hold, and that touches no
  one-way-door path (this file included), is approved by the review agent's verdict.
- **Review from the base branch**: the review job reads its instructions (the review prompt,
  `CLAUDE.md` and the constitution) from the base branch, and reads the pull request's versions only
  as the diff under review. The guard, level lock and `hold` checks likewise run code and settings
  kept under `.github/**` (the director's account, the guarded rules, the IDs of the `hold` and
  `director-approved` labels) from the base branch, and they re-run on label and review events as
  well as on pushes. They match those labels by ID, never by name, and fail while either is missing
  or renamed, because renaming a label records no event on the pull request.
- **Gate-defining files** (the coverage configuration, the license allowlist, pass-rule thresholds and
  baselines, analyzer configuration and the test quarantine list) change only in a pull request that
  changes nothing else.
- **Level lock check** (required): a pull request fails if it changes, moves, deletes or unlocks an
  object whose last director-committed state is locked, or if it sets `author: director`, unless the
  director's account opened it and added the `director-approved` label after its latest push.
  Sessions that act as the director commit as the director, so authorship alone never passes it.
- **`hold`**: the director can hold or close any pull request, with the `hold` label or a blocking
  review. A required check fails while the pull request, or another one from the same head branch,
  has the label or the director's latest review other than a comment requesting changes (a reply in
  a review thread does not release it); while another pull request from that head branch stays
  closed after the director closed it, until the director approves this one; and when anyone but
  the director made the label's last removal, dismissed that review or reopened the pull request
  after the director closed it. The director releases a hold carried over from another pull request
  on that one, by removing the label or approving or dismissing the review. Agents never override a
  hold.
- **Agent identity**: no agent run acts through the director's GitHub account. Agent commits, pull
  requests, issues, labels, comments and reviews come only from the Claude GitHub App or a dedicated
  bot account, so the director can approve escalated pull requests and the level lock check can trust
  commit identity, and never from the default `GITHUB_TOKEN` (pushes made with it don't trigger CI). A
  session that can act only as the director (a cloud session, the director's own terminal) is the
  director's own work: the director attends it and owns its pull requests. A Claude Code scheduled
  task also acts as the director but runs unattended, so it runs only read-only checks and never
  commits, pushes or writes pull requests, issues, labels, comments or reviews. In no session do
  agents add `director-approved`, approve a pull request as the director, merge through a ruleset
  bypass, remove `hold`, rename or delete either label, dismiss the director's review or reopen a pull
  request the director closed.
- **Merging**: Allow auto-merge is on, and the worker enables squash auto-merge on its own pull
  requests as the Claude GitHub App. Required workflows run on every pull request with no path filters
  and fail closed: a check that cannot run, an untrusted author or an unparseable review verdict fails
  the check and never skips it, and the verdict comes from structured output. A merge queue needs an
  organization-owned repository; moving this one to a free GitHub organization is the director's
  call. Until then there is no "branch up to date" requirement: main's CI runs after each merge, and a
  merge that breaks it is reverted and gets an issue. With a merge queue, every required workflow also
  triggers on `merge_group`; there the review workflow does not re-run the review, but re-reports the
  verdict recorded for that pull request's head commit and fails if there is none.
- **Local controls**: deny rules and PreToolUse hooks in `.claude/settings.json` for dangerous
  commands, among them any command or GitHub tool call that adds `director-approved`, approves a pull
  request, merges through a ruleset bypass, removes `hold`, renames or deletes either label,
  dismisses a review or reopens a pull request the director closed (a hook also sees
  `gh api` and MCP calls, which prefix-matched deny rules miss). Projects cloud threads do not read
  those permission rules, so the GitHub-side controls are the primary ones, except in a session that
  holds the director's credentials: there GitHub cannot tell the agent from the director, and only
  these rules and hooks stop it.
- **Bounded runs**: `--max-turns` and job timeouts on every run; `CLAUDE_CODE_RETRY_WATCHDOG=1` so a run
  waits out a usage limit instead of failing; one concurrency group per issue; after two failed CI
  rounds the worker parks the branch and labels the item `blocked` (a check that waits for the
  director's approval is not a failed round); the reviewer pushes at most two fix rounds per pull
  request.
- **Pinned versions**: the Action, Claude Code and Spec Kit versions are pinned in the workflows and
  upgraded through a pull request plus a smoke run.

## Escalations and updates

- **`needs-decision`**: the agent writes the question on the item, with options and a recommended
  answer, parks that item and moves on. Remote Control and the mobile app can push a notification.
- **Security channel** (escalation item 5): the agent never describes a suspected or confirmed
  vulnerability or incident in an issue, pull request, commit, CI log or repository file. It writes
  the question, options and recommendation in a draft repository security advisory, files a neutral
  `needs-decision` item that links nothing sensitive, and keeps exploit detail out of public pull
  requests until the director publishes the advisory.
- **Nothing sensitive in public**: no secrets, tokens, player data, IP addresses or unredacted Staging
  or Production output in the repository, pull-request evidence included. Dev and Staging are not
  reachable by the public without the director's approval (escalation item 2).
- **Roadmap decisions**: agents decide each roadmap proposed change and open question before the
  first feature of the sub-spec it affects is specified, recording the decision in the phase spec and
  the reasoning in that feature's `research.md` (constitution, Governance, Roadmap decisions). Only
  the escalation items among them become `needs-decision`, such as the hosting provider and region,
  paid DDoS scrubbing, spare continent nodes, merchant-of-record fees, the default visibility of
  per-player stats, launch regions, the minimum age and moderation staffing.
- **Weekly digest** (written by the producer as an issue): what shipped, the decisions made, a link to
  the latest playable build (only through Dev's access control, so none before Dev is hosted), the key
  metrics, the open `needs-decision` items and the open pull requests waiting for the director's
  approval. An agent whose pull request the guard check holds requests the director's review on it.
- **Running records**: `docs/patch-notes.md` for behavior changes, `design/decisions.md` for design
  calls.

## What it costs

- **Claude**: the director's existing subscription. Cloud sessions, scheduled tasks and the Action
  (with a `claude setup-token` OAuth token, valid for a year) all draw from the plan's 5-hour and weekly
  limits, shared with interactive use. Unattended work waits for resets; buying usage credits is an
  escalation. Anthropic's terms assume ordinary, individual usage, so keep automation volume moderate.
- **GitHub Actions**: free on standard runners, because this repository is public. GPU runners (for the
  client frame-time test), rented load-test hardware and a hosted Dev environment cost money, which
  makes them escalation items. The producer raises one costed `needs-decision` per paid resource
  (load-test hosts, GPU runner, Dev environment) one phase before it is needed. Until the director
  approves the hardware, load-backed acceptance criteria are marked `pending-hardware` and block their
  roadmap gate, not the merge (constitution, Principle X).

## Rollout

Prove each step by hand on a few real items before it runs unattended.

1. **Now**: keep working in interactive ultracode sessions, and adopt the labels, the issue template,
   `needs-decision` and the weekly digest. These sessions act as the director, so they count as
   director-attended work whose pull requests the director owns. To hand work off unattended,
   configure the session's git author and gh credentials as the Claude GitHub App or a bot account.
   Interactive sessions already resume on their own after a usage limit resets, if the reset is under
   24 hours away.
2. **First code pull request**: CI, the "checks" and "owners" rulesets, CODEOWNERS, the guard,
   level lock and `hold` checks, the workflow that removes `director-approved` on every push, the
   deny rules and hooks in `.claude/settings.json`, and auto-merge.
3. **Reviewer**: the Action on `pull_request`, with the review prompt read from the base branch.
4. **Worker**: the Action on `status/ready`, for small and pattern-following items; long features run
   as chained Action runs or in sessions that act as the App or a bot account.
5. **Producer**: a daily scheduled GitHub Actions workflow that runs the Action as the App to groom
   the backlog and write the digest.
6. **Night shift**: mutation testing and bot playtests, then load tests once hardware exists.

Known limits to design around: scheduled tasks act as the director's GitHub user, so they are used
only for read-only checks; GitHub runs scheduled workflows only from the default branch and, in a
public repository, disables them after 60 days without repository activity; each trigger starts a
fresh session, so all state lives in GitHub; the `ultracode` keyword does not start a workflow from a
scheduled-task or `-p` prompt, so unattended runs ask for multi-agent workflows explicitly when they
need them.

## Alternatives considered

| Option | Verdict |
| --- | --- |
| Spec Queue Runner (the director's own) | Retired. About ten defects, and it never ran unattended: headless stages couldn't build or commit, review fixes landed after merge, safety rails were never wired in. Native features now cover its purpose. |
| GitHub Agentic Workflows | GA on 2026-10-28, but its Claude engine needs an API key; subscription tokens are unsupported. |
| GitHub Copilot cloud agent | Paid credits at API rates, the Copilot harness rather than Claude Code, and automations documented for private repositories. |
| Tembo | Runs Claude Code on the subscription, but adds about $60 a month, and its token handling is undocumented. |
| OpenHands Agent Canvas | MIT and can run Claude Code, but about four months old with weekly churn; local automations need custom webhooks. |
| Paperclip and "AI company" frameworks (MetaGPT, ChatDev, CrewAI) | Heavy churn or demo-grade; no evidence of shipping real software unattended. |
| A custom orchestrator on the Agent SDK or Managed Agents | API billing, and it rebuilds what the Action and scheduled workflows already provide. |

## Housekeeping for the retired runner

- Retired on 2026-10-29: pull requests 3 and 18 in `nbon12/spec-queue-runner` are closed with their
  branches kept; the `deploy/branch-protection.sh` script from pull request 18 is worth reusing for
  this repository's rulesets.
- For the director: revoke the retired runner's GitHub token, and archive the spec-queue-runner
  repository once nothing more is needed from it.

## Sources

- Claude Code: [cloud sessions](https://code.claude.com/docs/en/claude-code-on-the-web.md),
  [scheduled tasks](https://code.claude.com/docs/en/routines.md),
  [GitHub Actions](https://code.claude.com/docs/en/github-actions.md),
  [permission modes](https://code.claude.com/docs/en/permission-modes.md),
  [authentication](https://code.claude.com/docs/en/authentication.md),
  [legal and compliance](https://code.claude.com/docs/en/legal-and-compliance.md),
  [interactive mode](https://code.claude.com/docs/en/interactive-mode.md),
  [Projects](https://code.claude.com/docs/en/claude-projects.md),
  [Remote Control](https://code.claude.com/docs/en/remote-control.md)
- GitHub: [Actions limits](https://docs.github.com/en/actions/reference/limits),
  [Agentic Workflows GA](https://github.blog/changelog/2026-10-28-github-agentic-workflows-generally-available/),
  [gh-aw Claude engine](https://github.github.com/gh-aw/reference/engines/claude/),
  [Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
- Others: [Tembo pricing](https://www.tembo.io/pricing),
  [OpenHands Agent Canvas](https://openhands.dev/blog/introducing-agent-canvas),
  [Paperclip](https://github.com/paperclipai/paperclip)
