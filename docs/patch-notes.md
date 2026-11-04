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

## 2026-11-04 — Telemetry and test harness, slice 3: one-command stack and the Harness workflow (Pre-Phase 1, sub-spec 2)

Spec: [specs/014-telemetry-test-harness/spec.md](../specs/014-telemetry-test-harness/spec.md) · PR: (this PR) · Issue: #13

### Behavior and contract changes

- **`infra/harness/up.sh`** is the one command:
  - It brings up kind (or k3d with `HARNESS_CLUSTER=k3d`) with ClickHouse, the OpenTelemetry Collector and Chaos
    Mesh 2.8.4.
  - `--observability` adds Prometheus and Grafana.
  - It applies the schema, port-forwards ClickHouse to `127.0.0.1:18123`, and prints `export MMO_CLICKHOUSE=...`
    as its last line.
  - It is idempotent. The ClickHouse password is generated into a cluster secret, never committed.
- **New scripts**: `smoke.sh`, `golden.sh`, `calibrate.sh` and `down.sh`.
- **Store DDL** gains `otel_logs`, `otel_traces` and the trace-ID index tables, as the collector's ClickHouse
  exporter v0.161.0 expects them. The exporter runs with `create_schema: false`.
- **`.github/workflows/harness.yml`** runs three jobs on PRs and on `main` for `src/`, `harness/`, `infra/harness/` and
  itself:
  - `stack`: the one command twice, the smoke check, and the observability profile;
  - `golden`: replays `harness/golden/*` and uploads recorded candidates;
  - `netem`: LAN calibration on loopback for 30 s.

  `workflow_dispatch` with `calibrate: true` calibrates every profile for 5 minutes each.
- **Workflow tests** now also run for changes under `infra/`.
- **`harness calibrate --write-calibrated`** skips reordering variants, which have no file of their own.
- **`harness profile netem`** writes `seed` whenever jitter, reorder or loss is random. netem's seeded PRNG
  drives all three on Linux 6.3+. A fixed delay gets no seed.
- **`calibrate.sh`** strips the seed when the machine's `tc` doesn't know it. On GitHub's `ubuntu-latest`, `tc`
  answered `What is "seed"?`. It also passes the repo's profiles folder explicitly, and fails when storing calibrated
  settings fails.
- **The `stack` job** checks that a second `up.sh` recreates nothing (`infra/harness/identity.sh` before and after).
  The workflow runs with pipefail. `smoke.sh` checks every table that `schema apply` lists.

### Open follow-ups

- Commit the golden recordings from the first `golden-candidates` artifact on `main` (T034).
- Commit calibrated profile settings from a `calibrate: true` run (T045).
- Hosted runners vary in CPU model, so a golden run may be "not comparable" on another class. A self-hosted runner of one
  model gives the issue's fixed runner class.

## 2026-11-04 — Telemetry and test harness, slice 2: network profiles, calibration, probe pair, bots (Pre-Phase 1, sub-spec 2)

Spec: [specs/014-telemetry-test-harness/spec.md](../specs/014-telemetry-test-harness/spec.md) · PR: #50 · Issue: #13

### Behavior and contract changes

- **Network profiles**: `harness/profiles/` holds LAN, Typical, Cross-region and Poor, each with an
  automatic `-reorder` variant.
  - Definitions: round trip is the median, jitter is the round-trip standard deviation, and loss is
    measured per direction.
  - `harness profile netem` prints a `tc` script: per-direction delay, jitter and seeded loss, with a
    pfifo child unless the profile allows reordering. `--tcp-ingress` puts the impairment on ifb0.
  - `harness profile chaos` prints a Chaos Mesh `NetworkChaos`.
- **Probe pair**: `harness probe serve` and `harness probe run` exchange sequenced UDP at 30 Hz. A
  receive-bitmap report separates loss on the way up from loss on the way down.
  - Every packet is written to `net_samples`.
  - `harness calibrate` re-reads a stored run. `--write-calibrated` stores the passing per-direction
    settings, runner class and kernel in the profile file.
  - Exit codes: 0 within tolerance, 9 outside.
- **Bots**: `harness stub-target` sends fake 30 Hz snapshots and echoes inputs. `harness bots run` runs
  a 60 Hz bot host that writes `bot_frames` in 5,000-row batches.
  - A host that misses more than 1% of its frame deadlines marks its rows `valid = 0`, and then
    `bot.host_health` is inconclusive.
- Probe and stub datagrams: `contracts/network-profile.md`.

### Shared pieces to reuse

- `UdpIo`: one policy for UDP errors, where socket errors are skipped and cancellation or disposal
  stops.
- `BatchWriter` and `IFrameSink`: batched, off-loop row writing.

### Open follow-ups

- Calibrating the profiles with real netem needs root or Chaos Mesh, so it moves to slice 3's CI job
  (`tasks.md` T045). Until then, profiles carry no calibrated settings and use the arithmetic split.
- The auto-merge workflow fails on this repo with `gh: Resource not accessible by personal access
  token (HTTP 403)`. Sessions merge their own PRs until the token's permissions are fixed.

## 2026-11-04 — Telemetry and test harness, slice 1: store, loader, pass rules, golden runs (Pre-Phase 1, sub-spec 2)

Spec: [specs/014-telemetry-test-harness/spec.md](../specs/014-telemetry-test-harness/spec.md) · PR: #49 · Issue: #13

### Behavior and contract changes

- **`src/MmoEngine.Harness`** and the **`harness`** CLI (`schema apply`, `load`, `rules run`,
  `rules baselines sync`, `golden record`, `golden check`).
  - The ClickHouse store's DDL is embedded and idempotent: `runs`, `sim_ticks`, `sim_events`,
    `net_samples`, `bot_frames`, `rule_results`, `baselines`, `run_aggregates`, `sweep_cells`, and
    `*_retained` twins. Raw rows expire after 14 days.
  - The loader reads sub-spec 1's run folders and sweep folders. It is idempotent by run ID, and the
    `runs` row is the commit marker.
  - Pass rules are YAML files in `harness/rules/`: SQL, plus a fixed threshold or a baseline.
    - Exit codes: 0 pass, 5 failed, 6 inconclusive, 2 invalid rule.
    - A run that doesn't pass is copied to the retained tables, and its manifest, seed and inputs are
      written to `--artifacts`.
  - Golden runs compare a replay's `ticks.csv` with a recording, tick by tick. Exit codes: 7 diverged,
    with the first divergent tick; 8 not comparable, for another runner class.
  - Connection settings come from `--clickhouse` or `MMO_CLICKHOUSE`.
- **Simulator manifest**: `runtime.cpuModel` and `runtime.runnerClass` (from `MMO_RUNNER_CLASS`) are
  added. They are additive, so the run-output format is still version 1.
- **Tests need Docker**: `tests/MmoEngine.Harness.Tests` starts `clickhouse/clickhouse-server:25.8`
  through Testcontainers. GitHub's hosted runners have Docker.

### Shared pieces to reuse

- **`IHarnessStore` / `ClickHouseStore`**: batched inserts (5,000 rows), rule queries with
  `{run_id:String}`, and retention copies. New run kinds (probe, bot) write through it.
- **`RunnerClass`**: the fingerprint that makes hashes and timings comparable (spec 014, research R5).
- **`harness/rules/`**: add a gate as a rule file, not as code. `harness/baselines/` changes only by
  commit.

### Open follow-ups

- Slices 2 to 4 of spec 014 (`tasks.md`):
  - network profiles, calibration and the probe pair;
  - the bot host and stub target;
  - the one-command kind/k3d stack and its CI workflow, plus the committed golden runs recorded on the
    CI runner class;
  - the replay viewer.

  Exit criteria in #13 that wait on them: the one-command harness, calibrated profiles, and determinism
  on the fixed runner class in CI.
- kind can't create a cluster inside a Claude cloud session's container (kubeadm fails there), so the
  stack slice is proven by a GitHub Actions job instead.

## 2026-11-04 — Move to .NET 10

PR: #48 · Issue: #13

### Behavior and contract changes

- The SDK pin is now **10.0.100** (`global.json`, `rollForward: latestPatch`) and every project targets
  **net10.0** (`Directory.Build.props`). .NET 10 is the current LTS; .NET 9 support ends on 2026-11-10.
- Test projects set `IsTestingPlatformApplication=false`, so `dotnet test` stays on VSTest
  (`xunit.runner.visualstudio` and `coverlet.collector`). The .NET 10 SDK otherwise refuses VSTest for
  xunit.v3 projects, which are Microsoft.Testing.Platform apps by default. CI commands are unchanged.

### Assumptions that no longer hold

- "The SDK is pinned to .NET 9" (the 2026-11-03 entry's .NET 10 follow-up is done). CsCheck 5.x is now
  possible but not adopted here.
- Run manifests and benchmark notes recorded before this change name .NET 9; they stay as recorded.

## 2026-11-03 — Regionizer core library and offline simulator (Pre-Phase 1, sub-spec 1)

Spec: [specs/013-regionizer-simulator/spec.md](../specs/013-regionizer-simulator/spec.md) · PR: #47 · Issue: #13

### Behavior and contract changes

The first application code in the repo:

- **`src/MmoEngine.Regionizer`**: the regionizer core that sub-spec 10 runs inside the continent server. It has no package dependencies, and an architecture test enforces that.
  - Claims, section- or position-anchored, through same-area neighbours and open portals (research D1, D2).
  - A merge gap of 0 or 1, where 1 means *adjacent* claims merge (D3).
  - Folia-order merge-later and tick end, split damping, islands, guarded state moves, and timers in tick-counter (default) and absolute modes (D4–D6).
  - Portal toggles made mid-tick are deferred to the last tick end.
  - Layer changes happen only through an open portal or a direct-entry (air) layer.
  - Heavy projectiles keep their firing region.
  - A region with no entities dies at its tick end.
  - `InvariantChecker` checks rules 1–6 with an independent claim walk.
- **`src/MmoEngine.Maps`** and **`portal-validator`**: map format version 1 (`contracts/map-format.md`), with Leak, DeadPortal, MissingPolicy and SealingFailure findings. Exit codes are 0, 1 and 2. Diagnostics go to stderr as Serilog JSON.
- **`src/MmoEngine.Regionizer.Simulator`** and **`regionizer-sim`** (`run`, `sweep`, `replay`, `check`, `memo`):
  - seeded runs, configurable tick rates, and lockstep and interleaved scheduling;
  - monitors at every base step;
  - the nine scenarios;
  - sweeps, trace replay, the analytic checks and the decision memo.

  Run folders follow `contracts/run-output.md` (format version 1); sub-spec 2's loaders read them.
- **CI**: `.github/workflows/dotnet.yml` builds, checks `dotnet format`, runs every test with coverage, and fails when a changed `src/` file is under 95% line coverage (`tools/coverage_gate.py`).

### Shared pieces to reuse

- **The core** (`MmoEngine.Regionizer`): reference it alone. Its public API is `contracts/regionizer-api.md`. Run `InvariantChecker.Check` after ticks in any simulation test.
- **`DeterministicRandom`**: SplitMix64 and xoshiro256** with named and per-entity streams. Use it for any seeded randomness in simulated code. `System.Random`, trigonometry and `Math.Pow` are banned there by `BannedApiTests`.
- **The run-folder, map, trace and sweep-index formats**: `contracts/`.

### Assumptions that no longer hold

- "The repo has no application code or .NET CI": both exist now.

### Open follow-ups

- **.NET 10 (dated)**: the SDK is pinned to .NET 9 because the .NET 10 SDK isn't installed on the machine that ran this work. **.NET 9 support ends on 2026-11-10.** Moving means changing `global.json` and `TargetFramework`, and it also unblocks CsCheck 5.x.
- **Sonar and Codecov**: the constitution asks for both, and both need third-party accounts and repository secrets, which no agent can create. To turn them on:
  1. create the SonarCloud and Codecov projects for `nbon12/mmo-engine`;
  2. add the `SONAR_TOKEN` and `CODECOV_TOKEN` secrets.

  Until then, CI enforces the 95% changed-file coverage itself.
- **`AnalysisLevel latest-recommended`**: not turned on, because doing it inside this PR would add a large unrelated cleanup. It's a follow-up PR of its own.
- **Decision memo**: `docs/simulator/decision-memo.md` comes from a *first-pass* sweep at 9,000 ticks and 5 seeds per cell, not the issue's 18,000 ticks and 10 seeds. The full-scale specs are in `simulator/sweeps/`. Its values for open decision 1 (section size and claim radius per class) are recommendations recorded under Principle VI. They stay in configuration. All numbers below are measurements on one M1 Max.
  - **Section size: flagged.** At 5,000 entities, single-threaded, no size keeps bookkeeping p99 under the proposed 1 ms. Baseline measures 1.1–3.2 ms and One base 0.8–1.9 ms. The issue planned to revise this budget after the first measurement, so revising it, or optimising the bookkeeping, is the next decision. The section size stays at 16 m.
  - **Claim radii.** Each class's radius sits at the top of its sweep (infantry 40 m, ground vehicle 48 m, aircraft 32 m), because no single class fuses fights on the default map. Combined, they raise the largest-region reading from about 482 to about 842 players at 2,000 players. The issue's grid near the chosen values must run before they are adopted.
  - **Lane bridging limit**: 5 travellers on the 600 m lane. At 10 travellers the fights fuse on up to 1.2% of ticks, and at 20 on 7.6% on average. The jump is steep, as the issue's estimate predicted.
  - **Base spacing and split damping.** Minimum base spacing reads 1,000 m (900 m plus a 100 m margin). Split damping reads *p*dead 50%. The air layer is not adopted (a 14% drop, below the 25% bar).
  - **Target fight size** is a reading only. Open decision 2 is set by load tests.
- **24-hour soak**: done at 1,000 players (`docs/simulator/results/soak-24h/`), with zero violations and zero timer faults. A rerun at the scenario's default population (5,900) would take several times longer and is a follow-up.
- **Lockstep bookkeeping**: lockstep runs each region's tick to completion before the next, so its bookkeeping readings exclude merge-later work. An interleaved bookkeeping reading is a follow-up.
- **Portal deferral in the server**: the core applies a deferred portal toggle when no region is ticking. Sub-spec 10's tick pool may never reach that point, so it must pick a guaranteed point.
- **Small items from the PR #47 review**:
  - per-layer portal-endpoint lists for `ClaimCalculator.PortalInBox`;
  - a spatial lookup for portal toggles;
  - an O(n) `LocationQueue.CompleteForRegion`;
  - in interleaved mode, a region absorbed after ticking can roll synthetic timers twice in one step. Movement is unaffected, because each entity moves by its own elapsed time.
- **Sub-spec 2** (the telemetry and test harness) is the next part of #13. It covers ClickHouse loading of these run folders, the fixed runner class and golden runs.

## 2026-11-03 — @claude mention agent on GitHub

Spec: [specs/001-claude-mention-agent/spec.md](../specs/001-claude-mention-agent/spec.md) · PR: #35

### Behavior and contract changes

Tooling only. No application change.

- Mentioning `@claude` in an issue, issue comment, PR review or PR review comment runs Claude Code (`.github/workflows/claude.yml`). It replies on the same thread, and for a change it pushes a branch and offers a PR link.
- Only `OWNER`/`MEMBER`/`COLLABORATOR` mentions start a run. A mention on the sweep log issue (label `sweep-log`) never does, even from nbon12. That closes the follow-up from the #43 entry below.
- Only issue *opened* triggers on `issues`; assigning an issue doesn't.
- Runs for one issue or PR never overlap, but GitHub keeps only one waiting: in a burst of mentions, only the newest waiting one gets a reply.
- `auto-merge.yml` no longer merges a PR whose head commit is from `claude[bot]`, matched by git name and email too, since the action's commits resolve to the `github-actions[bot]` login. Merge such a PR by hand, or push a reviewed commit on top. auto-merge ignores the `claude` check, and it re-decides when `Workflow tests` finishes.
- New CI: `Workflow tests` (`.github/workflows/workflow-tests.yml`) runs `tests/workflows/` on PRs and pushes touching `.github/` or the tests.
- `.github/dependabot.yml` now opens weekly bump PRs for the SHA-pinned actions.
- Needs the `CLAUDE_CODE_OAUTH_TOKEN` (or `ANTHROPIC_API_KEY`) repository secret and the Claude GitHub App.

### Assumptions that no longer hold

- The Done story sweep entry below says this repo has no `claude.yml`, so `@claude` handoffs aren't available. They are now, and `.routines/done-story-sweep.md` says so.

### Shared pieces to reuse

- `tests/workflows/gh_expr.py` has `Expr`, an evaluator for the GitHub Actions expression subset used in `if:` conditions. Use it to test other workflows' gates against event payloads.

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
