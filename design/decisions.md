---
type: design-decisions
---

# Design decisions

The AI's running ledger of the game-design and art calls it made because the
[creative direction](creative-direction.md) or the [style guide](style-guide.md) didn't settle
them. Newest first. Every entry is reversible: say so in feedback, and the AI changes it and
records the reversal here.

Engineering decisions don't go here. They live in each feature's `research.md`, its pull request
and [the patch notes](../docs/patch-notes.md).

Each entry has this shape:

```markdown
## YYYY-MM-DD — <the decision in a few words>

- **Question**: what had to be decided
- **Choice**: what was picked
- **Why**: the pillar or tie-breaker it follows, or "default" when neither applied
- **Alternatives**: what else was considered
- **Where**: the spec, pull request, level or asset page it affects
- **Status**: active, revised (link to the newer entry), or reverted
```

<!-- Add the newest entry below this line. -->
