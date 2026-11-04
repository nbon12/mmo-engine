-- One row per run; the latest version wins (ReplacingMergeTree). Read with FINAL. Kept indefinitely.
-- `loaded = 1` is the loader's commit marker (research R3). Contract: specs/014-telemetry-test-harness/contracts/store-ddl.md
CREATE TABLE IF NOT EXISTS runs
(
    run_id        String,
    kind          LowCardinality(String),
    git_sha       String,
    config_hash   String,
    scenario      String,
    seed          UInt64,
    scheduling    String,
    profile       String,
    runner_class  String,
    sweep         String,
    cell_id       String,
    started       DateTime64(3, 'UTC'),
    finished      Nullable(DateTime64(3, 'UTC')),
    ticks         UInt64,
    verdict       LowCardinality(String),
    loaded        UInt8,
    retained      UInt8,
    manifest      String,
    version       UInt64,
    loaded_at     DateTime64(3, 'UTC') DEFAULT now64(3)
)
ENGINE = ReplacingMergeTree(version)
ORDER BY run_id
