-- Accepted value of a baseline-mode rule per network profile. Written only by `harness baselines sync`
-- from the committed files in harness/baselines/ (FR-014). Latest set_at wins.
CREATE TABLE IF NOT EXISTS baselines
(
    rule_id        String,
    profile        String,
    value          Float64,
    source_run_id  String,
    set_at         DateTime64(3, 'UTC')
)
ENGINE = ReplacingMergeTree(set_at)
ORDER BY (rule_id, profile)
