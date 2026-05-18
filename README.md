# dotnet-dbg-mcp

[![CI](https://github.com/dotnet-dbg-mcp/dotnet-dbg-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/dotnet-dbg-mcp/dotnet-dbg-mcp/actions/workflows/ci.yml)

> **Status:** very early — Phase 1 (foundation) complete. Diagnostic tooling not yet implemented.

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
  DotnetDbgMcp.Core/        # diagnostic primitives (process discovery, EventPipe sessions, parsing)
  DotnetDbgMcp.Server/      # ASP.NET Core host + MCP tools + bearer auth
samples/
  CoreClrSample/            # plain ASP.NET Core webapi used as a target for testing
  NativeAotSample/          # webapi with PublishAot=true and EventSourceSupport=true
tests/
  DotnetDbgMcp.Core.Tests/
  DotnetDbgMcp.Server.IntegrationTests/
```

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
dotnet run --project src/DotnetDbgMcp.Server
```

## Roadmap

See [`docs/`](./docs) (to be filled in) and the planning notes in the session workspace.
Phases:

1. ✅ Foundation (this commit)
2. ⏳ Core diagnostics (process discovery, capability detection, counters, CPU sampling)
3. ⏳ MCP server MVP wired to Core
4. ⏳ Advanced tools (GC, exceptions, HTTP/ASP.NET, custom EventSources, dumps)
5. ⏳ Kubernetes sidecar + manifests + integration tests in Docker
6. ⏳ Documentation polish
7. ⏳ Future: cloud-native integrations (Azure / AWS / GCP), NativeAOT fallback profiling, ClrMD dump inspection

## License

TBD.
