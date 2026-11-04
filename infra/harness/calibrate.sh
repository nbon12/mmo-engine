#!/usr/bin/env bash
# Calibrates network profiles with tc netem on this machine's loopback (spec 014, US4, FR-022 to FR-026).
# On loopback both directions leave through `lo`, so one netem qdisc applies each direction's settings.
# Needs root for tc (sudo), Docker for a scratch ClickHouse unless MMO_CLICKHOUSE is set, and the .NET SDK.
#
#   infra/harness/calibrate.sh [--seconds 300] [--write-calibrated] [profile ...]   (default: every profile)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SECONDS_PER_PROFILE=300
WRITE=""
PROFILES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --seconds) SECONDS_PER_PROFILE="$2"; shift 2 ;;
    --write-calibrated) WRITE="--write-calibrated"; shift ;;
    *) PROFILES+=("$1"); shift ;;
  esac
done
PROFILES_DIR="$ROOT/harness/profiles"
BUILD_OUT="$(mktemp -d)"
dotnet build "$ROOT/src/MmoEngine.Harness.Cli" -c Release -o "$BUILD_OUT" >/dev/null
# Run the built DLL directly (not `dotnet run`), so a background server's $! is the server process itself.
harness() { dotnet "$BUILD_OUT/harness.dll" "$@"; }
CONTAINER=""
SERVER=""
SERVER_OUT=""
cleanup() {
  sudo tc qdisc del dev lo root 2>/dev/null || true
  if [ -n "$SERVER" ]; then kill "$SERVER" 2>/dev/null || true; wait "$SERVER" 2>/dev/null || true; fi
  if [ -n "$CONTAINER" ]; then docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; fi
  rm -rf "$BUILD_OUT" "$SERVER_OUT"
}
trap cleanup EXIT
if [ ${#PROFILES[@]} -eq 0 ]; then
  mapfile -t PROFILES < <(harness profile list --profiles "$PROFILES_DIR" | python3 -c 'import json,sys; print("\n".join(p["name"] for p in json.load(sys.stdin)))')
fi
if [ -z "${MMO_CLICKHOUSE:-}" ]; then
  CONTAINER=harness-calibration-clickhouse
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name harness-calibration-clickhouse -p 18125:8123 \
    -e CLICKHOUSE_USER=harness -e CLICKHOUSE_PASSWORD=calibration clickhouse/clickhouse-server:25.8 >/dev/null
  for _ in $(seq 1 60); do curl -fsS http://127.0.0.1:18125/ping >/dev/null 2>&1 && break; sleep 1; done
  export MMO_CLICKHOUSE="Host=127.0.0.1;Port=18125;Username=harness;Password=calibration"
fi
harness schema apply >/dev/null
SERVER_OUT="$(mktemp)"
dotnet "$BUILD_OUT/harness.dll" probe serve --bind 127.0.0.1:7400 >"$SERVER_OUT" &
SERVER=$!
# Probe only once the server says it is listening, so no early probe is counted as lost.
for _ in $(seq 1 120); do
  grep -q '"listening"' "$SERVER_OUT" && break
  kill -0 "$SERVER" 2>/dev/null || { echo "the probe server exited before listening" >&2; exit 1; }
  sleep 0.5
done
grep -q '"listening"' "$SERVER_OUT" || { echo "the probe server did not start listening within 60 s" >&2; exit 1; }
# Older iproute2 builds don't know netem's `seed`; without it the loss pattern isn't repeatable, which only
# matters for impaired runs that are replayed, not for calibration (spec 014, research R7).
SEED_FILTER=cat
# `netem help` prints its usage and exits non-zero, so capture the text first (pipefail would hide a match).
netem_help="$(tc qdisc add dev lo root netem help 2>&1 || true)"
if ! grep -q seed <<<"$netem_help"; then
  echo "::warning::this tc has no netem seed option; calibrating with unseeded loss"
  SEED_FILTER="sed -e s/[[:space:]]seed[[:space:]][0-9]*//"
fi
status=0
for profile in "${PROFILES[@]}"; do
  sudo tc qdisc del dev lo root 2>/dev/null || true
  harness profile netem --profiles "$PROFILES_DIR" --profile "$profile" --device lo --seed 20261104 | $SEED_FILTER | sudo bash
  run_id="calibration-$(tr '[:upper:]' '[:lower:]' <<<"$profile")-$(date -u +%Y%m%dT%H%M%SZ)"
  set +e
  harness probe run --target 127.0.0.1:7400 --profiles "$PROFILES_DIR" --profile "$profile" --seconds "$SECONDS_PER_PROFILE" --run-id "$run_id"
  code=$?
  set -e
  sudo tc qdisc del dev lo root 2>/dev/null || true
  if [ "$code" -eq 0 ] && [ -n "$WRITE" ]; then
    set +e
    harness calibrate --run-id "$run_id" --profiles "$PROFILES_DIR" --profile "$profile" $WRITE >/dev/null
    stored=$?
    set -e
    if [ "$stored" -ne 0 ]; then echo "::error::storing calibrated settings for $profile failed (exit $stored)"; status=1; fi
  fi
  if [ "$code" -ne 0 ]; then echo "::error::profile $profile outside tolerance (exit $code)"; [ "$status" -eq 0 ] && status=9; fi
done
exit "$status"
