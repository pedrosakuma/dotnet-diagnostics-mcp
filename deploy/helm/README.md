# Helm deployment: dotnet-diagnostics-orchestrator

The central orchestrator Helm chart lives at [`dotnet-diagnostics-orchestrator/`](./dotnet-diagnostics-orchestrator).
It deploys the Phase 7 orchestrator surface (`list_pods`, `attach_to_pod`, routed diagnostics tools) behind the same bearer-token auth model as the existing sidecar.

## Quick start: Helm

```bash
helm upgrade --install diag-orchestrator \
  deploy/helm/dotnet-diagnostics-orchestrator \
  --namespace diagnosticsmcp-system \
  --create-namespace \
  --set bearerToken.value="$(openssl rand -hex 32)" \
  --set orchestrator.allowedNamespaces[0]=diagnosticsmcp-workloads \
  --set orchestrator.defaultNamespace=diagnosticsmcp-workloads
```

Then port-forward the Service and point the MCP client at `http://127.0.0.1:5130/mcp`.
Run `helm test diag-orchestrator -n diagnosticsmcp-system` after install to verify `/health` responds.

> **The chart refuses to render without an explicit bearer.** `helm template` /
> `helm install` exit with an error if `bearerToken.value` is still the public
> placeholder `replace-me-with-a-strong-random-token` and `bearerToken.existingSecret`
> is empty. Either supply `--set bearerToken.value=$(openssl rand -hex 32)` or
> `--set bearerToken.existingSecret=<name-of-existing-secret>`.

## TLS is required for any non-loopback bind

The orchestrator authenticates clients with a static bearer token. **The token
travels in the `Authorization` header on every request**, so plaintext HTTP on
anything other than a strict loopback interface lets any network observer on the
path (kube-proxy hop, mesh sidecar, transparent IDS, …) replay the bearer.

Two equally-valid postures are supported:

1. **TLS-terminating gateway in front of the Service** (recommended). Enable the
   bundled Ingress with `--set ingress.enabled=true` and supply your own
   `ingress.tls` block (cert-manager, externally managed cert, mesh ingress, …).
   Example:

   ```yaml
   ingress:
     enabled: true
     className: nginx
     hosts:
       - host: diagnostics.example.internal
         paths:
           - path: /
             pathType: Prefix
     tls:
       - secretName: diagnostics-orchestrator-tls
         hosts:
           - diagnostics.example.internal
   ```

2. **Service-mesh mTLS** (Istio, Linkerd, Cilium ClusterMesh, …). The chart's
   Service binds plain HTTP because the mesh sidecar handles TLS in-cluster.
   Make sure the mesh is enforcing STRICT mTLS — permissive mode still allows
   plaintext.

In both cases pair the chart with `networkPolicy.enabled=true` to fail-closed
ingress at L3/L4:

```yaml
networkPolicy:
  enabled: true
  fromNamespaces:
    - my-llm-client-ns
```

**Do NOT expose the orchestrator Service to the internet without a
TLS-terminating + authenticating proxy in front of it.** The bearer alone is
insufficient against passive observers.

## RBAC scope

The chart defaults to **namespace-scoped** `Role`+`RoleBinding`, granting
`pods get/list/watch`, `pods/ephemeralcontainers update/patch`, and
`pods/portforward get/create` only inside the chart's namespace. Use
`--set rbac.scope=cluster` to fall back to the previous cluster-wide
`ClusterRole`+`ClusterRoleBinding` when the orchestrator must observe / attach
to Pods across multiple namespaces — at the cost of widening blast radius if
the bearer is compromised.

The `pods/portforward` subresource needs BOTH `get` and `create`: the
Kubernetes API uses an HTTP GET that is upgraded to a WebSocket (HTTP 101)
for the port-forward stream that `KubernetesPortForwardManager` opens.

## Chart notes

- `bearerToken.existingSecret` lets operators plug in an externally managed Secret with key `token`; otherwise the chart can create one from `bearerToken.value`.
- `orchestrator.allowedNamespaces` maps to `Orchestrator__NamespaceAllowlist__*`. When omitted, the chart defaults to the Helm release namespace so the deployment stays fail-closed.
- `orchestrator.ephemeralContainerImage` defaults to the chart image. Pin a digest or release tag in production so the orchestrator and injected ephemeral container stay aligned.

## Operations and security

- **Token rotation:** update the Secret, then restart the Deployment (`kubectl rollout restart deploy/<release>-dotnet-diagnostics-orchestrator`). Existing in-memory investigation handles are intentionally lost on restart; clients re-run `attach_to_pod` per the stateless design.
- **RBAC scope:** the chart defaults to namespace-scoped `Role`+`RoleBinding`. Set `rbac.scope=cluster` to opt into the previous cluster-wide `ClusterRole`+`ClusterRoleBinding` when multi-namespace observation is required.
- **Exposure:** the bearer is equivalent to privileged diagnostics access inside the allowed scope. Bind only to a TLS-terminating Ingress / mesh sidecar (see the TLS section above), enable `networkPolicy.enabled` for L3/L4 fail-closed ingress, and avoid public Ingress unless an additional auth proxy is in front of it.
- **Current Linux attach limitation:** the chart deploys the orchestrator control plane, but today's `attach_to_pod` implementation still lacks the shared `/tmp` volume mount on injected ephemeral containers. Use the manual attach patch or sidecar topology for Linux investigations until that code follow-up lands.

For raw manifests and Kustomize overlays, see [`../k8s/README.md`](../k8s/README.md). For the transport design and stateless restart model, see [`../../docs/central-orchestrator-design.md`](../../docs/central-orchestrator-design.md).
