#!/usr/bin/env bash
# Deletes the harness cluster that up.sh created (kind by default, or k3d with HARNESS_CLUSTER=k3d).
set -euo pipefail
NAME="${HARNESS_CLUSTER_NAME:-mmo-harness}"
pkill -f "port-forward svc/clickhouse" 2>/dev/null || true
case "${HARNESS_CLUSTER:-kind}" in
  kind) kind delete cluster --name "$NAME" ;;
  k3d) k3d cluster delete "$NAME" ;;
  *) echo "HARNESS_CLUSTER must be kind or k3d" >&2; exit 2 ;;
esac
