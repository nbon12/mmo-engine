#!/usr/bin/env bash
# Runs a sweep as an indexed Kubernetes Job (spec 014, FR-032; issue #62): builds the simulator image
# (infra/sweeps/Dockerfile), loads it into the local kind or k3d cluster, runs one pod per shard
# (`regionizer-sim sweep --shard k/n`, infra/sweeps/manifests/sweep-job.yaml), copies the shards out and
# merges them with infra/sweeps/merge-shards.py.
#
#   infra/sweeps/k8s-sweep.sh <sweep-name | sweep-spec.json> <shards> <out-dir>
#
# Writes <out-dir>/shards/shard-<k>/<sweep>/ (each pod's full results, per-tick files included),
# <out-dir>/trimmed/shard-<k>/ (what the decision memo reads, infra/sweeps/trim.py) and the merged sweep at
# <out-dir>/<sweep>/, the same folder the Sweeps workflow's branches merge into. Exits 3 when a run stopped on
# a violation or timer fault (the results are still merged, as run-sweep.sh does), 1 when the Job failed.
#
# Environment:
#   HARNESS_CLUSTER       kind (default) or k3d: where the image is loaded and which kubectl context is used
#                         (kind-<name> or k3d-<name>); none to skip loading and use the current context (the
#                         cluster pulls SWEEP_IMAGE itself)
#   HARNESS_CLUSTER_NAME  cluster name (default mmo-harness, as up.sh)
#   SWEEP_IMAGE           image name (default mmo-engine/regionizer-sim:local)
#   SWEEP_SKIP_BUILD      1 to use SWEEP_IMAGE as it is, without building it
#   SWEEP_NAMESPACE       namespace (default sweeps)
#   SWEEP_PARALLELISM     pods at once (default: every shard)
#   SWEEP_CPU             each pod's whole CPUs: its request and limit, and its runs at once (default 1)
#   SWEEP_MEMORY          each pod's memory request (default 512Mi)
#   SWEEP_STORAGE, SWEEP_ACCESS_MODE  the results volume (default 5Gi, ReadWriteOnce)
#   SWEEP_TIMEOUT         seconds to wait for the Job (default 21600)
#   SWEEP_START_TIMEOUT   seconds for the first pod to start (default 600); a pod that can't get its image
#                         fails the run at once
#   SWEEP_RUNNER_CLASS    the runner class recorded in each manifest.json (default k8s-<cluster>)
#   SWEEP_KEEP            1 to keep the Job, its volume and its ConfigMap afterwards (default: deleted)
# Needs: docker (unless SWEEP_SKIP_BUILD=1), kubectl, kind or k3d, python3.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
usage="usage: k8s-sweep.sh <sweep-name | sweep-spec.json> <shards> <out-dir>"
WHICH="${1:?$usage}"
SHARDS="${2:?$usage}"
OUT="${3:?$usage}"
TOOL="${HARNESS_CLUSTER:-kind}"
CLUSTER="${HARNESS_CLUSTER_NAME:-mmo-harness}"
IMAGE="${SWEEP_IMAGE:-mmo-engine/regionizer-sim:local}"
NS="${SWEEP_NAMESPACE:-sweeps}"
TIMEOUT="${SWEEP_TIMEOUT:-21600}"
START_TIMEOUT="${SWEEP_START_TIMEOUT:-600}"
log() { printf '[k8s sweep] %s\n' "$*" >&2; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "missing tool: $1" >&2; exit 2; }; }
case "$TOOL" in
  kind|k3d) need "$TOOL"; CONTEXT=(--context "$TOOL-$CLUSTER") ;;
  none) CONTEXT=() ;;
  *) echo "HARNESS_CLUSTER must be kind, k3d or none, was $TOOL" >&2; exit 2 ;;
esac
need kubectl; need python3
# Every kubectl call goes to the cluster the image was loaded into, whatever the current context is.
k() { kubectl "${CONTEXT[@]}" "$@"; }

if [ -f "$WHICH" ]; then SPEC="$WHICH"; else SPEC="$ROOT/simulator/sweeps/$WHICH.json"; fi
[ -f "$SPEC" ] || { echo "no sweep spec $WHICH (a file, or a name in simulator/sweeps/)" >&2; exit 2; }
# The sweep's name names its Job and its output folder; the shards must be 1 to its seeds, as in the Sweeps
# workflow, so every shard holds a run of every cell.
read -r NAME SEEDS < <(python3 -c '
import json, sys
spec = json.load(open(sys.argv[1]))
print(spec["name"], spec["seeds"])' "$SPEC")
[[ "$NAME" =~ ^[a-z0-9]([a-z0-9-]{0,40}[a-z0-9])?$ ]] || { echo "sweep name $NAME can't name a Job (lowercase letters, digits and dashes, at most 42)" >&2; exit 2; }
[[ "$SHARDS" =~ ^[0-9]+$ ]] && [ "$SHARDS" -ge 1 ] && [ "$SHARDS" -le "$SEEDS" ] \
  || { echo "shards must be 1 to the sweep's $SEEDS seeds, was $SHARDS" >&2; exit 2; }
CPU="${SWEEP_CPU:-1}"
[[ "$CPU" =~ ^[1-9][0-9]*$ ]] || { echo "SWEEP_CPU must be a whole number of CPUs (it is also the runs at once), was $CPU" >&2; exit 2; }
if [ -d "$OUT" ] && [ -n "$(ls -A "$OUT")" ]; then echo "$OUT is not empty; use a new folder" >&2; exit 2; fi
mkdir -p "$OUT"

if [ "${SWEEP_SKIP_BUILD:-}" != 1 ]; then
  need docker
  # The image has no repository, so it carries the revision it was built from (manifest.json's gitSha).
  GIT_SHA="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"
  log "building $IMAGE at $GIT_SHA"
  docker build -q -f "$ROOT/infra/sweeps/Dockerfile" --build-arg "GIT_SHA=$GIT_SHA" -t "$IMAGE" "$ROOT" >/dev/null
fi
case "$TOOL" in
  kind) log "loading $IMAGE into kind cluster $CLUSTER"; kind load docker-image "$IMAGE" --name "$CLUSTER" >/dev/null ;;
  k3d) log "loading $IMAGE into k3d cluster $CLUSTER"; k3d image import "$IMAGE" -c "$CLUSTER" >/dev/null ;;
esac

JOB="sweep-$NAME"
READER="$JOB-reader"
# The manifests (infra/sweeps/manifests/) with their placeholders filled in.
render() {
  SWEEP_NAME="$NAME" NAMESPACE="$NS" SHARDS="$SHARDS" PARALLELISM="${SWEEP_PARALLELISM:-$SHARDS}" IMAGE="$IMAGE" \
    RUNNER_CLASS="${SWEEP_RUNNER_CLASS:-k8s-$TOOL}" CPU="$CPU" MEMORY="${SWEEP_MEMORY:-512Mi}" \
    STORAGE="${SWEEP_STORAGE:-5Gi}" ACCESS_MODE="${SWEEP_ACCESS_MODE:-ReadWriteOnce}" \
    python3 -c '
import os, string, sys
print(string.Template(open(sys.argv[1]).read()).substitute(os.environ))' "$ROOT/infra/sweeps/manifests/$1"
}
remove() { k -n "$NS" delete "job/$JOB" "pod/$READER" "pvc/$JOB-results" "configmap/$JOB-spec" --ignore-not-found "$@" >/dev/null; }
k create namespace "$NS" --dry-run=client -o yaml | k apply -f - >/dev/null
# A Job's pod template can't change, and the results volume must start empty: remove an earlier run's.
remove --wait
# Whatever happens next, the Job, its volume and its ConfigMap go (the results are copied out first).
if [ "${SWEEP_KEEP:-}" != 1 ]; then trap 'remove --wait=false || true' EXIT; fi
k -n "$NS" create configmap "$JOB-spec" --from-file=spec.json="$SPEC" >/dev/null
log "running $NAME as $SHARDS shards (job/$JOB in namespace $NS)"
render sweep-job.yaml | k apply -f - >/dev/null

# The Job's state: Complete, Failed, Stuck (a pod can't get its image or start its container), Started (a pod
# has run), or nothing yet.
job_state() {
  k -n "$NS" get jobs,pods -l "mmo-engine/sweep=$NAME" -o json | python3 -c '
import json, sys
STUCK = {"ErrImagePull", "ImagePullBackOff", "ErrImageNeverPull", "InvalidImageName",
         "CreateContainerConfigError", "CreateContainerError"}
items = json.load(sys.stdin)["items"]
pods = [i for i in items if i["kind"] == "Pod"]
for job in (i for i in items if i["kind"] == "Job"):
    for c in job.get("status", {}).get("conditions", []):
        if c["type"] in ("Complete", "Failed") and c["status"] == "True":
            print(c["type"])
            sys.exit()
for pod in pods:
    for status in pod.get("status", {}).get("containerStatuses", []):
        reason = status.get("state", {}).get("waiting", {}).get("reason", "")
        if reason in STUCK:
            print("Stuck", pod["metadata"]["name"], reason)
            sys.exit()
print("Started" if any(p.get("status", {}).get("phase") in ("Running", "Succeeded", "Failed") for p in pods) else "")'
}
state=""
started=""
begun=$SECONDS
while [ $((SECONDS - begun)) -lt "$TIMEOUT" ]; do
  state="$(job_state)"
  case "$state" in
    Complete|Failed|Stuck*) break ;;
    Started) started=1 ;;
  esac
  if [ -z "$started" ] && [ $((SECONDS - begun)) -ge "$START_TIMEOUT" ]; then
    state="Stuck: no pod started in ${START_TIMEOUT}s"
    break
  fi
  state=""
  sleep 5
done
if [ "$state" != Complete ]; then
  echo "job/$JOB: ${state:-did not finish in ${TIMEOUT}s}; its pods:" >&2
  k -n "$NS" get pods -l "job-name=$JOB" -o wide >&2 || true
  k -n "$NS" describe pods -l "job-name=$JOB" 2>&1 | tail -40 >&2 || true
  k -n "$NS" logs -l "job-name=$JOB" --tail=50 --prefix >&2 || true
  exit 1
fi

log "copying the shards out"
render sweep-reader.yaml | k apply -f - >/dev/null
k -n "$NS" wait --for=condition=Ready "pod/$READER" --timeout=5m >/dev/null
k -n "$NS" cp "$READER:/results" "$OUT/shards" >/dev/null

code=0
trimmed=()
for i in $(seq 1 "$SHARDS"); do
  shard="$OUT/shards/shard-$i"
  [ -f "$shard/exit-code" ] || { echo "shard $i wrote no exit-code" >&2; exit 1; }
  shard_code="$(cat "$shard/exit-code")"
  if [ "$shard_code" = 3 ]; then
    echo "::warning::sweep $NAME shard $i/$SHARDS exited 3 (a run stopped on a violation or timer fault); keeping its results"
    code=3
  fi
  python3 "$ROOT/infra/sweeps/trim.py" "$shard/$NAME" "$OUT/trimmed/shard-$i" >/dev/null
  trimmed+=("$OUT/trimmed/shard-$i")
done
python3 "$ROOT/infra/sweeps/merge-shards.py" "$OUT/$NAME" "${trimmed[@]}" >&2
log "merged into $OUT/$NAME"
exit "$code"
