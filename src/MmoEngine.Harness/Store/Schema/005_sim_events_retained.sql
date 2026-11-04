-- Retained copy of sim_events: same columns, no TTL. The rule runner copies failing runs here; golden runs too.
CREATE TABLE IF NOT EXISTS sim_events_retained
(
    run_id        String,
    seq           UInt64,
    tick          UInt64,
    time_steps    UInt64,
    type          LowCardinality(String),
    region_a      Nullable(Int64),
    region_b      Nullable(Int64),
    cause_class   String,
    cause_entity  Nullable(Int64),
    value         Nullable(Float64),
    detail        String,
    loaded_at     DateTime DEFAULT now()
)
ENGINE = MergeTree
ORDER BY (run_id, seq)
