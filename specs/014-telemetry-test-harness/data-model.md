# Data Model: Telemetry and Test Harness

The store's tables are in [contracts/store-ddl.md](contracts/store-ddl.md). The authoritative DDL is
the embedded SQL in `src/MmoEngine.Harness/Store/Schema/`. Every raw table is a `MergeTree` ordered
by `run_id`, then by tick, frame or sequence number, with a 14-day TTL on `loaded_at`. Each raw table
has a `*_retained` twin without a TTL.

| Table | One row per | Key columns | Written by |
| --- | --- | --- | --- |
| `runs` | Run (latest version wins: `ReplacingMergeTree(version)`) | run_id, kind, git_sha, config_hash, scenario, seed, profile, runner_class, sweep, cell_id, started, finished, verdict, loaded | loader, rule runner (verdict), probe, bot host |
| `sim_ticks` | Simulator metrics row | run_id, tick, every `ticks.csv` column | loader |
| `sim_events` | Simulator event | run_id, seq, tick, type, region_a, region_b, cause_class, cause_entity, value, detail | loader |
| `sweep_cells` | Sweep cell and seed (`cells.csv`) | sweep, cell_id, seed_index, seed, run_id, parameters (JSON), summary columns | loader |
| `net_samples` | Probe packet | run_id, probe_id, direction, seq, send_ns, recv_ns, lost, duplicate | probe pair |
| `bot_frames` | Bot frame | run_id, host_id, bot_id, frame, deadline_missed, correction_m, snap, underrun, remote_position_error_m, snapshots_received, valid | bot host |
| `rule_results` | Rule per run | run_id, rule_id, value, threshold, samples, outcome, note, evaluated_at | rule runner |
| `baselines` | Rule per profile | rule_id, profile, value, source_run_id, set_at | `harness rules baselines sync`, a mirror of the committed files (the runner reads the files) |
| `run_aggregates` | Run and metric | run_id, metric, p50, p95, p99, max, mean | loader (from `summary.json`) |

## Run kinds

`simulator`, `sweep-cell`, `probe`, `bot`, `golden`. Rules select by kind.

## Rule outcome

`passed`, `failed`, `inconclusive` or `error`. A run's verdict is the worst outcome of its rules:
`error` is worse than `failed`, which is worse than `inconclusive`, which is worse than `passed`. A
run that no rule applies to has verdict `none`.

## State changes

- `runs.loaded`: 0, then 1, once all the run's rows are inserted (the commit marker).
- `runs.verdict`: `pending`, then `passed`, `failed`, `inconclusive` or `error`. Each change inserts a
  new row version.
- `runs.retained`: 0, then 1, once the run's rows are copied to the retained tables.
