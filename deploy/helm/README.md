# Helm deployment: dotnet-diagnostics-orchestrator

The central orchestrator Helm chart lives at [`dotnet-diagnostics-orchestrator/`](./dotnet-diagnostics-orchestrator).
It deploys the Phase 7 orchestrator surface (`list_pods`, `attach_to_pod`, routed diagnostics tools) behind the same HTTP auth surface as the existing sidecar: legacy/static bearer tokens, scoped opaque tokens, or OIDC/JWT validation.

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

The orchestrator authenticates clients with either opaque bearer tokens or OIDC-minted JWTs. **The bearer/JWT travels in the `Authorization` header on every request**, so plaintext HTTP on anything other than a strict loopback interface lets any network observer on the path (kube-proxy hop, mesh sidecar, transparent IDS, …) replay it.

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

## Metrics and Prometheus scraping

The chart exposes the orchestrator's Prometheus endpoint on the same Service port at
`/metrics`.

- `metrics.enabled=true` (default) keeps the endpoint mapped.
- `metrics.open=false` (default) keeps `/metrics` **scope-protected** — callers need a bearer with the `metrics-read` scope.
- `metrics.open=true` makes `/metrics` unauthenticated for the common "cluster-local scrape behind mTLS / NetworkPolicy" posture.
- `serviceMonitor.enabled=true` renders `templates/serviceMonitor.yaml` when the Prometheus Operator CRD is installed.
- If you keep `metrics.open=false`, set `serviceMonitor.bearerTokenSecret.{name,key}` to a Secret containing a bearer with `metrics-read`.

Example values:

```yaml
metrics:
  enabled: true
  open: false
serviceMonitor:
  enabled: true
  interval: 30s
  scrapeTimeout: 10s
  bearerTokenSecret:
    name: dotnet-diag-tokens
    key: metrics
```

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

## Scoped bearer tokens (recommended)

> **B5.5 (#186) — implements [RFC 0001 §6](../../docs/rfcs/0001-per-tool-authorization-scopes.md#6-wire-format-and-config-examples).**
> The legacy `bearerToken.value` / `bearerToken.existingSecret` path keeps working
> unchanged for backward compatibility, but new deployments should bind one or
> more scoped tokens via `bearerTokens`. Each token is restricted to a subset of
> tools by its `scopes` list (see RFC §2 for the taxonomy: `read-counters`,
> `eventpipe`, `heap-read`, `sensitive-heap-read`, `ptrace`, `dump-write`,
> `orchestrator-list`, `orchestrator-attach`, `orchestrator-admin`,
> `investigation-export`, `job-control`, `metrics-read`, or `*` for root).

### `values.yaml` example — viewer + admin pair

```yaml
bearerTokens:
  - name: ops-viewer
    valueFrom:
      secretKeyRef:
        name: dotnet-diag-tokens
        key: viewer
    scopes:
      - read-counters
      - eventpipe
      - investigation-export
  - name: ops-admin
    valueFrom:
      secretKeyRef:
        name: dotnet-diag-tokens
        key: admin
    scopes:
      - "*"
```

The chart renders each entry as:

```yaml
env:
  - name: Auth__BearerTokens__0__Name
    value: ops-viewer
  - name: Auth__BearerTokens__0__Token
    valueFrom:
      secretKeyRef: { name: dotnet-diag-tokens, key: viewer }
  - name: Auth__BearerTokens__0__Scopes__0
    value: read-counters
  # …one __Scopes__{j} env var per scope, repeated for each token
```

These are the exact keys consumed by `BearerTokenRegistry` (server-side, B5.1
#188 — parses `Auth:BearerTokens`).

### Matching multi-key Secret (RFC §6.3)

Create the Secret out-of-band so the chart never sees the raw token values.
A single multi-key Secret is the recommended layout:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: dotnet-diag-tokens
  namespace: diagnosticsmcp-system
type: Opaque
stringData:
  viewer: "<output of: openssl rand -hex 32>"
  admin:  "<output of: openssl rand -hex 32>"
```

Apply it before `helm install`:

```bash
kubectl -n diagnosticsmcp-system create secret generic dotnet-diag-tokens \
  --from-literal=viewer="$(openssl rand -hex 32)" \
  --from-literal=admin="$(openssl rand -hex 32)"
```

> Per RFC §6.3, the token **names** (`ops-viewer`, `ops-admin`) appear in audit
> logs and are non-sensitive; only the values are. Use the N-single-key-Secrets
> alternative only when different RBAC subjects must be able to rotate
> different tokens independently.

### Precedence and back-compat

| Configuration | Behaviour |
|---|---|
| `bearerToken` only (legacy single-bearer) | Unchanged — chart renders the legacy Secret and the `MCP_BEARER_TOKEN` env var. The token resolves to a synthetic root principal. |
| `bearerTokens` only (recommended) | Chart skips the legacy Secret + env var entirely. Server loads scoped tokens from `Auth:BearerTokens`. |
| Both set | Chart renders both. **The scoped registry wins at runtime; the legacy `MCP_BEARER_TOKEN` is ignored and the server logs a `Warning` at startup naming the ignored variable** (RFC §7.1, B5.1 #188). Useful only as a migration overlap window. |
| Neither set (defaults) | `helm template` / `helm install` aborts with the H1 placeholder guard. Migrate to `bearerTokens` or override `bearerToken.value`. |

### Cloud Run / non-Kubernetes wiring

The same env shape (`Auth__BearerTokens__N__Name` / `__Token` / `__Scopes__M`)
is what `BearerTokenRegistry` reads regardless of platform. See
[RFC 0001 §6.4](../../docs/rfcs/0001-per-tool-authorization-scopes.md#64-cloud-run--secret-manager-sketch)
for the Cloud Run + Secret Manager mapping (`--set-secrets` for token values,
`--set-env-vars` for names and scope lists). The same approach applies to ECS
task definitions, Azure Container Apps secrets, and any other env-var-driven
container platform.

### Modifier scopes vs legacy `Diagnostics__Allow*` flags (B5.4 / #185)

Three RFC 0001 scopes — `sensitive-heap-read`, `eventsource-any`, and
`symbols-remote` — subsume the legacy deployment-wide flags
`Diagnostics:AllowSensitiveHeapValues`, `Diagnostics:EventSourceAllowlist`, and
`Diagnostics:SymbolServerAllowlist`. Layer them onto specific bearer tokens
instead of unlocking them globally:

```yaml
bearerTokens:
  - name: ops-incident
    valueFrom:
      secretKeyRef: { name: dotnet-diag-tokens, key: incident }
    scopes:
      - heap-read
      - ptrace
      - sensitive-heap-read   # raw string previews on heap drilldowns
      - eventsource-any       # any EventSource provider, not just the curated allowlist
      - symbols-remote        # remote symbol servers (`srv*https://…`)
```

Modifier scopes are deliberately **literal** — a `*` (root) bearer does NOT
auto-grant them, so the deployment-wide gate still applies unless the operator
explicitly layers the scope on top of a privileged token. The legacy flags
remain available as fallback; they log a once-per-process deprecation warning
when they are what unlocked a call. See `docs/tool-reference.md` § "Security
gates (B4)" and `docs/rfcs/0001-per-tool-authorization-scopes.md` § 7 for the
deprecation timeline.

## OIDC / JWT auth (Phase 8 / #196)

Set `oidc.enabled=true` to let the HTTP transport accept JWT-shaped bearer tokens from an external issuer (Entra ID, IAM Identity Center, Keycloak, workload identity federation, ...). Opaque bearer tokens keep working unchanged; the server routes JWT-shaped tokens through OIDC discovery + JWKS validation and leaves everything else on `bearerTokens` / `MCP_BEARER_TOKEN`.

### `values.yaml` example

```yaml
oidc:
  enabled: true
  issuer: https://login.microsoftonline.com/<tenant-id>/v2.0
  audience: api://dotnet-diagnostics-mcp
  # Optional when your issuer uses a non-standard claim name.
  scopeClaim: ""
  # Optional: claim presence only -> null, exact value -> string, allowlist -> array.
  requiredClaimsJson: '{"azp":"diag-client"}'
```

When enabled the chart renders these env vars into the Pod:

- `MCP_OIDC_ISSUER`
- `MCP_OIDC_AUDIENCE`
- optional `MCP_OIDC_SCOPE_CLAIM`
- optional `MCP_OIDC_REQUIRED_CLAIMS_JSON`

`oidc.enabled=true` requires both `oidc.issuer` and `oidc.audience`. Use `requiredClaimsJson` to pin extra issuer-specific claims such as `azp`, `appid`, or tenant ids without changing server code.

## Operations and security

- **Token rotation:** update the Secret, then restart the Deployment (`kubectl rollout restart deploy/<release>-dotnet-diagnostics-orchestrator`). Existing in-memory investigation handles are intentionally lost on restart; clients re-run `attach_to_pod` per the stateless design.
- **RBAC scope:** the chart defaults to namespace-scoped `Role`+`RoleBinding`. Set `rbac.scope=cluster` to opt into the previous cluster-wide `ClusterRole`+`ClusterRoleBinding` when multi-namespace observation is required.
- **Exposure:** the bearer is equivalent to privileged diagnostics access inside the allowed scope. Bind only to a TLS-terminating Ingress / mesh sidecar (see the TLS section above), enable `networkPolicy.enabled` for L3/L4 fail-closed ingress, and avoid public Ingress unless an additional auth proxy is in front of it.
- **Current Linux attach limitation:** the chart deploys the orchestrator control plane, but today's `attach_to_pod` implementation still lacks the shared `/tmp` volume mount on injected ephemeral containers. Use the manual attach patch or sidecar topology for Linux investigations until that code follow-up lands.

For raw manifests and Kustomize overlays, see [`../k8s/README.md`](../k8s/README.md). For the transport design and stateless restart model, see [`../../docs/central-orchestrator-design.md`](../../docs/central-orchestrator-design.md).
