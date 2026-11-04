# Contract: Store DDL

The authoritative DDL is `src/MmoEngine.Harness/Store/Schema/*.sql`. The files are embedded in the
library and applied in file-name order by `harness schema apply`. Every statement is `CREATE TABLE IF
NOT EXISTS`, so applying the schema is idempotent (FR-001).

| File | Table | Engine and order | Expiry |
| --- | --- | --- | --- |
| 001 | `runs` | `ReplacingMergeTree(version)`, `run_id`; read with `FINAL` | none |
| 002, 003 | `sim_ticks`, `sim_ticks_retained` | `MergeTree`, `(run_id, tick)` | raw: 14 days on `loaded_at`; retained: none |
| 004, 005 | `sim_events`, `sim_events_retained` | `MergeTree`, `(run_id, seq)` | as above |
| 006, 007 | `net_samples`, `net_samples_retained` | `MergeTree`, `(run_id, probe_id, direction, seq)` | as above |
| 008, 009 | `bot_frames`, `bot_frames_retained` | `MergeTree`, `(run_id, host_id, bot_id, frame)` | as above |
| 010 | `rule_results` | `MergeTree`, `(run_id, rule_id, evaluated_at)` | none |
| 011 | `baselines` | `ReplacingMergeTree(set_at)`, `(rule_id, profile)` | none |
| 012 | `run_aggregates` | `ReplacingMergeTree`, `(run_id, metric)` | none |
| 013 | `sweep_cells` | `ReplacingMergeTree`, `(sweep, cell_id, seed_index)` | none |
| 014 to 017 | `otel_logs`, `otel_traces`, `otel_traces_trace_id_ts` and its materialized view | as the OpenTelemetry Collector ClickHouse exporter v0.161.0 creates them | 14 days on `Timestamp` (or `Start`) |

## Column mapping from the run folder

`sim_ticks` takes the `ticks.csv` columns in snake case: `timeSteps` becomes `time_steps`,
`crossPairsDeferred` becomes `cross_pairs_deferred`, and so on. Empty cross-pair cells become NULL.
`sim_events` adds a `seq` column, the row's order in `events.csv`. An empty region, entity or value
becomes NULL. `run_aggregates` takes `summary.json`'s `metrics` object, one row per metric.
`sweep_cells` takes `cells.csv`'s fixed columns and the p99 and total summary columns. It keeps the
cell's swept parameters (from `sweep.json`) as JSON, and the whole `cells.csv` row as JSON in
`summary`.

## Changes

Changes are additive: a new table, or a new column with a default. A non-additive change needs a new
table name and a migration note in the patch notes, because rows written by older tools stay in the
store for up to 14 days.
