-- One row per sweep cell and seed, from a sweep's cells.csv and sweep.json (FR-007). Kept indefinitely:
-- this is the decision memo's evidence. `parameters` is the cell's swept paths and values as JSON.
CREATE TABLE IF NOT EXISTS sweep_cells
(
    sweep                     String,
    scenario                  String,
    cell_id                   String,
    seed_index                UInt32,
    seed                      UInt64,
    run_id                    String,
    completed                 UInt8,
    parameters                String,
    region_count_p99          Float64,
    largest_region_players_p99 Float64,
    largest_region_share_p99  Float64,
    fused_fight_pairs_p99     Float64,
    section_churn_p99         Float64,
    merges                    UInt64,
    absorptions               UInt64,
    splits                    UInt64,
    fusions                   UInt64,
    oversized_fusion_ticks    UInt64,
    bookkeeping_p99_micros    Float64,
    summary                   String
)
ENGINE = ReplacingMergeTree
ORDER BY (sweep, cell_id, seed_index)
