#!/usr/bin/env bash
# Checks a running harness (spec 014, US6 scenarios 4 and 5): every schema table exists, the collector
# writes OTLP logs and traces to ClickHouse and exposes metrics for Prometheus, and a simulator run loads
# and passes the committed rules. Needs MMO_CLICKHOUSE (the last line of up.sh) and kubectl access.
#
#   eval "$(infra/harness/up.sh | tail -1)" && infra/harness/smoke.sh [--observability]
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
: "${MMO_CLICKHOUSE:?set MMO_CLICKHOUSE (the last line of up.sh)}"
PORT="$(sed -n 's/.*Port=\([0-9]*\).*/\1/p' <<<"$MMO_CLICKHOUSE")"
USER_NAME="$(sed -n 's/.*Username=\([^;]*\).*/\1/p' <<<"$MMO_CLICKHOUSE")"
PASSWORD="$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$MMO_CLICKHOUSE")"
query() { curl -fsS -u "$USER_NAME:$PASSWORD" "http://127.0.0.1:$PORT/" --data-binary "$1"; }
# shellcheck source=infra/harness/lib.sh
source "$ROOT/infra/harness/lib.sh"
harness_build harness
harness_build sim
fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }

echo "1. tables: every table the store contract lists, and every table the schema defines"
CONTRACT="$ROOT/specs/014-telemetry-test-harness/contracts/store-ddl.md"
mapfile -t EXPECTED < <(python3 -c '
import re, sys
for line in open(sys.argv[1]):
    if line.startswith("| 0"):
        for name in re.findall(r"`([a-z_]+)`", line.split("|")[2]):
            print(name)' "$CONTRACT")
[ "${#EXPECTED[@]}" -ge 16 ] || fail "could not read the table list from $CONTRACT"
APPLY_OUT="$(harness schema apply)"
mapfile -t APPLIED < <(python3 -c 'import json,sys; print("\n".join(json.loads(sys.stdin.read().strip().splitlines()[-1])["tables"]))' <<<"$APPLY_OUT")
for table in "${EXPECTED[@]}" "${APPLIED[@]}"; do
  [ "$(query "EXISTS TABLE $table")" = "1" ] || fail "table $table is missing"
done

echo "2. collector: OTLP logs and traces to ClickHouse, metrics for Prometheus"
kubectl -n harness port-forward svc/otel-collector 14318:4318 18889:8889 >/dev/null 2>&1 &
PF=$!
trap 'kill $PF 2>/dev/null || true' EXIT
for _ in $(seq 1 30); do curl -s -o /dev/null http://127.0.0.1:14318/ && break; sleep 1; done
MARK="smoke-$(date +%s%N)"
NOW="$(date +%s%N)"
curl -fsS -X POST http://127.0.0.1:14318/v1/logs -H 'Content-Type: application/json' \
  -d '{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"harness-smoke"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"'"$NOW"'","severityText":"INFO","body":{"stringValue":"'"$MARK"'"}}]}]}]}' >/dev/null
curl -fsS -X POST http://127.0.0.1:14318/v1/traces -H 'Content-Type: application/json' \
  -d '{"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"harness-smoke"}}]},"scopeSpans":[{"spans":[{"traceId":"5b8efff798038103d269b633813fc60c","spanId":"eee19b7ec3c1b174","name":"'"$MARK"'","kind":1,"startTimeUnixNano":"'"$NOW"'","endTimeUnixNano":"'"$((NOW + 1000000))"'"}]}]}]}' >/dev/null
curl -fsS -X POST http://127.0.0.1:14318/v1/metrics -H 'Content-Type: application/json' \
  -d '{"resourceMetrics":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"harness-smoke"}}]},"scopeMetrics":[{"metrics":[{"name":"harness_smoke_total","sum":{"aggregationTemporality":2,"isMonotonic":true,"dataPoints":[{"asInt":"1","timeUnixNano":"'"$NOW"'"}]}}]}]}]}' >/dev/null
for _ in $(seq 1 30); do
  logs="$(query "SELECT count() FROM otel_logs WHERE Body = '$MARK'")"
  traces="$(query "SELECT count() FROM otel_traces WHERE SpanName = '$MARK'")"
  [ "$logs" = "1" ] && [ "$traces" = "1" ] && break
  sleep 1
done
[ "$logs" = "1" ] || fail "the OTLP log did not reach otel_logs"
[ "$traces" = "1" ] || fail "the OTLP span did not reach otel_traces"
METRICS="$(curl -fsS http://127.0.0.1:18889/metrics)" || fail "the collector's metrics endpoint did not answer"
grep -q harness_smoke_total <<<"$METRICS" || fail "the collector does not expose OTLP metrics for Prometheus"

echo "3. a simulator run loads and passes the committed rules"
OUT="$(mktemp -d)"
sim run "$ROOT/tests/MmoEngine.Harness.Tests/Fixtures/runs/tiny-config.json" --out "$OUT/run" >/dev/null
RUN_ID="$(harness load "$OUT/run" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')"
[ -n "$RUN_ID" ] || fail "load printed no runId"
harness rules run --run-id "$RUN_ID" --rules "$ROOT/harness/rules" --baselines "$ROOT/harness/baselines" --artifacts "$OUT/artifacts" >/dev/null || fail "rules did not pass the clean run"

if [ "${1:-}" = "--observability" ]; then
  echo "4. observability profile: Prometheus and Grafana are ready"
  kubectl -n harness rollout status deployment/prometheus --timeout=5m >/dev/null || fail "prometheus is not ready"
  kubectl -n harness rollout status deployment/grafana --timeout=5m >/dev/null || fail "grafana is not ready"
  echo "5. observability profile: Prometheus scraped the collector's OTLP metric"
  kubectl -n harness port-forward svc/prometheus 19090:9090 >/dev/null 2>&1 &
  PROM_PF=$!
  trap 'kill $PF $PROM_PF 2>/dev/null || true' EXIT
  scraped=""
  for _ in $(seq 1 60); do
    answer="$(curl -fsS 'http://127.0.0.1:19090/api/v1/query?query=harness_smoke_total' 2>/dev/null || true)"
    if python3 -c 'import json,sys; sys.exit(0 if json.loads(sys.argv[1])["data"]["result"] else 1)' "$answer" 2>/dev/null; then
      scraped=yes
      break
    fi
    sleep 2
  done
  [ -n "$scraped" ] || fail "Prometheus has no harness_smoke_total series"
else
  echo "4. core profile: Prometheus and Grafana are not installed"
  if kubectl -n harness get deployment prometheus >/dev/null 2>&1 || kubectl -n harness get deployment grafana >/dev/null 2>&1; then
    fail "the core profile installed Prometheus or Grafana"
  fi
fi
echo "SMOKE OK"
