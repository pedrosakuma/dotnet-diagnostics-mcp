# Central / on-demand topology: ephemeral debug containers

This recipe deploys the MCP server **on demand** as a Kubernetes
[ephemeral debug container](https://kubernetes.io/docs/concepts/workloads/pods/ephemeral-containers/)
instead of a permanent sidecar. The MCP only consumes resources during the
window of an active investigation, then goes away with the Pod's lifetime.

## Why on-demand?

The sidecar topology (`sample-sidecar.yaml`) is correct but pays a recurring
cost on every Pod that *might* be investigated: extra container, extra memory
reservation, extra attack surface. In a large cluster that adds up fast.

The on-demand topology pays the cost only when:

- A real investigation is in progress, and
- Only on the specific Pod under investigation.

Trade-off: the target Pod must be **prepared** in advance so the ephemeral
container can reach the diagnostic IPC socket. See "Preparing the target"
below.

## How it works

1. The target Pod runs the application container as it normally would, with
   two opt-ins (a shared `/tmp` `emptyDir` volume and a fixed non-root UID).
2. When an investigation starts, an operator (or automation) attaches an
   ephemeral container running the `dotnet-diagnostics-mcp` image to the
   target Pod via the `pods/ephemeralcontainers` subresource. The patch
   declares `targetContainerName: app` so the ephemeral container shares the
   target's **PID namespace**, and mounts the same `diag-tmp` volume at
   `/tmp` so the diagnostic IPC socket is visible.
3. `kubectl port-forward` exposes the MCP endpoint to the investigator's
   workstation. The model walks the standard diagnostic flow
   (`list_dotnet_processes` → `get_diagnostic_capabilities` → collectors).
4. When the investigation is over the operator deletes / restarts the Pod.
   Ephemeral containers are scoped to the Pod's lifetime — there is no way
   to remove them without recreating the Pod, which is intentional: it
   guarantees the diagnostic surface is short-lived.

## Preparing the target

The target Deployment must:

- Define an `emptyDir` volume (e.g. `diag-tmp`) and mount it at `/tmp` in
  the application container. The .NET runtime creates its diagnostic IPC
  socket at `/tmp/dotnet-diagnostic-<pid>-...-socket`. Ephemeral containers
  share the target's PID namespace but **not** its mount namespace, so the
  only way to reach that socket from another container is through a shared
  volume mounted at the same path.
- Pin the application container to a known non-root UID/GID (this manifest
  uses `10001`) so the ephemeral container can run as the same UID and read
  the socket without elevated privileges.
- Set `fsGroup` on the Pod for good measure.

See [`central-target.yaml`](./central-target.yaml) for the minimal config.
**No sidecar required.**

## Attaching on demand

```bash
POD=$(kubectl -n diagnosticsmcp-central get pod -l app=sample-target -o jsonpath='{.items[0].metadata.name}')

kubectl -n diagnosticsmcp-central patch pod "$POD" \
    --subresource=ephemeralcontainers \
    --patch-file=deploy/k8s/ephemeral-attach.patch.json
```

The patch ([`ephemeral-attach.patch.json`](./ephemeral-attach.patch.json))
declares:

```jsonc
{
  "targetContainerName": "app",      // shares PID namespace with the app
  "volumeMounts": [
    { "name": "diag-tmp", "mountPath": "/tmp" }   // same socket path
  ],
  "securityContext": {
    "runAsUser": 10001, "runAsGroup": 10001       // same UID as app
  }
}
```

Wait for the ephemeral container to be Running:

```bash
kubectl -n diagnosticsmcp-central get pod "$POD" \
    -o jsonpath='{.status.ephemeralContainerStatuses[*].state}'
```

Then expose the MCP endpoint to the local workstation:

```bash
kubectl -n diagnosticsmcp-central port-forward pod/"$POD" 8787:8787
```

The MCP server is now reachable at `http://localhost:8787/mcp`. Use the
bearer token from the patch (`devtoken` in the sample; rotate via the
patch's `env` for real deployments).

## End-to-end smoke test

```bash
SID=$(curl -sN -X POST http://localhost:8787/mcp \
    -H "Authorization: Bearer devtoken" \
    -H "Accept: application/json, text/event-stream" \
    -H "Content-Type: application/json" \
    -D /tmp/h.txt \
    -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}' \
    >/dev/null && grep -i mcp-session-id /tmp/h.txt | awk '{print $2}' | tr -d '\r')

curl -s -X POST http://localhost:8787/mcp \
    -H "Authorization: Bearer devtoken" \
    -H "Mcp-Session-Id: $SID" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

curl -sN -X POST http://localhost:8787/mcp \
    -H "Authorization: Bearer devtoken" \
    -H "Mcp-Session-Id: $SID" \
    -H "Content-Type: application/json" \
    -H "Accept: application/json, text/event-stream" \
    -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_dotnet_processes","arguments":{}}}'
```

The response should include the target's PID with its
`managedEntrypointAssemblyName` plus the ephemeral container's own PID
(both run inside the same shared PID namespace).

## RBAC

The principal applying the patch needs:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  namespace: diagnosticsmcp-central
  name: diagnosticsmcp-ondemand
rules:
  - apiGroups: [""]
    resources: ["pods/ephemeralcontainers"]
    verbs: ["update", "patch"]
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list"]
  - apiGroups: [""]
    resources: ["pods/portforward"]
    verbs: ["get", "create"]
```

Use a `RoleBinding` to grant this to a dedicated `ServiceAccount` that the
investigation tooling (or a central orchestrator) authenticates as. Treat
this binding as equivalent to interactive shell access on the target Pod's
PID namespace.

## What this recipe deliberately does NOT do

- **It does not provide a single central agent that fan-outs to many
  Pods.** The current model is one ephemeral container per investigation —
  the investigator (or an external orchestrator) is responsible for
  selecting the target Pod and applying the patch. A future iteration may
  add a central HTTP orchestrator that talks to the K8s API and exposes a
  "fleet of pods" surface over MCP. See the tracking issue.
- **It does not work against fully unprepared targets.** Without the
  shared `/tmp` volume + matching UID the ephemeral container cannot read
  the diagnostic socket. A future iteration may add `/proc/<pid>/root/tmp`
  traversal in the diag server (requires running the ephemeral container
  as root with `CAP_SYS_PTRACE`).

## NativeAOT CPU sampling (perf bundled by default)

`collect_cpu_sample` against a NativeAOT target falls back to Linux `perf`
since SampleProfiler is absent. The default sidecar image now ships `perf`,
so the only runtime change required is to grant the right capabilities:

1. **Use the default image** — `dotnet-diagnostics-mcp:dev` (or the published
   GHCR tag without a suffix). Pass `--build-arg INSTALL_PERF=false` and use
   the `-lean` GHCR tag only when you explicitly want to skip the ~80 MB perf
   payload — this disables `collect_off_cpu_sample` and the perf-replay
   thread-snapshot fallback.
2. **Patch `ephemeral-attach.patch.json`** to grant the relevant
   capabilities on the ephemeral container's `securityContext`:

```json
"securityContext": {
  "runAsUser": 10001,
  "runAsGroup": 10001,
  "runAsNonRoot": true,
  "capabilities": { "add": ["PERFMON", "SYS_PTRACE"] }
}
```

The host's `kernel.perf_event_paranoid` must also be `<= 1` (or `<= 2`
with `CAP_PERFMON`). Frames returned by the perf path are native symbols
only — `MethodIdentity` is `null`, so the assembly-MCP handoff is
unavailable for NativeAOT hotspots.
