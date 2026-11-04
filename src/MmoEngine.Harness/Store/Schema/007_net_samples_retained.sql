-- Retained copy of net_samples: same columns, no TTL. The rule runner copies failing runs here; golden runs too.
CREATE TABLE IF NOT EXISTS net_samples_retained
(
    run_id     String,
    probe_id   String,
    direction  LowCardinality(String),
    seq        UInt64,
    send_ns    Nullable(Int64),
    recv_ns    Nullable(Int64),
    lost       UInt8,
    duplicate  UInt8,
    reordered  UInt8,
    loaded_at  DateTime DEFAULT now()
)
ENGINE = MergeTree
ORDER BY (run_id, probe_id, direction, seq)
