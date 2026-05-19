# dotnet-diagnostics-mcp

[![CI](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/actions/workflows/ci.yml)

> **Status:** MCP server functional with 9 diagnostic tools. End-to-end tests
> pass. See [`docs/`](./docs) for the tool reference and investigation
> playbooks.

An **MCP server** that lets an LLM perform on-demand performance diagnostics on running
**.NET 10** applications — locally or in Kubernetes — **without any modification to the
target app**.

It builds on the existing .NET diagnostics pipeline (`Microsoft.Diagnostics.NETCore.Client`
+ EventPipe) and exposes it as MCP tools over Streamable HTTP so any MCP-aware client
(Claude Desktop, Copilot CLI, custom agents, ...) can drive an investigation.

## Goals

- **Zero changes to the target app** for CoreCLR (JIT/R2R) processes
- **Cross-platform** Linux + Windows (containers as a first-class citizen)
- **Graceful NativeAOT support** — tools that can't work on AOT (CPU sampling, gcdump)
  return a clear `not_supported` instead of crashing
- **LLM-friendly outputs** — every tool returns summarized JSON (top-N hotspots, aggregates),
  not raw `.nettrace` blobs

## Repository layout

```
src/
  DotnetDiagnosticsMcp.Core/        # diagnostic primitives (process discovery, EventPipe sessions, parsing)
  DotnetDiagnosticsMcp.Server/      # ASP.NET Core host + MCP tools + bearer auth
samples/
  CoreClrSample/            # plain ASP.NET Core webapi used as a target for testing
  NativeAotSample/          # webapi with PublishAot=true and EventSourceSupport=true
tests/
  DotnetDiagnosticsMcp.Core.Tests/
  DotnetDiagnosticsMcp.Server.IntegrationTests/
```

## Tools at a glance

| Tool | Purpose |
|---|---|
| `list_dotnet_processes` / `get_process_info` | Discover .NET processes via diagnostic IPC |
| `get_diagnostic_capabilities` | Detect CoreCLR vs NativeAOT and what's usable |
| `snapshot_counters` | EventCounters over a window (cpu, memory, requests, ...) |
| `collect_cpu_sample` | Top-N CPU hotspots (inclusive/exclusive) — **CoreCLR only** |
| `collect_exceptions` | Managed exceptions thrown in a window, aggregated by type |
| `collect_gc_events` | GC pauses + per-generation counts |
| `collect_event_source` | Generic EventSource passthrough (HTTP, Kestrel, custom) |
| `collect_process_dump` | Write a Mini / Triage / WithHeap / Full dump to disk |

Full schemas and return shapes: [`docs/tool-reference.md`](./docs/tool-reference.md).
Common investigation recipes: [`docs/investigation-playbooks.md`](./docs/investigation-playbooks.md).
Client setup (C# SDK, GUI clients, curl): [`docs/client-setup.md`](./docs/client-setup.md).
Kubernetes sidecar: [`deploy/k8s/README.md`](./deploy/k8s/README.md).
Azure (App Service + Container Apps) recipes: [`deploy/azure/README.md`](./deploy/azure/README.md).

## Build & test

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK (pinned in `global.json`).

## Authentication

The MCP server requires a Bearer token on every request to `/mcp/*` (the `/health` endpoint is open).

- Set `MCP_BEARER_TOKEN` to use a fixed token (recommended).
- If unset, an ephemeral token is generated at startup and printed in the logs.

```bash
export MCP_BEARER_TOKEN="$(openssl rand -hex 32)"
dotnet run --project src/DotnetDiagnosticsMcp.Server
```

### Local shared instance (recommended for dev)

The HTTP transport is multi-client, so a single shared MCP instance on
`127.0.0.1:8787` serves any number of concurrent clients (multiple `gh copilot`
sessions, editors, etc). Use the lifecycle wrapper for an idempotent,
deterministic local setup:

```bash
scripts/local-mcp.sh start     # builds (Release) + starts in background; idempotent
scripts/local-mcp.sh status
scripts/local-mcp.sh logs -f
scripts/local-mcp.sh restart
scripts/local-mcp.sh stop
```

The script pins URL (`LOCAL_MCP_URL`, default `http://127.0.0.1:8787`) and bearer
token (`MCP_BEARER_TOKEN`, default `demo-local-token-2026`), uses
`--no-launch-profile` so `launchSettings.json` can't silently override the port,
and stores its PID at `/tmp/dotnet-diagnostics-mcp.pid` and logs at
`/tmp/dotnet-diagnostics-mcp.log` (overridable via `LOCAL_MCP_PIDFILE` /
`LOCAL_MCP_LOGFILE`). Pair with the matching entry in `~/.copilot/mcp-config.json`:

```json
{
  "mcpServers": {
    "dotnet-diagnostics": {
      "type": "http",
      "url": "http://127.0.0.1:8787/mcp",
      "headers": { "Authorization": "Bearer demo-local-token-2026" }
    }
  }
}
```

There is also a dedicated `local-mcp` launch profile for one-off foreground runs:
`dotnet run --project src/DotnetDiagnosticsMcp.Server --launch-profile local-mcp`.

## Roadmap

See [`docs/`](./docs) (to be filled in) and the planning notes in the session workspace.
Phases:

1. ✅ Foundation
2. ✅ Core diagnostics (process discovery, capability detection, counters, CPU sampling)
3. ✅ MCP server MVP wired to Core
4. ✅ Advanced tools (GC, exceptions, custom EventSources, dumps)
5. ✅ Kubernetes sidecar topology + manifests (see [`deploy/k8s/`](./deploy/k8s))
6. ✅ Documentation polish (tool reference, investigation playbooks, client setup)
7. ✅ Cloud-native integrations: Azure App Service + Container Apps (see [`deploy/azure/`](./deploy/azure)). AWS / GCP tracked as follow-up issues.
8. ⏳ Future: NativeAOT publish, AWS ECS / Fargate, GCP Cloud Run.

## License

TBD.
