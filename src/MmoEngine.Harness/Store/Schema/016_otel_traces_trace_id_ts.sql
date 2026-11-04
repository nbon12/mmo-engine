-- The OpenTelemetry Collector ClickHouse exporter's otel_traces_trace_id_ts schema, as exporter v0.161.0 creates it,
-- with the harness's 14-day raw-row TTL added. The exporter runs with create_schema: false (FR-031),
-- so the repo's DDL owns these tables. Re-generate when the collector version changes (spec 014, research R9).
CREATE TABLE IF NOT EXISTS otel_traces_trace_id_ts
(
    `TraceId` String CODEC(ZSTD(1)),
    `Start` DateTime CODEC(Delta(4), ZSTD(1)),
    `End` DateTime CODEC(Delta(4), ZSTD(1)),
    INDEX idx_trace_id TraceId TYPE bloom_filter(0.01) GRANULARITY 1
)
ENGINE = MergeTree
PARTITION BY toDate(Start)
ORDER BY (TraceId, Start)
TTL toDateTime(Start) + toIntervalDay(14)
SETTINGS index_granularity = 8192, ttl_only_drop_parts = 1
