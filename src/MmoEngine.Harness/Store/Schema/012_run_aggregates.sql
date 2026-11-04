-- Per-run percentiles of each metric (from summary.json, or computed by a tool). Kept indefinitely for trend lines.
CREATE TABLE IF NOT EXISTS run_aggregates
(
    run_id  String,
    metric  String,
    p50     Nullable(Float64),
    p95     Nullable(Float64),
    p99     Nullable(Float64),
    max     Nullable(Float64),
    mean    Nullable(Float64)
)
ENGINE = ReplacingMergeTree
ORDER BY (run_id, metric)
