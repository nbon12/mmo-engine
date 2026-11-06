#!/usr/bin/env bash
# Calibrates network profiles with tc netem on this machine (spec 014, US4, FR-022 to FR-026).
#
# Each direction gets its own netem: the probe server runs in a network namespace joined to this one by a
# veth pair, so mmo-up's egress impairs the way up and mmo-down's egress the way down. (On loopback both
# directions shared one queue, and keeping packets in order coupled them.)
#
# Per profile: short search runs tune the per-direction delay and jitter, starting from the profile's own
# settings (its stored calibration, else the arithmetic split), until the round trip and jitter are within
# half the tolerance (calibration_search.py); then one full-length run checks them. A reordering variant is
# measured with its base profile's settings and netem's reorder, with no search of its own. With
# --write-calibrated (5-minute runs only, FR-024), settings that pass are stored in the profile's file.
# Needs root for ip and tc (sudo), Docker for a scratch ClickHouse unless MMO_CLICKHOUSE is set, and the .NET SDK.
#
#   infra/harness/calibrate.sh [--seconds 300] [--search-seconds 60] [--write-calibrated] [profile ...]
#   (default: every profile)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SECONDS_PER_PROFILE=300
SEARCH_SECONDS=60
MAX_SEARCH_STEPS=5
WRITE=""
PROFILES=()
while [ $# -gt 0 ]; do
  case "$1" in
    --seconds) SECONDS_PER_PROFILE="$2"; shift 2 ;;
    --search-seconds) SEARCH_SECONDS="$2"; shift 2 ;;
    --write-calibrated) WRITE="--write-calibrated"; shift ;;
    *) PROFILES+=("$1"); shift ;;
  esac
done
# Stored calibrations come only from full-length runs (FR-024: 5 minutes per profile).
if [ -n "$WRITE" ] && python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) < 300 else 1)' "$SECONDS_PER_PROFILE"; then
  echo "--write-calibrated needs --seconds 300 or more (FR-024); got $SECONDS_PER_PROFILE" >&2
  exit 2
fi
# A search run is never longer than the full run.
SEARCH_SECONDS="$(python3 -c 'import sys; print(min(float(sys.argv[1]), float(sys.argv[2])))' "$SEARCH_SECONDS" "$SECONDS_PER_PROFILE")"
PROFILES_DIR="$ROOT/harness/profiles"
# shellcheck source=infra/harness/lib.sh
source "$ROOT/infra/harness/lib.sh"
harness_build harness
NS=mmo-probe
UP_DEV=mmo-up
DOWN_DEV=mmo-down
CLIENT_IP=10.77.0.1
SERVER_IP=10.77.0.2
SERVER_PORT=7400
CONTAINER=""
SERVER=""
SERVER_OUT=""
WORK="$(mktemp -d)"
cleanup() {
  if [ -n "$SERVER" ]; then
    sudo pkill -f "probe serve --bind $SERVER_IP:$SERVER_PORT" 2>/dev/null || true
    wait "$SERVER" 2>/dev/null || true
  fi
  sudo ip netns del "$NS" 2>/dev/null || true
  sudo ip link del "$UP_DEV" 2>/dev/null || true
  if [ -n "$CONTAINER" ]; then docker rm -f "$CONTAINER" >/dev/null 2>&1 || true; fi
  # The server ran as root and wrote under $WORK.
  sudo rm -rf "$WORK"
}
trap cleanup EXIT

if [ ${#PROFILES[@]} -eq 0 ]; then
  mapfile -t PROFILES < <(harness profile list --profiles "$PROFILES_DIR" | python3 -c 'import json,sys; print("\n".join(p["name"] for p in json.load(sys.stdin)))')
fi
if [ -z "${MMO_CLICKHOUSE:-}" ]; then
  CONTAINER=harness-calibration-clickhouse
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER" -p 18125:8123 \
    -e CLICKHOUSE_USER=harness -e CLICKHOUSE_PASSWORD=calibration clickhouse/clickhouse-server:25.8 >/dev/null
  for _ in $(seq 1 60); do curl -fsS http://127.0.0.1:18125/ping >/dev/null 2>&1 && break; sleep 1; done
  export MMO_CLICKHOUSE="Host=127.0.0.1;Port=18125;Username=harness;Password=calibration"
fi
harness schema apply >/dev/null

# The probe namespace and the veth pair between it and this namespace.
sudo ip netns del "$NS" 2>/dev/null || true
sudo ip link del "$UP_DEV" 2>/dev/null || true
sudo ip netns add "$NS"
sudo ip link add "$UP_DEV" type veth peer name "$DOWN_DEV"
sudo ip link set "$DOWN_DEV" netns "$NS"
sudo ip addr add "$CLIENT_IP/24" dev "$UP_DEV"
sudo ip link set "$UP_DEV" up
sudo ip netns exec "$NS" ip addr add "$SERVER_IP/24" dev "$DOWN_DEV"
sudo ip netns exec "$NS" ip link set "$DOWN_DEV" up
sudo ip netns exec "$NS" ip link set lo up

# The probe server runs in the namespace. The built DLL runs directly (no `dotnet run` parent).
SERVER_OUT="$WORK/server.out"
mkdir -p "$WORK/server-home"
sudo ip netns exec "$NS" env HOME="$WORK/server-home" DOTNET_ROOT="${DOTNET_ROOT:-}" "$(command -v dotnet)" \
  "$HARNESS_BUILD_DIR/harness/harness.dll" probe serve --bind "$SERVER_IP:$SERVER_PORT" >"$SERVER_OUT" &
SERVER=$!
# Probe only once the server says it is listening, so no early probe is counted as lost.
for _ in $(seq 1 120); do
  grep -q '"listening"' "$SERVER_OUT" && break
  # The server runs under sudo, so check that its process exists rather than signalling it.
  ps -p "$SERVER" >/dev/null || { echo "the probe server exited before listening" >&2; exit 1; }
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

# apply <profile> [<delay-ms> <jitter-ms>]: the same per-direction settings on the way up and the way down;
# without overrides, the profile's own settings (what impaired runs use).
apply() {
  local overrides=()
  if [ $# -ge 3 ]; then overrides=(--delay-ms "$2" --jitter-ms "$3"); fi
  harness profile netem --profiles "$PROFILES_DIR" --profile "$1" --device "$UP_DEV" --seed 20261104 \
    "${overrides[@]}" | $SEED_FILTER | sudo bash
  harness profile netem --profiles "$PROFILES_DIR" --profile "$1" --device "$DOWN_DEV" --seed 20261105 \
    "${overrides[@]}" | $SEED_FILTER | sudo ip netns exec "$NS" bash
}

# probe <profile> <seconds> <run-id> <output>: one probe run; prints its exit code.
probe() {
  set +e
  harness probe run --target "$SERVER_IP:$SERVER_PORT" --profiles "$PROFILES_DIR" --profile "$1" \
    --seconds "$2" --run-id "$3" >"$4"
  local code=$?
  set -e
  echo "$code"
}

# fail_profile <profile> <exit>: report a profile that didn't calibrate and keep the most serious status.
fail_profile() {
  echo "::error::profile $1 outside tolerance (exit $2)"
  if [ "$status" -eq 0 ]; then status="$2"; fi
}

status=0
for profile in "${PROFILES[@]}"; do
  slug="$(tr '[:upper:]' '[:lower:]' <<<"$profile")"
  # Targets and starting settings: the profile's stored calibration, else the arithmetic split.
  if ! read -r target_rtt target_jitter delay jitter variant < <(harness profile list --profiles "$PROFILES_DIR" | python3 -c '
import json, sys
p = next((p for p in json.load(sys.stdin) if p["name"].lower() == sys.argv[1].lower()), None)
if p is None:
    sys.exit(f"no profile named {sys.argv[1]}")
d = p["perDirection"]
print(p["roundTripMs"], p["jitterMs"], d["delayMs"], d["jitterMs"], "yes" if p["allowReordering"] else "no")' "$profile"); then
    fail_profile "$profile" 2
    continue
  fi

  run_id="calibration-$slug-$(date -u +%Y%m%dT%H%M%SZ)"
  result="$WORK/$slug.json"
  if [ "$variant" = yes ]; then
    # A variant measures exactly what impaired runs apply: its base's settings, with netem's reorder.
    apply "$profile"
    code="$(probe "$profile" "$SECONDS_PER_PROFILE" "$run_id" "$result")"
    cat "$result"
    if [ "$code" -ne 0 ]; then fail_profile "$profile" "$code"; fi
    continue
  fi

  verdict=next
  best_score=""
  for step in $(seq 1 "$MAX_SEARCH_STEPS"); do
    apply "$profile" "$delay" "$jitter"
    out="$WORK/$slug-search-$step.json"
    search_id="calibration-$slug-search$step-$(date -u +%Y%m%dT%H%M%SZ)"
    probe "$profile" "$SEARCH_SECONDS" "$search_id" "$out" >/dev/null
    if ! read -r verdict next_delay next_jitter score < <(python3 "$ROOT/infra/harness/calibration_search.py" "$out" "$target_rtt" "$target_jitter" "$delay" "$jitter"); then
      verdict=fail; score=inf
    fi
    echo "calibration search $profile step $step: $verdict at per-direction delay ${delay} ms, jitter ${jitter} ms (error ${score})"
    [ "$verdict" = fail ] && break
    # Keep the best measured settings, in case the search runs out of steps.
    if [ -z "$best_score" ] || python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) < float(sys.argv[2]) else 1)' "$score" "$best_score"; then
      best_score="$score"; best_delay="$delay"; best_jitter="$jitter"; best_id="$search_id"; best_out="$out"
    fi
    [ "$verdict" = done ] && break
    delay="$next_delay"; jitter="$next_jitter"
  done
  if [ "$verdict" = fail ]; then
    cat "$out" 2>/dev/null || true
    echo "::error::$profile: a search run produced no usable measurement"
    fail_profile "$profile" 9
    continue
  fi
  if [ "$verdict" != done ]; then
    echo "::warning::$profile: the search didn't reach its band in $MAX_SEARCH_STEPS steps; checking the best measured settings"
  fi
  delay="$best_delay"; jitter="$best_jitter"

  if [ "$verdict" = done ] && python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) >= float(sys.argv[2]) else 1)' "$SEARCH_SECONDS" "$SECONDS_PER_PROFILE"; then
    # The search run was already full length: it is the check.
    run_id="$best_id"; cp "$best_out" "$result"
    code="$(python3 -c 'import json,sys; r=json.loads([l for l in open(sys.argv[1]) if l.startswith("{")][-1]); print(0 if r["passed"] else 9)' "$result")"
  else
    apply "$profile" "$delay" "$jitter"
    code="$(probe "$profile" "$SECONDS_PER_PROFILE" "$run_id" "$result")"
  fi
  cat "$result"
  if [ "$code" -eq 0 ] && [ -n "$WRITE" ]; then
    set +e
    harness calibrate --run-id "$run_id" --profiles "$PROFILES_DIR" --profile "$profile" \
      --delay-ms "$delay" --jitter-ms "$jitter" $WRITE >/dev/null
    stored=$?
    set -e
    if [ "$stored" -ne 0 ]; then echo "::error::storing calibrated settings for $profile failed (exit $stored)"; status=1; fi
  fi
  if [ "$code" -ne 0 ]; then fail_profile "$profile" "$code"; fi
done
exit "$status"
