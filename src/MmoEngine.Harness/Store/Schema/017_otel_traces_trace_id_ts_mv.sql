-- The OpenTelemetry Collector ClickHouse exporter's otel_traces_trace_id_ts_mv schema, as exporter v0.161.0 creates it,
-- with the harness's 14-day raw-row TTL added. The exporter runs with create_schema: false (FR-031),
-- so the repo's DDL owns these tables. Re-generate when the collector version changes (spec 014, research R9).
CREATE MATERIALIZED VIEW IF NOT EXISTS otel_traces_trace_id_ts_mv TO otel_traces_trace_id_ts
(
    `TraceId` String,
    `Start` DateTime64(9),
    `End` DateTime64(9)
)
AS SELECT
    TraceId,
    min(Timestamp) AS Start,
    max(Timestamp) AS End
FROM otel_traces
WHERE TraceId != ''
GROUP BY TraceId
