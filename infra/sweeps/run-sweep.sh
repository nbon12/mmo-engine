#!/usr/bin/env bash
# Runs one sweep of simulator/sweeps/ and keeps what the decision memo reads (spec 013, issue #13):
# the sweep's sweep.json and cells.csv, and each run's manifest.json and summary.json. Per-tick and
# per-event files are dropped, so a full-scale sweep's results stay small enough for a git branch.
#
#   infra/sweeps/run-sweep.sh <sweep-name> <out-dir>     (writes <out-dir>/<sweep-name>/)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NAME="${1:?usage: run-sweep.sh <sweep-name> <out-dir>}"
OUT="${2:?usage: run-sweep.sh <sweep-name> <out-dir>}"
SPEC="$ROOT/simulator/sweeps/$NAME.json"
[ -f "$SPEC" ] || { echo "no sweep named $NAME in simulator/sweeps/" >&2; exit 2; }
# shellcheck source=infra/harness/lib.sh
source "$ROOT/infra/harness/lib.sh"
harness_build sim
FULL="$(mktemp -d)"
trap 'rm -rf "$FULL"' EXIT
sim sweep "$SPEC" --out "$FULL" --scenarios "$ROOT/simulator/scenarios"
python3 "$ROOT/infra/sweeps/trim.py" "$FULL/$NAME" "$OUT/$NAME"
