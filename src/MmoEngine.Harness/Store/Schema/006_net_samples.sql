-- Raw rows expire after 14 days (research R4); net_samples_retained keeps failing and golden runs indefinitely.
CREATE TABLE IF NOT EXISTS net_samples
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
TTL loaded_at + INTERVAL 14 DAY
