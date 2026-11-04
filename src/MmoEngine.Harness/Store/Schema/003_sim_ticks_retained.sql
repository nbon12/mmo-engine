-- Retained copy of sim_ticks: same columns, no TTL. The rule runner copies failing runs here; golden runs too.
CREATE TABLE IF NOT EXISTS sim_ticks_retained
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
