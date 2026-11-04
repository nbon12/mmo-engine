#!/usr/bin/env bash
# Prints the identity of a running harness: the cluster node's container, the namespaces, the credentials
# secret and its password hash, each deployment and daemonset, and each pod (a rollout replaces pods
# without changing the deployment's UID). Two runs of up.sh that recreate nothing
# print the same lines (spec 014, US6 scenario 2).
set -euo pipefail
NAME="${HARNESS_CLUSTER_NAME:-mmo-harness}"
case "${HARNESS_CLUSTER:-kind}" in
  kind) echo "node $(docker inspect --format '{{.Id}}' "$NAME-control-plane")" ;;
  k3d) echo "node $(docker inspect --format '{{.Id}}' "k3d-$NAME-server-0")" ;;
esac
for ns in harness chaos-mesh; do
  echo "namespace $ns $(kubectl get namespace "$ns" -o jsonpath='{.metadata.uid}')"
done
echo "secret $(kubectl -n harness get secret clickhouse-credentials -o jsonpath='{.metadata.uid}')" \
  "$(kubectl -n harness get secret clickhouse-credentials -o jsonpath='{.data.password}' | sha256sum | cut -c1-16)"
kubectl -n harness get deployments -o jsonpath='{range .items[*]}deployment {.metadata.name} {.metadata.uid}{"\n"}{end}' | sort
kubectl -n chaos-mesh get deployments,daemonsets -o jsonpath='{range .items[*]}{.kind} {.metadata.name} {.metadata.uid}{"\n"}{end}' | sort
for ns in harness chaos-mesh; do
  kubectl -n "$ns" get pods -o jsonpath='{range .items[*]}pod {.metadata.namespace}/{.metadata.name} {.metadata.uid}{"\n"}{end}' | sort
done
