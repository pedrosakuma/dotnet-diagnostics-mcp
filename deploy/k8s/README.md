# Deploying dotnet-diagnostics-mcp on Kubernetes

This folder now contains both Kubernetes deployment models shipped by the repo:

- the **always-on sidecar** for direct per-Pod investigations, and
- the **central orchestrator** for Phase 7 fleet mode (`list_pods`, `attach_to_pod`, proxied diagnostics tools).

## Files

- [`sample-sidecar.yaml`](./sample-sidecar.yaml) — Namespace, Secret, Deployment with **target app + diagnosticsmcp sidecar**, and a ClusterIP Service. Best when investigations are frequent.
- [`central-target.yaml`](./central-target.yaml) — prepared target Deployment **without** a sidecar; used by the on-demand and orchestrator flows.
- [`ephemeral-attach.patch.json`](./ephemeral-attach.patch.json) — raw `pods/ephemeralcontainers` patch for manual on-demand attach.
- [`CENTRAL-TOPOLOGY.md`](./CENTRAL-TOPOLOGY.md) — operator-driven on-demand recipe and background on prepared targets.
- [`orchestrator/`](./orchestrator) — raw RBAC / Secret / Deployment / Service manifests plus Kustomize overlays for the central orchestrator.
- [`../helm/README.md`](../helm/README.md) — Helm install path for the same orchestrator surface.

## Sidecar topology refresher

The sidecar topology remains the simplest direct deployment model when one Pod equals one diagnostics endpoint.
It still requires the same Linux pod-level prerequisites:

1. **Shared `/tmp`** between the app and diagnostics container so both see `/tmp/dotnet-diagnostic-<pid>`.
2. **Shared PID visibility** (`shareProcessNamespace: true`) so the sidecar can enumerate the target process.
3. **Matching UID/GID (or `fsGroup`)** so the sidecar can open the diagnostic socket.

See [`sample-sidecar.yaml`](./sample-sidecar.yaml) for the canonical manifest.

## Central orchestrator quick starts

### 1. Raw `kubectl apply`

```bash
kubectl create namespace diagnosticsmcp-system
kubectl create namespace diagnosticsmcp-workloads

# Edit deploy/k8s/orchestrator/deployment.yaml so Orchestrator__DefaultNamespace
# and Orchestrator__NamespaceAllowlist__0 match your target namespace.
# Edit deploy/k8s/orchestrator/rbac.yaml if you install outside diagnosticsmcp-system.
# Replace the placeholder Secret token with `openssl rand -hex 32` output.

kubectl apply -f deploy/k8s/orchestrator/rbac.yaml
kubectl apply -f deploy/k8s/orchestrator/secret.yaml
kubectl apply -f deploy/k8s/orchestrator/deployment.yaml
kubectl apply -f deploy/k8s/orchestrator/service.yaml
```

Then port-forward the Service and point the MCP client at `http://127.0.0.1:5130/mcp`.

### 2. Kustomize overlay

```bash
kubectl kustomize deploy/k8s/orchestrator/kustomize/overlays/dev | kubectl apply -f -
```

The dev overlay scopes discovery to `diagnosticsmcp-dev`, generates a placeholder bearer Secret, and keeps the image on `ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`.
The prod overlay shows the same pattern with a pinned tag, explicit `replicas: 1`, and larger resource reservations.

### 3. Helm

```bash
helm upgrade --install diag-orchestrator \
  deploy/helm/dotnet-diagnostics-orchestrator \
  --namespace diagnosticsmcp-system \
  --create-namespace \
  --set bearerToken.value="$(openssl rand -hex 32)" \
  --set orchestrator.allowedNamespaces[0]=diagnosticsmcp-workloads \
  --set orchestrator.defaultNamespace=diagnosticsmcp-workloads
```

See [`../helm/README.md`](../helm/README.md) for chart values, `helm test`, and notes output.

## Operator runbook

### Rotate the outer bearer token

1. Update the Secret (`kubectl apply -f deploy/k8s/orchestrator/secret.yaml` or `kubectl patch secret ...`).
2. Restart the orchestrator Deployment: `kubectl rollout restart deploy/dotnet-diagnostics-orchestrator -n <ns>`.
3. Distribute the new token to clients.

Restart is expected: the orchestrator is intentionally stateless across restarts (see [`docs/central-orchestrator-design.md` §5.7](../../docs/central-orchestrator-design.md#57-orchestrator-restart-behavior)). Existing in-memory investigation handles are discarded and clients re-run `attach_to_pod`.

### Scope RBAC to a single namespace

Issue #20 requires `pods`, `pods/ephemeralcontainers`, and `pods/portforward`. The shipped raw/Helm surfaces use a ClusterRole because that is the exact fleet-wide permission set the orchestrator needs when it spans namespaces.

For a single tenant namespace, replace the ClusterRole / ClusterRoleBinding with a Role / RoleBinding carrying the same rules in that namespace:

```yaml
apiGroups: [""]
resources: ["pods"]
verbs: ["get", "list", "watch"]
---
apiGroups: [""]
resources: ["pods/ephemeralcontainers"]
verbs: ["update", "patch"]
---
apiGroups: [""]
resources: ["pods/portforward"]
verbs: ["create"]
```

Keep `Orchestrator__NamespaceAllowlist__*` aligned with the namespace you grant.

### How the orchestrator reaches the pod-local MCP server

Per the design doc's [§4 proxy mechanics](../../docs/central-orchestrator-design.md#4-proxy-mechanics), the orchestrator does **not** shell out to `kubectl port-forward`.
It uses the Kubernetes API in-process to:

1. patch `pods/ephemeralcontainers`,
2. wait for the injected diagnostics container to become Running,
3. open `pods/portforward` streams to the pod-local MCP listener on `Orchestrator__ProxyPodPort` (default `5130`), and
4. proxy the existing diagnostics tool surface through that investigation handle.

That is why the orchestrator itself does **not** need `CAP_SYS_PTRACE`: ptrace-heavy work happens inside the injected per-Pod diagnostics container, not in the central Deployment.

## Security notes

- **Bearer = privileged diagnostics access.** Treat the orchestrator token as equivalent to shell-like access inside the namespaces it can reach.
- **Do not expose the Service publicly.** Keep it `ClusterIP`, require an auth proxy or mesh identity at the edge, and prefer mTLS / NetworkPolicies between clients and the orchestrator.
- **Keep Secrets external when possible.** `MCP_BEARER_TOKEN` should come from a Kubernetes Secret or external secret manager; never bake it into the image.
- **Prepared targets still matter.** The orchestrator still depends on the shared `/tmp` emptyDir + matching UID contract documented in [`CENTRAL-TOPOLOGY.md`](./CENTRAL-TOPOLOGY.md) and [`central-target.yaml`](./central-target.yaml).
- **Current Linux attach limitation:** the deploy assets package the orchestrator control plane today, but the current `attach_to_pod` implementation does **not yet** inject the target's shared `/tmp` volume mount into its ephemeral diagnostics container. On Linux prepared targets, keep using the manual [`ephemeral-attach.patch.json`](./ephemeral-attach.patch.json) or the always-on sidecar until that follow-up lands in code; otherwise the pod-local MCP server will not see `/tmp/dotnet-diagnostic-*`.
- **Ephemeral containers persist until pod recreation.** `detach` only closes the orchestrator-side session; operators still restart / recreate the Pod to remove the in-Pod diagnostics container.
