#!/bin/sh
# Runs one shard of a sweep inside an indexed Kubernetes Job (spec 014, FR-032; issue #62). The Job's
# completion index i (0-based, JOB_COMPLETION_INDEX) runs shard i+1 of SWEEP_SHARDS, every n-th run, exactly
# as `infra/sweeps/run-sweep.sh <sweep> <out> k/n` does in the Sweeps workflow.
#
# Runs SWEEP_RUN_PARALLELISM runs at once: the pod's whole CPUs. The runtime's processor count already follows
# the pod's CPU limit; the explicit value keeps runs at once equal to the CPUs even where a cluster sets no limit.
#
# Reads the sweep spec at SWEEP_SPEC (default /sweep/spec.json) and writes /results/shard-<k>/<sweep-name>/.
# A retried pod starts its shard over. Exit 3 (a run stopped on a violation or timer fault) still leaves
# complete results, so it is recorded in /results/shard-<k>/exit-code and the pod succeeds; any other
# failure fails the pod.
set -eu
: "${JOB_COMPLETION_INDEX:?run this as an indexed Job (completionMode: Indexed)}"
: "${SWEEP_SHARDS:?set SWEEP_SHARDS to the Job's completions}"
: "${SWEEP_RUN_PARALLELISM:?set SWEEP_RUN_PARALLELISM to the pod's CPUs}"
SPEC="${SWEEP_SPEC:-/sweep/spec.json}"
RESULTS="${SWEEP_RESULTS:-/results}"
k=$((JOB_COMPLETION_INDEX + 1))
out="$RESULTS/shard-$k"
# The shell is PID 1, which ignores SIGTERM unless it traps it: pass it on to the simulator and wait for it to
# stop, so a deleted pod stops writing its shard within the grace period. The trap comes first, so a SIGTERM
# that arrives while an earlier attempt's files are removed, or before the simulator starts, is not lost.
child=""
terminated=""
trap 'terminated=1; [ -n "$child" ] && kill -TERM "$child" 2>/dev/null' TERM
rm -rf "$out"
mkdir -p "$out"
if [ -n "$terminated" ]; then exit 143; fi
dotnet /app/regionizer-sim.dll sweep "$SPEC" --out "$out" --scenarios /app/scenarios --shard "$k/$SWEEP_SHARDS" --parallelism "$SWEEP_RUN_PARALLELISM" &
child=$!
if [ -n "$terminated" ]; then kill -TERM "$child" 2>/dev/null; fi
set +e
wait "$child"
code=$?
# A trapped signal interrupts the first wait; wait again for the simulator to exit.
while kill -0 "$child" 2>/dev/null; do wait "$child"; code=$?; done
set -e
echo "$code" > "$out/exit-code"
case "$code" in
  0|3) exit 0 ;;
  *) exit "$code" ;;
esac
