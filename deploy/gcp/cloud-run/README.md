# GCP Cloud Run recipe

On-demand diagnostics for .NET applications running on **GCP Cloud Run**
(fully-managed). The recipe deploys `dotnet-diagnostics-mcp` as a **sidecar
container** alongside your application in the same Cloud Run service
revision. The sidecar attaches to the app via the .NET runtime's diagnostic
IPC socket (created in `/tmp`), so the target app needs **no code changes**.

This is the GCP-native counterpart to the
[Azure Container Apps recipe](../../azure/container-apps/) and the
[AWS ECS / Fargate recipe](../../aws/ecs-fargate/). It implements
[`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md)
Phase 2.

| Artifact | Purpose |
|---|---|
| [`service.yaml`](service.yaml) | Knative-style Cloud Run Service manifest: two containers (`diag` + `app`), shared in-memory `/tmp`, Secret Manager-backed bearer token. |

For Kubernetes on GKE (or any other cluster), use the generic recipes under
[`../../k8s/`](../../k8s/) instead.

---

## ⚠️ Cloud Run is a reduced-capability host (read this first)

Cloud Run runs each container inside **gVisor** and does **not** expose a
knob for adding Linux capabilities. There is no Cloud Run equivalent of
`linuxParameters.capabilities.add: ["SYS_PTRACE"]` or the K8s
`securityContext.capabilities.add`. The ClrMD- and `ptrace`-backed MCP tools
therefore fail with `PermissionDenied` on this platform.

| MCP tool | Cloud Run | Reason |
|---|---|---|
| `snapshot_counters` | ✅ Works | EventPipe over the diag socket. |
| `collect_cpu_sample` | ✅ Works | EventPipe (SampleProfiler). |
| `collect_gc_events` | ✅ Works | EventPipe (GC keyword). |
| `collect_exceptions` | ✅ Works | EventPipe (Exception keyword). |
| `collect_event_source` | ✅ Works | EventPipe (arbitrary provider). |
| `collect_activities` | ✅ Works | EventPipe (ActivitySource). |
| `collect_allocation_sample` | ✅ Works | EventPipe (GCAllocationTick). |
| `list_dotnet_processes` / `get_process_info` / `get_diagnostic_capabilities` | ✅ Works | Diagnostic socket only. |
| `collect_thread_snapshot` | ❌ Blocked | Needs `ptrace`. |
| `inspect_live_heap` | ❌ Blocked | Needs `ptrace`. |
| `inspect_dump` (live PID) | ❌ Blocked | Needs `ptrace`. |
| `collect_process_dump` | ❌ Blocked | Needs `ptrace`. |
| `collect_off_cpu_sample` | ❌ Blocked | Needs `perf` + `CAP_PERFMON`. |

If you need the blocked tools, deploy on **AWS ECS / Fargate**
([`../../aws/ecs-fargate/`](../../aws/ecs-fargate/)) or **Kubernetes**
([`../../k8s/`](../../k8s/)) instead.

There is one additional caveat. Cloud Run **does not** document a
Kubernetes-style shared PID namespace for multi-container services. Sharing
`/tmp` is enough for the diagnostic socket — every working tool in the table
above is socket-only — but it means process *enumeration* over the socket is
the only supported discovery path, and you should not expect `/proc/<pid>`
visibility across containers.

---

## Prerequisites

1. **gcloud SDK** authenticated against the target project:
   ```bash
   gcloud auth login
   gcloud config set project <PROJECT_ID>
   gcloud config set run/region us-central1
   ```
2. **Required APIs enabled** in the project:
   ```bash
   gcloud services enable run.googleapis.com \
                          secretmanager.googleapis.com \
                          artifactregistry.googleapis.com
   ```
3. **Container images reachable from Cloud Run** — typically pushed to
   Artifact Registry in the same region:
   - Your application image.
   - The diagnostics sidecar image — default
     `ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`. If your project
     blocks anonymous GHCR pulls, mirror it to Artifact Registry first.
4. **A runtime service account** for the Cloud Run revision. It needs at
   minimum `roles/secretmanager.secretAccessor` on the bearer token secret:
   ```bash
   gcloud iam service-accounts create diag-mcp-sa \
     --display-name "dotnet-diagnostics-mcp Cloud Run runtime"

   APP_SA="diag-mcp-sa@$(gcloud config get-value project).iam.gserviceaccount.com"
   ```
5. **A bearer token in Secret Manager** for the MCP HTTP transport:
   ```bash
   openssl rand -hex 32 | gcloud secrets create diag-mcp-bearer-token \
     --replication-policy=automatic --data-file=-

   gcloud secrets add-iam-policy-binding diag-mcp-bearer-token \
     --member="serviceAccount:${APP_SA}" \
     --role="roles/secretmanager.secretAccessor"
   ```
   `service.yaml` references this secret by name (default
   `BEARER_SECRET_NAME` → `diag-mcp-bearer-token`) with `key: latest`.

## UID matching (read this first)

Both containers must agree on the UID that creates and reads
`/tmp/dotnet-diagnostic-<pid>-<unique>-socket`. The default
`dotnet-diagnostics-mcp` image runs as UID **10001**. Most Microsoft
`mcr.microsoft.com/dotnet/aspnet:*` images run as **root**.

Cloud Run does **not** support setting `securityContext.runAsUser`
per-container in its multi-container Service manifest — the effective UID is
the one baked into each image via `USER`. You have two options:

- **Easiest**: rebuild the diag image with `USER root` (drop the non-root
  user from `deploy/Dockerfile`). Trade-off: the MCP server runs as root
  inside its container.
- **Most secure**: rebuild your app image with `USER 10001` and ensure all
  files under `/tmp` are writable by that UID.

If the UIDs do not match, the diag container's `/tmp/dotnet-diagnostic-*`
attach will fail with `Permission denied`. See `AGENTS.md` →
"Diagnostic socket UID" for the underlying invariant.

---

## Deploy

1. **Edit `service.yaml`** and replace these placeholders:
   - `REGION` — Cloud Run region (matches `gcloud config get-value run/region`)
   - `PROJECT_NUMBER` — your project's numeric id
     (`gcloud projects describe $(gcloud config get-value project) --format='value(projectNumber)'`)
   - `APP_IMAGE` — your application's container image URI
   - `DIAG_IMAGE` — diagnostics sidecar image (default
     `ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`)
   - `APP_SERVICE_ACCOUNT` — the service account email created above
   - `BEARER_SECRET_NAME` — Secret Manager secret holding the bearer token
     (appears in two places: the `run.googleapis.com/secrets` annotation
     alias **and** in `valueFrom.secretKeyRef.name` — keep them in sync)
   - the `app` container's `startupProbe.tcpSocket.port` if your app does
     not listen on `8080`
2. **Validate without deploying** (dry-run; gcloud reports schema and
   API errors but creates nothing):
   ```bash
   gcloud run services replace deploy/gcp/cloud-run/service.yaml \
     --region us-central1 --dry-run
   ```
3. **Apply for real**:
   ```bash
   gcloud run services replace deploy/gcp/cloud-run/service.yaml \
     --region us-central1
   ```

The service is created with `run.googleapis.com/ingress: internal`, so it
is only reachable from inside the project's VPC (Cloud Run VPC connector,
GCE VMs on the same network, or Cloud Shell with a Serverless VPC Access
connector). Flip to `all` only if you must front it with an external load
balancer that adds an additional auth layer; the bearer token alone is not
intended to be the only line of defense on the public internet.

## Smoke test

```bash
# 1. Wait for the revision to become ready.
gcloud run services describe dotnet-diagnostics-mcp \
  --region us-central1 \
  --format='value(status.conditions[?type=`Ready`].status)'

# 2. Tail logs for both containers.
gcloud run services logs read dotnet-diagnostics-mcp \
  --region us-central1 --limit 100

# 3. From a host on the VPC, hit the MCP health endpoint.
TOKEN=$(gcloud secrets versions access latest --secret=diag-mcp-bearer-token)
URL=$(gcloud run services describe dotnet-diagnostics-mcp \
  --region us-central1 --format='value(status.url)')
curl -fsS -H "Authorization: Bearer $TOKEN" "$URL/health"
```

A passing health check proves the diag listener is up. It does **not**
imply that ptrace-backed tools work — they do not, see the capability
matrix above. The minimum supported smoke is calling `snapshot_counters`
over the MCP HTTP transport and seeing `System.Runtime` counters come back.

## MCP client snippet

```json
{
  "mcpServers": {
    "dotnet-diag-gcp": {
      "url": "https://<service-url>/mcp",
      "headers": {
        "Authorization": "Bearer <value of MCP_BEARER_TOKEN>"
      }
    }
  }
}
```

`<service-url>` is the value of `status.url` on the Cloud Run service. The
client must run on a host that can reach internal Cloud Run ingress — most
commonly via a Serverless VPC Access connector, a workstation on the VPC
through Identity-Aware Proxy, or an SSH tunnel.

## Out of scope

- **Public-by-default ingress.** The recipe defaults to internal ingress
  per the design doc. Public exposure is an explicit override.
- **External load balancer / IAP setup.** Cloud Run supports IAP and
  serverless NEGs, but provisioning them belongs in your platform's
  ingress layer, not in a diagnostics sidecar recipe.
- **GCP discovery tools.** A future `list_cloud_run_services` style tool
  that hits the Cloud Run API to enumerate candidate workloads lives in a
  separate issue and is intentionally not part of this recipe.
- **`collect_thread_snapshot` / `inspect_live_heap` / `inspect_dump` /
  `collect_process_dump` / `collect_off_cpu_sample`.** Blocked on gVisor;
  see the capability matrix above.
- **Cloud Functions / App Engine.** Both have execution models
  (request-scoped freeze, no shared `/tmp`) that break the sidecar pattern.

## Reference

- [`docs/cloud-recipes-design.md`](../../../docs/cloud-recipes-design.md) — the design that drives this recipe
- [`docs/cloud-integrations-design.md`](../../../docs/cloud-integrations-design.md) — parent portfolio decision
- [`AGENTS.md`](../../../AGENTS.md) — diagnostic socket UID invariant
- [Cloud Run multi-container services](https://cloud.google.com/run/docs/deploying#multicontainer) — GCP docs
- [Issue #22](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/22) — acceptance criteria

## Production: pin to a digest

The defaults above use a released version tag
(`ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1`) rather than `:latest`, so a
new upstream push cannot silently re-deploy under your stack. For production
workloads go one step further and pin to a **content-addressable digest** so the
exact image bytes are immutable across replicas, rollbacks, and pull retries:

```bash
# Resolve the current digest for the version tag you trust:
docker buildx imagetools inspect \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:0.3.1 \
  --format '{{json .Manifest}}' | jq -r .digest
# -> sha256:...

# Use the digest form in your parameters / Bicep / service.yaml / template:
ghcr.io/pedrosakuma/dotnet-diagnostics-mcp@sha256:<digest>
```

Mirror the digest into your private registry (Artifact Registry, ECR, ACR) for
air-gapped or pull-quota-limited environments. Bump the pinned digest on the
same cadence as your other base images; the SLSA build provenance attestation
published by [`.github/workflows/publish-container.yml`](../../../.github/workflows/publish-container.yml)
lets you verify the bytes before promoting.
