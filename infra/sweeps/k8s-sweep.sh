#!/usr/bin/env bash
# Runs a sweep as an indexed Kubernetes Job (spec 014, FR-032; issue #62): builds the simulator image
# (infra/sweeps/Dockerfile), loads it into the local kind or k3d cluster, runs one pod per shard
# (`regionizer-sim sweep --shard k/n`, infra/sweeps/manifests/sweep-job.yaml), copies the shards out and
# merges them with infra/sweeps/merge-shards.py.
#
#   infra/sweeps/k8s-sweep.sh <sweep-name | sweep-spec.json> <shards> <out-dir>
#
# Writes <out-dir>/_shards/shard-<k>/<sweep>/ (each pod's full results, per-tick files included),
# <out-dir>/_trimmed/shard-<k>/ (what the decision memo reads, infra/sweeps/trim.py) and the merged sweep at
# <out-dir>/<sweep>/ (a sweep name never starts with an underscore, so it can't collide with them), the same folder the Sweeps workflow's branches merge into. Exits 3 when a run stopped on
# a violation or timer fault (the results are still merged, as run-sweep.sh does), 1 when the Job failed.
#
# Environment:
#   HARNESS_CLUSTER       kind (default) or k3d: where the image is loaded and which kubectl context is used
#                         (kind-<name> or k3d-<name>); none to use the current context with SWEEP_IMAGE as it is
#                         (no local build: that cluster must be able to pull it)
#   HARNESS_CLUSTER_NAME  cluster name (default mmo-harness, as up.sh)
#   SWEEP_IMAGE           image name (default mmo-engine/regionizer-sim:local)
#   SWEEP_SKIP_BUILD      1 to use SWEEP_IMAGE as it is, without building it
#   SWEEP_NAMESPACE       namespace (default sweeps)
#   SWEEP_PARALLELISM     pods at once (default: every shard)
#   SWEEP_CPU             each pod's whole CPUs: its request and limit, and its runs at once (default 1)
#   SWEEP_MEMORY          each pod's memory request and limit (default 2Gi)
#   SWEEP_STORAGE, SWEEP_ACCESS_MODE  the results volume (default 5Gi, ReadWriteOnce)
#   SWEEP_TIMEOUT         seconds to wait for the Job (default 21600)
#   SWEEP_START_TIMEOUT   seconds for the first pod to start (default 600)
#   SWEEP_PULL_GRACE      seconds a pod may keep failing to pull its image before the run fails (default 120);
#                         an image that can never be pulled or a container that can't be created fails it at once
#   SWEEP_RUNNER_CLASS    the runner class recorded in each manifest.json (default k8s-<cluster>)
#   SWEEP_KEEP            1 to keep the Job, its volume and its ConfigMap afterwards, untouched (default: deleted;
#                         a volume whose shards could not be copied out is always kept)
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
PULL_GRACE="${SWEEP_PULL_GRACE:-120}"
log() { printf '[k8s sweep] %s\n' "$*" >&2; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "missing tool: $1" >&2; exit 2; }; }
case "$TOOL" in
  kind|k3d) need "$TOOL"; CONTEXT=(--context "$TOOL-$CLUSTER") ;;
  none) CONTEXT=(); SWEEP_SKIP_BUILD=1 ;;
  *) echo "HARNESS_CLUSTER must be kind, k3d or none, was $TOOL" >&2; exit 2 ;;
esac
need kubectl; need python3
# Every kubectl call goes to the cluster the image was loaded into, whatever the current context is.
# (The +-expansion keeps an empty CONTEXT working under set -u on bash before 4.4.)
k() { kubectl ${CONTEXT[@]+"${CONTEXT[@]}"} "$@"; }

if [ -f "$WHICH" ]; then SPEC="$WHICH"; else SPEC="$ROOT/simulator/sweeps/$WHICH.json"; fi
[ -f "$SPEC" ] || { echo "no sweep spec $WHICH (a file, or a name in simulator/sweeps/)" >&2; exit 2; }
# The sweep's name names its Job and its output folder; the shards must be 1 to its seeds, as in the Sweeps
# workflow, so every shard holds a run of every cell.
read -r NAME SEEDS < <(python3 -c '
import json, sys
spec = json.load(open(sys.argv[1]))
print(spec["name"], spec["seeds"])' "$SPEC")
[[ "$NAME" =~ ^[a-z0-9]([a-z0-9-]{0,40}[a-z0-9])?$ ]] || { echo "sweep name $NAME can't name a Job (lowercase letters, digits and dashes, at most 42)" >&2; exit 2; }
# No leading zero: the manifest's YAML would read 010 as octal 8.
[[ "$SHARDS" =~ ^[1-9][0-9]*$ ]] && [ "$SHARDS" -ge 1 ] && [ "$SHARDS" -le "$SEEDS" ] \
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
  # Its output goes to stderr, so a compile error reaches the log (as lib.sh does for the CLIs).
  docker build -f "$ROOT/infra/sweeps/Dockerfile" --build-arg "GIT_SHA=$GIT_SHA" -t "$IMAGE" "$ROOT" >&2
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
    RUNNER_CLASS="${SWEEP_RUNNER_CLASS:-k8s-$TOOL}" CPU="$CPU" MEMORY="${SWEEP_MEMORY:-2Gi}" \
    STORAGE="${SWEEP_STORAGE:-5Gi}" ACCESS_MODE="${SWEEP_ACCESS_MODE:-ReadWriteOnce}" \
    python3 -c '
import os, string, sys
print(string.Template(open(sys.argv[1]).read()).substitute(os.environ))' "$ROOT/infra/sweeps/manifests/$1"
}
remove() { k -n "$NS" delete "job/$JOB" "pod/$READER" "pvc/$JOB-results" "configmap/$JOB-spec" --ignore-not-found "$@" >/dev/null; }
# On the way out: everything, except a volume whose shards could not be copied out.
KEEP_VOLUME=""
clean_up() {
  if [ -n "$KEEP_VOLUME" ]; then
    k -n "$NS" delete "job/$JOB" "pod/$READER" "configmap/$JOB-spec" --ignore-not-found --wait=false >/dev/null || true
    echo "kept pvc/$JOB-results in namespace $NS: its shards could not be copied out" >&2
  else
    remove --wait=false || true
  fi
}
k create namespace "$NS" --dry-run=client -o yaml | k apply -f - >/dev/null
# A Job's pod template can't change, and the results volume must start empty: remove an earlier run's, and wait
# for its pods to go, so none of them is taken for this run's.
remove --wait --cascade=foreground
k -n "$NS" wait --for=delete pod -l "mmo-engine/sweep=$NAME" --timeout=5m >/dev/null 2>&1 || true
# Whatever happens next, the Job, its volume and its ConfigMap go (the results are copied out first).
if [ "${SWEEP_KEEP:-}" != 1 ]; then trap clean_up EXIT; fi
k -n "$NS" create configmap "$JOB-spec" --from-file=spec.json="$SPEC" >/dev/null
log "running $NAME as $SHARDS shards (job/$JOB in namespace $NS)"
render sweep-job.yaml | k apply -f - >/dev/null

# The Job's state: Complete, Failed, Stuck (a pod's image can never be pulled or its container can't be created),
# Pulling (a pod is failing to pull its image, which may pass), Started (a pod has run), or nothing yet.
job_state() {
  k -n "$NS" get jobs,pods -l "mmo-engine/sweep=$NAME" -o json | python3 -c '
import json, sys
STUCK = {"ErrImageNeverPull", "InvalidImageName", "CreateContainerConfigError", "CreateContainerError"}
PULLING = {"ErrImagePull", "ImagePullBackOff"}
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
for pod in pods:
    for status in pod.get("status", {}).get("containerStatuses", []):
        reason = status.get("state", {}).get("waiting", {}).get("reason", "")
        if reason in PULLING:
            print("Pulling", pod["metadata"]["name"], reason)
            sys.exit()
print("Started" if any(p.get("status", {}).get("phase") in ("Running", "Succeeded", "Failed") for p in pods) else "")'
}
state=""
started=""
pulling_since=""
errors=0
begun=$SECONDS
while [ $((SECONDS - begun)) -lt "$TIMEOUT" ]; do
  # One failed poll (an API server restart, a reset connection) is retried; a minute of them gives up.
  if ! state="$(job_state)"; then
    errors=$((errors + 1))
    if [ "$errors" -ge 12 ]; then state="Unreachable: 12 polls in a row failed"; break; fi
    state=""
    sleep 5
    continue
  fi
  errors=0
  case "$state" in
    Complete|Failed|Stuck*) break ;;
    Started) started=1 ;;
  esac
  # A pull that keeps failing past the grace period is as stuck as one that never can succeed.
  if [[ "$state" == Pulling* ]]; then
    pulling_since="${pulling_since:-$SECONDS}"
    if [ $((SECONDS - pulling_since)) -ge "$PULL_GRACE" ]; then state="Stuck ${state#Pulling } for ${PULL_GRACE}s"; break; fi
  else
    pulling_since=""
  fi
  if [ -z "$started" ] && [ $((SECONDS - begun)) -ge "$START_TIMEOUT" ]; then
    state="Stuck: no pod started in ${START_TIMEOUT}s"
    break
  fi
  state=""
  sleep 5
done
# Copies every shard folder on the results volume to <out-dir>/_shards/ through a reader pod; fails if any step does.
copy_out() {
  render sweep-reader.yaml | k apply -f - >/dev/null \
    && k -n "$NS" wait --for=condition=Ready "pod/$READER" --timeout=5m >/dev/null \
    && k -n "$NS" cp "$READER:/results" "$OUT/_shards" >/dev/null
}
if [ "$state" != Complete ]; then
  echo "job/$JOB: ${state:-did not finish in ${TIMEOUT}s}; its pods:" >&2
  k -n "$NS" get pods -l "job-name=$JOB" -o wide >&2 || true
  k -n "$NS" describe pods -l "job-name=$JOB" 2>&1 | tail -40 >&2 || true
  k -n "$NS" logs -l "job-name=$JOB" --tail=50 --prefix >&2 || true
  if [ "${SWEEP_KEEP:-}" = 1 ]; then
    echo "SWEEP_KEEP=1: left job/$JOB and pvc/$JOB-results in namespace $NS as they are" >&2
    exit 1
  fi
  # Shards that finished are kept, as run-sweep.sh keeps a sweep cut short: once any pod has run (whatever
  # ended the wait), stop the Job, then copy what the volume holds. If the copy fails, the volume stays.
  if [ -n "$started" ] || [ "$state" = Failed ]; then
    # Foreground, then wait for the pods: none may still be writing the volume while it is copied.
    k -n "$NS" delete "job/$JOB" --cascade=foreground --wait >/dev/null || true
    k -n "$NS" wait --for=delete pod -l "job-name=$JOB" --timeout=2m >/dev/null 2>&1 || true
    if copy_out; then
      echo "kept the shards that ran in $OUT/_shards/; merge the finished ones with merge-shards.py --allow-missing (--partial for one cut short)" >&2
    else
      echo "could not copy the shards out" >&2
      KEEP_VOLUME=1
    fi
  fi
  exit 1
fi

log "copying the shards out"
copy_out || { KEEP_VOLUME=1; echo "could not copy the shards out" >&2; exit 1; }

code=0
trimmed=()
for i in $(seq 1 "$SHARDS"); do
  shard="$OUT/_shards/shard-$i"
  [ -f "$shard/exit-code" ] || { echo "shard $i wrote no exit-code" >&2; exit 1; }
  shard_code="$(cat "$shard/exit-code")"
  if [ "$shard_code" = 3 ]; then
    echo "::warning::sweep $NAME shard $i/$SHARDS exited 3 (a run stopped on a violation or timer fault); keeping its results"
    code=3
  fi
  python3 "$ROOT/infra/sweeps/trim.py" "$shard/$NAME" "$OUT/_trimmed/shard-$i" >/dev/null
  trimmed+=("$OUT/_trimmed/shard-$i")
done
python3 "$ROOT/infra/sweeps/merge-shards.py" "$OUT/$NAME" "${trimmed[@]}" >&2
log "merged into $OUT/$NAME"
exit "$code"
