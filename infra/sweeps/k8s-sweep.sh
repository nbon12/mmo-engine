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
#   HARNESS_CLUSTER       kind (default) or k3d: where the image is loaded; none to skip loading (the
#                         cluster pulls SWEEP_IMAGE itself)
#   HARNESS_CLUSTER_NAME  cluster name (default mmo-harness, as up.sh)
#   SWEEP_IMAGE           image name (default mmo-engine/regionizer-sim:local)
#   SWEEP_SKIP_BUILD      1 to use SWEEP_IMAGE as it is, without building it
#   SWEEP_NAMESPACE       namespace (default sweeps)
#   SWEEP_PARALLELISM     pods at once (default: every shard)
#   SWEEP_CPU, SWEEP_MEMORY  each pod's requests (default 1 and 512Mi)
#   SWEEP_STORAGE, SWEEP_ACCESS_MODE  the results volume (default 5Gi, ReadWriteOnce)
#   SWEEP_TIMEOUT         seconds to wait for the Job (default 21600)
#   MMO_RUNNER_CLASS      the runner class recorded in each manifest.json (default k8s-<cluster>)
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
log() { printf '[k8s sweep] %s\n' "$*" >&2; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "missing tool: $1" >&2; exit 2; }; }
need kubectl; need python3

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
if [ -d "$OUT" ] && [ -n "$(ls -A "$OUT")" ]; then echo "$OUT is not empty; use a new folder" >&2; exit 2; fi
mkdir -p "$OUT"

if [ "${SWEEP_SKIP_BUILD:-}" != 1 ]; then
  need docker
  log "building $IMAGE"
  docker build -q -f "$ROOT/infra/sweeps/Dockerfile" -t "$IMAGE" "$ROOT" >/dev/null
fi
case "$TOOL" in
  kind) need kind; log "loading $IMAGE into kind cluster $CLUSTER"; kind load docker-image "$IMAGE" --name "$CLUSTER" >/dev/null ;;
  k3d) need k3d; log "loading $IMAGE into k3d cluster $CLUSTER"; k3d image import "$IMAGE" -c "$CLUSTER" >/dev/null ;;
  none) ;;
  *) echo "HARNESS_CLUSTER must be kind, k3d or none, was $TOOL" >&2; exit 2 ;;
esac

JOB="sweep-$NAME"
READER="$JOB-reader"
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
# A Job's pod template can't change, and the results volume must start empty: remove an earlier run's.
kubectl -n "$NS" delete "job/$JOB" "pod/$READER" "pvc/$JOB-results" "configmap/$JOB-spec" \
  --ignore-not-found --wait >/dev/null
kubectl -n "$NS" create configmap "$JOB-spec" --from-file=spec.json="$SPEC" >/dev/null
log "running $NAME as $SHARDS shards (job/$JOB in namespace $NS)"
SWEEP_NAME="$NAME" NAMESPACE="$NS" SHARDS="$SHARDS" PARALLELISM="${SWEEP_PARALLELISM:-$SHARDS}" IMAGE="$IMAGE" \
  RUNNER_CLASS="${MMO_RUNNER_CLASS:-k8s-$TOOL}" CPU="${SWEEP_CPU:-1}" MEMORY="${SWEEP_MEMORY:-512Mi}" \
  STORAGE="${SWEEP_STORAGE:-5Gi}" ACCESS_MODE="${SWEEP_ACCESS_MODE:-ReadWriteOnce}" \
  python3 -c '
import os, string, sys
print(string.Template(open(sys.argv[1]).read()).substitute(os.environ))' "$ROOT/infra/sweeps/manifests/sweep-job.yaml" \
  | kubectl apply -f - >/dev/null

state=""
deadline=$((SECONDS + TIMEOUT))
while [ "$SECONDS" -lt "$deadline" ]; do
  state="$(kubectl -n "$NS" get "job/$JOB" -o json | python3 -c '
import json, sys
conditions = json.load(sys.stdin).get("status", {}).get("conditions", [])
print(next((c["type"] for c in conditions if c["type"] in ("Complete", "Failed") and c["status"] == "True"), ""))')"
  [ -n "$state" ] && break
  sleep 5
done
if [ "$state" != Complete ]; then
  echo "job/$JOB ${state:-did not finish in ${TIMEOUT}s}; its pods:" >&2
  kubectl -n "$NS" get pods -l "job-name=$JOB" -o wide >&2 || true
  kubectl -n "$NS" logs -l "job-name=$JOB" --tail=50 --prefix >&2 || true
  exit 1
fi

log "copying the shards out"
SWEEP_NAME="$NAME" NAMESPACE="$NS" IMAGE="$IMAGE" python3 -c '
import json, os
print(json.dumps({"apiVersion": "v1", "kind": "Pod",
  "metadata": {"name": "sweep-%s-reader" % os.environ["SWEEP_NAME"], "namespace": os.environ["NAMESPACE"]},
  "spec": {"restartPolicy": "Never", "containers": [{"name": "reader", "image": os.environ["IMAGE"],
    "imagePullPolicy": "IfNotPresent", "command": ["sleep", "3600"],
    "volumeMounts": [{"name": "results", "mountPath": "/results", "readOnly": True}]}],
    "volumes": [{"name": "results", "persistentVolumeClaim": {"claimName": "sweep-%s-results" % os.environ["SWEEP_NAME"]}}]}}))' \
  | kubectl apply -f - >/dev/null
kubectl -n "$NS" wait --for=condition=Ready "pod/$READER" --timeout=5m >/dev/null
kubectl -n "$NS" cp "$READER:/results" "$OUT/shards" >/dev/null
kubectl -n "$NS" delete "pod/$READER" --wait=false >/dev/null

code=0
trimmed=()
for k in $(seq 1 "$SHARDS"); do
  shard="$OUT/shards/shard-$k"
  [ -f "$shard/exit-code" ] || { echo "shard $k wrote no exit-code" >&2; exit 1; }
  shard_code="$(cat "$shard/exit-code")"
  if [ "$shard_code" = 3 ]; then
    echo "::warning::sweep $NAME shard $k/$SHARDS exited 3 (a run stopped on a violation or timer fault); keeping its results"
    code=3
  fi
  python3 "$ROOT/infra/sweeps/trim.py" "$shard/$NAME" "$OUT/trimmed/shard-$k" >/dev/null
  trimmed+=("$OUT/trimmed/shard-$k")
done
python3 "$ROOT/infra/sweeps/merge-shards.py" "$OUT/$NAME" "${trimmed[@]}" >&2
log "merged into $OUT/$NAME"
exit "$code"
