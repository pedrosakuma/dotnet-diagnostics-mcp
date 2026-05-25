# Local sidecar validation (Docker)

Reproduces the Kubernetes sidecar topology with plain Docker, so you can run
the whole stack locally before deploying to a cluster.

Two images, two containers, one shared `/tmp` volume + a shared PID namespace
— the exact building blocks Kubernetes provides via `emptyDir` +
`shareProcessNamespace`.

## Build the images

From the repo root:

```bash
docker build -t dotnet-diagnostics-mcp:dev -f deploy/Dockerfile .
docker build -t coreclr-sample:dev   -f samples/CoreClrSample/Dockerfile .
```

> 🔧 **Need a smaller image without `perf`?** Add `--build-arg INSTALL_PERF=false`
> to the sidecar build. The default image ships `perf` so `collect_sample(kind="off_cpu")`
> and the Linux NativeAOT perf-replay thread-snapshot fallback work out of the
> box (perf still needs `CAP_PERFMON` at runtime — add `--cap-add PERFMON` to the
> sidecar `docker run`, or lower `kernel.perf_event_paranoid` on the host).
> Opting out of the install skips ~80 MB of `linux-tools-*` packages; the capability
> detector will then report `canSampleOffCpu: false`. See issue #104.
>
> ```bash
> docker build --build-arg INSTALL_PERF=false \
>   -t dotnet-diagnostics-mcp:dev-lean -f deploy/Dockerfile .
> ```
>

## Run the topology

```bash
docker network create diagmcp-net 2>/dev/null || true
docker volume  create diagnosticsmcp-tmp >/dev/null

# 1) the target app — owns PID 1 in the shared namespace
docker run -d --name sample --network diagmcp-net \
  -v diagnosticsmcp-tmp:/tmp \
  -p 18080:8080 \
  coreclr-sample:dev

# 2) the MCP sidecar — joins sample's PID namespace and /tmp volume
docker run -d --name mcp --network diagmcp-net \
  --pid=container:sample \
  -v diagnosticsmcp-tmp:/tmp \
  --user 0 \
  --cap-add SYS_PTRACE \
  -e MCP_BEARER_TOKEN=dev-token \
  -p 18787:8080 \
  dotnet-diagnostics-mcp:dev
```

`--user 0` is the easy path for local validation because the sample image runs
as root and creates `/tmp/dotnet-diagnostic-1` owned by root. In Kubernetes,
the recommended setup is to run **both** containers as the same non-root UID
(the sample manifest pins UID/GID `10001` and sets `fsGroup: 10001`).

### Sidecar ops: auto-recycle on image swap

When the sidecar image is rolled forward (`docker pull … && docker run …` with
the same name, or a Kubernetes deployment update of just the sidecar
container), the still-running process keeps serving the **previous** build
until something else recycles it. Set `DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART=true`
on the sidecar container — the built-in `StaleBinaryWatcher` polls the
on-disk MVID once a minute and, on drift, asks the host to stop gracefully so
the supervisor (`--restart=always`, systemd, K8s) brings up the fresh build.
Without the env var the watcher only logs a warning. See issue #75.

### Heads up: ClrMD tools need `CAP_SYS_PTRACE` on Linux

`collect_thread_snapshot`, `inspect_heap(source="live")`, `inspect_heap(source="dump")` (for live PIDs)
and `collect_process_dump` all attach via ClrMD, which under the hood issues
`ptrace(PTRACE_ATTACH, …)`. Matching UIDs alone is **not** enough on Linux:
the kernel's [Yama LSM](https://www.kernel.org/doc/Documentation/admin-guide/LSM/Yama.rst)
defaults `kernel.yama.ptrace_scope=1` on Debian/Ubuntu/WSL, which blocks
same-UID peer attach. You will see a structured error like:

```json
{ "error": { "kind": "PermissionDenied",
             "message": "Could not PTRACE_ATTACH to any thread of the process N." } }
```

Mitigations, in order of preference for local Docker:

- Pass `--cap-add SYS_PTRACE` to the **sidecar** container (it is the one that
  performs the ptrace call). The target container does not need it.
- Or relax the host (affects everything on the box):
  `sudo sysctl -w kernel.yama.ptrace_scope=0`.
- Or run the sidecar as root **and** as a parent of the target — covers the
  Yama "parent only" mode (`ptrace_scope=1`).

For Kubernetes, see [`deploy/k8s/sample-sidecar.yaml`](../deploy/k8s/sample-sidecar.yaml):
add `capabilities.add: ["SYS_PTRACE"]` to the sidecar container's
`securityContext`, alongside the existing UID alignment.

EventPipe-based tools (`collect_events(kind="counters")`, `collect_sample(kind="cpu")`,
`collect_events(kind="exceptions")`, `collect_events(kind="gc")`, `collect_events(kind="activities")`, `collect_events(kind="event_source")`) do **not**
need `CAP_SYS_PTRACE` — they go through the diagnostic IPC socket only.

## Smoke-test the MCP endpoint

```bash
# Health (no auth)
curl -fsS http://127.0.0.1:18787/health

# Initialize the MCP session and grab the session id from the response header
curl -fsS -X POST http://127.0.0.1:18787/mcp \
  -H 'Authorization: Bearer dev-token' \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -D headers.txt -o init.txt \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'

SID=$(grep -i '^mcp-session-id:' headers.txt | awk '{print $2}' | tr -d '\r')

# Finish the handshake
curl -fsS -X POST http://127.0.0.1:18787/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'

# Discover .NET processes the sidecar can see
curl -fsS -X POST http://127.0.0.1:18787/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"inspect_process(view="list")","arguments":{}}}'
```

You should see at least PID `1` (the sample) and the sidecar's own PID.

Collect 5 seconds of `System.Runtime` counters from PID 1:

```bash
curl -fsS -X POST http://127.0.0.1:18787/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"collect_events(kind="counters")","arguments":{"processId":1,"durationSeconds":5,"providers":["System.Runtime"]}}}'
```

## Tear down

```bash
docker rm -f mcp sample
docker volume rm diagnosticsmcp-tmp
docker network rm diagmcp-net
```
