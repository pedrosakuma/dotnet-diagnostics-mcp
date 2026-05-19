# Azure deployment recipes

On-demand diagnostics for .NET applications running on Azure-managed container
hosts. Both recipes deploy `dotnet-diagnostics-mcp` as a **sidecar container**
alongside your application; the sidecar attaches to the app via the .NET
runtime's diagnostic IPC socket (created in `/tmp`), so the target app needs
**no code changes**.

| Recipe | Target host | Multi-container model | External MCP ingress? |
|---|---|---|---|
| [`container-apps/`](container-apps/) | Azure Container Apps | Single revision, two containers, shared `EmptyDir` over `/tmp` | Optional (toggle `ingressTarget=diag` + `externalIngress=true`) |
| [`app-service/`](app-service/)       | Azure App Service (Linux) | `siteContainers` sidecar (GA, 2024) | No — reach via `az webapp ssh` only |

For Kubernetes (AKS or any cluster), use the generic recipes under
[`../k8s/`](../k8s/) instead.

> **Windows containers on App Service** do not yet support the sidecar
> (`siteContainers`) feature. For Windows workloads either move to Container
> Apps or run the MCP server out-of-process on a separate VM and reach the
> app's diagnostic socket through a network-exposed Diagnostic Port.

---

## Prerequisites

1. **Azure CLI 2.60+** and **Bicep 0.30+**:
   ```bash
   az --version
   az bicep version
   az bicep upgrade   # if behind
   ```
2. **A resource group** in the target subscription:
   ```bash
   az group create -n diag-rg -l eastus
   ```
3. **Container images reachable by Azure**:
   - The diagnostic sidecar image: published as
     `ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest` (or build your own via
     `docker build -f deploy/Dockerfile .` and push to your registry).
   - Your application image, built however you build today.
   - If either lives in a private registry (ACR, GHCR, etc.) you'll need to
     pass `registryServer / registryUsername / registryPassword` parameters.
4. **A bearer token** for the MCP HTTP transport — pick a long random value
   and store it somewhere safe (Key Vault recommended):
   ```bash
   export DIAG_TOKEN=$(openssl rand -hex 32)
   ```

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**. Most Microsoft
`mcr.microsoft.com/dotnet/aspnet:*` images run as **root** by default. There
are two ways to align them:

- **Easiest**: rebuild the diag image with `USER root` (drop the non-root
  user from `deploy/Dockerfile`). Trade-off: the MCP server runs as root
  inside its container.
- **Most secure**: rebuild your app image with `USER 10001` and ensure
  `/app` is owned by that UID.

Container Apps and App Service Linux do not expose a `securityContext.runAsUser`
per container, so the alignment has to happen at image-build time.

---

## Validate without deploying (zero cost)

Both templates can be compiled and parameter-validated locally without ever
touching the subscription:

```bash
# Compile to ARM JSON (errors out on schema issues).
az bicep build --file deploy/azure/container-apps/main.bicep
az bicep build --file deploy/azure/app-service/main.bicep
```

A full dry-run against the API (requires login but creates nothing) uses
`az deployment group validate`:

```bash
az login
az account set --subscription <your-sub>

az deployment group validate \
  --resource-group diag-rg \
  --template-file deploy/azure/container-apps/main.bicep \
  --parameters \
      name=diag-demo \
      environmentId=/subscriptions/<sub>/resourceGroups/diag-rg/providers/Microsoft.App/managedEnvironments/<env> \
      appImage=mcr.microsoft.com/dotnet/samples:aspnetapp \
      diagBearerToken=$DIAG_TOKEN
```

---

## Recipe: Azure Container Apps

The Bicep deploys one Container App with two containers (`app` and `diag`)
sharing an `EmptyDir` volume at `/tmp`. Ingress defaults to the application
port; flip `ingressTarget=diag` + `externalIngress=true` if you want the MCP
endpoint reachable from outside the Container Apps environment.

```bash
az deployment group create \
  --resource-group diag-rg \
  --template-file deploy/azure/container-apps/main.bicep \
  --parameters \
      name=diag-demo \
      environmentId=$ENV_ID \
      appImage=$APP_IMAGE \
      diagImage=ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest \
      diagBearerToken=$DIAG_TOKEN
```

### Smoke test

```bash
# Shell into the diag sidecar:
az containerapp exec \
  --name diag-demo --resource-group diag-rg --container diag \
  --command /bin/sh

# Inside the diag container:
ls /tmp/dotnet-diagnostic-*-socket
curl -sH "Authorization: Bearer $MCP_BEARER_TOKEN" http://localhost:8787/health
```

If `ls /tmp/dotnet-diagnostic-*` is empty, the most likely cause is the UID
mismatch described above.

### Connecting an MCP client

When `ingressTarget=diag` + `externalIngress=true`:

```jsonc
// claude_desktop_config.json / mcp.json
{
  "mcpServers": {
    "azure-diag": {
      "type": "http",
      "url": "https://<fqdn-from-bicep-output>/mcp",
      "headers": { "Authorization": "Bearer <DIAG_TOKEN>" }
    }
  }
}
```

When the ingress targets `app` (default), the MCP endpoint is internal only.
Use `az containerapp exec` to tunnel, or attach an Azure private endpoint and
connect from a peered VNet.

---

## Recipe: Azure App Service (Linux)

The Bicep provisions a Linux App Service Plan + a Web App with two
`siteContainers`: `app` (with `isMain=true`) and `diag`. App Service
guarantees both share the writable filesystem root, so `/tmp` works for the
diagnostic socket out of the box.

```bash
az deployment group create \
  --resource-group diag-rg \
  --template-file deploy/azure/app-service/main.bicep \
  --parameters \
      siteName=diag-demo-app \
      appImage=$APP_IMAGE \
      diagImage=ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest \
      diagBearerToken=$DIAG_TOKEN
```

### Smoke test

App Service does not expose multiple ports publicly. SSH into the site and
hit the diag container's port from `localhost`:

```bash
az webapp ssh --name diag-demo-app --resource-group diag-rg
# inside:
ls /tmp/dotnet-diagnostic-*-socket
curl -sH "Authorization: Bearer $MCP_BEARER_TOKEN" http://localhost:8787/health
```

To use the MCP endpoint from your workstation, tunnel via SSH or expose the
diag port through an `applicationGateway` / `frontDoor` rule (not in this
template — keep diag traffic off the public internet whenever possible).

---

## Cleaning up

```bash
az group delete -n diag-rg -y
```

## Roadmap

- AWS ECS / Fargate sidecar recipe — [tracking issue #22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22).
- GCP Cloud Run multi-container recipe — [tracking issue #22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22).
- Optional managed-identity-based auth for the MCP HTTP transport (today the
  bearer token is the only mechanism).
