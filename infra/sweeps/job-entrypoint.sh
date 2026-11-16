#!/bin/sh
# Runs one shard of a sweep inside an indexed Kubernetes Job (spec 014, FR-032; issue #62). The Job's
# completion index i (0-based, JOB_COMPLETION_INDEX) runs shard i+1 of SWEEP_SHARDS, every n-th run, exactly
# as `infra/sweeps/run-sweep.sh <sweep> <out> k/n` does in the Sweeps workflow.
#
# Reads the sweep spec at SWEEP_SPEC (default /sweep/spec.json) and writes /results/shard-<k>/<sweep-name>/.
# A retried pod starts its shard over. Exit 3 (a run stopped on a violation or timer fault) still leaves
# complete results, so it is recorded in /results/shard-<k>/exit-code and the pod succeeds; any other
# failure fails the pod.
set -eu
: "${JOB_COMPLETION_INDEX:?run this as an indexed Job (completionMode: Indexed)}"
: "${SWEEP_SHARDS:?set SWEEP_SHARDS to the Job's completions}"
SPEC="${SWEEP_SPEC:-/sweep/spec.json}"
RESULTS="${SWEEP_RESULTS:-/results}"
k=$((JOB_COMPLETION_INDEX + 1))
out="$RESULTS/shard-$k"
rm -rf "$out"
mkdir -p "$out"
set +e
dotnet /app/regionizer-sim.dll sweep "$SPEC" --out "$out" --scenarios /app/scenarios --shard "$k/$SWEEP_SHARDS"
code=$?
set -e
echo "$code" > "$out/exit-code"
case "$code" in
  0|3) exit 0 ;;
  *) exit "$code" ;;
esac
