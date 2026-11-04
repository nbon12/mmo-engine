# Contract: Rule files and baseline files

## Rule file (`harness/rules/*.yaml`)

```yaml
id: sim.violations                 # required; lowercase letters, digits, '.', '_' or '-'; unique across files
description: Zero invariant violations.   # optional
applies_to: [simulator, sweep-cell]       # required; any of simulator, sweep-cell, probe, bot, golden
min_samples: 1                     # optional, default 0; fewer samples makes the result inconclusive
query: |                           # required; must contain {run_id:String}
  SELECT sum(violations), count()  # column 1 = value, column 2 = sample count
  FROM sim_ticks WHERE run_id = {run_id:String}
mode: fixed                        # required; fixed or baseline
comparison: "<="                   # fixed only; <=, <, >=, >, = (default <=); the value must satisfy value <op> threshold
threshold: 0                       # fixed only; required
```

Baseline mode replaces `comparison` and `threshold` with these fields:

```yaml
mode: baseline
direction: lower_is_better         # or higher_is_better (default lower_is_better)
max_regression: 10%                # required; "10%" or 0.1
```

Unknown fields are refused. A refused file stops the runner with exit code 2, and the message
starts with the file name and names the field.

## Outcomes

| Condition | Outcome |
| --- | --- |
| The query errors | `error` |
| No row, or a NULL or NaN value | `inconclusive` |
| Sample count below `min_samples` | `inconclusive` |
| Fixed: the value satisfies the comparison | `passed`, else `failed` |
| Baseline: no baseline for (rule, run profile) | `passed`, noted "no baseline (report only)" |
| Baseline, lower is better: value ≤ baseline × (1 + regression) | `passed`, else `failed` |
| Baseline, higher is better: value ≥ baseline × (1 − regression) | `passed`, else `failed` |

The run's verdict is its worst outcome, ranked error, then failed, then inconclusive, then passed. A
run that no rule applies to gets `none`. The exit code is 0 for passed or none, 6 for inconclusive, 5
for failed, and 2 for error.

## Baseline file (`harness/baselines/*.yaml`)

```yaml
- rule_id: bot.correction_p99
  profile: Typical                 # the run's network profile; simulator runs use "none"
  value: 0.42
  source_run_id: bot-typical-20261201-abc123   # required: the run the value came from
  set_at: 2026-12-01T00:00:00Z
```

A (rule, profile) pair appears once across all files. The runner reads these files directly. `harness
rules baselines sync` copies them into the `baselines` table for queries.
