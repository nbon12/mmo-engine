#!/usr/bin/env bash
# Runs one sweep of simulator/sweeps/ and keeps what the decision memo reads (spec 013, issue #13):
# the sweep's sweep.json and cells.csv, and each run's manifest.json and summary.json. Per-tick and
# per-event files are dropped, so a full-scale sweep's results stay small enough for a git branch.
#
# The full results go to <out-dir>/full/ as the sweep runs, so a sweep cut short can still be trimmed
# (trim.py --partial). A sweep whose runs stopped on a violation or timer fault (exit 3) is still trimmed,
# and the script exits with the sweep's code afterwards.
#
#   infra/sweeps/run-sweep.sh <sweep-name> <out-dir> [<k>/<n>]   (writes <out-dir>/<sweep-name>/)
#
# With <k>/<n>, only shard k of n runs (every n-th run); merge shards with infra/sweeps/merge-shards.py.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NAME="${1:?usage: run-sweep.sh <sweep-name> <out-dir>}"
OUT="${2:?usage: run-sweep.sh <sweep-name> <out-dir>}"
SHARD="${3:-1/1}"
SPEC="$ROOT/simulator/sweeps/$NAME.json"
[ -f "$SPEC" ] || { echo "no sweep named $NAME in simulator/sweeps/" >&2; exit 2; }
# shellcheck source=infra/harness/lib.sh
source "$ROOT/infra/harness/lib.sh"
harness_build sim
FULL="$OUT/full"
mkdir -p "$FULL"
set +e
sim sweep "$SPEC" --out "$FULL" --scenarios "$ROOT/simulator/scenarios" --shard "$SHARD"
code=$?
set -e
if [ "$code" -ne 0 ]; then echo "::warning::sweep $NAME exited $code (3: a run stopped on a violation or timer fault); keeping its results"; fi
python3 "$ROOT/infra/sweeps/trim.py" "$FULL/$NAME" "$OUT/$NAME"
exit "$code"
