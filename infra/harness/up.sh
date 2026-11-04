#!/usr/bin/env bash
# The one command (spec 014, US6, FR-030): bring up the harness on a local kind (default) or k3d cluster,
# apply the store's schema, and port-forward ClickHouse to localhost. Idempotent: an existing cluster,
# secret, release or port-forward is reused. CI runs this same script.
#
#   infra/harness/up.sh [--observability]
#
# Environment:
#   HARNESS_CLUSTER       kind (default) or k3d
#   HARNESS_CLUSTER_NAME  cluster name (default mmo-harness)
#   HARNESS_CLICKHOUSE_PORT  local port for ClickHouse HTTP (default 18123)
#   CHAOS_MESH_VERSION    Chaos Mesh Helm chart version (default 2.8.4)
# Needs: docker, kubectl, helm, kind or k3d, and the .NET SDK (for `harness schema apply`).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TOOL="${HARNESS_CLUSTER:-kind}"
NAME="${HARNESS_CLUSTER_NAME:-mmo-harness}"
PORT="${HARNESS_CLICKHOUSE_PORT:-18123}"
CHAOS_MESH_VERSION="${CHAOS_MESH_VERSION:-2.8.4}"
OVERLAY="base"
for arg in "$@"; do
  case "$arg" in
    --observability) OVERLAY="observability" ;;
    *) echo "unknown argument: $arg (expected --observability)" >&2; exit 2 ;;
  esac
done

log() { printf '[harness up] %s\n' "$*" >&2; }
need() { command -v "$1" >/dev/null 2>&1 || { echo "missing tool: $1" >&2; exit 2; }; }
need docker; need kubectl; need helm; need dotnet

case "$TOOL" in
  kind)
    need kind
    # Capture first: under pipefail, `grep -q` exiting early can SIGPIPE the producer.
    clusters="$(kind get clusters 2>/dev/null || true)"
    if ! grep -qx "$NAME" <<<"$clusters"; then
      log "creating kind cluster $NAME"
      kind create cluster --name "$NAME" --config "$ROOT/infra/harness/kind-config.yaml" --wait 180s
    else
      log "reusing kind cluster $NAME"
    fi
    kubectl config use-context "kind-$NAME" >/dev/null
    RUNTIME_SOCKET=/run/containerd/containerd.sock
    ;;
  k3d)
    need k3d
    clusters="$(k3d cluster list -o json 2>/dev/null || true)"
    if ! grep -q "\"name\":\"$NAME\"" <<<"$clusters"; then
      log "creating k3d cluster $NAME"
      k3d cluster create "$NAME" --wait
    else
      log "reusing k3d cluster $NAME"
    fi
    kubectl config use-context "k3d-$NAME" >/dev/null
    RUNTIME_SOCKET=/run/k3s/containerd/containerd.sock
    ;;
  *) echo "HARNESS_CLUSTER must be kind or k3d, was $TOOL" >&2; exit 2 ;;
esac

kubectl create namespace harness --dry-run=client -o yaml | kubectl apply -f - >/dev/null
if ! kubectl -n harness get secret clickhouse-credentials >/dev/null 2>&1; then
  log "creating the clickhouse-credentials secret (random password, never committed)"
  kubectl -n harness create secret generic clickhouse-credentials \
    --from-literal=username=harness \
    --from-literal=password="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)" >/dev/null
fi

log "applying manifests ($OVERLAY)"
kubectl apply -k "$ROOT/infra/harness/manifests/$OVERLAY" >/dev/null

if [ "$OVERLAY" = base ]; then
  # kubectl apply doesn't prune: remove the observability profile if an earlier run installed it.
  kubectl -n harness delete deployment/prometheus deployment/grafana service/prometheus service/grafana \
    --ignore-not-found >/dev/null
fi

# The chart regenerates its webhook certificates and rolls the controller on every upgrade, so an
# installed release at the wanted version is left alone (idempotence, spec 014 US6 scenario 2).
releases="$(helm -n chaos-mesh list --deployed -o json 2>/dev/null || true)"
if grep -q "\"chart\":\"chaos-mesh-$CHAOS_MESH_VERSION\"" <<<"$releases"; then
  log "reusing Chaos Mesh $CHAOS_MESH_VERSION"
else
  log "installing Chaos Mesh $CHAOS_MESH_VERSION"
  helm repo add chaos-mesh https://charts.chaos-mesh.org >/dev/null 2>&1 || true
  helm repo update chaos-mesh >/dev/null
  helm upgrade --install chaos-mesh chaos-mesh/chaos-mesh --namespace chaos-mesh --create-namespace \
    --version "$CHAOS_MESH_VERSION" \
    --set chaosDaemon.runtime=containerd --set chaosDaemon.socketPath="$RUNTIME_SOCKET" \
    --set dashboard.create=false --wait --timeout 10m >/dev/null
fi

log "waiting for rollouts"
deployments=(clickhouse otel-collector)
if [ "$OVERLAY" = observability ]; then deployments+=(prometheus grafana); fi
for d in "${deployments[@]}"; do
  kubectl -n harness rollout status "deployment/$d" --timeout=10m >/dev/null
done
kubectl -n chaos-mesh rollout status deployment/chaos-controller-manager --timeout=10m >/dev/null
kubectl -n chaos-mesh rollout status daemonset/chaos-daemon --timeout=10m >/dev/null

USER_NAME="$(kubectl -n harness get secret clickhouse-credentials -o jsonpath='{.data.username}' | base64 -d)"
PASSWORD="$(kubectl -n harness get secret clickhouse-credentials -o jsonpath='{.data.password}' | base64 -d)"
# Something already answering on the port must be this cluster's ClickHouse: it must accept the cluster
# secret's credentials. Anything else (a throwaway container, a stale forward) is refused clearly.
if curl -fsS "http://127.0.0.1:$PORT/ping" >/dev/null 2>&1; then
  if ! curl -fsS -u "$USER_NAME:$PASSWORD" "http://127.0.0.1:$PORT/" --data-binary "SELECT 1" >/dev/null 2>&1; then
    echo "127.0.0.1:$PORT is taken by a ClickHouse that is not this cluster's (it rejects the cluster's credentials)." >&2
    echo "Stop it, or set HARNESS_CLICKHOUSE_PORT to a free port." >&2
    exit 1
  fi
  log "reusing the port-forward on 127.0.0.1:$PORT"
elif (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
  echo "127.0.0.1:$PORT is taken by something that is not ClickHouse; set HARNESS_CLICKHOUSE_PORT to a free port." >&2
  exit 1
else
  log "port-forwarding ClickHouse to 127.0.0.1:$PORT"
  nohup kubectl -n harness port-forward svc/clickhouse "$PORT:8123" >"${TMPDIR:-/tmp}/mmo-harness-port-forward.log" 2>&1 &
  for _ in $(seq 1 60); do
    curl -fsS "http://127.0.0.1:$PORT/ping" >/dev/null 2>&1 && break
    sleep 1
  done
  curl -fsS "http://127.0.0.1:$PORT/ping" >/dev/null || { echo "ClickHouse port-forward did not come up" >&2; exit 1; }
fi

export MMO_CLICKHOUSE="Host=127.0.0.1;Port=$PORT;Username=$USER_NAME;Password=$PASSWORD"
log "applying the store schema"
dotnet run --project "$ROOT/src/MmoEngine.Harness.Cli" -c Release -- schema apply >/dev/null

log "ready"
# The last line is the connection setting for the harness tools.
echo "export MMO_CLICKHOUSE=\"$MMO_CLICKHOUSE\""
