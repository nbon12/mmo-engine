-- Raw rows expire after 14 days (research R4); bot_frames_retained keeps failing and golden runs indefinitely.
CREATE TABLE IF NOT EXISTS bot_frames
(
    run_id                   String,
    host_id                  String,
    bot_id                   UInt32,
    frame                    UInt64,
    deadline_missed          UInt8,
    correction_m             Float64,
    snap                     UInt8,
    underrun                 UInt8,
    remote_position_error_m  Float64,
    snapshots_received       UInt32,
    valid                    UInt8,
    loaded_at                DateTime DEFAULT now()
)
ENGINE = MergeTree
ORDER BY (run_id, host_id, bot_id, frame)
TTL loaded_at + INTERVAL 14 DAY
