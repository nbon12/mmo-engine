-- Raw rows expire after 14 days (research R4); sim_ticks_retained keeps failing and golden runs indefinitely.
CREATE TABLE IF NOT EXISTS sim_ticks
(
    run_id                 String,
    tick                   UInt64,
    time_steps             UInt64,
    region_count           UInt32,
    largest_region_players UInt32,
    largest_region_share   Float64,
    merges                 UInt32,
    absorptions            UInt32,
    splits                 UInt32,
    fusions                UInt32,
    fused_fight_pairs      UInt32,
    section_churn          UInt64,
    cross_pairs_deferred   Nullable(UInt64),
    cross_pairs_read_only  Nullable(UInt64),
    bookkeeping_micros     Float64,
    serialized_micros      Float64,
    oversized_fusion       UInt8,
    violations             UInt32,
    state_hash             String,
    loaded_at              DateTime DEFAULT now()
)
ENGINE = MergeTree
ORDER BY (run_id, tick)
TTL loaded_at + INTERVAL 14 DAY
