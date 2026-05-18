# Deploying dotnet-dbg-mcp on Kubernetes

This folder contains a working sidecar topology that lets an LLM (via an MCP
client) attach to a .NET application running in a Pod without modifying the
application's image or code.

## Files

- [`sample-sidecar.yaml`](./sample-sidecar.yaml) — Namespace, Secret (bearer
  token), Deployment with **target app + dbgmcp sidecar** and a ClusterIP
  Service that exposes the MCP endpoint.

## Why these pod-level settings are required

The .NET runtime exposes a Diagnostic IPC endpoint per process:

- **Linux**: a Unix domain socket at `/tmp/dotnet-diagnostic-<pid>`
- **Windows**: a named pipe `dotnet-diagnostic-<pid>`

For the sidecar to reach it on Linux pods, the two containers must agree on
two things:

1. **`shareProcessNamespace: true`** on the pod spec — without this the
   sidecar cannot see the target's PIDs, so
   `DiagnosticsClient.GetPublishedProcesses()` returns an empty list.
2. **A shared volume mounted at `/tmp` in both containers** — so the socket
   the runtime creates in the app container is visible from the sidecar. An
   `emptyDir` volume is enough; it lives only as long as the pod.
3. **Matching UID/GID (or `fsGroup`)** — the diagnostic socket inherits the
   UID of the .NET process. If the sidecar runs as a different non-root user,
   it gets `Permission denied` opening the socket. The manifest pins both
   containers to UID/GID `10001` and sets `fsGroup: 10001` on the pod. If the
   target app image *must* run as a different UID, either match `runAsUser`
   in the sidecar or rely on `fsGroup` + group-readable socket permissions.

The provided manifest configures all three.

> **Validated locally with Docker** using `--pid=container:<app>` + a shared
> volume mounted at `/tmp` in both containers — the same building blocks
> Kubernetes provides via `shareProcessNamespace` and `emptyDir`.

## Auth

The sidecar requires an `Authorization: Bearer <token>` header on every
request to `/mcp`. The token comes from the `MCP_BEARER_TOKEN` env var, which
the manifest sources from a Kubernetes `Secret`. Rotate the secret and restart
the pod to revoke a token.

`/health` is exempt from auth so the kubelet readiness/liveness probes work
without credentials.

## Connecting an MCP client

The Service exposes the sidecar on `ClusterIP:8787`. From a developer machine
the simplest path is `kubectl port-forward`:

```bash
kubectl -n dbgmcp-demo port-forward svc/sample-api-dbgmcp 8787:8787
```

Then point an MCP-aware client at `http://localhost:8787/mcp` with the bearer
token. For an LLM-based investigation, the model will typically:

1. Call `list_dotnet_processes` to discover the target's PID.
2. Call `get_diagnostic_capabilities` to see what's available (CoreCLR vs
   NativeAOT, sampling, gcdump, etc.).
3. Pick the appropriate tool — `snapshot_counters`, `collect_cpu_sample`,
   `collect_exceptions`, `collect_gc_events`, `collect_event_source`, or
   `collect_process_dump` — based on the symptom being investigated.

## Building the sidecar image

From the repo root:

```bash
docker build -t ghcr.io/example/dotnet-dbg-mcp:latest -f deploy/Dockerfile .
docker push ghcr.io/example/dotnet-dbg-mcp:latest
```

Replace the image reference in `sample-sidecar.yaml` with your own registry
coordinates before applying.

## Security considerations

- **Token confidentiality**: keep `MCP_BEARER_TOKEN` in a Secret (or external
  secret store via CSI driver). Never bake it into the image.
- **Network exposure**: the Service is `ClusterIP` by default. Use an Ingress
  with TLS termination or a service mesh (mTLS) if you need to expose it
  outside the cluster — never expose it directly to the public internet.
- **Privilege**: the sidecar runs as a non-root user (UID 10001). Process
  namespace sharing does not grant root, but it does let the sidecar see and
  attach to the target. Treat the sidecar's auth boundary as equivalent to
  shell access to the target process.
- **Resource isolation**: keep `resources.limits` on the sidecar so a runaway
  trace cannot starve the application.

## Caveats

- The sidecar will not be able to take a CPU sample of a **NativeAOT** target
  process: the `Microsoft-DotNETCore-SampleProfiler` provider does not exist
  in NativeAOT. Call `get_diagnostic_capabilities` first; the response
  documents what is and isn't possible per target.
- Dumps written by `collect_process_dump` land on the sidecar container's
  filesystem. Mount a `PersistentVolumeClaim` or use an `emptyDir` of
  sufficient size if you expect to capture large dumps.
- The current manifest assumes a single replica. For multi-replica
  deployments, point the MCP client at a specific Pod (`kubectl port-forward
  pod/...`) rather than the Service so you know which instance you're
  investigating.
