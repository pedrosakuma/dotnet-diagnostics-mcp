# Local sidecar validation (Docker)

Reproduces the Kubernetes sidecar topology with plain Docker, so you can run
the whole stack locally before deploying to a cluster.

Two images, two containers, one shared `/tmp` volume + a shared PID namespace
— the exact building blocks Kubernetes provides via `emptyDir` +
`shareProcessNamespace`.

## Build the images

From the repo root:

```bash
docker build -t dotnet-dbg-mcp:dev -f deploy/Dockerfile .
docker build -t coreclr-sample:dev   -f samples/CoreClrSample/Dockerfile .
```

## Run the topology

```bash
docker network create dbgmcp-net 2>/dev/null || true
docker volume  create dbgmcp-tmp >/dev/null

# 1) the target app — owns PID 1 in the shared namespace
docker run -d --name sample --network dbgmcp-net \
  -v dbgmcp-tmp:/tmp \
  -p 18080:8080 \
  coreclr-sample:dev

# 2) the MCP sidecar — joins sample's PID namespace and /tmp volume
docker run -d --name mcp --network dbgmcp-net \
  --pid=container:sample \
  -v dbgmcp-tmp:/tmp \
  --user 0 \
  -e MCP_BEARER_TOKEN=dev-token \
  -p 18787:8080 \
  dotnet-dbg-mcp:dev
```

`--user 0` is the easy path for local validation because the sample image runs
as root and creates `/tmp/dotnet-diagnostic-1` owned by root. In Kubernetes,
the recommended setup is to run **both** containers as the same non-root UID
(the sample manifest pins UID/GID `10001` and sets `fsGroup: 10001`).

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
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_dotnet_processes","arguments":{}}}'
```

You should see at least PID `1` (the sample) and the sidecar's own PID.

Collect 5 seconds of `System.Runtime` counters from PID 1:

```bash
curl -fsS -X POST http://127.0.0.1:18787/mcp \
  -H "Authorization: Bearer dev-token" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H "mcp-session-id: $SID" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"snapshot_counters","arguments":{"processId":1,"durationSeconds":5,"providers":["System.Runtime"]}}}'
```

## Tear down

```bash
docker rm -f mcp sample
docker volume rm dbgmcp-tmp
docker network rm dbgmcp-net
```
