-- Raw rows expire after 14 days (research R4); sim_events_retained keeps failing and golden runs indefinitely.
CREATE TABLE IF NOT EXISTS sim_events
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
TTL loaded_at + INTERVAL 14 DAY
