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

## Chart notes

- `bearerToken.existingSecret` lets operators plug in an externally managed Secret with key `token`; otherwise the chart can create one from `bearerToken.value`.
- `orchestrator.allowedNamespaces` maps to `Orchestrator__NamespaceAllowlist__*`. When omitted, the chart defaults to the Helm release namespace so the deployment stays fail-closed.
- `orchestrator.ephemeralContainerImage` defaults to the chart image. Pin a digest or release tag in production so the orchestrator and injected ephemeral container stay aligned.

## Operations and security

- **Token rotation:** update the Secret, then restart the Deployment (`kubectl rollout restart deploy/<release>-dotnet-diagnostics-orchestrator`). Existing in-memory investigation handles are intentionally lost on restart; clients re-run `attach_to_pod` per the stateless design.
- **RBAC scope:** the chart ships the cluster-scoped verbs required by issue #20. If a tenant only wants one namespace, replace the chart's ClusterRole/ClusterRoleBinding with a Role/RoleBinding carrying the same rules in that namespace.
- **Exposure:** the bearer is equivalent to privileged diagnostics access inside the allowed scope. Keep the Service internal, add network policies / mTLS at the cluster edge, and avoid public Ingress unless an auth proxy is in front of it.
- **Current Linux attach limitation:** the chart deploys the orchestrator control plane, but today's `attach_to_pod` implementation still lacks the shared `/tmp` volume mount on injected ephemeral containers. Use the manual attach patch or sidecar topology for Linux investigations until that code follow-up lands.

For raw manifests and Kustomize overlays, see [`../k8s/README.md`](../k8s/README.md). For the transport design and stateless restart model, see [`../../docs/central-orchestrator-design.md`](../../docs/central-orchestrator-design.md).
