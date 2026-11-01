# Roadmap

The phases the game is built in, each ending at a gate with exit criteria. The constitution
(Principle XII) makes these gates binding: work that depends on a gate's outputs starts only after
the gate passes. Imported from Claude Docs on 2026-10-29; edit them here from now on.

| Phase | Spec | Ends at |
| --- | --- | --- |
| Overview | [Sub-spec breakdown](sub-spec-breakdown.md): the 17 sub-specs in build order | — |
| Pre-Phase 1 | [Regionizer simulator and test harness](pre-phase-1-regionizer-simulator-and-test-harness.md) | The decision memo |
| Phase 1 | [Netcode and smoothness on one region](phase-1-netcode-and-smoothness.md) | Gate 9: baselines, and the parallel-phases decision |
| Phase 2 | [Regionizer and fight cap](phase-2-regionizer-and-fight-cap.md) | Gate 12: the stress ladder sets the fight cap |
| Phase 3 | [Platform: persistence, login, hosting and public API](phase-3-platform.md) | A hosted alpha on Staging |
| Phase 4 | [Scaling past one host](phase-4-scaling-past-one-host.md) | Stages A, B and C, each on its own trigger and gate |
| Phase 5 | [Launch readiness](phase-5-launch-readiness.md) | The public beta gate (needs the director's approval) |

Phase 5 builds on Phase 3 and can run before Phase 4. The Sep 30 architecture spec's original
four-phase roadmap is in [../architecture/history/](../architecture/history/server-architecture-2026-09-30.md);
these specs supersede it.

Each phase spec ends with proposed changes to the architecture spec and a list of open questions.
Agents decide both under the constitution's Governance, Roadmap decisions, before the first feature
of any sub-spec they affect is specified; only roadmap items on the escalation list wait for the
director. Each Proposed changes table has a Decision column that reads Yes, No, Pending, or Partly
(the constitution encodes the part named, and the rest still needs a decision), and an answered
question records its answer in its Status column. Each decision names its source:
"constitution 1.0.0", or the deciding feature, whose `research.md` holds the reasoning. A No also
corrects the spec's body. Until a change is decided, the constitution and the architecture spec
govern that point.
