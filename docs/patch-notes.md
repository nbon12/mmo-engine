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
- Only `OWNER`/`MEMBER`/`COLLABORATOR` mentions start a run. Everyone else's are skipped.
- Needs the `CLAUDE_CODE_OAUTH_TOKEN` (or `ANTHROPIC_API_KEY`) repository secret and the Claude GitHub App.

### Open follow-ups

- No build toolchains yet. When application code lands, add the setup steps and allow the CI build and test commands (spec FR-010).
- The workflow contract check and the live smoke test the spec calls for don't exist yet.

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
