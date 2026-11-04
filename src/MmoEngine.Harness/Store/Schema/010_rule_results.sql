-- One row per rule per run evaluation. Kept indefinitely (it is small and is the trend history).
CREATE TABLE IF NOT EXISTS rule_results
(
    run_id        String,
    rule_id       String,
    value         Nullable(Float64),
    threshold     Nullable(Float64),
    samples       UInt64,
    outcome       LowCardinality(String),
    note          String,
    evaluated_at  DateTime64(3, 'UTC') DEFAULT now64(3)
)
ENGINE = MergeTree
ORDER BY (run_id, rule_id, evaluated_at)
