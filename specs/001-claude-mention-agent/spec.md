# Feature Specification: @claude Mention Agent on GitHub

**Feature Branch**: `001-claude-mention-agent`

**Created**: 2026-11-02

**Status**: Draft

**Input**: User description: "Create a spec for the @claude behavior on GitHub issues: mentioning
@claude on an issue or PR runs Claude Code against the repo. This is one of the automations that
make the project possible, so the specs should cover it. A lightweight spec with only a spec.md is
fine."

## Context: the project's automations

This project is built largely by agents working in parallel, so a few automations carry it. This
spec covers the one that runs **on demand**: the @claude mention agent. The others run on a
schedule and are specified by their own prompts:

| Automation | Trigger | Defined in |
|------------|---------|------------|
| **@claude mention agent** (this spec) | A write-access user mentions `@claude` on an issue or PR | `.github/workflows/claude.yml` |
| Done story sweep | Scheduled task | `.routines/done-story-sweep.md` |
| Nightly Repo Hygiene | Scheduled task, nightly | `.routines/nightly-repo-hygiene.md` |
| Architecture Police | Scheduled task, nightly | `.claude/skills/architecture-police/SKILL.md` |

The mention agent is how the owner hands a single issue or PR to an agent without opening a
session: write the request on GitHub, mention `@claude`, and the reply arrives on the same thread.
The scheduled routines, in turn, leave findings and stories as issues that the owner can hand back
with a mention.

The workflow is the same one `nbon12/hoa_management_system` runs (its spec
`032-claude-mention-agent`), minus the build toolchains, because this repo has no application code
yet. It was added in PR #35.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Hand an issue or PR to Claude by mentioning it (Priority: P1)

The owner, or another collaborator with write access, writes a request on an issue, a comment, a
PR review or a PR review comment, and mentions `@claude`. Claude Code runs against the repository
on GitHub Actions and posts its answer on the same thread, with a link to the run. For a question,
the reply is the answer. For a change, Claude pushes a branch and the reply offers a link to open
the PR.

**Why this priority**: This is the whole feature. Without it, every piece of agent work needs a
session opened by hand.

**Independent Test**: On the default branch, with the credential secret set and the Claude GitHub
App installed, open an issue as the owner whose body mentions `@claude` and asks for a one-line
answer. A `Claude` workflow run starts for the `issues` event and succeeds, and `claude[bot]`
comments on that issue with the answer and a link to the run.

**Acceptance Scenarios**:

1. **Given** the workflow is on the default branch, a credential secret is set and the Claude
   GitHub App is installed, **When** a user with write access opens an issue whose title or body
   mentions `@claude`, **Then** a `Claude` run starts and `claude[bot]` posts a comment on that
   issue with the result and a link to the run.
2. **Given** the same setup, **When** a user with write access posts a new comment mentioning
   `@claude` on an existing issue or PR, **Then** a run starts and `claude[bot]` replies on that
   issue or PR.
3. **Given** the same setup, **When** a user with write access submits a PR review, or a PR review
   comment, whose body mentions `@claude`, **Then** a run starts and `claude[bot]` replies on that
   PR.
4. **Given** the same setup, **When** a comment, issue or review that does not mention `@claude` is
   created, **Then** the `Claude` job is skipped and nothing is posted.
5. **Given** a request that asks for a change to the repository, **When** Claude finishes, **Then**
   it has pushed its work to a new branch (or, for a request on a PR, to that PR's head branch), it
   has not pushed to the default branch or merged anything, and its reply links to the branch or
   offers a link to open the PR.

---

### User Story 2 - Outsiders cannot start a run (Priority: P1)

Anyone who can comment can type `@claude`. Only people with write access can start a run, so an
outsider cannot spend the credential's quota or point an agent with write permissions at the repo.

**Why this priority**: The job holds write permissions and a paid credential. Leaving it open to
anyone would be a security hole, so this ships with Story 1, not after it.

**Independent Test**: Read the workflow's job condition and confirm that every trigger path
requires an author association of `OWNER`, `MEMBER` or `COLLABORATOR`. Then mention `@claude` from
an account without write access and confirm that the job is skipped.

**Acceptance Scenarios**:

1. **Given** a user whose author association is not `OWNER`, `MEMBER` or `COLLABORATOR`, **When**
   they open an issue, comment, or review mentioning `@claude`, **Then** the `Claude` job is skipped:
   no Claude Code step runs, no credential is used and nothing is posted.
2. **Given** a bot account such as Dependabot (author association `CONTRIBUTOR` or `NONE`), **When**
   it comments on its own PR, **Then** the job is skipped.

---

### User Story 3 - A missing credential fails loudly (Priority: P2)

If neither credential secret is set, a mention must fail visibly, with an error that names what is
missing, instead of silently doing nothing.

**Why this priority**: This happened in `hoa_management_system` before its token was added: two
mentions failed and the cause was only visible in the run log. The behavior already exists in the
action; this story keeps it from regressing.

**Independent Test**: In a repository with the workflow but neither secret set, mention `@claude`
as the owner. The run fails, and its log says that `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN`
is required.

**Acceptance Scenarios**:

1. **Given** neither `CLAUDE_CODE_OAUTH_TOKEN` nor `ANTHROPIC_API_KEY` is set, **When** a
   write-access user mentions `@claude`, **Then** the run concludes `failure` and its log names the
   missing credential.
2. **Given** exactly one of the two secrets is set, **When** a write-access user mentions
   `@claude`, **Then** the run uses that secret and does not fail for lack of the other.

---

### Edge Cases

- **Several mentions on one issue or PR**: runs for the same issue or PR queue one behind another
  (concurrency group per issue/PR number, `cancel-in-progress: false`). A later mention never
  cancels work in progress.
- **Edited comments**: only newly created comments trigger. Editing an old comment to add
  `@claude` does nothing.
- **Assigning an issue**: the `issues: assigned` trigger runs Claude again when an issue whose
  title or body mentions `@claude` is assigned.
- **Events created by `GITHUB_TOKEN`**: GitHub does not start workflows for events that another
  workflow created with `GITHUB_TOKEN`, so a workflow cannot trigger Claude by posting a comment.
  Scheduled routines that want Claude must post as a write-access user.
- **Long or runaway work**: a run stops after 60 minutes or 100 agent turns, whichever comes first.
- **Workflow changes on a branch**: `issue_comment` and `issues` workflows always run from the
  default branch. A change to `claude.yml` takes effect only after it merges.
- **Untrusted text in the thread**: Claude reads the whole issue or PR, including comments by
  people without write access. That text can try to steer the agent (prompt injection). The gate
  stops outsiders from *starting* a run, but not from writing text that a run later reads.
  Least-privilege permissions and the no-merge rule limit what a steered run could do.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST define a `Claude` GitHub Actions workflow at
  `.github/workflows/claude.yml`. It MUST trigger on `issue_comment` (created),
  `pull_request_review_comment` (created), `pull_request_review` (submitted) and `issues` (opened,
  assigned).
- **FR-002**: The job MUST run only when the triggering text mentions `@claude` (comment body,
  review body, or issue title or body) **and** the author association is `OWNER`, `MEMBER` or
  `COLLABORATOR`. In every other case it MUST be skipped before any step runs.
- **FR-003**: The job MUST authenticate with `CLAUDE_CODE_OAUTH_TOKEN` (Claude subscription) or
  `ANTHROPIC_API_KEY` (Console API key), whichever repository secret is set. No credential may
  appear in the workflow file.
- **FR-004**: The job MUST request only these token permissions: `contents: write`,
  `pull-requests: write`, `issues: write`, `id-token: write` and `actions: read`.
- **FR-005**: Every third-party action MUST be pinned to a full commit SHA with a version comment,
  matching the repository's other workflows. Dependabot keeps the pins current.
- **FR-006**: Runs MUST be serialized per issue or PR, and a new run MUST NOT cancel a run in
  progress.
- **FR-007**: A run MUST be bounded at 60 minutes and 100 agent turns.
- **FR-008**: Claude MUST report back on the triggering thread, with a link to the run.
- **FR-009**: Claude MUST NOT merge pull requests or push directly to the default branch. Changes go
  to a new branch, or to the head branch of the PR it was asked about.
- **FR-010**: While the repository holds only specs and docs, the job MUST NOT install build
  toolchains or allow build commands. When application code lands, the spec that introduces it MUST
  add the matching setup steps, and allow the same build and test commands CI runs, so that Claude
  can verify its own changes.
- **FR-011**: If no credential is configured, the run MUST fail with an error that names the
  missing secret.

### Key Entities

- **Mention request**: an issue, comment, review or review comment that mentions `@claude`, written
  by a write-access user. It is the prompt for one run.
- **Claude run**: one `Claude` workflow run. It belongs to a single issue or PR and ends in a reply
  on that thread.
- **Credential secret**: `CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY`, stored as a repository
  secret.

### Constitution Requirements *(mandatory when applicable)*

- **Component and surface**: Repository tooling (GitHub Actions) only. No browser client, gateway,
  continent server, persistence tier, orchestration or web surface is touched.
- **State ownership**: N/A. The feature holds no game or web state. Its only state is the GitHub
  thread and the branches it pushes.
- **Interaction class**: N/A. Not a gameplay interaction.
- **Authority and disclosure**: N/A for the game. The equivalent rule is FR-002: GitHub's author
  association decides who can start a run.
- **Persistence and write policy**: N/A. No data is persisted beyond GitHub itself.
- **Contracts**: N/A. No wire protocol or HTTP API. The workflow's triggers and job condition are
  its contract (FR-001, FR-002).
- **Performance and evidence**: N/A for the tick path. Run duration is bounded by FR-007.
- **Security and abuse controls**: Write-access gate (FR-002), credentials only in repository
  secrets (FR-003), least-privilege token (FR-004), SHA-pinned actions (FR-005), no merge or direct
  push to the default branch (FR-009), bounded runs (FR-007). The prompt-injection risk from
  untrusted thread text is listed under Edge Cases.
- **Observability**: Each run's log and outcome are in the Actions tab, and the reply links to the
  run. No game telemetry is involved.
- **Licensing and design sources**: `anthropics/claude-code-action` (MIT) and `actions/checkout`
  (MIT). No new runtime dependency.
- **Accessibility**: N/A. No web surface.
- **Quality gates**: No application code, so coverage and Sonar targets do not apply. See below for
  how the scenarios are verified.
- **Executable and living spec**: Each scenario maps to one of two automated checks. Neither exists
  yet; building them is this spec's open follow-up.
  - A **workflow contract check** parses `.github/workflows/claude.yml` and asserts FR-001 to FR-007
    and FR-010: the triggers, the `@claude` and author-association condition on every path, both
    secrets wired, the exact permission set, full-SHA pins, the concurrency settings and the
    timeout and turn limits. It runs in CI on every PR that touches the workflow. It covers
    Story 2's scenarios and the skip in Story 1, scenario 4.
  - A **live smoke test** (`workflow_dispatch`, on demand) opens an issue as a write-access user,
    mentions `@claude` with a one-line question, waits for a `claude[bot]` reply, asserts that
    the reply links to the run, then closes the issue. It covers Story 1, scenarios 1 to 3. It must
    post with a write-access user's token, not `GITHUB_TOKEN` (see Edge Cases).
  - The contract check covers the configuration side of Story 1, scenario 5 and Story 3, scenario 2:
    no step merges, and both secrets are wired with neither hard-coded.
  - **Known gap**: Story 1, scenario 5 (Claude's own branch behavior) and Story 3, scenario 1 (the
    missing-credential error) rely on `claude-code-action`'s behavior. No test of ours verifies
    them yet. The error was observed in `hoa_management_system` runs 40960028942 and 41230905717.
    Closing this gap is part of the follow-up.
- **Spec independence and parallelism**: Individually completable. It does not depend on any other
  spec. `hoa_management_system`'s `032-claude-mention-agent` describes the same workflow there; the
  two can change independently.
- **Open decisions**: None. This feature does not touch the constitution's Open decisions list.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A one-line question to `@claude` from the owner gets a `claude[bot]` reply on the
  same issue within 3 minutes of posting.
- **SC-002**: Zero `Claude` runs execute the Claude Code step for a request from an author without
  write access.
- **SC-003**: 100% of the actions in `claude.yml` are pinned to a full commit SHA.
- **SC-004**: The owner can hand any open issue to an agent by writing one comment, without opening
  a session.

## Assumptions

- The Claude GitHub App is installed on the repository, and the owner has set one credential secret
  (`CLAUDE_CODE_OAUTH_TOKEN` was added on 2026-11-02).
- The repository stays owned by a personal account, so `OWNER` and `COLLABORATOR` are the
  associations that matter. `MEMBER` is kept so the gate still holds if the repo moves to an
  organization.
- `claude-code-action` decides how Claude replies, which branch it pushes to and how it tracks
  progress in its comment. This spec pins the version and does not restate the action's internals.
- Application build and test tooling is out of scope until application code exists (FR-010).
