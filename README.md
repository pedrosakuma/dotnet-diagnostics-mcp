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

> **`processId` is optional everywhere.** When the sidecar only sees one .NET
> process the server auto-resolves it and stamps a capability digest on every
> response (`resolvedProcess.autoResolved = true`), so the
> `list_dotnet_processes` → `get_diagnostic_capabilities` opener can be skipped
> entirely. On ambiguity / nothing visible you get a structured error with the
> candidate list inline. See [issue #42](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/42).

> **Prompts (curated playbooks).** The server also exposes 6 MCP Prompts that
> pre-package the recipes in [`docs/investigation-playbooks.md`](./docs/investigation-playbooks.md):
> `diagnose-high-latency`, `diagnose-memory-growth`, `diagnose-5xx-errors`,
> `diagnose-slow-outbound-http`, `triage-nativeaot`, `diagnose-safely-in-prod`.
> Every input is optional, every body is annotated for the LLM (`audience: ["assistant"]`).
> See [issue #44](https://github.com/pedrosakuma/dotnet-diagnostics-mcp/issues/44) and the
> [Prompts section in `docs/tool-reference.md`](./docs/tool-reference.md#prompts-curated-playbooks).

## Install (consumers)

Three distributions of the MCP server, all wire-compatible. Pick whichever fits your environment best — see [`docs/consumer-install.md`](./docs/consumer-install.md) for the full walkthrough including supervisor templates (systemd, Windows Scheduled Task, launchd) and `mcp-config.json` snippets.

```bash
# .NET global tool (requires .NET 10 SDK)
dotnet tool install -g DotnetDiagnosticsMcp.Server
dotnet-diagnostics-mcp --urls http://127.0.0.1:8787

# Container (no SDK needed; predictable filesystem)
docker run -d --restart unless-stopped -p 127.0.0.1:8787:8787 \
  -e MCP_BEARER_TOKEN=$(openssl rand -hex 32) \
  ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest

# For off-CPU sampling (collect_off_cpu_sample) or NativeAOT CPU sampling, use the
# `-perf` variant which ships linux-perf preinstalled:
#   ghcr.io/pedrosakuma/dotnet-diagnostics-mcp:latest-perf
# (Requires CAP_PERFMON or perf_event_paranoid <= -1 on the host.)

# Or grab a self-contained single-file binary for your OS/arch from the Releases page.
```

### Joint with `dotnet-assembly-mcp` (recommended for handoff resolution)

For the full handoff story — `MethodIdentity` / `TypeIdentity` resolving to
decompiled bodies, call graphs, attributes, type hierarchy — run both servers
together with the joint compose file:

```bash
export ASSEMBLIES_DIR=/abs/path/to/your/published/binaries
docker compose -f deploy/docker-compose.yml up -d
# diagnostics: http://localhost:8787/mcp
# assembly:    http://localhost:8788/mcp
```

The same `docker-compose.yml` ships in
[`pedrosakuma/dotnet-assembly-mcp:deploy/docker-compose.yml`](https://github.com/pedrosakuma/dotnet-assembly-mcp/blob/main/deploy/docker-compose.yml)
— bring it up from either checkout. Set `MCP_BEARER_TOKEN` on the host to gate
both servers with one shared token.

Health probe used by every supervisor (and the container's `HEALTHCHECK`):

```bash
dotnet-diagnostics-mcp --health-check --urls http://127.0.0.1:8787
```

The companion [`pedrosakuma/dotnet-assembly-mcp`](https://github.com/pedrosakuma/dotnet-assembly-mcp) is **optional in a dev environment** after #28 — `MethodIdentity` payloads now carry a resolved `SourceLocation` whenever a PDB is reachable on the diagnostics box, so the LLM can open `File:Line` directly. The partner is the right call for stripped binaries, decompilation, and call-graph queries.

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

### Contributor setup (shared dev instance)

> Looking for a consumer install? See the [Install](#install-consumers) section above.

The HTTP transport is multi-client, so a single shared MCP instance on `127.0.0.1:8787` serves any number of concurrent clients (multiple `gh copilot` sessions, editors, etc). Use the lifecycle wrapper for an idempotent, deterministic local setup against the current source checkout:

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
