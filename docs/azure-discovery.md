# Azure discovery

> v1 design for the `discover_azure` MCP tool. Tracking parent: [#230](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/230). Contract PR: [#232](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/232).

## Overview

`discover_azure` lets an LLM enumerate .NET workload candidates inside a single
Azure subscription, across three platforms:

| `kind`           | Platform                    | Backend PR |
|------------------|-----------------------------|------------|
| `webapps`        | Azure App Service           | [#233](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/233) (shipped) |
| `containerapps`  | Azure Container Apps        | [#233](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/233) (shipped) |
| `aksclusters`    | Azure Kubernetes Service    | [#234](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/234) |

Hard rules baked into the contract:

- **`subscriptionId` is required** on every call. There is no "list across every
  subscription the credential can see" mode — the LLM must commit to a scope.
- **No raw kubeconfig over MCP.** When `kind=aksclusters` and
  `includeKubeconfig=true`, the AKS backend mints an opaque handle
  (`AzureAksHandoff`) in a process-local store with a TTL. Consumers exchange
  the handle for an attach via the orchestrator, never via this tool.
- **Read-only.** No write to ARM, no resource modification.

## Tool signature

```csharp
[McpServerTool, Description("Discover .NET workloads in an Azure subscription…")]
public async Task<DiagnosticResult<DiscoverAzureResult>> DiscoverAzureAsync(
    string subscriptionId,                          // required
    string kind = "webapps",                        // webapps | containerapps | aksclusters
    string? resourceGroup = null,                   // optional filter
    bool   includeStopped = false,
    int    limit = 100,                             // clamped to 500
    string? cursor = null,
    bool   includeKubeconfig = false,               // aksclusters only
    CancellationToken cancellationToken = default);
```

`limit` is clamped server-side to a maximum of `500` (mirrors the orchestrator
`list_orchestrator` ceiling). Backends MAY clamp further.

## Response envelope

The discriminator echo lives at `data.kind`. Exactly one of `data.webapps` /
`data.containerapps` / `data.aksclusters` is populated; the other two are
`null`. Same shape as `list_orchestrator` so JSON consumers can branch without
re-running the tool.

### `kind=webapps`

```jsonc
{
  "summary": "discover_azure(kind=webapps): 2 candidate(s).",
  "hints": [],
  "data": {
    "kind": "webapps",
    "webapps": {
      "items": [
        {
          "resourceId": "/subscriptions/<sub>/resourceGroups/api/providers/Microsoft.Web/sites/checkout",
          "name": "checkout",
          "location": "westeurope",
          "defaultHostName": "checkout.azurewebsites.net",
          "runtimeStack": "DOTNETCORE|8.0",
          "runtimeVersion": "8.0",
          "instanceCount": 2,
          "state": "Running",
          "kind": "app,linux",
          "readinessWarnings": []
        }
      ],
      "nextCursor": null
    },
    "containerapps": null,
    "aksclusters":   null
  }
}
```

### `kind=containerapps`

```jsonc
{
  "summary": "discover_azure(kind=containerapps): 1 candidate(s).",
  "hints": [],
  "data": {
    "kind": "containerapps",
    "webapps": null,
    "containerapps": {
      "items": [
        {
          "resourceId": "/subscriptions/<sub>/resourceGroups/api/providers/Microsoft.App/containerApps/checkout",
          "name": "checkout",
          "location": "westeurope",
          "latestRevisionFqdn": "checkout.kindforest-1234abcd.westeurope.azurecontainerapps.io",
          "containerImages": ["myregistry.azurecr.io/checkout:1.2.3"],
          "minReplicas": 1,
          "maxReplicas": 5,
          "provisioningState": "Succeeded",
          "runningState": "Running",
          "readinessWarnings": []
        }
      ],
      "nextCursor": null
    },
    "aksclusters": null
  }
}
```

### `kind=aksclusters`

```jsonc
{
  "summary": "discover_azure(kind=aksclusters): 1 candidate(s).",
  "hints": [],
  "data": {
    "kind": "aksclusters",
    "webapps": null,
    "containerapps": null,
    "aksclusters": {
      "items": [
        {
          "resourceId": "/subscriptions/<sub>/resourceGroups/prod/providers/Microsoft.ContainerService/managedClusters/prod-aks",
          "name": "prod-aks",
          "location": "westeurope",
          "fqdn": "prod-aks-abc12345.hcp.westeurope.azmk8s.io",
          "kubernetesVersion": "1.30.0",
          "agentPoolCount": 3,
          "nodeResourceGroup": "MC_prod_prod-aks_westeurope",
          "handoff": {
            "kubeconfigHandle": "kc:0e8a571b-...",
            "expiresAt": "2025-12-01T12:00:00Z"
          },
          "readinessWarnings": []
        }
      ],
      "nextCursor": null
    }
  }
}
```

When `includeKubeconfig=false` (default), `handoff` is `null`. The AKS backend
NEVER returns raw kubeconfig content on this surface.

## Authentication

The Azure ARM client is built by `IAzureArmClientFactory` (added in #231) using
`Azure.Identity.DefaultAzureCredential` with default options. That gives the
standard discovery chain:

1. Environment variables (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, …)
2. Workload Identity (`AZURE_FEDERATED_TOKEN_FILE` in AKS)
3. Managed Identity (when running inside an Azure resource)
4. Azure CLI (`az login`)
5. Visual Studio / VS Code
6. Interactive browser (development only)

The factory holds a single long-lived credential; per-call `ArmClient` instances
are scoped to one subscription. Custom credential-chain configuration is
deliberately out of scope for v1 — when a consumer needs it (#233 / #234) the
options surface will be extended with a strict opt-in.

## RBAC

- All three `kind` values require **Reader** on the subscription (or a tighter
  resource-group scope) for listing. For `kind=webapps` and `kind=containerapps`
  this is **the only role needed** — the backends only call ARM list endpoints
  and never read app settings, secrets, or connection strings.
- `kind=aksclusters` with `includeKubeconfig=true` additionally requires the
  **Azure Kubernetes Service Cluster User Role** on each cluster the backend
  mints a kubeconfig for. The exact behaviour (silent skip, partial result, or
  hard fail) is decided in #234 alongside the kubeconfig handle store.

## Example invocations

The tool is reachable via any MCP client speaking the `discover_azure` tool name.
JSON-RPC arguments mirror the C# signature:

```jsonc
// Enumerate every App Service site in the subscription
{
  "name": "discover_azure",
  "arguments": {
    "subscriptionId": "00000000-0000-0000-0000-000000000000",
    "kind": "webapps"
  }
}

// Same, scoped to a single resource group, including stopped sites
{
  "name": "discover_azure",
  "arguments": {
    "subscriptionId": "00000000-0000-0000-0000-000000000000",
    "kind": "webapps",
    "resourceGroup": "rg-api-prod",
    "includeStopped": true
  }
}

// Enumerate Container Apps in a single resource group, page 2
{
  "name": "discover_azure",
  "arguments": {
    "subscriptionId": "00000000-0000-0000-0000-000000000000",
    "kind": "containerapps",
    "resourceGroup": "rg-checkout",
    "cursor": "<opaque continuation token from the prior response.nextCursor>"
  }
}
```

## readinessWarnings catalog

The backends emit best-effort signals on each candidate so the LLM can rank
attach targets without an extra round-trip. Empty `readinessWarnings` means no
problems were detected (it does NOT prove the site is attach-ready).

### `kind=webapps`

| Warning | Trigger | What it means for the LLM |
|---------|---------|---------------------------|
| `Windows OS — sidecar not supported` | `kind` field does not contain `linux` (e.g. `app`, `app,functionapp`). | Skip this site — the sidecar topology requires the Linux multi-container App Service surface. |
| (none — function apps excluded) | `kind` field contains `functionapp`. | Function apps are filtered out entirely; they never appear in the result. |

### `kind=containerapps`

| Warning | Trigger | What it means for the LLM |
|---------|---------|---------------------------|
| `No second container detected — sidecar topology not deployed` | Latest revision template has ≤ 1 container. | The app hasn't been migrated to the sidecar topology. Attach will fail; suggest the topology migration first. |
| `Scale=0` | `template.scale.minReplicas == 0`. | App may be scaled to zero and unreachable. Trigger a request before attempting to attach, or warn the user. |

## Implementation notes (issue #233)

- `IAzureWebAppsDiscovery` and `IAzureContainerAppsDiscovery` are now backed by
  the Azure SDK (`Azure.ResourceManager.AppService` / `.AppContainers`). The
  backends consume thin adapter interfaces
  (`IAzureWebSiteCollectionAdapter`, `IAzureContainerAppCollectionAdapter`) so
  unit tests can substitute in-memory fakes without referencing the Azure SDK.
- One `ListAsync` call consumes exactly one Azure SDK page. The adapter's
  continuation token is passed through verbatim as `nextCursor`; backend-side
  filtering (function-app exclusion, stopped-state filter) may shrink the page
  below the requested `limit` but the cursor still advances.
- AKS still routes through the `NotImplementedAzureAksDiscovery` stub until
  #234 lands.

## Stateless server contract

The MCP server stays stateless per RFC 0002. The kubeconfig handle store landing
in #234 is **process-local with a TTL**: handles do not survive a server
restart, are not shared across replicas, and never get persisted to disk.
Re-discovery is cheap and explicit.

## Scope

`discover_azure` requires the `azure-discovery` scope on the calling bearer
token. Mirroring the orchestrator scope split, this is a separate trust domain
from the `orchestrator-*` family because Azure ARM credentials and pod-local
diagnostic credentials have different blast radii. A token holding
`orchestrator-list` does NOT authorise Azure discovery.

## What this PR ships

[#232](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/232) ships
**only the contract**:

- The `discover_azure` tool with `kind`-dispatch, scope enforcement, structured
  error envelopes, and limit clamping.
- The three backend interfaces (`IAzureWebAppsDiscovery`,
  `IAzureContainerAppsDiscovery`, `IAzureAksDiscovery`) plus default
  implementations that throw `NotImplementedException`.
- The response record types — `AzureWebAppCandidate`,
  `AzureContainerAppCandidate`, `AzureAksClusterCandidate`, `AzureAksHandoff`,
  `AzurePagedResult<T>`, `DiscoverAzureResult`.
- Registration gated on `AzureDiscovery:Enabled`. When the master switch is
  off the tool is not exposed and the Azure SDK is never reached.

The real backends arrive in #233 (App Service + Container Apps) and #234 (AKS).
